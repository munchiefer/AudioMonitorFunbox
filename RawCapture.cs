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
    /// <summary>A packet of audio (buffer, bytes), on the capture thread.</summary>
    public event Action<byte[], int>? DataAvailable;
    /// <summary>The capture ended by itself (e.g. device unplugged, or another app took it exclusively).</summary>
    public event Action<Exception>? Failed;

    public RawCapture(string deviceId, bool raw)
    {
        object? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, true)!)!;
            Check(((IMMDeviceEnumerator)enumerator).GetDevice(deviceId, out device));
            var iid = typeof(IAudioClient2).GUID;
            Check(device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
            client = (IAudioClient2)obj;

            if (raw)
            {
                // AudioClientProperties { cbSize = 16, bIsOffload = 0, eCategory = Other (0), Options = RAW (1) }
                IntPtr properties = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.WriteInt32(properties, 0, 16);
                    Marshal.WriteInt32(properties, 4, 0);
                    Marshal.WriteInt32(properties, 8, 0);
                    Marshal.WriteInt32(properties, 12, 1);
                    IsRaw = client.SetClientProperties(properties) >= 0;   // not supported -> carry on with normal capture
                }
                finally { Marshal.FreeHGlobal(properties); }
            }

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
                // 200 ms of room, so a scheduling hiccup never loses audio; packets still arrive every engine period.
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

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}
