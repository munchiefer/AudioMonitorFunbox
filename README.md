# Sonic Funbox

A small Windows tray app for people who play, stream and chat at the same time.

Designed and crafted by **munchiefer**; programmed by **Claude** (Anthropic's AI) under munchiefer's guidance as a non-programmer product owner. See [Credits](#credits).

## How it was made
- **Code:** written by AI.
- **Product:** human crafted. The idea, the design and every decision are a person's.
- **Visuals:** AI-generated for now. They are meant to become human crafted: if you would like to donate your time to make them, you will be properly credited for your work. Contact: munchiefer@gmail.com

## TL;DR

| Feature | What it does, in one line |
|---|---|
| **Speaker copy** | Plays your PC's sound on a second device too, e.g. speakers and headphones at once. |
| **Microphones** | Turns up to 4 mics/inputs into one mic for Discord (needs the free VB-CABLE driver). |
| **Listen to VB-CABLE** | Lets you hear exactly what Discord hears from you. |
| **Fun box** | Voice effects for Discord (chipmunk, radio, demon, ghost, echo, walkie-talkie and more). |
| **Hotkeys** | One key turns the effects on/off, another plays them only while held, in any app or game. |
| **Pedalboard** (Sonic Funbox) | Guitar or bass (4/5 strings) through pedals and an amp, each with its own sound, to your speakers and Discord. |
| **Guitar input** (Sonic Funbox) | The Rocksmith Real Tone cable, an audio interface, or your PC's own jack (line-in or mic-in). |
| **Tuner** (Sonic Funbox) | Shows the note you play, plus tuning presets (E standard, Drop D, B standard, ...) with a box per string. |
| **Device memory** | Remembers your devices, even unplugged, and picks them up again when you plug them back in. |

Two editions: **Sonic Funbox** (everything, for Real Tone cable players) and **Sonic Funbox Lite** (no Pedalboard).
(Up to version 2.0.2 they were called AudioFunbox and AudioMonitor.)
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
- **Pedalboard** (Sonic Funbox, the edition for the Rocksmith Real Tone cable): play your guitar or 4/5-string
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

| | Sonic Funbox | Sonic Funbox Lite |
|---|---|---|
| Made for | Rocksmith Real Tone cable players | everyone |
| Speaker copy, Microphones, Fun box | yes | yes |
| Pedalboard (guitar or bass through pedals, tuner with tuning presets; Real Tone cable, audio interface or PC jack) | yes | no |
| Get it | installer or portable exe | installer or portable exe |

Same program, same settings: only one of them runs at a time, and you can switch between them freely.

## Requirements

- Windows 10 version 2004 or newer, or Windows 11, 64-bit. Nothing else to install: the exe is self-contained.
- For the mic mixer: the free [VB-CABLE](https://vb-audio.com/Cable/) virtual audio driver.

## Download

Version 2.1.0. Pick one (all files are also on the [latest release page](https://github.com/munchiefer/SonicFunbox/releases/latest)):

| File | What it is | SHA-256 |
|---|---|---|
| [`SonicFunbox-Setup-2.1.0.exe`](https://github.com/munchiefer/SonicFunbox/releases/download/v2.1.0/SonicFunbox-Setup-2.1.0.exe) | **Installer**: choose Sonic Funbox Lite, or Sonic Funbox for the Rocksmith Real Tone cable; run it again to upgrade, remove the pedalboard or uninstall | `718FDD8F07B810D4A571D3A5B19EA393F0DC20EAE63733FA4FF3674E16813C7B` |
| [`SonicFunbox.exe`](https://github.com/munchiefer/SonicFunbox/releases/download/v2.1.0/SonicFunbox.exe) | Sonic Funbox, portable: just run it (everything, including the Pedalboard) | `6477AB47201D0DC9FFB6EBD6465A7B428D31B96DFA6E3016473F1617CFF3B522` |
| [`SonicFunboxLite.exe`](https://github.com/munchiefer/SonicFunbox/releases/download/v2.1.0/SonicFunboxLite.exe) | Sonic Funbox Lite, portable: just run it (no Pedalboard) | `AAA3CF94500AE1C75231DF38BCC2EEFBEDAF2D7289AA36B868F08A674E7B6EE3` |

The installer and the portable Sonic Funbox Lite are also kept in this repository
([`release/`](release/SonicFunbox-Setup-2.1.0.exe), [`portable versions/`](portable%20versions/SonicFunboxLite.exe)).

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
dotnet publish -c Release -o out                        # SonicFunbox.exe
dotnet publish -c Release -p:Edition=Monitor -o out     # SonicFunboxLite.exe
```

Tests: `tests/mictest` compiles the app's sources with a `Test.cs`; `tests/examples` holds ready-made tests
(most read your real devices, none play sound or touch your settings).

```
cd tests/mictest
dotnet run -c Release                                       # as Sonic Funbox
dotnet run -c Release -p:DefineConstants=MONITOR_EDITION    # as Sonic Funbox Lite
```

The installer is built with [Inno Setup 6](https://jrsoftware.org/isinfo.php) from `installer/SonicFunbox.iss`
(after publishing both exes into `portable versions`; only `SonicFunboxLite.exe` is kept in git there):

```
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=2.1.0 installer\SonicFunbox.iss
```

The icon (`app.ico`) is generated from `assets/icon-master.png` by `python assets/make_icon.py` (needs Pillow).

## Credits

- **Design and craft: munchiefer.** The idea, what the app does and how it behaves, every feature and decision,
  and the testing by ear and hands on real hardware.
- **Programming: Claude**, Anthropic's AI, through Claude Code. The code, the measurements and the build were done
  by Claude, working from munchiefer's design and feedback.
- **Icon:** the "Sonic Funbox", chosen by munchiefer, generated with Google Gemini (see
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

## License

MIT, see [LICENSE](LICENSE). Third-party parts (NAudio, the .NET runtime)
are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Rocksmith is a trademark of Ubisoft; VB-CABLE is
made by VB-Audio. This project isn't affiliated with either.
