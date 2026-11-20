using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AudioMonitor;

/// <summary>
/// Shared-mode WASAPI renderer using IAudioClient3 (Windows 10+), so the device's audio engine can run
/// at its minimum period (often ~3 ms instead of the default 10 ms) and only a couple of periods are
/// queued. Plain shared mode (NAudio's WasapiOut) always queues ~22 ms whatever latency is requested.
/// Other apps keep using the device normally (this is not exclusive mode).
/// </summary>
sealed class LowLatencyOutput : IDisposable
{
    readonly IAudioClient3 client;
    readonly IAudioRenderClient render;
    readonly AutoResetEvent ready = new(false);
    readonly int channels;
    readonly int bufferFrames;
    readonly int targetFrames;
    ISampleProvider? source;
    Thread? thread;
    volatile bool stopping;
    float[] scratch = Array.Empty<float>();

    public int SampleRate { get; }
    public double PeriodMs { get; }
    public double QueuedMs => targetFrames * 1000.0 / SampleRate;
    public event Action<Exception>? Failed;

    /// <param name="targetMs">Audio to keep queued. 0 = as little as is safe (one engine period + margin).</param>
    /// <param name="lowLatencyEngine">Ask for the device's minimum engine period (otherwise its default).</param>
    public LowLatencyOutput(string deviceId, int targetMs, bool lowLatencyEngine)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, true)!)!;
        Check(enumerator.GetDevice(deviceId, out var device));
        var iid = typeof(IAudioClient3).GUID;
        Check(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
        client = (IAudioClient3)obj;

        Check(client.GetMixFormat(out IntPtr format));
        try
        {
            int tag = Marshal.ReadInt16(format, 0);
            channels = Marshal.ReadInt16(format, 2);
            SampleRate = Marshal.ReadInt32(format, 4);
            int bits = Marshal.ReadInt16(format, 14);
            bool isFloat = tag == 3 || (tag == unchecked((short)0xFFFE) && new Guid(ReadBytes(format, 24, 16)) == FloatSubtype);
            if (!isFloat || bits != 32) throw new NotSupportedException("Device mix format is not 32-bit float.");

            Check(client.GetSharedModeEnginePeriod(format, out uint defaultPeriod, out _, out uint minPeriod, out _));
            uint period = lowLatencyEngine ? minPeriod : defaultPeriod;
            int hr = client.InitializeSharedAudioStream(0x00040000 /* EVENTCALLBACK */, period, format, IntPtr.Zero);
            if (hr == unchecked((int)0x88890028) /* AUDCLNT_E_ENGINE_PERIODICITY_LOCKED */)
            {
                // Another app already runs this device at a fixed period: use that one.
                Check(client.GetCurrentSharedModeEnginePeriod(out IntPtr current, out period));
                Marshal.FreeCoTaskMem(current);
                hr = client.InitializeSharedAudioStream(0x00040000, period, format, IntPtr.Zero);
            }
            Check(hr);
            PeriodMs = period * 1000.0 / SampleRate;
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }

        Check(client.GetBufferSize(out uint size));
        bufferFrames = (int)size;
        // At least one engine period plus a safety margin (the engine pulls a whole period at a time).
        double minMs = PeriodMs + Math.Max(2.5, PeriodMs * 0.25);
        int minFrames = (int)Math.Ceiling(minMs * SampleRate / 1000);
        targetFrames = Math.Clamp(targetMs * SampleRate / 1000, minFrames, bufferFrames);

        Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
        var renderIid = typeof(IAudioRenderClient).GUID;
        Check(client.GetService(ref renderIid, out var renderObj));
        render = (IAudioRenderClient)renderObj;
    }

    /// <summary><paramref name="stereo"/> must be 32-bit float stereo at <see cref="SampleRate"/>.</summary>
    public void Start(ISampleProvider stereo)
    {
        source = stereo;
        thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "LowLatencyOutput" };
        thread.Start();
    }

    void Run()
    {
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            Fill();                 // pre-roll so the first period isn't an underrun
            Check(client.Start());
            while (!stopping)
            {
                ready.WaitOne(200);
                if (!stopping) Fill();
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            if (!stopping) Failed?.Invoke(ex);
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    void Fill()
    {
        Check(client.GetCurrentPadding(out uint padding));
        int frames = targetFrames - (int)padding;
        if (frames <= 0) return;
        if (scratch.Length < frames * 2) scratch = new float[frames * 2];
        int read = source!.Read(scratch, 0, frames * 2);
        if (read < frames * 2) Array.Clear(scratch, read, frames * 2 - read);

        Check(render.GetBuffer((uint)frames, out IntPtr data));
        if (channels == 2)
        {
            Marshal.Copy(scratch, 0, data, frames * 2);
        }
        else
        {
            // Left/right into the first two channels (or both into a mono device), silence elsewhere.
            var outBuf = new float[frames * channels];
            for (int i = 0; i < frames; i++)
            {
                if (channels == 1) outBuf[i] = 0.5f * (scratch[i * 2] + scratch[i * 2 + 1]);
                else { outBuf[i * channels] = scratch[i * 2]; outBuf[i * channels + 1] = scratch[i * 2 + 1]; }
            }
            Marshal.Copy(outBuf, 0, data, outBuf.Length);
        }
        Check(render.ReleaseBuffer((uint)frames, 0));
    }

    public void Dispose()
    {
        stopping = true;
        ready.Set();
        thread?.Join(1000);
        Marshal.ReleaseComObject(render);
        Marshal.ReleaseComObject(client);
        ready.Dispose();
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    static byte[] ReadBytes(IntPtr p, int offset, int count)
    {
        var b = new byte[count];
        Marshal.Copy(p + offset, b, 0, count);
        return b;
    }

    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    // ---- interop ----

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true)]
    static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient3
    {
        // IAudioClient
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        // IAudioClient2
        [PreserveSig] int IsOffloadCapable(int category, out int capable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
        // IAudioClient3
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriod, out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriod);
        [PreserveSig] int InitializeSharedAudioStream(int streamFlags, uint periodInFrames, IntPtr format, IntPtr sessionGuid);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, int flags);
    }
}
