using System;
using System.Globalization;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The one preference this program keeps for itself, under HKCU.
    ///
    /// The other two checkboxes in the settings window have no storage here on purpose:
    /// "auto-start this hider" *is* the Run entry, and "disable Boot Camp autostart" *is*
    /// the Startup-tab switch. Both are read back from the registry Windows already keeps,
    /// so the window can never disagree with what Task Manager shows.
    /// </summary>
    internal static class Settings
    {
        private const string KeyPath = @"Software\HideBootcampTrayUtility";
        private const string EnabledValueName = "Enabled";

        /// <summary>
        /// Whether this program takes over the Boot Camp hotkeys. Off until asked: it ends
        /// Apple's tray utility and keeps a process running after its window closes, and
        /// neither should happen to somebody who only opened the settings to look.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    object value = key.GetValue(EnabledValueName);
                    if (value == null)
                    {
                        return false;
                    }
                    try
                    {
                        return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
                    }
                    catch (Exception)
                    {
                        // Something else wrote a string or a blob here; treat it as unset.
                        return false;
                    }
                }
            }

            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    {
                        if (key != null)
                        {
                            key.SetValue(EnabledValueName, value ? 1 : 0, RegistryValueKind.DWord);
                        }
                    }
                }
                catch (Exception)
                {
                    // An industrial image may have a write filter in the way. The setting
                    // still holds for this session; it just will not survive a reboot.
                }
            }
        }
    }
}
