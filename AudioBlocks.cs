using NAudio.Wave;

namespace AudioMonitor;

// Audio building blocks shared by the speaker copy (AudioEngine) and the mic mixer (MicMixer).

/// <summary>
/// Plays its (stereo) source very slightly faster or slower (max ±0.3 %, inaudible) so that the
/// buffer it reads from stays at the target level. This cancels clock drift between devices
/// without dropping or inserting audio. Uses 4-point Hermite interpolation. Never returns short.
/// </summary>
sealed class AdaptiveRate : ISampleProvider
{
    const double MaxAdjust = 0.003;
    const double Gain = 0.0002;       // per ms of error: 5 ms off -> 0.1 % faster
    const double Smoothing = 0.02;    // level is a sawtooth (packets in, periods out) -> average it
    const double IntegralGain = 0.00001;  // per ms*s of error: removes the steady-state offset of P-only control

    readonly ISampleProvider source;
    readonly BufferedWaveProvider level;
    readonly Func<int> target;
    float[] frames = new float[2 * 4096];   // source frames not yet consumed (interleaved stereo)
    int frameCount = 1;                      // one zero frame of history to start
    double position = 1;                     // fractional frame index into 'frames'
    double averageLevelMs;
    double integral;
    public double LastRatio { get; private set; } = 1;

    public AdaptiveRate(ISampleProvider source, BufferedWaveProvider level, Func<int> target)
    {
        this.source = source;
        this.level = level;
        this.target = target;
        averageLevelMs = target();
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public void ShiftLevel(int ms) => averageLevelMs += ms;

    public void Reset()
    {
        averageLevelMs = target();
        integral = 0;
    }

    public int ReadCalls;
    public int Underruns;
    public Exception? LastError;

    public int Read(float[] buffer, int offset, int count)
    {
        ReadCalls++;
        try { return ReadCore(buffer, offset, count); }
        catch (Exception ex) { LastError = ex; Array.Clear(buffer, offset, count); return count; }
    }

    int ReadCore(float[] buffer, int offset, int count)
    {
        int outFrames = count / 2;
        if (level.BufferedBytes < outFrames * level.WaveFormat.BlockAlign) Underruns++;
        averageLevelMs += (level.BufferedDuration.TotalMilliseconds - averageLevelMs) * Smoothing;
        double error = averageLevelMs - target();
        double limit = MaxAdjust / IntegralGain;
        integral = Math.Clamp(integral + error * outFrames / WaveFormat.SampleRate, -limit, limit);
        double ratio = 1 + Math.Clamp(error * Gain + integral * IntegralGain, -MaxAdjust, MaxAdjust);
        LastRatio = ratio;

        // Make sure we hold every source frame the interpolation will touch.
        int needed = (int)Math.Ceiling(position + outFrames * ratio) + 3;
        if (frames.Length < needed * 2) Array.Resize(ref frames, needed * 2);
        if (needed > frameCount)
        {
            int want = (needed - frameCount) * 2;
            int got = source.Read(frames, frameCount * 2, want);
            if (got < want) Array.Clear(frames, frameCount * 2 + got, want - got);
            frameCount = needed;
        }

        for (int i = 0; i < outFrames; i++)
        {
            int i0 = (int)position;
            float t = (float)(position - i0);
            int b = (i0 - 1) * 2;
            for (int c = 0; c < 2; c++)
            {
                float ym1 = frames[b + c], y0 = frames[b + 2 + c], y1 = frames[b + 4 + c], y2 = frames[b + 6 + c];
                float c1 = 0.5f * (y1 - ym1);
                float c2 = ym1 - 2.5f * y0 + 2f * y1 - 0.5f * y2;
                float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
                buffer[offset + i * 2 + c] = ((c3 * t + c2) * t + c1) * t + y0;
            }
            position += ratio;
        }

        // Discard consumed frames, keeping one frame of history before the current position.
        int drop = (int)position - 1;
        if (drop > 0)
        {
            Array.Copy(frames, drop * 2, frames, 0, (frameCount - drop) * 2);
            frameCount -= drop;
            position -= drop;
        }
        if (count > outFrames * 2) buffer[offset + count - 1] = 0;
        return count;
    }
}

/// <summary>Remembers the loudest sample that passed through, for diagnostics.</summary>
sealed class PeakMeter : ISampleProvider
{
    readonly ISampleProvider source;
    float peak;
    public PeakMeter(ISampleProvider source) => this.source = source;
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Take() => Interlocked.Exchange(ref peak, 0);

    public int Read(float[] buffer, int offset, int count)
    {
        int n = source.Read(buffer, offset, count);
        float p = 0;
        for (int i = offset; i < offset + n; i++) p = Math.Max(p, Math.Abs(buffer[i]));
        if (p > Volatile.Read(ref peak)) Volatile.Write(ref peak, p);
        return n;
    }
}

/// <summary>Takes front-left/front-right from a multichannel stream.</summary>
sealed class FirstTwoChannels : ISampleProvider
{
    readonly ISampleProvider source;
    readonly int channels;
    float[] temp = Array.Empty<float>();

    public FirstTwoChannels(ISampleProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int needed = frames * channels;
        if (temp.Length < needed) temp = new float[needed];
        int read = source.Read(temp, 0, needed) / channels;
        for (int i = 0; i < read; i++)
        {
            buffer[offset + i * 2] = temp[i * channels];
            buffer[offset + i * 2 + 1] = temp[i * channels + 1];
        }
        if (read < frames) Array.Clear(buffer, offset + read * 2, (frames - read) * 2);
        return frames * 2;
    }
}

/// <summary>
/// Buffer between a capture device and an output device that runs on its own clock. Keeps a target
/// fill level through <see cref="AdaptiveRate"/> (clock drift), starts exactly at that level on the
/// first audio, and heals itself: after a gap (a lost packet, e.g. the Rocksmith cable drops one now
/// and then) that ran it dry it refills immediately, and after a stall it drops the backlog.
/// </summary>
sealed class DriftBuffer
{
    readonly BufferedWaveProvider buffer;
    bool primed;
    int seenUnderruns;

    public readonly AdaptiveRate Rate;
    /// <summary>Stereo float at the output rate; never returns short.</summary>
    public ISampleProvider Output => Rate;
    public int TargetMs { get; }
    public int InputRate => buffer.WaveFormat.SampleRate;
    /// <summary>Diagnostics: how often a gap was healed.</summary>
    public int Recenters { get; private set; }

    public DriftBuffer(WaveFormat inputFormat, int outputRate, int targetMs)
    {
        TargetMs = targetMs;
        buffer = new BufferedWaveProvider(inputFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        ISampleProvider sp = buffer.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new FirstTwoChannels(sp);
        if (sp.WaveFormat.SampleRate != outputRate) sp = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, outputRate);
        Rate = new AdaptiveRate(sp, buffer, () => TargetMs);
    }

    public void Push(byte[] data, int count)
    {
        int level = (int)buffer.BufferedDuration.TotalMilliseconds;
        int underruns = Rate.Underruns;
        if (!primed || level > TargetMs + 40)
        {
            // First audio, or after a stall/burst: restart exactly at the target level.
            buffer.ClearBuffer();
            AddSilence(TargetMs);
            Rate.Reset();
            primed = true;
            seenUnderruns = underruns;
        }
        else if (underruns != seenUnderruns)
        {
            // A gap ran the buffer dry: refill to the target now, instead of crackling for seconds
            // while the (deliberately slow) drift correction catches up.
            seenUnderruns = underruns;
            if (level < TargetMs)
            {
                AddSilence(TargetMs - level);
                Rate.ShiftLevel(TargetMs - level);
                Recenters++;
            }
        }
        buffer.AddSamples(data, 0, count);
    }

    public double LevelMs => buffer.BufferedDuration.TotalMilliseconds;

    void AddSilence(int ms)
    {
        int bytes = (int)((long)buffer.WaveFormat.AverageBytesPerSecond * ms / 1000);
        bytes -= bytes % buffer.WaveFormat.BlockAlign;
        if (bytes > 0) buffer.AddSamples(new byte[bytes], 0, bytes);
    }
}
