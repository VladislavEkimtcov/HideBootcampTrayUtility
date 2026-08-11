using System;
using System.Reflection;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// Start-with-Windows support via the per-user Run key -- which is the same thing as
    /// appearing in Task Manager's Startup tab, the way task.md asks for.
    ///
    /// A Run entry is used rather than a service or a scheduled task because this program
    /// has to live in the logged-on user's session: it presses volume keys into that
    /// session's input queue and writes that user's backlight level. HKCU\...\Run also
    /// needs no administrator rights and survives profile migration.
    ///
    /// The entry starts the program with --background, so a logon never puts a settings
    /// window in the user's face -- the whole point of the program is not to be seen.
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// Also the name the HideBootcampTrayUtility.ps1 prototype installed itself under, so ticking
        /// the box replaces the prototype's entry instead of running both.
        /// </summary>
        public const string ValueName = "HideBootcampTrayUtility";

        /// <summary>
        /// Full path of the running executable. Environment.ProcessPath does not exist on
        /// .NET Framework, so the entry assembly's location is used instead.
        /// </summary>
        public static string ExecutablePath
        {
            get { return Assembly.GetEntryAssembly().Location; }
        }

        private static string RunCommand
        {
            // Quoted so a path containing spaces is passed as one argument.
            get { return "\"" + ExecutablePath + "\" " + Program.BackgroundSwitch; }
        }

        /// <summary>
        /// True when the entry exists, points at this exact executable, and has not been
        /// switched off in Task Manager. An entry left behind by a copy that has since been
        /// moved counts as disabled, so ticking the box repairs it.
        /// </summary>
        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }
                string value = key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }
                if (!string.Equals(ExtractExecutable(value), ExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return !StartupApproval.IsDisabled(RegistryHive.CurrentUser, ValueName);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (enabled)
                    {
                        key.SetValue(ValueName, RunCommand, RegistryValueKind.String);
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

            if (enabled)
            {
                // Somebody may have switched this entry off in Task Manager on an earlier
                // run. Writing the Run value again would not undo that, and the tick would
                // silently do nothing, so the switch is cleared too.
                StartupApproval.TrySetDisabled(RegistryHive.CurrentUser, ValueName, false);
            }
            else
            {
                StartupApproval.Remove(RegistryHive.CurrentUser, ValueName);
            }
        }

        /// <summary>
        /// Pulls the executable path out of a Run command line, which is the quoted path
        /// followed by this program's own switches. Shared with BootCamp, which has to read
        /// Apple's Run entry the same way to find where Boot Camp was installed.
        /// </summary>
        internal static string ExtractExecutable(string command)
        {
            string trimmed = command.Trim();
            if (trimmed.Length > 0 && trimmed[0] == '"')
            {
                int closing = trimmed.IndexOf('"', 1);
                if (closing > 0)
                {
                    return trimmed.Substring(1, closing - 1);
                }
                return trimmed.Substring(1);
            }

            // Unquoted: an older or hand-written entry. Take everything up to the first
            // space, which is all an unquoted path can have been anyway.
            int space = trimmed.IndexOf(' ');
            return space < 0 ? trimmed : trimmed.Substring(0, space);
        }
    }
}
