using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioMonitor;

/// <summary>
/// What identifies an audio device beyond its Windows ID, which can change (Windows updates, driver reinstalls,
/// another USB port). Saved next to each chosen ID, so the choice can be found again under a new ID.
///   Name:     the device's name with Windows' numbering removed ("2- USB Audio" → "USB Audio", "1 - Monitor" → "Monitor").
///   Hardware: maker, product and audio part, e.g. "USB\VID_1234&amp;PID_5678&amp;MI_00"; plus the serial number when
///             the device has one (it tells two identical devices apart). The USB-port part is left out on purpose.
///   Flow:     "Render" (plays) or "Capture" (records).
/// No personal data: these are the same model names and codes that Windows shows in Device Manager.
/// </summary>
public sealed class DeviceFingerprint
{
    public string Name { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string Flow { get; set; } = "";

    // PKEY_Device_InstanceId of the endpoint's audio device, e.g. "{1}.USB\VID_1234&PID_5678&MI_00\7&1A2B3C4D&0&0002".
    static readonly PropertyKey InstanceIdKey = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
    static readonly Regex Numbering = new(@"(?<=^|\()\d+\s?-\s", RegexOptions.CultureInvariant);

    public bool SameAs(DeviceFingerprint other) =>
        Name == other.Name && Hardware == other.Hardware && Flow == other.Flow;

    public bool IsValid => Name.Length is > 0 and <= 300 && Hardware.Length <= 300 && Flow is "Render" or "Capture";

    public static DeviceFingerprint? Of(MMDevice device)
    {
        try
        {
            string name = device.FriendlyName;
            string instance = "";
            try
            {
                var store = device.Properties;
                if (store.Contains(InstanceIdKey) && store[InstanceIdKey].Value is string s) instance = s;
            }
            catch { }
            return Make(name, instance, device.DataFlow == DataFlow.Capture ? "Capture" : "Render");
        }
        catch { return null; }
    }

    /// <summary>Builds a fingerprint from what Windows reports (public for tests).</summary>
    public static DeviceFingerprint Make(string friendlyName, string instanceId, string flow) => new()
    {
        Name = NormalizeName(friendlyName),
        Hardware = HardwareKey(instanceId),
        Flow = flow,
    };

    public static string NormalizeName(string name) => Numbering.Replace(name ?? "", "").Trim();

    /// <summary>
    /// "{1}.USB\VID_1234&amp;PID_5678&amp;MI_00\7&amp;1A2B3C4D&amp;0&amp;0002" → "USB\VID_1234&amp;PID_5678&amp;MI_00": the last part
    /// is made up from the USB port, unless it has no "&amp;", which means it's the device's serial number: kept.
    /// </summary>
    public static string HardwareKey(string instanceId)
    {
        string s = instanceId ?? "";
        if (s.StartsWith('{') && s.IndexOf("}.", StringComparison.Ordinal) is int close and > 0) s = s[(close + 2)..];
        int cut = s.LastIndexOf('\\');
        if (cut < 0) return s.ToUpperInvariant();
        string last = s[(cut + 1)..];
        return (last.Contains('&') ? s[..cut] : s).ToUpperInvariant();
    }

    /// <summary>
    /// Index of the one candidate that matches (same name, hardware and direction), or -1 when none does, or when
    /// several do: then it can't tell which one was meant, and doesn't guess.
    /// </summary>
    public static int FindMatch(DeviceFingerprint saved, IReadOnlyList<DeviceFingerprint> candidates)
    {
        if (!saved.IsValid) return -1;
        int found = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!candidates[i].SameAs(saved)) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }
}

/// <summary>
/// Windows' device notifications (plugged in, unplugged, default device changed), passed on to the UI thread.
/// The callback only posts: Windows forbids calling back into the device list from inside a notification.
/// </summary>
sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    readonly MMDeviceEnumerator enumerator = new();
    readonly SynchronizationContext context;
    readonly Action changed;
    bool disposed;

    /// <summary>Create on the UI thread: <paramref name="changed"/> runs there.</summary>
    public DeviceWatcher(Action changed)
    {
        this.changed = changed;
        context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        enumerator.RegisterEndpointNotificationCallback(this);
    }

    void Post() { if (!disposed) context.Post(_ => { if (!disposed) changed(); }, null); }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Post();
    public void OnDeviceAdded(string pwstrDeviceId) => Post();
    public void OnDeviceRemoved(string deviceId) => Post();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Post();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }   // very frequent, not needed

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        enumerator.Dispose();
    }
}
