using System;
using System.Globalization;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// A flash asked for by another launch. A null Request means "--flash-stop": the caller
    /// wants whatever is flashing to end and the light to go back.
    /// </summary>
    internal sealed class FlashRequestedEventArgs : EventArgs
    {
        private readonly FlashRequest _request;

        public FlashRequestedEventArgs(FlashRequest request)
        {
            _request = request;
        }

        public FlashRequest Request
        {
            get { return _request; }
        }
    }

    /// <summary>How the light moves between the two ends of its range.</summary>
    internal enum FlashMode
    {
        /// <summary>A triangle wave: dark up to full and back, smoothly.</summary>
        Sweep,

        /// <summary>Full for a moment, dark for longer. A beacon rather than a fade.</summary>
        Strobe
    }

    /// <summary>
    /// What one "--flash" invocation asked for, plus the two translations it needs: from the
    /// command line a person or an agent typed, and to and from the single line of text that
    /// carries the request between processes.
    ///
    /// The whole point of the feature is that this program is already resident with the SMC
    /// device open, so a second launch does not flash anything itself -- it parses the
    /// arguments here, hands the result to the copy that is already running, and leaves.
    /// </summary>
    internal sealed class FlashRequest
    {
        public const string FlashSwitch = "--flash";
        public const string StopSwitch = "--flash-stop";
        public const string DeviceSwitch = "--flash-device";
        public const string DurationSwitch = "--duration";
        public const string ModeSwitch = "--flash-mode";

        /// <summary>
        /// The one device this build can flash. The utility never sets display brightness --
        /// it only tells Apple's keyboard driver that ACPI brightness exists so that F1 and
        /// F2 work, which is a different thing -- so "screen" and "both" are rejected rather
        /// than quietly downgraded to a keyboard flash.
        /// </summary>
        public const string KeyboardDevice = "keyboard";

        /// <summary>The value of --duration that means "until somebody touches the machine".</summary>
        public const string BackAtKeyboard = "BackAtKeyboard";

        private readonly FlashMode _mode;
        private readonly int _durationSeconds;

        public FlashRequest(FlashMode mode, int durationSeconds)
        {
            _mode = mode;
            _durationSeconds = durationSeconds;
        }

        public FlashMode Mode
        {
            get { return _mode; }
        }

        /// <summary>How long to flash for, or zero for BackAtKeyboard -- no ceiling at all.</summary>
        public int DurationSeconds
        {
            get { return _durationSeconds; }
        }

        /// <summary>
        /// The defaults, which are also what a bare "--flash" means: sweep gently until the
        /// human is back. That is the shape an agent wants at the end of a run, because it
        /// costs nothing to leave running and stops itself the moment it has worked.
        /// </summary>
        public static FlashRequest Default
        {
            get { return new FlashRequest(FlashMode.Sweep, 0); }
        }

        /// <summary>
        /// Reads the flash arguments. Every value is matched case-insensitively, because the
        /// switches are meant to be typed from memory by something that was told about them
        /// once.
        /// </summary>
        /// <returns>False with a sentence for the user in <paramref name="error"/>.</returns>
        public static bool TryParseCommandLine(string[] args, out FlashRequest request,
            out string error)
        {
            request = null;
            error = null;

            FlashMode mode = FlashMode.Sweep;
            int durationSeconds = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];

                if (Matches(argument, FlashSwitch))
                {
                    continue;
                }

                if (Matches(argument, DeviceSwitch))
                {
                    string value;
                    if (!TryTakeValue(args, ref i, DeviceSwitch, out value, out error))
                    {
                        return false;
                    }
                    if (!Matches(value, KeyboardDevice))
                    {
                        error = "This build flashes the keyboard backlight only; \"" + value +
                            "\" is not something it can drive. Pass " + DeviceSwitch + " " +
                            KeyboardDevice + ", or leave the switch off.";
                        return false;
                    }
                    continue;
                }

                if (Matches(argument, DurationSwitch))
                {
                    string value;
                    if (!TryTakeValue(args, ref i, DurationSwitch, out value, out error))
                    {
                        return false;
                    }
                    if (!TryParseDuration(value, out durationSeconds, out error))
                    {
                        return false;
                    }
                    continue;
                }

                if (Matches(argument, ModeSwitch))
                {
                    string value;
                    if (!TryTakeValue(args, ref i, ModeSwitch, out value, out error))
                    {
                        return false;
                    }
                    if (!TryParseMode(value, out mode, out error))
                    {
                        return false;
                    }
                    continue;
                }

                error = "Unrecognised argument \"" + argument + "\". Expected " + DeviceSwitch +
                    ", " + DurationSwitch + " or " + ModeSwitch + ".";
                return false;
            }

            request = new FlashRequest(mode, durationSeconds);
            return true;
        }

        private static bool TryParseDuration(string value, out int seconds, out string error)
        {
            seconds = 0;
            error = null;

            if (Matches(value, BackAtKeyboard))
            {
                return true;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                || seconds < 0)
            {
                error = "\"" + value + "\" is not a duration. Pass a whole number of seconds, or " +
                    BackAtKeyboard + " to flash until somebody touches the machine.";
                return false;
            }

            // Zero is how BackAtKeyboard travels internally, so "--duration 0" -- which can
            // only be a mistake anyway -- must not silently become "forever".
            if (seconds == 0)
            {
                error = "A duration of zero would flash for no time at all. Pass at least 1, or " +
                    BackAtKeyboard + ".";
                return false;
            }

            return true;
        }

        private static bool TryParseMode(string value, out FlashMode mode, out string error)
        {
            error = null;
            if (Matches(value, "sweep"))
            {
                mode = FlashMode.Sweep;
                return true;
            }
            if (Matches(value, "strobe"))
            {
                mode = FlashMode.Strobe;
                return true;
            }
            mode = FlashMode.Sweep;
            error = "\"" + value + "\" is not a flash mode. Pass sweep or strobe.";
            return false;
        }

        private static bool TryTakeValue(string[] args, ref int index, string name,
            out string value, out string error)
        {
            error = null;
            if (index + 1 >= args.Length)
            {
                value = null;
                error = name + " needs a value after it.";
                return false;
            }
            index++;
            value = args[index];
            return true;
        }

        private static bool Matches(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The request as one line, for the registry slot the running copy reads. Kept
        /// deliberately readable: the value is worth being able to look at with regedit when
        /// a flash does not happen.
        /// </summary>
        public string ToWire()
        {
            return KeyboardDevice + "|" +
                (_durationSeconds > 0
                    ? _durationSeconds.ToString(CultureInfo.InvariantCulture)
                    : BackAtKeyboard) + "|" +
                (_mode == FlashMode.Strobe ? "strobe" : "sweep");
        }

        /// <summary>
        /// Reads back what ToWire wrote. Anything unreadable is refused rather than defaulted:
        /// a garbled slot means some other program is writing there, and starting an endless
        /// flash on the strength of that would be hard for the user to explain.
        /// </summary>
        public static bool TryParseWire(string wire, out FlashRequest request)
        {
            request = null;
            if (string.IsNullOrEmpty(wire))
            {
                return false;
            }

            string[] fields = wire.Split('|');
            if (fields.Length != 3 || !Matches(fields[0], KeyboardDevice))
            {
                return false;
            }

            int durationSeconds;
            string ignored;
            if (!TryParseDuration(fields[1], out durationSeconds, out ignored))
            {
                return false;
            }

            FlashMode mode;
            if (!TryParseMode(fields[2], out mode, out ignored))
            {
                return false;
            }

            request = new FlashRequest(mode, durationSeconds);
            return true;
        }
    }
}
