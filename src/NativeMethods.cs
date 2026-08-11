using System;
using System.Runtime.InteropServices;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The P/Invoke surface needed to talk to Apple's two Boot Camp drivers, recovered by
    /// disassembling Bootcamp.exe and proven in the HideBootcampTrayUtility.ps1 prototype.
    ///
    /// Two things are unusual here and are deliberate:
    ///
    ///   * DeviceIoControl is declared twice. \\.\KeyManager registers a hotkey by taking
    ///     an event HANDLE as the raw Type3InputBuffer with nInBufferSize = 0
    ///     (METHOD_NEITHER), so that overload passes an IntPtr; the SMC write to
    ///     \\.\MacHALDriver passes a real byte buffer instead.
    ///
    ///   * Raw IntPtr handles rather than SafeHandle. The driver keeps the hotkey event
    ///     handles for the lifetime of the device handle, and they are waited on as a
    ///     single IntPtr[] by WaitForMultipleObjects; HotkeyWorker closes them in a
    ///     documented order at shutdown, which SafeHandle finalisation would not respect.
    /// </summary>
    internal static class NativeMethods
    {
        public static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareReadWrite = 0x00000003;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x00000080;

        /// <summary>WaitForMultipleObjects returned because the timeout elapsed.</summary>
        public const uint WaitTimeout = 0x00000102;
        public const uint Infinite = 0xFFFFFFFF;

        /// <summary>The MacHALDriver IOCTL that writes an SMC key.</summary>
        public const uint IoctlSmcWrite = 0x9C402458;

        // The four control codes below were read straight off Bootcamp.exe under x64dbg,
        // with a logging breakpoint on DeviceIoControl. Together they are the whole of
        // what the tray utility does to the trackpad and to F1/F2 -- see DriverInit.
        //
        // Each one decodes as a plain CTL_CODE with METHOD_BUFFERED and FILE_ANY_ACCESS;
        // the device type is what tells them apart.

        /// <summary>
        /// \\.\AppleTrackpad: CTL_CODE(FILE_DEVICE_MOUSE, 0x801, ...). Takes a four-byte
        /// mode word. Bootcamp.exe reads Software\Apple Inc.\Trackpad\Mode and sends it
        /// here; its own error text calls this IOCTL_TRACKPAD_SET_MODE.
        /// </summary>
        public const uint IoctlTrackpadSetMode = 0x000F2004;

        /// <summary>
        /// \\.\AppleKeyboard: device type 0xB403, function 0x807. Takes a four-byte
        /// boolean, the OSXFnBehavior setting. IOCTL_KEYBOARD_SET_OSX_FN_BEHAVIOR.
        /// </summary>
        public const uint IoctlKeyboardSetOsxFnBehavior = 0xB403201C;

        /// <summary>
        /// \\.\AppleKeyboard: device type 0xB403, function 0x812. Takes a four-byte
        /// boolean. IOCTL_ACPI_BRIGHTNESS_AVAILABLE -- this is the one that makes F1 and
        /// F2 change the display brightness.
        /// </summary>
        public const uint IoctlKeyboardAcpiBrightnessAvailable = 0xB4032048;

        /// <summary>
        /// \\.\MacHALDriver: device type 0x9C40, function 0x921. No input; fills a 60-byte
        /// output buffer. Bootcamp.exe asks this first and only then tells the keyboard
        /// driver that ACPI brightness is available, so it is the availability test.
        /// </summary>
        public const uint IoctlSmcAcpiBrightnessInfo = 0x9C402484;

        /// <summary>The buffer size Bootcamp.exe passes to IoctlSmcAcpiBrightnessInfo.</summary>
        public const int AcpiBrightnessInfoSize = 60;

        public const byte VkVolumeMute = 0xAD;
        public const byte VkVolumeDown = 0xAE;
        public const byte VkVolumeUp = 0xAF;

        private const uint KeyEventKeyUp = 0x0002;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
        private static extern bool DeviceIoControlPtr(IntPtr device, uint controlCode,
            IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
        private static extern bool DeviceIoControlBuf(IntPtr device, uint controlCode,
            byte[] inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
        private static extern bool DeviceIoControlOut(IntPtr device, uint controlCode,
            IntPtr inBuffer, uint inBufferSize, byte[] outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEventW(IntPtr securityAttributes,
            bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles,
            bool waitAll, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags,
            UIntPtr extraInfo);

        /// <summary>
        /// Opens one of the Apple device objects for read/write. Both open for an ordinary
        /// user on a Boot Camp install, which is why this program never needs elevation to
        /// do its actual job.
        /// </summary>
        /// <returns>The device handle, or InvalidHandleValue on failure.</returns>
        public static IntPtr OpenDevice(string path)
        {
            return CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        }

        /// <summary>
        /// Hands \\.\KeyManager an auto-reset event that it signals when the given hotkey
        /// is pressed. The handle travels as the Type3InputBuffer itself with a length of
        /// zero -- that is the inverted-call convention Bootcamp.exe uses.
        /// </summary>
        public static bool RegisterHotkey(IntPtr keyManager, uint hotkeyCode, IntPtr eventHandle)
        {
            uint returned;
            return DeviceIoControlPtr(keyManager, hotkeyCode, eventHandle, 0, IntPtr.Zero, 0,
                out returned, IntPtr.Zero);
        }

        /// <summary>
        /// Writes the keyboard-backlight SMC key "LKSB" through \\.\MacHALDriver.
        /// The hardware level is 0..4095 and is sent shifted left by four, big-endian.
        /// LKSB is write-only, so nothing can read the level back -- HotkeyWorker tracks it.
        /// </summary>
        public static bool SetBacklight(IntPtr macHalDriver, int level)
        {
            ushort raw = (ushort)((level << 4) & 0xFFFF);
            byte[] buffer = new byte[7]
            {
                (byte)'L', (byte)'K', (byte)'S', (byte)'B', 0,
                (byte)(raw >> 8), (byte)(raw & 0xFF)
            };
            uint returned;
            return DeviceIoControlBuf(macHalDriver, IoctlSmcWrite, buffer, 7, IntPtr.Zero, 0,
                out returned, IntPtr.Zero);
        }

        /// <summary>
        /// Sends one four-byte setting to a driver and nothing back. Every one of the
        /// trackpad and keyboard initialisation IOCTLs has this shape: Bootcamp.exe reads a
        /// DWORD out of the registry and hands it over with no output buffer at all.
        /// </summary>
        public static bool SendSetting(IntPtr device, uint controlCode, int value)
        {
            byte[] buffer = BitConverter.GetBytes(value);
            uint returned;
            return DeviceIoControlBuf(device, controlCode, buffer, (uint)buffer.Length,
                IntPtr.Zero, 0, out returned, IntPtr.Zero);
        }

        /// <summary>
        /// Asks a driver a question with no input: the shape of MacHALDriver's ACPI
        /// brightness query. The reply is not decoded anywhere -- only whether the call
        /// succeeded, which is how Bootcamp.exe itself uses it.
        /// </summary>
        public static bool Query(IntPtr device, uint controlCode, byte[] outBuffer)
        {
            uint returned;
            return DeviceIoControlOut(device, controlCode, IntPtr.Zero, 0, outBuffer,
                (uint)outBuffer.Length, out returned, IntPtr.Zero);
        }

        /// <summary>
        /// Presses and releases a virtual key. The volume keys are handed straight to
        /// Windows this way, which is what raises the familiar volume overlay -- Boot Camp
        /// does the same rather than driving the mixer itself.
        /// </summary>
        public static void TapKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        // ---- How long the machine has been left alone -------------------------------
        //
        // Bootcamp.exe imports GetLastInputInfo and SetTimer and no input hook, so its
        // "turn the backlight off after N minutes" is a poll rather than anything that
        // watches keystrokes. BacklightIdle does the same.

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        /// <summary>
        /// Milliseconds since the last keyboard or mouse input, or zero if Windows will not
        /// say -- which reads as "someone is here", the safe answer for a caller deciding
        /// whether to switch the backlight off.
        /// </summary>
        public static int IdleMilliseconds()
        {
            LastInputInfo info = new LastInputInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(LastInputInfo));
            if (!GetLastInputInfo(ref info))
            {
                return 0;
            }

            // Both are tick counts that wrap every 49.7 days. Subtracting them unchecked
            // and taking the low 32 bits is correct across the wrap; comparing them is not.
            unchecked
            {
                int elapsed = (int)((uint)Environment.TickCount - info.Time);
                return elapsed < 0 ? 0 : elapsed;
            }
        }

        // ---- Being told when to do it all again -------------------------------------

        public const int WmDeviceChange = 0x0219;
        public const int WmPowerBroadcast = 0x0218;

        public const int DbtDeviceArrival = 0x8000;
        public const int DbtDeviceRemoveComplete = 0x8004;

        public const int PbtApmResumeSuspend = 0x0007;
        public const int PbtApmResumeAutomatic = 0x0012;

        private const int DbtDevTypeDeviceInterface = 5;
        private const int DeviceNotifyWindowHandle = 0;

        /// <summary>
        /// GUID_DEVINTERFACE_HID. The trackpad and the keyboard both arrive as HID
        /// interfaces -- the same GUID appears in Bootcamp.exe's own device paths.
        /// </summary>
        private static readonly Guid HidDeviceInterface =
            new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        [StructLayout(LayoutKind.Sequential)]
        private struct DevBroadcastDeviceInterface
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public Guid ClassGuid;
            public short Name;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient,
            IntPtr filter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        /// <summary>
        /// Asks for WM_DEVICECHANGE whenever a HID device comes or goes, so the driver
        /// settings can be pushed again after the trackpad is re-enumerated.
        /// </summary>
        /// <returns>The registration handle, or IntPtr.Zero if it could not be made.</returns>
        public static IntPtr RegisterHidNotification(IntPtr window)
        {
            DevBroadcastDeviceInterface filter = new DevBroadcastDeviceInterface();
            filter.Size = Marshal.SizeOf(typeof(DevBroadcastDeviceInterface));
            filter.DeviceType = DbtDevTypeDeviceInterface;
            filter.ClassGuid = HidDeviceInterface;

            IntPtr buffer = Marshal.AllocHGlobal(filter.Size);
            try
            {
                Marshal.StructureToPtr(filter, buffer, false);
                return RegisterDeviceNotificationW(window, buffer, DeviceNotifyWindowHandle);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static void UnregisterNotification(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                UnregisterDeviceNotification(handle);
            }
        }

        // ---- Reaching the real System32 from a 32-bit process ------------------------
        //
        // MSBuild builds an AnyCPU executable as Prefer32Bit, so this program runs under
        // WOW64 and everything it asks for in System32 is answered from SysWOW64 instead.
        // Apple's control panel is installed only in the real System32, so the two calls
        // that go looking for it have to switch the redirector off first. See BootCamp.

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64DisableWow64FsRedirection(out IntPtr oldValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);

        /// <summary>
        /// Turns the WOW64 file-system redirector off for this thread until it is disposed.
        ///
        /// It has to be a narrow scope. While it is open a 32-bit process that loads a DLL
        /// out of System32 by name gets the 64-bit copy and fails, so nothing but the one
        /// call being protected belongs inside it. The revert is per-thread, which is why
        /// the whole thing is confined to the UI thread.
        ///
        /// On 64-bit Windows the calls below both succeed; on 32-bit Windows they fail and
        /// there is nothing to undo, which is what Wow64Redirection.Off leaves behind.
        /// </summary>
        public sealed class Wow64Redirection : IDisposable
        {
            private IntPtr _oldValue;
            private bool _disabled;

            private Wow64Redirection()
            {
                _disabled = Wow64DisableWow64FsRedirection(out _oldValue);
            }

            public static Wow64Redirection Off()
            {
                return new Wow64Redirection();
            }

            public void Dispose()
            {
                if (_disabled)
                {
                    _disabled = false;
                    Wow64RevertWow64FsRedirection(_oldValue);
                }
            }
        }
    }
}
