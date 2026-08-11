using System;
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

            bool background = HasSwitch(args, BackgroundSwitch);

            using (SingleInstance instance = new SingleInstance())
            {
                if (!instance.TryAcquire())
                {
                    // Already running. Launching the program by hand is how you get back to
                    // the settings of a copy that has no window, so that is what a second
                    // launch means -- unless it came from the autostart entry, which should
                    // find the hider already up and quietly stand down.
                    if (!background)
                    {
                        SingleInstance.SignalRunningInstance();
                    }
                    return 0;
                }

                if (background && !Settings.Enabled)
                {
                    // Autostart is on but the hider is off. Nothing to do and no window to
                    // show, so do not sit in memory pretending otherwise.
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // WindowsFormsSynchronizationContext.Current is what the single-instance
                // listener thread posts the "show settings" request to, so install it before
                // the context is constructed.
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

                using (HiderContext context = new HiderContext(instance, !background))
                {
                    Application.Run(context);
                }
            }
            return 0;
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
