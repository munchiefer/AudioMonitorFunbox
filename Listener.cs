using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMonitor;

/// <summary>
/// "Listen to VB-CABLE": plays what the mic mixer sends into VB-CABLE (exactly what Discord hears, effects
/// included) on a device of your choice, e.g. your headphones. It takes the audio straight from the mixer,
/// before VB-CABLE, which saves a round trip through the virtual cable. The mixer's output device and the
/// listening device run on separate clocks, so a small self-healing, drift-corrected buffer sits in between.
/// </summary>
sealed class Listener : IDisposable
{
    readonly IDisposable? player;
    readonly IDisposable? subscription;
    readonly int outputRate;
    readonly double outputPeriodMs;
    volatile DriftBuffer? buffer;
    readonly double chunkMs;
    int retiredUnderruns, retiredRecenters;
    byte[] bytes = Array.Empty<byte>();

    /// <summary>Set (from any thread) when listening can no longer run; the owner restarts it.</summary>
    public volatile string? Fault;
    public string OutputInfo { get; } = "";
    public double OutputMs { get; }
    public int BufferTargetMs => buffer?.TargetMs ?? 0;
    public int Underruns => retiredUnderruns + (buffer?.Rate.Underruns ?? 0);
    public int Recenters => retiredRecenters + (buffer?.Recenters ?? 0);

    /// <param name="chunkHintMs">How much audio the mixer hands over at a time (its output period), if known.</param>
    public Listener(MMDevice output, int latencyMs, AudioFeed feed, double chunkHintMs)
    {
        try
        {
            LowLatencyOutput? low = null;
            try { low = new LowLatencyOutput(output.ID, latencyMs, lowLatencyEngine: true); }
            catch { low = null; }
            outputRate = low?.SampleRate ?? output.AudioClient.MixFormat.SampleRate;
            outputPeriodMs = low?.PeriodMs ?? 10;
            var source = new BufferReader(this);
            chunkMs = Math.Max(chunkHintMs, 2);
            Rebuild(48000);

            if (low != null)
            {
                low.Failed += ex => Fault = "Output stopped: " + ex.Message;
                player = low;
                low.Start(source);
                OutputMs = low.QueuedMs;
                OutputInfo = $"low-latency, {low.QueuedMs:0.#} ms queued";
            }
            else
            {
                var wasapi = new WasapiOut(output, AudioClientShareMode.Shared, true, Math.Max(latencyMs, 10));
                wasapi.PlaybackStopped += (_, e) => { if (e.Exception != null) Fault = "Output stopped: " + e.Exception.Message; };
                wasapi.Init(source);
                player = wasapi;
                wasapi.Play();
                OutputMs = 22;
                OutputInfo = "standard mode, ~22 ms queued";
            }
            subscription = feed.Subscribe(OnAudio);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Buffer target: the mixer usually hands over one period at a time but now and then two (measured), so two
    /// chunks of wobble, plus one output read and a margin (and half a period more for devices that read in big
    /// 10 ms chunks, as measured on the pedalboard). Sized once, so chunk-size jitter never forces a rebuild.
    /// </summary>
    void Rebuild(int inputRate)
    {
        var old = buffer;
        if (old != null) { retiredUnderruns += old.Rate.Underruns; retiredRecenters += old.Recenters; }
        int target = (int)Math.Ceiling(2 * chunkMs + outputPeriodMs + 4 + (outputPeriodMs > 5 ? outputPeriodMs / 2 : 0));
        buffer = new DriftBuffer(WaveFormat.CreateIeeeFloatWaveFormat(inputRate, 2), outputRate, target);
    }

    /// <summary>The mixer's output, as it is sent to VB-CABLE (on the mixer's output thread).</summary>
    void OnAudio(float[] mono, int count, int rate)
    {
        var b = buffer!;
        if (b.InputRate != rate)
        {
            Rebuild(rate);   // the mixer restarted at a different sample rate
            b = buffer!;
        }
        int length = AudioFeed.ToStereoBytes(mono, count, ref bytes);
        b.Push(bytes, length);
    }

    public void Dispose()
    {
        subscription?.Dispose();
        if (player is WasapiOut w) { try { w.Stop(); } catch { } }
        player?.Dispose();
    }

    /// <summary>Reads whatever buffer is current (it can be replaced when the mixer restarts).</summary>
    sealed class BufferReader(Listener owner) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(owner.outputRate, 2);
        public int Read(float[] samples, int offset, int count) => owner.buffer!.Output.Read(samples, offset, count);
    }
}
