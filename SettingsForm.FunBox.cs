namespace AudioMonitor;

// The "Fun box" tab: voice effects on what Discord hears, presets and the two global hotkeys.
sealed partial class SettingsForm
{
    readonly CheckBox fxEnabledBox = new() { Text = "Voice effects on", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly System.Windows.Forms.Timer fxDebounce = new() { Interval = 600 };
    readonly List<Action> fxReloads = new();
    readonly Label toggleKeyLabel = HotkeyLabel(), pttKeyLabel = HotkeyLabel();
    readonly Button toggleSetButton = new() { Text = "Set…", AutoSize = true }, toggleClearButton = new() { Text = "Clear", AutoSize = true };
    readonly Button pttSetButton = new() { Text = "Set…", AutoSize = true }, pttClearButton = new() { Text = "Clear", AutoSize = true };
    bool capturingHotkey, effectJustTicked;

    static Label HotkeyLabel() => new()
    {
        AutoSize = false, Width = 120, Height = 23, TextAlign = ContentAlignment.MiddleLeft, BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(3, 4, 3, 3), UseMnemonic = false, BackColor = SystemColors.Window,
    };

    FlowLayoutPanel BuildFunPage()
    {
        Label Fixed(string text) => new() { Text = text, AutoSize = false, Width = 95, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 4, 0, 3), UseMnemonic = false };

        var page = Column(
            Help("Effects on your voice for what Discord hears (the mic mixer must be on). Hear yourself with Discord's " +
                 "Settings > Voice & Video > Mic Test. Turn off Discord's Noise Suppression there, or it removes much of the effect."),
            fxEnabledBox,
            Header("Effects"),
            FxRow("Pitch", "Shifts your voice up (chipmunk) or down (deep voice). Adds about 20 ms delay.",
                f => f.PitchOn, (f, v) => f.PitchOn = v, -12, 12, f => f.PitchSemitones, (f, v) => f.PitchSemitones = v,
                v => v == 0 ? "0 (no change)" : $"{v:+0;-0} semitones", "Pitch shift in semitones: +12 = one octave up, -12 = one octave down."),
            FxRow("Underwater", "Muffled and bubbly, with a slow wobble, like talking from under water.",
                f => f.UnderwaterOn, (f, v) => f.UnderwaterOn = v, 0, 100, f => f.UnderwaterDepth, (f, v) => f.UnderwaterDepth = v,
                v => $"{v}% deep", "How deep: more muffled and more wobble."),
            FxRow("Radio", "Old radio: thin, with grit.",
                f => f.RadioOn, (f, v) => f.RadioOn = v, 0, 100, f => f.RadioDrive, (f, v) => f.RadioDrive = v,
                v => $"{v}% grit", "How much grit (distortion) the radio adds."),
            FxRow("Telephone", "Old landline: thin, boxy and a little lo-fi.",
                f => f.TelephoneOn, (f, v) => f.TelephoneOn = v, 0, 100, f => f.TelephoneGrit, (f, v) => f.TelephoneGrit = v,
                v => $"{v}% grit", "Line quality: higher = rougher, crunchier phone line."),
            FxRow("Demon", "Your voice with a deep, gritty copy underneath.",
                f => f.DemonOn, (f, v) => f.DemonOn = v, 0, 100, f => f.DemonDepth, (f, v) => f.DemonDepth = v,
                v => $"{v}% deep", "How deep and how loud the demon voice is."),
            FxRow("Ghost", "Breathy and airy, with an eerie room that swells in while you talk.",
                f => f.GhostOn, (f, v) => f.GhostOn = v, 0, 100, f => f.GhostAmount, (f, v) => f.GhostAmount = v,
                v => $"{v}% eerie", "More whisper and a bigger, eerier swell."),
            FxRow("Echo", "Repeats your voice after a delay, like shouting in a canyon.",
                f => f.EchoOn, (f, v) => f.EchoOn = v, 100, 800, f => f.EchoMs, (f, v) => f.EchoMs = v,
                v => $"{v} ms", "Time between the repeats."),
            FxRow("Reverb", "Room sound, from a small room to a cave.",
                f => f.ReverbOn, (f, v) => f.ReverbOn = v, 0, 100, f => f.ReverbSize, (f, v) => f.ReverbSize = v,
                v => $"{v}% room", "Room size: higher = bigger, longer-ringing room."),
            VolumeRow(),
            Header("Hotkeys (work in any app, also in games)"),
            Row(Fixed("On/off"), toggleKeyLabel, toggleSetButton, toggleClearButton),
            Row(Fixed("Push-to-talk"), pttKeyLabel, pttSetButton, pttClearButton),
            Help("Keyboard keys, key combos or mouse buttons (middle, Mouse 4/5). The keys keep working normally in other " +
                 "apps, so pick ones you don't use for anything else."),
            Header("Presets"),
            Row(FxPreset("Chipmunk", "High, squeaky voice (pitch +7).", f => { f.PitchOn = true; f.PitchSemitones = 7; }),
                FxPreset("Deep voice", "Low, heavy voice (pitch -5).", f => { f.PitchOn = true; f.PitchSemitones = -5; }),
                FxPreset("Underwater", "Talking from the bottom of the pool.", f => { f.UnderwaterOn = true; f.UnderwaterDepth = 60; }),
                FxPreset("Radio", "Old radio.", f => { f.RadioOn = true; f.RadioDrive = 60; })),
            Row(FxPreset("Telephone", "Old landline call.", f => { f.TelephoneOn = true; f.TelephoneGrit = 45; }),
                FxPreset("Demon", "Deep, gritty demon in a dark room.", f => { f.DemonOn = true; f.DemonDepth = 65; f.ReverbOn = true; f.ReverbSize = 40; }),
                FxPreset("Ghost", "Whispery ghost with an eerie swell.", f => { f.GhostOn = true; f.GhostAmount = 60; }),
                FxPreset("Cave", "Huge echoing cave.", f => { f.ReverbOn = true; f.ReverbSize = 90; f.EchoOn = true; f.EchoMs = 400; })),
            Row(FxPreset("Villain", "Deep voice in a big hall.", f => { f.PitchOn = true; f.PitchSemitones = -4; f.ReverbOn = true; f.ReverbSize = 60; }),
                FxPreset("Mission control", "Military walkie-talkie: narrow, crunchy radio.\n" +
                       "With the push-to-talk hotkey: a key-up click when you press it and a burst of static when you let go.\n" +
                       "Without it: the static plays when you stop talking.",
                       f => { f.RadioOn = true; f.RadioDrive = 55; f.WalkieTalkie = true; }),
                FxPreset("Reset", "All effects off.", null)));

        tips.SetToolTip(fxEnabledBox, "Turns the voice effects on or off (also with the On/off hotkey). With a push-to-talk hotkey,\n" +
                                      "leave this off: the effects then play only while you hold the key.");
        tips.SetToolTip(toggleKeyLabel, "Press this anywhere to switch the voice effects on or off. A short blip (only you hear it) confirms: rising = on, falling = off.");
        tips.SetToolTip(pttKeyLabel, "Hold this anywhere to use the voice effects; let go and Discord hears your normal voice. Your mic is never muted.");
        tips.SetToolTip(toggleSetButton, "Click, then press the key, key combo or mouse button to use. Esc cancels.");
        tips.SetToolTip(pttSetButton, "Click, then press the key, key combo or mouse button to use. Esc cancels.");
        tips.SetToolTip(toggleClearButton, "Remove this hotkey.");
        tips.SetToolTip(pttClearButton, "Remove this hotkey.");

        toggleSetButton.Click += (_, _) => StartHotkeyCapture(toggle: true);
        pttSetButton.Click += (_, _) => StartHotkeyCapture(toggle: false);
        toggleClearButton.Click += (_, _) => { app.SetHotkeys(null, app.Settings.FxPushToTalkHotkey); ShowHotkeys(); };
        pttClearButton.Click += (_, _) => { app.SetHotkeys(app.Settings.FxToggleHotkey, null); ShowHotkeys(); };
        return page;
    }

    /// <summary>Overall volume of what Discord hears from the mixer: mics, guitar and effects, effects on or off.</summary>
    Control VolumeRow()
    {
        var name = new Label { Text = "Volume", AutoSize = false, Width = 90, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        var bar = Slider(0, 200, 180, 25, 5, 10);
        bar.MouseWheel += WheelScrollsWindow;
        var value = Value();
        bar.ValueChanged += (_, _) =>
        {
            value.Text = bar.Value + "%";
            if (loading) return;
            app.Settings.Fx.Volume = bar.Value;
            FxChanged();
        };
        fxReloads.Add(() => { bar.Value = Math.Clamp(app.Settings.Fx.Volume, 0, 200); value.Text = bar.Value + "%"; });
        const string tip = "Overall volume of what Discord hears from the mixer: your mics, the guitar and the effects, whether effects are on or not.\n" +
                           "100% = exactly as your mics deliver it. The effects themselves never change your loudness: each is matched to your mic.";
        tips.SetToolTip(name, tip);
        tips.SetToolTip(bar, tip);
        var row = Row(name, bar, value);
        row.Margin = new Padding(0, 6, 0, 0);
        return row;
    }

    /// <summary>One effect: on/off box, a slider and its value, bound to the Fun box settings.</summary>
    Control FxRow(string name, string tip, Func<VoiceFxSettings, bool> getOn, Action<VoiceFxSettings, bool> setOn,
                  int min, int max, Func<VoiceFxSettings, int> get, Action<VoiceFxSettings, int> set, Func<int, string> format, string sliderTip)
    {
        var box = new CheckBox { Text = name, AutoSize = false, Width = 90, Height = 30 };
        int range = max - min;
        var bar = Slider(min, max, 180, Math.Max(1, range / 12), 1, Math.Max(1, range / 10));
        bar.MouseWheel += WheelScrollsWindow;
        var value = Value();
        box.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            setOn(app.Settings.Fx, box.Checked);
            effectJustTicked = box.Checked;
            FxChanged();
            effectJustTicked = false;
        };
        bar.ValueChanged += (_, _) =>
        {
            value.Text = format(bar.Value);
            if (loading) return;
            set(app.Settings.Fx, bar.Value);
            FxChanged();
        };
        fxReloads.Add(() =>
        {
            box.Checked = getOn(app.Settings.Fx);
            bar.Value = Math.Clamp(get(app.Settings.Fx), min, max);
            value.Text = format(bar.Value);
        });
        tips.SetToolTip(box, tip);
        tips.SetToolTip(bar, sliderTip);
        return Row(box, bar, value);
    }

    /// <summary>A preset replaces the effect choice. It switches the effects on so you hear it, unless you use push-to-talk.</summary>
    Button FxPreset(string text, string tip, Action<VoiceFxSettings>? set)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) =>
        {
            bool pushToTalk = app.Settings.FxPushToTalkHotkey != null;
            // A preset changes the effects only: your Volume stays where you set it.
            var fx = new VoiceFxSettings { Enabled = set != null && (!pushToTalk || app.Settings.Fx.Enabled), Volume = app.Settings.Fx.Volume };
            set?.Invoke(fx);
            app.Settings.Fx = fx.Sanitized();
            ShowFx(app.Settings.Fx);
            app.SetEffects(save: true);
        };
        tips.SetToolTip(b, tip);
        return b;
    }

    void HookFunEvents()
    {
        fxEnabledBox.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            app.Settings.Fx.Enabled = fxEnabledBox.Checked;
            FxChanged();
        };
        fxDebounce.Tick += (_, _) => { fxDebounce.Stop(); app.SetEffects(save: true); };
        ShowHotkeys();
    }

    void FxChanged()
    {
        var fx = app.Settings.Fx;
        // Ticking an effect switches the effects on so it can be heard right away (not with push-to-talk:
        // then the effects should only ever play while the key is held).
        if (effectJustTicked && !fx.Enabled && app.Settings.FxPushToTalkHotkey == null)
        {
            fx.Enabled = true;
            bool was = loading;
            loading = true;
            fxEnabledBox.Checked = true;
            loading = was;
        }
        app.SetEffects(save: false);   // live
        fxDebounce.Stop();
        fxDebounce.Start();
    }

    void ShowFx(VoiceFxSettings fx)
    {
        bool was = loading;
        loading = true;
        fxEnabledBox.Checked = fx.Enabled;
        foreach (var reload in fxReloads) reload();
        loading = was;
    }

    void ShowHotkeys()
    {
        toggleKeyLabel.Text = app.Settings.FxToggleHotkey?.ToString() ?? "(none)";
        pttKeyLabel.Text = app.Settings.FxPushToTalkHotkey?.ToString() ?? "(none)";
        toggleClearButton.Enabled = app.Settings.FxToggleHotkey != null;
        pttClearButton.Enabled = app.Settings.FxPushToTalkHotkey != null;
    }

    void StartHotkeyCapture(bool toggle)
    {
        if (capturingHotkey) return;
        capturingHotkey = true;
        var button = toggle ? toggleSetButton : pttSetButton;
        var label = toggle ? toggleKeyLabel : pttKeyLabel;
        button.Text = "Press it…";
        label.Text = "Esc = cancel";
        app.CaptureHotkey(binding =>
        {
            capturingHotkey = false;
            if (IsDisposed) return;
            button.Text = "Set…";
            if (binding != null)
            {
                var other = toggle ? app.Settings.FxPushToTalkHotkey : app.Settings.FxToggleHotkey;
                if (binding.SameAs(other))
                    MessageBox.Show(this, $"{binding} is already the {(toggle ? "push-to-talk" : "on/off")} hotkey. Pick a different one.", "Hotkeys");
                else if (toggle) app.SetHotkeys(binding, app.Settings.FxPushToTalkHotkey);
                else app.SetHotkeys(app.Settings.FxToggleHotkey, binding);
            }
            ShowHotkeys();
        });
    }

    /// <summary>The on/off hotkey can change the effects while this window is open: keep the box in step.</summary>
    void UpdateFunStatus()
    {
        if (fxEnabledBox.Checked == app.Settings.Fx.Enabled) return;
        bool was = loading;
        loading = true;
        fxEnabledBox.Checked = app.Settings.Fx.Enabled;
        loading = was;
    }
}
