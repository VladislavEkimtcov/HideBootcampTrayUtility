using System;
using System.Globalization;
using System.Threading;
using Microsoft.Win32;

namespace HideBootcampTrayUtility
{
    /// <summary>
    /// The keyboard backlight: one SMC key, one remembered level, and the three things that
    /// move it -- F5/F6, the machine being left alone, and the --flash beacon.
    ///
    /// The beacon is the odd one out and gets its own flag. The other two settle the light
    /// somewhere and leave it; a flash borrows the light, writes it dozens of times a second
    /// without remembering any of it, and must hand back exactly what it was given. So while
    /// _flashing is set, Dim, Restore and Reassert stand down rather than fight it, and only
    /// a hotkey -- which means the person the beacon was calling has arrived -- takes it back.
    ///
    /// This used to live inside HotkeyWorker, where the level was a local of the listener
    /// thread. It cannot stay there now that BacklightIdle also drives the light, so the
    /// level and the device handle live here instead and every entry point takes the lock.
    /// The lock is held across a ramp, which is about a tenth of a second: a hotkey pressed
    /// during someone else's ramp waits rather than interleaving with it, which is the
    /// behaviour that looks right.
    ///
    /// The hardware key is "LKSB", written through \\.\MacHALDriver. Bootcamp.exe also
    /// *reads* it -- IOCTL 0x9C402460, "LKSB\0" in, twelve bytes out -- so the old note in
    /// this project that the key is write-only was wrong. Those twelve bytes are not
    /// decoded here, so the level is still tracked in memory and persisted to the same
    /// registry value Boot Camp uses.
    /// </summary>
    internal sealed class Backlight : IDisposable
    {
        // Boot Camp's own registry home. The level is deliberately shared with it: whoever
        // is driving the keyboard has to remember it, and using the same value means Boot
        // Camp and this program agree about where the light was left.
        private const string KeyPath = @"Software\Apple Inc.\Apple Keyboard Support";
        private const string ValueName = "Light Value";

        /// <summary>Boot Camp's user-facing backlight scale is 0..16, not 0..4095.</summary>
        public const int MaxUiStep = 16;

        /// <summary>The SMC key takes a 12-bit level.</summary>
        public const int MaxHardwareLevel = 4095;

        /// <summary>Fallback when no level has ever been stored: half brightness.</summary>
        private const int DefaultUiStep = 8;

        // Boot Camp ramps the backlight rather than jumping to the new level. Eight steps
        // of 12 ms is close enough to its stepper that the change does not look abrupt,
        // and short enough that a held-down F6 still feels immediate.
        private const int RampSteps = 8;
        private const int RampStepMilliseconds = 12;

        // Going dark because nobody is here is not a keypress, and should not look like
        // one. A slower ramp over about a second reads as the machine settling rather than
        // as the light being switched off.
        private const int FadeSteps = 32;
        private const int FadeStepMilliseconds = 30;

        private readonly object _sync = new object();

        private IntPtr _device = NativeMethods.InvalidHandleValue;
        private bool _deviceTried;
        private int _uiStep;
        private int _currentLevel;
        private bool _dimmed;
        private bool _flashing;

        public Backlight()
        {
            _uiStep = ReadUiStep();
            _currentLevel = ToHardwareLevel(_uiStep);
        }

        /// <summary>Whether the light is currently down because the machine was left alone.</summary>
        public bool IsDimmed
        {
            get { lock (_sync) { return _dimmed; } }
        }

        /// <summary>
        /// Writes the remembered level to the hardware. Worth doing at startup and again
        /// after a resume: nothing can read the level back, so the SMC and the stored value
        /// can disagree until something asserts one of them.
        /// </summary>
        public void Reassert()
        {
            lock (_sync)
            {
                if (_flashing)
                {
                    // A device arriving mid-beacon is not a reason to interrupt it. The level
                    // gets asserted anyway the moment the flash ends and puts the light back.
                    return;
                }
                _dimmed = false;
                _currentLevel = ToHardwareLevel(_uiStep);
                Write(_currentLevel);
            }
        }

        /// <summary>
        /// Moves the level one notch, as F5 and F6 do, and remembers where it got to. A
        /// press while the light is dimmed counts as someone arriving: the dim is dropped
        /// and the step applies to the level they left it at.
        /// </summary>
        public void Step(int delta)
        {
            lock (_sync)
            {
                // F5 or F6 during a beacon means the person it was calling has arrived and
                // is already reaching for the brightness. Take the flash down here rather
                // than waiting for the flasher thread to notice the keypress, so the step
                // below is the last thing to touch the light -- EndFlash sees the flash is
                // over and leaves the level the user just chose alone.
                bool wasFlashing = _flashing;
                _flashing = false;

                int wanted = _uiStep + delta;
                if (wanted < 0)
                {
                    wanted = 0;
                }
                else if (wanted > MaxUiStep)
                {
                    wanted = MaxUiStep;
                }

                // wasFlashing forces the ramp even when the level is not changing: pressing
                // F6 at full brightness during a beacon must still put the light back where
                // the beacon found it, and the ramp below is what does that.
                if (wanted == _uiStep && !_dimmed && !wasFlashing)
                {
                    return;
                }

                _uiStep = wanted;
                _dimmed = false;
                Ramp(ToHardwareLevel(_uiStep), RampSteps, RampStepMilliseconds);
                WriteUiStep(_uiStep);
            }
        }

        /// <summary>
        /// Fades the light out because nobody has touched the machine. The stored level is
        /// deliberately left alone -- it is the level the user chose, not the one the room
        /// is in -- so Restore has something to come back to and Boot Camp still agrees
        /// with us if it ever runs again.
        /// </summary>
        public void Dim()
        {
            lock (_sync)
            {
                // Nobody being here is the whole reason a beacon is running. Fading it out
                // would switch off the one thing still trying to fetch them.
                if (_dimmed || _flashing)
                {
                    return;
                }
                _dimmed = true;
                Ramp(0, FadeSteps, FadeStepMilliseconds);
            }
        }

        /// <summary>Brings the light back to the level it was left at. Does nothing if it never went.</summary>
        public void Restore()
        {
            lock (_sync)
            {
                // While flashing, _dimmed is only a note of where the light has to be put
                // back to. Acting on it now would fight the beacon; EndFlash honours it.
                if (!_dimmed || _flashing)
                {
                    return;
                }
                _dimmed = false;
                Ramp(ToHardwareLevel(_uiStep), RampSteps, RampStepMilliseconds);
            }
        }

        /// <summary>
        /// Takes the light over for a beacon. From here until EndFlash the idle timer and the
        /// device-arrival re-assert stand down, and FlashSet is the only thing writing.
        /// </summary>
        /// <returns>False if a beacon is already running, which the caller should leave alone.</returns>
        public bool TryBeginFlash()
        {
            lock (_sync)
            {
                if (_flashing)
                {
                    return false;
                }
                _flashing = true;
                return true;
            }
        }

        /// <summary>
        /// Puts the light at a raw hardware level for one step of a beacon. Deliberately does
        /// not touch _uiStep or the registry: a flash is something happening *to* the light,
        /// not a level the user chose, and Boot Camp must not come back to find its stored
        /// value halfway through a sweep.
        /// </summary>
        public void FlashSet(int hardwareLevel)
        {
            if (hardwareLevel < 0)
            {
                hardwareLevel = 0;
            }
            else if (hardwareLevel > MaxHardwareLevel)
            {
                hardwareLevel = MaxHardwareLevel;
            }

            lock (_sync)
            {
                if (!_flashing)
                {
                    // Somebody else has taken the light back -- a hotkey, most likely. The
                    // flasher thread will notice on its next pass; until it does, stop
                    // writing over what they did.
                    return;
                }
                Write(hardwareLevel);
                _currentLevel = hardwareLevel;
            }
        }

        /// <summary>
        /// Ends the beacon and gives the light back exactly as it was found -- including
        /// dark, if the idle timer had already faded it out before the beacon started.
        /// Returns quietly if something else has already taken over.
        /// </summary>
        public void EndFlash()
        {
            lock (_sync)
            {
                if (!_flashing)
                {
                    return;
                }
                _flashing = false;
                Ramp(_dimmed ? 0 : ToHardwareLevel(_uiStep), RampSteps, RampStepMilliseconds);
            }
        }

        /// <summary>Walks the hardware level to the target. Callers must already hold _sync.</summary>
        private void Ramp(int target, int steps, int millisecondsPerStep)
        {
            int from = _currentLevel;
            for (int step = 1; step <= steps; step++)
            {
                Write(from + ((target - from) * step / steps));
                Thread.Sleep(millisecondsPerStep);
            }
            // Written once more without the rounding of the interpolation above.
            Write(target);
            _currentLevel = target;
        }

        /// <summary>
        /// Writes the SMC key. The device is opened on first use and kept -- opening it per
        /// keypress would put a CreateFile in the middle of every ramp step.
        /// </summary>
        private void Write(int level)
        {
            if (_device == NativeMethods.InvalidHandleValue && !_deviceTried)
            {
                _deviceTried = true;
                _device = NativeMethods.OpenDevice(@"\\.\MacHALDriver");
            }
            if (_device != NativeMethods.InvalidHandleValue)
            {
                NativeMethods.SetBacklight(_device, level);
            }
        }

        private static int ToHardwareLevel(int uiStep)
        {
            int level = uiStep << 8;
            return level > MaxHardwareLevel ? MaxHardwareLevel : level;
        }

        private static int ReadUiStep()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
                {
                    if (key == null)
                    {
                        return DefaultUiStep;
                    }
                    object value = key.GetValue(ValueName);
                    if (value == null)
                    {
                        return DefaultUiStep;
                    }
                    int step = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    if (step < 0)
                    {
                        return 0;
                    }
                    return step > MaxUiStep ? MaxUiStep : step;
                }
            }
            catch (Exception)
            {
                // Something else wrote a string or a blob here; treat it as unset.
                return DefaultUiStep;
            }
        }

        private static void WriteUiStep(int uiStep)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key != null)
                    {
                        key.SetValue(ValueName, uiStep, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception)
            {
                // Not being able to remember the level is a small loss -- the keys still
                // work for this session -- and nowhere near worth interrupting the user.
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_device != NativeMethods.InvalidHandleValue)
                {
                    NativeMethods.CloseHandle(_device);
                    _device = NativeMethods.InvalidHandleValue;
                }
            }
        }
    }
}
