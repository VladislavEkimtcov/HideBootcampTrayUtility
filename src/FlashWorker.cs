using System;
using System.Collections.Generic;
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

        // At most one SMC write every 50 ms, so 20 a second, and fewer in practice because
        // Backlight skips a step that would not visibly change the light (a strobe writes
        // twice per blink, not once per step). Boot Camp's own ramp writes 8 in 96 ms, so
        // this is the same order of traffic, just sustained; and a 50 ms step is still far
        // finer than the eye resolves in a moving light.
        private const int StepMilliseconds = 50;

        /// <summary>
        /// The longest any one beacon may run, whatever it asked for -- including
        /// BackAtKeyboard, and a --duration larger than this.
        ///
        /// A flash is a sustained stream of SMC writes through MacHALDriver, and that driver
        /// does not know about anybody else using the SMC. Left running for an hour it is
        /// a standing invitation for another program's transaction to land in the middle of
        /// one of ours: the SMC is then left with its input buffer closed, rejects every
        /// command from every process (this program's restore-the-level write included, so
        /// the light is stranded at full) and only a power cycle clears it. A beacon is for
        /// getting somebody's attention, and two minutes either does that or it will not.
        ///
        /// This is a clock, not a guard: it is only looked at between writes, so it cannot
        /// end a write that is itself stuck. That is SmcGuard's job -- it serialises the
        /// writes with other programs, and stops them altogether once the SMC stops answering.
        /// </summary>
        public const int MaxFlashSeconds = 120;

        /// <summary>
        /// The most flashing allowed in any BudgetWindowSeconds, however it is asked for. A
        /// script that fires a beacon in a loop gets the first few minutes of it and then
        /// the light is left alone: every flash is hundreds of SMC writes, and the SMC is
        /// shared with the fan controller. A flash that would overrun the budget is cut
        /// short to what is left, and one with almost nothing left is not started.
        /// </summary>
        public const int BudgetSeconds = 180;
        public const int BudgetWindowSeconds = 600;

        // With less than this of the budget left a beacon would be cut too short to be seen,
        // so it is not started. (A short flash that was asked for is another matter: it runs.)
        private const int MinimumFlashMilliseconds = 3000;

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

        // A flasher that Stop could not get to finish: stuck inside a driver call. While it
        // is alive no other flasher starts, so there is never more than one thread writing.
        private Thread _unfinished;

        // When each recent flash ended and how long it ran, newest last, for the budget.
        private readonly List<FlashSpan> _recent = new List<FlashSpan>();

        public FlashWorker(Backlight backlight)
        {
            _backlight = backlight;
        }

        /// <summary>
        /// Starts flashing, replacing whatever was flashing before. A second request landing
        /// mid-beacon means the newer one is what the user wants to see, so the old thread is
        /// stopped and joined first and the two never drive the light at once -- and if the
        /// old one will not finish, the new one does not start at all.
        ///
        /// Nothing starts, silently, while the SMC is not answering, or once the flash
        /// budget for the last few minutes is spent.
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

                if (_unfinished != null)
                {
                    if (_unfinished.IsAlive)
                    {
                        return;
                    }
                    _unfinished = null;
                }

                if (_backlight.SmcBackingOff)
                {
                    return;
                }

                long budgetLeft = BudgetLeftMilliseconds();
                if (budgetLeft < MinimumFlashMilliseconds)
                {
                    return;
                }

                Session session = new Session();
                session.Request = request;
                session.LimitMilliseconds = Math.Min(LimitMilliseconds(request.DurationSeconds), budgetLeft);
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
            else
            {
                // Stuck in a driver call. Remembered so that Start will not put a second
                // thread beside it; the event is left open for it to find when it wakes.
                lock (_sync)
                {
                    _unfinished = thread;
                }
            }
        }

        /// <summary>
        /// How much of the budget is left for a flash starting now. Callers hold _sync.
        /// </summary>
        private long BudgetLeftMilliseconds()
        {
            long now = SmcGuard.NowMilliseconds();
            long windowStart = now - (long)BudgetWindowSeconds * 1000;
            long spent = 0;

            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                FlashSpan span = _recent[i];
                if (span.EndedAt <= windowStart)
                {
                    _recent.RemoveAt(i);
                    continue;
                }

                // A flash that ended inside the window but began before it only counts for
                // the part that is inside.
                long inside = Math.Min(span.Duration, span.EndedAt - windowStart);
                spent += inside;
            }

            return Math.Max(0, (long)BudgetSeconds * 1000 - spent);
        }

        private void RecordFlash(long durationMilliseconds)
        {
            lock (_sync)
            {
                FlashSpan span = new FlashSpan();
                span.EndedAt = SmcGuard.NowMilliseconds();
                span.Duration = durationMilliseconds;
                _recent.Add(span);
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
            long limitMilliseconds = session.LimitMilliseconds;

            try
            {
                while (true)
                {
                    int elapsed = ElapsedSince(startTick);

                    // A duration is a ceiling, not a contract: whichever comes first, the
                    // clock running out or the user sitting back down, ends the beacon. It
                    // has done its job either way, and a flash you cannot stop by touching
                    // the keyboard is a flash you come to resent. There is always a clock:
                    // "until somebody touches the machine" still stops at MaxFlashSeconds.
                    if (elapsed >= limitMilliseconds)
                    {
                        break;
                    }

                    if (elapsed >= ArmingMilliseconds && InputSince(elapsed))
                    {
                        break;
                    }

                    // False means the light was taken back or the SMC stopped answering;
                    // either way the beacon is over, and a dead SMC is left alone.
                    if (!_backlight.FlashSet(LevelFor(request.Mode, elapsed)))
                    {
                        break;
                    }

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
                RecordFlash(ElapsedSince(startTick));
            }
        }

        /// <summary>
        /// How long a beacon may run: what was asked for, but never more than
        /// MaxFlashSeconds. Zero is how BackAtKeyboard travels, and means "as long as
        /// allowed" -- there is no request that gets an unbounded flash.
        /// </summary>
        private static long LimitMilliseconds(int requestedSeconds)
        {
            int seconds = (requestedSeconds <= 0 || requestedSeconds > MaxFlashSeconds)
                ? MaxFlashSeconds
                : requestedSeconds;
            return (long)seconds * 1000;
        }

        /// <summary>
        /// True when the machine has been idle for less time than the beacon has been
        /// running, which can only mean somebody touched it after the beacon started.
        /// Mouse movement counts: "back at the keyboard" means back at the machine.
        /// </summary>
        private static bool InputSince(int elapsedMilliseconds)
        {
            return IdleProbe() < elapsedMilliseconds - InputSlackMilliseconds;
        }

        // How long the machine has been idle; replaced in a test, which has no one to type.
        internal static Func<int> IdleProbe = NativeMethods.IdleMilliseconds;

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
            public long LimitMilliseconds;
            public ManualResetEvent Stop;
        }

        /// <summary>One finished flash, as the budget remembers it.</summary>
        private struct FlashSpan
        {
            public long EndedAt;
            public long Duration;
        }
    }
}
