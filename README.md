# Boot Camp Tray Hider

Replaces Apple's Boot Camp tray utility on a Windows MacBook with a background program
that has no tray icon and no window of its own.

The tray utility does two quite different jobs, and both have to be taken over:

**Settings it pushes into the drivers once, at logon.** Without them the trackpad has no
tap to click, no secondary tap and no scrolling, and F1/F2 do nothing to the display
brightness. Nothing has to stay running afterwards — the drivers keep the settings until
they are reloaded.

**Hotkeys it sits and waits for.** Five keys have nothing behind them but a listening
program:

| Key | What it does |
| --- | --- |
| F5 / F6 | keyboard backlight down / up |
| F10 / F11 / F12 | mute / volume down / volume up |

It also fades the keyboard backlight out when the machine is left alone, which this
program now does too.

## Using it

Run `bin\HideBootcampTrayUtility.exe`. The settings window has three checkboxes; hover any of them
for an explanation.

- **Enabled** — take over from Boot Camp. Applies the driver settings, ends `Bootcamp.exe`
  and keeps this program running in the background after the window closes. Unticked, the
  program exits when you close it.
- **Auto-start this hider** — adds it to Task Manager's Startup tab (the per-user `Run`
  key) so it starts silently at logon. No administrator rights needed.
- **Disable Boot Camp autostart** — switches off Apple's own `Apple_KbdMgr` startup entry
  using the same mechanism as Task Manager's Startup tab. Boot Camp's entry is
  machine-wide, so Windows asks for administrator approval when you close the window.
  The entry is disabled, not deleted, and can be switched back on here or in Task Manager.

Settings are saved when you close the window — that is the only gesture there is.

Once it is running with no window, launching the executable again brings its settings back
rather than starting a second copy.

The trackpad and keyboard settings themselves are still Apple's to edit: use the Boot Camp
control panel for those. This program reads what it left behind and pushes it to the
drivers, which is all the tray utility was doing with them.

## Building

```
build.cmd
```

No .NET SDK, no Visual Studio, no NuGet: this builds with the MSBuild and C# compiler that
ship inside Windows itself, at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`. That is
deliberate — the target is a Windows IoT Enterprise LTSC image with no toolchain on it, so
the program is a single .NET Framework 4.8 executable that runs on the image as delivered.
The `MSB3644` warning about missing reference assemblies is expected and harmless; with no
targeting packs installed MSBuild resolves the references from the GAC instead.

Because the compiler is the in-box one, the sources are **C# 5**: no string interpolation,
no `?.`, no `nameof`, no expression-bodied members.

## How it works

Recovered from `Bootcamp.exe` with x64dbg, driving its `headless.exe` from a script with
non-breaking logging breakpoints on `DeviceIoControl`, `CreateFileW` and
`RegQueryValueExW`. A registry, service and PnP snapshot taken either side of that run came
back identical, so what follows really is the whole difference the tray utility makes.

Apple's binary probes a dozen device names to cover every Mac it ships on. Four exist on a
MacBookAir6,1, and everything below goes through them: `\\.\AppleTrackpad`,
`\\.\AppleKeyboard`, `\\.\KeyManager` and `\\.\MacHALDriver`. None of the four needs
elevation.

### The settings pushed at logon (`DriverInit`)

| Device | IOCTL | Sent |
| --- | --- | --- |
| `\\.\AppleTrackpad` | `0x000F2004` `IOCTL_TRACKPAD_SET_MODE` | `HKCU\...\Apple Inc.\Trackpad\Mode`, as a DWORD |
| `\\.\AppleKeyboard` | `0xB403201C` `IOCTL_KEYBOARD_SET_OSX_FN_BEHAVIOR` | `OSXFnBehavior`, as a DWORD |
| `\\.\MacHALDriver` | `0x9C402484` | nothing; 60 bytes back, used only as "did that work" |
| `\\.\AppleKeyboard` | `0xB4032048` `IOCTL_ACPI_BRIGHTNESS_AVAILABLE` | `1` |

The IOCTL names are Apple's own, lifted from the error strings in the binary. That last row
is what gives F1 and F2 back; the first is tap to click, secondary tap and scrolling, all
three of which live in the one `Mode` word.

These are re-sent whenever a HID device arrives or the machine wakes, because a driver that
has just been reloaded knows none of it. `Bootcamp.exe` watches for the same two things —
it imports `RegisterDeviceNotification` and `RegisterPowerSettingNotification`, and still
carries the symbol `CMainFrame::OnPowerBroadcast`.

### The hotkeys (`HotkeyWorker`)

Hotkeys arrive as an inverted-call notification. `\\.\KeyManager` is opened and handed one
auto-reset event per hotkey, passed as the raw `Type3InputBuffer` with `nInBufferSize = 0`
(`METHOD_NEITHER`). The driver signals the matching event on a keypress; a background
thread sits in `WaitForMultipleObjects`. Boot Camp registers twenty-nine of these; the five
above are the ones with nothing else behind them. The volume keys are then passed straight
to Windows as `VK_VOLUME_*` presses, which is what raises the familiar volume overlay.

### The backlight (`Backlight`, `BacklightIdle`)

The keyboard backlight is the Apple SMC key `LKSB`, written through `\\.\MacHALDriver` with
IOCTL `0x9C402458` and an input buffer of `"LKSB\0"` + big-endian `level << 4`. The
hardware level is 0..4095; Boot Camp's user-facing scale is 0..16 and maps as `level << 8`.
The level is tracked in memory and persisted to the same registry value Boot Camp uses,
`HKCU\Software\Apple Inc.\Apple Keyboard Support\Light Value`.

Boot Camp can also *read* the key — IOCTL `0x9C402460`, `"LKSB\0"` in and twelve bytes out
— so an earlier note in this project that `LKSB` is write-only was wrong. Those twelve
bytes are not decoded here, so the stored level is still what this program trusts.

`User Absence` in the same registry key is the "turn off keyboard backlight when computer
is not used for" slider, in milliseconds. `Bootcamp.exe` imports `GetLastInputInfo` and
`SetTimer` and installs no input hook, so it polls; so does this. The fade deliberately
does not write `Light Value` — that is the level the user chose, not the level the room is
in — so the light comes back where they left it.

### What is not implemented: "Adjust in Low Light"

The ambient-light checkbox on the same control panel tab is left alone, and the evidence
says that is probably the right place for it:

- `"Adjust in Low Light"` appears in `Bootcamp.exe` only as a registry value name.
- The binary contains exactly one SMC key literal, `LKSB`. No ambient-light key
  (`ALV0`, `ALV1`, `MSAL`, `ALSF`, …) is anywhere in it.
- Its only WMI use is `ROOT\CIMV2`, with no light-sensor query.

So the light is almost certainly not being read in user mode at all: the flag is handed to
a driver and the firmware follows the sensor itself. Settling which of the keyboard IOCTLs
carries it would mean disassembling `KeyMagic.sys`.

## What it writes

| Where | What |
| --- | --- |
| `HKCU\Software\HideBootcampTrayUtility` | `Enabled` |
| `HKCU\...\CurrentVersion\Run` | `HideBootcampTrayUtility`, when auto-start is on |
| `HKCU\...\Explorer\StartupApproved\Run` | the Startup-tab switch for its own entry |
| `HKLM\...\Explorer\StartupApproved\Run` | the Startup-tab switch for `Apple_KbdMgr`, elevated |
| `HKCU\Software\Apple Inc.\Apple Keyboard Support` | `Light Value`, shared with Boot Camp |

Everything else Boot Camp stores — the trackpad `Mode`, `OSXFnBehavior`, `User Absence` —
is read and never written: those belong to the Boot Camp control panel. Nothing is written
outside the registry and nothing is installed.
