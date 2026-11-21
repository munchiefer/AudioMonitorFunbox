# Audio Monitor: version history

Newest first. Each published build bumps `<Version>` in `AudioMonitor.csproj`; `release/README.txt` carries the
current version and the SHA-256 of each published exe.

## 2.0.1 (2026-11-21)
- **Speaker copy, "Only these apps": no more crackles.** Windows delivers app capture's 10 ms packets unevenly
  (measured 7 to 12.75 ms apart; a whole device: 9.8 to 10.2 ms). At some moments, which come and go as the
  two devices' clocks drift, a late packet found the 17 ms buffer short and the copy crackled. App capture now
  keeps 27 ms: a replay of measured timings ran clean at every phase, with about 6 ms to spare. The copy is
  about 10 ms later in this mode. Copying a whole device keeps its 17 ms, so a sync tuned by ear stays as it was.
- **Speaker copy heals itself:** if a read ever finds the buffer short (a late packet, or the audio resuming
  after a pause), it refills to its target at once, like the mic mixer already did, instead of crackling again
  and again while the slow drift correction catches up.
- **Speaker copy starts at the right delay:** the first audio used to land one packet (10 ms) late, and the
  drift correction then dipped below the target for about a minute. It now starts exactly at the target.

## 2.0.0 (2026-11-21)
- **Guitar or bass.** An Instrument choice on the Pedalboard tab: Guitar (6 strings), Bass (4 strings), Bass
  (5 strings). Guitar and bass each keep their own pedal sound (the two basses share one), so switching brings
  that sound back. Your existing knob settings become the guitar sound; the bass starts from a basic bass preset.
- **Bass voicing keeps the low notes.** In bass mode the cabinet goes down to ~25 Hz (low E 41 Hz, low B 31 Hz,
  drop A 27.5 Hz all pass; the guitar voicing cuts them by 7 to 14 dB), the amp's tone controls sit at
  80 Hz / 500 Hz / 2.5 kHz, and the drive keeps the lows clean below 150 Hz. Fixed gains keep the same knobs
  within 1 dB of the guitar voicing (measured with test noise). The guitar sound is unchanged, bit for bit.
- **Presets follow the instrument:** the guitar presets as before, plus bass presets (Clean, Warm, Growl, Slap,
  Pick, Dub, Boost only, All off).
- **Tuner tuning presets:** pick a tuning (guitar: E standard, Drop D, E♭ standard, D standard, Drop C, Open G,
  DADGAD; 4-string bass: E standard, Drop D, E♭ standard, D standard, Drop C; 5-string bass: B standard, Drop A,
  half step down). One box per string shows its target note; the string you play lights up with which way to
  tune and how many cents. The tuner now reads down to 25 Hz (drop A on a 5-string). The main display still
  shows the note you play.
- **Input:** a new install starts with the input "not chosen": the Real Tone cable is picked by itself as soon
  as one is connected, otherwise you choose. "PC audio jack" now covers line-in, mic-in, the front panel and a
  laptop's headset jack, with advice in the tab (if your jack isn't listed, plug the instrument in first).
- **Low-latency input** for the pedalboard where the device allows a shorter engine period than 10 ms (often
  onboard sound): about 8 ms less lag there (estimated). Anything that doesn't work out falls back to the normal
  path. The Real Tone cable only offers 10 ms, so it stays exactly as before.
- **Pedalboard: choose how the guitar is connected.** Rocksmith Real Tone cable, an audio interface's
  instrument input, or the PC's own jack. The Device list follows the choice (Real Tone cables, or every
  input), and help under it explains each.
- **Channel choice** for an audio interface or the PC jack: Auto (the clearly louder side), Left / Input 1,
  Right / Input 2, or both mixed. A mono guitar cable in a stereo jack usually feeds the left side only, which
  used to come out 6 dB quieter, mixed with the empty side's noise. It applies to the pedalboard, the tuner, and
  the mic mixer when it takes the guitar directly. The Real Tone cable always uses both sides, exactly as before.
- **Rocksmith step-aside only for the cable:** a guitar on an audio interface or the line-in keeps playing while
  Rocksmith runs.
- The pedalboard's buffer and lag figure use the input's real packet size (10 ms for the cable, so unchanged).
- Older settings: a guitar input picked by hand that isn't the cable becomes "Audio interface" (USB) or "PC
  audio jack" (other), keeping the device and the old both-sides mix; "Automatic" still picks the cable.
- Texts that said "cable" for any input now say "input"; the status shows the input's real packet size.

## 1.6.2 (2026-11-21)
- Review release: no change to what the app does.
- When an audio device can't be opened (for example another app holds it in exclusive mode, and the app retries
  every few seconds), everything that was already opened is released at once instead of waiting for the garbage
  collector (measured: 600 failed opens used to leave ~600 handles open, now none).
- The low-latency output reuses its buffer on mono and surround devices (no garbage on the audio thread).
- One line-ending style on every machine (`.gitattributes`), so a fresh clone builds byte-identical exes.
- Only the AudioMonitor portable exe is in the repository; AudioFunbox (the edition for the Rocksmith Real Tone
  cable) is installed with the installer, which still offers both editions.
- Wording fixes in comments, test notes and README.txt.

## 1.6.1 (2026-11-20)
- "Start with Windows" is now a shortcut to the exe in the Startup folder (`AudioMonitorFunbox.lnk`), not a
  registry value holding a path: Windows lists it under Startup apps with the app's name and icon. The old
  registry value is turned into the shortcut once and removed; a shortcut whose exe has gone (moved, or the
  other edition removed) is pointed at the running exe at the next start. The installer's option creates the
  same shortcut and its uninstaller removes it only if it points into the installed folder.
- The version reads plainly "1.6.1" (no git commit appended).

## 1.6.0 (2026-11-20)
- **Installer** (Inno Setup, `installer/AudioMonitorFunbox.iss`): choose AudioMonitor or AudioFunbox; per-user,
  no administrator rights. Run again: "Upgrade to the full version" or "Remove pedalboard", plus "Uninstall" and
  "Update or repair". Switching keeps settings; uninstall asks before deleting them (default: keep).
  Silent use: `/VERYSILENT /EDITION=Monitor|Funbox`.
- **New icon**: the Material Icons "headphones" glyph in white on dark gray, for the exe, the settings window and
  the tray; the tray icon shows a green dot while something is running.
- Portable exes now live in `portable versions/`; the installer in `release/`.
- MIT license and third-party notices.

## 1.5.0 (2026-11-20)
- **Two editions** from the same sources: **AudioFunbox** (everything, the former Audio Monitor) and
  **AudioMonitor** (no Pedalboard tab; build with `-p:Edition=Monitor`). They share the settings file, the
  single-instance lock and the "Start with Windows" entry, so only one runs at a time; starting the second one
  now says so instead of doing nothing.
- **Device fingerprints:** each chosen device's name (without Windows' "1 - " / "2- " numbering), hardware model
  (`USB\VID_…&PID_…&MI_…`, plus the serial number if the device has one) and direction are saved with the
  choice. When Windows gives the device a new ID, the one connected device with the same fingerprint takes over
  the choice everywhere; with no match, or two identical devices, nothing is guessed.
- **Live device updates:** Windows' device notifications now trigger the reconnect check at once (within about
  0.4 s instead of up to 3 s; the 3-second check stays as a safety net), and the open Settings window reloads its
  device lists by itself (not while a list is open). An unplugged choice shows as "Name (disconnected)".

## 1.4.4 (2026-11-16)
- Pedalboard: the noise gate is removed (pedal, knob, and the gate in every preset). Nothing cuts the signal any more
  (a faint cable hum stays audible); before this, picking any preset switched the gate back on at -45 to -55 dB.
  Old settings files load fine: the gate keys are ignored and dropped on the next save.

## 1.4.3 (2026-11-16)
- The guitar cable is captured **raw** (new `RawCapture`), in the pedalboard and in the mic mixer. Windows 11's
  Voice Clarity (speech noise suppression, attached to USB inputs) was fading the cable's signal to silence.
- Tuner covers bass: down to a 5-string's low B (30.9 Hz). Before, nothing below 55 Hz was detected.
- The test harness now lives in `tests/`.

## 1.4.2 (2026-11-13)
- Voice effects keep the mic's loudness: one fixed gain per effect setting, calibrated on a built-in test
  voice (they used to change loudness by -9 to +20 dB). No auto-adjust while talking, by design.
- Volume knob under the effects (100% = unchanged), applied to everything the mixer sends.
- Mission control: audible "chk-shh" on push-to-talk press, static on release also when effects are ticked on;
  end-of-sentence static works for quiet voices; quieter hiss; gentler compressor.
- Gentle noise reducer on the effects path (not on the dry voice).

## 1.4.1 (2026-11-12)
- "Listen to VB-CABLE": hear what Discord hears on a chosen device.

## 1.4.0 (2026-11-12)
- Fix: the guitar never reached Discord in Auto-switch mode. New per-input **Always live** (on by default for
  the Rocksmith cable).
- Global hotkeys for the Fun box: on/off (with a blip only you hear) and push-to-talk. Keyboard keys,
  combos or mouse buttons; listen-only.
- Effects: Robot replaced by Underwater; new Telephone, Demon, Ghost. Echo/reverb tails ring out on release.
- Mission control became a military walkie-talkie (key-up click, squelch static).

## 1.3.1 (2026-11-09)
- Pin icons on the tabs: a pinned tab always has its own column and the window widens to fit. Remembered.

## 1.3.0 (2026-11-09)
- Pedalboard tab for the Rocksmith Real Tone cable: gate, compressor, boost (with Auto), drive, amp + cabinet,
  modulation, delay, reverb, master, safety limiter, tuner, meters, presets.
- The mixer takes the guitar through the pedals while the pedalboard is on.
- Rocksmith step-aside: the pedalboard and the cable's mixer input pause while the game runs.

## 1.2.1 (2026-11-09)
- Resizable settings window: fixed-width tab columns side by side as the window widens, vertical scroll bar,
  minimum height half the full height. The mouse wheel no longer changes sliders that weren't clicked.

## 1.2.0 (2026-11-09)
- Up to 4 mixer inputs, Level captions and tooltips everywhere.
- Self-healing inputs: clock drift correction per input, instant refill after a lost packet, unplugged inputs
  skipped and picked up again.
- Fun box tab: Pitch, Robot, Radio, Echo, Reverb and presets.

## 1.1.0 (early November 2026)
- Mic mixer: two microphones into VB-CABLE for Discord, Mix or Auto-switch.
- VB-CABLE detection, installed version, download page button, opt-in online update check.

## 1.0.0 to 1.0.5 (up to 2026-11-06)
- Tray app that copies the PC's audio (a device, or selected apps) to a second output device.
- Lag work: event-driven loopback capture, drift correction, IAudioClient3 low-latency output, "Minimum" buffer.
  1.0.5 measured a 22.6 ms gap between the two devices.
- Copy delay slider (replaced an earlier three-device "Sync" mode).
- Single self-contained exe, security pass (runs as normal user, no network, settings sanitized and moved to
  `%LOCALAPPDATA%`), .NET 10 with the security/reliability analyzers.
