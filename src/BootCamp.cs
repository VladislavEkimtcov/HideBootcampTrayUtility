using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// Everything this program does *to* Apple's Boot Camp: end the tray utility, stop it
    /// coming back at the next logon, start it again if it is wanted back, and open the
    /// control panel Apple leaves out of the Start menu.
    ///
    /// Ending it needs no special rights -- it runs as the logged-on user, and so do we.
    /// Its autostart is a different matter: Boot Camp installs a machine-wide Run entry,
    /// HKLM\...\Run\Apple_KbdMgr, and switching that off writes to HKLM. That is the one
    /// thing this program's asInvoker manifest cannot do, so the write is handed to a
    /// second copy of the program started with the runas verb, which puts up the UAC
    /// prompt, changes the one value and leaves. Everything else still runs unelevated.
    ///
    /// The elevated copy sets the same Startup-tab switch a person would (see
    /// StartupApproval) rather than deleting the Run entry, so the change is visible where
    /// users expect it and can be undone from Task Manager without this program.
    /// </summary>
    internal static class BootCamp
    {
        /// <summary>Bootcamp.exe, without the extension -- what Process reports.</summary>
        private const string TrayProcessName = "Bootcamp";

        /// <summary>Where Boot Camp installs, when its Run entry is no help.</summary>
        private const string TrayFallbackFolder = "Boot Camp";
        private const string TrayFileName = "Bootcamp.exe";

        /// <summary>
        /// The Boot Camp control panel: the trackpad, keyboard and backlight settings
        /// themselves. It lives in System32 under this name and nowhere else -- there is no
        /// .cpl and no Start menu entry, and the only way in that Apple ships is the tray
        /// icon's context menu, which is exactly what this program takes away.
        /// </summary>
        private const string ControlPanelFileName = "AppleControlPanel.exe";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>The name Boot Camp gives its own Run entry.</summary>
        public const string RunValueName = "Apple_KbdMgr";

        /// <summary>The switch the elevated copy of the program is started with.</summary>
        private const string ElevatedSwitch = "--bootcamp-autostart";
        private const string DisableArgument = "disable";
        private const string EnableArgument = "enable";

        // Exit codes of the elevated copy. Its only way of reporting back: the runas verb
        // needs UseShellExecute, which rules out capturing its output.
        private const int ExitWritten = 0;
        private const int ExitWriteFailed = 1;
        private const int ExitBadArguments = 2;

        /// <summary>ERROR_CANCELLED -- the UAC prompt was dismissed.</summary>
        private const int ErrorCancelled = 1223;

        /// <summary>What came of asking for Boot Camp's autostart to change.</summary>
        public enum ChangeResult
        {
            Changed,
            Cancelled,
            Failed
        }

        /// <summary>
        /// Ends the Boot Camp tray utility if it is running. Killing it is the only option
        /// -- it has no close command and no window to ask -- but it holds no state worth
        /// saving, and its hotkeys are exactly what this program is taking over.
        /// </summary>
        public static void KillTrayUtility()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(TrayProcessName);
            }
            catch (Exception)
            {
                return;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                    }
                    catch (Exception)
                    {
                        // Already gone by the time we got here, or running as another user.
                        // Nothing useful to do either way.
                    }
                }
            }
        }

        /// <summary>Whether Apple's tray utility is running right now.</summary>
        public static bool IsTrayUtilityRunning()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(TrayProcessName);
            }
            catch (Exception)
            {
                return false;
            }

            foreach (Process process in processes)
            {
                process.Dispose();
            }
            return processes.Length > 0;
        }

        /// <summary>
        /// Full path of the Boot Camp control panel: the real System32 copy, which is the
        /// only one there is.
        ///
        /// MSBuild builds this AnyCPU executable as Prefer32Bit, so it runs under WOW64 and
        /// asking for System32 gets SysWOW64, where Apple installs nothing. Both the
        /// existence check and the launch therefore run with the redirector switched off.
        ///
        /// The obvious alternative, the "Sysnative" alias, gets as far as File.Exists and
        /// then fails: the control panel's manifest asks for highestAvailable, so an
        /// administrator's copy is started by the elevation service, and that service is a
        /// different process, for which Sysnative means nothing. ShellExecute comes back
        /// with ERROR_PATH_NOT_FOUND.
        /// </summary>
        public static string ControlPanelPath
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32"),
                    ControlPanelFileName);
            }
        }

        /// <summary>
        /// Full path of Apple's tray utility, taken from the Run entry that starts it --
        /// the one place the installer records where it put itself. Falls back to the
        /// default install folder when the entry has been deleted rather than disabled.
        /// </summary>
        public static string TrayUtilityPath
        {
            get
            {
                string fromRunEntry = TrayUtilityPathFromRunEntry();
                if (fromRunEntry != null)
                {
                    return fromRunEntry;
                }

                return Path.Combine(Path.Combine(ProgramFilesDirectory, TrayFallbackFolder),
                    TrayFileName);
            }
        }

        /// <summary>
        /// The 64-bit Program Files, not the (x86) one this 32-bit process is otherwise
        /// handed: Boot Camp is a 64-bit install. ProgramW6432 is set for processes of
        /// either bitness on 64-bit Windows, and absent on 32-bit Windows, where the plain
        /// folder is already the right one.
        /// </summary>
        private static string ProgramFilesDirectory
        {
            get
            {
                string native = Environment.GetEnvironmentVariable("ProgramW6432");
                if (!string.IsNullOrEmpty(native))
                {
                    return native;
                }
                return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }
        }

        /// <summary>
        /// Where Boot Camp's own Run entry says it is, or null if that leads nowhere.
        ///
        /// Apple writes the path unquoted, spaces and all, so the entry has to be tried
        /// whole before it is split on the first space -- otherwise the standard reading of
        /// a Run command finds only "C:\Program".
        /// </summary>
        private static string TrayUtilityPathFromRunEntry()
        {
            string command = ReadRunCommand();
            if (string.IsNullOrEmpty(command))
            {
                return null;
            }

            string whole = command.Trim();
            if (SafeExists(whole))
            {
                return whole;
            }

            string path = AutoStart.ExtractExecutable(command);
            if (!string.IsNullOrEmpty(path) && SafeExists(path))
            {
                return path;
            }
            return null;
        }

        public static bool HasControlPanel()
        {
            using (NativeMethods.Wow64Redirection.Off())
            {
                return SafeExists(ControlPanelPath);
            }
        }

        public static bool HasTrayUtility()
        {
            return SafeExists(TrayUtilityPath);
        }

        /// <summary>
        /// Opens the Boot Camp control panel.
        ///
        /// Its manifest asks for highestAvailable, so on an administrator's account Windows
        /// puts up a UAC prompt -- which means the shell has to do the starting. Started
        /// any other way it fails outright with ERROR_ELEVATION_REQUIRED. On a standard
        /// account highestAvailable is just asInvoker and nothing is asked.
        /// </summary>
        public static ChangeResult OpenControlPanel()
        {
            // No working directory: it is a System32 program with nothing beside it, and
            // handing the shell a redirected directory would only reintroduce the problem
            // the scope below exists to avoid.
            using (NativeMethods.Wow64Redirection.Off())
            {
                if (!SafeExists(ControlPanelPath))
                {
                    return ChangeResult.Failed;
                }
                return Launch(ControlPanelPath, null);
            }
        }

        /// <summary>
        /// Starts Apple's tray utility again, for someone who wants it back. It is
        /// asInvoker, so this raises no prompt.
        /// </summary>
        public static ChangeResult StartTrayUtility()
        {
            if (!HasTrayUtility())
            {
                return ChangeResult.Failed;
            }
            string path = TrayUtilityPath;
            // Its localised strings live in Boot Camp.Resources, beside the executable.
            return Launch(path, Path.GetDirectoryName(path));
        }

        /// <summary>
        /// Starts one of Apple's programs and does not wait for it: both of them are
        /// windows the user is about to work in, not helpers with an answer to give.
        /// </summary>
        private static ChangeResult Launch(string path, string workingDirectory)
        {
            ProcessStartInfo start = new ProcessStartInfo(path);
            start.UseShellExecute = true;
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                start.WorkingDirectory = workingDirectory;
            }

            try
            {
                Process started = Process.Start(start);
                if (started != null)
                {
                    started.Dispose();
                }
                return ChangeResult.Changed;
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == ErrorCancelled
                    ? ChangeResult.Cancelled
                    : ChangeResult.Failed;
            }
            catch (Exception)
            {
                return ChangeResult.Failed;
            }
        }

        private static bool SafeExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The command line Boot Camp's own Run entry holds, or null.</summary>
        private static string ReadRunCommand()
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                           RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(RunKeyPath, false))
                {
                    return key == null ? null : key.GetValue(RunValueName) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether Boot Camp installed a machine-wide autostart entry at all.</summary>
        public static bool HasStartupEntry()
        {
            return !string.IsNullOrEmpty(ReadRunCommand());
        }

        /// <summary>
        /// Whether Boot Camp is stopped from starting at logon. An absent Run entry counts
        /// as disabled: there is nothing left to start, and the checkbox should say so.
        /// </summary>
        public static bool IsStartupDisabled()
        {
            if (!HasStartupEntry())
            {
                return true;
            }
            return StartupApproval.IsDisabled(RegistryHive.LocalMachine, RunValueName);
        }

        /// <summary>
        /// Asks for Boot Camp's autostart to be switched off or back on, elevating to do
        /// it. Blocks while the UAC prompt is up, which is what the caller wants: the
        /// answer is the next thing that has to happen either way.
        /// </summary>
        public static ChangeResult RequestStartupDisabled(bool disabled)
        {
            ProcessStartInfo start = new ProcessStartInfo(AutoStart.ExecutablePath);
            start.Arguments = ElevatedSwitch + " " + (disabled ? DisableArgument : EnableArgument);
            // The runas verb is what raises the prompt, and it needs the shell to do the
            // starting -- hence UseShellExecute, and hence exit codes rather than output.
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;

            Process helper;
            try
            {
                helper = Process.Start(start);
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == ErrorCancelled
                    ? ChangeResult.Cancelled
                    : ChangeResult.Failed;
            }
            catch (Exception)
            {
                return ChangeResult.Failed;
            }

            if (helper == null)
            {
                return ChangeResult.Failed;
            }

            using (helper)
            {
                helper.WaitForExit();
                return helper.ExitCode == ExitWritten ? ChangeResult.Changed : ChangeResult.Failed;
            }
        }

        /// <summary>
        /// Whether the arguments are the elevated copy's, rather than something a user
        /// typed. Kept beside the switch itself so the two cannot drift apart.
        /// </summary>
        public static bool IsElevatedRequest(string[] args)
        {
            return args.Length > 0 && string.Equals(args[0], ElevatedSwitch, StringComparison.Ordinal);
        }

        /// <summary>
        /// What the elevated copy of the program runs instead of showing a window: one
        /// registry value, then out. Returns the process exit code.
        /// </summary>
        public static int RunElevatedWrite(string[] args)
        {
            if (args.Length != 2)
            {
                return ExitBadArguments;
            }

            bool disable = string.Equals(args[1], DisableArgument, StringComparison.Ordinal);
            if (!disable && !string.Equals(args[1], EnableArgument, StringComparison.Ordinal))
            {
                return ExitBadArguments;
            }

            return StartupApproval.TrySetDisabled(RegistryHive.LocalMachine, RunValueName, disable)
                ? ExitWritten
                : ExitWriteFailed;
        }
    }
}
