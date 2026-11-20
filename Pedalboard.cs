using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMonitor;

/// <summary>
/// Hands the processed guitar (mono float) from the pedalboard to whoever listens, e.g. the mic mixer, so
/// Discord gets the guitar through the pedals instead of the raw cable. Lives as long as the app, so the
/// pedalboard can restart (new output device, Rocksmith closing, ...) without the mixer restarting.
/// </summary>
sealed class AudioFeed
{
    readonly object gate = new();
    volatile Action<float[], int, int>[] listeners = Array.Empty<Action<float[], int, int>>();

    /// <summary>Called on the capture thread with (samples, count, sampleRate).</summary>
    public IDisposable Subscribe(Action<float[], int, int> listener)
    {
        lock (gate) listeners = listeners.Append(listener).ToArray();
        return new Unsubscriber(this, listener);
    }

    public void Push(float[] mono, int count, int rate)
    {
        foreach (var l in listeners) l(mono, count, rate);
    }

    sealed class Unsubscriber(AudioFeed feed, Action<float[], int, int> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (feed.gate) feed.listeners = feed.listeners.Where(l => l != listener).ToArray();
        }
    }

    /// <summary>Mono samples → interleaved stereo float bytes, ready for a <see cref="DriftBuffer"/>.</summary>
    public static int ToStereoBytes(float[] mono, int count, ref byte[] bytes)
    {
        int length = count * 8;
        if (bytes.Length < length) bytes = new byte[length];
        var span = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, length));
        for (int i = 0; i < count; i++) span[2 * i] = span[2 * i + 1] = mono[i];
        return length;
    }
}

/// <summary>
/// Guitar input (the Rocksmith Real Tone cable) → pedal chain → speakers/headphones, and into the
/// <see cref="AudioFeed"/> for the mic mixer. The pedals run on the capture thread, right as each 10 ms
/// packet arrives; the output side has a small self-healing, drift-corrected buffer (the cable and the
/// output device run on separate clocks, and the cable drops a packet now and then).
/// The input is captured RAW (see <see cref="RawCapture"/>): Windows' speech processing must not touch an
/// instrument.
/// </summary>
sealed class Pedalboard : IDisposable
{
    const int RecentSamples = 8192;

    readonly RawCapture capture = null!;
    readonly IDisposable? player;
    readonly DriftBuffer monitor = null!;
    readonly PedalChain chain = null!;
    readonly AudioFeed feed;
    readonly int channels;
    readonly float[] recent = new float[RecentSamples];
    readonly object recentLock = new();
    int recentPos, recentCount;
    float[] mono = Array.Empty<float>();
    byte[] stereoBytes = Array.Empty<byte>();
    float inputMax;

    /// <summary>Set (from any thread) when the pedalboard can no longer run; the owner restarts it.</summary>
    public volatile string? Fault;
    public string InputId { get; }
    public string OutputId { get; }
    public int SampleRate { get; }
    public string OutputInfo { get; } = "";
    /// <summary>Estimated string-to-speaker delay in ms, excluding the converters inside the devices.</summary>
    public double LagMs { get; }
    public double CaptureMs => 10;
    public int BufferTargetMs => monitor.TargetMs;
    public double OutputMs { get; }
    /// <summary>True when the input bypasses Windows' per-device effects (e.g. Voice Clarity).</summary>
    public bool RawInput => capture.IsRaw;

    public Pedalboard(MMDevice input, MMDevice output, PedalboardSettings settings, int latencyMs, AudioFeed feed, bool mute = false)
    {
        this.feed = feed;
        InputId = input.ID;
        OutputId = output.ID;
        try
        {
            LowLatencyOutput? low = null;
            try { low = new LowLatencyOutput(output.ID, latencyMs, lowLatencyEngine: true); }
            catch { low = null; }
            int outputRate = low?.SampleRate ?? output.AudioClient.MixFormat.SampleRate;
            double outputPeriodMs = low?.PeriodMs ?? 10;

            capture = new RawCapture(input.ID, raw: true);
            SampleRate = capture.WaveFormat.SampleRate;
            channels = capture.WaveFormat.Channels;

            chain = new PedalChain(SampleRate, settings) { Mute = mute };
            // Packets arrive every 10 ms, so the buffer swings ±5 ms around its target; it must stay above one
            // output read plus some jitter at its lowest point. Devices that read in big 10 ms chunks (e.g. some
            // wireless USB headsets) swing it further: measured 2 dropouts in 20 s without the extra half period, 0 with it.
            // The 4 ms jitter margin: at 3 ms the buffer's lowest point measured 2.2 ms over 30 s, too close for a busy PC.
            int targetMs = (int)Math.Ceiling(5 + outputPeriodMs + 4 + (outputPeriodMs > 5 ? outputPeriodMs / 2 : 0));
            monitor = new DriftBuffer(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2), outputRate, targetMs);

            if (low != null)
            {
                low.Failed += ex => Fault = "Output stopped: " + ex.Message;
                player = low;
                low.Start(monitor.Output);
                OutputMs = low.QueuedMs;
                OutputInfo = $"low-latency, {low.QueuedMs:0.#} ms queued";
            }
            else
            {
                var wasapi = new WasapiOut(output, AudioClientShareMode.Shared, true, Math.Max(latencyMs, 10));
                wasapi.PlaybackStopped += (_, e) => { if (e.Exception != null) Fault = "Output stopped: " + e.Exception.Message; };
                wasapi.Init(monitor.Output);
                player = wasapi;
                wasapi.Play();
                OutputMs = 22;
                OutputInfo = "standard mode, ~22 ms queued";
            }
            // A sample waits ~5 ms on average for its 10 ms packet, then ~the buffer target, then the output queue.
            LagMs = CaptureMs / 2 + targetMs + OutputMs;

            capture.DataAvailable += OnAudio;
            capture.Failed += ex => Fault = ex.Message;
            capture.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Update(PedalboardSettings settings) => chain.Update(settings);
    public bool Mute { set => chain.Mute = value; }
    public (float In, float Out, bool Limiting, bool InputClipped) TakeMeters() => chain.TakeMeters();
    public int Underruns => monitor.Rate.Underruns;
    public int Recenters => monitor.Recenters;
    public double BufferLevelMs => monitor.LevelMs;

    /// <summary>Loudest raw input sample since <see cref="ResetInputMax"/> (for "Auto" boost).</summary>
    public float InputMax => Volatile.Read(ref inputMax);
    public void ResetInputMax() => Volatile.Write(ref inputMax, 0);

    /// <summary>Copies the most recent raw (unprocessed) input, oldest first, for the tuner.</summary>
    public int CopyRecentInput(float[] destination)
    {
        lock (recentLock)
        {
            int n = Math.Min(Math.Min(destination.Length, recentCount), RecentSamples);
            int start = (recentPos - n + RecentSamples) % RecentSamples;
            for (int i = 0; i < n; i++) destination[i] = recent[(start + i) % RecentSamples];
            return n;
        }
    }

    void OnAudio(byte[] data, int byteCount)
    {
        try
        {
            int frames = byteCount / (4 * channels);   // 32-bit float; the capture thread already has pro-audio scheduling
            if (mono.Length < frames) mono = new float[frames];
            ReadMono(data, frames);

            float max = 0;
            for (int i = 0; i < frames; i++) max = Math.Max(max, Math.Abs(mono[i]));
            if (max > Volatile.Read(ref inputMax)) Volatile.Write(ref inputMax, max);
            lock (recentLock)
            {
                for (int i = 0; i < frames; i++)
                {
                    recent[recentPos] = mono[i];
                    recentPos = (recentPos + 1) % RecentSamples;
                }
                recentCount = Math.Min(RecentSamples, recentCount + frames);
            }

            chain.Process(mono, frames);
            int length = AudioFeed.ToStereoBytes(mono, frames, ref stereoBytes);
            monitor.Push(stereoBytes, length);
            feed.Push(mono, frames, SampleRate);
        }
        catch (Exception ex)
        {
            Fault = ex.Message;
        }
    }

    /// <summary>Average of the first two channels (the cable sends the same signal on both).</summary>
    void ReadMono(byte[] data, int frames)
    {
        int stride = 4 * channels;
        int used = Math.Min(channels, 2);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < used; c++) sum += BitConverter.ToSingle(data, f * stride + c * 4);
            mono[f] = sum / used;
        }
    }

    public void Dispose()
    {
        capture?.Dispose();
        if (player is WasapiOut w) { try { w.Stop(); } catch { } }
        player?.Dispose();
    }
}
