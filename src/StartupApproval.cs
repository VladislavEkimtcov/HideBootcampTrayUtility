using System;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// Reads and writes the switch behind Task Manager's Startup tab.
    ///
    /// Task Manager does not delete a Run entry when you disable it. It leaves the entry
    /// alone and records the decision under
    /// ...\CurrentVersion\Explorer\StartupApproved\Run, in a value named after the Run
    /// entry: twelve bytes, of which bit 0 of the first byte means "disabled" and the last
    /// eight are the FILETIME of when that happened. Explorer consults it at logon.
    ///
    /// Using the same switch is what makes both of this program's autostart options honest
    /// -- whatever it does here shows up in Task Manager exactly as if a person had
    /// clicked it there, and can be undone from the same place.
    ///
    /// Which hive the value lives in follows the Run entry it describes: HKCU for a
    /// per-user entry (this program's own), HKLM for a machine-wide one (Boot Camp's).
    /// The HKLM half needs administrator rights to write, which is why it is reached
    /// through the elevated helper in BootCamp.
    /// </summary>
    internal static class StartupApproval
    {
        public const string RunSubKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        private const int ValueLength = 12;
        private const byte DisabledBit = 0x01;

        /// <summary>The byte Windows writes for a plain, never-disabled entry.</summary>
        private const byte DefaultEnabledFlags = 0x02;

        /// <summary>
        /// Whether the named Run entry has been switched off. An entry with no approval
        /// value has never been touched in Task Manager, and is therefore enabled.
        /// </summary>
        public static bool IsDisabled(RegistryHive hive, string valueName)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(RunSubKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    byte[] value = key.GetValue(valueName) as byte[];
                    if (value == null || value.Length == 0)
                    {
                        return false;
                    }
                    return (value[0] & DisabledBit) != 0;
                }
            }
            catch (Exception)
            {
                // No read access, or somebody put something else in this value. Either way
                // the safe reading is "not disabled by us".
                return false;
            }
        }

        /// <summary>
        /// Switches the named Run entry on or off.
        ///
        /// Only bit 0 is touched: the rest of the first byte varies between entries
        /// (0x02, 0x04 and 0x06 all appear on a normal machine) and is Windows' business,
        /// not ours, so an existing value is amended rather than replaced.
        /// </summary>
        /// <returns>False if the value could not be written -- almost always because the
        /// hive is HKLM and the caller is not elevated.</returns>
        public static bool TrySetDisabled(RegistryHive hive, string valueName, bool disabled)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (RegistryKey key = root.CreateSubKey(RunSubKeyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    byte[] existing = key.GetValue(valueName) as byte[];
                    byte[] value = new byte[ValueLength];
                    if (existing != null && existing.Length >= ValueLength)
                    {
                        Array.Copy(existing, value, ValueLength);
                    }
                    else
                    {
                        value[0] = DefaultEnabledFlags;
                    }

                    if (disabled)
                    {
                        value[0] |= DisabledBit;
                        // Task Manager stamps the moment of the change here; matching it
                        // keeps the entry indistinguishable from one disabled by hand.
                        WriteFileTime(value, 4, DateTime.UtcNow);
                    }
                    else
                    {
                        value[0] &= unchecked((byte)~DisabledBit);
                        Array.Clear(value, 4, 8);
                    }

                    key.SetValue(valueName, value, RegistryValueKind.Binary);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Removes any approval value for an entry that no longer exists.</summary>
        public static void Remove(RegistryHive hive, string valueName)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                using (RegistryKey key = root.OpenSubKey(RunSubKeyPath, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }
            }
            catch (Exception)
            {
                // Leaving a stale approval value behind is harmless: with no Run entry to
                // describe, nothing ever reads it.
            }
        }

        private static void WriteFileTime(byte[] buffer, int offset, DateTime utc)
        {
            long ticks = utc.ToFileTimeUtc();
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(ticks >> (8 * i));
            }
        }
    }
}
