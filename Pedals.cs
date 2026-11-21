using System.Text.Json.Serialization;

namespace AudioMonitor;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModType { Chorus, Flanger, Tremolo }

/// <summary>How the guitar reaches the PC. Decides which device list, channel choice and help the Pedalboard shows.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuitarInputType
{
    /// <summary>The Rocksmith Real Tone cable (a USB instrument input). Pauses while Rocksmith runs.</summary>
    RealToneCable,
    /// <summary>An audio interface with an instrument (Hi-Z) input, usually USB.</summary>
    AudioInterface,
    /// <summary>The PC's own line-in or mic-in jack.</summary>
    PcJack,
}

/// <summary>Which channel of a stereo input carries the guitar.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuitarChannel
{
    /// <summary>The clearly louder side (a mono guitar cable in a stereo jack usually feeds the left only); both if similar.</summary>
    Auto,
    Left,
    Right,
    /// <summary>The average of both sides (the Real Tone cable sends the same signal on both).</summary>
    Both,
}

/// <summary>The instrument on the Pedalboard. Guitar and bass each keep their own pedal sound; the two basses share one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Instrument
{
    /// <summary>6-string guitar.</summary>
    Guitar6,
    /// <summary>4-string bass.</summary>
    Bass4,
    /// <summary>5-string bass (low B).</summary>
    Bass5,
}

/// <summary>
/// The pedal knobs: one sound. <see cref="PedalboardSettings"/> holds the sound in use (flat, as before 2.0.0) and
/// keeps the other instrument family's sound here, so switching guitar / bass brings that sound back.
/// </summary>
public sealed class PedalTone
{
    public int MasterDb { get; set; }
    public bool CompOn { get; set; }
    public int CompSustain { get; set; } = 40;
    public int CompLevelDb { get; set; }
    public bool BoostOn { get; set; } = true;
    public int BoostDb { get; set; } = 6;
    public bool DriveOn { get; set; }
    public int DriveGain { get; set; } = 40;
    public int DriveTone { get; set; } = 50;
    public int DriveLevelDb { get; set; } = -6;
    public bool AmpOn { get; set; }
    public int AmpBass { get; set; } = 50;
    public int AmpMid { get; set; } = 50;
    public int AmpTreble { get; set; } = 50;
    public bool ModOn { get; set; }
    public ModType ModType { get; set; } = ModType.Chorus;
    public int ModRate { get; set; } = 30;
    public int ModDepth { get; set; } = 50;
    public bool DelayOn { get; set; }
    public int DelayMs { get; set; } = 350;
    public int DelayFeedback { get; set; } = 35;
    public int DelayMix { get; set; } = 30;
    public bool ReverbOn { get; set; }
    public int ReverbSize { get; set; } = 50;
    public int ReverbMix { get; set; } = 25;

    public static PedalTone From(PedalboardSettings s) => new()
    {
        MasterDb = s.MasterDb, CompOn = s.CompOn, CompSustain = s.CompSustain, CompLevelDb = s.CompLevelDb,
        BoostOn = s.BoostOn, BoostDb = s.BoostDb, DriveOn = s.DriveOn, DriveGain = s.DriveGain, DriveTone = s.DriveTone,
        DriveLevelDb = s.DriveLevelDb, AmpOn = s.AmpOn, AmpBass = s.AmpBass, AmpMid = s.AmpMid, AmpTreble = s.AmpTreble,
        ModOn = s.ModOn, ModType = s.ModType, ModRate = s.ModRate, ModDepth = s.ModDepth, DelayOn = s.DelayOn,
        DelayMs = s.DelayMs, DelayFeedback = s.DelayFeedback, DelayMix = s.DelayMix, ReverbOn = s.ReverbOn,
        ReverbSize = s.ReverbSize, ReverbMix = s.ReverbMix,
    };

    public void ApplyTo(PedalboardSettings s)
    {
        s.MasterDb = MasterDb; s.CompOn = CompOn; s.CompSustain = CompSustain; s.CompLevelDb = CompLevelDb;
        s.BoostOn = BoostOn; s.BoostDb = BoostDb; s.DriveOn = DriveOn; s.DriveGain = DriveGain; s.DriveTone = DriveTone;
        s.DriveLevelDb = DriveLevelDb; s.AmpOn = AmpOn; s.AmpBass = AmpBass; s.AmpMid = AmpMid; s.AmpTreble = AmpTreble;
        s.ModOn = ModOn; s.ModType = ModType; s.ModRate = ModRate; s.ModDepth = ModDepth; s.DelayOn = DelayOn;
        s.DelayMs = DelayMs; s.DelayFeedback = DelayFeedback; s.DelayMix = DelayMix; s.ReverbOn = ReverbOn;
        s.ReverbSize = ReverbSize; s.ReverbMix = ReverbMix;
    }

    /// <summary>Same limits as the knobs (the file is user-editable).</summary>
    public PedalTone Sanitized()
    {
        var s = new PedalboardSettings();
        ApplyTo(s);
        s.Sanitized();
        return From(s);
    }

    /// <summary>A preset's starting point: everything off, keeping the boost amount and master volume you set.</summary>
    public static PedalTone PresetBase(PedalboardSettings current) =>
        new() { MasterDb = current.MasterDb, BoostDb = current.BoostDb, BoostOn = false };

    /// <summary>The bass sound before any bass knob was turned: the basic "Clean" bass preset.</summary>
    public static PedalTone BassDefault()
    {
        var t = new PedalTone { BoostOn = false };
        BassPresets.Clean(t);
        return t;
    }
}

/// <summary>Basic bass sounds (shared by the default bass sound and the bass preset buttons).</summary>
public static class BassPresets
{
    public static void Clean(PedalTone t) { t.CompOn = true; t.CompSustain = 20; }
    public static void Warm(PedalTone t)
    {
        t.CompOn = true; t.CompSustain = 35;
        t.AmpOn = true; t.AmpBass = 62; t.AmpMid = 45; t.AmpTreble = 40;
    }
    public static void Growl(PedalTone t)
    {
        t.CompOn = true; t.CompSustain = 30;
        t.DriveOn = true; t.DriveGain = 35; t.DriveTone = 45; t.DriveLevelDb = -6;
        t.AmpOn = true; t.AmpBass = 58; t.AmpMid = 60; t.AmpTreble = 50;
    }
    public static void Slap(PedalTone t)
    {
        t.CompOn = true; t.CompSustain = 60;
        t.AmpOn = true; t.AmpBass = 62; t.AmpMid = 30; t.AmpTreble = 70;
    }
    public static void Pick(PedalTone t)
    {
        t.CompOn = true; t.CompSustain = 40;
        t.AmpOn = true; t.AmpBass = 50; t.AmpMid = 55; t.AmpTreble = 68;
    }
    public static void Dub(PedalTone t)
    {
        t.CompOn = true; t.CompSustain = 45;
        t.AmpOn = true; t.AmpBass = 70; t.AmpMid = 40; t.AmpTreble = 25;
    }
}

/// <summary>A tuning: its name and the target note of each string, low to high, as MIDI note numbers (A4 = 69 = 440 Hz).</summary>
public sealed record Tuning(string Name, int[] Strings)
{
    public static double Hz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
    public override string ToString() => Name;   // shown in the tuning list
}

/// <summary>Tuning presets per instrument. The first one is each instrument's default.</summary>
public static class Tunings
{
    // MIDI numbers: E2 = 40, A2 = 45, D3 = 50, G3 = 55, B3 = 59, E4 = 64; E1 = 28, A1 = 33, D2 = 38, G2 = 43; B0 = 23.
    public static readonly Tuning[] Guitar6 =
    {
        new("E standard", new[] { 40, 45, 50, 55, 59, 64 }),
        new("Drop D", new[] { 38, 45, 50, 55, 59, 64 }),
        new("E♭ standard (half step down)", new[] { 39, 44, 49, 54, 58, 63 }),
        new("D standard", new[] { 38, 43, 48, 53, 57, 62 }),
        new("Drop C", new[] { 36, 43, 48, 53, 57, 62 }),
        new("Open G", new[] { 38, 43, 50, 55, 59, 62 }),
        new("DADGAD", new[] { 38, 45, 50, 55, 57, 62 }),
    };

    public static readonly Tuning[] Bass4 =
    {
        new("E standard", new[] { 28, 33, 38, 43 }),
        new("Drop D", new[] { 26, 33, 38, 43 }),
        new("E♭ standard (half step down)", new[] { 27, 32, 37, 42 }),
        new("D standard", new[] { 26, 31, 36, 41 }),
        new("Drop C", new[] { 24, 31, 36, 41 }),
    };

    public static readonly Tuning[] Bass5 =
    {
        new("B standard (B E A D G)", new[] { 23, 28, 33, 38, 43 }),
        new("Drop A", new[] { 21, 28, 33, 38, 43 }),
        new("Half step down", new[] { 22, 27, 32, 37, 42 }),
    };

    public static Tuning[] For(Instrument instrument) => instrument switch
    {
        Instrument.Bass4 => Bass4,
        Instrument.Bass5 => Bass5,
        _ => Guitar6,
    };

    /// <summary>The named tuning for this instrument, or its default when the name is unknown.</summary>
    public static Tuning Find(Instrument instrument, string? name) =>
        For(instrument).FirstOrDefault(t => t.Name == name) ?? For(instrument)[0];
}

/// <summary>Pedalboard settings. Every value is clamped by <see cref="Sanitized"/> (the file is user-editable).</summary>
public sealed class PedalboardSettings
{
    public bool Enabled { get; set; }
    /// <summary>
    /// How the guitar is connected. Null = not chosen yet (a new install: the app picks the Real Tone cable as soon as
    /// one is connected, otherwise the user chooses). Settings from before 2.0.0 are worked out once on load.
    /// </summary>
    public GuitarInputType? InputType { get; set; }
    /// <summary>Real Tone cable to use. Empty = automatic: the Rocksmith cable, if connected.</summary>
    public string InputDeviceId { get; set; } = "";
    /// <summary>Audio interface or PC jack input to use (one choice for both types). Empty = not chosen yet.</summary>
    public string ExternalInputDeviceId { get; set; } = "";
    /// <summary>Channel carrying the guitar on an audio interface or PC jack (the Real Tone cable always uses both).</summary>
    public GuitarChannel InputChannel { get; set; } = GuitarChannel.Auto;
    /// <summary>Where the guitar is played. Empty = the Windows default output (follows changes).</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>The instrument played. Its family's sound is in the flat knob values below.</summary>
    public Instrument Instrument { get; set; } = Instrument.Guitar6;
    /// <summary>The guitar sound while a bass is chosen (null = never switched: the flat values are the guitar sound).</summary>
    public PedalTone? GuitarTone { get; set; }
    /// <summary>The bass sound while the guitar is chosen (null = never set: starts from the basic bass preset).</summary>
    public PedalTone? BassTone { get; set; }
    /// <summary>Chosen tuning preset per instrument (a name from <see cref="Tunings"/>; unknown = the default).</summary>
    public string GuitarTuning { get; set; } = "";
    public string Bass4Tuning { get; set; } = "";
    public string Bass5Tuning { get; set; } = "";

    // ---- the sound in use (the current instrument family's) ----
    public int MasterDb { get; set; }                     // -30..+12

    // No noise gate since 1.4.4, by design: nothing cuts the signal or the fade of a note (a faint hum stays). Old settings
    // files may still hold GateOn / GateThresholdDb: the JSON loader skips unknown keys and the next save drops them.
    public bool CompOn { get; set; }
    public int CompSustain { get; set; } = 40;           // 0..100
    public int CompLevelDb { get; set; }                 // -12..+12
    public bool BoostOn { get; set; } = true;
    public int BoostDb { get; set; } = 6;                // 0..30
    public bool DriveOn { get; set; }
    public int DriveGain { get; set; } = 40;             // 0..100
    public int DriveTone { get; set; } = 50;             // 0..100
    public int DriveLevelDb { get; set; } = -6;          // -24..+6
    public bool AmpOn { get; set; }
    public int AmpBass { get; set; } = 50;               // 0..100 (50 = flat)
    public int AmpMid { get; set; } = 50;
    public int AmpTreble { get; set; } = 50;
    public bool ModOn { get; set; }
    public ModType ModType { get; set; } = ModType.Chorus;
    public int ModRate { get; set; } = 30;               // 0..100
    public int ModDepth { get; set; } = 50;              // 0..100
    public bool DelayOn { get; set; }
    public int DelayMs { get; set; } = 350;              // 50..1000
    public int DelayFeedback { get; set; } = 35;         // 0..90 %
    public int DelayMix { get; set; } = 30;              // 0..100 %
    public bool ReverbOn { get; set; }
    public int ReverbSize { get; set; } = 50;            // 0..100
    public int ReverbMix { get; set; } = 25;             // 0..100 %

    public PedalboardSettings Sanitized()
    {
        if (InputType is { } type && !Enum.IsDefined(type)) InputType = null;
        InputDeviceId ??= "";
        ExternalInputDeviceId ??= "";
        if (!Enum.IsDefined(InputChannel)) InputChannel = GuitarChannel.Auto;
        OutputDeviceId ??= "";
        if (!Enum.IsDefined(Instrument)) Instrument = Instrument.Guitar6;
        GuitarTone = GuitarTone?.Sanitized();
        BassTone = BassTone?.Sanitized();
        GuitarTuning = Tunings.Find(Instrument.Guitar6, GuitarTuning).Name;
        Bass4Tuning = Tunings.Find(Instrument.Bass4, Bass4Tuning).Name;
        Bass5Tuning = Tunings.Find(Instrument.Bass5, Bass5Tuning).Name;
        MasterDb = Math.Clamp(MasterDb, -30, 12);
        CompSustain = Math.Clamp(CompSustain, 0, 100);
        CompLevelDb = Math.Clamp(CompLevelDb, -12, 12);
        BoostDb = Math.Clamp(BoostDb, 0, 30);
        DriveGain = Math.Clamp(DriveGain, 0, 100);
        DriveTone = Math.Clamp(DriveTone, 0, 100);
        DriveLevelDb = Math.Clamp(DriveLevelDb, -24, 6);
        AmpBass = Math.Clamp(AmpBass, 0, 100);
        AmpMid = Math.Clamp(AmpMid, 0, 100);
        AmpTreble = Math.Clamp(AmpTreble, 0, 100);
        if (!Enum.IsDefined(ModType)) ModType = ModType.Chorus;
        ModRate = Math.Clamp(ModRate, 0, 100);
        ModDepth = Math.Clamp(ModDepth, 0, 100);
        DelayMs = Math.Clamp(DelayMs, 50, 1000);
        DelayFeedback = Math.Clamp(DelayFeedback, 0, 90);
        DelayMix = Math.Clamp(DelayMix, 0, 100);
        ReverbSize = Math.Clamp(ReverbSize, 0, 100);
        ReverbMix = Math.Clamp(ReverbMix, 0, 100);
        return this;
    }

    public PedalboardSettings Clone() => (PedalboardSettings)MemberwiseClone();

    /// <summary>Connection type for the audio paths (the Real Tone cable while not chosen yet).</summary>
    public GuitarInputType Type => InputType ?? GuitarInputType.RealToneCable;

    /// <summary>The guitar device ID chosen for the current connection type ("" = automatic cable / not chosen).</summary>
    public string ChosenInputId => Type == GuitarInputType.RealToneCable ? InputDeviceId : ExternalInputDeviceId;

    /// <summary>Channel to use: always both for the Real Tone cable (exactly as before 2.0.0), else the choice.</summary>
    public GuitarChannel EffectiveChannel => Type == GuitarInputType.RealToneCable ? GuitarChannel.Both : InputChannel;

    public static bool IsBass(Instrument i) => i is Instrument.Bass4 or Instrument.Bass5;
    public bool Bass => IsBass(Instrument);

    /// <summary>
    /// Switches the instrument. Between guitar and bass, the sound in use is kept for the old family and the new
    /// family's sound comes back (the first time on bass: the basic bass preset). 4- and 5-string share one sound.
    /// </summary>
    public void SwitchInstrument(Instrument to)
    {
        if (!Enum.IsDefined(to) || to == Instrument) return;
        bool wasBass = Bass, toBass = IsBass(to);
        if (wasBass != toBass)
        {
            var current = PedalTone.From(this);
            if (wasBass) BassTone = current; else GuitarTone = current;
            var next = (toBass ? BassTone : GuitarTone) ?? (toBass ? PedalTone.BassDefault() : new PedalTone());
            next.ApplyTo(this);
        }
        Instrument = to;
    }

    /// <summary>The chosen tuning of the current instrument.</summary>
    public Tuning CurrentTuning => Tunings.Find(Instrument, Instrument switch
    {
        Instrument.Bass4 => Bass4Tuning,
        Instrument.Bass5 => Bass5Tuning,
        _ => GuitarTuning,
    });

    public void SetTuning(string name)
    {
        string valid = Tunings.Find(Instrument, name).Name;
        if (Instrument == Instrument.Bass4) Bass4Tuning = valid;
        else if (Instrument == Instrument.Bass5) Bass5Tuning = valid;
        else GuitarTuning = valid;
    }
}

/// <summary>
/// Turns captured 32-bit float frames into the mono guitar signal, using the chosen channel. Both = the average of
/// the first two channels, computed exactly as before 2.0.0. Auto follows each side's level (a few hundred ms) and
/// uses one side when it's clearly louder (a mono guitar cable in a stereo jack: guitar on the left, the right
/// silent or just noise), both when they're similar; changes glide over 10 ms, so they never click.
/// One instance per capture stream (it keeps state); runs on the capture thread.
/// </summary>
sealed class ChannelPicker
{
    const float SelectRatio = 4f;     // mean-square ratio: one side 6 dB louder -> use it alone
    const float ReleaseRatio = 2f;    // back to both once it's within 3 dB
    readonly GuitarChannel mode;
    readonly float envCoef, glide;
    float envL, envR;
    float wl = 0.5f, wr = 0.5f;       // current weights of left/right
    GuitarChannel autoChoice = GuitarChannel.Both;

    public ChannelPicker(GuitarChannel mode, int sampleRate)
    {
        this.mode = Enum.IsDefined(mode) ? mode : GuitarChannel.Both;
        envCoef = 1 - MathF.Exp(-1f / (0.3f * sampleRate));
        glide = 1f / (0.01f * sampleRate);
        if (this.mode == GuitarChannel.Left) (wl, wr) = (1, 0);
        else if (this.mode == GuitarChannel.Right) (wl, wr) = (0, 1);
    }

    /// <summary>For tests and diagnostics: the side Auto currently uses (Left, Right or Both).</summary>
    public GuitarChannel AutoChoice => autoChoice;

    /// <summary><paramref name="data"/> holds <paramref name="frames"/> frames of 32-bit float, <paramref name="channels"/> per frame.</summary>
    public void ToMono(byte[] data, int frames, int channels, float[] mono)
    {
        int stride = 4 * channels;
        if (channels < 2)
        {
            for (int f = 0; f < frames; f++) mono[f] = BitConverter.ToSingle(data, f * stride);
            return;
        }
        if (mode == GuitarChannel.Both)
        {
            // Unchanged from before 2.0.0 (bit for bit): the average of the first two channels.
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < 2; c++) sum += BitConverter.ToSingle(data, f * stride + c * 4);
                mono[f] = sum / 2;
            }
            return;
        }
        float tl = mode == GuitarChannel.Left ? 1 : mode == GuitarChannel.Right ? 0 : Target(autoChoice).L;
        float tr = 1 - tl;
        for (int f = 0; f < frames; f++)
        {
            float l = BitConverter.ToSingle(data, f * stride), r = BitConverter.ToSingle(data, f * stride + 4);
            if (mode == GuitarChannel.Auto)
            {
                envL += (l * l - envL) * envCoef;
                envR += (r * r - envR) * envCoef;
                if ((f & 63) == 0) { Decide(); (tl, tr) = Target(autoChoice); }
            }
            wl = wl < tl ? Math.Min(tl, wl + glide) : Math.Max(tl, wl - glide);
            wr = wr < tr ? Math.Min(tr, wr + glide) : Math.Max(tr, wr - glide);
            mono[f] = wl * l + wr * r;
        }
    }

    static (float L, float R) Target(GuitarChannel side) => side switch
    {
        GuitarChannel.Left => (1, 0),
        GuitarChannel.Right => (0, 1),
        _ => (0.5f, 0.5f),
    };

    void Decide()
    {
        if (envL + envR < 1e-12f) return;   // digital silence: keep the current choice
        autoChoice = autoChoice switch
        {
            GuitarChannel.Left => envL < envR * ReleaseRatio ? GuitarChannel.Both : GuitarChannel.Left,
            GuitarChannel.Right => envR < envL * ReleaseRatio ? GuitarChannel.Both : GuitarChannel.Right,
            _ => envL > envR * SelectRatio ? GuitarChannel.Left : envR > envL * SelectRatio ? GuitarChannel.Right : GuitarChannel.Both,
        };
    }
}

/// <summary>
/// The guitar signal chain, mono float in place: compressor → boost → drive → amp + cabinet →
/// modulation → delay → reverb → master → safety limiter. Settings can change at any moment (the UI swaps in
/// a new snapshot). Pedals fade in/out over ~10 ms when switched, and gains glide, so nothing clicks.
/// The limiter (1 ms look-ahead, ceiling -1 dBFS) means the output can never clip, whatever the settings.
/// </summary>
sealed class PedalChain
{
    public const float Ceiling = 0.891f;   // -1 dBFS

    readonly int rate;
    volatile PedalboardSettings settings;
    PedalboardSettings last;

    readonly Compressor comp;
    readonly Drive drive;
    readonly AmpCab amp;
    readonly Modulation mod;
    readonly DelayPedal delay;
    readonly PedalReverb reverb;
    readonly Limiter limiter;
    readonly Smoother compMix = new(), boostMix = new(), driveMix = new(), ampMix = new(),
                      modMix = new(), delayMix = new(), reverbMix = new(), boostGain = new(1), master = new(1);

    float inPeak, outPeak;
    int limitingSamples;
    volatile bool inputClipped;
    public volatile bool Mute;

    public PedalChain(int sampleRate, PedalboardSettings s)
    {
        rate = sampleRate;
        settings = last = s.Clone();
        comp = new Compressor(rate);
        drive = new Drive(rate);
        amp = new AmpCab(rate);
        mod = new Modulation(rate);
        delay = new DelayPedal(rate);
        reverb = new PedalReverb(rate);
        limiter = new Limiter(rate);
        foreach (var m in new[] { compMix, boostMix, driveMix, ampMix, modMix, delayMix, reverbMix }) m.Coef = Smoother.CoefFor(10, rate);
        boostGain.Coef = master.Coef = Smoother.CoefFor(20, rate);
        SetInitial(s);
    }

    void SetInitial(PedalboardSettings s)
    {
        // Start at the current settings instead of fading everything in.
        compMix.Value = s.CompOn ? 1 : 0; boostMix.Value = s.BoostOn ? 1 : 0;
        driveMix.Value = s.DriveOn ? 1 : 0; ampMix.Value = s.AmpOn ? 1 : 0; modMix.Value = s.ModOn ? 1 : 0;
        delayMix.Value = s.DelayOn ? 1 : 0; reverbMix.Value = s.ReverbOn ? 1 : 0;
        boostGain.Value = DbToGain(s.BoostDb);
        master.Value = DbToGain(s.MasterDb);
        drive.Configure(s);
        amp.Configure(s);
    }

    /// <summary>Apply new settings live (thread-safe).</summary>
    public void Update(PedalboardSettings s) => settings = s.Clone();

    public static float DbToGain(double db) => (float)Math.Pow(10, db / 20);

    public void Process(float[] x, int count)
    {
        var s = settings;
        if (!ReferenceEquals(s, last))
        {
            // A pedal switched (back) on starts clean, instead of replaying an old tail.
            if (s.DelayOn && !last.DelayOn && delayMix.Value < 0.001f) delay.Clear();
            if (s.ReverbOn && !last.ReverbOn && reverbMix.Value < 0.001f) reverb.Clear();
            if (s.ModOn && !last.ModOn && modMix.Value < 0.001f) mod.Clear();
            if (s.Bass != last.Bass) master.Value = 0;   // guitar <-> bass: the voicing changes, fade back in (20 ms)
            drive.Configure(s);
            amp.Configure(s);
            last = s;
        }

        float boostTarget = DbToGain(s.BoostDb), masterTarget = Mute ? 0 : DbToGain(s.MasterDb);
        float delayMixAmount = s.DelayMix / 100f, delayFeedback = s.DelayFeedback / 100f;
        float reverbSize = s.ReverbSize / 100f, reverbMixAmount = s.ReverbMix / 100f;
        float modRate = s.ModRate / 100f, modDepth = s.ModDepth / 100f;
        int delaySamples = s.DelayMs * rate / 1000;
        float inP = 0, outP = 0;
        int limiting = 0;
        bool clipped = false;

        for (int i = 0; i < count; i++)
        {
            float v = x[i];
            float a = Math.Abs(v);
            if (a > inP) inP = a;
            if (a >= 0.999f) clipped = true;

            float m = compMix.Next(s.CompOn ? 1 : 0);
            if (m > 0.0001f) v += (comp.Process(v, s.CompSustain, s.CompLevelDb) - v) * m; else comp.Idle(v);
            m = boostMix.Next(s.BoostOn ? 1 : 0);
            float g = boostGain.Next(boostTarget);
            v *= 1 + (g - 1) * m;
            m = driveMix.Next(s.DriveOn ? 1 : 0);
            if (m > 0.0001f) v += (drive.Process(v) - v) * m;
            m = ampMix.Next(s.AmpOn ? 1 : 0);
            if (m > 0.0001f) v += (amp.Process(v) - v) * m;
            m = modMix.Next(s.ModOn ? 1 : 0);
            if (m > 0.0001f) v += (mod.Process(v, s.ModType, modRate, modDepth) - v) * m;
            m = delayMix.Next(s.DelayOn ? 1 : 0);
            if (m > 0.0001f) v += (delay.Process(v, delaySamples, delayFeedback, delayMixAmount) - v) * m;
            m = reverbMix.Next(s.ReverbOn ? 1 : 0);
            if (m > 0.0001f) v += (reverb.Process(v, reverbSize, reverbMixAmount) - v) * m;
            v *= master.Next(masterTarget);

            v = limiter.Process(v);
            if (limiter.Limiting) limiting++;
            if (!float.IsFinite(v)) { Reset(); v = 0; }   // never let a bad value through
            x[i] = v;
            a = Math.Abs(v);
            if (a > outP) outP = a;
        }

        if (inP > Volatile.Read(ref inPeak)) Volatile.Write(ref inPeak, inP);
        if (outP > Volatile.Read(ref outPeak)) Volatile.Write(ref outPeak, outP);
        if (limiting > 0) Interlocked.Add(ref limitingSamples, limiting);
        if (clipped) inputClipped = true;
    }

    void Reset()
    {
        delay.Clear(); reverb.Clear(); mod.Clear(); drive.Clear(); amp.Clear(); limiter.Clear(); comp.Clear();
    }

    /// <summary>Loudest raw input and output sample since the last call (0..1).</summary>
    public (float In, float Out, bool Limiting, bool InputClipped) TakeMeters()
    {
        bool clipped = inputClipped;
        inputClipped = false;
        return (Interlocked.Exchange(ref inPeak, 0), Interlocked.Exchange(ref outPeak, 0),
                Interlocked.Exchange(ref limitingSamples, 0) > rate / 200, clipped);   // > 5 ms of limiting
    }

    // ---------------- building blocks ----------------

    internal sealed class Smoother
    {
        public float Value;
        public float Coef = 0.002f;
        public Smoother(float value = 0) => Value = value;
        public static float CoefFor(double ms, int rate) => (float)(1 - Math.Exp(-1000.0 / (ms * rate)));
        public float Next(float target)
        {
            float d = target - Value;
            Value = Math.Abs(d) < 1e-6f ? target : Value + d * Coef;
            return Value;
        }
    }

    /// <summary>RBJ "Audio EQ Cookbook" biquad, transposed direct form II, double precision.</summary>
    internal sealed class Biquad
    {
        double b0 = 1, b1, b2, a1, a2, z1, z2;

        public float Process(float x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return (float)y;
        }

        public void Clear() { z1 = z2 = 0; }

        void Set(double nb0, double nb1, double nb2, double na0, double na1, double na2)
        {
            b0 = nb0 / na0; b1 = nb1 / na0; b2 = nb2 / na0; a1 = na1 / na0; a2 = na2 / na0;
        }

        static (double cos, double alpha) W(double fs, double f, double q)
        {
            double w = 2 * Math.PI * Math.Min(f, fs * 0.45) / fs;
            return (Math.Cos(w), Math.Sin(w) / (2 * q));
        }

        public Biquad LowPass(double fs, double f, double q)
        {
            var (c, al) = W(fs, f, q);
            Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
            return this;
        }

        public Biquad HighPass(double fs, double f, double q)
        {
            var (c, al) = W(fs, f, q);
            Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
            return this;
        }

        public Biquad Peak(double fs, double f, double q, double db)
        {
            var (c, al) = W(fs, f, q);
            double A = Math.Pow(10, db / 40);
            Set(1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A);
            return this;
        }

        public Biquad LowShelf(double fs, double f, double db)
        {
            var (c, al) = W(fs, f, Math.Sqrt(0.5));   // shelf slope 1
            double A = Math.Pow(10, db / 40), s = 2 * Math.Sqrt(A) * al;
            Set(A * ((A + 1) - (A - 1) * c + s), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - s),
                (A + 1) + (A - 1) * c + s, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - s);
            return this;
        }

        public Biquad HighShelf(double fs, double f, double db)
        {
            var (c, al) = W(fs, f, Math.Sqrt(0.5));
            double A = Math.Pow(10, db / 40), s = 2 * Math.Sqrt(A) * al;
            Set(A * ((A + 1) + (A - 1) * c + s), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - s),
                (A + 1) - (A - 1) * c + s, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - s);
            return this;
        }
    }

    /// <summary>
    /// Sustain compressor (ratio 4:1, attack 5 ms, release 150 ms). "Sustain" lowers the threshold; the make-up
    /// gain keeps a firm note (-18 dBFS) at the same level, so quieter notes and tails come up.
    /// </summary>
    internal sealed class Compressor
    {
        const double Ratio = 4;
        readonly float attack, release;
        float env;

        public Compressor(int rate)
        {
            attack = Smoother.CoefFor(5, rate);
            release = Smoother.CoefFor(150, rate);
        }

        public void Clear() => env = 0;
        public void Idle(float x) { float a = Math.Abs(x); env += (a - env) * (a > env ? attack : release); }

        public float Process(float x, int sustain, int levelDb)
        {
            Idle(x);
            double threshold = -20 - sustain * 0.3;   // -20 .. -50 dBFS
            double level = 20 * Math.Log10(env + 1e-9);
            double reduction = level > threshold ? (level - threshold) * (1 - 1 / Ratio) : 0;
            double makeup = Math.Max(0, (-18 - threshold) * (1 - 1 / Ratio));
            return x * DbToGain(makeup - reduction + levelDb);
        }
    }

    /// <summary>
    /// Overdrive/distortion: tightens the lows and pushes the mids, then an asymmetric soft clipper running at
    /// 4x the sample rate (so the distortion doesn't fold back as harsh aliasing), then a tone control.
    /// Gain 0 = barely breaks up, 100 = heavy distortion (+50 dB into the clipper). Adds ~0.5 ms.
    /// </summary>
    internal sealed class Drive
    {
        const int Taps = 96, Phases = 4, PhaseTaps = Taps / Phases;
        const float Bias = 0.12f;
        static readonly float[] Fir = DesignFir();
        static readonly float BiasOffset = MathF.Tanh(Bias);

        readonly int rate;
        readonly float[] upHistory = new float[PhaseTaps * 2];
        readonly float[] downHistory = new float[Taps * 2];
        int upPos, downPos;
        readonly Biquad tighten = new(), mids = new(), tone = new();
        // Bass: a 150 Hz Linkwitz-Riley crossover (2nd order; the high band is inverted, so the two bands add up flat).
        // Only the high band is driven: the low notes stay clean and full instead of being cut or smeared.
        readonly Biquad splitLow = new(), splitHigh = new();
        float dcIn, dcOut;
        float k = 1, level = 1;
        int lastGain = -1, lastTone = -1, lastLevel = int.MinValue;
        bool bass;

        public Drive(int rate)
        {
            this.rate = rate;
            tighten.HighPass(rate, 110, 0.6);
            mids.Peak(rate, 800, 0.8, 5);
            splitLow.LowPass(rate, 150, 0.5);
            splitHigh.HighPass(rate, 150, 0.5);
        }

        public void Configure(PedalboardSettings s)
        {
            if (s.Bass != bass) { bass = s.Bass; Clear(); }
            if (s.DriveGain != lastGain) { k = DbToGain(s.DriveGain * 0.5); lastGain = s.DriveGain; }
            if (s.DriveTone != lastTone) { tone.LowPass(rate, 1200 * Math.Pow(6, s.DriveTone / 100.0), 0.7); lastTone = s.DriveTone; }
            if (s.DriveLevelDb != lastLevel) { level = DbToGain(s.DriveLevelDb); lastLevel = s.DriveLevelDb; }
        }

        public void Clear()
        {
            Array.Clear(upHistory); Array.Clear(downHistory);
            tighten.Clear(); mids.Clear(); tone.Clear(); splitLow.Clear(); splitHigh.Clear();
            dcIn = dcOut = 0;
        }

        public float Process(float x)
        {
            float low = 0;
            if (bass)
            {
                low = splitLow.Process(x);
                x = mids.Process(-splitHigh.Process(x)) * k;
            }
            else x = mids.Process(tighten.Process(x)) * k;

            // Upsample x4 (polyphase FIR), clip each sub-sample, low-pass and keep every 4th.
            upPos = (upPos + PhaseTaps - 1) % PhaseTaps;
            upHistory[upPos] = upHistory[upPos + PhaseTaps] = x;
            for (int p = 0; p < Phases; p++)
            {
                float u = 0;
                for (int t = 0; t < PhaseTaps; t++) u += Fir[t * Phases + p] * upHistory[upPos + t];
                u *= Phases;
                float shaped = MathF.Tanh(u + Bias) - BiasOffset;
                downPos = (downPos + Taps - 1) % Taps;
                downHistory[downPos] = downHistory[downPos + Taps] = shaped;
            }
            float y = 0;
            for (int t = 0; t < Taps; t++) y += Fir[t] * downHistory[downPos + t];

            // The asymmetric curve adds a little DC: block it (1-pole high-pass at ~10 Hz).
            float dc = y - dcIn + 0.9987f * dcOut;
            dcIn = y; dcOut = dc;
            return (tone.Process(dc) + low) * level;   // low = 0 for guitar
        }

        /// <summary>Blackman-windowed sinc low-pass at 4x rate, cutoff at 0.8 x the original Nyquist.</summary>
        static float[] DesignFir()
        {
            var h = new float[Taps];
            double fc = 0.8 * 0.5 / Phases, sum = 0, mid = (Taps - 1) / 2.0;
            for (int n = 0; n < Taps; n++)
            {
                double t = n - mid;
                double sinc = t == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * t) / (Math.PI * t);
                double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / (Taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * n / (Taps - 1));
                h[n] = (float)(sinc * w);
                sum += h[n];
            }
            for (int n = 0; n < Taps; n++) h[n] = (float)(h[n] / sum);
            return h;
        }
    }

    /// <summary>
    /// Amp tone stack (bass 120 Hz shelf, mid 700 Hz, treble 2.8 kHz shelf; ±12 dB each) and a guitar speaker
    /// cabinet voicing: low cut with a resonance bump, a slight low-mid dip, presence peak, steep roll-off
    /// above ~5 kHz (a guitar speaker reproduces almost nothing up there, which is what tames the fizz).
    /// </summary>
    internal sealed class AmpCab
    {
        readonly int rate;
        readonly Biquad bass = new(), mid = new(), treble = new();
        readonly Biquad[] cab = { new(), new(), new(), new(), new(), new() };
        int lastBass = -1, lastMid = -1, lastTreble = -1;
        bool? bassVoicing;
        float makeup = 1.25f;

        // Guitar: tone stack 120 Hz / 700 Hz / 2.8 kHz; a guitar speaker cut below 70 Hz and above ~5 kHz.
        // Bass: tone stack 80 Hz / 500 Hz / 2.5 kHz; a bass cabinet that goes down to ~25 Hz (low B 30.9 Hz, drop A
        // 27.5 Hz) and rolls off above ~4 kHz. Make-up gains keep the same knobs equally loud (measured with test noise).
        double BassHz => bassVoicing == true ? 80 : 120;
        double MidHz => bassVoicing == true ? 500 : 700;
        double TrebleHz => bassVoicing == true ? 2500 : 2800;

        public AmpCab(int rate)
        {
            this.rate = rate;
            Voice(bass: false);
        }

        void Voice(bool bass)
        {
            bassVoicing = bass;
            if (bass)
            {
                cab[0].HighPass(rate, 25, 0.7);
                cab[1].Peak(rate, 60, 1.0, 2);
                cab[2].Peak(rate, 400, 1.0, -2);
                cab[3].Peak(rate, 1800, 1.2, 2);
                cab[4].LowPass(rate, 4000, 0.7);
                cab[5].LowPass(rate, 4000, 0.7);
                makeup = BassMakeup;
            }
            else
            {
                cab[0].HighPass(rate, 70, 0.8);
                cab[1].Peak(rate, 110, 1.4, 3);
                cab[2].Peak(rate, 450, 1.0, -3);
                cab[3].Peak(rate, 2300, 1.2, 3);
                cab[4].LowPass(rate, 5200, 0.7);
                cab[5].LowPass(rate, 5200, 0.7);
                makeup = 1.25f;
            }
            lastBass = lastMid = lastTreble = -1;   // the tone stack moves to the new frequencies
        }

        /// <summary>Bass voicing's fixed make-up gain (calibrated: same knobs, same loudness as the guitar voicing).</summary>
        internal const float BassMakeup = 1.25f;

        static double Db(int knob) => (knob - 50) * 0.24;   // 0..100 -> -12..+12 dB

        public void Configure(PedalboardSettings s)
        {
            if (s.Bass != bassVoicing) { Voice(s.Bass); Clear(); }
            if (s.AmpBass != lastBass) { bass.LowShelf(rate, BassHz, Db(s.AmpBass)); lastBass = s.AmpBass; }
            if (s.AmpMid != lastMid) { mid.Peak(rate, MidHz, 0.9, Db(s.AmpMid)); lastMid = s.AmpMid; }
            if (s.AmpTreble != lastTreble) { treble.HighShelf(rate, TrebleHz, Db(s.AmpTreble)); lastTreble = s.AmpTreble; }
        }

        public void Clear()
        {
            bass.Clear(); mid.Clear(); treble.Clear();
            foreach (var b in cab) b.Clear();
        }

        public float Process(float x)
        {
            x = treble.Process(mid.Process(bass.Process(x)));
            foreach (var b in cab) x = b.Process(x);
            return x * makeup;   // the cabinet's roll-off takes away some loudness; give it back
        }
    }

    /// <summary>Chorus (12-24 ms swept delay), flanger (0.5-4.5 ms with feedback) or tremolo (volume wobble).</summary>
    internal sealed class Modulation
    {
        readonly int rate;
        readonly float[] buf;
        int write;
        double phase;

        public Modulation(int rate)
        {
            this.rate = rate;
            buf = new float[rate / 20];   // 50 ms
        }

        public void Clear() { Array.Clear(buf); phase = 0; }

        public float Process(float x, ModType type, float rate01, float depth01)
        {
            double hz = type switch
            {
                ModType.Chorus => 0.1 * Math.Pow(50, rate01),    // 0.1 .. 5 Hz
                ModType.Flanger => 0.05 * Math.Pow(40, rate01),  // 0.05 .. 2 Hz
                _ => 1 * Math.Pow(12, rate01),                   // 1 .. 12 Hz
            };
            phase += hz / rate;
            if (phase >= 1) phase -= 1;
            float lfo = 0.5f + 0.5f * (float)Math.Sin(2 * Math.PI * phase);

            if (type == ModType.Tremolo) return x * (1 - depth01 * lfo);

            float delayMs = type == ModType.Chorus ? 12 + 12 * depth01 * lfo : 0.5f + 4 * depth01 * lfo;
            float wet = Read(delayMs * rate / 1000f);
            buf[write] = type == ModType.Flanger ? x + wet * 0.6f : x;
            write = (write + 1) % buf.Length;
            return 0.6f * (x + wet);
        }

        float Read(float delay)
        {
            float pos = write - delay;
            if (pos < 0) pos += buf.Length;
            int i0 = (int)pos;
            float frac = pos - i0;
            return buf[i0 % buf.Length] * (1 - frac) + buf[(i0 + 1) % buf.Length] * frac;
        }
    }

    /// <summary>Delay with darkening repeats (each repeat loses some treble, like an analog/tape delay).</summary>
    internal sealed class DelayPedal
    {
        readonly float[] buf;
        readonly float damp;
        int write;
        float time = -1, lowpass;

        public DelayPedal(int rate)
        {
            buf = new float[rate * 1100 / 1000];
            damp = (float)(1 - Math.Exp(-2 * Math.PI * 3500 / rate));
        }

        public void Clear() { Array.Clear(buf); lowpass = 0; }

        public float Process(float x, int delaySamples, float feedback, float mix)
        {
            if (time < 0) time = delaySamples;
            time += (delaySamples - time) * 0.0005f;   // glide (slight pitch bend while turning the knob, like tape)
            float pos = write - time;
            if (pos < 0) pos += buf.Length;
            int i0 = (int)pos;
            float frac = pos - i0;
            float d = buf[i0 % buf.Length] * (1 - frac) + buf[(i0 + 1) % buf.Length] * frac;
            lowpass += (d - lowpass) * damp;
            buf[write] = x + lowpass * feedback;
            write = (write + 1) % buf.Length;
            return x + d * mix;
        }
    }

    /// <summary>Freeverb-style room: 8 damped combs in parallel, then 4 all-passes.</summary>
    internal sealed class PedalReverb
    {
        static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };   // at 44.1 kHz
        static readonly int[] AllPassTunings = { 556, 441, 341, 225 };
        readonly float[][] combs, allPasses;
        readonly int[] combPos, allPassPos;
        readonly float[] combStore;

        public PedalReverb(int rate)
        {
            double scale = rate / 44100.0;
            combs = CombTunings.Select(t => new float[(int)(t * scale)]).ToArray();
            allPasses = AllPassTunings.Select(t => new float[(int)(t * scale)]).ToArray();
            combPos = new int[combs.Length];
            allPassPos = new int[allPasses.Length];
            combStore = new float[combs.Length];
        }

        public void Clear()
        {
            foreach (var c in combs) Array.Clear(c);
            foreach (var a in allPasses) Array.Clear(a);
            Array.Clear(combStore);
        }

        public float Process(float x, float size, float mix)
        {
            // Size 0..1 -> ring-out of roughly 0.5 s (small room) to 2.5 s (big hall).
            float feedback = 0.7f + size * 0.22f, damp = 0.25f, input = x * 0.015f, wet = 0;
            for (int c = 0; c < combs.Length; c++)
            {
                var b = combs[c];
                float y = b[combPos[c]];
                combStore[c] = y * (1 - damp) + combStore[c] * damp;
                b[combPos[c]] = input + combStore[c] * feedback;
                if (++combPos[c] == b.Length) combPos[c] = 0;
                wet += y;
            }
            for (int a = 0; a < allPasses.Length; a++)
            {
                var b = allPasses[a];
                float stored = b[allPassPos[a]];
                b[allPassPos[a]] = wet + stored * 0.5f;
                wet = stored - wet;
                if (++allPassPos[a] == b.Length) allPassPos[a] = 0;
            }
            return x * (1 - mix * 0.3f) + wet * 3f * mix;
        }
    }

    /// <summary>
    /// Brick-wall peak limiter: looks 1 ms ahead so it can turn down smoothly *before* a peak arrives (no
    /// distortion from clipping), then recovers over ~80 ms. Below the ceiling it does nothing at all.
    /// </summary>
    internal sealed class Limiter
    {
        readonly float[] samples, needed;
        readonly float attack, release;
        int pos;
        float gain = 1;
        public bool Limiting { get; private set; }

        public Limiter(int rate)
        {
            int lookahead = rate / 1000;
            samples = new float[lookahead + 1];
            needed = new float[lookahead + 1];
            Array.Fill(needed, 1f);
            attack = (float)(1 - Math.Exp(-4.0 / lookahead));   // ~98 % there within the look-ahead
            release = Smoother.CoefFor(80, rate);
        }

        public void Clear() { Array.Clear(samples); Array.Fill(needed, 1f); gain = 1; pos = 0; }

        public float Process(float x)
        {
            float a = Math.Abs(x);
            samples[pos] = x;
            needed[pos] = a > Ceiling ? Ceiling / a : 1;
            if (++pos == samples.Length) pos = 0;
            float delayed = samples[pos];   // the oldest sample: 1 ms ago
            float target = 1;
            for (int i = 0; i < needed.Length; i++) if (needed[i] < target) target = needed[i];
            gain += (target - gain) * (target < gain ? attack : release);
            Limiting = gain < 0.97f;
            return Math.Clamp(delayed * gain, -Ceiling, Ceiling);
        }
    }
}

/// <summary>Guitar and bass tuner: YIN pitch detection on recent raw input.</summary>
static class Tuner
{
    static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>
    /// Fundamental frequency in Hz, or 0 when there is no clear note (silence, noise, chord). Covers a 5-string
    /// bass's low B (30.9 Hz) and drop A (27.5 Hz) up to the high frets of a guitar (~1400 Hz). Give it the last 8192 samples: one
    /// cycle of a low B lasts 32 ms, so low notes need ~170 ms of sound to be measured.
    /// </summary>
    public static double Detect(float[] input, int count, int rate)
    {
        if (count < 2048) return 0;
        int recent = Math.Min(count, 4096);
        double energy = 0;
        for (int i = count - recent; i < count; i++) energy += input[i] * input[i];
        if (Math.Sqrt(energy / recent) < 0.001) return 0;   // too quiet

        // Low notes (bass, and a guitar's low strings): the whole buffer at a quarter of the sample rate.
        if (count >= 8192)
        {
            double low = Yin(input, count - 8192, 8192, 4, rate, 25, 700);   // 25 Hz: room below drop A (A0 = 27.5 Hz)
            if (low > 0 && low < 170) return low;
        }
        // Higher notes: the last 4096 samples at half rate (finer steps, so more precise up there).
        return Yin(input, count - recent, recent, 2, rate, 55, 1400);
    }

    /// <summary>YIN pitch detection on a decimated stretch of the input.</summary>
    static double Yin(float[] input, int start, int length, int decimate, int rate, double minHz, double maxHz)
    {
        int n = length / decimate;
        if (n < 512) return 0;
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int j = 0; j < decimate; j++) sum += input[start + i * decimate + j];
            x[i] = sum / decimate;
        }
        double fs = rate / (double)decimate;
        int window = n / 2;
        int tauMax = Math.Min(window - 2, (int)(fs / minHz));
        int tauMin = Math.Max(2, (int)(fs / maxHz));
        var d = new double[tauMax + 2];
        for (int tau = 1; tau <= tauMax + 1; tau++)
        {
            double sum = 0;
            for (int j = 0; j < window; j++) { double diff = x[j] - x[j + tau]; sum += diff * diff; }
            d[tau] = sum;
        }
        // Cumulative mean normalized difference; first dip under the threshold wins (avoids octave errors).
        double running = 0;
        var cmnd = new double[tauMax + 2];
        cmnd[0] = 1;
        for (int tau = 1; tau <= tauMax + 1; tau++)
        {
            running += d[tau];
            cmnd[tau] = running > 0 ? d[tau] * tau / running : 1;
        }
        int best = -1;
        for (int tau = tauMin; tau <= tauMax; tau++)
        {
            if (cmnd[tau] < 0.12)
            {
                while (tau + 1 <= tauMax && cmnd[tau + 1] < cmnd[tau]) tau++;
                best = tau;
                break;
            }
        }
        if (best < 0) return 0;
        // Parabolic interpolation for sub-sample accuracy.
        double s0 = cmnd[best - 1], s1 = cmnd[best], s2 = cmnd[best + 1];
        double denom = s0 - 2 * s1 + s2;
        double shift = Math.Abs(denom) > 1e-12 ? 0.5 * (s0 - s2) / denom : 0;
        return fs / (best + Math.Clamp(shift, -1, 1));
    }

    /// <summary>
    /// The string whose target note (MIDI numbers, see <see cref="Tuning"/>) the note played is closest to, and how far
    /// off it is in cents (negative = flat: tune up), or (-1, 0) when no string's target is within a semitone.
    /// </summary>
    public static (int Index, double Cents) NearestString(double hz, int[] targets)
    {
        int best = -1;
        double bestCents = 0;
        if (hz <= 0) return (-1, 0);
        for (int i = 0; i < targets.Length; i++)
        {
            double cents = 1200 * Math.Log2(hz / Tuning.Hz(targets[i]));
            if (Math.Abs(cents) <= 100 && (best < 0 || Math.Abs(cents) < Math.Abs(bestCents))) { best = i; bestCents = cents; }
        }
        return (best, best < 0 ? 0 : bestCents);
    }

    /// <summary>Nearest note name (e.g. "E2") and how far off it is in cents (-50..+50).</summary>
    public static (string Note, double Cents) Describe(double hz)
    {
        double midi = 69 + 12 * Math.Log2(hz / 440);
        int nearest = (int)Math.Round(midi);
        return (Names[((nearest % 12) + 12) % 12] + (nearest / 12 - 1), (midi - nearest) * 100);
    }
}
