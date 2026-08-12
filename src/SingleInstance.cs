using System;
using System.Threading;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// Keeps one hider per logon session.
    ///
    /// This matters more here than in a program with a tray icon. Once enabled, this one
    /// runs with no window and nothing to see, so launching it again is the natural way to
    /// get back to its settings -- and if that started a second copy, two processes would
    /// be fighting over the same five hotkey registrations with no sign of it on screen.
    /// Instead the second process signals the first and exits, and the first brings its
    /// settings window up.
    ///
    /// Being resident is also what makes --flash worth having, so the same channel carries
    /// flash requests. Those need a payload, which a bare event cannot hold, so the request
    /// is left in an HKCU value and the event only says "there is one waiting". HKCU is
    /// already this program's storage and is scoped to the logon session by construction,
    /// which is the same scope as the "Local\" objects below -- a named pipe would have
    /// needed an ACL to say the same thing.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        // "Local\" scopes all three objects to the logon session, so two users on the same
        // machine each get their own hider -- which is right, because each user has their
        // own backlight level and their own volume.
        private const string MutexName = @"Local\HideBootcampTrayUtility.SingleInstance";
        private const string SignalName = @"Local\HideBootcampTrayUtility.ShowSettings";
        private const string FlashSignalName = @"Local\HideBootcampTrayUtility.Flash";

        private const string KeyPath = @"Software\HideBootcampTrayUtility";
        private const string FlashRequestValueName = "FlashRequest";

        /// <summary>
        /// What the slot holds to mean "stop flashing". A word rather than an empty value so
        /// a half-written slot can never read as a cancellation.
        /// </summary>
        private const string StopRequest = "stop";

        private Mutex _mutex;
        private bool _owned;
        private EventWaitHandle _showSettingsSignal;
        private EventWaitHandle _flashSignal;
        private EventWaitHandle _stopSignal;
        private Thread _listener;

        /// <summary>Raised on a background thread when another launch asks for the window.</summary>
        public event EventHandler ShowSettingsRequested;

        /// <summary>
        /// Raised on the listener thread when another launch asks for a flash. The argument
        /// is null for "--flash-stop". Handlers must not touch the UI without posting to it.
        /// </summary>
        public event EventHandler<FlashRequestedEventArgs> FlashRequested;

        /// <summary>
        /// True if this process is the first instance. When false the caller should signal
        /// the running instance and quit.
        /// </summary>
        public bool TryAcquire()
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                // The mutex already existed, so this thread never took ownership of it and
                // must not release it later -- ReleaseMutex would throw. Let go of it here
                // and record that there is nothing to release.
                _mutex.Close();
                _mutex = null;
                return false;
            }
            _owned = true;

            _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            _flashSignal = new EventWaitHandle(false, EventResetMode.AutoReset, FlashSignalName);
            _stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset);

            _listener = new Thread(Listen);
            _listener.IsBackground = true;
            _listener.Name = "HideBootcampTrayUtility single-instance listener";
            _listener.Start();
            return true;
        }

        /// <summary>Asks the already-running instance to show its settings window.</summary>
        public static void SignalRunningInstance()
        {
            EventWaitHandle signal;
            if (!EventWaitHandle.TryOpenExisting(SignalName, out signal))
            {
                // The other instance is shutting down; nothing to ask.
                return;
            }
            using (signal)
            {
                signal.Set();
            }
        }

        /// <summary>
        /// Asks the already-running instance to flash, or -- with a null request -- to stop
        /// flashing. The request is written before the event is set, so the listener never
        /// wakes to find a slot that has not been filled in yet.
        /// </summary>
        /// <returns>False if there was nobody listening, so the caller can flash itself.</returns>
        public static bool TrySignalFlash(FlashRequest request)
        {
            EventWaitHandle signal;
            if (!EventWaitHandle.TryOpenExisting(FlashSignalName, out signal))
            {
                return false;
            }

            using (signal)
            {
                if (!WriteFlashRequest(request == null ? StopRequest : request.ToWire()))
                {
                    return false;
                }
                signal.Set();
                return true;
            }
        }

        private static bool WriteFlashRequest(string wire)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    key.SetValue(FlashRequestValueName, wire, RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception)
            {
                // A write filter on an industrial image, most likely. Reporting it as "no
                // instance" sends the caller down the cold-start path, which still flashes.
                return false;
            }
        }

        private static string ReadFlashRequest()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null)
                    {
                        return null;
                    }
                    return key.GetValue(FlashRequestValueName) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Listen()
        {
            WaitHandle[] handles = new WaitHandle[] { _showSettingsSignal, _flashSignal, _stopSignal };
            while (true)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index == 0)
                {
                    EventHandler handler = ShowSettingsRequested;
                    if (handler != null)
                    {
                        handler(this, EventArgs.Empty);
                    }
                    continue;
                }

                if (index == 1)
                {
                    RaiseFlashRequested();
                    continue;
                }

                return;
            }
        }

        private void RaiseFlashRequested()
        {
            EventHandler<FlashRequestedEventArgs> handler = FlashRequested;
            if (handler == null)
            {
                return;
            }

            string wire = ReadFlashRequest();
            if (wire == null)
            {
                // The slot was unreadable. Doing nothing is the right answer: the alternative
                // is starting an endless flash the user cannot account for.
                return;
            }

            if (string.Equals(wire, StopRequest, StringComparison.OrdinalIgnoreCase))
            {
                handler(this, new FlashRequestedEventArgs(null));
                return;
            }

            FlashRequest request;
            if (FlashRequest.TryParseWire(wire, out request))
            {
                handler(this, new FlashRequestedEventArgs(request));
            }
        }

        public void Dispose()
        {
            if (_stopSignal != null)
            {
                _stopSignal.Set();
            }
            if (_listener != null)
            {
                _listener.Join(TimeSpan.FromSeconds(1));
                _listener = null;
            }
            if (_showSettingsSignal != null)
            {
                _showSettingsSignal.Close();
                _showSettingsSignal = null;
            }
            if (_flashSignal != null)
            {
                _flashSignal.Close();
                _flashSignal = null;
            }
            if (_stopSignal != null)
            {
                _stopSignal.Close();
                _stopSignal = null;
            }
            if (_mutex != null)
            {
                if (_owned)
                {
                    _mutex.ReleaseMutex();
                    _owned = false;
                }
                _mutex.Close();
                _mutex = null;
            }
        }
    }
}
