using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioMonitor;

sealed class TrayApp : ApplicationContext
{
    public Settings Settings { get; } = Settings.Load();
    public string Status { get; private set; } = "";
    public string MicStatus { get; private set; } = "";
    public string UpdateCheckStatus { get; private set; } = "";
    public string PedalStatus { get; private set; } = "";
    public event Action? StatusChanged;
    /// <summary>A device was plugged in or out, or a saved choice moved to a device's new ID: reload device lists.</summary>
    public event Action? DevicesChanged;

    readonly NotifyIcon tray;
    readonly ToolStripMenuItem enabledItem;
    readonly ToolStripMenuItem startupItem;
    readonly ToolStripMenuItem micItem;
    readonly ToolStripMenuItem micMixItem;
    readonly ToolStripMenuItem micAutoItem;
    readonly ToolStripMenuItem pedalItem;
    readonly System.Windows.Forms.Timer timer;
    readonly System.Windows.Forms.Timer deviceSettle;
    readonly DeviceWatcher? deviceWatcher;
    readonly Icon iconOn = MakeIcon(on: true);
    readonly Icon iconOff = MakeIcon(on: false);

    AudioEngine? engine;
    string? engineSourceId;      // device id actually captured (AllAudio mode)
    bool engineSourceIsDefault;
    string baseStatus = "";      // status set by Restart(); temporary warnings fall back to it
    SettingsForm? form;
    MicMixer? mic;
    int[] micSlots = Array.Empty<int>();    // settings row -> index inside the running mixer (-1 = not running)
    readonly List<string> missingMicIds = new();   // chosen inputs that weren't connected at the last start
    bool copyOn, micOn, pedalOn, updateCheckRunning;
    Pedalboard? pedal;
    readonly AudioFeed guitarFeed = new();   // pedalboard -> mic mixer (survives pedalboard restarts)
    string? pedalOutputId;                    // device actually played on
    bool pedalOutputIsDefault, rocksmithRunning, tunerMute;
    string? micFeedId;                        // guitar input the running mixer takes from the pedalboard
    bool micGuitarPaused;                     // the mixer skipped the guitar cable because Rocksmith has it
    int micAlwaysLiveCount;                   // inputs the running mixer mixes in regardless of Auto-switch
    readonly GlobalHotkeys hotkeys;
    bool pushToTalkHeld;
    readonly AudioFeed mixFeed = new();       // what the mixer sends into VB-CABLE, for listening (survives restarts)
    Listener? listener;
    string listenName = "";
    string? listenError;

    public TrayApp()
    {
        enabledItem = new ToolStripMenuItem("Monitoring on", null, (_, _) =>
        {
            Settings.Enabled = !Settings.Enabled;
            Apply();
        });
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            try { Startup.Set(!Startup.IsEnabled()); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Edition.Name); }
        });
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());
        micItem = new ToolStripMenuItem("Mic mixer on", null, (_, _) =>
        {
            Settings.MicEnabled = !Settings.MicEnabled;
            ApplyMic();
        });
        micMixItem = new ToolStripMenuItem("Mix both mics", null, (_, _) => SetMicMode(MicMode.Mix));
        micAutoItem = new ToolStripMenuItem("Auto-switch (louder mic)", null, (_, _) => SetMicMode(MicMode.AutoSwitch));
        pedalItem = new ToolStripMenuItem("Pedalboard on", null, (_, _) =>
        {
            Settings.Pedal.Enabled = !Settings.Pedal.Enabled;
            ApplyPedal();
        });
        var micModeItem = new ToolStripMenuItem("Mic mode");
        micModeItem.DropDownItems.AddRange(new ToolStripItem[] { micMixItem, micAutoItem });

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            enabledItem, micItem, micModeItem, pedalItem, settingsItem, new ToolStripSeparator(), startupItem, new ToolStripSeparator(), exitItem,
        });
        if (!Edition.HasPedalboard) menu.Items.Remove(pedalItem);
        menu.Opening += (_, _) =>
        {
            enabledItem.Checked = Settings.Enabled;
            micItem.Checked = Settings.MicEnabled;
            micMixItem.Checked = Settings.MicMode == MicMode.Mix;
            micAutoItem.Checked = Settings.MicMode == MicMode.AutoSwitch;
            pedalItem.Checked = Settings.Pedal.Enabled;
            startupItem.Checked = Startup.IsEnabled();
        };

        tray = new NotifyIcon { ContextMenuStrip = menu, Icon = iconOff, Text = Edition.Name, Visible = true };

        // Created after the first control, so its events arrive on this (UI) thread.
        hotkeys = new GlobalHotkeys();
        hotkeys.TogglePressed += ToggleEffects;
        hotkeys.PushToTalkChanged += held =>
        {
            pushToTalkHeld = held;
            mic?.SetPushToTalk(held);
        };
        hotkeys.SetBindings(Settings.FxToggleHotkey, Settings.FxPushToTalkHotkey);
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };

        timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => Tick();
        timer.Start();

        // React to devices being plugged in or out right away (the 3-second timer stays as a safety net).
        // Windows sends several notifications per plug; wait for them to settle, then check once.
        deviceSettle = new System.Windows.Forms.Timer { Interval = 400 };
        deviceSettle.Tick += (_, _) =>
        {
            deviceSettle.Stop();
            Tick();
            DevicesChanged?.Invoke();
        };
        try { deviceWatcher = new DeviceWatcher(() => { deviceSettle.Stop(); deviceSettle.Start(); }); }
        catch { deviceWatcher = null; }   // without notifications the timer still reconnects within 3 s

        Startup.Repair();
        rocksmithRunning = ProcessUtil.IsRocksmithRunning();
        Restart();
        RestartPedal();
        RestartMic();
        RestartListener();
        if (string.IsNullOrEmpty(Settings.MonitorDeviceId)) ShowSettings();
    }

    /// <summary>Save settings and rebuild the audio pipeline.</summary>
    public void Apply()
    {
        Settings.Save();
        Restart();
    }

    public void SetVolume(int percent)
    {
        Settings.Volume = percent;
        Settings.Save();
        if (engine != null) engine.Volume = percent / 100f;
    }

    /// <summary>Applied live (no audio restart) so it can be tuned by ear.</summary>
    public void SetCopyDelay(int ms, bool save)
    {
        Settings.CopyDelayMs = ms;
        if (save) Settings.Save();
        engine?.SetDelay(ms);
    }

    void Restart()
    {
        StopEngine();
        if (!Settings.Enabled) { SetStatus(false, "Off"); return; }
        if (string.IsNullOrEmpty(Settings.MonitorDeviceId)) { SetStatus(false, "Pick a \"Copy to\" device in Settings"); return; }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var monitor = FindDevice(enumerator, Settings.MonitorDeviceId)
                          ?? throw new Exception("\"Copy to\" device not connected");

            MMDevice? source = null;
            if (Settings.Mode == MonitorMode.AllAudio)
            {
                engineSourceIsDefault = string.IsNullOrEmpty(Settings.SourceDeviceId);
                source = engineSourceIsDefault
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                    : FindDevice(enumerator, Settings.SourceDeviceId) ?? throw new Exception("Source device not connected");
                engineSourceId = source.ID;
            }

            if (source != null && source.ID == monitor.ID)
                throw new Exception(engineSourceIsDefault
                    ? $"The Windows default output is {Short(monitor.FriendlyName)}, the same device as \"Copy to\". Pick another \"Copy to\" device."
                    : "\"Copy from\" and \"Copy to\" are the same device.");

            engine = new AudioEngine(monitor, Settings.CopyDelayMs, Settings.Volume / 100f, Settings.LatencyMs);
            string outputsText = Short(monitor.FriendlyName) + (Settings.CopyDelayMs > 0 ? $" (+{Settings.CopyDelayMs} ms)" : "")
                                 + $". Output: {engine.OutputInfo}";
            if (source != null)
            {
                engine.StartDeviceLoopback(source);
                baseStatus = $"{Short(source.FriendlyName)} → {outputsText}";
            }
            else
            {
                engine.SyncProcesses(ProcessUtil.ResolveRootPids(Settings.Apps));
                baseStatus = $"{Settings.Apps.Count} app(s) → {outputsText}";
            }
            SetStatus(true, baseStatus);
        }
        catch (Exception ex)
        {
            StopEngine();
            SetStatus(false, "Error: " + ex.Message);
        }
    }

    void Tick()
    {
        try { TickCore(); }
        finally
        {
            if (fingerprintsDirty) { fingerprintsDirty = false; Settings.Save(); }
        }
    }

    void TickCore()
    {
        // Rocksmith takes the guitar cable for itself: step aside while it runs, come back when it closes.
        bool rocksmith = ProcessUtil.IsRocksmithRunning();
        if (rocksmith != rocksmithRunning)
        {
            rocksmithRunning = rocksmith;
            RestartPedal();
            if (Settings.MicEnabled && (micGuitarPaused || MicIncludesGuitar())) RestartMic();
        }
        else if (PedalWanted && !rocksmithRunning && (pedal == null || pedal.Fault != null || PedalDefaultOutputChanged()))
        {
            RestartPedal();   // e.g. cable or headphones replugged, or the Windows default output changed
        }

        if (Settings.MicEnabled && (mic == null || mic.Fault != null || MissingMicIsBack())) RestartMic();   // e.g. mic unplugged and back
        if (Settings.ListenEnabled && (listener == null || listener.Fault != null)) RestartListener();   // e.g. headphones replugged
        if (Settings.UpdateCheckOptIn && !updateCheckRunning
            && (Settings.LastUpdateCheckUtc == null || DateTime.UtcNow - Settings.LastUpdateCheckUtc > TimeSpan.FromHours(24)))
            _ = RunUpdateCheck();

        if (!Settings.Enabled) return;

        // Retry after errors (e.g. headphones unplugged, then plugged back in).
        if (engine == null || engine.Fault != null)
        {
            if (string.IsNullOrEmpty(Settings.MonitorDeviceId)) return;
            Restart();
            return;
        }

        if (Settings.Mode == MonitorMode.SelectedApps)
        {
            // Pick up apps started (or closed) after monitoring began.
            engine.SyncProcesses(ProcessUtil.ResolveRootPids(Settings.Apps));
            if (engine.LastAppError != null && engine.ActiveAppStreams == 0 && Settings.Apps.Count > 0)
                SetStatus(true, "App capture failed: " + engine.LastAppError);
            else if (Status != baseStatus) SetStatus(true, baseStatus);
        }
        else if (engineSourceIsDefault)
        {
            // Follow changes of the Windows default output device.
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID != engineSourceId) Restart();
            }
            catch { }
        }
    }

    /// <summary>
    /// The connected device with this saved ID. If Windows has since given that device a new ID (after an update,
    /// a driver reinstall or another USB port), the saved fingerprint finds it again and the choice moves to the
    /// new ID, everywhere it's used.
    /// </summary>
    MMDevice? FindDevice(MMDeviceEnumerator enumerator, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active)
            {
                RememberFingerprint(id, device);
                return device;
            }
        }
        catch { }

        if (!Settings.DeviceFingerprints.TryGetValue(id, out var print)) return null;
        try
        {
            var flow = print.Flow == "Capture" ? DataFlow.Capture : DataFlow.Render;
            var candidates = new List<(MMDevice Device, DeviceFingerprint Print)>();
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                if (DeviceFingerprint.Of(d) is { } p) candidates.Add((d, p));
            int match = DeviceFingerprint.FindMatch(print, candidates.Select(c => c.Print).ToList());
            if (match < 0) return null;
            var found = candidates[match].Device;
            Settings.ReplaceDeviceId(id, found.ID);
            fingerprintsDirty = true;
            ScheduleDevicesChanged();
            return found;
        }
        catch { return null; }
    }

    // Fingerprint changes are saved by the next Tick, never from inside FindDevice: the settings window calls
    // into FindDevice too, and a save from there would be a surprise (and must never happen in a test).
    bool fingerprintsDirty;

    void RememberFingerprint(string id, MMDevice device)
    {
        var print = DeviceFingerprint.Of(device);
        if (print == null) return;
        if (Settings.DeviceFingerprints.TryGetValue(id, out var known) && known.SameAs(print)) return;
        Settings.DeviceFingerprints[id] = print;
        fingerprintsDirty = true;
    }

    /// <summary>Tell the settings window shortly, after the current restart has finished (not in the middle of it).</summary>
    void ScheduleDevicesChanged()
    {
        deviceSettle.Stop();
        deviceSettle.Start();
    }

    bool PedalWanted => Edition.HasPedalboard && Settings.Pedal.Enabled;

    void StopEngine()
    {
        var e = engine;
        engine = null;
        e?.Dispose();
    }

    void SetStatus(bool on, string text)
    {
        Status = text;
        copyOn = on;
        RefreshTray();
    }

    void SetMicStatus(bool on, string text)
    {
        MicStatus = text;
        micOn = on;
        RefreshTray();
    }

    void RefreshTray()
    {
        tray.Icon = copyOn || micOn || pedalOn ? iconOn : iconOff;
        var tip = $"{Edition.Name} – Copy: {Status}" + (Settings.MicEnabled ? $" | Mic: {MicStatus}" : "");
        if (PedalWanted)
            tip = $"{Edition.Name} | Pedalboard: {(pedalOn ? "on" : rocksmithRunning ? "paused (Rocksmith)" : "off")} | Copy: {Status}";
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        StatusChanged?.Invoke();
    }

    // ---------------- Mic mixer ----------------

    /// <summary>Save settings and rebuild the mic mixer.</summary>
    public void ApplyMic()
    {
        Settings.Save();
        RestartMic();
    }

    /// <summary>Applied live (no restart) so it can be adjusted while talking.</summary>
    public void SetMicGain(int row, int percent, bool save)
    {
        if (row < 0 || row >= Settings.Mics.Count) return;
        Settings.Mics[row].Gain = percent;
        if (save) Settings.Save();
        if (mic != null && row < micSlots.Length && micSlots[row] >= 0) mic.SetGain(micSlots[row], percent / 100f);
    }

    public void SetMicMode(MicMode mode)
    {
        Settings.MicMode = mode;
        Settings.Save();
        if (mic != null) mic.Mode = mode;
        if (micOn) SetMicStatus(true, MicRunningText());
    }

    /// <summary>Fun box: applied live, no restart.</summary>
    public void SetEffects(bool save)
    {
        if (save) Settings.Save();
        mic?.SetEffects(Settings.Fx);
    }

    /// <summary>The on/off hotkey: flips "Voice effects on" and confirms with a blip only you hear.</summary>
    void ToggleEffects()
    {
        Settings.Fx.Enabled = !Settings.Fx.Enabled;
        Settings.Save();
        mic?.SetEffects(Settings.Fx);
        Blip.Play(Settings.Fx.Enabled);
        if (micOn) SetMicStatus(true, MicRunningText()); else StatusChanged?.Invoke();
    }

    public void SetHotkeys(HotkeyBinding? toggle, HotkeyBinding? pushToTalk)
    {
        Settings.FxToggleHotkey = toggle;
        Settings.FxPushToTalkHotkey = pushToTalk;
        Settings.Save();
        hotkeys.SetBindings(toggle, pushToTalk);
    }

    /// <summary>The next key or mouse button pressed becomes the result (null = cancelled with Esc).</summary>
    public void CaptureHotkey(Action<HotkeyBinding?> done) => hotkeys.BeginCapture(done);
    public void CancelHotkeyCapture() => hotkeys.CancelCapture();

    /// <summary>Whether a mixer input is mixed in regardless of Auto-switch (automatic: yes for the guitar cable).</summary>
    public bool IsAlwaysLive(MicEntry entry)
    {
        if (entry.AlwaysLive is bool chosen) return chosen;
        if (string.IsNullOrEmpty(entry.DeviceId)) return false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return FindDevice(enumerator, entry.DeviceId) is { } d && IsGuitarCable(d);
        }
        catch { return false; }
    }

    /// <summary>Level (0..1) per settings row, and which row is live (-1 = all / mix).</summary>
    public (float[] Levels, int ActiveRow) MicLevels()
    {
        var result = new float[Settings.Mics.Count];
        if (mic == null) return (result, -1);
        var (levels, active) = mic.TakeLevels();
        int activeRow = -1;
        for (int row = 0; row < result.Length && row < micSlots.Length; row++)
        {
            int index = micSlots[row];
            if (index < 0 || index >= levels.Length) continue;
            result[row] = levels[index];
            if (index == active && activeRow < 0) activeRow = row;
        }
        return (result, activeRow);
    }

    string cableName = "";
    int micCount;

    string MicRunningText() =>
        $"{micCount} input(s) → {Short(cableName)}, {(micCount < 2 ? "single input" : Settings.MicMode == MicMode.Mix ? "mixing" : micAlwaysLiveCount > 0 ? $"auto-switching ({micAlwaysLiveCount} always live)" : "auto-switching")}"
        + (Settings.Fx.Enabled ? ", voice effects on" : "")
        + (micFeedId != null ? ", guitar through the pedalboard" : "")
        + (micGuitarPaused ? ". Guitar cable paused while Rocksmith runs" : "")
        + (missingMicIds.Count > 0 ? $". {missingMicIds.Count} chosen input(s) not connected (skipped until plugged in)" : "")
        + ". In Discord, choose \"CABLE Output\" as Input Device.";

    bool MissingMicIsBack()
    {
        if (mic == null || missingMicIds.Count == 0) return false;
        using var enumerator = new MMDeviceEnumerator();
        return missingMicIds.Any(id => FindDevice(enumerator, id) != null);
    }

    void RestartMic()
    {
        StopMic();
        missingMicIds.Clear();
        micFeedId = null;
        micGuitarPaused = false;
        micAlwaysLiveCount = 0;
        if (!Settings.MicEnabled) { SetMicStatus(false, "Off"); return; }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var cable = VbCable.FindCableInput(enumerator)
                        ?? throw new Exception("VB-CABLE not found. Install it from vb-audio.com (button below), then it starts automatically.");
            var inputs = new List<MicInputSpec>();
            string? guitarId = PedalWanted ? PedalInputId(enumerator) : null;
            micSlots = Enumerable.Repeat(-1, Settings.Mics.Count).ToArray();
            for (int row = 0; row < Settings.Mics.Count; row++)
            {
                var entry = Settings.Mics[row];
                if (string.IsNullOrEmpty(entry.DeviceId)) continue;
                var device = FindDevice(enumerator, entry.DeviceId);
                if (device == null) { missingMicIds.Add(entry.DeviceId); continue; }   // keep the others running
                if (VbCable.IsCable(device.FriendlyName))
                    throw new Exception("VB-CABLE can't be an input here: the mixer writes into it, so it would feed back into itself.");
                // With the pedalboard on, the guitar comes from it (processed); otherwise straight from the cable,
                // except while Rocksmith runs: it holds the cable, and trying to open it would stop the whole mixer.
                bool viaPedal = !string.IsNullOrEmpty(guitarId) && device.ID == guitarId;
                if (!viaPedal && rocksmithRunning && IsGuitarCable(device)) { micGuitarPaused = true; continue; }
                int existing = inputs.FindIndex(i => i.Device.ID == device.ID);
                if (existing >= 0) { micSlots[row] = existing; continue; }   // same input picked twice
                micSlots[row] = inputs.Count;
                bool alwaysLive = entry.AlwaysLive ?? IsGuitarCable(device);
                if (alwaysLive) micAlwaysLiveCount++;
                // The guitar cable is captured raw: Windows' Voice Clarity (speech noise suppression) would rewrite it.
                inputs.Add(new MicInputSpec(device, entry.Gain / 100f, viaPedal ? guitarFeed : null, alwaysLive, Raw: IsGuitarCable(device)));
                if (viaPedal) micFeedId = device.ID;
            }
            if (inputs.Count == 0)
            {
                SetMicStatus(false, micGuitarPaused ? "Paused: Rocksmith is using the guitar cable. Resumes by itself when it closes."
                    : missingMicIds.Count > 0 ? "The chosen inputs aren't connected" : "Add at least one microphone");
                return;
            }

            mic = new MicMixer(cable, inputs, Settings.MicMode, Settings.Fx, Settings.LatencyMs, mixFeed);
            mic.SetPushToTalk(pushToTalkHeld);
            cableName = cable.FriendlyName;
            micCount = inputs.Count;
            SetMicStatus(true, MicRunningText());
        }
        catch (Exception ex)
        {
            StopMic();
            SetMicStatus(false, "Error: " + ex.Message);
        }
    }

    void StopMic()
    {
        var m = mic;
        mic = null;
        m?.Dispose();
    }

    // ---------------- Listen to VB-CABLE ----------------

    /// <summary>Save settings and restart listening (on/off, device or output buffer changed).</summary>
    public void ApplyListen()
    {
        Settings.Save();
        RestartListener();
    }

    public string ListenStatus
    {
        get
        {
            if (!Settings.ListenEnabled) return "Off";
            if (listenError != null) return "Error: " + listenError;
            if (listener == null) return "Starting…";
            if (mic == null) return $"Listening on {listenName}. The mic mixer is off, so there's nothing to hear yet.";
            // Voice -> mic packet (~5 ms avg) -> mic buffer -> listening buffer -> output queue.
            double lag = 5 + mic.InputBufferMs + listener.BufferTargetMs + listener.OutputMs;
            return $"Listening on {listenName}, about {lag:0} ms after you speak.";
        }
    }

    void RestartListener()
    {
        StopListener();
        listenError = null;
        if (Settings.ListenEnabled)
        {
            try
            {
                if (string.IsNullOrEmpty(Settings.ListenDeviceId)) throw new Exception("Pick your headphones to listen on.");
                using var enumerator = new MMDeviceEnumerator();
                var device = FindDevice(enumerator, Settings.ListenDeviceId) ?? throw new Exception("The listening device isn't connected.");
                if (VbCable.IsCable(device.FriendlyName)) throw new Exception("Can't listen on VB-CABLE itself (it would loop). Pick your headphones.");
                listener = new Listener(device, Settings.LatencyMs, mixFeed, mic?.OutputPeriodMs ?? 3);
                listenName = Short(device.FriendlyName);
            }
            catch (Exception ex)
            {
                StopListener();
                listenError = ex.Message;
            }
        }
        RefreshTray();
    }

    void StopListener()
    {
        var l = listener;
        listener = null;
        l?.Dispose();
    }

    // ---------------- Pedalboard ----------------

    /// <summary>Save settings and rebuild the pedalboard (on/off, devices or output buffer changed).</summary>
    public void ApplyPedal(bool restartMic = true)
    {
        Settings.Save();
        RestartPedal();
        // The mixer takes the guitar from the pedalboard only while it is on: switch its source.
        if (restartMic && Settings.MicEnabled && (micFeedId != null || MicIncludesGuitar())) RestartMic();
    }

    /// <summary>Pedal knobs: applied live, no restart.</summary>
    public void SetPedals(bool save)
    {
        if (save) Settings.Save();
        pedal?.Update(Settings.Pedal);
    }

    /// <summary>Tuner on = sound muted (like a tuner pedal). Not saved: the next start is never silent.</summary>
    public void SetTunerMute(bool on)
    {
        tunerMute = on;
        if (pedal != null) pedal.Mute = on;
    }

    public bool PedalRunning => pedal != null && pedal.Fault == null;
    public (float In, float Out, bool Limiting, bool InputClipped) PedalMeters() => pedal?.TakeMeters() ?? default;
    public int PedalRecentInput(float[] destination) => pedal?.CopyRecentInput(destination) ?? 0;
    public int PedalSampleRate => pedal?.SampleRate ?? 48000;
    public float PedalInputMax => pedal?.InputMax ?? 0;
    public void PedalResetInputMax() => pedal?.ResetInputMax();
    /// <summary>Lag breakdown of the running pedalboard: (total, capture wait, buffer, output) in ms.</summary>
    public (double Total, double Capture, int Buffer, double Output)? PedalLag =>
        pedal == null ? null : (pedal.LagMs, pedal.CaptureMs / 2, pedal.BufferTargetMs, pedal.OutputMs);

    /// <summary>The chosen guitar input, or (automatic) the Rocksmith cable if connected; "" if none.</summary>
    string PedalInputId(MMDeviceEnumerator enumerator)
    {
        if (!string.IsNullOrEmpty(Settings.Pedal.InputDeviceId)) return Settings.Pedal.InputDeviceId;
        try
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                if (d.FriendlyName.Contains("Rocksmith", StringComparison.OrdinalIgnoreCase)) return d.ID;
        }
        catch { }
        return "";
    }

    bool IsGuitarCable(MMDevice device)
    {
        if (device.ID == Settings.Pedal.InputDeviceId) return true;
        try { return device.FriendlyName.Contains("Rocksmith", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    bool MicIncludesGuitar()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string guitarId = PedalInputId(enumerator);
            return Settings.Mics.Any(m => !string.IsNullOrEmpty(m.DeviceId)
                && (m.DeviceId == guitarId || (FindDevice(enumerator, m.DeviceId) is { } d && IsGuitarCable(d))));
        }
        catch { return false; }
    }

    bool PedalDefaultOutputChanged()
    {
        if (pedal == null || !pedalOutputIsDefault) return false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID != pedalOutputId;
        }
        catch { return false; }
    }

    void RestartPedal()
    {
        StopPedal();
        var s = Settings.Pedal;
        if (!PedalWanted) { SetPedalStatus(false, "Off"); return; }
        if (rocksmithRunning) { SetPedalStatus(false, "Paused: Rocksmith is using the guitar cable. Resumes by itself when Rocksmith closes."); return; }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string inputId = PedalInputId(enumerator);
            if (inputId == "") throw new Exception("Rocksmith cable not found. Plug it in, or pick the guitar input above.");
            var input = FindDevice(enumerator, inputId) ?? throw new Exception("The guitar input isn't connected.");
            pedalOutputIsDefault = string.IsNullOrEmpty(s.OutputDeviceId);
            var output = pedalOutputIsDefault
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : FindDevice(enumerator, s.OutputDeviceId) ?? throw new Exception("The \"Play on\" device isn't connected.");
            if (VbCable.IsCable(output.FriendlyName))
                throw new Exception("Pick speakers or headphones to play on. Discord gets the guitar through the mic mixer.");
            pedalOutputId = output.ID;
            pedal = new Pedalboard(input, output, s, Settings.LatencyMs, guitarFeed, tunerMute);
            SetPedalStatus(true, $"{Short(input.FriendlyName)} → {Short(output.FriendlyName)}. Output: {pedal.OutputInfo}."
                + (pedal.RawInput ? " Raw input: Windows' voice processing is bypassed." : " Raw input isn't available for this device: Windows' effects may alter the sound."));
        }
        catch (Exception ex)
        {
            StopPedal();
            string message = ex.HResult == unchecked((int)0x8889000A)   // AUDCLNT_E_DEVICE_IN_USE
                ? "Another app has the guitar input to itself (exclusive mode). Trying again every few seconds."
                : ex.Message;
            SetPedalStatus(false, "Error: " + message);
        }
    }

    void StopPedal()
    {
        var p = pedal;
        pedal = null;
        p?.Dispose();
    }

    void SetPedalStatus(bool on, string text)
    {
        PedalStatus = text;
        pedalOn = on;
        RefreshTray();
    }

    // ---------------- VB-CABLE update check (opt-in only) ----------------

    public void SetUpdateCheckOptIn(bool on)
    {
        Settings.UpdateCheckOptIn = on;
        if (!on) UpdateCheckStatus = "";
        Settings.Save();
        StatusChanged?.Invoke();
        if (on) _ = RunUpdateCheck();
    }

    /// <summary>Runs only when the user opted in. One HTTPS request to vb-audio.com; see UpdateChecker.</summary>
    public async Task RunUpdateCheck()
    {
        if (updateCheckRunning || !Settings.UpdateCheckOptIn) return;
        updateCheckRunning = true;
        UpdateCheckStatus = "Checking…";
        StatusChanged?.Invoke();
        try
        {
            var (latest, error) = await UpdateChecker.CheckAsync().ConfigureAwait(true);   // resume on the UI thread
            if (!Settings.UpdateCheckOptIn) { UpdateCheckStatus = ""; return; }   // opted out meanwhile
            var installed = VbCable.InstalledVersion();
            if (latest == null)
            {
                UpdateCheckStatus = $"Last check {DateTime.Now:t}: {error}. Will retry in an hour.";
                Settings.LastUpdateCheckUtc = DateTime.UtcNow - TimeSpan.FromHours(23);
            }
            else
            {
                Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                Settings.LatestCableVersion = latest.ToString();
                bool newer = installed != null && latest > installed;
                UpdateCheckStatus = $"Latest VB-CABLE: {latest} (checked {DateTime.Now:t})" +
                    (installed == null ? "" : newer ? $". Update available, you have {installed}." : ". You're up to date.");
                if (newer && Settings.NotifiedCableVersion != latest.ToString())
                {
                    tray.ShowBalloonTip(8000, "VB-CABLE update available",
                        $"Version {latest} is out (you have {installed}). Open {Edition.Name}'s Microphones settings for the download page.",
                        ToolTipIcon.Info);
                    Settings.NotifiedCableVersion = latest.ToString();
                }
            }
            Settings.Save();
        }
        finally
        {
            updateCheckRunning = false;
            StatusChanged?.Invoke();
        }
    }

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    void ShowSettings()
    {
        if (form == null || form.IsDisposed)
        {
            form = new SettingsForm(this);
            form.Show();
        }
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        deviceSettle.Stop();
        deviceWatcher?.Dispose();
        deviceSettle.Dispose();
        form?.Close();
        StopEngine();
        StopMic();
        StopPedal();
        StopListener();
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    /// <summary>The app icon (white headphones on dark gray, embedded app.ico) at the closest size available.</summary>
    public static Icon AppIcon(Size size)
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("AudioMonitor.app.ico");
            if (stream != null) return new Icon(stream, size);
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }

    /// <summary>Tray icon: the app icon; with a green dot in the corner while something is running.</summary>
    static Icon MakeIcon(bool on)
    {
        var size = SystemInformation.SmallIconSize;
        size = new Size(Math.Max(16, size.Width), Math.Max(16, size.Height));
        using var plain = AppIcon(size);
        if (!on) return (Icon)plain.Clone();
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(plain, new Rectangle(Point.Empty, size));
            float d = size.Width * 0.5f, x = size.Width - d, y = size.Height - d;
            using var ring = new SolidBrush(Color.FromArgb(48, 48, 48));   // separates the dot from the glyph
            g.FillEllipse(ring, x - 1, y - 1, d + 1, d + 1);
            using var dot = new SolidBrush(Color.FromArgb(63, 185, 80));
            g.FillEllipse(dot, x + 0.5f, y + 0.5f, d - 2, d - 2);
        }
        IntPtr handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
