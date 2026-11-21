using System.Drawing.Drawing2D;

namespace AudioMonitor;

// The "Pedalboard" tab: guitar or bass (Real Tone cable, audio interface or PC jack) through pedals to speakers/headphones and
// Discord, with a tuner and tuning presets.
sealed partial class SettingsForm
{
    const int KnobWidth = 88;

    readonly ComboBox instrumentCombo = Combo(270);
    readonly ComboBox inputTypeCombo = Combo(270);
    readonly ComboBox pedalInputCombo = Combo(270);
    readonly ComboBox channelCombo = Combo(270);
    readonly Label inputHelpLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), MinimumSize = new Size(0, 64), ForeColor = SystemColors.GrayText, UseMnemonic = false };
    List<DeviceItem> pedalCaptureDevices = new();
    readonly ComboBox pedalOutputCombo = Combo(270);
    readonly CheckBox pedalEnabledBox = new() { Text = "Pedalboard on", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
    readonly Label pedalStatusLabel = Status();
    readonly ProgressBar pedalInMeter = new() { Width = 80, Height = 10, Maximum = 100, Margin = new Padding(3, 8, 3, 3) };
    readonly ProgressBar pedalOutMeter = new() { Width = 80, Height = 10, Maximum = 100, Margin = new Padding(3, 8, 3, 3) };
    readonly Label pedalWarnLabel = new() { AutoSize = true, Margin = new Padding(3, 5, 3, 3), UseMnemonic = false };
    readonly CheckBox tunerBox = new() { Text = "Tuner", AutoSize = false, Width = 78, Height = 36 };
    readonly TunerView tunerView = new() { Width = 270, Height = 36, Margin = new Padding(3, 0, 3, 3) };
    readonly ComboBox tuningCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, Margin = new Padding(3, 3, 3, 3) };
    readonly StringsView stringsView = new() { Width = 351, Height = 40, Margin = new Padding(3, 0, 3, 3) };
    FlowLayoutPanel guitarPresets = null!, bassPresets = null!;
    readonly Button autoBoostButton = new() { Text = "Auto", Width = KnobWidth, Height = 26, Margin = new Padding(3, 16, 3, 3) };
    readonly ComboBox modTypeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = KnobWidth, Margin = new Padding(3, 0, 3, 3) };
    readonly System.Windows.Forms.Timer pedalDebounce = new() { Interval = 600 };
    readonly System.Windows.Forms.Timer autoBoostTimer = new() { Interval = 1000 };
    readonly List<Action> pedalReloads = new();
    readonly float[] tunerSamples = new float[8192];   // ~170 ms: enough for a bass's low B
    readonly double[] tunerHistory = new double[3];
    int autoBoostSecondsLeft, warnHoldTicks, limitHoldTicks;
    string warnText = "";

    FlowLayoutPanel BuildPedalPage()
    {
        Label Fixed(string text) => new() { Text = text, AutoSize = false, Width = 72, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 0, 3), UseMnemonic = false };
        modTypeCombo.Items.AddRange(new object[] { ModType.Chorus, ModType.Flanger, ModType.Tremolo });
        modTypeCombo.MouseWheel += WheelScrollsWindow;
        instrumentCombo.MouseWheel += WheelScrollsWindow;
        inputTypeCombo.MouseWheel += WheelScrollsWindow;
        channelCombo.MouseWheel += WheelScrollsWindow;
        tuningCombo.MouseWheel += WheelScrollsWindow;
        instrumentCombo.Items.AddRange(new object[]
        {
            new InstrumentItem(Instrument.Guitar6, "Guitar (6 strings)"),
            new InstrumentItem(Instrument.Bass4, "Bass (4 strings)"),
            new InstrumentItem(Instrument.Bass5, "Bass (5 strings)"),
        });
        inputTypeCombo.Items.AddRange(new object[]
        {
            new InputTypeItem(GuitarInputType.RealToneCable, "Rocksmith Real Tone cable"),
            new InputTypeItem(GuitarInputType.AudioInterface, "Audio interface (instrument input)"),
            new InputTypeItem(GuitarInputType.PcJack, "PC audio jack (line-in or mic-in)"),
        });
        pedalInputCombo.MouseWheel += WheelScrollsWindow;
        pedalOutputCombo.MouseWheel += WheelScrollsWindow;

        var meters = Row(Caption("In"), pedalInMeter, Caption("Out"), pedalOutMeter, pedalWarnLabel);
        tunerBox.Font = new Font(Font, FontStyle.Bold);

        var page = Column(
            Help("Plays your guitar or bass through these pedals to your speakers or headphones: from the Rocksmith Real Tone cable, " +
                 "an audio interface or the PC's jack. While it's on, Discord gets it through the pedals too (via the mic mixer)."),
            Row(Fixed("Instrument"), instrumentCombo),
            Row(Fixed("Input"), inputTypeCombo),
            Row(Fixed("Device"), pedalInputCombo),
            Row(Fixed("Channel"), channelCombo),
            inputHelpLabel,
            Row(Fixed("Play on"), pedalOutputCombo),
            pedalEnabledBox, pedalStatusLabel, meters,
            Row(tunerBox, tunerView),
            Row(new Label { Text = "Tuning", AutoSize = false, Width = 78, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 3, 3) }, tuningCombo),
            stringsView,
            Header("Pedals (the sound flows from top to bottom)"),
            PedalRow("Comp", s => s.CompOn, (s, v) => s.CompOn = v,
                "Compressor: evens out loud and soft picking and makes notes ring longer.",
                Knob("Sustain", 0, 100, s => s.CompSustain, (s, v) => s.CompSustain = v, v => $"{v}%",
                    "How much it squeezes: higher = more even, longer sustain (and more noise between notes)."),
                Knob("Level", -12, 12, s => s.CompLevelDb, (s, v) => s.CompLevelDb = v, Db,
                    "Volume after the compressor.")),
            PedalRow("Boost", s => s.BoostOn, (s, v) => s.BoostOn = v,
                "Clean boost: makes the guitar louder without changing its sound.",
                Knob("Gain", 0, 30, s => s.BoostDb, (s, v) => s.BoostDb = v, Db,
                    "How much louder. +6 dB = twice the signal, +12 dB = four times.\nThe limiter at the end catches peaks, so it never clips."),
                autoBoostButton),
            PedalRow("Drive", s => s.DriveOn, (s, v) => s.DriveOn = v,
                "Overdrive / distortion. Low gain = warm crunch, high gain = heavy distortion.",
                Knob("Gain", 0, 100, s => s.DriveGain, (s, v) => s.DriveGain = v, v => $"{v}%",
                    "Amount of distortion."),
                Knob("Tone", 0, 100, s => s.DriveTone, (s, v) => s.DriveTone = v, v => $"{v}%",
                    "Darker (left) or brighter (right)."),
                Knob("Level", -24, 6, s => s.DriveLevelDb, (s, v) => s.DriveLevelDb = v, Db,
                    "Volume after the drive. Distortion is loud: use this to match the clean volume.")),
            PedalRow("Amp", s => s.AmpOn, (s, v) => s.AmpOn = v,
                "Amp tone controls plus a guitar speaker cabinet. Makes the plain cable sound like a guitar amp (and tames drive fizz).",
                Knob("Bass", 0, 100, s => s.AmpBass, (s, v) => s.AmpBass = v, AmpScale, "Low end. 5 = flat."),
                Knob("Mid", 0, 100, s => s.AmpMid, (s, v) => s.AmpMid = v, AmpScale, "Middle: body and punch. 5 = flat."),
                Knob("Treble", 0, 100, s => s.AmpTreble, (s, v) => s.AmpTreble = v, AmpScale, "Highs: bite and sparkle. 5 = flat.")),
            PedalRow("Mod", s => s.ModOn, (s, v) => s.ModOn = v,
                "Modulation: chorus (shimmering, wide), flanger (jet-plane sweep) or tremolo (pulsing volume).",
                Column(new Label { Text = "Type", AutoSize = false, Width = KnobWidth, Height = 16, Margin = new Padding(3, 0, 3, 0) }, modTypeCombo),
                Knob("Rate", 0, 100, s => s.ModRate, (s, v) => s.ModRate = v, v => $"{v}%", "Speed of the sweep or pulse."),
                Knob("Depth", 0, 100, s => s.ModDepth, (s, v) => s.ModDepth = v, v => $"{v}%", "How strong the effect is.")),
            PedalRow("Delay", s => s.DelayOn, (s, v) => s.DelayOn = v,
                "Echo: repeats what you play, each repeat a little darker.",
                Knob("Time", 50, 1000, s => s.DelayMs, (s, v) => s.DelayMs = v, v => $"{v} ms", "Time between repeats."),
                Knob("Repeats", 0, 90, s => s.DelayFeedback, (s, v) => s.DelayFeedback = v, v => $"{v}%", "How long the repeats go on."),
                Knob("Mix", 0, 100, s => s.DelayMix, (s, v) => s.DelayMix = v, v => $"{v}%", "Volume of the repeats.")),
            PedalRow("Reverb", s => s.ReverbOn, (s, v) => s.ReverbOn = v,
                "Room sound, from a small room to a huge hall.",
                Knob("Size", 0, 100, s => s.ReverbSize, (s, v) => s.ReverbSize = v, v => $"{v}%", "Room size: higher = longer ring-out."),
                Knob("Mix", 0, 100, s => s.ReverbMix, (s, v) => s.ReverbMix = v, v => $"{v}%", "How much room sound is added.")),
            MasterRow(),
            Header("Presets"),
            guitarPresets = Column(Row(Preset("Clean", "Clean amp, a little compression and room.", p =>
                {
                    p.CompOn = true; p.CompSustain = 30;
                    p.AmpOn = true; p.AmpBass = 55; p.AmpMid = 50; p.AmpTreble = 60;
                    p.ReverbOn = true; p.ReverbSize = 35; p.ReverbMix = 20;
                }),
                Preset("Crunch", "Light overdrive through the amp: classic rock rhythm.", p =>
                {
                    p.DriveOn = true; p.DriveGain = 45; p.DriveTone = 55; p.DriveLevelDb = -8;
                    p.AmpOn = true; p.AmpBass = 55; p.AmpMid = 60; p.AmpTreble = 55;
                    p.ReverbOn = true; p.ReverbSize = 30; p.ReverbMix = 15;
                }),
                Preset("Lead", "Singing distortion for solos, with echo and room.", p =>
                {
                    p.CompOn = true; p.CompSustain = 25;
                    p.DriveOn = true; p.DriveGain = 70; p.DriveTone = 60; p.DriveLevelDb = -10;
                    p.AmpOn = true; p.AmpBass = 50; p.AmpMid = 65; p.AmpTreble = 55;
                    p.DelayOn = true; p.DelayMs = 380; p.DelayFeedback = 30; p.DelayMix = 25;
                    p.ReverbOn = true; p.ReverbSize = 45; p.ReverbMix = 20;
                }),
                Preset("Metal", "Heavy, tight distortion with scooped mids.", p =>
                {
                    p.DriveOn = true; p.DriveGain = 95; p.DriveTone = 45; p.DriveLevelDb = -12;
                    p.AmpOn = true; p.AmpBass = 65; p.AmpMid = 30; p.AmpTreble = 65;
                })),
            Row(Preset("Ambient", "Clean, shimmering and spacious: chorus, long echo, big hall.", p =>
                {
                    p.CompOn = true; p.CompSustain = 40;
                    p.AmpOn = true; p.AmpBass = 50; p.AmpMid = 45; p.AmpTreble = 60;
                    p.ModOn = true; p.ModType = ModType.Chorus; p.ModRate = 20; p.ModDepth = 50;
                    p.DelayOn = true; p.DelayMs = 520; p.DelayFeedback = 55; p.DelayMix = 40;
                    p.ReverbOn = true; p.ReverbSize = 85; p.ReverbMix = 45;
                }),
                Preset("Funk", "Squeezed, bright clean tone for rhythm.", p =>
                {
                    p.CompOn = true; p.CompSustain = 75;
                    p.AmpOn = true; p.AmpBass = 45; p.AmpMid = 55; p.AmpTreble = 70;
                }),
                Preset("Boost only", "Just louder: same sound as the input. Set the boost with Auto.", p =>
                {
                    p.BoostOn = true;
                }),
                Preset("All off", "Every pedal off: the plain input signal (the limiter still protects against clipping).", _ => { }))),
            bassPresets = Column(
                Row(Preset("Clean", "Bass: clean and direct, a little compression.", BassPresets.Clean),
                    Preset("Warm", "Bass: round and warm, a little low end added.", BassPresets.Warm),
                    Preset("Growl", "Bass: driven mids and top over clean, full lows.", BassPresets.Growl),
                    Preset("Slap", "Bass: squeezed, scooped mids and bright top for slap and pop.", BassPresets.Slap)),
                Row(Preset("Pick", "Bass: bright and punchy, for playing with a pick.", BassPresets.Pick),
                    Preset("Dub", "Bass: deep and dark, highs rolled off.", BassPresets.Dub),
                    Preset("Boost only", "Just louder: same sound as the input. Set the boost with Auto.", t => t.BoostOn = true),
                    Preset("All off", "Every pedal off: the plain input signal (the limiter still protects against clipping).", _ => { }))));
        guitarPresets.Margin = bassPresets.Margin = Padding.Empty;

        HookPedalEvents();
        return page;
    }

    static string Db(int v) => v > 0 ? $"+{v} dB" : $"{v} dB";
    static string AmpScale(int v) => (v / 10.0).ToString("0.0");

    /// <summary>A labelled slider bound to one pedal setting (live while dragging, saved shortly after).</summary>
    Control Knob(string caption, int min, int max, Func<PedalboardSettings, int> get, Action<PedalboardSettings, int> set,
                 Func<int, string> format, string tip, int width = KnobWidth)
    {
        var label = new Label { AutoSize = false, Width = width, Height = 16, Margin = new Padding(3, 0, 3, 0), UseMnemonic = false };
        int range = max - min;
        var bar = Slider(min, max, width, Math.Max(1, range / 10), 1, Math.Max(1, range / 10));
        bar.AutoSize = false;
        bar.Height = 26;
        bar.TickStyle = TickStyle.None;
        bar.Margin = new Padding(3, 0, 3, 3);
        bar.MouseWheel += WheelScrollsWindow;
        void Show() => label.Text = $"{caption}: {format(bar.Value)}";
        bar.ValueChanged += (_, _) =>
        {
            Show();
            if (loading) return;
            set(app.Settings.Pedal, bar.Value);
            PedalKnobChanged();
        };
        pedalReloads.Add(() => { bar.Value = Math.Clamp(get(app.Settings.Pedal), min, max); Show(); });
        tips.SetToolTip(bar, tip);
        tips.SetToolTip(label, tip);
        return Column(label, bar);
    }

    Control PedalRow(string name, Func<PedalboardSettings, bool> get, Action<PedalboardSettings, bool> set, string tip, params Control[] knobs)
    {
        var box = new CheckBox { Text = name, AutoSize = false, Width = 78, Height = 44, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 0, 3, 3) };
        box.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            set(app.Settings.Pedal, box.Checked);
            PedalKnobChanged();
        };
        pedalReloads.Add(() => box.Checked = get(app.Settings.Pedal));
        tips.SetToolTip(box, tip);
        var row = Row(new Control[] { box }.Concat(knobs).ToArray());
        row.Margin = new Padding(0, 0, 0, 2);
        return row;
    }

    Control MasterRow()
    {
        var name = new Label { Text = "Master", AutoSize = false, Width = 78, Height = 44, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 0, 3, 3) };
        const string tip = "Overall volume of the pedalboard (speakers and Discord). A safety limiter after it keeps the sound from ever clipping.";
        tips.SetToolTip(name, tip);
        return Row(name, Knob("Volume", -30, 12, s => s.MasterDb, (s, v) => s.MasterDb = v, Db, tip, width: 2 * KnobWidth + 6));
    }

    Button Preset(string text, string tip, Action<PedalTone> set)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) =>
        {
            // Presets change the pedal knobs only (from everything off, keeping the boost amount and the master volume):
            // the instrument, input, devices, on/off and tunings stay as you set them.
            var tone = PedalTone.PresetBase(app.Settings.Pedal);
            set(tone);
            tone.Sanitized().ApplyTo(app.Settings.Pedal);
            ShowPedals();
            app.SetPedals(save: true);
        };
        tips.SetToolTip(b, tip);
        return b;
    }

    void HookPedalEvents()
    {
        pedalEnabledBox.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            app.Settings.Pedal.Enabled = pedalEnabledBox.Checked;
            app.ApplyPedal();
        };
        pedalInputCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            string id = (pedalInputCombo.SelectedItem as DeviceItem)?.Id ?? "";
            var s = app.Settings.Pedal;
            if (s.Type == GuitarInputType.RealToneCable) s.InputDeviceId = id; else s.ExternalInputDeviceId = id;
            app.ApplyGuitarInput();
        };
        inputTypeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading || inputTypeCombo.SelectedItem is not InputTypeItem { Type: { } type }) return;
            app.Settings.Pedal.InputType = type;
            BeginInvoke(ShowGuitarInput);   // rebuilds this list (drops "choose..."): not while it's handling its own change
            app.ApplyGuitarInput();
        };
        instrumentCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading || instrumentCombo.SelectedItem is not InstrumentItem item) return;
            app.SetInstrument(item.Instrument);
            ShowPedals();   // that instrument's knobs, presets and tunings
        };
        tuningCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading || tuningCombo.SelectedItem is not Tuning tuning) return;
            app.SetTuning(tuning.Name);
            stringsView.Targets = app.Settings.Pedal.CurrentTuning.Strings;
            stringsView.Active = -1;
            stringsView.Invalidate();
        };
        channelCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading || channelCombo.SelectedItem is not ChannelItem item || app.Settings.Pedal.Type == GuitarInputType.RealToneCable) return;
            app.Settings.Pedal.InputChannel = item.Channel;
            app.ApplyGuitarInput();
        };
        pedalOutputCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            app.Settings.Pedal.OutputDeviceId = (pedalOutputCombo.SelectedItem as DeviceItem)?.Id ?? "";
            app.ApplyPedal(restartMic: false);
        };
        modTypeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading || modTypeCombo.SelectedItem is not ModType type) return;
            app.Settings.Pedal.ModType = type;
            PedalKnobChanged();
        };
        tunerBox.CheckedChanged += (_, _) =>
        {
            app.SetTunerMute(tunerBox.Checked);
            Array.Clear(tunerHistory);
            if (!tunerBox.Checked) { tunerView.HasNote = false; tunerView.Invalidate(); stringsView.Active = -1; stringsView.Invalidate(); }
        };
        pedalDebounce.Tick += (_, _) => { pedalDebounce.Stop(); app.SetPedals(save: true); };

        autoBoostButton.Click += (_, _) =>
        {
            if (autoBoostTimer.Enabled) return;
            if (!app.PedalRunning) { MessageBox.Show(this, "Turn the pedalboard on first.", "Auto boost"); return; }
            app.PedalResetInputMax();
            autoBoostSecondsLeft = 8;
            autoBoostButton.Text = $"Play! {autoBoostSecondsLeft}";
            autoBoostTimer.Start();
        };
        autoBoostTimer.Tick += (_, _) =>
        {
            if (--autoBoostSecondsLeft > 0) { autoBoostButton.Text = $"Play! {autoBoostSecondsLeft}"; return; }
            autoBoostTimer.Stop();
            autoBoostButton.Text = "Auto";
            float peak = app.PedalInputMax;
            if (peak < 0.003f)
            {
                MessageBox.Show(this, "No guitar signal was heard. Check the cable and the guitar's volume knob, then try again.", "Auto boost");
                return;
            }
            // Hardest note just under the limiter's threshold (-3 dBFS), measured on the clean input.
            int db = (int)Math.Floor(20 * Math.Log10(0.708 / peak));
            app.Settings.Pedal.BoostDb = Math.Clamp(db, 0, 30);
            app.Settings.Pedal.BoostOn = true;
            ShowPedals();
            app.SetPedals(save: true);
            if (db > 30) MessageBox.Show(this, $"Your guitar is very quiet: it needs +{db} dB, the boost goes up to +30 dB. Turn the guitar's volume knob up, or add Master volume.", "Auto boost");
        };

        tips.SetToolTip(instrumentCombo, "Guitar or bass. Each keeps its own pedal sound (both basses share one): switching brings that sound back.\n" +
                                         "Bass mode keeps the low notes (low E, low B, drop tunings) that the guitar voicing cuts.\n" +
                                         "The presets and the tunings follow the instrument.");
        tips.SetToolTip(inputTypeCombo, "How your instrument reaches the PC: the Rocksmith Real Tone cable, an audio interface with an instrument (Hi-Z) input,\n" +
                                        "or one of the PC's own input jacks (line-in, mic-in, front panel or a laptop's headset jack).");
        tips.SetToolTip(pedalInputCombo, "The input device. For the Real Tone cable, \"Automatic\" picks it when it's plugged in.\n" +
                                         "For an audio interface or the PC's jack, pick the input the instrument is plugged into.");
        tips.SetToolTip(channelCombo, "Which side of the input carries the guitar. A mono guitar cable in a stereo jack usually feeds the left side only.\n" +
                                      "Auto uses the clearly louder side, or both when they're similar. The Real Tone cable always uses both.");
        tips.SetToolTip(pedalOutputCombo, "Where you hear the guitar.\nTip: if Speaker copy is on and copies this device to your headphones, the headphones get the guitar too (about 20 ms later).");
        tips.SetToolTip(pedalEnabledBox, "Turns the pedalboard on or off. While on, the mic mixer (Discord) takes the guitar from the pedalboard instead of the raw cable.");
        tips.SetToolTip(pedalInMeter, "Level coming from the input, before the pedals. If it ever hits the end, the input itself is clipping: turn the instrument's\n" +
                                      "volume knob down a little (on a PC's mic-in, also lower its level in Windows' sound settings).");
        tips.SetToolTip(pedalOutMeter, "Level after all pedals, as you hear it.");
        tips.SetToolTip(pedalWarnLabel, "\"limiting\" = the safety limiter is turning peaks down so they can't clip. Occasional flickers are fine; if it's on all the time, lower Boost or Master.");
        tips.SetToolTip(tunerBox, "Shows the note you play and how far off it is. Mutes the sound while on, like a tuner pedal.");
        tips.SetToolTip(tunerView, "Play one string at a time and let it ring: the note you play and how far off it is. Green = in tune.\n" +
                                   "Works for guitar and bass (down to a 5-string's low B, and drop A).");
        tips.SetToolTip(tuningCombo, "The tuning you want. The boxes below show each string's target note, low to high.");
        tips.SetToolTip(stringsView, "Each string's target note in the chosen tuning (low to high). With the tuner on, the string you're playing lights up:\n" +
                                     "\u2191 = tune up, \u2193 = tune down, by that many cents (100 cents = one fret); \"in tune\" within 5 cents.");
        tips.SetToolTip(autoBoostButton, "Click, then play your hardest for 8 seconds: sets Boost so your loudest notes land just under the limit.");
        tips.SetToolTip(modTypeCombo, "Chorus = shimmer, Flanger = jet sweep, Tremolo = pulsing volume.");
        tips.SetToolTip(pedalStatusLabel, "Lag = time from string to speaker: the input delivers sound in packets (10 ms for the Real Tone cable; half a packet's wait on average),\n" +
                                          "then a small safety buffer, then the output device's queue. The converters inside the input and speakers add a few ms more.");
    }

    void PedalKnobChanged()
    {
        app.SetPedals(save: false);   // live
        pedalDebounce.Stop();
        pedalDebounce.Start();
    }

    void ShowPedals()
    {
        bool was = loading;
        loading = true;
        foreach (var reload in pedalReloads) reload();
        modTypeCombo.SelectedItem = app.Settings.Pedal.ModType;
        pedalEnabledBox.Checked = app.Settings.Pedal.Enabled;
        loading = was;
        ShowInstrument();
        ShowGuitarInput();
    }

    /// <summary>The instrument, its tuning list and string targets, and its preset buttons.</summary>
    void ShowInstrument()
    {
        var s = app.Settings.Pedal;
        bool was = loading;
        loading = true;
        instrumentCombo.SelectedItem = instrumentCombo.Items.Cast<InstrumentItem>().FirstOrDefault(i => i.Instrument == s.Instrument);
        tuningCombo.Items.Clear();
        tuningCombo.Items.AddRange(Tunings.For(s.Instrument));
        var tuning = s.CurrentTuning;
        tuningCombo.SelectedItem = tuning;
        stringsView.Targets = tuning.Strings;
        stringsView.Active = -1;
        stringsView.Invalidate();
        guitarPresets.Visible = !s.Bass;
        bassPresets.Visible = s.Bass;
        loading = was;
    }

    sealed record InstrumentItem(Instrument Instrument, string Name) { public override string ToString() => Name; }
    sealed record InputTypeItem(GuitarInputType? Type, string Name) { public override string ToString() => Name; }
    sealed record ChannelItem(GuitarChannel Channel, string Name) { public override string ToString() => Name; }

    void LoadPedalDevices(List<DeviceItem> render, List<DeviceItem> capture)
    {
        pedalCaptureDevices = capture;
        FillGuitarInputs();
        Fill(pedalOutputCombo, render.Where(d => !VbCable.IsCable(d.Name)).ToList(), app.Settings.Pedal.OutputDeviceId,
             new DeviceItem("", "Windows default output (follows changes)"));
    }

    /// <summary>The Guitar list for the chosen connection type: Real Tone cables (plus "Automatic"), or every input.</summary>
    void FillGuitarInputs()
    {
        var s = app.Settings.Pedal;
        bool was = loading;
        loading = true;
        if (s.Type == GuitarInputType.RealToneCable)
        {
            // Rocksmith cables, plus an input picked here before 2.0.0 that isn't one (so it shows with its real name).
            var cables = pedalCaptureDevices.Where(d => d.Name.Contains("Rocksmith", StringComparison.OrdinalIgnoreCase)
                                                        || d.Id == s.InputDeviceId).ToList();
            Fill(pedalInputCombo, cables, s.InputDeviceId, new DeviceItem("", "Automatic (the Rocksmith cable)"));
        }
        else
        {
            // Every input (all but VB-CABLE): an interface or a sound card can have any name.
            Fill(pedalInputCombo, pedalCaptureDevices, s.ExternalInputDeviceId, new DeviceItem("", "(pick the input the instrument is plugged into)"));
        }
        loading = was;
    }

    /// <summary>Shows the connection type, its device list, channel choice and help (after a load or a type change).</summary>
    void ShowGuitarInput()
    {
        var s = app.Settings.Pedal;
        bool was = loading;
        loading = true;
        // Not chosen yet (a new install without a Real Tone cable): a "choose" entry heads the list until a type is picked.
        bool unchosen = s.InputType == null, hasChoose = inputTypeCombo.Items.Count > 3;
        if (unchosen && !hasChoose) inputTypeCombo.Items.Insert(0, new InputTypeItem(null, "(choose how your instrument is connected)"));
        if (!unchosen && hasChoose) inputTypeCombo.Items.RemoveAt(0);
        inputTypeCombo.SelectedItem = inputTypeCombo.Items.Cast<InputTypeItem>().FirstOrDefault(i => i.Type == s.InputType);
        FillGuitarInputs();
        pedalInputCombo.Enabled = !unchosen;
        channelCombo.Items.Clear();
        switch (s.Type)
        {
            case GuitarInputType.RealToneCable:
                channelCombo.Items.Add(new ChannelItem(GuitarChannel.Both, "Both (the cable sends the same on each side)"));
                break;
            case GuitarInputType.AudioInterface:
                channelCombo.Items.AddRange(new object[]
                {
                    new ChannelItem(GuitarChannel.Auto, "Auto (the louder input)"), new ChannelItem(GuitarChannel.Left, "Input 1 (left)"),
                    new ChannelItem(GuitarChannel.Right, "Input 2 (right)"), new ChannelItem(GuitarChannel.Both, "Both inputs mixed"),
                });
                break;
            default:
                channelCombo.Items.AddRange(new object[]
                {
                    new ChannelItem(GuitarChannel.Auto, "Auto (the louder side)"), new ChannelItem(GuitarChannel.Left, "Left"),
                    new ChannelItem(GuitarChannel.Right, "Right"), new ChannelItem(GuitarChannel.Both, "Both sides mixed"),
                });
                break;
        }
        channelCombo.SelectedItem = channelCombo.Items.Cast<ChannelItem>().FirstOrDefault(i => i.Channel == s.EffectiveChannel)
                                    ?? channelCombo.Items[0];
        channelCombo.Enabled = !unchosen && s.Type != GuitarInputType.RealToneCable;
        inputHelpLabel.Text = unchosen
            ? "Choose how your guitar or bass reaches the PC. With a Rocksmith Real Tone cable plugged in, it's picked by itself."
            : s.Type switch
            {
                GuitarInputType.RealToneCable =>
                    "Plug the cable in and leave Device on \"Automatic\". Rocksmith takes the cable over while the game runs: " +
                    "the pedalboard pauses and comes back by itself.",
                GuitarInputType.AudioInterface =>
                    "Plug the instrument into the interface's instrument (Hi-Z) input and pick the interface under Device. " +
                    "Channel: the input it's plugged into (Auto finds the louder one).",
                _ =>
                    "Use a mono cable or adapter. Line-in is best; mic-in works but is hotter (if the In meter hits the end, lower its level " +
                    "in Windows' sound settings). If your jack isn't listed, plug the instrument in first. A PC jack sounds duller than an instrument input.",
            };
        loading = was;
    }

    void UpdatePedalStatus()
    {
        string text = "Status: " + app.PedalStatus;
        if (app.PedalLag is { } lag)
            text += $"\nLag: about {lag.Total:0} ms from string to speaker (input {lag.Capture:0.#} + buffer {lag.Buffer} + output {lag.Output:0.#} ms).";
        pedalStatusLabel.Text = text;
        if ((inputTypeCombo.SelectedItem as InputTypeItem)?.Type != app.Settings.Pedal.InputType) ShowGuitarInput();   // e.g. the cable was just picked
        if (pedalEnabledBox.Checked != app.Settings.Pedal.Enabled)
        {
            bool was = loading;
            loading = true;
            pedalEnabledBox.Checked = app.Settings.Pedal.Enabled;
            loading = was;
        }
    }

    void UpdatePedalMeters()
    {
        var (inPeak, outPeak, limiting, clipped) = app.PedalMeters();
        pedalInMeter.Value = ToMeter(inPeak);
        pedalOutMeter.Value = ToMeter(outPeak);
        if (clipped) { warnText = "input clipping!"; warnHoldTicks = 25; }
        if (limiting) limitHoldTicks = 6;
        if (warnHoldTicks > 0) warnHoldTicks--;
        if (limitHoldTicks > 0) limitHoldTicks--;
        string show = warnHoldTicks > 0 ? warnText : limitHoldTicks > 0 ? "limiting" : "";
        if (pedalWarnLabel.Text != show)
        {
            pedalWarnLabel.Text = show;
            pedalWarnLabel.ForeColor = warnHoldTicks > 0 ? Color.Firebrick : Color.DarkOrange;
        }

        if (!tunerBox.Checked) return;
        int n = app.PedalRecentInput(tunerSamples);
        double hz = Tuner.Detect(tunerSamples, n, app.PedalSampleRate);
        // Median of the last 3 readings: steadies the needle, ignores a stray octave jump.
        tunerHistory[0] = tunerHistory[1]; tunerHistory[1] = tunerHistory[2]; tunerHistory[2] = hz;
        var sorted = tunerHistory.OrderBy(v => v).ToArray();
        double median = sorted[1];
        tunerView.HasNote = median > 0;
        if (median > 0) (tunerView.Note, tunerView.Cents) = Tuner.Describe(median);
        tunerView.Invalidate();
        (stringsView.Active, stringsView.Cents) = median > 0 ? Tuner.NearestString(median, stringsView.Targets) : (-1, 0);
        stringsView.Invalidate();
    }

    /// <summary>
    /// One box per string of the chosen tuning (low to high) with its target note. The string being played (within a
    /// semitone of its target) lights up with which way to tune and by how many cents.
    /// </summary>
    sealed class StringsView : Control
    {
        public int[] Targets = Array.Empty<int>();
        public int Active = -1;
        public double Cents;

        public StringsView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int n = Math.Max(1, Targets.Length), gap = 4;
            int w = (Width - gap * (n - 1)) / n;
            using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
            for (int i = 0; i < Targets.Length; i++)
            {
                var r = new Rectangle(i * (w + gap), 0, w - 1, Height - 1);   // -1: the border stays inside the control
                bool on = i == Active;
                bool inTune = on && Math.Abs(Cents) < 5;
                var color = !on ? SystemColors.GrayText : inTune ? Color.ForestGreen : Color.DarkOrange;
                using (var pen = new Pen(color, on ? 2 : 1)) g.DrawRectangle(pen, r);
                var (note, _) = Tuner.Describe(Tuning.Hz(Targets[i]));
                TextRenderer.DrawText(g, note, Font, new Rectangle(r.X, r.Y + 2, r.Width, r.Height / 2), on ? color : ForeColor,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (on)
                {
                    // Cents = how far the note played is from the target: flat (below) -> tune up.
                    string label = inTune ? "in tune" : Cents < 0 ? $"\u2191 {-Cents:0}" : $"\u2193 {Cents:0}";
                    TextRenderer.DrawText(g, label, small, new Rectangle(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2 - 2), color,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }
    }

    /// <summary>Note name and a needle from -50 to +50 cents; green when within 5 cents.</summary>
    sealed class TunerView : Control
    {
        public string Note = "";
        public double Cents;
        public bool HasNote;

        public TunerView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool inTune = HasNote && Math.Abs(Cents) < 5;
            var color = !HasNote ? SystemColors.GrayText : inTune ? Color.ForestGreen : Color.DarkOrange;
            using (var big = new Font(Font.FontFamily, 14, FontStyle.Bold))
                TextRenderer.DrawText(g, HasNote ? Note : "–", big, new Rectangle(0, 0, 54, Height), color,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            int x0 = 62, x1 = Width - 10, y = Height / 2 + 2;
            using var pen = new Pen(SystemColors.GrayText, 1);
            g.DrawLine(pen, x0, y, x1, y);
            foreach (int c in new[] { -50, -25, 0, 25, 50 })
            {
                int x = x0 + (c + 50) * (x1 - x0) / 100;
                int h = c == 0 ? 10 : 5;
                g.DrawLine(pen, x, y - h, x, y + h);
            }
            if (HasNote)
            {
                int x = x0 + (int)((Math.Clamp(Cents, -50, 50) + 50) * (x1 - x0) / 100);
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, x - 2, y - 12, 5, 24);
                string label = inTune ? "in tune" : $"{Cents:+0;-0} cents";
                TextRenderer.DrawText(g, label, Font, new Point(x1 - 60, 0), color);
            }
        }
    }
}
