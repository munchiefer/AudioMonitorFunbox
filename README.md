# AudioFunbox and AudioMonitor

A small Windows tray app for people who play, stream and chat at the same time.

Designed and crafted by **munchiefer**; programmed by **Claude** (Anthropic's AI). See [Credits](#credits).

## TL;DR

| Feature | What it does, in one line |
|---|---|
| **Speaker copy** | Plays your PC's sound on a second device too, e.g. speakers and headphones at once. |
| **Microphones** | Turns up to 4 mics/inputs into one mic for Discord (needs the free VB-CABLE driver). |
| **Listen to VB-CABLE** | Lets you hear exactly what Discord hears from you. |
| **Fun box** | Voice effects for Discord (chipmunk, radio, demon, ghost, echo, walkie-talkie and more). |
| **Hotkeys** | One key turns the effects on/off, another plays them only while held, in any app or game. |
| **Pedalboard** (AudioFunbox) | Guitar or bass (4/5 strings) through pedals and an amp, each with its own sound, to your speakers and Discord. |
| **Guitar input** (AudioFunbox) | The Rocksmith Real Tone cable, an audio interface, or your PC's own jack (line-in or mic-in). |
| **Tuner** (AudioFunbox) | Shows the note you play, plus tuning presets (E standard, Drop D, B standard, ...) with a box per string. |
| **Device memory** | Remembers your devices, even unplugged, and picks them up again when you plug them back in. |

Two editions: **AudioFunbox** (everything, for Real Tone cable players) and **AudioMonitor** (no Pedalboard).
Free, no ads, no account, no internet needed.

## What each part does

- **Speaker copy**: play what your PC plays on two devices at once (for example speakers and headphones),
  with a delay slider to line them up and Windows' low-latency mode where the driver supports it.
  Copy everything, or only the apps you pick.
- **Microphones**: mix up to four inputs (mics, a headset mic, a guitar cable) into
  [VB-CABLE](https://vb-audio.com/Cable/), so Discord or any other app can use them as one microphone.
  Mix them all, or let the loudest one win (Auto-switch), with "Always live" inputs.
  "Listen to VB-CABLE" lets you hear what Discord hears.
- **Fun box**: voice effects on that mix (Pitch, Underwater, Radio, Telephone, Demon, Ghost, Echo, Reverb,
  a walkie-talkie preset), each at a fixed, calibrated loudness, with global on/off and push-to-talk hotkeys.
- **Pedalboard** (AudioFunbox, the edition for the Rocksmith Real Tone cable): play your guitar or 4/5-string
  bass through pedals (compressor, boost, drive, amp + cabinet, modulation, delay, reverb, safety limiter) to
  your speakers or headphones and into the mixer. Guitar and bass each keep their own sound and presets; bass
  mode keeps the low notes. A tuner shows the note you play, with tuning presets per instrument. Made for the
  Real Tone cable, and it also takes an audio interface's instrument input or one of the PC's own jacks (pick
  which channel carries the instrument, or let Auto find it).
  The guitar is captured raw, past Windows 11's speech noise suppression, which otherwise fades an
  instrument's held notes to silence.

Chosen devices are remembered: unplug your headphones and the choice stays; plug them back in and they're used
again within a second or two. If Windows gives a device a new ID, it's recognised by its name and hardware model.

## Two editions

| | AudioFunbox | AudioMonitor |
|---|---|---|
| Made for | Rocksmith Real Tone cable players | everyone |
| Speaker copy, Microphones, Fun box | yes | yes |
| Pedalboard (guitar or bass through pedals, tuner with tuning presets; Real Tone cable, audio interface or PC jack) | yes | no |
| Get it | installer (choose AudioFunbox) | installer or portable exe |

Same program, same settings: only one of them runs at a time, and you can switch between them freely.

## Requirements

- Windows 10 version 2004 or newer, or Windows 11, 64-bit. Nothing else to install: the exe is self-contained.
- For the mic mixer: the free [VB-CABLE](https://vb-audio.com/Cable/) virtual audio driver.

## Download

Version 2.0.0. Pick one:

| File | What it is | SHA-256 |
|---|---|---|
| [`release/AudioMonitorFunbox-Setup-2.0.0.exe`](release/AudioMonitorFunbox-Setup-2.0.0.exe) | **Installer**: choose AudioMonitor, or AudioFunbox for the Rocksmith Real Tone cable; run it again to upgrade, remove the pedalboard or uninstall | `9174F0A159995B4B95DA5266F72B19372C728BB73D2DF3C4872903FA45CF7920` |
| [`portable versions/AudioMonitor.exe`](portable%20versions/AudioMonitor.exe) | AudioMonitor, portable: just run it (no Pedalboard) | `D366D680978614BA044224DB50BF1098BCB2E7B8684418A6337B6DF786A0F7AF` |

AudioFunbox comes with the installer only (there is no portable download of it; to get a portable copy, build it
from source, see [Building](#building)). Once installed, its exe's SHA-256 is `25C4FAC4B067794298C1A61C8648B40F990B320650A48B2A8F051AE27E1D170F`.

The installer needs no administrator rights: it installs for your Windows user only. Nothing is code-signed, so
SmartScreen may warn on first run ("More info" > "Run anyway"); check a download with `Get-FileHash <file>` in
PowerShell. Full usage notes: [release/README.txt](release/README.txt).

## Privacy and security

- Runs as a normal user; never asks for administrator rights and never installs drivers.
- No network access, telemetry or logging. The only exception is an opt-in VB-CABLE version check (off by
  default, with a warning before it's enabled): one HTTPS request a day to vb-audio.com, nothing sent about you.
- Audio never leaves your PC. Settings live in `%LOCALAPPDATA%\AudioMonitor\settings.json`, and every value is
  checked when it's loaded.
- Hotkeys use Windows' standard keyboard/mouse listener only while a hotkey is set, and only check for that key.

## Building

Needs the .NET 10 SDK.

```
dotnet build -c Release
dotnet publish -c Release -o out                        # AudioFunbox.exe
dotnet publish -c Release -p:Edition=Monitor -o out     # AudioMonitor.exe
```

Tests: `tests/mictest` compiles the app's sources with a `Test.cs`; `tests/examples` holds ready-made tests
(most read your real devices, none play sound or touch your settings).

```
cd tests/mictest
dotnet run -c Release                                       # as AudioFunbox
dotnet run -c Release -p:DefineConstants=MONITOR_EDITION    # as AudioMonitor
```

The installer is built with [Inno Setup 6](https://jrsoftware.org/isinfo.php) from `installer/AudioMonitorFunbox.iss`
(after publishing both exes into `portable versions`; only `AudioMonitor.exe` is kept in git there):

```
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=2.0.0 installer\AudioMonitorFunbox.iss
```

The icon is generated by `python assets/make_icon.py` (needs Pillow).

## Credits

- **Design and craft: munchiefer.** The idea, what the app does and how it behaves, every feature and decision,
  and the testing by ear and hands on real hardware.
- **Programming: Claude**, Anthropic's AI, through Claude Code. The code, the measurements and the build were done
  by Claude, working from munchiefer's design and feedback.
- **Icon:** the "headphones" glyph from Google's [Material Icons / Material Symbols](https://github.com/google/material-design-icons),
  Apache License 2.0 (see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

## License

MIT, see [LICENSE](LICENSE). Third-party parts (NAudio, the .NET runtime, the Material Icons headphones glyph)
are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Rocksmith is a trademark of Ubisoft; VB-CABLE is
made by VB-Audio. This project isn't affiliated with either.
