using System;
using System.Globalization;
using Microsoft.Win32;
using System.Windows.Forms;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// "Turn off keyboard backlight when computer is not used for..." -- the slider on the
    /// Keyboard tab of the Boot Camp control panel, which stores its answer in milliseconds
    /// as HKCU\Software\Apple Inc.\Apple Keyboard Support\User Absence.
    ///
    /// Bootcamp.exe imports GetLastInputInfo and SetTimer and installs no input hook, so it
    /// polls rather than watching keystrokes, and so does this. A second between polls is
    /// far finer than a setting whose shortest position is five seconds, and costs one
    /// syscall.
    ///
    /// The timer is a WinForms one, so the fade runs on the message-loop thread. It blocks
    /// that thread for about a second -- there is no window open to be blocked when this
    /// fires, since by definition nobody has touched the machine for minutes.
    /// </summary>
    internal sealed class BacklightIdle : IDisposable
    {
        private const string KeyPath = @"Software\Apple Inc.\Apple Keyboard Support";
        private const string ValueName = "User Absence";

        private const int PollMilliseconds = 1000;

        /// <summary>
        /// The shortest position on Boot Camp's slider is five seconds. Anything below a
        /// second would be a value this program did not write and cannot mean well, so it
        /// is read as "never".
        /// </summary>
        private const int MinimumInterval = 1000;

        private readonly Backlight _backlight;
        private readonly Timer _timer;

        private int _interval;

        public BacklightIdle(Backlight backlight)
        {
            _backlight = backlight;
            _timer = new Timer();
            _timer.Interval = PollMilliseconds;
            _timer.Tick += OnTick;
        }

        /// <summary>
        /// Begins watching, re-reading the interval as it goes. Re-read on every start
        /// because the Boot Camp control panel can be used to change the slider while this
        /// program is running, and it writes straight to the registry.
        /// </summary>
        public void Start()
        {
            _interval = ReadInterval();
            if (_interval <= 0)
            {
                // The slider is at "Never".
                Stop();
                return;
            }
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            // Leaving the light off after being switched off would strand it dark with
            // nothing left running to bring it back.
            _backlight.Restore();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (NativeMethods.IdleMilliseconds() >= _interval)
            {
                _backlight.Dim();
            }
            else
            {
                _backlight.Restore();
            }
        }

        private static int ReadInterval()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null)
                    {
                        return 0;
                    }
                    object value = key.GetValue(ValueName);
                    if (value == null)
                    {
                        return 0;
                    }
                    int milliseconds = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return milliseconds < MinimumInterval ? 0 : milliseconds;
                }
            }
            catch (Exception)
            {
                // Unreadable, out of range for an Int32, or written as something other than
                // a number. Treat every one of those as "never" rather than guessing at an
                // interval that would switch the light off at some arbitrary moment.
                return 0;
            }
        }

        public void Dispose()
        {
            // Through Stop, so a program shutting down while the light is faded out does
            // not leave the keyboard dark with nothing left to bring it back.
            Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
        }
    }
}
