using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HideBootcampTrayUtility
{
    internal static class Program
    {
        /// <summary>
        /// Start without showing the settings window. This is what the autostart entry
        /// passes: a hider that opened a window at every logon would be no better than the
        /// tray icon it replaces.
        /// </summary>
        public const string BackgroundSwitch = "--background";

        /// <summary>
        /// Not for people to type. A --flash that finds nobody home starts one of these,
        /// which becomes the resident copy and does the flashing itself -- the same
        /// relaunch-with-an-internal-switch trick BootCamp uses for its elevated helper.
        /// </summary>
        private const string FlashResidentSwitch = "--flash-resident";

        private const int ExitOk = 0;
        private const int ExitBadArguments = 2;
        private const int ExitCouldNotStart = 5;

        private static bool _consoleTried;
        private static TextWriter _callerConsole;

        [STAThread]
        private static int Main(string[] args)
        {
            // Disabling Boot Camp's machine-wide startup entry means writing to HKLM, which
            // this program cannot do unelevated, so the running copy starts an elevated one
            // of these to do it. That copy is not the hider: it writes one value and leaves,
            // before the single-instance guard would send it away.
            if (BootCamp.IsElevatedRequest(args))
            {
                return BootCamp.RunElevatedWrite(args);
            }

            // Cancelling a beacon only ever means telling the copy that is running it. If
            // there is no such copy there is nothing flashing, which is the asked-for state.
            if (HasSwitch(args, FlashRequest.StopSwitch))
            {
                SingleInstance.TrySignalFlash(null);
                return ExitOk;
            }

            FlashRequest openingFlash = null;

            if (IsFlashResident(args))
            {
                if (!FlashRequest.TryParseWire(args[1], out openingFlash))
                {
                    return ExitBadArguments;
                }
            }
            else if (HasSwitch(args, FlashRequest.FlashSwitch))
            {
                return RequestFlash(args);
            }

            bool background = HasSwitch(args, BackgroundSwitch);

            using (SingleInstance instance = new SingleInstance())
            {
                if (!instance.TryAcquire())
                {
                    // Already running. Launching the program by hand is how you get back to
                    // the settings of a copy that has no window, so that is what a second
                    // launch means -- unless it came from the autostart entry, which should
                    // find the hider already up and quietly stand down.
                    if (openingFlash != null)
                    {
                        // A copy started up in the moment between this one being launched to
                        // do the flashing and it getting here. Hand the request over instead.
                        SingleInstance.TrySignalFlash(openingFlash);
                    }
                    else if (!background)
                    {
                        SingleInstance.SignalRunningInstance();
                    }
                    return ExitOk;
                }

                if (background && !Settings.Enabled)
                {
                    // Autostart is on but the hider is off. Nothing to do and no window to
                    // show, so do not sit in memory pretending otherwise.
                    return ExitOk;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // WindowsFormsSynchronizationContext.Current is what the single-instance
                // listener thread posts the "show settings" request to, so install it before
                // the context is constructed.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                using (HiderContext context = new HiderContext(instance, !background, openingFlash))
                {
                    Application.Run(context);
                }
            }
            return ExitOk;
        }

        /// <summary>
        /// Reads a --flash command line and gets somebody flashing. Never flashes here: this
        /// process exists for a few milliseconds, and a beacon has to outlive it.
        /// </summary>
        private static int RequestFlash(string[] args)
        {
            FlashRequest request;
            string error;
            if (!FlashRequest.TryParseCommandLine(args, out request, out error))
            {
                WriteToCaller(error);
                return ExitBadArguments;
            }

            if (SingleInstance.TrySignalFlash(request))
            {
                // Handed to the resident copy, which is the whole point of the feature: it
                // already has the SMC device open and it is going to be there in an hour.
                // Returning now rather than waiting matters, because BackAtKeyboard has no
                // end until a human turns up and whatever ran this must not be held open
                // until then.
                return ExitOk;
            }

            return StartFlashResident(request);
        }

        /// <summary>
        /// Nobody was home, so put somebody there. The new copy shows its settings window,
        /// which is deliberate: this program has no tray icon, and that window is the only
        /// way the user finds out that a launch just left something running.
        /// </summary>
        private static int StartFlashResident(FlashRequest request)
        {
            ProcessStartInfo start = new ProcessStartInfo(AutoStart.ExecutablePath);
            start.Arguments = FlashResidentSwitch + " \"" + request.ToWire() + "\"";

            // Through the shell, which starts the copy with no inherited handles. That is the
            // point rather than an incidental: started the other way the new copy keeps the
            // caller's stdout and stderr open for as long as it lives, and a caller waiting
            // to read them hangs on a program that is now meant to sit there for an hour --
            // which is exactly the agent this feature exists for.
            start.UseShellExecute = true;

            try
            {
                Process started = Process.Start(start);
                if (started == null)
                {
                    WriteToCaller("Could not start a copy to flash with.");
                    return ExitCouldNotStart;
                }
                // Not waited on -- it is the resident copy now, and the beacon outlives this
                // process by design.
                started.Dispose();
                return ExitOk;
            }
            catch (Exception ex)
            {
                WriteToCaller("Could not start a copy to flash with: " + ex.Message);
                return ExitCouldNotStart;
            }
        }

        /// <summary>
        /// Whether these arguments are the flashing copy's rather than something a user
        /// typed. Matched on args[0] and an exact count, the same shape as
        /// BootCamp.IsElevatedRequest, so a stray --flash-resident cannot be mistaken for one.
        /// </summary>
        private static bool IsFlashResident(string[] args)
        {
            return args.Length == 2
                && string.Equals(args[0], FlashResidentSwitch, StringComparison.Ordinal);
        }

        /// <summary>
        /// Says something back to whoever ran the command. A WinExe has no console, so one
        /// has to be borrowed from the caller -- and .NET has already bound Console.Error to
        /// a writer that goes nowhere, so it is rebound over the handle that borrowing gets.
        /// Launched from Explorer or the Run key there is no console to borrow and the exit
        /// code has to speak for itself.
        /// </summary>
        private static void WriteToCaller(string message)
        {
            if (!_consoleTried)
            {
                _consoleTried = true;
                if (NativeMethods.AttachParentConsole())
                {
                    try
                    {
                        StreamWriter writer = new StreamWriter(Console.OpenStandardError());
                        writer.AutoFlush = true;
                        _callerConsole = writer;
                    }
                    catch (Exception)
                    {
                        // Nothing to write to after all. The exit code still carries.
                    }
                }
            }

            if (_callerConsole == null)
            {
                return;
            }

            try
            {
                _callerConsole.WriteLine(message);
            }
            catch (Exception)
            {
                // The caller's console went away mid-message. Not worth a second failure.
            }
        }

        private static bool HasSwitch(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
