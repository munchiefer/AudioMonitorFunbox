using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioMonitor;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MonitorMode { AllAudio, SelectedApps }

public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public MonitorMode Mode { get; set; } = MonitorMode.AllAudio;
    /// <summary>Render device to mirror in AllAudio mode. Empty = current default output device.</summary>
    public string SourceDeviceId { get; set; } = "";
    /// <summary>Render device the copy is played on (e.g. headphones).</summary>
    public string MonitorDeviceId { get; set; } = "";
    /// <summary>Process names (lowercase, no .exe) to mirror in SelectedApps mode.</summary>
    public List<string> Apps { get; set; } = new();
    public int Volume { get; set; } = 100;
    /// <summary>Monitor output buffer in ms. Lower = less lag, too low = crackling.</summary>
    public int LatencyMs { get; set; } = 20;
    /// <summary>
    /// Extra delay for the copy, so it lines up with a slower source device: e.g. Bluetooth headphones
    /// set as the Windows default output, copied to the (faster) speakers.
    /// </summary>
    public int CopyDelayMs { get; set; }
    public const int MaxCopyDelayMs = 600;

    // ---- Mic mixer (into VB-CABLE, for Discord) ----
    public bool MicEnabled { get; set; }
    /// <summary>Up to 4 inputs (mics, the Rocksmith cable, ...), each with a level in percent (0..200).</summary>
    public List<MicEntry> Mics { get; set; } = new();
    public MicMode MicMode { get; set; } = MicMode.AutoSwitch;
    /// <summary>Fun box: voice effects applied to the mic mix.</summary>
    public VoiceFxSettings Fx { get; set; } = new();
    /// <summary>Listen to what the mixer sends into VB-CABLE (what Discord hears) on <see cref="ListenDeviceId"/>.</summary>
    public bool ListenEnabled { get; set; }
    /// <summary>Render device to listen on (e.g. headphones). Empty = not chosen yet.</summary>
    public string ListenDeviceId { get; set; } = "";
    /// <summary>Global hotkey: voice effects on/off. Null = none.</summary>
    public HotkeyBinding? FxToggleHotkey { get; set; }
    /// <summary>Global hotkey: voice effects only while held. Null = none.</summary>
    public HotkeyBinding? FxPushToTalkHotkey { get; set; }

    // ---- Pedalboard (guitar input through pedals, to speakers/headphones and the mic mixer) ----
    public PedalboardSettings Pedal { get; set; } = new();

    // ---- Device fingerprints (since 1.5.0) ----
    /// <summary>
    /// For each chosen device ID: what identifies the device, so the choice survives Windows giving it a new ID.
    /// Only IDs still chosen somewhere are kept.
    /// </summary>
    public Dictionary<string, DeviceFingerprint> DeviceFingerprints { get; set; } = new();

    /// <summary>A device came back under a new ID: move every choice (and its fingerprint) to it.</summary>
    public void ReplaceDeviceId(string oldId, string newId)
    {
        string Swap(string id) => id == oldId ? newId : id;
        SourceDeviceId = Swap(SourceDeviceId);
        MonitorDeviceId = Swap(MonitorDeviceId);
        ListenDeviceId = Swap(ListenDeviceId);
        Pedal.InputDeviceId = Swap(Pedal.InputDeviceId);
        Pedal.ExternalInputDeviceId = Swap(Pedal.ExternalInputDeviceId);
        Pedal.OutputDeviceId = Swap(Pedal.OutputDeviceId);
        foreach (var m in Mics) m.DeviceId = Swap(m.DeviceId);
        if (DeviceFingerprints.Remove(oldId, out var print)) DeviceFingerprints[newId] = print;
    }

    IEnumerable<string> ChosenDeviceIds() =>
        new[] { SourceDeviceId, MonitorDeviceId, ListenDeviceId, Pedal.InputDeviceId, Pedal.ExternalInputDeviceId, Pedal.OutputDeviceId }
            .Concat(Mics.Select(m => m.DeviceId)).Where(id => !string.IsNullOrEmpty(id));

    // ---- Settings window ----
    public static readonly string[] TabNames = { "Speaker copy", "Microphones", "Fun box", "Pedalboard" };
    /// <summary>Tabs pinned by the user: each always gets its own column (the window widens to fit).</summary>
    public List<string> PinnedTabs { get; set; } = new();

    // Version 1.1.0 stored two fixed mics; read once and moved into Mics (never written again).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mic1DeviceId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mic2DeviceId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Mic1Gain { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Mic2Gain { get; set; }

    // ---- VB-CABLE update check (opt-in: off unless the user allows internet access) ----
    public bool UpdateCheckOptIn { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string LatestCableVersion { get; set; } = "";
    /// <summary>Newest version the user was already notified about (so each version notifies once).</summary>
    public string NotifiedCableVersion { get; set; } = "";

    // Local (not roaming) app data: audio device IDs only mean something on this one PC.
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioMonitor");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static string OldDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioMonitor");

    /// <summary>Versions up to 1.0.0 kept settings in roaming %APPDATA%; move them once.</summary>
    static void MigrateFromRoaming()
    {
        try
        {
            string oldFile = Path.Combine(OldDir, "settings.json");
            if (File.Exists(FilePath) || !File.Exists(oldFile)) return;
            Directory.CreateDirectory(Dir);
            File.Move(oldFile, FilePath);
            foreach (var leftover in Directory.EnumerateFiles(OldDir, "settings.json.tmp")) File.Delete(leftover);
            if (!Directory.EnumerateFileSystemEntries(OldDir).Any()) Directory.Delete(OldDir);
        }
        catch { /* keep using defaults / the old file stays where it was */ }
    }

    public static Settings Load()
    {
        MigrateFromRoaming();
        Settings? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
        }
        catch { /* corrupt file -> defaults */ }
        return (loaded ?? new Settings()).Sanitized();
    }

    /// <summary>The file is user-editable: never trust its values (e.g. a huge volume = ear-damaging gain).</summary>
    Settings Sanitized()
    {
        if (!Enum.IsDefined(Mode)) Mode = MonitorMode.AllAudio;
        SourceDeviceId ??= "";
        MonitorDeviceId ??= "";
        Apps = (Apps ?? new()).Where(a => !string.IsNullOrWhiteSpace(a) && a.Length <= 260)
                              .Select(ProcessUtil.Normalize).Distinct().Take(100).ToList();
        Volume = Math.Clamp(Volume, 0, 100);
        LatencyMs = Math.Clamp(LatencyMs, 0, 150);   // 0 = minimum the device allows
        CopyDelayMs = Math.Clamp(CopyDelayMs, 0, MaxCopyDelayMs);
        Mics ??= new();
        if (Mics.Count == 0)   // migrate the 1.1.0 two-mic settings
        {
            if (!string.IsNullOrEmpty(Mic1DeviceId)) Mics.Add(new MicEntry { DeviceId = Mic1DeviceId, Gain = Mic1Gain ?? 100 });
            if (!string.IsNullOrEmpty(Mic2DeviceId)) Mics.Add(new MicEntry { DeviceId = Mic2DeviceId, Gain = Mic2Gain ?? 100 });
        }
        Mic1DeviceId = Mic2DeviceId = null;
        Mic1Gain = Mic2Gain = null;
        Mics = Mics.Where(m => m != null).Take(MicMixer.MaxInputs).ToList();
        foreach (var m in Mics)
        {
            m.DeviceId ??= "";
            m.Gain = Math.Clamp(m.Gain, 0, 200);
        }
        if (!Enum.IsDefined(MicMode)) MicMode = MicMode.AutoSwitch;
        Fx = (Fx ?? new()).Sanitized();
        FxToggleHotkey = FxToggleHotkey?.Sanitized();
        FxPushToTalkHotkey = FxPushToTalkHotkey?.Sanitized();
        if (FxPushToTalkHotkey != null && FxPushToTalkHotkey.SameAs(FxToggleHotkey)) FxPushToTalkHotkey = null;   // one key, one job
        Pedal ??= new();
        MigrateGuitarInput();
        Pedal = Pedal.Sanitized();
        ListenDeviceId ??= "";
        PinnedTabs = (PinnedTabs ?? new()).Where(t => TabNames.Contains(t)).Distinct().ToList();
        var chosen = ChosenDeviceIds().ToHashSet();
        DeviceFingerprints = (DeviceFingerprints ?? new())
            .Where(kv => kv.Key != null && chosen.Contains(kv.Key) && kv.Value != null && kv.Value.IsValid)
            .Take(32).ToDictionary(kv => kv.Key, kv => kv.Value);
        LatestCableVersion = Version.TryParse(LatestCableVersion, out var latest) ? latest.ToString() : "";
        NotifiedCableVersion = Version.TryParse(NotifiedCableVersion, out var notified) ? notified.ToString() : "";
        if (LastUpdateCheckUtc > DateTime.UtcNow) LastUpdateCheckUtc = null;   // clock changes / hand edits
        return this;
    }

    static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    /// <summary>
    /// Settings without a guitar connection type (before 2.0.0, or never chosen). A guitar input picked by hand that is
    /// not a Rocksmith cable (known from its saved fingerprint) becomes an audio interface (USB) or a PC jack (anything
    /// else), keeping the same device; any other input picked by hand stays the Real Tone cable. "Automatic" stays
    /// not chosen: the app takes the Real Tone cable as soon as one is connected (as before), otherwise the user picks.
    /// </summary>
    void MigrateGuitarInput()
    {
        if (Pedal.InputType != null) return;
        string id = Pedal.InputDeviceId ?? "";
        if (id.Length == 0) return;
        if (DeviceFingerprints != null && DeviceFingerprints.TryGetValue(id, out var print) && print != null
            && !(print.Name ?? "").Contains("Rocksmith", StringComparison.OrdinalIgnoreCase))
        {
            Pedal.InputType = (print.Hardware ?? "").StartsWith("USB", StringComparison.OrdinalIgnoreCase)
                ? GuitarInputType.AudioInterface : GuitarInputType.PcJack;
            Pedal.ExternalInputDeviceId = id;
            Pedal.InputDeviceId = "";
            Pedal.InputChannel = GuitarChannel.Both;   // exactly what the app did with it before
        }
        else Pedal.InputType = GuitarInputType.RealToneCable;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // Write to a temp file, then swap it in, so a crash mid-write can't leave a half-written file.
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SaveOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch { /* ignore */ }
    }
}

public sealed class MicEntry
{
    /// <summary>Capture device ID; empty = not chosen yet.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>Level in percent, 0..200 (100 = as Windows delivers it).</summary>
    public int Gain { get; set; } = 100;
    /// <summary>Always mixed in, never switched off by Auto-switch. Null = automatic (on for the Rocksmith cable).</summary>
    public bool? AlwaysLive { get; set; }
}
