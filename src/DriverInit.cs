using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The part of Apple's tray utility that is not hotkeys at all: pushing the saved Boot
    /// Camp settings into the trackpad and keyboard drivers, once, at logon.
    ///
    /// This is what an earlier version of this program missed. Booting with Bootcamp.exe
    /// suppressed left the trackpad with no tap-to-click, no secondary tap and no scrolling,
    /// and F1/F2 doing nothing to the display brightness. Running Apple's utility once fixed
    /// both -- and *killing it again did not break them*, which is the whole clue: nothing
    /// has to stay resident, something just has to be said to the drivers once.
    ///
    /// What is said was read off Bootcamp.exe under x64dbg, with a non-breaking logging
    /// breakpoint on DeviceIoControl. In order, it:
    ///
    ///     reads  HKCU\Software\Apple Inc.\Trackpad\Mode
    ///     sends  it to \\.\AppleTrackpad as IOCTL_TRACKPAD_SET_MODE (4-byte DWORD)
    ///     reads  HKCU\Software\Apple Inc.\Apple Keyboard Support\OSXFnBehavior
    ///     sends  it to \\.\AppleKeyboard as IOCTL_KEYBOARD_SET_OSX_FN_BEHAVIOR
    ///     asks   \\.\MacHALDriver for its ACPI brightness information
    ///     sends  1 to \\.\AppleKeyboard as IOCTL_ACPI_BRIGHTNESS_AVAILABLE
    ///
    /// and that last line is what gives F1 and F2 back. A registry snapshot taken either
    /// side of that run came back byte-identical -- no service started, no value written --
    /// so those four calls really are the entire difference.
    ///
    /// Bootcamp.exe tries a dozen device names to cover every Mac it ships on. Only four
    /// exist on a MacBookAir6,1 (AppleTrackpad, AppleKeyboard, KeyManager, MacHALDriver),
    /// so a device that will not open is ordinary and is not reported as a failure; a
    /// device that opens and then refuses an IOCTL is a real fault and is.
    /// </summary>
    internal static class DriverInit
    {
        private const string TrackpadKeyPath = @"Software\Apple Inc.\Trackpad";
        private const string TrackpadModeValue = "Mode";

        private const string KeyboardKeyPath = @"Software\Apple Inc.\Apple Keyboard Support";
        private const string FnBehaviorValue = "OSXFnBehavior";

        /// <summary>
        /// What OSXFnBehavior means when nothing has ever written it: 1, the state the Boot
        /// Camp control panel shows as "Use all F1, F2, etc. keys as standard function keys"
        /// unticked -- the Mac behaviour, and the reason F1/F2 are brightness at all.
        /// </summary>
        private const int DefaultFnBehavior = 1;

        /// <summary>
        /// The value Bootcamp.exe hands to IOCTL_ACPI_BRIGHTNESS_AVAILABLE. Its own is the
        /// result of the MacHALDriver query below; on this hardware that came back as one,
        /// and the query's 60-byte reply is not documented anywhere, so the reply is used
        /// as a yes/no rather than decoded.
        /// </summary>
        private const int AcpiBrightnessAvailable = 1;

        /// <summary>
        /// Pushes every setting Boot Camp would push. Safe to call as often as you like:
        /// each IOCTL sets driver state outright rather than toggling it, which is what
        /// makes re-running this after a resume or a device arrival the right thing to do.
        /// </summary>
        /// <returns>A sentence for the settings window describing what happened.</returns>
        public static string Apply()
        {
            List<string> applied = new List<string>();
            List<string> failed = new List<string>();

            ApplyTrackpad(applied, failed);
            ApplyKeyboard(applied, failed);

            return Describe(applied, failed);
        }

        /// <summary>
        /// Tap to click, secondary tap and scrolling. All three live in the one Mode word
        /// -- 41 on a machine with tap-to-click and secondary tap ticked -- and until the
        /// driver is told it, none of them work.
        /// </summary>
        private static void ApplyTrackpad(List<string> applied, List<string> failed)
        {
            int mode;
            if (!TryReadDword(TrackpadKeyPath, TrackpadModeValue, out mode))
            {
                // Nothing has ever set a trackpad mode on this machine, so there is no
                // value to restore and no sensible one to invent. Guessing here would mean
                // silently turning behaviour on that the user never asked for.
                failed.Add("no saved trackpad mode");
                return;
            }

            IntPtr trackpad = NativeMethods.OpenDevice(@"\\.\AppleTrackpad");
            if (trackpad == NativeMethods.InvalidHandleValue)
            {
                failed.Add("no trackpad device");
                return;
            }

            try
            {
                if (NativeMethods.SendSetting(trackpad, NativeMethods.IoctlTrackpadSetMode, mode))
                {
                    applied.Add("trackpad");
                }
                else
                {
                    failed.Add("the trackpad refused its mode");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(trackpad);
            }
        }

        /// <summary>
        /// The two things the keyboard driver has to be told: whether the top row is Mac
        /// keys or plain function keys, and that there is an ACPI brightness control behind
        /// F1 and F2 for it to drive.
        /// </summary>
        private static void ApplyKeyboard(List<string> applied, List<string> failed)
        {
            IntPtr keyboard = NativeMethods.OpenDevice(@"\\.\AppleKeyboard");
            if (keyboard == NativeMethods.InvalidHandleValue)
            {
                failed.Add("no keyboard device");
                return;
            }

            try
            {
                int fnBehavior;
                if (!TryReadDword(KeyboardKeyPath, FnBehaviorValue, out fnBehavior))
                {
                    fnBehavior = DefaultFnBehavior;
                }

                if (NativeMethods.SendSetting(keyboard,
                        NativeMethods.IoctlKeyboardSetOsxFnBehavior, fnBehavior))
                {
                    applied.Add("function keys");
                }
                else
                {
                    failed.Add("the keyboard refused the Fn setting");
                }

                if (!HasAcpiBrightness())
                {
                    // No panel to dim. Saying otherwise would leave F1/F2 handing keypresses
                    // to a control that is not there.
                    failed.Add("no ACPI brightness control");
                    return;
                }

                if (NativeMethods.SendSetting(keyboard,
                        NativeMethods.IoctlKeyboardAcpiBrightnessAvailable,
                        AcpiBrightnessAvailable))
                {
                    applied.Add("F1/F2 brightness");
                }
                else
                {
                    failed.Add("the keyboard refused the brightness setting");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(keyboard);
            }
        }

        /// <summary>
        /// Whether the SMC will talk about display brightness. Bootcamp.exe asks this
        /// immediately before enabling F1/F2 and does not appear to look at the answer
        /// beyond whether the call worked, so neither does this.
        /// </summary>
        private static bool HasAcpiBrightness()
        {
            IntPtr macHal = NativeMethods.OpenDevice(@"\\.\MacHALDriver");
            if (macHal == NativeMethods.InvalidHandleValue)
            {
                return false;
            }

            try
            {
                byte[] info = new byte[NativeMethods.AcpiBrightnessInfoSize];
                return NativeMethods.Query(macHal,
                    NativeMethods.IoctlSmcAcpiBrightnessInfo, info);
            }
            finally
            {
                NativeMethods.CloseHandle(macHal);
            }
        }

        private static bool TryReadDword(string keyPath, string valueName, out int value)
        {
            value = 0;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    object stored = key.GetValue(valueName);
                    if (stored == null)
                    {
                        return false;
                    }
                    value = Convert.ToInt32(stored, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch (Exception)
            {
                // Something else wrote a string or a blob where a number belongs. Treat it
                // as unset rather than as a reason to stop initialising the other devices.
                return false;
            }
        }

        private static string Describe(List<string> applied, List<string> failed)
        {
            if (applied.Count == 0)
            {
                return "No Boot Camp driver settings could be applied (" +
                       string.Join(", ", failed.ToArray()) + ").";
            }

            string sentence = "Applied Boot Camp's " + Join(applied) + " settings.";
            if (failed.Count > 0)
            {
                sentence += " Skipped: " + string.Join(", ", failed.ToArray()) + ".";
            }
            return sentence;
        }

        /// <summary>Joins with commas and a final "and", the way a person would write it.</summary>
        private static string Join(List<string> parts)
        {
            if (parts.Count == 1)
            {
                return parts[0];
            }

            string[] leading = parts.GetRange(0, parts.Count - 1).ToArray();
            return string.Join(", ", leading) + " and " + parts[parts.Count - 1];
        }
    }
}
