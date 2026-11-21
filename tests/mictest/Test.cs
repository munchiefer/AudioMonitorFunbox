// 2.0.0 checks: guitar/bass instrument with its own sound, bass voicing, tuning presets, input types and channel,
// "not chosen yet" input, low-latency capture fallback. SILENT and safe: no TrayApp is constructed (the window test
// uses an uninitialized fake whose handlers never run, and checks that settings.json is untouched); nothing is played
// (the pedal chain is muted, the mixer's test gain is 0 and it only feeds VB-CABLE). Run for both editions.
// Arg 0 = folder for the window screenshots.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using AudioMonitor;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
string outDir = args.Length > 0 ? args[0] : ".";
Console.WriteLine($"Edition under test: {Edition.Name}");
const int Rate = 48000;
var rng = new Random(1);
double Rms(float[] x, int from, int to) { double s = 0; for (int i = from; i < to; i++) s += x[i] * x[i]; return Math.Sqrt(s / (to - from)); }
double Db(double v) => 20 * Math.Log10(v + 1e-12);

// ---- A) ChannelPicker ----
{
    byte[] Frames(int frames, int channels, Func<int, int, float> sample)
    {
        var b = new byte[frames * channels * 4];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++) BitConverter.TryWriteBytes(b.AsSpan((f * channels + c) * 4), sample(f, c));
        return b;
    }
    float[] Pick(GuitarChannel mode, byte[] data, int channels)
    {
        int frames = data.Length / (4 * channels);
        var picker = new ChannelPicker(mode, Rate);
        var output = new float[frames];
        var block = new float[480];
        for (int start = 0; start < frames; start += 480)
        {
            int n = Math.Min(480, frames - start);
            picker.ToMono(data.AsSpan(start * channels * 4, n * channels * 4).ToArray(), n, channels, block);
            Array.Copy(block, 0, output, start, n);
        }
        return output;
    }
    var noise = Frames(Rate, 2, (_, _) => (float)(rng.NextDouble() * 2 - 1));
    var both = Pick(GuitarChannel.Both, noise, 2);
    bool exact = true;
    for (int f = 0; f < Rate && exact; f++)
        exact = both[f] == (BitConverter.ToSingle(noise, f * 8) + BitConverter.ToSingle(noise, f * 8 + 4)) / 2;
    Check(exact, "channel Both: bit for bit the old average (what the Real Tone cable always gets)");
    float Guitar(int f) => (float)(0.2 * Math.Sin(2 * Math.PI * 110 * f / Rate));
    int n2 = Rate * 2;
    double dry = Rms(Enumerable.Range(0, n2).Select(Guitar).ToArray(), Rate, n2);
    var leftOnly = Frames(n2, 2, (f, c) => c == 0 ? Guitar(f) : 0f);
    Check(Math.Abs(Db(Rms(Pick(GuitarChannel.Auto, leftOnly, 2), Rate, n2) / dry)) < 0.1, "channel Auto: a left-only guitar at full level (not 6 dB down)");
    var rightOnly = Frames(n2, 2, (f, c) => c == 1 ? Guitar(f) : 0f);
    Check(Rms(Pick(GuitarChannel.Left, rightOnly, 2), Rate, n2) < 1e-6, "channel Left on a right-only signal: silence (the choice is respected)");
}

// ---- B) settings: input, instrument, sounds, tunings ----
{
    var sanitize = typeof(Settings).GetMethod("Sanitized", BindingFlags.NonPublic | BindingFlags.Instance)!;
    Settings Load(string json) => (Settings)sanitize.Invoke(JsonSerializer.Deserialize<Settings>(json)!, null)!;
    const string Jack = "{0.0.1.00000000}.{11111111-0000-0000-0000-000000000001}";
    const string Cable = "{0.0.1.00000000}.{11111111-0000-0000-0000-000000000003}";
    string Old(string id, string? name, string hw) =>
        "{\"Pedal\":{\"Enabled\":true,\"InputDeviceId\":\"" + id + "\",\"DriveOn\":true,\"DriveGain\":77}" +
        (name == null ? "" : ",\"DeviceFingerprints\":{\"" + id + "\":{\"Name\":\"" + name + "\",\"Hardware\":\"" + hw.Replace("\\", "\\\\") + "\",\"Flow\":\"Capture\"}}") + "}";

    var fresh = Load("{}");
    Check(fresh.Pedal.InputType == null && fresh.Pedal.Type == GuitarInputType.RealToneCable,
          "new settings: input not chosen yet (the app takes a Real Tone cable when it sees one)");
    var auto = Load("{\"Pedal\":{\"Enabled\":true}}");
    Check(auto.Pedal.InputType == null, "older settings on \"Automatic\": not chosen yet, so the cable is still picked by itself");
    var jack = Load(Old(Jack, "Line In (Onboard Audio)", @"HDAUDIO\FUNC_01&VEN_AAAA&DEV_0001"));
    Check(jack.Pedal.InputType == GuitarInputType.PcJack && jack.Pedal.ExternalInputDeviceId == Jack && jack.Pedal.InputChannel == GuitarChannel.Both,
          "older settings with an onboard input picked: PC jack, same device, both channels as before");
    var cable = Load(Old(Cable, "Microphone (Rocksmith USB Guitar Adapter)", @"USB\VID_ABCD&PID_0001&MI_00"));
    Check(cable.Pedal.InputType == GuitarInputType.RealToneCable && cable.Pedal.InputDeviceId == Cable, "older settings with the cable picked: Real Tone cable");
    Check(jack.Pedal.Instrument == Instrument.Guitar6 && jack.Pedal.DriveOn && jack.Pedal.DriveGain == 77 && jack.Pedal.BassTone == null,
          "older settings: guitar, today's knobs are the guitar sound, no bass sound yet");

    // every knob is part of a sound, and copying a sound keeps every knob
    var toneProps = typeof(PedalTone).GetProperties().Where(p => p.CanWrite).ToList();
    var setProps = typeof(PedalboardSettings).GetProperties().ToDictionary(p => p.Name);
    Check(toneProps.Count == 25 && toneProps.All(p => setProps.ContainsKey(p.Name) && setProps[p.Name].PropertyType == p.PropertyType),
          $"a sound holds all {toneProps.Count} knobs, each matching the pedalboard's");
    var odd = new PedalboardSettings();
    foreach (var p in toneProps)
        setProps[p.Name].SetValue(odd, p.PropertyType == typeof(bool) ? true : p.PropertyType == typeof(ModType) ? ModType.Tremolo : (object)7);
    var copy = new PedalboardSettings();
    PedalTone.From(odd).ApplyTo(copy);
    Check(toneProps.All(p => Equals(setProps[p.Name].GetValue(copy), setProps[p.Name].GetValue(odd))), "From + ApplyTo copy every knob");

    var s = new PedalboardSettings { DriveOn = true, DriveGain = 77, AmpOn = true, AmpTreble = 80 };
    s.SwitchInstrument(Instrument.Bass4);
    var bassDefault = PedalTone.BassDefault();
    Check(s.Instrument == Instrument.Bass4 && s.GuitarTone?.DriveGain == 77 && !s.DriveOn && s.CompOn == bassDefault.CompOn && s.CompSustain == bassDefault.CompSustain,
          "guitar -> bass: the guitar sound is kept, the bass starts from the basic bass preset");
    s.CompSustain = 55; s.AmpOn = true; s.AmpBass = 66;
    s.SwitchInstrument(Instrument.Bass5);
    Check(s.Instrument == Instrument.Bass5 && s.CompSustain == 55 && s.AmpBass == 66, "4-string -> 5-string: the same bass sound");
    s.SwitchInstrument(Instrument.Guitar6);
    Check(s.DriveOn && s.DriveGain == 77 && s.AmpTreble == 80 && s.BassTone?.CompSustain == 55, "bass -> guitar: the guitar sound comes back, the bass sound is kept");
    s.SwitchInstrument(Instrument.Bass4);
    Check(s.CompSustain == 55 && s.AmpBass == 66, "guitar -> bass again: your bass sound comes back");

    s.SetTuning("Drop D");
    Check(s.Bass4Tuning == "Drop D" && s.CurrentTuning.Strings.SequenceEqual(new[] { 26, 33, 38, 43 }), "a tuning is saved per instrument (4-string Drop D: D1 A1 D2 G2)");
    s.SetTuning("No such tuning");
    Check(s.Bass4Tuning == "E standard", "an unknown tuning falls back to the instrument's default");
    s.SetTuning("Drop C");
    string saved = JsonSerializer.Serialize(s);
    var again = (Settings)sanitize.Invoke(JsonSerializer.Deserialize<Settings>("{\"Pedal\":" + saved + "}")!, null)!;
    Check(again.Pedal.Instrument == Instrument.Bass4 && again.Pedal.GuitarTone?.DriveGain == 77 && again.Pedal.CompSustain == 55
          && again.Pedal.Bass4Tuning == "Drop C" && saved.Contains("\"Instrument\":\"Bass4\""), "instrument, both sounds and tunings survive save + load");
    var bad = Load("{\"Pedal\":{\"Instrument\":9,\"GuitarTuning\":\"x\",\"BassTone\":{\"DriveGain\":500,\"MasterDb\":99}}}");
    Check(bad.Pedal.Instrument == Instrument.Guitar6 && bad.Pedal.GuitarTuning == "E standard" && bad.Pedal.BassTone!.DriveGain == 100 && bad.Pedal.BassTone.MasterDb == 12,
          "out-of-range instrument, tuning and stored-sound values are reset or clamped on load");
}

// ---- C) bass voicing in the pedal chain ----
{
    float[] RunChain(float[] input, Action<PedalboardSettings> set, Instrument inst)
    {
        var st = new PedalboardSettings { Instrument = inst, BoostOn = false };
        set(st);
        var chain = new PedalChain(Rate, st.Sanitized());
        var x = (float[])input.Clone();
        var block = new float[480];
        for (int i = 0; i < x.Length; i += 480) { Array.Copy(x, i, block, 0, 480); chain.Process(block, 480); Array.Copy(block, 0, x, i, 480); }
        return x;
    }
    int n = Rate * 3;
    float[] Sine(double hz) => Enumerable.Range(0, n).Select(i => (float)(0.05 * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();
    foreach (var (hz, note) in new[] { (27.5, "A0, drop A"), (30.87, "B0, 5-string low B"), (41.2, "E1, low E") })
    {
        var tone = Sine(hz);
        double d = Rms(tone, Rate, n);
        double g = Db(Rms(RunChain(tone, st => st.AmpOn = true, Instrument.Guitar6), Rate, n) / d);
        double b = Db(Rms(RunChain(tone, st => st.AmpOn = true, Instrument.Bass5), Rate, n) / d);
        double bd = Db(Rms(RunChain(tone, st => { st.DriveOn = true; st.DriveLevelDb = 0; }, Instrument.Bass5), Rate, n) / d);
        Check(b > -1.5 && bd > -1.5 && b - g > 4, $"bass keeps {note} ({hz} Hz): amp {b:+0.0;-0.0} dB, drive {bd:+0.0;-0.0} dB (guitar voicing: amp {g:+0.0;-0.0} dB)");
    }
    var pink = new float[n];
    double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
    for (int i = 0; i < n; i++)
    {
        double w = rng.NextDouble() * 2 - 1;
        b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
        b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
        pink[i] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.01); b6 = w * 0.115926;
    }
    foreach (var (name, set) in new (string, Action<PedalboardSettings>)[]
             { ("amp", st => st.AmpOn = true), ("drive", st => st.DriveOn = true), ("drive + amp", st => { st.DriveOn = true; st.DriveGain = 80; st.AmpOn = true; }) })
    {
        double diff = Db(Rms(RunChain(pink, set, Instrument.Bass4), Rate, n) / Rms(RunChain(pink, set, Instrument.Guitar6), Rate, n));
        Check(Math.Abs(diff) <= 1.0, $"same knobs, guitar vs bass voicing, {name}: {diff:+0.00;-0.00} dB apart on test noise (fixed gains, within 1 dB)");
    }
    // switching instrument live: no bad values, never above the limiter's ceiling
    var st0 = new PedalboardSettings { AmpOn = true, DriveOn = true }.Sanitized();
    var live = new PedalChain(Rate, st0);
    var buf = new float[480];
    float worst = 0;
    bool finite = true;
    for (int k = 0; k < 200; k++)
    {
        for (int i = 0; i < 480; i++) buf[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 55 * (k * 480 + i) / Rate));
        if (k == 100) { var sw = st0.Clone(); sw.SwitchInstrument(Instrument.Bass4); sw.DriveOn = true; sw.AmpOn = true; live.Update(sw); }
        live.Process(buf, 480);
        foreach (var v in buf) { finite &= float.IsFinite(v); worst = Math.Max(worst, Math.Abs(v)); }
    }
    Check(finite && worst <= PedalChain.Ceiling + 1e-6, $"switching guitar -> bass while playing: no bad values, never above the limiter's ceiling (peak {worst:0.000})");
}

// ---- D) tuner and tuning presets ----
{
    float[] Note(double hz, double level = 0.1)
    {
        var x = new float[8192];
        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Rate, decay = Math.Exp(-t * 1.5);
            double v = 0.5 * Math.Sin(2 * Math.PI * hz * t) + 1.0 * Math.Sin(2 * Math.PI * 2 * hz * t + 0.4) + 0.6 * Math.Sin(2 * Math.PI * 3 * hz * t + 1.1)
                     + 0.3 * Math.Sin(2 * Math.PI * 4 * hz * t + 2.0) + 0.15 * Math.Sin(2 * Math.PI * 5 * hz * t + 0.7);
            x[i] = (float)(level * decay * v / 1.6 + 0.00032 * Math.Sin(2 * Math.PI * 50 * t) + (rng.NextDouble() * 2 - 1) * 0.0003);
        }
        return x;
    }
    int strings = 0, read = 0;
    double worst = 0;
    foreach (var inst in new[] { Instrument.Guitar6, Instrument.Bass4, Instrument.Bass5 })
        foreach (var tuning in Tunings.For(inst))
            foreach (int midi in tuning.Strings)
            {
                strings++;
                double target = Tuning.Hz(midi);
                double f = Tuner.Detect(Note(target), 8192, Rate);
                var (index, cents) = Tuner.NearestString(f, tuning.Strings);
                if (f > 0 && index >= 0 && tuning.Strings[index] == midi && Math.Abs(cents) < 3) read++;
                else Console.WriteLine($"      missed: {inst} {tuning.Name}, {Tuner.Describe(target).Note} ({target:0.00} Hz) read as {f:0.00} Hz");
                if (f > 0) worst = Math.Max(worst, Math.Abs(1200 * Math.Log2(f / target)));
            }
    Check(read == strings, $"every string of every tuning preset is read as its target: {read} of {strings}, worst {worst:0.0} cents");
    var a0 = Note(Tuning.Hz(21) * Math.Pow(2, 20 / 1200.0));   // drop A, 20 cents sharp
    var (ai, ac) = Tuner.NearestString(Tuner.Detect(a0, 8192, Rate), Tunings.Bass5[1].Strings);
    Check(ai == 0 && Math.Abs(ac - 20) < 3, $"drop A (27.5 Hz) 20 cents sharp: string 1 lights up, {ac:+0.0;-0.0} cents (tune down)");
    var flatE = Note(82.41 * Math.Pow(2, -35 / 1200.0));
    var (ei, ec) = Tuner.NearestString(Tuner.Detect(flatE, 8192, Rate), Tunings.Guitar6[0].Strings);
    Check(ei == 0 && Math.Abs(ec + 35) < 3, $"guitar low E 35 cents flat: string 1 lights up, {ec:+0.0;-0.0} cents (tune up)");
    Check(Tuner.NearestString(Tuning.Hz(42), Tunings.Guitar6[0].Strings).Index == -1, "a note more than a semitone from every string: no string lights up");
    Check(Tunings.Bass5[0].Name.StartsWith("B standard") && Tunings.Bass4[0].Name == "E standard" && Tunings.Guitar6[0].Name == "E standard",
          "defaults: guitar and 4-string E standard, 5-string B standard");
    var quiet = Note(41.2, 0.004);
    Check(Tuner.Describe(Math.Max(1, Tuner.Detect(quiet, 8192, Rate))).Note == "E1", "a soft low E (-50 dBFS) is still read");
    var hum = new float[8192];
    for (int i = 0; i < hum.Length; i++) hum[i] = (float)(0.00032 * Math.Sin(2 * Math.PI * 50 * i / Rate) + (rng.NextDouble() * 2 - 1) * 0.0003);
    Check(Tuner.Detect(hum, 8192, Rate) == 0, "hum alone (nothing played) shows no note");
}

// ---- E) the Pedalboard tab (handlers never run: settings.json must stay untouched) ----
string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioMonitor", "settings.json");
DateTime? stamp = File.Exists(settingsFile) ? File.GetLastWriteTimeUtc(settingsFile) : null;
if (Edition.HasPedalboard)
{
    ApplicationConfiguration.Initialize();
    foreach (var (type, inst) in new (GuitarInputType?, Instrument)[] { (null, Instrument.Guitar6), (GuitarInputType.RealToneCable, Instrument.Bass5), (GuitarInputType.PcJack, Instrument.Guitar6) })
    {
        var app = (TrayApp)RuntimeHelpers.GetUninitializedObject(typeof(TrayApp));
        void SetField(string name, object? value) => typeof(TrayApp).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, value);
        var fake = new Settings { PinnedTabs = { "Pedalboard" }, MonitorDeviceId = "x", Pedal = new PedalboardSettings { InputType = type, Instrument = inst } };
        SetField("<Settings>k__BackingField", fake);
        foreach (var f in new[] { "<Status>k__BackingField", "<MicStatus>k__BackingField", "<UpdateCheckStatus>k__BackingField", "<PedalStatus>k__BackingField" })
            SetField(f, "");
        var form = new SettingsForm(app);
        typeof(SettingsForm).GetField("loading", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
        form.Show(); Application.DoEvents();
        T Get<T>(string name) => (T)typeof(SettingsForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        string shownType = Get<ComboBox>("inputTypeCombo").SelectedItem?.ToString() ?? "(none)";
        string shownInst = Get<ComboBox>("instrumentCombo").SelectedItem?.ToString() ?? "(none)";
        var tuning = Get<ComboBox>("tuningCombo");
        bool bassShown = Get<FlowLayoutPanel>("bassPresets").Visible, guitarShown = Get<FlowLayoutPanel>("guitarPresets").Visible;
        Console.WriteLine($"      {type?.ToString() ?? "not chosen"} / {inst}: Input \"{shownType}\", Instrument \"{shownInst}\", Tuning \"{tuning.SelectedItem}\" ({tuning.Items.Count}), presets: {(bassShown ? "bass" : "")}{(guitarShown ? "guitar" : "")}");
        bool ok = shownInst.StartsWith(inst == Instrument.Bass5 ? "Bass (5" : "Guitar") && tuning.Items.Count == Tunings.For(inst).Length
                  && bassShown == PedalboardSettings.IsBass(inst) && guitarShown != bassShown
                  && (type == null ? shownType.StartsWith("(choose") && !Get<ComboBox>("pedalInputCombo").Enabled : !shownType.StartsWith("("));
        Check(ok, $"Pedalboard tab, {type?.ToString() ?? "input not chosen"} / {inst}: instrument, tunings, presets and input shown right");
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(outDir, $"pedalboard_{type?.ToString() ?? "unchosen"}_{inst}.png"));
        }
        form.Close();
    }
}
Check((File.Exists(settingsFile) ? File.GetLastWriteTimeUtc(settingsFile) : (DateTime?)null) == stamp, "settings.json untouched by the window test");

// ---- F) live and muted ----
{
    using var en = new MMDeviceEnumerator();
    var inputs = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Where(x => !VbCable.IsCable(x.FriendlyName)).ToList();
    var output = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var cableDev = inputs.FirstOrDefault(x => x.FriendlyName.Contains("Rocksmith"));
    var other = inputs.FirstOrDefault(x => !x.FriendlyName.Contains("Rocksmith"));
    var feed = new AudioFeed();
    if (cableDev != null && !ProcessUtil.IsRocksmithRunning())
    {
        foreach (var inst in new[] { Instrument.Guitar6, Instrument.Bass4 })
        {
            using var p = new Pedalboard(cableDev, output, new PedalboardSettings { Instrument = inst }.Sanitized(), 0, feed, mute: true);
            Thread.Sleep(800); p.TakeMeters(); Thread.Sleep(1200);   // a muted chain fades to 0 over its first ~0.3 s of audio: discard that
            var m = p.TakeMeters();
            Console.WriteLine($"      cable, {inst}: packets {p.CaptureMs} ms (low-latency {p.LowLatencyInput}), buffer {p.BufferTargetMs} ms, lag {p.LagMs:0.#} ms, raw {p.RawInput}, out {m.Out}, fault {p.Fault ?? "none"}");
            Check(p.Fault == null && p.CaptureMs == 10 && !p.LowLatencyInput && m.Out == 0,
                  $"Real Tone cable, {inst}: runs; 10 ms only, so the normal path, exactly as before; muted output exactly 0");
        }
    }
    else Console.WriteLine("      (Real Tone cable not plugged in, or Rocksmith running: cable run skipped)");
    if (other != null)
    {
        using var p = new Pedalboard(other, output, new PedalboardSettings().Sanitized(), 0, feed, mute: true, channel: GuitarChannel.Auto);
        Thread.Sleep(800); p.TakeMeters(); Thread.Sleep(1200);
        var m = p.TakeMeters();
        Console.WriteLine($"      {other.FriendlyName}, channel Auto: packets {p.CaptureMs} ms (low-latency {p.LowLatencyInput}), fault {p.Fault ?? "none"}");
        Check(p.Fault == null && m.Out == 0, "pedalboard on another input (stand-in for an interface or jack): runs, muted output exactly 0");
        var cableIn = VbCable.FindCableInput(en);
        if (cableIn != null)
        {
            var spec = new MicInputSpec(other, 1f, AlwaysLive: true, Raw: true, Channel: GuitarChannel.Left);
            using var mixer = new MicMixer(cableIn, new[] { spec }, MicMode.Mix, new VoiceFxSettings(), 0) { TestOutputGain = 0 };
            Thread.Sleep(1500);
            Check(mixer.Fault == null, "mixer with a guitar input on one channel: runs (into VB-CABLE, at zero output gain)");
        }
    }
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
