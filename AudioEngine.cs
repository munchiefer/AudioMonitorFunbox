using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioMonitor;

/// <summary>
/// Captures audio (whole device loopback or selected processes) and plays a copy on another output,
/// optionally delayed so it lines up with a slower source device (e.g. Bluetooth headphones as the
/// Windows default output). Each input has its own buffer ("tap"); a tiny adaptive resampler keeps it
/// at its target level, so clock drift between devices never builds up lag and never needs audible trims.
/// </summary>
sealed class AudioEngine : IDisposable
{
    /// <summary>
    /// Jitter buffer per tap: enough for one output read (an engine period) plus one capture packet,
    /// plus a small margin. Recomputed once the capture packet size is known.
    /// </summary>
    int baseBufferMs;
    int delayMs;

    /// <summary>
    /// Beyond these errors (startup, a capture gap, a stall) the buffer is snapped straight back to its
    /// target instead of being slowly steered, so the speakers/headphones alignment stays exact.
    /// Normal packet jitter (~10 ms) never reaches them.
    /// </summary>
    const int ResyncBelowMs = 25;
    const int ResyncAboveMs = 40;

    readonly List<Output> outputs = new();
    readonly Output monitorOutput = null!;
    readonly int captureRate;

    WasapiCapture? loopback;
    Input? loopbackInput;
    readonly Dictionary<int, Input> processInputs = new();
    readonly HashSet<int> failedPids = new();
    readonly ConcurrentQueue<(int Pid, string Error)> failures = new();

    /// <summary>Set (from any thread) when the engine can no longer run; the owner restarts it.</summary>
    public volatile string? Fault;
    public string? LastAppError { get; private set; }
    public int ActiveAppStreams => processInputs.Count;

    sealed class Output
    {
        public required string Name;
        public required IDisposable Player;
        public required Action Play;
        public required double PeriodMs;
        public required double QueuedMs;
        public required bool LowLatency;
        public required MixingSampleProvider Mixer;
        public required VolumeSampleProvider Volume;
        public required PeakMeter Meter;
        public required int SampleRate;
        public volatile int TargetMs;
    }

    sealed class Tap
    {
        public required Output Output;
        public required BufferedWaveProvider Buffer;
        public required AdaptiveRate Rate;
        public bool Primed;
    }

    sealed class Input
    {
        public readonly List<Tap> Taps = new();
        public ProcessLoopbackCapture? Capture;
    }

    /// <param name="output">Device the copy is played on (e.g. headphones or speakers).</param>
    /// <param name="delayMs">Extra delay for the copy, to line it up with a slower source device.</param>
    public AudioEngine(MMDevice output, int delayMs, float volume, int latencyMs)
    {
        latencyMs = Math.Clamp(latencyMs, 0, 200);
        try
        {
            monitorOutput = CreateOutput(output, "Copy", volume, latencyMs);
            captureRate = monitorOutput.SampleRate;
            baseBufferMs = BaseBufferFor(captureMs: 10);
            SetTargets(delayMs);
            foreach (var o in outputs) o.Play();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <param name="latencyMs">Audio to keep queued on the device; 0 = the minimum it allows.</param>
    Output CreateOutput(MMDevice device, string name, float vol, int latencyMs)
    {
        LowLatencyOutput? low = null;
        try { low = new LowLatencyOutput(device.ID, latencyMs, lowLatencyEngine: true); }
        catch { low = null; }   // older driver / unusual format: fall back to standard shared mode

        int rate = low?.SampleRate ?? device.AudioClient.MixFormat.SampleRate;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = true };
        var meter = new PeakMeter(mixer);
        var volume = new VolumeSampleProvider(meter) { Volume = vol };
        Output output;
        if (low != null)
        {
            low.Failed += ex => Fault = $"{name} output stopped: {ex.Message}";
            output = new Output
            {
                Name = name, Player = low, Play = () => low.Start(volume), PeriodMs = low.PeriodMs, QueuedMs = low.QueuedMs,
                LowLatency = true, Mixer = mixer, Volume = volume, Meter = meter, SampleRate = rate,
            };
        }
        else
        {
            var player = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Max(latencyMs, 10));
            player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception != null) Fault = $"{name} output stopped: {e.Exception.Message}";
            };
            try { player.Init(volume); }
            catch { player.Dispose(); throw; }
            output = new Output
            {
                Name = name, Player = player, Play = player.Play, PeriodMs = 10, QueuedMs = 22,
                LowLatency = false, Mixer = mixer, Volume = volume, Meter = meter, SampleRate = rate,
            };
        }
        outputs.Add(output);
        return output;
    }

    // A read takes one output period of audio, and packets arrive every captureMs at a drifting phase:
    // keep one full read, plus half a packet of phase wobble, plus 2 ms margin (measured: no underruns).
    int BaseBufferFor(double captureMs) =>
        (int)Math.Ceiling(Math.Max(monitorOutput.PeriodMs, captureMs) + captureMs / 2 + 2);

    /// <summary>Diagnostics: reads that found less audio buffered than requested (audible as crackle).</summary>
    public int Underruns => AllInputs().SelectMany(i => i.Taps).Sum(t => t.Rate.Underruns);

    /// <summary>What the copy device actually queues, and whether low-latency mode is active.</summary>
    public string OutputInfo => monitorOutput.LowLatency
        ? $"low-latency, {monitorOutput.QueuedMs:0.#} ms queued"
        : "standard mode, ~22 ms queued";

    public float Volume { set => monitorOutput.Volume.Volume = value; }

    /// <summary>Peak level (before volume) of the copy since the last call.</summary>
    public float TakePeak() => monitorOutput.Meter.Take();

    void SetTargets(int delay)
    {
        delayMs = Math.Clamp(delay, 0, Settings.MaxCopyDelayMs);
        monitorOutput.TargetMs = baseBufferMs + delayMs;
    }

    /// <summary>Change the copy's delay live, without restarting the audio (so it can be tuned by ear).</summary>
    public void SetDelay(int delayMs)
    {
        var before = outputs.ToDictionary(o => o, o => o.TargetMs);
        SetTargets(delayMs);
        foreach (var input in AllInputs())
            foreach (var tap in input.Taps)
            {
                int delta = tap.Output.TargetMs - before[tap.Output];
                if (delta > 0) AddSilence(tap.Buffer, delta);
                else if (delta < 0) Drop(tap.Buffer, -delta);
                tap.Rate.ShiftLevel(delta);
            }
    }

    IEnumerable<Input> AllInputs()
    {
        if (loopbackInput != null) yield return loopbackInput;
        foreach (var i in processInputs.Values) yield return i;
    }

    /// <summary>Capture everything rendered on <paramref name="source"/>.</summary>
    /// <remarks>
    /// The source device is deliberately left at its normal engine period. Forcing it to its minimum
    /// period makes capture packets smaller, but also makes the source itself play earlier, so the gap
    /// to the copy doesn't shrink (measured with a metronome).
    /// </remarks>
    public void StartDeviceLoopback(MMDevice source)
    {
        baseBufferMs = BaseBufferFor(captureMs: 10);
        SetTargets(delayMs);

        loopback = new FastLoopbackCapture(source);
        var input = loopbackInput = CreateInput(loopback.WaveFormat);
        loopback.DataAvailable += (_, e) => Push(input, e.Buffer, e.BytesRecorded);
        loopback.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Fault = "Source capture stopped: " + e.Exception.Message;
        };
        loopback.StartRecording();
    }

    /// <summary>Make the set of captured processes match <paramref name="pids"/>. Call on the UI thread.</summary>
    public void SyncProcesses(ICollection<int> pids)
    {
        while (failures.TryDequeue(out var f))
        {
            RemoveProcess(f.Pid);
            failedPids.Add(f.Pid);
            LastAppError = f.Error;
        }

        foreach (var pid in processInputs.Keys.Where(p => !pids.Contains(p)).ToList())
            RemoveProcess(pid);
        failedPids.IntersectWith(pids);

        foreach (var pid in pids)
        {
            if (processInputs.ContainsKey(pid) || failedPids.Contains(pid)) continue;

            var capture = new ProcessLoopbackCapture(pid, captureRate);
            var input = CreateInput(capture.WaveFormat);
            input.Capture = capture;
            capture.DataAvailable += (data, count) => Push(input, data, count);
            capture.Failed += ex => failures.Enqueue((pid, ex.Message));
            processInputs[pid] = input;
            capture.Start();
        }
    }

    void RemoveProcess(int pid)
    {
        if (!processInputs.Remove(pid, out var input)) return;
        input.Capture?.Dispose();
        foreach (var tap in input.Taps) tap.Output.Mixer.RemoveMixerInput(tap.Rate);
    }

    Input CreateInput(WaveFormat format)
    {
        var input = new Input();
        foreach (var output in outputs)
        {
            var buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            AddSilence(buffer, output.TargetMs);   // start exactly at the target delay

            ISampleProvider sp = buffer.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > 2) sp = new FirstTwoChannels(sp);
            if (sp.WaveFormat.SampleRate != output.SampleRate) sp = new WdlResamplingSampleProvider(sp, output.SampleRate);
            var tap = new Tap { Output = output, Buffer = buffer, Rate = new AdaptiveRate(sp, buffer, () => output.TargetMs) };
            input.Taps.Add(tap);
            output.Mixer.AddMixerInput(tap.Rate);
        }
        return input;
    }

    static void Push(Input input, byte[] data, int count)
    {
        foreach (var tap in input.Taps)
        {
            int target = tap.Output.TargetMs;
            int level = (int)tap.Buffer.BufferedDuration.TotalMilliseconds;
            if (!tap.Primed)
            {
                // First real audio: start at exactly the target delay, however long the capture took to start.
                tap.Buffer.ClearBuffer();
                AddSilence(tap.Buffer, target);
                tap.Rate.Reset();
                tap.Primed = true;
            }
            else if (level < target - ResyncBelowMs)
            {
                AddSilence(tap.Buffer, target - level);   // after a gap in the source audio
                tap.Rate.ShiftLevel(target - level);
            }
            else if (level > target + ResyncAboveMs)
            {
                Drop(tap.Buffer, level - target);          // after a stall/burst
                tap.Rate.ShiftLevel(target - level);
            }
            tap.Buffer.AddSamples(data, 0, count);
        }
    }

    static int Bytes(WaveFormat f, int ms)
    {
        int bytes = (int)((long)f.AverageBytesPerSecond * ms / 1000);
        return bytes - bytes % f.BlockAlign;
    }

    static void AddSilence(BufferedWaveProvider buffer, int ms)
    {
        int bytes = Bytes(buffer.WaveFormat, ms);
        if (bytes > 0) buffer.AddSamples(new byte[bytes], 0, bytes);
    }

    static void Drop(BufferedWaveProvider buffer, int ms)
    {
        int bytes = Math.Min(Bytes(buffer.WaveFormat, ms), buffer.BufferedBytes);
        bytes -= bytes % buffer.WaveFormat.BlockAlign;
        if (bytes > 0) buffer.Read(new byte[bytes], 0, bytes);
    }

    /// <summary>Diagnostics: current buffer level per output for the device loopback input.</summary>
    public string DebugLevels() => loopbackInput == null ? "" :
        string.Join(", ", loopbackInput.Taps.Select(t => $"{t.Output.Name}: {t.Buffer.BufferedDuration.TotalMilliseconds:F1}/{t.Output.TargetMs}ms x{t.Rate.LastRatio:F5} reads={t.Rate.ReadCalls} err={t.Rate.LastError?.GetType().Name}: {t.Rate.LastError?.Message}"));

    /// <summary>Diagnostics: buffer level vs target for every input/output pair.</summary>
    public string DebugLevelsAll() => string.Join("; ", AllInputs().SelectMany(i => i.Taps)
        .Select(t => $"{t.Output.Name} {t.Buffer.BufferedDuration.TotalMilliseconds:F0}/{t.Output.TargetMs}ms"));

    public void Dispose()
    {
        try { loopback?.StopRecording(); } catch { }
        loopback?.Dispose();
        foreach (var pid in processInputs.Keys.ToList()) RemoveProcess(pid);
        foreach (var o in outputs)
        {
            try { (o.Player as WasapiOut)?.Stop(); } catch { }
            o.Player.Dispose();
        }
    }

    /// <summary>
    /// Event-driven loopback with a 10 ms buffer. NAudio's WasapiLoopbackCapture polls a 100 ms
    /// buffer and delivers ~60 ms chunks, which added a lot of lag.
    /// </summary>
    sealed class FastLoopbackCapture : WasapiCapture
    {
        public FastLoopbackCapture(MMDevice device) : base(device, true, 10) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
    }
}
