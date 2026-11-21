using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMonitor;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MicMode { Mix, AutoSwitch }

/// <summary>One mixer input. Feed: take this (processed) audio instead of capturing the device, e.g. the guitar
/// after the pedalboard. AlwaysLive: always mixed in, never switched off by Auto-switch (e.g. the guitar).
/// Raw: capture without Windows' per-device effects (Voice Clarity etc.), for an instrument input.
/// Channel (raw inputs only): which channel carries the instrument; Both = the average, as for the Real Tone cable.</summary>
sealed record MicInputSpec(MMDevice Device, float Gain, AudioFeed? Feed = null, bool AlwaysLive = false, bool Raw = false,
                           GuitarChannel Channel = GuitarChannel.Both);

/// <summary>
/// Captures up to four microphones (or other inputs, like the Rocksmith cable) and plays them, mixed or
/// auto-switched and optionally through voice effects, into an output device: normally VB-CABLE's
/// "CABLE Input", so apps like Discord can use "CABLE Output" as one microphone. Each input has its own
/// self-healing, drift-corrected buffer (separate USB devices run on separate clocks).
/// </summary>
sealed class MicMixer : IDisposable
{
    public const int MaxInputs = 4;

    readonly List<MicInput> mics = new();
    readonly MicCombiner combiner = null!;
    readonly VoiceEffects effects = null!;
    readonly IDisposable? player;

    /// <summary>Set (from any thread) when the mixer can no longer run; the owner restarts it.</summary>
    public volatile string? Fault;
    public string OutputInfo { get; } = "";

    /// <summary>Output side: the device's engine period (how often it pulls audio) in ms.</summary>
    public double OutputPeriodMs { get; }
    /// <summary>Each input's buffer target in ms (part of the voice-to-Discord delay).</summary>
    public int InputBufferMs { get; }

    /// <param name="outputTap">Gets a copy of everything sent to the output (what Discord hears), e.g. to listen to it.</param>
    public MicMixer(MMDevice output, IReadOnlyList<MicInputSpec> inputs, MicMode mode, VoiceFxSettings fx, int latencyMs,
                    AudioFeed? outputTap = null)
    {
        if (inputs.Count < 1 || inputs.Count > MaxInputs) throw new ArgumentException($"1 to {MaxInputs} inputs are needed.");
        try
        {
            LowLatencyOutput? low = null;
            try { low = new LowLatencyOutput(output.ID, latencyMs, lowLatencyEngine: true); }
            catch { low = null; }
            player = low;   // owned from here on, so Dispose() releases it if anything below fails
            int rate = low?.SampleRate ?? output.AudioClient.MixFormat.SampleRate;
            double outputPeriodMs = low?.PeriodMs ?? 10;
            OutputPeriodMs = outputPeriodMs;

            foreach (var input in inputs) mics.Add(new MicInput(input, rate, outputPeriodMs, this));
            InputBufferMs = mics[0].Buffer.TargetMs;
            combiner = new MicCombiner(mics.Cast<IMicSource>().ToList(), rate) { Mode = mode };
            effects = new VoiceEffects(combiner, fx);
            ISampleProvider final = outputTap != null ? new OutputTap(effects, outputTap) : effects;

            Action play;
            if (low != null)
            {
                low.Failed += ex => Fault = "Mic output stopped: " + ex.Message;
                play = () => low.Start(final);
                OutputInfo = $"low-latency, {low.QueuedMs:0.#} ms queued";
            }
            else
            {
                var wasapi = new WasapiOut(output, AudioClientShareMode.Shared, true, Math.Max(latencyMs, 10));
                player = wasapi;
                wasapi.PlaybackStopped += (_, e) => { if (e.Exception != null) Fault = "Mic output stopped: " + e.Exception.Message; };
                wasapi.Init(final);
                play = wasapi.Play;
                OutputInfo = "standard mode, ~22 ms queued";
            }
            play();
            foreach (var m in mics) m.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public MicMode Mode { get => combiner.Mode; set => combiner.Mode = value; }

    public void SetGain(int input, float gain)
    {
        if (input >= 0 && input < mics.Count) mics[input].Gain = Math.Clamp(gain, 0f, 2f);
    }

    /// <summary>Apply new voice effect settings live.</summary>
    public void SetEffects(VoiceFxSettings fx) => effects.Update(fx);

    /// <summary>Push-to-talk key for the voice effects held (true) or released (false).</summary>
    public void SetPushToTalk(bool held) => effects.SetPushToTalk(held);

    /// <summary>Recent level (0..1, after gain) per input, and which input is live (-1 = all, in Mix mode).</summary>
    public (float[] Levels, int Active) TakeLevels() => (mics.Select(m => m.TakePeak()).ToArray(), combiner.ActiveMic);

    /// <summary>Diagnostics: reads that found an input buffer short of audio, and how many gaps were healed.</summary>
    public int Underruns => mics.Sum(m => m.Buffer.Rate.Underruns);
    public int Recenters => mics.Sum(m => m.Buffer.Recenters);

    /// <summary>Tests only: scales what is sent to the output (0 = silent), so tests never play mics aloud.</summary>
    internal float TestOutputGain { set => combiner.OutputGain = value; }

    public void Dispose()
    {
        foreach (var m in mics) m.Dispose();
        if (player is WasapiOut w) { try { w.Stop(); } catch { } }
        player?.Dispose();
    }

    /// <summary>Passes the output through unchanged and hands a (mono) copy to a feed.</summary>
    sealed class OutputTap(ISampleProvider source, AudioFeed feed) : ISampleProvider
    {
        float[] mono = Array.Empty<float>();
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = source.Read(buffer, offset, count);
            int frames = n / 2;
            if (mono.Length < frames) mono = new float[frames];
            for (int i = 0; i < frames; i++) mono[i] = buffer[offset + i * 2];   // both channels carry the same voice
            feed.Push(mono, frames, WaveFormat.SampleRate);
            return n;
        }
    }

    /// <summary>What the combiner needs from an input (lets tests feed synthetic signals).</summary>
    internal interface IMicSource
    {
        ISampleProvider Stream { get; }   // stereo float at the output rate, never returns short
        float Gain { get; }
        bool AlwaysLive { get; }
        void NotePeak(float peak);
    }

    /// <summary>One input: capture (or a feed) → self-healing drift buffer → stereo float at the output rate.</summary>
    sealed class MicInput : IMicSource, IDisposable
    {
        readonly WasapiCapture? capture;
        readonly RawCapture? rawCapture;
        readonly AudioFeed? feed;
        readonly int outputRate, targetMs;
        IDisposable? subscription;
        byte[] feedBytes = Array.Empty<byte>();
        float[] pickMono = Array.Empty<float>();
        byte[] pickBytes = Array.Empty<byte>();
        float peak;
        volatile float gain;
        volatile DriftBuffer buffer;
        public DriftBuffer Buffer => buffer;
        public float Gain { get => gain; set => gain = value; }
        public bool AlwaysLive { get; }
        public ISampleProvider Stream => buffer.Output;

        public MicInput(MicInputSpec spec, int outputRate, double outputPeriodMs, MicMixer owner)
        {
            var device = spec.Device;
            gain = spec.Gain;
            feed = spec.Feed;
            AlwaysLive = spec.AlwaysLive;
            this.outputRate = outputRate;
            // One output read, plus half a 10 ms capture packet of phase wobble, plus a margin
            // (same rule as the speaker copy, which measured clean).
            targetMs = (int)Math.Ceiling(Math.Max(outputPeriodMs, 10) + 5 + 2);
            if (feed != null)
            {
                buffer = new DriftBuffer(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), outputRate, targetMs);   // silent until audio arrives
                return;
            }
            if (spec.Raw)
            {
                // An instrument (the guitar cable): bypass Windows' speech processing.
                rawCapture = new RawCapture(device.ID, raw: true);
                string name = device.FriendlyName;
                rawCapture.Failed += ex => owner.Fault = $"\"{name}\" stopped: {ex.Message}";
                if (spec.Channel == GuitarChannel.Both || rawCapture.WaveFormat.Channels < 2)
                {
                    // As before 2.0.0: the drift buffer takes the device's own format (the mix averages left and right).
                    buffer = new DriftBuffer(rawCapture.WaveFormat, outputRate, targetMs);
                    rawCapture.DataAvailable += (data, bytes) => buffer.Push(data, bytes);
                }
                else
                {
                    // Only the channel that carries the instrument (e.g. the left of a PC's line-in), as stereo.
                    var format = rawCapture.WaveFormat;
                    var picker = new ChannelPicker(spec.Channel, format.SampleRate);
                    buffer = new DriftBuffer(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, 2), outputRate, targetMs);
                    rawCapture.DataAvailable += (data, bytes) =>
                    {
                        int frames = bytes / format.BlockAlign;
                        if (pickMono.Length < frames) pickMono = new float[frames];
                        picker.ToMono(data, frames, format.Channels, pickMono);
                        int length = AudioFeed.ToStereoBytes(pickMono, frames, ref pickBytes);
                        buffer.Push(pickBytes, length);
                    };
                }
                return;
            }
            capture = new WasapiCapture(device, true, 10);
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) owner.Fault = $"\"{device.FriendlyName}\" stopped: {e.Exception.Message}";
            };
            buffer = new DriftBuffer(capture.WaveFormat, outputRate, targetMs);
            capture.DataAvailable += (_, e) => buffer.Push(e.Buffer, e.BytesRecorded);
        }

        public void Start()
        {
            if (feed != null) subscription = feed.Subscribe(OnFeed);
            else if (rawCapture != null) rawCapture.Start();
            else capture!.StartRecording();
        }

        /// <summary>Processed audio from the feed (capture thread of its producer).</summary>
        void OnFeed(float[] mono, int count, int rate)
        {
            var b = buffer;
            if (b.InputRate != rate) buffer = b = new DriftBuffer(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2), outputRate, targetMs);
            int length = AudioFeed.ToStereoBytes(mono, count, ref feedBytes);
            b.Push(feedBytes, length);
        }

        public void NotePeak(float p) { if (p > Volatile.Read(ref peak)) Volatile.Write(ref peak, p); }
        public float TakePeak() => Interlocked.Exchange(ref peak, 0);

        public void Dispose()
        {
            subscription?.Dispose();
            rawCapture?.Dispose();
            if (capture == null) return;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
    }

    /// <summary>
    /// Turns the input streams into one voice signal (stereo, both channels equal):
    ///  - Mix: sum of all inputs, with a soft limiter so several loud inputs can't clip.
    ///  - AutoSwitch: the input that hears you clearly louder than the live one (for a moment) becomes
    ///    live, with a short crossfade. A cough or bump is too short to switch. Inputs marked Always live
    ///    (e.g. the guitar) don't take part: they are mixed in all the time.
    /// </summary>
    internal sealed class MicCombiner : ISampleProvider
    {
        const float SwitchRatio = 1.6f;      // another input must be ~4 dB louder...
        const double HoldMs = 200;           // ...for this long before switching
        const double FadeMs = 30;            // crossfade length
        const float NoiseFloor = 0.003f;     // ~-50 dBFS: below this nobody is talking, keep the current input
        const double AttackMs = 50, ReleaseMs = 150;   // loudness follower: short bursts barely register

        readonly List<IMicSource> mics;
        readonly int[] switched;   // inputs Auto-switch chooses between (the ones not marked Always live)
        readonly float[][] temp;
        readonly float[] envelope, weight;
        readonly double sampleRate;
        double louderForMs;
        int candidate = -1;
        volatile int active;

        public MicCombiner(List<IMicSource> mics, int sampleRate)
        {
            this.mics = mics;
            this.sampleRate = sampleRate;
            temp = mics.Select(_ => Array.Empty<float>()).ToArray();
            envelope = new float[mics.Count];
            weight = new float[mics.Count];
            switched = Enumerable.Range(0, mics.Count).Where(m => !mics[m].AlwaysLive).ToArray();
            active = switched.Length > 0 ? switched[0] : 0;
            weight[active] = 1;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }
        public volatile MicMode Mode;
        public volatile float OutputGain = 1f;
        /// <summary>The auto-switched live input; -1 = every input is live (Mix, or nothing to choose between).</summary>
        public int ActiveMic => mics.Count == 1 ? 0 : (Mode == MicMode.Mix || switched.Length <= 1 ? -1 : active);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            double blockMs = frames * 1000.0 / sampleRate;

            for (int m = 0; m < mics.Count; m++)
            {
                if (temp[m].Length < frames * 2) temp[m] = new float[frames * 2];
                mics[m].Stream.Read(temp[m], 0, frames * 2);
                // Downmix to mono with gain (in place, first half of the buffer), and track loudness.
                float gain = mics[m].Gain, sum = 0, peak = 0;
                var t = temp[m];
                for (int i = 0; i < frames; i++)
                {
                    float v = 0.5f * (t[i * 2] + t[i * 2 + 1]) * gain;
                    t[i] = v;
                    sum += v * v;
                    peak = Math.Max(peak, Math.Abs(v));
                }
                mics[m].NotePeak(peak);
                float rms = frames > 0 ? MathF.Sqrt(sum / frames) : 0;
                double tau = rms > envelope[m] ? AttackMs : ReleaseMs;
                envelope[m] += (rms - envelope[m]) * (float)(1 - Math.Exp(-blockMs / tau));
            }

            float gainOut = OutputGain;
            if (Mode == MicMode.Mix || mics.Count == 1 || switched.Length <= 1)
            {
                for (int i = 0; i < frames; i++)
                {
                    float v = 0;
                    for (int m = 0; m < mics.Count; m++) v += temp[m][i];
                    v = VoiceEffects.SoftLimit(v) * gainOut;
                    buffer[offset + i * 2] = v;
                    buffer[offset + i * 2 + 1] = v;
                }
            }
            else
            {
                UpdateSelection(blockMs);
                float step = (float)(1000.0 / (FadeMs * sampleRate));
                int live = active;
                for (int i = 0; i < frames; i++)
                {
                    float v = 0;
                    for (int m = 0; m < mics.Count; m++)
                    {
                        if (mics[m].AlwaysLive) { v += temp[m][i]; continue; }
                        weight[m] = m == live ? Math.Min(1, weight[m] + step) : Math.Max(0, weight[m] - step);
                        v += temp[m][i] * weight[m];
                    }
                    v = VoiceEffects.SoftLimit(v) * gainOut;   // the live mic plus always-live inputs can add up
                    buffer[offset + i * 2] = v;
                    buffer[offset + i * 2 + 1] = v;
                }
            }
            if (count > frames * 2) buffer[offset + count - 1] = 0;
            return count;
        }

        void UpdateSelection(double blockMs)
        {
            // The loudest other input, if it's clearly louder than the live one.
            int loudest = -1;
            foreach (int m in switched)
                if (m != active && envelope[m] > NoiseFloor && envelope[m] > envelope[active] * SwitchRatio
                    && (loudest < 0 || envelope[m] > envelope[loudest]))
                    loudest = m;

            if (loudest >= 0 && loudest == candidate) louderForMs += blockMs;
            else { candidate = loudest; louderForMs = loudest >= 0 ? blockMs : 0; }

            if (candidate >= 0 && louderForMs >= HoldMs)
            {
                active = candidate;
                candidate = -1;
                louderForMs = 0;
            }
        }
    }
}
