using System;
using System.Threading;

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
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        // "Local\" scopes both objects to the logon session, so two users on the same
        // machine each get their own hider -- which is right, because each user has their
        // own backlight level and their own volume.
        private const string MutexName = @"Local\HideBootcampTrayUtility.SingleInstance";
        private const string SignalName = @"Local\HideBootcampTrayUtility.ShowSettings";

        private Mutex _mutex;
        private bool _owned;
        private EventWaitHandle _showSettingsSignal;
        private EventWaitHandle _stopSignal;
        private Thread _listener;

        /// <summary>Raised on a background thread when another launch asks for the window.</summary>
        public event EventHandler ShowSettingsRequested;

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

        private void Listen()
        {
            WaitHandle[] handles = new WaitHandle[] { _showSettingsSignal, _stopSignal };
            while (true)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index != 0)
                {
                    return;
                }
                EventHandler handler = ShowSettingsRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
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
