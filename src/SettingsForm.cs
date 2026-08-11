using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The whole user interface: three checkboxes, a line telling you that closing the
    /// window is how they are saved, and three buttons for reaching Apple's own programs.
    ///
    /// There is no OK button and no Cancel button on purpose. Two of the three settings are
    /// registry state Windows itself owns, so there is nothing to "cancel" back to once the
    /// window is closed anyway, and a program whose job is to stay out of the way should
    /// not ask twice. Everything is applied in FormClosing.
    ///
    /// The buttons are the exception, and deliberately so: they are not settings of this
    /// program but actions on another one, so they happen when pressed. "Close to save
    /// settings" stays true of everything above them.
    ///
    /// The window is built in code rather than with a .resx designer file: the in-box
    /// MSBuild can compile resources, but hand-written layout keeps this to one source file
    /// with nothing generated to keep in step.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const string EnabledTooltip =
            "Take over from Apple's tray utility.\r\n\r\n" +
            "Applies the saved Boot Camp settings to the trackpad and keyboard drivers -- " +
            "tap to click, secondary tap, and F1/F2 display brightness -- and handles the " +
            "hotkeys Windows has no answer for: F5 and F6 for the keyboard backlight, " +
            "F10, F11 and F12 for mute and volume.\r\n\r\n" +
            "Ends Apple's Bootcamp.exe tray utility, and keeps this program running in the " +
            "background after you close this window so the keys go on working.\r\n\r\n" +
            "Unticked, this program stops handling the keys and exits when the window closes.";

        private const string AutoStartTooltip =
            "Adds this program to the Startup tab of Task Manager, so it starts silently at " +
            "every logon with no window.\r\n\r\n" +
            "This is a per-user setting and needs no administrator rights. You can turn it " +
            "off again here or in Task Manager.";

        private const string DisableBootCampTooltip =
            "Stops Apple's Bootcamp.exe from starting at logon, using the same switch as " +
            "Task Manager's Startup tab -- the entry is left in place, just disabled.\r\n\r\n" +
            "Boot Camp's entry is machine-wide, so this one change needs administrator " +
            "approval: Windows will ask when you close this window.";

        private const string NoBootCampEntryTooltip =
            "Boot Camp has no startup entry on this PC, so there is nothing to disable.";

        private const string BootCampExplainer =
            "The trackpad, keyboard and backlight settings themselves are still Apple's to " +
            "edit, in the Boot Camp Control Panel -- which Apple never adds to the Start " +
            "menu, and which is normally reachable only from the tray icon this program " +
            "does away with. Open it here; this program reads what it leaves behind and " +
            "pushes it to the drivers.";

        private const string ControlPanelTooltip =
            "Opens Apple's Boot Camp Control Panel (AppleControlPanel.exe), where the " +
            "trackpad, keyboard and keyboard-backlight settings are made.\r\n\r\n" +
            "Apple asks for administrator rights to open it, so Windows may prompt. " +
            "Closing its window is enough to end it.";

        private const string StartTrayTooltip =
            "Starts Apple's Bootcamp.exe tray utility, which puts its icon back in the " +
            "notification area.\r\n\r\n" +
            "You only need this to hand the hotkeys back to Apple. Everything the tray " +
            "utility does is what this program is doing in its place.";

        private const string StopTrayTooltip =
            "Ends Apple's Bootcamp.exe tray utility.\r\n\r\n" +
            "Apple gives it no exit command and no way to close it, so this is the only " +
            "way to be rid of it once it is running.";

        private const string NoBootCampInstalledTooltip =
            "Boot Camp is not installed on this PC.";

        private readonly HotkeyWorker _worker;
        private readonly string _driverStatus;

        private readonly CheckBox _enabled;
        private readonly CheckBox _autoStart;
        private readonly CheckBox _disableBootCampStartup;
        private readonly Label _status;
        private readonly Button _openControlPanel;
        private readonly Button _startTray;
        private readonly Button _stopTray;
        private readonly Label _trayStatus;
        private readonly ToolTip _tips;

        public SettingsForm(HotkeyWorker worker, string driverStatus)
        {
            _worker = worker;
            _driverStatus = driverStatus;

            Text = "Boot Camp Tray Hider";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(452, 414);

            _tips = new ToolTip();
            // The explanations are long sentences, not labels, so they are given time to be
            // read and are shown even when the window is not the active one.
            _tips.AutoPopDelay = 30000;
            _tips.InitialDelay = 350;
            _tips.ReshowDelay = 150;
            _tips.ShowAlways = true;

            _enabled = AddCheckBox("Enabled", 18, EnabledTooltip);
            _autoStart = AddCheckBox("Auto-start this hider", 54, AutoStartTooltip);
            _disableBootCampStartup = AddCheckBox("Disable Boot Camp autostart", 90,
                DisableBootCampTooltip);

            _status = new Label();
            _status.Location = new Point(20, 130);
            _status.Size = new Size(412, 92);
            _status.ForeColor = SystemColors.GrayText;
            Controls.Add(_status);

            Label hint = new Label();
            hint.Text = "Close to save settings.";
            hint.Location = new Point(20, 232);
            hint.AutoSize = true;
            Controls.Add(hint);

            Label explainer = new Label();
            explainer.Text = BootCampExplainer;
            explainer.Location = new Point(20, 268);
            explainer.Size = new Size(412, 86);
            Controls.Add(explainer);

            _openControlPanel = AddButton("Open Control Panel", 20, 358,
                ControlPanelTooltip, OnOpenControlPanel);
            _startTray = AddButton("Start tray utility", 160, 358, StartTrayTooltip,
                OnStartTray);
            _stopTray = AddButton("Stop tray utility", 300, 358, StopTrayTooltip,
                OnStopTray);

            _trayStatus = new Label();
            _trayStatus.Location = new Point(20, 392);
            _trayStatus.Size = new Size(412, 16);
            _trayStatus.ForeColor = SystemColors.GrayText;
            Controls.Add(_trayStatus);

            LoadCurrentState();
        }

        private CheckBox AddCheckBox(string text, int top, string tooltip)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Location = new Point(20, top);
            box.Size = new Size(412, 24);
            _tips.SetToolTip(box, tooltip);
            Controls.Add(box);
            return box;
        }

        private Button AddButton(string text, int left, int top, string tooltip,
            EventHandler onClick)
        {
            Button button = new Button();
            button.Text = text;
            button.Location = new Point(left, top);
            button.Size = new Size(132, 27);
            button.Click += onClick;
            _tips.SetToolTip(button, tooltip);
            Controls.Add(button);
            return button;
        }

        /// <summary>
        /// Fills the boxes in from what is actually true right now, rather than from
        /// anything this program remembers. Two of the three live in registry keys Windows
        /// and Task Manager also write, so reading them fresh is the only way the window
        /// can be trusted.
        /// </summary>
        private void LoadCurrentState()
        {
            _enabled.Checked = Settings.Enabled;
            _autoStart.Checked = AutoStart.IsEnabled();
            _disableBootCampStartup.Checked = BootCamp.IsStartupDisabled();

            if (!BootCamp.HasStartupEntry())
            {
                // Nothing to disable: the box reads as already done, and says why.
                _disableBootCampStartup.Enabled = false;
                _tips.SetToolTip(_disableBootCampStartup, NoBootCampEntryTooltip);
            }

            // Two sentences, because the program now does two separable things and either
            // can fail on its own: the one-off push of Boot Camp's settings into the
            // drivers, and the hotkeys it goes on handling afterwards.
            string hotkeys = _worker.Status;
            if (string.IsNullOrEmpty(hotkeys))
            {
                hotkeys = "Not handling the hotkeys yet. Tick Enabled and close this window.";
            }

            _status.Text = string.IsNullOrEmpty(_driverStatus)
                ? hotkeys
                : _driverStatus + "\r\n\r\n" + hotkeys;

            if (!BootCamp.HasControlPanel())
            {
                _openControlPanel.Enabled = false;
                _tips.SetToolTip(_openControlPanel, NoBootCampInstalledTooltip);
            }
            if (!BootCamp.HasTrayUtility())
            {
                _tips.SetToolTip(_startTray, NoBootCampInstalledTooltip);
            }

            UpdateTrayButtons(null);
        }

        /// <summary>
        /// Brings the two tray buttons and the line under them into line with whether
        /// Apple's utility is running. Read afresh every time rather than remembered:
        /// anything at all can start or end that process while this window is open.
        /// </summary>
        /// <param name="note">
        /// What just happened, if anything, shown in place of the plain state.
        /// </param>
        private void UpdateTrayButtons(string note)
        {
            bool installed = BootCamp.HasTrayUtility();
            bool running = BootCamp.IsTrayUtilityRunning();

            _startTray.Enabled = installed && !running;
            _stopTray.Enabled = running;

            if (!string.IsNullOrEmpty(note))
            {
                _trayStatus.Text = note;
            }
            else if (running)
            {
                _trayStatus.Text = "Apple's tray utility is running.";
            }
            else if (installed)
            {
                _trayStatus.Text = "Apple's tray utility is not running.";
            }
            else
            {
                _trayStatus.Text = "Boot Camp is not installed on this PC.";
            }
        }

        private void OnOpenControlPanel(object sender, EventArgs e)
        {
            BootCamp.ChangeResult result = BootCamp.OpenControlPanel();
            if (result == BootCamp.ChangeResult.Failed)
            {
                MessageBox.Show(this,
                    "Apple's Boot Camp Control Panel could not be opened.\r\n\r\n" +
                    "It is installed as \"" + BootCamp.ControlPanelPath + "\".",
                    "Boot Camp Tray Hider",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            // Cancelled means the UAC prompt was dismissed. The user has already answered
            // the question, so there is nothing to tell them.
        }

        private void OnStartTray(object sender, EventArgs e)
        {
            BootCamp.ChangeResult result = BootCamp.StartTrayUtility();
            if (result == BootCamp.ChangeResult.Failed)
            {
                MessageBox.Show(this,
                    "Apple's tray utility could not be started.\r\n\r\n" +
                    "It is installed as \"" + BootCamp.TrayUtilityPath + "\".",
                    "Boot Camp Tray Hider",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateTrayButtons(null);
                return;
            }

            // Boot Camp takes a moment to appear, and IsTrayUtilityRunning asks the system
            // rather than the process just started, so give it that moment before the line
            // below the buttons claims anything.
            WaitForTrayUtility();

            // Enabled means this program ends the tray utility whenever it applies itself,
            // which it does the moment this window closes. Saying so is more use than
            // silently undoing what the user just asked for.
            UpdateTrayButtons(_enabled.Checked
                ? "Started. Enabled is ticked, so it is ended again when you close this window."
                : null);
        }

        private void OnStopTray(object sender, EventArgs e)
        {
            // No wait afterwards: KillTrayUtility does not come back until the process it
            // ended has actually gone.
            BootCamp.KillTrayUtility();
            UpdateTrayButtons(null);
        }

        /// <summary>
        /// Waits briefly for Apple's utility to show up in the process list, so the buttons
        /// are not redrawn from a state that is about to be half a second out of date. The
        /// window is unresponsive for as long as this takes, which is why it is short.
        /// </summary>
        private void WaitForTrayUtility()
        {
            UseWaitCursor = true;
            try
            {
                for (int waited = 0; waited < 1500; waited += 100)
                {
                    if (BootCamp.IsTrayUtilityRunning())
                    {
                        return;
                    }
                    Thread.Sleep(100);
                }
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            Settings.Enabled = _enabled.Checked;
            AutoStart.SetEnabled(_autoStart.Checked);
            ApplyBootCampStartup();
        }

        /// <summary>
        /// The one setting that has to leave this process to be applied. Nothing happens
        /// unless the box actually differs from the state on disk, so closing the window
        /// without touching it never raises a UAC prompt.
        /// </summary>
        private void ApplyBootCampStartup()
        {
            if (!_disableBootCampStartup.Enabled)
            {
                return;
            }

            bool wanted = _disableBootCampStartup.Checked;
            if (wanted == BootCamp.IsStartupDisabled())
            {
                return;
            }

            BootCamp.ChangeResult result = BootCamp.RequestStartupDisabled(wanted);
            if (result == BootCamp.ChangeResult.Changed)
            {
                return;
            }

            string detail = result == BootCamp.ChangeResult.Cancelled
                ? "Administrator approval was not given, so Boot Camp's autostart was left unchanged."
                : "Boot Camp's autostart could not be changed.";

            MessageBox.Show(this,
                detail + "\r\n\r\nIts entry is machine-wide, so changing it needs " +
                "administrator rights. You can also switch \"Apple_KbdMgr\" off in " +
                "Task Manager's Startup tab.\r\n\r\nYour other settings were saved.",
                "Boot Camp Tray Hider",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tips != null)
            {
                _tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
