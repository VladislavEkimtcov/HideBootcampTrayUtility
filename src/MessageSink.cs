using System;
using System.Windows.Forms;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// A window that exists only to be sent messages, because a program with no window has
    /// nowhere for Windows to tell it that the trackpad came back.
    ///
    /// Two things undo what DriverInit did: the HID stack re-enumerating the trackpad or
    /// keyboard, and waking from sleep. Bootcamp.exe watches for both -- it imports
    /// RegisterDeviceNotification and RegisterPowerSettingNotification, and its own
    /// disassembly still carries the symbol CMainFrame::OnPowerBroadcast -- so this does
    /// too, and asks DriverInit to run again.
    ///
    /// It is a hidden top-level window rather than a message-only one on purpose:
    /// WM_POWERBROADCAST is a broadcast, and broadcasts do not reach HWND_MESSAGE windows.
    /// Nothing ever shows it, so it costs a handle and no pixels.
    ///
    /// Device arrivals come in bursts -- one HID interface at a time as a device
    /// enumerates -- so they are collapsed by a short timer instead of re-initialising
    /// once per message.
    /// </summary>
    internal sealed class MessageSink : NativeWindow, IDisposable
    {
        /// <summary>
        /// Long enough for a trackpad's several HID interfaces to finish arriving, short
        /// enough that nobody notices the gap before tap-to-click works again.
        /// </summary>
        private const int SettleMilliseconds = 1500;

        private readonly Action _reinitialise;
        private readonly Timer _settle;

        private IntPtr _notification = IntPtr.Zero;

        public MessageSink(Action reinitialise)
        {
            _reinitialise = reinitialise;

            _settle = new Timer();
            _settle.Interval = SettleMilliseconds;
            _settle.Tick += OnSettled;

            CreateParams parameters = new CreateParams();
            parameters.Caption = "HideBootcampTrayUtility";
            // No WS_VISIBLE and a zero-sized rectangle: created, never shown.
            parameters.Style = 0;
            parameters.ExStyle = 0;
            parameters.X = 0;
            parameters.Y = 0;
            parameters.Width = 0;
            parameters.Height = 0;
            CreateHandle(parameters);

            _notification = NativeMethods.RegisterHidNotification(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WmDeviceChange)
            {
                int change = m.WParam.ToInt32();
                if (change == NativeMethods.DbtDeviceArrival ||
                    change == NativeMethods.DbtDeviceRemoveComplete)
                {
                    Schedule();
                }
            }
            else if (m.Msg == NativeMethods.WmPowerBroadcast)
            {
                int evt = m.WParam.ToInt32();
                if (evt == NativeMethods.PbtApmResumeAutomatic ||
                    evt == NativeMethods.PbtApmResumeSuspend)
                {
                    Schedule();
                }
            }

            base.WndProc(ref m);
        }

        /// <summary>Restarts the settle timer, so a burst of messages produces one call.</summary>
        private void Schedule()
        {
            _settle.Stop();
            _settle.Start();
        }

        private void OnSettled(object sender, EventArgs e)
        {
            _settle.Stop();
            try
            {
                _reinitialise();
            }
            catch (Exception)
            {
                // This runs on the message loop with no window to report to, and the next
                // arrival or resume will try again. Taking the process down over it would
                // cost the user their hotkeys as well.
            }
        }

        public void Dispose()
        {
            _settle.Stop();
            _settle.Tick -= OnSettled;
            _settle.Dispose();

            NativeMethods.UnregisterNotification(_notification);
            _notification = IntPtr.Zero;

            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }
}
