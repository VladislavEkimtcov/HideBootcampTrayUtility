using System;
using System.Threading;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// Drives the keyboard backlight as a "come back to the keyboard" beacon.
    ///
    /// This runs on its own thread rather than on the message loop, unlike the idle fade in
    /// BacklightIdle. That fade blocks the UI thread for about a second and gets away with
    /// it because by definition nobody is there; a flash can run for an hour, and the settings
    /// window has to stay usable while it does.
    ///
    /// The thread only ever touches the hardware through Backlight's flash entry points, which
    /// take the same lock as the hotkeys and the idle timer. That is what stops a flash from
    /// interleaving with an F5 ramp, and what guarantees the level the user chose comes back
    /// when the beacon stops.
    /// </summary>
    internal sealed class FlashWorker : IDisposable
    {
        // A sweep the length of a slow breath. Long enough not to read as an alarm, short
        // enough that a glance across the room catches it moving.
        private const int SweepPeriodMilliseconds = 1800;

        // Bright and brief, dark and longer -- a lighthouse rather than a blinking cursor.
        private const int StrobeOnMilliseconds = 200;
        private const int StrobeOffMilliseconds = 500;

        // One SMC write every 30 ms, so about 33 a second. Boot Camp's own ramp writes 8 in
        // 96 ms, so this is the same order of traffic, just sustained; and at 4096 hardware
        // levels a 30 ms step is far finer than the eye resolves in a moving light.
        private const int StepMilliseconds = 30;

        /// <summary>
        /// How long the beacon runs before input is allowed to stop it. Without this a flash
        /// fired from a terminal the user is still typing in could cancel itself before they
        /// ever saw it -- and GetLastInputInfo answering "0" for any reason would cancel every
        /// flash instantly.
        /// </summary>
        private const int ArmingMilliseconds = 1000;

        /// <summary>
        /// Slack between the idle clock and the elapsed clock, which are sampled a moment
        /// apart. Below this the two disagreeing means nothing.
        /// </summary>
        private const int InputSlackMilliseconds = 250;

        private readonly Backlight _backlight;
        private readonly object _sync = new object();

        private Thread _thread;
        private ManualResetEvent _stop;
        private bool _disposed;

        public FlashWorker(Backlight backlight)
        {
            _backlight = backlight;
        }

        /// <summary>
        /// Starts flashing, replacing whatever was flashing before. A second request landing
        /// mid-beacon means the newer one is what the user wants to see, so the old thread is
        /// stopped and joined first and the two never drive the light at once.
        /// </summary>
        public void Start(FlashRequest request)
        {
            if (request == null)
            {
                return;
            }

            Stop();

            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                Session session = new Session();
                session.Request = request;
                session.Stop = new ManualResetEvent(false);

                _stop = session.Stop;
                _thread = new Thread(Run);
                _thread.IsBackground = true;
                _thread.Name = "HideBootcampTrayUtility backlight flasher";
                _thread.Start(session);
            }
        }

        /// <summary>
        /// Ends the beacon and waits for the light to be put back. Safe to call when nothing
        /// is flashing.
        /// </summary>
        public void Stop()
        {
            Thread thread;
            ManualResetEvent stop;

            lock (_sync)
            {
                thread = _thread;
                stop = _stop;
                _thread = null;
                _stop = null;
            }

            if (thread == null)
            {
                return;
            }

            stop.Set();

            // Joined outside the lock: the worker takes Backlight's lock on its way out, and
            // a caller holding this one meanwhile is one hop from a deadlock. The wait is
            // generous compared with the loop, which can only ever be one 30 ms step and one
            // restoring ramp from noticing.
            if (thread.Join(TimeSpan.FromSeconds(3)))
            {
                stop.Close();
            }
        }

        private void Run(object state)
        {
            Session session = (Session)state;
            FlashRequest request = session.Request;
            ManualResetEvent stop = session.Stop;

            if (!_backlight.TryBeginFlash())
            {
                return;
            }

            int startTick = Environment.TickCount;
            long limitMilliseconds = (long)request.DurationSeconds * 1000;

            try
            {
                while (true)
                {
                    int elapsed = ElapsedSince(startTick);

                    // A duration is a ceiling, not a contract: whichever comes first, the
                    // clock running out or the user sitting back down, ends the beacon. It
                    // has done its job either way, and a flash you cannot stop by touching
                    // the keyboard is a flash you come to resent.
                    if (limitMilliseconds > 0 && elapsed >= limitMilliseconds)
                    {
                        break;
                    }

                    if (elapsed >= ArmingMilliseconds && InputSince(elapsed))
                    {
                        break;
                    }

                    _backlight.FlashSet(LevelFor(request.Mode, elapsed));

                    if (stop.WaitOne(StepMilliseconds))
                    {
                        break;
                    }
                }
            }
            finally
            {
                // In a finally so the light is never stranded: an exception here, or the
                // process being shut down through Dispose, still hands the user back the
                // level they left.
                _backlight.EndFlash();
            }
        }

        /// <summary>
        /// True when the machine has been idle for less time than the beacon has been
        /// running, which can only mean somebody touched it after the beacon started.
        /// Mouse movement counts: "back at the keyboard" means back at the machine.
        /// </summary>
        private static bool InputSince(int elapsedMilliseconds)
        {
            return NativeMethods.IdleMilliseconds() < elapsedMilliseconds - InputSlackMilliseconds;
        }

        /// <summary>
        /// Where in its waveform the beacon is, as a hardware level. Interpolation is done in
        /// hardware units rather than Boot Camp's 0..16 scale for the same reason Backlight's
        /// ramp is: sixteen steps across a sweep would be visibly a staircase.
        /// </summary>
        private static int LevelFor(FlashMode mode, int elapsedMilliseconds)
        {
            if (mode == FlashMode.Strobe)
            {
                int phase = elapsedMilliseconds % (StrobeOnMilliseconds + StrobeOffMilliseconds);
                return phase < StrobeOnMilliseconds ? Backlight.MaxHardwareLevel : 0;
            }

            int half = SweepPeriodMilliseconds / 2;
            int sweepPhase = elapsedMilliseconds % SweepPeriodMilliseconds;
            return sweepPhase < half
                ? Backlight.MaxHardwareLevel * sweepPhase / half
                : Backlight.MaxHardwareLevel * (SweepPeriodMilliseconds - sweepPhase) / half;
        }

        /// <summary>
        /// Milliseconds since the beacon started. TickCount wraps every 49.7 days, so the
        /// subtraction is unchecked and the low 32 bits are taken -- the same arithmetic
        /// NativeMethods.IdleMilliseconds does, and for the same reason.
        /// </summary>
        private static int ElapsedSince(int startTick)
        {
            unchecked
            {
                int elapsed = Environment.TickCount - startTick;
                return elapsed < 0 ? 0 : elapsed;
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_sync)
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// What one run of the thread was told to do. Handed over at Thread.Start rather than
        /// read from fields, so a second Start racing the first cannot change the waveform
        /// out from under a thread that is already flashing.
        /// </summary>
        private sealed class Session
        {
            public FlashRequest Request;
            public ManualResetEvent Stop;
        }
    }
}
