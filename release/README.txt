AudioFunbox and AudioMonitor 2.0.2
==================================

Play your PC audio on your speakers and your headphones at the same time, combine up to four
microphones/inputs into one for Discord, add fun voice effects, and (AudioFunbox) play your guitar
(Rocksmith Real Tone cable) through a pedalboard.

Two editions, same program:
  - AudioFunbox.exe   for Rocksmith Real Tone cable players: Speaker copy, Microphones, Fun box and
                      the Pedalboard. Installer or portable exe.
  - AudioMonitor.exe  the same without the Pedalboard tab. Installer or portable exe.
  Only one of them runs at a time (starting the other one tells you so). They share their settings
  and the "Start with Windows" entry, so you can switch between them without setting anything up again.

Requirements: Windows 10 version 2004 or newer, or Windows 11 (64-bit). Nothing else to install
(the .NET 10 runtime is built in).

Install, or run portable
------------------------
  - Installer (AudioMonitorFunbox-Setup-<version>.exe): pick AudioMonitor or AudioFunbox. It installs for
    your Windows user only (no administrator rights) into %LOCALAPPDATA%\Programs\AudioMonitorFunbox, with a
    Start menu shortcut and optional desktop shortcut and "Start with Windows".
    Run it again later to switch: with AudioMonitor installed it offers "Upgrade to the full version",
    with AudioFunbox installed "Remove pedalboard"; both also offer "Uninstall" and "Update or repair".
    Switching keeps all your settings. Uninstall asks whether to delete your settings too (default: keep).
  - Portable: AudioFunbox.exe and AudioMonitor.exe run as they are, from any folder. Nothing to install.
    Download them from the release page: https://github.com/munchiefer/AudioMonitorFunbox/releases/latest

Use
---
Run AudioFunbox or AudioMonitor. Its icon (a yellow and white speaker box with headphones) appears in the
system tray, with a green dot while something is running.
  - Left-click the icon for Settings. Right-click it for: Monitoring on, Mic mixer on, Mic mode,
    Pedalboard on (AudioFunbox), Start with Windows, Exit.
  - Your devices are remembered. Unplug your headphones and the choice stays (shown as
    "(disconnected)"); plug them back in and they're used again within a second or two, without
    picking them again. If Windows gives a device a new ID (after an update, a driver reinstall or
    another USB port), the app recognises it by its name and hardware model and keeps your choice.
    The device lists in the open Settings window update by themselves when you plug something in or out.
  - The Settings window can be resized: make it wider to see two, three or all four tabs side by side;
    make it shorter and it scrolls.
  - Click the pin icon on a tab to keep it always visible in its own column (the window widens to fit;
    click again to unpin). Pins are remembered.

Speaker copy tab
  - "Copy from": the device whose audio is copied, usually "Windows default output" (it follows
    when you switch the default). That device keeps playing normally.
  - "Copy to": the second device (e.g. headphones). It gets a copy about 20-25 ms behind.
  - "Output buffer": "Minimum" (far left) keeps as little audio queued as the device safely allows,
    using Windows' low-latency audio mode where the driver supports it. Raise it if you hear crackles.
  - "Delay the copy": when the source device is the slower one (e.g. Bluetooth headphones set as the
    Windows default, copied to your speakers), raise this until both sound as one. Applies instantly.
  - "Only these apps": copy only selected apps (their child processes are included). Windows delivers
    app audio less evenly than a whole device, so this mode keeps 8 ms more audio buffered: the copy is
    about 8 ms later, which avoids crackles.

Microphones tab (use several inputs as one mic, e.g. in Discord)
  - Needs VB-CABLE, a free virtual audio cable driver from VB-Audio (https://vb-audio.com/Cable/).
    Install it once; the app finds it automatically.
  - Add up to 4 inputs (mics, a headset mic, the Rocksmith guitar cable, ...). Each has a Level
    slider (100% = unchanged, 200% = twice as loud, 0% = muted) and a live level meter.
  - Mode:
      Mix:         all inputs are live at once.
      Auto-switch: the input that hears you clearly louder is live (switches in ~0.3 s, with a short
                   crossfade; a cough or bump on another mic doesn't switch it).
  - "Always live" (per input): in Auto-switch mode, that input is mixed in all the time instead of
    competing with the mics. Ticked automatically for the Rocksmith cable, so the guitar always reaches
    Discord while Auto-switch picks between your mics.
  - Self-healing: each input's clock drift is corrected continuously, and if a device drops a packet
    (the Rocksmith cable does now and then) the buffer refills at once instead of crackling. Inputs
    that are unplugged are skipped and come back automatically when plugged in again.
  - In Discord: Settings > Voice & Video > Input Device = "CABLE Output (VB-Audio Virtual Cable)", and
    set Noise Suppression to None: Discord's noise suppression treats a guitar (and voice effects) as
    noise and removes much of it.
  - Listen to VB-CABLE: tick it and pick your headphones to hear exactly what Discord hears (mics,
    guitar and voice effects), about 40-60 ms after you speak. Use headphones: on speakers your mic
    would hear itself and can echo or howl.
  - The tab shows the installed VB-CABLE version and a button to open its download page.
  - Hover over any slider or option for a short explanation.

Fun box tab (voice effects on what Discord hears)
  - Pitch (chipmunk / deep voice), Underwater, Radio, Telephone, Demon, Ghost, Echo and Reverb, each
    with its own slider, plus presets (Chipmunk, Deep voice, Underwater, Radio, Telephone, Demon,
    Ghost, Cave, Villain, Mission control, Reset).
  - Effects keep your mic's loudness: every effect setting has one fixed volume, levelled to a normal
    speaking voice (nothing adjusts itself while you talk). The effects also turn down background room
    noise a little while they're on; your normal voice is never touched.
  - Volume (under the effects): overall volume of everything Discord hears from the mixer, effects on
    or off. 100% = exactly as your mics deliver it.
  - Mission control = military walkie-talkie. Pressing the push-to-talk hotkey keys the radio ("chk-shh"),
    letting go drops it ("kshhh"), also when "Voice effects on" is ticked. Without push-to-talk, the
    static plays when you finish a sentence.
  - Hotkeys (keyboard keys, combos or mouse buttons; they work in any app, also in games):
      On/off:       switches the effects on or off; a short blip only you hear confirms it
                    (rising = on, falling = off).
      Push-to-talk: the effects play only while you hold it; otherwise Discord hears your normal voice.
  - Changes apply instantly. Hear yourself with Discord's Settings > Voice & Video > Mic Test.

Pedalboard tab (AudioFunbox only: your guitar or bass through pedals)
  - Instrument: Guitar (6 strings), Bass (4 strings) or Bass (5 strings). Guitar and bass each keep their
    own pedal sound (the two basses share one): switching brings that sound back. Bass mode keeps the low
    notes (low E, low B, drop tunings) that the guitar voicing cuts. The presets and tunings follow it.
  - Input: how your instrument reaches the PC. A new install picks the Real Tone cable by itself as soon
    as one is connected; otherwise choose here.
      Rocksmith Real Tone cable:  Device "Automatic" picks it when it's plugged in.
      Audio interface:            the instrument (Hi-Z) input of an audio interface; pick the interface
                                  and the input the instrument is plugged into (Channel; Auto finds it).
      PC audio jack:              one of the PC's own jacks (line-in, mic-in, front panel, a laptop's
                                  headset jack), with a mono cable or adapter. Line-in is best; mic-in
                                  is hotter (if the In meter hits the end, lower its level in Windows'
                                  sound settings). If your jack isn't listed, plug the instrument in
                                  first. A PC jack sounds duller than an instrument input. Channel: a
                                  mono cable in a stereo jack usually feeds the left side only; Auto
                                  uses the clearly louder side.
  - Plays the guitar on your speakers or headphones through: Compressor, Boost, Drive, Amp + cabinet,
    Modulation (chorus / flanger / tremolo), Delay, Reverb, Master volume. Each pedal has an on/off
    switch and its own knobs.
  - There is no noise gate (removed in 1.4.4): nothing ever cuts your sound or the fade of a note.
    The cable's faint mains hum stays audible between notes; the Compressor makes it a little louder.
  - A safety limiter at the end means it can never clip, however much you boost.
  - "Boost" alone makes the guitar louder without changing its sound. "Auto": click, play your hardest
    for 8 seconds, and it sets the boost so your loudest notes land just under the limit.
  - The guitar is captured raw, whatever the input: Windows 11 attaches "Voice Clarity" (speech noise
    suppression) to USB inputs, which rewrites an instrument's signal (it fades steady sounds and held
    notes away). The app bypasses it for the guitar, in the pedalboard and in the mic mixer. Other
    apps are not affected.
  - Tuner for guitar and bass (mutes the sound while on): shows the note you play, down to a 5-string's
    drop A. Pick a tuning (guitar: E standard, Drop D, E-flat standard, D standard, Drop C, Open G,
    DADGAD; 4-string bass: E standard, Drop D, E-flat standard, D standard, Drop C; 5-string bass:
    B standard, Drop A, half step down): one box per string shows its target note, and the string you
    play lights up with which way to tune (up/down arrow) and how many cents (100 cents = one fret).
  - Input/output meters, and presets. Guitar: Clean, Crunch, Lead, Metal, Ambient, Funk, Boost only,
    All off. Bass: Clean, Warm, Growl, Slap, Pick, Dub, Boost only, All off.
  - Where the device allows it (often onboard sound), the input runs in low-latency mode (shorter
    packets than the usual 10 ms); the status line says so. The Real Tone cable always uses 10 ms.
  - While it's on, Discord (the mic mixer) gets the instrument through the pedals instead of the raw input.
  - Rocksmith takes the cable for itself while it runs: the pedalboard (and the cable in the mic mixer)
    pause and come back by themselves when Rocksmith closes. An audio interface or the PC's jack
    keeps playing.
  - Lag from string to ear is shown in the tab: about 22 ms on speakers that support Windows'
    low-latency mode, about 40 ms on headsets that don't (the cable itself delivers sound in 10 ms
    packets, which Windows doesn't allow to go lower). Play through your speakers for the tightest feel.

What it does and doesn't do
---------------------------
  - Runs as a normal user. It never asks for administrator rights, and never installs drivers.
  - No network access by default, no telemetry, no logging. To notice Rocksmith starting, it checks
    the names of running programs every few seconds (names only, nothing else is read). Audio never leaves your PC; it only goes
    to your own devices (and to VB-CABLE, if you use the mic mixer).
  - Hotkeys: only if you set some, the app listens for them with Windows' standard keyboard/mouse
    listener. It only checks whether your hotkey is pressed: it doesn't record, store or send any
    keystrokes, and every key still reaches the app you're using. With no hotkeys set, nothing listens.
  - Optional VB-CABLE update check: OFF unless you tick it and confirm the warning. When on, about
    once a day it makes one HTTPS request to vb-audio.com/Cable/ to read the latest version number.
    It sends nothing about you or your PC (only "AudioMonitor/<version>" as the app name), follows no
    redirects, and never downloads or installs anything. Untick it to stop all network access.
  - Stores settings in %LOCALAPPDATA%\AudioMonitor\settings.json (both editions; 1.0.0 used %APPDATA%,
    moved automatically). To recognise your devices it saves their names and hardware model codes (the
    ones Device Manager shows) next to your choices; nothing else about your PC.
  - "Start with Windows" puts a shortcut named AudioMonitorFunbox in your Startup folder
    (%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup), so Windows lists it under Startup apps
    with the app's name and icon. Shared by both editions: it starts the edition you ticked it in last.
    If the exe has moved, the next start points the shortcut at it again. Untick it to remove the shortcut.
    (Up to 1.6.0 this was a registry value under HKCU\...\Run; it's turned into the shortcut automatically.)
  - To uninstall: untick "Start with Windows", exit, then delete the exe(s) and %LOCALAPPDATA%\AudioMonitor.

Tip: keep the exes in their own folder that only you can write to, e.g. %LOCALAPPDATA%\Programs\AudioMonitor.

The exes aren't code-signed, so Windows SmartScreen may warn on first run ("More info" > "Run anyway").
Check that your copy is genuine with its SHA-256 hash (PowerShell: Get-FileHash AudioFunbox.exe):
  AudioFunbox.exe   5CE5352CA60D0BA17527B259BC7684599E8B625AAF3BCEEA0C57040DC490CCC5
  AudioMonitor.exe  EA57D785D40827C27973C8B13D2C8457DF46D267A41964C6D22020688A81FF8D
  The installer's own hash is listed on the project page:
  https://github.com/munchiefer/AudioMonitorFunbox
