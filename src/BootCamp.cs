using System;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The two things this program does *to* Apple's Boot Camp tray utility: end it, and
    /// stop it coming back at the next logon.
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

        /// <summary>Whether Boot Camp installed a machine-wide autostart entry at all.</summary>
        public static bool HasStartupEntry()
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                           RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(RunKeyPath, false))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
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
