using NAudio.Wave;
using Biquad = AudioMonitor.PedalChain.Biquad;

namespace AudioMonitor;

/// <summary>Fun box settings. Every value is clamped by <see cref="Sanitized"/> (the file is user-editable).</summary>
public sealed class VoiceFxSettings
{
    public bool Enabled { get; set; }
    public bool PitchOn { get; set; }
    public int PitchSemitones { get; set; } = 5;     // -12..+12
    public bool UnderwaterOn { get; set; }
    public int UnderwaterDepth { get; set; } = 50;   // 0..100
    public bool RadioOn { get; set; }
    public int RadioDrive { get; set; } = 50;        // 0..100
    public bool TelephoneOn { get; set; }
    public int TelephoneGrit { get; set; } = 40;     // 0..100
    public bool DemonOn { get; set; }
    public int DemonDepth { get; set; } = 50;        // 0..100
    public bool GhostOn { get; set; }
    public int GhostAmount { get; set; } = 50;       // 0..100
    public bool EchoOn { get; set; }
    public int EchoMs { get; set; } = 300;           // 100..800
    public bool ReverbOn { get; set; }
    public int ReverbSize { get; set; } = 50;        // 0..100
    /// <summary>Mission control preset: Radio becomes a military walkie-talkie, with key-up click and squelch tail.</summary>
    public bool WalkieTalkie { get; set; }
    /// <summary>Overall volume of everything the mixer sends (mics, guitar, effects), in percent: 100 = unchanged.</summary>
    public int Volume { get; set; } = 100;           // 0..200

    public VoiceFxSettings Sanitized()
    {
        PitchSemitones = Math.Clamp(PitchSemitones, -12, 12);
        UnderwaterDepth = Math.Clamp(UnderwaterDepth, 0, 100);
        RadioDrive = Math.Clamp(RadioDrive, 0, 100);
        TelephoneGrit = Math.Clamp(TelephoneGrit, 0, 100);
        DemonDepth = Math.Clamp(DemonDepth, 0, 100);
        GhostAmount = Math.Clamp(GhostAmount, 0, 100);
        EchoMs = Math.Clamp(EchoMs, 100, 800);
        ReverbSize = Math.Clamp(ReverbSize, 0, 100);
        Volume = Math.Clamp(Volume, 0, 200);
        return this;
    }

    /// <summary>Same effect choice and knobs (ignoring on/off and volume, which don't change the effects' sound).</summary>
    internal static bool SameEffects(VoiceFxSettings a, VoiceFxSettings b) =>
        (a.PitchOn, a.PitchSemitones, a.UnderwaterOn, a.UnderwaterDepth, a.RadioOn, a.RadioDrive, a.TelephoneOn, a.TelephoneGrit,
         a.DemonOn, a.DemonDepth, a.GhostOn, a.GhostAmount, a.EchoOn, a.EchoMs, a.ReverbOn, a.ReverbSize, a.WalkieTalkie)
        == (b.PitchOn, b.PitchSemitones, b.UnderwaterOn, b.UnderwaterDepth, b.RadioOn, b.RadioDrive, b.TelephoneOn, b.TelephoneGrit,
            b.DemonOn, b.DemonDepth, b.GhostOn, b.GhostAmount, b.EchoOn, b.EchoMs, b.ReverbOn, b.ReverbSize, b.WalkieTalkie);

    public VoiceFxSettings Clone() => (VoiceFxSettings)MemberwiseClone();

    internal bool AnyEffect => PitchOn || UnderwaterOn || RadioOn || TelephoneOn || DemonOn || GhostOn || EchoOn || ReverbOn;
}

/// <summary>
/// Real-time voice effects on the mic mix, in this order: pitch, demon, underwater, telephone, radio
/// (or walkie-talkie), ghost, echo, reverb, then a soft limiter.
///
/// The effects are heard while "Voice effects on" is ticked, or while the push-to-talk key is held. Switching
/// fades over ~15 ms (no click), and when they switch off only their input is cut, so echo and reverb tails
/// ring out naturally instead of stopping dead.
///
/// Walkie-talkie (Mission control): a key-up click when push-to-talk is pressed and a burst of static when it
/// is released; without push-to-talk, the static plays when you stop talking. All sounds are synthesized.
///
/// Loudness: every effect setting has one fixed gain that makes it as loud as the dry voice (ITU-R BS.1770
/// K-weighting), worked out once on a built-in test voice at a fixed standard speaking level. Nothing adjusts
/// itself while you talk: the same settings always get the same gain. The Volume setting then scales
/// everything, effects on or off.
/// Input and output: stereo float, both channels equal.
/// </summary>
sealed class VoiceEffects : ISampleProvider
{
    readonly ISampleProvider source;
    readonly int rate;
    VoiceFxSettings last;

    // Settings plus their loudness calibration, swapped as one (the UI thread writes, the audio thread reads).
    sealed record Snapshot(VoiceFxSettings Fx, float CalibDb, int Version);
    volatile Snapshot snap;
    Snapshot lastSnap;
    readonly bool calibrationMode;          // the background calibration's own instance: no matching, no volume
    readonly object calibrationGate = new();
    (VoiceFxSettings Fx, int Version)? pendingCalibration;
    bool calibrating;
    Task calibrationTask = Task.CompletedTask;
    int version;

    // fixed loudness gain per effect setting (K-weighted, at a standard speaking level)
    const float SpeechFloor = 1e-5f;        // -50 dB: quieter than this, nobody is talking
    const float RefSpeechMs = 0.0025f;      // -26 dB: the standard speaking level the effects are levelled at
    const float TailDesignRms = 0.095f;     // level the click/static were designed at
    const float NoiseFloorMs = RefSpeechMs * 0.001f;   // -56 dB: the noise reducer works below this (fixed)
    static readonly float SoundScale = MathF.Sqrt(RefSpeechMs) / TailDesignRms;   // click/static at the standard voice level
    readonly Biquad kDryShelf, kDryHighPass;
    readonly float fastCoef, slowCoef, gainCoef, volumeCoef;
    float dryFast, gainDb, calibDb, wetGain = 1, volume = 1;
    float speechMs = RefSpeechMs;           // your speaking level: only used to notice when you stop talking

    readonly PitchShifter pitch, demonPitch;
    readonly Echo echo;
    readonly Reverb reverb, ghostReverb;
    readonly Biquad radioHighPass = new(), radioLowPass = new();
    readonly Biquad[] walkieBand, hissBand, telephoneBand, tailBand;
    readonly Biquad demonLowPass = new(), ghostHighPass = new(), whisperHighPass = new(), whisperLowPass = new(), underwaterLowPass = new(), underwaterLowPass2 = new();
    readonly float[] wobble;
    int wobbleWrite;
    double wobblePhase, murkPhase;
    uint noiseState = 0x9E3779B9;

    // switching and push-to-talk
    float active;                  // 0..1, the fade between dry and effects
    readonly float fadeStep;
    int ringOut;                   // samples the effects keep running after switching off (echo/reverb tails)
    volatile bool pushToTalk;
    int pttPresses, pttReleases, seenPresses, seenReleases;

    // walkie-talkie state
    float compEnvelope, compGain = 1, transmitGate;
    float voiceEnvelope;           // speech detector (RMS-ish)
    int talkingSamples, silentSamples;
    bool talking;
    int clickPos = -1, tailPos = -1;
    float tailCrackle = 1;
    readonly int clickLength, tailLength, hangSamples, minTalkSamples;
    int sincePttRelease = int.MaxValue;    // samples since push-to-talk was released (no double static after it)
    readonly Biquad[] keyBand;

    // gentle noise reducer on the effects' input (not the dry voice): room noise far below your voice is turned down
    float expanderGain = 1;
    readonly float expanderOpen, expanderClose;

    // ghost
    float ghostEnvelope, ghostSwell;

    public VoiceEffects(ISampleProvider source, VoiceFxSettings settings) : this(source, settings, calibration: false) { }

    VoiceEffects(ISampleProvider source, VoiceFxSettings settings, bool calibration)
    {
        this.source = source;
        rate = source.WaveFormat.SampleRate;
        calibrationMode = calibration;
        last = settings.Clone();
        pitch = new PitchShifter(rate);
        demonPitch = new PitchShifter(rate);
        echo = new Echo(rate);
        reverb = new Reverb(rate);
        ghostReverb = new Reverb(rate);
        radioHighPass.HighPass(rate, 400, 0.707);
        radioLowPass.LowPass(rate, 3000, 0.707);
        walkieBand = new[] { new Biquad().HighPass(rate, 300, 0.707), new Biquad().HighPass(rate, 300, 0.707), new Biquad().LowPass(rate, 2800, 0.707), new Biquad().LowPass(rate, 2800, 0.707) };
        hissBand = new[] { new Biquad().HighPass(rate, 1500, 0.707), new Biquad().LowPass(rate, 4500, 0.707) };
        tailBand = new[] { new Biquad().HighPass(rate, 700, 0.707), new Biquad().LowPass(rate, 5500, 0.707) };
        keyBand = new[] { new Biquad().HighPass(rate, 900, 0.707), new Biquad().LowPass(rate, 6000, 0.707) };
        expanderOpen = 1 - MathF.Exp(-1000f / (2 * rate));
        expanderClose = 1 - MathF.Exp(-1000f / (80 * rate));
        telephoneBand = new[]
        {
            new Biquad().HighPass(rate, 350, 0.707), new Biquad().HighPass(rate, 350, 0.707),
            new Biquad().LowPass(rate, 3400, 0.707), new Biquad().LowPass(rate, 3400, 0.707),
            new Biquad().Peak(rate, 1300, 1.0, 6),
        };
        demonLowPass.LowPass(rate, 1800, 0.707);
        ghostHighPass.HighPass(rate, 250, 0.707);
        whisperHighPass.HighPass(rate, 2000, 0.707);
        whisperLowPass.LowPass(rate, 7000, 0.707);
        underwaterLowPass.LowPass(rate, 1000, 2);
        wobble = new float[rate / 50];   // 20 ms
        fadeStep = 1000f / (15 * rate);
        clickLength = rate * 90 / 1000;
        tailLength = rate * 320 / 1000;
        hangSamples = rate * 180 / 1000;
        minTalkSamples = rate * 250 / 1000;

        kDryShelf = new Biquad().HighShelf(rate, 1681.97, 4.0);
        kDryHighPass = new Biquad().HighPass(rate, 38.13, 0.5);
        fastCoef = 1 - MathF.Exp(-1000f / (30 * rate));
        slowCoef = 1 - MathF.Exp(-1000f / (800 * rate));
        gainCoef = 1 - MathF.Exp(-64 * 1000f / (15 * rate));        // a new effect's gain glides in over ~15 ms (no click)
        volumeCoef = 1 - MathF.Exp(-1000f / (20 * rate));
        float calibration0 = calibration ? 0 : Calibrate(last, rate);
        snap = lastSnap = new Snapshot(last, calibration0, 0);
        calibDb = gainDb = calibration0;
        wetGain = DbToGain(calibration0);
        volume = calibration ? 1 : last.Volume / 100f;
    }

    static float DbToGain(float db) => MathF.Pow(10, db / 20);

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>
    /// Apply new settings live (thread-safe). A different effect choice is re-calibrated for loudness in the
    /// background (a few ms); on/off and Volume changes apply as they are.
    /// </summary>
    public void Update(VoiceFxSettings settings)
    {
        var clone = settings.Clone();
        lock (calibrationGate)
        {
            var current = snap;
            if (VoiceFxSettings.SameEffects(current.Fx, clone)) { snap = current with { Fx = clone }; return; }
            int v = ++version;
            snap = new Snapshot(clone, current.CalibDb, v);
            pendingCalibration = (clone, v);
            if (!calibrating)
            {
                calibrating = true;
                calibrationTask = Task.Run(CalibrationLoop);
            }
        }
    }

    void CalibrationLoop()
    {
        while (true)
        {
            (VoiceFxSettings Fx, int Version) job;
            lock (calibrationGate)
            {
                if (pendingCalibration is not { } next) { calibrating = false; return; }
                job = next;
                pendingCalibration = null;
            }
            float db = Calibrate(job.Fx, rate);
            lock (calibrationGate)
            {
                if (snap.Version == job.Version) snap = snap with { CalibDb = db };
            }
        }
    }

    /// <summary>Tests: wait until background calibration has finished.</summary>
    internal void WaitForCalibration()
    {
        Task t;
        lock (calibrationGate) t = calibrationTask;
        t.Wait();
    }

    /// <summary>Tests: the gain currently applied to the effects (dB), its fixed target and the speech detector's level.</summary>
    internal (float GainDb, float CalibDb, float SpeechMs) Loudness => (gainDb, calibDb, speechMs);

    /// <summary>Push-to-talk key pressed/released (any thread). Effects play while it's held.</summary>
    public void SetPushToTalk(bool held)
    {
        if (held == pushToTalk) return;
        pushToTalk = held;
        if (held) Interlocked.Increment(ref pttPresses); else Interlocked.Increment(ref pttReleases);
    }

    /// <summary>Tests: whether the effects are currently (fading) on.</summary>
    internal float Active => active;

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        var sn = snap;
        var s = sn.Fx;
        if (!ReferenceEquals(sn, lastSnap))
        {
            calibDb = sn.CalibDb;   // the new effect setting's fixed gain
            lastSnap = sn;
        }
        float targetVolume = calibrationMode ? 1 : s.Volume / 100f;
        // An effect switched (back) on starts clean, instead of replaying an old echo tail.
        if (s.EchoOn && !last.EchoOn) echo.Clear();
        if (s.ReverbOn && !last.ReverbOn) reverb.Clear();
        if (s.PitchOn && !last.PitchOn) pitch.Clear();
        if (s.DemonOn && !last.DemonOn) demonPitch.Clear();
        if (s.GhostOn && !last.GhostOn) ghostReverb.Clear();
        last = s;

        bool walkie = s.WalkieTalkie && s.RadioOn;
        // Push-to-talk presses/releases since the last block (counters: a quick tap between two blocks still counts).
        int presses = Volatile.Read(ref pttPresses), releases = Volatile.Read(ref pttReleases);
        bool pressed = presses != seenPresses, released = releases != seenReleases;
        seenPresses = presses; seenReleases = releases;
        bool ptt = pushToTalk;
        if (walkie)
        {
            // Pressing push-to-talk keys the radio ("chk-shh"), letting go drops it ("kshhh"),
            // whether or not "Voice effects on" is also ticked.
            if (pressed) { clickPos = 0; tailPos = -1; }
            if (released && !ptt) { tailPos = 0; sincePttRelease = 0; }
        }

        bool on = s.Enabled || ptt;
        bool idle = active <= 0 && !on && ringOut <= 0 && clickPos < 0 && tailPos < 0;
        if (idle || !s.AnyEffect && clickPos < 0 && tailPos < 0)
        {
            // No effects playing: the mic passes through unchanged, apart from Volume. Keep learning your speaking level.
            active = on ? 1 : 0;
            bool scale = targetVolume != 1 || volume != 1;
            for (int i = 0; i < n / 2; i++)
            {
                float x = 0.5f * (buffer[offset + i * 2] + buffer[offset + i * 2 + 1]);
                TrackSpeech(x);
                if (!scale) continue;
                float y = SoftLimit(x * NextVolume(targetVolume));
                buffer[offset + i * 2] = y;
                buffer[offset + i * 2 + 1] = y;
            }
            return n;
        }

        double pitchRatio = Math.Pow(2, s.PitchSemitones / 12.0);
        double demonRatio = Math.Pow(2, (-6 - 6 * s.DemonDepth / 100.0) / 12);
        float demonDepth = s.DemonDepth / 100f, underwater = s.UnderwaterDepth / 100f, grit = s.TelephoneGrit / 100f;
        float ghost = s.GhostAmount / 100f;
        float drive = 1 + s.RadioDrive * 0.15f, driveNorm = MathF.Tanh(drive);
        float walkieDrive = 2.5f + s.RadioDrive * 0.04f, walkieNorm = MathF.Tanh(walkieDrive);
        int echoDelay = s.EchoMs * rate / 1000;
        float roomSize = s.ReverbSize / 100f;
        int telephoneLevels = 1 << (7 - (int)(grit * 3.5f));   // 8-bit .. ~4-bit phone line
        float wobbleBase = 0.003f * rate, wobbleDepth = (0.002f + 0.006f * underwater) * rate;
        double underwaterCutoff = 1800 - 1450 * underwater;
        float voiceCoef = 1 - MathF.Exp(-1000f / (10 * rate));

        for (int i = 0; i < n / 2; i++)
        {
            float x = 0.5f * (buffer[offset + i * 2] + buffer[offset + i * 2 + 1]);
            TrackSpeech(x);
            active = on ? Math.Min(1, active + fadeStep) : Math.Max(0, active - fadeStep);
            if (active > 0) ringOut = rate * 3; else if (ringOut > 0) ringOut--;
            if (sincePttRelease < int.MaxValue) sincePttRelease++;
            // Room noise ~30 dB or more below a normal speaking level is turned down (2:1, at most 12 dB) before the
            // effects, which otherwise bring it up (compression, distortion). Your voice itself is untouched.
            float expTarget = dryFast >= NoiseFloorMs ? 1 : Math.Max(0.25f, MathF.Sqrt(dryFast / NoiseFloorMs));
            expanderGain += (expTarget - expanderGain) * (expTarget > expanderGain ? expanderOpen : expanderClose);
            float w = x * active * expanderGain;   // the effects' input; when they switch off, tails still ring out

            if (s.PitchOn) w = pitch.Process(w, pitchRatio);
            if (s.DemonOn)
            {
                float deep = demonPitch.Process(w, demonRatio);
                deep = demonLowPass.Process(MathF.Tanh(deep * 2.5f) * 0.6f);
                w = w * (0.6f - 0.25f * demonDepth) + deep * (0.6f + 0.8f * demonDepth);
            }
            if (s.UnderwaterOn)
            {
                // Slow pitch wobble (swept delay) and a resonant, slowly drifting low-pass: muffled and bubbly.
                wobble[wobbleWrite] = w;
                wobblePhase += 1.1 / rate; if (wobblePhase >= 1) wobblePhase -= 1;
                murkPhase += 0.6 / rate; if (murkPhase >= 1) murkPhase -= 1;
                float delay = wobbleBase + wobbleDepth * (0.5f + 0.5f * MathF.Sin((float)(2 * Math.PI * wobblePhase)));
                w = ReadWobble(delay);
                wobbleWrite = (wobbleWrite + 1) % wobble.Length;
                if ((i & 31) == 0)
                {
                    double cutoff = underwaterCutoff * (1 + 0.3 * Math.Sin(2 * Math.PI * murkPhase));
                    underwaterLowPass.LowPass(rate, cutoff, 2);       // resonant: the "bubbly" part
                    underwaterLowPass2.LowPass(rate, cutoff, 0.7);    // second stage: properly muffled (24 dB/octave)
                }
                w = underwaterLowPass2.Process(underwaterLowPass.Process(w)) * (1.2f + underwater);
            }
            if (s.TelephoneOn)
            {
                foreach (var b in telephoneBand) w = b.Process(w);
                w = MathF.Tanh(w * (1.5f + grit * 2)) / (1.5f + grit * 2) * 1.8f;
                w = MuLawCrush(w, telephoneLevels);
            }
            if (s.RadioOn)
            {
                if (walkie) w = Walkie(w, x, s, ptt, walkieDrive, walkieNorm, voiceCoef, speechMs);
                else w = 0.8f * MathF.Tanh(drive * radioLowPass.Process(radioHighPass.Process(w))) / driveNorm;
            }
            if (s.GhostOn)
            {
                // Airy voice + whisper noise that follows the voice, with an eerie reverb that swells in.
                float level = Math.Abs(w);
                ghostEnvelope += (level - ghostEnvelope) * (level > ghostEnvelope ? 0.004f : 0.0002f);
                float whisper = whisperLowPass.Process(whisperHighPass.Process(Noise())) * ghostEnvelope * (2 + 4 * ghost);
                float voice = ghostHighPass.Process(w) * (1 - 0.5f * ghost);
                ghostSwell += ((ghostEnvelope > 0.003f ? 1 : 0) - ghostSwell) * (ghostEnvelope > 0.003f ? 0.00005f : 0.00002f);
                float wet = ghostReverb.ProcessWet(voice + whisper, 0.9f) * (0.4f + ghostSwell * (1 + ghost));
                w = voice + whisper + wet;
            }
            if (s.EchoOn) w = echo.Process(w, echoDelay);
            if (s.ReverbOn) w = reverb.Process(w, roomSize);

            // The effect setting's fixed loudness gain (no auto-adjusting while you talk).
            if (!calibrationMode)
            {
                if ((i & 63) == 0) UpdateGain();
                w *= wetGain;
            }

            float y = x * (1 - active) + w;
            if (clickPos >= 0) y += Click() * SoundScale;
            if (tailPos >= 0) y += Tail() * SoundScale;
            y = SoftLimit(y * NextVolume(targetVolume));
            buffer[offset + i * 2] = y;
            buffer[offset + i * 2 + 1] = y;
        }
        return n;
    }

    /// <summary>
    /// K-weights the dry mic sample for the noise reducer and the walkie's "stopped talking" detector (which
    /// follows your speaking level so it works for quiet and loud voices). Not used for any loudness.
    /// </summary>
    void TrackSpeech(float x)
    {
        float kd = kDryHighPass.Process(kDryShelf.Process(x));
        float kd2 = kd * kd;
        dryFast += (kd2 - dryFast) * fastCoef;
        // Only sounds within ~10 dB of your speaking level count as speech (room noise doesn't drag it down).
        if (dryFast > Math.Max(SpeechFloor, speechMs * 0.1f)) speechMs += (kd2 - speechMs) * slowCoef;
    }

    /// <summary>Glides to the current effect setting's fixed gain (only moves when the setting changes).</summary>
    void UpdateGain()
    {
        if (gainDb == calibDb) return;
        float d = calibDb - gainDb;
        gainDb = Math.Abs(d) < 0.01f ? calibDb : gainDb + d * gainCoef;
        wetGain = DbToGain(gainDb);
    }

    float NextVolume(float target)
    {
        float d = target - volume;
        volume = Math.Abs(d) < 1e-5f ? target : volume + d * volumeCoef;
        return volume;
    }

    /// <summary>
    /// Military walkie-talkie: narrow band, squashed hard, clipped, hiss while transmitting. Without
    /// push-to-talk, a speech detector decides when you've finished and fires the squelch tail.
    /// </summary>
    float Walkie(float w, float dry, VoiceFxSettings s, bool ptt, float drive, float norm, float voiceCoef, float speechLevel)
    {
        foreach (var b in walkieBand) w = b.Process(w);
        float a = Math.Abs(w);
        compEnvelope = a > compEnvelope ? a : compEnvelope * 0.9997f;
        // Gentle squash (at most +6 dB): the radio sound comes mostly from the narrow band and the clipping, so a
        // quiet voice isn't lifted much more than a loud one and the fixed loudness gain fits all voices better.
        float target = Math.Clamp(0.3f / (compEnvelope + 1e-4f), 0.5f, 2f);
        compGain += (target - compGain) * 0.002f;
        w = MathF.Tanh(w * compGain * drive) / norm * 0.7f;

        bool transmitting;
        if (s.Enabled)
        {
            // Latched on (no push-to-talk): detect the end of speech.
            voiceEnvelope += (dry * dry - voiceEnvelope) * voiceCoef;
            // Speaking = within ~10 dB of your usual level (and above -48 dBFS), not room noise.
            bool loud = voiceEnvelope > Math.Max(0.004f * 0.004f, speechLevel * 0.1f);
            if (loud) { talkingSamples++; silentSamples = 0; if (!talking && talkingSamples > rate / 50) talking = true; }
            else if (talking && ++silentSamples > hangSamples)
            {
                // Finished a phrase: squelch tail (unless push-to-talk just played one).
                if (talkingSamples >= minTalkSamples && !ptt && sincePttRelease > rate) tailPos = 0;
                talking = false;
                talkingSamples = 0;
            }
            else if (!talking) talkingSamples = 0;
            transmitting = talking || loud || ptt;
            // Like a real radio, nothing is sent between phrases (no squashed-up room noise).
            transmitGate += ((transmitting ? 1 : 0) - transmitGate) * (transmitting ? 0.02f : 0.001f);
            w *= transmitGate;
        }
        else transmitting = ptt;

        if (transmitting)
        {
            float hiss = Noise();
            foreach (var b in hissBand) hiss = b.Process(hiss);
            w += hiss * 0.008f;   // light radio hiss (was 0.02: too much)
        }
        return w;
    }

    /// <summary>Key-up "chk-shh": a sharp click, a little 2.2 kHz ring and a short burst of static (~90 ms).</summary>
    float Click()
    {
        float t = clickPos / (float)rate;
        float y = clickPos < rate / 500 ? Noise() * 0.6f * (1 - clickPos / (rate / 500f)) : 0;
        y += 0.2f * MathF.Sin(2 * MathF.PI * 2200 * t) * MathF.Exp(-t * 200);
        float burst = Noise();
        foreach (var b in keyBand) burst = b.Process(burst);
        float attack = Math.Min(1, clickPos / (rate * 0.004f));
        y += burst * 0.4f * attack * MathF.Exp(-t * 28);
        if (++clickPos >= clickLength) clickPos = -1;
        return y;
    }

    /// <summary>Squelch tail ("kshhh"): band-passed static with a crackly level, ending abruptly like a radio.</summary>
    float Tail()
    {
        int attack = rate * 3 / 1000, fadeStart = tailLength - rate * 40 / 1000;
        float env = tailPos < attack ? tailPos / (float)attack
                  : tailPos > fadeStart ? 1 - (tailPos - fadeStart) / (float)(tailLength - fadeStart) : 1;
        if (tailPos % (rate / 200) == 0) tailCrackle = 0.65f + 0.35f * (NextRandom() & 0xFFFF) / 65535f;
        float y = Noise();
        foreach (var b in tailBand) y = b.Process(y);
        if (++tailPos >= tailLength) tailPos = -1;
        return y * 0.28f * env * tailCrackle;   // about as loud as a normal voice
    }

    static readonly Dictionary<(int Rate, string Key), float> Calibrations = new();

    /// <summary>
    /// The fixed loudness gain of an effect setting: runs the built-in test voice, at the standard speaking
    /// level, through a private copy of the effects and returns the gain (dB) that makes them as loud as the dry
    /// voice. Remembered per setting, so a setting always gets exactly the same gain.
    /// </summary>
    static float Calibrate(VoiceFxSettings settings, int rate)
    {
        if (!settings.AnyEffect) return 0;
        var fx = settings.Clone();
        fx.Enabled = true;
        fx.Volume = 100;
        string key = System.Text.Json.JsonSerializer.Serialize(fx);
        lock (Calibrations) if (Calibrations.TryGetValue((rate, key), out float known)) return known;
        var input = TestVoice(rate);
        var chain = new VoiceEffects(new MonoSource(input, rate), fx, calibration: true);
        var output = new float[input.Length];
        var block = new float[960];
        for (int i = 0; i < input.Length; i += 480)
        {
            int frames = Math.Min(480, input.Length - i);
            chain.Read(block, 0, frames * 2);
            for (int j = 0; j < frames; j++) output[i + j] = block[j * 2];
        }
        float db = MatchDb(input, output, rate, RefSpeechMs);
        lock (Calibrations) Calibrations[(rate, key)] = db;
        return db;
    }

    /// <summary>Gain (dB) that matches the wet K-weighted energy to the dry one, over the parts where the voice speaks.</summary>
    static float MatchDb(float[] dry, float[] wet, int rate, float speechMs)
    {
        var dShelf = new Biquad().HighShelf(rate, 1681.97, 4.0); var dHp = new Biquad().HighPass(rate, 38.13, 0.5);
        var wShelf = new Biquad().HighShelf(rate, 1681.97, 4.0); var wHp = new Biquad().HighPass(rate, 38.13, 0.5);
        float fast = 1 - MathF.Exp(-1000f / (30 * rate)), env = 0;
        double sumDry = 0, sumWet = 0;
        float threshold = Math.Max(SpeechFloor * 0.01f, speechMs * 0.03f);
        for (int i = 0; i < dry.Length; i++)
        {
            float kd = dHp.Process(dShelf.Process(dry[i])), kw = wHp.Process(wShelf.Process(wet[i]));
            env += (kd * kd - env) * fast;
            if (i < rate / 4 || env < threshold) continue;   // skip the warm-up and the pauses
            sumDry += kd * kd;
            sumWet += kw * kw;
        }
        if (sumWet <= 1e-12 || sumDry <= 1e-12) return 0;
        return Math.Clamp((float)(10 * Math.Log10(sumDry / sumWet)), -24, 24);
    }

    static readonly Dictionary<int, float[]> TestVoices = new();

    /// <summary>
    /// 6 s of synthetic speech (glottal pulses through vowel formants, syllables, pauses, some "s" sounds),
    /// at the reference speaking level. Deterministic, made once per sample rate.
    /// </summary>
    static float[] TestVoice(int rate)
    {
        lock (TestVoices)
        {
            if (TestVoices.TryGetValue(rate, out var cached)) return cached;
#pragma warning disable CA5394 // a fixed-seed test signal, not security
            var rng = new Random(7);
            int n = rate * 6;   // long enough to average out the vowel mix
            var y = new float[n];
            var vowels = new (double F1, double F2, double F3)[] { (730, 1090, 2440), (530, 1840, 2480), (270, 2290, 3010), (570, 840, 2410), (300, 870, 2240) };
            double phase = 0, tilt = 0;
            var res = new double[3, 2];
            int pos = 0;
            while (pos < n)
            {
                int syl = (int)(rate * (0.18 + rng.NextDouble() * 0.14));
                int gap = (int)(rate * (rng.NextDouble() < 0.2 ? 0.3 + rng.NextDouble() * 0.2 : 0.04 + rng.NextDouble() * 0.1));
                var v = vowels[rng.Next(vowels.Length)];
                double f0 = 105 + rng.NextDouble() * 45;
                int fric = rng.NextDouble() < 0.35 ? rate * 6 / 100 : 0;
                double hpPrev = 0, hpOut = 0;
                for (int i = 0; i < syl + fric && pos < n; i++, pos++)
                {
                    double sample;
                    if (i < fric)
                    {
                        double noise = rng.NextDouble() * 2 - 1;
                        hpOut = 0.7 * (hpOut + noise - hpPrev); hpPrev = noise;
                        sample = hpOut * 0.25 * Math.Sin(Math.PI * i / fric);
                    }
                    else
                    {
                        int k = i - fric;
                        double env = Math.Min(1, Math.Min(k / (0.02 * rate), (syl - k) / (0.03 * rate)));
                        phase += f0 * (1 + 0.08 * Math.Sin(2 * Math.PI * k / syl)) / rate;
                        if (phase >= 1) phase -= 1;
                        tilt += (1 - 2 * phase - tilt) * 0.15;
                        double sum = 0;
                        double[] fs = { v.F1, v.F2, v.F3 }, gains = { 1.0, 0.5, 0.25 };
                        for (int r = 0; r < 3; r++)
                        {
                            double bw = 80 + fs[r] * 0.05, rr = Math.Exp(-Math.PI * bw / rate), th = 2 * Math.PI * fs[r] / rate;
                            double o = (1 - rr) * tilt + 2 * rr * Math.Cos(th) * res[r, 0] - rr * rr * res[r, 1];
                            res[r, 1] = res[r, 0]; res[r, 0] = o;
                            sum += o * gains[r];
                        }
                        sample = sum * env;
                    }
                    y[pos] = (float)sample;
                }
                pos += gap;
            }
#pragma warning restore CA5394
            // Scale so its speaking parts sit at the reference level (K-weighted).
            var shelf = new Biquad().HighShelf(rate, 1681.97, 4.0); var hp = new Biquad().HighPass(rate, 38.13, 0.5);
            float fast = 1 - MathF.Exp(-1000f / (30 * rate)), envK = 0;
            double sumK = 0; int count = 0;
            var k2 = new float[n];
            for (int i = 0; i < n; i++) { float kk = hp.Process(shelf.Process(y[i])); k2[i] = kk * kk; envK += (k2[i] - envK) * fast; }
            float peakEnv = 0; envK = 0;
            for (int i = 0; i < n; i++) { envK += (k2[i] - envK) * fast; peakEnv = Math.Max(peakEnv, envK); }
            envK = 0;
            for (int i = 0; i < n; i++) { envK += (k2[i] - envK) * fast; if (envK > peakEnv * 0.03f) { sumK += k2[i]; count++; } }
            float g = (float)Math.Sqrt(RefSpeechMs / (sumK / Math.Max(1, count)));
            for (int i = 0; i < n; i++) y[i] *= g;
            TestVoices[rate] = y;
            return y;
        }
    }

    sealed class MonoSource(float[] mono, int rate) : ISampleProvider
    {
        int pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        public int Read(float[] b, int off, int count)
        {
            for (int i = 0; i < count / 2; i++) { float v = pos < mono.Length ? mono[pos] : 0; pos++; b[off + 2 * i] = b[off + 2 * i + 1] = v; }
            return count;
        }
    }

    float ReadWobble(float delay)
    {
        float pos = wobbleWrite - delay;
        if (pos < 0) pos += wobble.Length;
        int i0 = (int)pos;
        float frac = pos - i0;
        return wobble[i0 % wobble.Length] * (1 - frac) + wobble[(i0 + 1) % wobble.Length] * frac;
    }

    /// <summary>Phone-line grit: μ-law companding with a coarse quantizer (fewer levels = rougher).</summary>
    static float MuLawCrush(float x, int levels)
    {
        const float Mu = 255f;
        float a = Math.Min(1, Math.Abs(x));
        float c = MathF.Log(1 + Mu * a) / MathF.Log(1 + Mu);
        c = MathF.Round(c * levels) / levels;
        return Math.Sign(x) * (MathF.Pow(1 + Mu, c) - 1) / Mu;
    }

    uint NextRandom()
    {
        noiseState ^= noiseState << 13;
        noiseState ^= noiseState >> 17;
        noiseState ^= noiseState << 5;
        return noiseState;
    }

    float Noise() => (NextRandom() & 0xFFFFFF) / 8388608f - 1f;

    /// <summary>Unity gain up to 0.8, then smoothly approaches 1.0 instead of hard clipping.</summary>
    internal static float SoftLimit(float x)
    {
        float a = Math.Abs(x);
        if (a <= 0.8f) return x;
        return Math.Sign(x) * (0.8f + 0.2f * MathF.Tanh((a - 0.8f) / 0.2f));
    }

    /// <summary>
    /// Delay-line pitch shifter: two read taps sweep through a 40 ms window at the pitch ratio,
    /// crossfaded with sin² windows (which always sum to 1). ~20 ms added latency.
    /// </summary>
    internal sealed class PitchShifter
    {
        readonly float[] buf;
        readonly int window;
        int write;
        double phase;

        public PitchShifter(int rate)
        {
            window = rate * 40 / 1000;
            buf = new float[window + 4];
        }

        public void Clear() { Array.Clear(buf); phase = 0; }

        public float Process(float x, double ratio)
        {
            buf[write] = x;
            double p2 = phase + 0.5; if (p2 >= 1) p2 -= 1;
            float y = Tap(phase * window) * Weight(phase) + Tap(p2 * window) * Weight(p2);
            phase += (1 - ratio) / window;
            if (phase >= 1) phase -= 1; else if (phase < 0) phase += 1;
            write = (write + 1) % buf.Length;
            return y;
        }

        float Tap(double delay)
        {
            double pos = write - delay;
            if (pos < 0) pos += buf.Length;
            int i0 = (int)pos;
            float frac = (float)(pos - i0);
            return buf[i0 % buf.Length] * (1 - frac) + buf[(i0 + 1) % buf.Length] * frac;
        }

        static float Weight(double p) { float s = (float)Math.Sin(Math.PI * p); return s * s; }
    }

    internal sealed class Echo
    {
        readonly float[] buf;
        int write;
        public Echo(int rate) => buf = new float[rate];   // up to 1 s
        public void Clear() => Array.Clear(buf);

        public float Process(float x, int delay)
        {
            float delayed = buf[(write - delay + buf.Length) % buf.Length];
            buf[write] = x + delayed * 0.45f;   // feedback: each repeat ~7 dB quieter
            write = (write + 1) % buf.Length;
            return x + delayed * 0.5f;
        }
    }

    /// <summary>Compact Freeverb-style reverb: 6 damped comb filters in parallel, then 2 all-passes.</summary>
    internal sealed class Reverb
    {
        static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491 };   // at 44.1 kHz
        static readonly int[] AllPassTunings = { 556, 441 };
        readonly float[][] combs, allPasses;
        readonly int[] combPos, allPassPos;
        readonly float[] combStore;

        public Reverb(int rate)
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

        public float Process(float x, float size)
        {
            float mix = 0.25f + size * 0.25f;
            return x * (1 - mix * 0.5f) + ProcessWet(x, size) * mix;
        }

        /// <summary>Only the reverberated sound (no dry signal).</summary>
        public float ProcessWet(float x, float size)
        {
            float feedback = 0.7f + size * 0.28f, damp = 0.2f, input = x * 0.015f, wet = 0;
            for (int c = 0; c < combs.Length; c++)
            {
                var b = combs[c];
                float y = b[combPos[c]];
                combStore[c] = y * (1 - damp) + combStore[c] * damp;
                b[combPos[c]] = input + combStore[c] * feedback;
                combPos[c] = (combPos[c] + 1) % b.Length;
                wet += y;
            }
            for (int a = 0; a < allPasses.Length; a++)
            {
                var b = allPasses[a];
                float stored = b[allPassPos[a]];
                b[allPassPos[a]] = wet + stored * 0.5f;
                wet = stored - wet;
                allPassPos[a] = (allPassPos[a] + 1) % b.Length;
            }
            return wet * 3f;
        }
    }
}
