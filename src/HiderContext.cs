using System;
using System.Threading;
using System.Windows.Forms;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// What the program is when there is no window on screen.
    ///
    /// An ApplicationContext rather than a main form is what makes "close to save settings"
    /// possible: closing the settings window disposes it, and the message loop carries on
    /// with no forms at all, serving hotkeys until the user turns the program off. Windows
    /// would end the process the moment the last form closed if the form were the context.
    ///
    /// The loop still has to exist even with nothing visible, because it is what the
    /// single-instance listener posts to when a second launch asks for the settings window.
    /// </summary>
    internal sealed class HiderContext : ApplicationContext
    {
        private readonly SingleInstance _instance;
        private readonly Backlight _backlight;
        private readonly HotkeyWorker _worker;
        private readonly BacklightIdle _idle;
        private readonly FlashWorker _flash;
        private readonly SynchronizationContext _ui;

        private MessageSink _sink;
        private SettingsForm _form;
        private string _driverStatus = "";

        public HiderContext(SingleInstance instance, bool showSettings, FlashRequest openingFlash)
        {
            _instance = instance;
            _backlight = new Backlight();
            _worker = new HotkeyWorker(_backlight);
            _idle = new BacklightIdle(_backlight);
            _flash = new FlashWorker(_backlight);

            // Captured here, on the UI thread, because the listener fires on its own thread
            // and forms may only be touched from this one.
            _ui = SynchronizationContext.Current;
            _instance.ShowSettingsRequested += OnShowSettingsRequested;
            _instance.FlashRequested += OnFlashRequested;

            ApplyEnabledState();

            if (showSettings)
            {
                ShowSettings();
            }

            if (openingFlash != null)
            {
                // A --flash that found nobody running started this process to do the flashing.
                // Last, so the window above is already up: it is the only sign that a program
                // with no tray icon has just taken up residence.
                _flash.Start(openingFlash);
            }
        }

        /// <summary>
        /// Brings everything into line with the saved setting: the driver settings Boot
        /// Camp would have applied at logon, the hotkeys, and the backlight idle timer.
        /// Boot Camp's tray utility is cleared out of the way first -- two programs
        /// registered for the same hotkeys would each get half the keypresses.
        /// </summary>
        private void ApplyEnabledState()
        {
            if (Settings.Enabled)
            {
                BootCamp.KillTrayUtility();

                // Before the hotkeys, because this is the half that has to happen at all:
                // without it the trackpad has no tap-to-click and F1/F2 do nothing, and no
                // amount of hotkey handling would put that right.
                ApplyDriverSettings();

                if (_sink == null)
                {
                    _sink = new MessageSink(ApplyDriverSettings);
                }

                _worker.Start();
                _idle.Start();
            }
            else
            {
                _idle.Stop();
                _worker.Stop();
                ReleaseSink();
            }
        }

        /// <summary>
        /// Pushes Boot Camp's saved settings into the drivers and re-asserts the backlight
        /// level. Called at startup and again whenever a HID device arrives or the machine
        /// wakes, either of which can leave a freshly loaded driver knowing nothing.
        /// </summary>
        private void ApplyDriverSettings()
        {
            _driverStatus = DriverInit.Apply();
            _backlight.Reassert();
        }

        private void ReleaseSink()
        {
            if (_sink != null)
            {
                _sink.Dispose();
                _sink = null;
            }
        }

        private void OnShowSettingsRequested(object sender, EventArgs e)
        {
            if (_ui == null)
            {
                return;
            }
            _ui.Post(delegate { ShowSettings(); }, null);
        }

        /// <summary>
        /// Handled straight on the listener thread, unlike the settings window. Nothing here
        /// touches a form, and going through the message loop would make the beacon hostage
        /// to whatever has the UI thread -- including the settings window's own modal work.
        /// </summary>
        private void OnFlashRequested(object sender, FlashRequestedEventArgs e)
        {
            if (e.Request == null)
            {
                _flash.Stop();
                return;
            }
            _flash.Start(e.Request);
        }

        private void ShowSettings()
        {
            if (_form != null && !_form.IsDisposed)
            {
                // Already open -- a second launch should surface it, not stack another.
                if (_form.WindowState == FormWindowState.Minimized)
                {
                    _form.WindowState = FormWindowState.Normal;
                }
                _form.Activate();
                return;
            }

            _form = new SettingsForm(_worker, _driverStatus);
            _form.FormClosed += OnSettingsClosed;
            _form.Show();
            _form.Activate();
        }

        /// <summary>
        /// The settings window has saved everything by the time this runs, so all that is
        /// left is to obey it: keep going with no window, or leave.
        /// </summary>
        private void OnSettingsClosed(object sender, FormClosedEventArgs e)
        {
            _form = null;
            ApplyEnabledState();

            if (!Settings.Enabled)
            {
                ExitThread();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _instance.ShowSettingsRequested -= OnShowSettingsRequested;
                _instance.FlashRequested -= OnFlashRequested;
                ReleaseSink();
                // Both before the backlight is let go, and the flasher before the idle timer:
                // ending the beacon is what puts the light back to the level -- or the dim --
                // it borrowed, and only then can stopping the idle timer undo that dim.
                _flash.Dispose();
                _idle.Dispose();
                _worker.Dispose();
                _backlight.Dispose();
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Dispose();
                    _form = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
