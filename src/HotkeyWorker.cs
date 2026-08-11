using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The five keys that stop working on a MacBook the moment Bootcamp.exe is not running:
    ///
    ///     F5 / F6         keyboard backlight down / up
    ///     F10 / F11 / F12 mute / volume down / volume up
    ///
    /// F1/F2 brightness and the trackpad are not here, but not because they need nothing:
    /// they need to be *started*, once, and then they keep going on their own. That is
    /// DriverInit's job. This class is only the keys that need somebody listening.
    ///
    /// The mechanism, recovered by disassembling Bootcamp.exe: hotkeys arrive as an
    /// inverted-call notification. Each hotkey is registered with \\.\KeyManager by
    /// handing it an auto-reset event; the driver signals the matching event on keypress
    /// and this class sits in WaitForMultipleObjects waiting for one of them. The tray
    /// utility registers twenty-nine of these; these five are the ones with nothing else
    /// behind them.
    ///
    /// The wait runs on its own thread so the settings window stays responsive, and so
    /// the process can go on serving hotkeys with no window open at all. The backlight
    /// itself belongs to Backlight, which the idle timer shares.
    /// </summary>
    internal sealed class HotkeyWorker : IDisposable
    {
        private enum Hotkey
        {
            BacklightDown,
            BacklightUp,
            Mute,
            VolumeDown,
            VolumeUp
        }

        // Hotkey identifiers as the driver knows them: first mapped empirically on a
        // MacBookAir6,1 in the HideBootcampTrayUtility.ps1 prototype, and since confirmed exactly by
        // watching Bootcamp.exe register the same codes against \\.\KeyManager under
        // x64dbg. Order matches the Hotkey enum: the wait index is the enum value plus one.
        private static readonly uint[] HotkeyCodes = new uint[]
        {
            0xB403208F, // F5  backlight down
            0xB403208B, // F6  backlight up
            0xB4032073, // F10 mute
            0xB403204B, // F11 volume down
            0xB4032043  // F12 volume up
        };

        private readonly object _sync = new object();
        private readonly Backlight _backlight;

        private Thread _thread;
        private IntPtr _stopEvent = IntPtr.Zero;
        private string _status = "";
        private bool _running;

        public HotkeyWorker(Backlight backlight)
        {
            _backlight = backlight;
        }

        /// <summary>
        /// A sentence about what the hotkeys are actually doing, for the settings window
        /// to show. Set from the worker thread, read from the UI thread.
        /// </summary>
        public string Status
        {
            get { lock (_sync) { return _status; } }
        }

        public bool IsRunning
        {
            get { lock (_sync) { return _running; } }
        }

        /// <summary>
        /// Begins serving the hotkeys. Safe to call when already running, so callers can
        /// treat it as "make sure this is on" without tracking state themselves.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_thread != null)
                {
                    if (_thread.IsAlive)
                    {
                        return;
                    }
                    // A previous attempt gave up -- the drivers were missing, say. Clear it
                    // away so this call is a real retry rather than a silent no-op.
                    ReleaseStopEvent();
                    _thread = null;
                }

                _stopEvent = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
                if (_stopEvent == IntPtr.Zero)
                {
                    _status = "Could not create the shutdown event: " + LastError();
                    return;
                }

                _status = "Starting...";
                _thread = new Thread(Run);
                _thread.IsBackground = true;
                _thread.Name = "HideBootcampTrayUtility hotkey listener";
                _thread.Start();
            }
        }

        /// <summary>
        /// Stops serving the hotkeys and waits for the driver handles to be released, so a
        /// later Start() can register them again from a clean slate.
        /// </summary>
        public void Stop()
        {
            Thread thread;
            lock (_sync)
            {
                thread = _thread;
                if (thread == null)
                {
                    return;
                }
                if (_stopEvent != IntPtr.Zero)
                {
                    NativeMethods.SetEvent(_stopEvent);
                }
            }

            // Joined outside the lock: the worker thread takes it too when it publishes
            // status, and holding it here would deadlock the two against each other.
            thread.Join(TimeSpan.FromSeconds(3));

            lock (_sync)
            {
                ReleaseStopEvent();
                _thread = null;
                _running = false;
                _status = "Hotkeys are not being handled.";
            }
        }

        /// <summary>Closes the stop event. Callers must already hold _sync.</summary>
        private void ReleaseStopEvent()
        {
            if (_stopEvent != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_stopEvent);
                _stopEvent = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// The listener thread: open the device, register the five hotkeys, then wait.
        /// Everything it owns is closed on the way out, whether it left by the stop event
        /// or by an error.
        /// </summary>
        private void Run()
        {
            IntPtr keyManager = NativeMethods.InvalidHandleValue;
            IntPtr[] hotkeyEvents = null;

            try
            {
                keyManager = NativeMethods.OpenDevice(@"\\.\KeyManager");
                if (keyManager == NativeMethods.InvalidHandleValue)
                {
                    SetStatus("Could not open \\\\.\\KeyManager (" + LastError() +
                              "). Are the Boot Camp drivers installed?");
                    return;
                }

                int registered;
                hotkeyEvents = RegisterHotkeys(keyManager, out registered);
                if (registered == 0)
                {
                    SetStatus("The keyboard driver accepted none of the hotkeys. Is another " +
                              "copy of Boot Camp already handling them?");
                    return;
                }

                lock (_sync)
                {
                    _running = true;
                    _status = registered == HotkeyCodes.Length
                        ? "Handling F5/F6 backlight and F10/F11/F12 volume."
                        : string.Format(CultureInfo.CurrentCulture,
                            "Handling {0} of {1} hotkeys; the rest were refused by the driver.",
                            registered, HotkeyCodes.Length);
                }

                Listen(hotkeyEvents);
            }
            catch (Exception ex)
            {
                // A listener thread that throws would take the whole process down and the
                // hotkeys with it. Report and stop instead.
                SetStatus("Hotkey handling stopped: " + ex.Message);
            }
            finally
            {
                CloseHandles(hotkeyEvents, keyManager);
                lock (_sync)
                {
                    _running = false;
                }
            }
        }

        /// <summary>
        /// Creates one auto-reset event per hotkey and offers each to the driver. A refusal
        /// is not fatal -- the remaining keys still work -- so the events are created for
        /// every hotkey either way and the count of accepted ones is reported back.
        /// </summary>
        private static IntPtr[] RegisterHotkeys(IntPtr keyManager, out int registered)
        {
            IntPtr[] events = new IntPtr[HotkeyCodes.Length];
            registered = 0;

            for (int i = 0; i < HotkeyCodes.Length; i++)
            {
                events[i] = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
                if (events[i] == IntPtr.Zero)
                {
                    continue;
                }
                if (NativeMethods.RegisterHotkey(keyManager, HotkeyCodes[i], events[i]))
                {
                    registered++;
                }
            }
            return events;
        }

        /// <summary>
        /// The wait loop. The stop event sits at index 0 of the wait array, so a return of
        /// zero means shut down and anything higher is a hotkey.
        /// </summary>
        private void Listen(IntPtr[] hotkeyEvents)
        {
            IntPtr[] waitOn = new IntPtr[hotkeyEvents.Length + 1];
            lock (_sync)
            {
                waitOn[0] = _stopEvent;
            }
            Array.Copy(hotkeyEvents, 0, waitOn, 1, hotkeyEvents.Length);

            while (true)
            {
                uint result = NativeMethods.WaitForMultipleObjects((uint)waitOn.Length, waitOn,
                    false, NativeMethods.Infinite);

                if (result == 0 || result >= (uint)waitOn.Length)
                {
                    // Index 0 is the stop event; anything out of range is a failed or
                    // abandoned wait, which there is no useful way to recover from.
                    return;
                }

                switch ((Hotkey)(result - 1))
                {
                    case Hotkey.BacklightDown:
                        _backlight.Step(-1);
                        break;

                    case Hotkey.BacklightUp:
                        _backlight.Step(1);
                        break;

                    case Hotkey.Mute:
                        NativeMethods.TapKey(NativeMethods.VkVolumeMute);
                        break;

                    case Hotkey.VolumeDown:
                        NativeMethods.TapKey(NativeMethods.VkVolumeDown);
                        break;

                    case Hotkey.VolumeUp:
                        NativeMethods.TapKey(NativeMethods.VkVolumeUp);
                        break;
                }
            }
        }

        private static void CloseHandles(IntPtr[] hotkeyEvents, IntPtr keyManager)
        {
            // The device handle goes first. Closing it is what makes KeyManager drop the
            // hotkey registrations, so by the time the event handles are released nothing
            // is left pointing at them -- and a later Start() gets a clean slate.
            if (keyManager != NativeMethods.InvalidHandleValue)
            {
                NativeMethods.CloseHandle(keyManager);
            }
            if (hotkeyEvents != null)
            {
                for (int i = 0; i < hotkeyEvents.Length; i++)
                {
                    if (hotkeyEvents[i] != IntPtr.Zero)
                    {
                        NativeMethods.CloseHandle(hotkeyEvents[i]);
                    }
                }
            }
        }

        private void SetStatus(string status)
        {
            lock (_sync)
            {
                _status = status;
            }
        }

        private static string LastError()
        {
            int code = Marshal.GetLastWin32Error();
            return new System.ComponentModel.Win32Exception(code).Message;
        }
    }
}
