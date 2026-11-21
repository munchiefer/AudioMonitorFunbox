using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AudioMonitor;

/// <summary>
/// Shared-mode WASAPI capture that can ask Windows for the RAW signal: the device's audio without Windows'
/// per-device effects. That matters for an instrument input: Windows 11 attaches "Voice Clarity" (speech noise
/// suppression) to USB capture devices like the Rocksmith Real Tone cable, and a speech denoiser rewrites a
/// guitar or bass signal (measured: the cable's steady signal faded to digital silence within 2 s through the
/// normal path, and stayed at its true level in raw mode). Other apps keep using the device normally.
/// Event-driven, on its own pro-audio thread; delivers 32-bit float packets (one per 10 ms engine period).
/// </summary>
sealed class RawCapture : IDisposable
{
    readonly IAudioClient2 client;
    readonly IAudioCaptureClient capture;
    readonly AutoResetEvent ready = new(false);
    readonly int frameBytes;
    Thread? thread;
    volatile bool stopping;
    byte[] buffer = Array.Empty<byte>();

    /// <summary>32-bit float at the device's mix rate and channel count.</summary>
    public WaveFormat WaveFormat { get; }
    /// <summary>True if Windows granted raw mode (false: the device doesn't support it, normal capture is used).</summary>
    public bool IsRaw { get; }
    /// <summary>
    /// How often packets arrive, in ms: the device's default engine period (10 ms for the Real Tone cable and most
    /// devices), or its minimum period when low-latency capture was granted. Packets are never longer; if another app
    /// runs the device faster, they are shorter (safe side).
    /// </summary>
    public double PacketMs { get; } = 10;
    /// <summary>True when the device runs at its minimum engine period (low-latency capture) for this stream.</summary>
    public bool LowLatency { get; }
    /// <summary>A packet of audio (buffer, bytes), on the capture thread.</summary>
    public event Action<byte[], int>? DataAvailable;
    /// <summary>The capture ended by itself (e.g. device unplugged, or another app took it exclusively).</summary>
    public event Action<Exception>? Failed;

    /// <param name="lowLatency">
    /// Ask for the device's minimum engine period when it's shorter than the default (onboard sound often allows
    /// ~3 ms instead of 10 ms). Anything that doesn't work out falls back to the normal 10 ms path.
    /// </param>
    public RawCapture(string deviceId, bool raw, bool lowLatency = false)
    {
        object? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, true)!)!;
            Check(((IMMDeviceEnumerator)enumerator).GetDevice(deviceId, out device));
            client = Activate(device);
            if (raw) IsRaw = AskRaw(client);   // not supported -> carry on with normal capture

            if (client.GetDevicePeriod(out long defaultPeriod, out _) >= 0 && defaultPeriod >= 10_000 && defaultPeriod <= 1_000_000)
                PacketMs = defaultPeriod / 10_000.0;   // 100 ns units; only plausible values (1..100 ms) are used

            Check(client.GetMixFormat(out IntPtr format));
            try
            {
                int tag = Marshal.ReadInt16(format, 0);
                int channels = Marshal.ReadInt16(format, 2);
                int rate = Marshal.ReadInt32(format, 4);
                int bits = Marshal.ReadInt16(format, 14);
                bool isFloat = tag == 3 || (tag == unchecked((short)0xFFFE) && new Guid(ReadBytes(format, 24, 16)) == FloatSubtype);
                if (!isFloat || bits != 32) throw new NotSupportedException("Device mix format is not 32-bit float.");
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
                frameBytes = 4 * channels;
                if (lowLatency && client is IAudioClient3 low
                    && low.GetSharedModeEnginePeriod(format, out uint defaultFrames, out _, out uint minFrames, out _) >= 0
                    && minFrames > 0 && minFrames < defaultFrames)
                {
                    // Low latency: the minimum engine period, with a buffer of at least 3 periods. On any failure (another
                    // app holds the device at another period, a tiny buffer, ...) start over with a fresh client below.
                    if (low.InitializeSharedAudioStream(0x00040000 /* EVENTCALLBACK */, minFrames, format, IntPtr.Zero) >= 0
                        && client.GetBufferSize(out uint bufferFrames) >= 0 && bufferFrames >= 3 * minFrames)
                    {
                        LowLatency = true;
                        PacketMs = minFrames * 1000.0 / rate;
                    }
                    else
                    {
                        Marshal.ReleaseComObject(client);
                        client = Activate(device);
                        if (raw) IsRaw = AskRaw(client);
                    }
                }
                // The normal path: 200 ms of room, so a scheduling hiccup never loses audio; packets still arrive every engine period.
                if (!LowLatency)
                    Check(client.Initialize(0 /* shared */, 0x00040000 /* EVENTCALLBACK */, 2_000_000, 0, format, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(format); }

            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            var captureIid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureIid, out var captureObj));
            capture = (IAudioCaptureClient)captureObj;
        }
        catch
        {
            // E.g. the device is busy (another app has it in exclusive mode): release what was opened right away,
            // instead of leaving it to the garbage collector while the owner retries every few seconds.
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            ready.Dispose();
            throw;
        }
        finally
        {
            // Only needed to open the audio client, which keeps its own reference to the device.
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    static IAudioClient2 Activate(IMMDevice device)
    {
        var iid = typeof(IAudioClient2).GUID;
        Check(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
        return (IAudioClient2)obj;
    }

    /// <summary>Asks for raw mode (no Windows per-device effects); true if granted.</summary>
    static bool AskRaw(IAudioClient2 client)
    {
        // AudioClientProperties { cbSize = 16, bIsOffload = 0, eCategory = Other (0), Options = RAW (1) }
        IntPtr properties = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(properties, 0, 16);
            Marshal.WriteInt32(properties, 4, 0);
            Marshal.WriteInt32(properties, 8, 0);
            Marshal.WriteInt32(properties, 12, 1);
            return client.SetClientProperties(properties) >= 0;
        }
        finally { Marshal.FreeHGlobal(properties); }
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "RawCapture" };
        thread.Start();
    }

    void Run()
    {
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            Check(client.Start());
            while (!stopping)
            {
                ready.WaitOne(200);
                if (stopping) break;
                while (true)
                {
                    Check(capture.GetNextPacketSize(out int next));
                    if (next == 0) break;
                    Check(capture.GetBuffer(out IntPtr data, out int frames, out int flags, out _, out _));
                    int bytes = frames * frameBytes;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((flags & 2) != 0) Array.Clear(buffer, 0, bytes);   // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buffer, 0, bytes);
                    Check(capture.ReleaseBuffer(frames));
                    DataAvailable?.Invoke(buffer, bytes);
                }
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

    public void Dispose()
    {
        stopping = true;
        ready.Set();
        thread?.Join(1000);
        Marshal.ReleaseComObject(capture);
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
    static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    // ---- interop ----

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

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

    [ComImport, Guid("726778CD-F60A-4eda-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient2
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

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}
