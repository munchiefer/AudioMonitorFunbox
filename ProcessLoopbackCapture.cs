using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMonitor;

/// <summary>
/// Captures the audio rendered by one process (and its child processes) using the
/// Windows 10 2004+ process-loopback API. Output format: 16-bit stereo PCM at the requested rate.
/// </summary>
sealed class ProcessLoopbackCapture : IDisposable
{
    const string VirtualDevice = "VAD\\Process_Loopback";
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    public int ProcessId { get; }
    public WaveFormat WaveFormat { get; }
    public event Action<byte[], int>? DataAvailable;
    public event Action<Exception>? Failed;

    Thread? thread;
    volatile bool stopping;

    public ProcessLoopbackCapture(int processId, int sampleRate)
    {
        ProcessId = processId;
        WaveFormat = new WaveFormat(sampleRate, 16, 2);
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = $"ProcessCapture {ProcessId}" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    void Run()
    {
        AudioClient? client = null;
        // Must outlive the audio client (it's registered with it), so it's disposed last in 'finally'.
        var dataReady = new AutoResetEvent(false);
        bool started = false;
        try
        {
            client = Activate(ProcessId);
            var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback
                        | unchecked((AudioClientStreamFlags)0x80000000)  // AUTOCONVERTPCM
                        | unchecked((AudioClientStreamFlags)0x08000000); // SRC_DEFAULT_QUALITY
            client.Initialize(AudioClientShareMode.Shared, flags, 200_000, 0, WaveFormat, Guid.Empty);
            client.SetEventHandle(dataReady.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            client.Start();
            started = true;

            int blockAlign = WaveFormat.BlockAlign;
            var buffer = new byte[WaveFormat.AverageBytesPerSecond / 2];
            while (!stopping)
            {
                dataReady.WaitOne(100);
                while (!stopping && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var bufferFlags);
                    int bytes = frames * blockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((bufferFlags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    if (bytes > 0) DataAvailable?.Invoke(buffer, bytes);
                }
            }
        }
        catch (Exception ex)
        {
            if (!stopping) Failed?.Invoke(ex);
        }
        finally
        {
            if (started) { try { client!.Stop(); } catch { } }
            client?.Dispose();
            dataReady.Dispose();
        }
    }

    static AudioClient Activate(int processId)
    {
        var activation = new ActivationParams
        {
            ActivationType = 1,          // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0,     // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        int paramsSize = Marshal.SizeOf<ActivationParams>();
        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        var handler = new CompletionHandler();
        try
        {
            Marshal.StructureToPtr(activation, pParams, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = paramsSize, pBlobData = pParams }, pVariant, false);

            ActivateAudioInterfaceAsync(VirtualDevice, IID_IAudioClient, pVariant, handler, out var operation);
            if (!handler.Done.WaitOne(5000) && handler.Abandon())
                throw new TimeoutException("Process loopback activation timed out.");

            operation.GetActivateResult(out int hr, out object audioClient);
            Marshal.ThrowExceptionForHR(hr);
            return new AudioClient((NAudio.CoreAudioApi.Interfaces.IAudioClient)audioClient);
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
            // Safe even after a timeout: a late completion sees "abandoned" and never touches the event.
            handler.Done.Dispose();
        }
    }

    public void Dispose()
    {
        stopping = true;
        thread?.Join(1000);
    }

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public int cbSize;
        public IntPtr pBlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        int state;   // 0 = waiting, 1 = completed, 2 = caller gave up

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) == 2)
            {
                // Nobody is waiting any more: release the late audio client instead of leaking it.
                try
                {
                    activateOperation.GetActivateResult(out _, out object late);
                    if (late != null) Marshal.ReleaseComObject(late);
                }
                catch { }
                return;
            }
            Done.Set();
        }

        /// <summary>Called on timeout. Returns false if the activation completed in the meantime (use it).</summary>
        public bool Abandon()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0) return true;
            Done.WaitOne();
            return false;
        }
    }
}
