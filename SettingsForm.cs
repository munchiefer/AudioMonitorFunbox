using System.Diagnostics;
using System.Drawing.Drawing2D;
using NAudio.CoreAudioApi;

namespace AudioMonitor;

sealed partial class SettingsForm : Form
{
    sealed record DeviceItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    const int W = 330;     // standard control width
    const int Edge = 10;   // space around the tab columns
    const int Gap = 10;    // space between tab columns

    readonly TrayApp app;
    readonly ToolTip tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    readonly FlowLayoutPanel speakerPage, micPage, funPage, pedalPage;
    // Resizable layout: each tab is a fixed-width column. A wider window shows more of them side by side
    // (centered); the last column keeps the remaining tabs. A shorter window scrolls vertically.
    readonly TabPage[] pages;
    readonly TabControl[] columns;
    readonly Panel scroller = new() { Dock = DockStyle.Fill, AutoScroll = true };
    Size columnSize;        // fixed size of one column (fits the largest tab)
    string shownLayout = "";   // which tabs are in which column right now
    TabPage? currentPage;   // the tab last looked at; stays selected when columns merge
    bool distributing;
    // Pin icons on the tab headers: 0 = not pinned (grey, tilted), 1 = pinned (blue, upright).
    readonly ImageList pinIcons = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
    const int PinHotWidth = 24;   // clicks this far from a tab header's left edge hit the pin
    bool loading;
    List<DeviceItem> captureDevices = new();

    // ---- Speaker copy tab ----
    readonly ComboBox monitorCombo = Combo(W);
    readonly RadioButton allRadio = new() { Text = "All audio playing on:", AutoSize = true };
    readonly ComboBox sourceCombo = Combo(W);
    readonly RadioButton appsRadio = new() { Text = "Only these apps (and their child processes), wherever they play:", AutoSize = true };
    readonly CheckedListBox appsList = new() { CheckOnClick = true, Width = W, Height = 100, IntegralHeight = false };
    readonly Button refreshButton = new() { Text = "Refresh", AutoSize = true };
    readonly TrackBar delayBar = Slider(0, Settings.MaxCopyDelayMs, W, 50, 1, 10);
    readonly Label delayLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0) };
    readonly Label delayHelp = Help(
        "Use this when the source device is the slower one, e.g. Bluetooth headphones set as the " +
        "Windows default output, copied to your speakers. Raise it until both sound as one " +
        "(arrow keys move 1 ms). Changes apply instantly.");
    readonly System.Windows.Forms.Timer delayDebounce = new() { Interval = 600 };
    readonly TrackBar volumeBar = Slider(0, 100, 280, 10, 1, 10);
    readonly Label volumeLabel = Value();
    readonly TrackBar latencyBar = Slider(0, 150, 280, 10, 5, 10);
    readonly Label latencyLabel = Value();
    readonly System.Windows.Forms.Timer latencyDebounce = new() { Interval = 400 };
    readonly CheckBox enabledBox = new() { Text = "Speaker copy on", AutoSize = true };
    readonly Label statusLabel = Status();

    // ---- Microphones tab ----
    readonly CheckBox micEnabledBox = new() { Text = "Mic mixer on", AutoSize = true };
    readonly FlowLayoutPanel micRowsPanel = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
    readonly List<MicRow> micRows = new();
    readonly Button addMicButton = new() { Text = "+ Add input", AutoSize = true };
    readonly RadioButton mixRadio = new() { Text = "Mix: all inputs live at once", AutoSize = true };
    readonly RadioButton autoRadio = new() { Text = "Auto-switch: the input that hears you louder is live", AutoSize = true };
    readonly System.Windows.Forms.Timer micGainDebounce = new() { Interval = 600 };
    readonly System.Windows.Forms.Timer meterTimer = new() { Interval = 80 };
    readonly Label cableLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), UseMnemonic = false };
    readonly Label cableVersionLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), UseMnemonic = false };
    readonly Button cableDownloadButton = new() { Text = "Open VB-CABLE download page", AutoSize = true };
    readonly CheckBox updateOptInBox = new() { Text = "Check online for VB-CABLE updates (once a day)", AutoSize = true };
    readonly Button checkNowButton = new() { Text = "Check now", AutoSize = true };
    readonly Label updateStatusLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), UseMnemonic = false };
    readonly Label micStatusLabel = Status();
    readonly CheckBox listenBox = new() { Text = "Listen to what Discord hears", AutoSize = true };
    readonly ComboBox listenCombo = Combo(270);
    readonly Label listenStatusLabel = new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText, UseMnemonic = false };

    sealed class MicRow
    {

        public required ComboBox Combo;
        public required TrackBar Gain;
        public required Label GainLabel;
        public required ProgressBar Meter;
        public required Label Live;
        public required CheckBox AlwaysLive;
    }

    static ComboBox Combo(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
    static TrackBar Slider(int min, int max, int width, int tick, int small, int large) =>
        new() { Minimum = min, Maximum = max, Width = width, TickFrequency = tick, SmallChange = small, LargeChange = large };
    // UseMnemonic = false: show "&" literally ("Voice & Video") instead of treating it as a shortcut marker.
    static Label Help(string text) => new() { AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText, Text = text, UseMnemonic = false };
    static Label Status() => new() { AutoSize = true, MaximumSize = new Size(360, 0), MinimumSize = new Size(0, 48), ForeColor = SystemColors.GrayText, UseMnemonic = false };
    static Label Value() => new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 0, 3) };

    public SettingsForm(TrayApp app)
    {
        this.app = app;
        Text = Edition.Name;
        Icon = TrayApp.AppIcon(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        speakerPage = BuildSpeakerPage();
        micPage = BuildMicPage();
        funPage = BuildFunPage();
        pedalPage = BuildPedalPage();
        pinIcons.Images.Add(PinIcon(false));
        pinIcons.Images.Add(PinIcon(true));
        var names = Settings.TabNames;
        // AudioMonitor builds the Pedalboard page too (its controls are wired up), it just never shows it.
        pages = Edition.HasPedalboard
            ? new[] { Page(names[0], speakerPage), Page(names[1], micPage), Page(names[2], funPage), Page(names[3], pedalPage) }
            : new[] { Page(names[0], speakerPage), Page(names[1], micPage), Page(names[2], funPage) };
        UpdatePinIcons();
        columns = pages.Select(_ => NewColumn()).ToArray();
        foreach (var page in pages)
        {
            var p = page;
            p.Enter += (_, _) => currentPage = p;   // clicked into a tab shown as its own column
            RouteWheel(p);
        }
        scroller.Controls.AddRange(columns);

        var closeButton = new Button { Text = "Close", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        closeButton.Click += (_, _) => Close();
        CancelButton = closeButton;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(Edge - 3, 0, Edge - 3, 6) };
        footer.Controls.Add(closeButton);

        Controls.Add(scroller);
        Controls.Add(footer);
        scroller.BringToFront();   // docked last, so it fills what the footer leaves

        LoadDevices();
        LoadApps();
        LoadValues();
        RebuildMicRows();

        // Start as narrow as the pinned tabs allow (one column showing everything if none are pinned);
        // the minimum height is half of the full height.
        int naturalHeight = columnSize.Height + 2 * Edge + footer.GetPreferredSize(Size.Empty).Height;
        ClientSize = new Size(ClientWidthFor(MinColumns()), naturalHeight);
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min(Width, screen.Width), Math.Min(Height / 2, screen.Height));
        if (Height > screen.Height) Height = screen.Height;
        if (Width > screen.Width) Width = screen.Width;
        scroller.SizeChanged += (_, _) => LayoutColumns();
        LayoutColumns();
        AddTooltips();
        HookSpeakerEvents();
        HookMicEvents();
        HookFunEvents();

        // Pick up devices that were plugged in / installed (e.g. VB-CABLE) while the window was in the background.
        Activated += (_, _) => { LoadDevices(); UpdateCableInfo(); };
        // ...and live, while the window is open in the background (e.g. kept on top): devices plugged in or out.
        app.DevicesChanged += OnDevicesChanged;
        devicesRetry.Tick += (_, _) => { devicesRetry.Stop(); OnDevicesChanged(); };

        app.StatusChanged += UpdateStatus;
        meterTimer.Tick += (_, _) => { UpdateMeters(); UpdatePedalMeters(); };
        meterTimer.Start();
        FormClosed += (_, _) =>
        {
            app.StatusChanged -= UpdateStatus;
            app.DevicesChanged -= OnDevicesChanged;
            devicesRetry.Dispose();
            meterTimer.Dispose();
            if (latencyDebounce.Enabled) { latencyDebounce.Stop(); app.Settings.LatencyMs = latencyBar.Value; app.Apply(); app.ApplyMic(); app.ApplyPedal(restartMic: false); app.ApplyListen(); }
            latencyDebounce.Dispose();
            if (delayDebounce.Enabled) app.SetCopyDelay(delayBar.Value, save: true);
            delayDebounce.Dispose();
            if (micGainDebounce.Enabled) app.Settings.Save();
            micGainDebounce.Dispose();
            if (fxDebounce.Enabled) app.SetEffects(save: true);
            fxDebounce.Dispose();
            if (capturingHotkey) app.CancelHotkeyCapture();
            if (pedalDebounce.Enabled) app.SetPedals(save: true);
            pedalDebounce.Dispose();
            autoBoostTimer.Dispose();
            if (tunerBox.Checked) app.SetTunerMute(false);   // never leave the guitar muted once the tuner is out of sight
            tips.Dispose();
            pinIcons.Dispose();
        };
    }

    // ---------------- layout ----------------

    static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(6), UseVisualStyleBackColor = false, BackColor = SystemColors.Control };
        page.Controls.Add(content);
        return page;
    }

    static FlowLayoutPanel Column(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    Label Header(string text) => new() { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 10, 3, 3) };

    TabControl NewColumn()
    {
        var column = new TabControl
        {
            Margin = Padding.Empty, Visible = false, ImageList = pinIcons, ShowToolTips = true,
            Padding = new Point(4, 3),   // a little tighter than default, so 4 tabs + pins fit one column
        };
        column.Selected += (_, e) => { if (!distributing && e.TabPage != null) currentPage = e.TabPage; };
        column.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < column.TabCount; i++)
            {
                var r = column.GetTabRect(i);
                if (r.Contains(e.Location) && e.X < r.Left + PinHotWidth)
                {
                    var page = column.TabPages[i];
                    BeginInvoke(() => TogglePin(page));   // not while this TabControl is handling its own click
                    return;
                }
            }
        };
        return column;
    }

    // ---------------- pinned tabs ----------------

    bool IsPinned(TabPage page) => app.Settings.PinnedTabs.Contains(page.Text);

    /// <summary>Columns the window must show: one per pinned tab, plus one for the others (if any).</summary>
    int MinColumns()
    {
        int pinned = pages.Count(IsPinned);
        return Math.Max(1, pinned + (pinned < pages.Length ? 1 : 0));
    }

    int ClientWidthFor(int count) =>
        2 * Edge + count * columnSize.Width + (count - 1) * Gap + SystemInformation.VerticalScrollBarWidth;

    void UpdatePinIcons()
    {
        foreach (var page in pages)
        {
            bool pinned = IsPinned(page);
            page.ImageIndex = pinned ? 1 : 0;
            page.ToolTipText = pinned
                ? "Pinned: always shown in its own column. Click the pin to unpin."
                : "Click the pin to keep this tab always visible in its own column (the window widens to fit).";
        }
    }

    void TogglePin(TabPage page)
    {
        if (IsDisposed) return;
        ApplyPin(page, !IsPinned(page));
        app.Settings.Save();
    }

    /// <summary>Pins/unpins a tab and resizes the window to match (no saving: tests call this directly).</summary>
    void ApplyPin(TabPage page, bool pin)
    {
        var pinnedList = app.Settings.PinnedTabs;
        int oldWidth = ClientWidthFor(MinColumns());
        if (pin == pinnedList.Contains(page.Text)) return;
        if (pin) pinnedList.Add(page.Text); else pinnedList.Remove(page.Text);
        UpdatePinIcons();
        currentPage = page;

        var screen = Screen.FromControl(this).WorkingArea;
        int chrome = Width - ClientSize.Width;
        int needed = Math.Min(ClientWidthFor(MinColumns()) + chrome, screen.Width);
        int wasExactly = Math.Min(oldWidth + chrome, screen.Width);
        MinimumSize = new Size(needed, MinimumSize.Height);
        if (WindowState == FormWindowState.Normal)
        {
            if (Width < needed) Width = needed;                    // pinning: widen to fit
            else if (!pin && Width == wasExactly) Width = needed;   // unpinning: give back the column the pin added
            if (Right > screen.Right) Left = Math.Max(screen.Left, screen.Right - Width);   // keep it on screen
        }
        LayoutColumns();
    }

    static Bitmap PinIcon(bool pinned)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (!pinned)
        {
            g.TranslateTransform(8, 8);
            g.RotateTransform(45);
            g.TranslateTransform(-8, -8);
        }
        var color = pinned ? Color.FromArgb(0, 103, 192) : Color.FromArgb(120, 120, 120);
        using var pen = new Pen(color, 1.4f);
        var head = new[]
        {
            new PointF(5, 1.5f), new PointF(11, 1.5f), new PointF(10, 3), new PointF(10, 6.5f),
            new PointF(12.5f, 9), new PointF(3.5f, 9), new PointF(6, 6.5f), new PointF(6, 3),
        };
        if (pinned)
        {
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, head);
        }
        else g.DrawPolygon(pen, head);
        g.DrawLine(pen, 8, 9, 8, 15);
        return bmp;
    }

    /// <summary>TabControl doesn't size itself: fit the columns to the largest page (called whenever a page changes size).</summary>
    void Relayout()
    {
        var sizes = new[] { speakerPage, micPage, funPage, pedalPage }.Select(p => p.GetPreferredSize(Size.Empty)).ToList();
        var size = new Size(sizes.Max(s => s.Width) + 24, sizes.Max(s => s.Height) + 40);
        // If everything fitted before (e.g. an input was added), grow the window instead of adding a scroll bar.
        bool fitted = !columnSize.IsEmpty && scroller.ClientSize.Height >= columnSize.Height + 2 * Edge;
        int extra = size.Height - columnSize.Height;
        columnSize = size;
        if (fitted && extra > 0 && IsHandleCreated && WindowState == FormWindowState.Normal)
            Height = Math.Min(Height + extra, Screen.FromControl(this).WorkingArea.Height);
        LayoutColumns();
    }

    /// <summary>Fits as many fixed-width tab columns side by side as the window allows, centered.</summary>
    void LayoutColumns()
    {
        if (columnSize.IsEmpty || columns == null) return;
        int contentHeight = columnSize.Height + 2 * Edge;
        // Width left once a vertical scroll bar is needed (independent of the column count, so it can't flip-flop).
        int available = scroller.Width - (contentHeight > scroller.Height ? SystemInformation.VerticalScrollBarWidth : 0);
        // Pinned tabs always get their columns, even if the window is narrower (only on a too-small screen).
        int count = Math.Clamp((available - 2 * Edge + Gap) / (columnSize.Width + Gap), MinColumns(), pages.Length);
        int total = count * columnSize.Width + (count - 1) * Gap;
        bool overflow = total + 2 * Edge > available;

        scroller.SuspendLayout();
        // Shrinking the window can make WinForms scroll sideways before we re-place the columns; undo that
        // (when the columns fit the width there is nothing to scroll to horizontally).
        if (!overflow && scroller.AutoScrollPosition.X != 0) scroller.AutoScrollPosition = new Point(0, -scroller.AutoScrollPosition.Y);
        count = Distribute(count);
        int x = overflow ? Edge + scroller.AutoScrollPosition.X : Math.Max(Edge, (available - total) / 2);
        int y = Edge + scroller.AutoScrollPosition.Y;   // child positions are relative to the scrolled view
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i].Visible = i < count;
            // Hidden columns are parked under the first one: their old spot could still widen the scroll area.
            columns[i].Bounds = new Rectangle(x + Math.Min(i, count - 1) * (columnSize.Width + Gap), y, columnSize.Width, columnSize.Height);
        }
        scroller.AutoScrollMinSize = new Size(overflow ? total + 2 * Edge : 0, contentHeight);
        scroller.ResumeLayout();
    }

    /// <summary>
    /// Puts the tabs into <paramref name="count"/> columns: each pinned tab gets its own; of the others, the first
    /// ones get their own column while there is room and the last column holds the rest. Columns keep the
    /// tabs' order. Only moves tabs when the arrangement changes. Returns the number of columns used.
    /// </summary>
    int Distribute(int count)
    {
        var unpinned = pages.Where(p => !IsPinned(p)).ToList();
        var groups = pages.Where(IsPinned).Select(p => new List<TabPage> { p }).ToList();
        int free = Math.Min(count - groups.Count, unpinned.Count);
        for (int i = 0; i < free; i++)
            groups.Add(i < free - 1 ? new List<TabPage> { unpinned[i] } : unpinned.Skip(i).ToList());
        groups = groups.OrderBy(g => Array.IndexOf(pages, g[0])).ToList();

        string layout = string.Join("|", groups.Select(g => string.Join("+", g.Select(p => p.Text))));
        if (layout == shownLayout) return groups.Count;

        var current = currentPage ?? pages[0];
        distributing = true;
        try
        {
            foreach (var column in columns) column.TabPages.Clear();   // detaches, doesn't dispose
            for (int c = 0; c < groups.Count; c++)
            {
                foreach (var page in groups[c]) columns[c].TabPages.Add(page);
                if (groups[c].Contains(current)) columns[c].SelectedTab = current;
            }
        }
        finally { distributing = false; }
        currentPage = current;
        shownLayout = layout;
        return groups.Count;
    }

    /// <summary>
    /// Mouse wheel over a slider or dropdown that wasn't clicked scrolls the window instead of changing the value
    /// (so scrolling past the Output buffer slider can't restart the audio). Once clicked, the wheel adjusts it as usual.
    /// </summary>
    void RouteWheel(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is TrackBar or ComboBox) c.MouseWheel += WheelScrollsWindow;
            RouteWheel(c);
        }
    }

    void WheelScrollsWindow(object? sender, MouseEventArgs e)
    {
        if (sender is not Control c || c.Focused || !scroller.VerticalScroll.Visible) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        int lines = SystemInformation.MouseWheelScrollLines;
        int step = lines <= 0 ? scroller.ClientSize.Height : lines * Font.Height;   // -1 = "one screen at a time"
        scroller.AutoScrollPosition = new Point(0, -scroller.AutoScrollPosition.Y - e.Delta * step / 120);
    }

    FlowLayoutPanel BuildSpeakerPage()
    {
        Padding indent = new(20, 3, 3, 3);
        sourceCombo.Margin = indent;
        appsList.Margin = indent;
        refreshButton.Margin = indent;

        return Column(
            Header("Copy from"),
            // Radio buttons are exclusive per container, so each choice gets its own group.
            Column(allRadio, sourceCombo, appsRadio, appsList, refreshButton),
            Header("Copy to (e.g. headphones)"), monitorCombo,
            Header("Delay the copy"), delayLabel, delayBar, delayHelp,
            Header("Copy volume"), Row(volumeBar, volumeLabel),
            Header("Output buffer (lower = less lag, raise it if audio crackles)"), Row(latencyBar, latencyLabel),
            enabledBox, statusLabel);
    }

    FlowLayoutPanel BuildMicPage()
    {
        cableDownloadButton.Click += (_, _) => OpenDownloadPage();
        checkNowButton.Click += (_, _) => _ = app.RunUpdateCheck();

        return Column(
            Help("Combines your microphones and other inputs (e.g. the Rocksmith cable) into VB-CABLE, so " +
                 "Discord can use them as one mic: in Discord, set Input Device to \"CABLE Output\"."),
            Header("Inputs (up to 4)"), micRowsPanel, addMicButton,
            Header("Mode"),
            Column(mixRadio, autoRadio),
            Header("VB-CABLE"), cableLabel, cableVersionLabel, cableDownloadButton,
            Row(updateOptInBox, checkNowButton), updateStatusLabel,
            micEnabledBox, micStatusLabel,
            Header("Listen to VB-CABLE"),
            listenBox,
            Row(new Label { Text = "On", AutoSize = false, Width = 55, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 3, 0, 3) }, listenCombo),
            listenStatusLabel,
            Help("Use headphones: on speakers your mic hears itself, which can echo or howl."));
    }

    // ---------------- mic rows ----------------

    void RebuildMicRows()
    {
        var s = app.Settings;
        if (s.Mics.Count == 0) s.Mics.Add(new MicEntry());   // start with one empty row to fill in
        micRowsPanel.SuspendLayout();
        foreach (Control c in micRowsPanel.Controls) c.Dispose();
        micRowsPanel.Controls.Clear();
        micRows.Clear();
        for (int i = 0; i < s.Mics.Count; i++) micRowsPanel.Controls.Add(BuildMicRow(i, s.Mics[i]));
        micRowsPanel.ResumeLayout();
        addMicButton.Enabled = s.Mics.Count < MicMixer.MaxInputs;
        Relayout();
    }

    FlowLayoutPanel BuildMicRow(int index, MicEntry entry)
    {
        var name = new Label { Text = $"Input {index + 1}", AutoSize = false, Width = 55, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
        var combo = Combo(250);
        var remove = new Button { Text = "✕", Width = 28, Height = 23, Margin = new Padding(3, 2, 3, 3) };
        var gain = Slider(0, 200, 150, 25, 5, 10);
        gain.AutoSize = false;
        gain.Height = 30;
        gain.Value = Math.Clamp(entry.Gain, 0, 200);
        var gainLabel = new Label { Text = gain.Value + "%", AutoSize = false, Width = 40, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 3, 3) };
        var meter = new ProgressBar { Width = 80, Height = 10, Maximum = 100, Margin = new Padding(3, 11, 3, 3) };
        var live = new Label { AutoSize = true, ForeColor = Color.ForestGreen, Margin = new Padding(3, 8, 3, 3) };

        loading = true;
        Fill(combo, captureDevices, entry.DeviceId, null);
        loading = false;

        var always = new CheckBox
        {
            Text = "Always live (Auto-switch never turns it off)", AutoSize = true, Margin = new Padding(58, 0, 3, 3),
            Checked = app.IsAlwaysLive(entry), Enabled = autoRadio.Checked,
        };
        always.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            entry.AlwaysLive = always.Checked;
            app.ApplyMic();
        };
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            entry.DeviceId = (combo.SelectedItem as DeviceItem)?.Id ?? "";
            entry.AlwaysLive = null;   // back to automatic for the new device (on for the guitar cable)
            loading = true;
            always.Checked = app.IsAlwaysLive(entry);
            loading = false;
            app.ApplyMic();
        };
        remove.Click += (_, _) =>
        {
            app.Settings.Mics.Remove(entry);
            app.ApplyMic();
            BeginInvoke(RebuildMicRows);   // not while this row's button is still handling its click
        };
        gain.ValueChanged += (_, _) =>
        {
            gainLabel.Text = gain.Value + "%";
            if (loading) return;
            int row = app.Settings.Mics.IndexOf(entry);
            app.SetMicGain(row, gain.Value, save: false);   // live
            micGainDebounce.Stop();
            micGainDebounce.Start();
        };

        combo.MouseWheel += WheelScrollsWindow;
        gain.MouseWheel += WheelScrollsWindow;

        tips.SetToolTip(name, "One of the inputs that are combined into VB-CABLE.");
        tips.SetToolTip(combo, "The microphone or input device to use: e.g. a USB microphone, your headset mic, or the Rocksmith guitar cable.\n" +
                               (Edition.HasPedalboard
                                   ? "The guitar (the Rocksmith cable, or the input picked in the Pedalboard tab) is captured raw, on its channel;\nwith the Pedalboard on, it comes through the pedals."
                                   : "The Rocksmith cable is captured raw, past Windows' voice processing."));
        tips.SetToolTip(remove, "Remove this input.");
        tips.SetToolTip(gain, "Level of this input. 100% = as Windows delivers it, 200% = twice as loud, 0% = muted.\n" +
                              "Use it to balance inputs, e.g. turn down a loud desk mic or turn up the guitar cable.\n" +
                              "In Auto-switch mode it also changes which input counts as \"louder\".");
        tips.SetToolTip(gainLabel, "Level of this input (100% = unchanged).");
        tips.SetToolTip(meter, "Live level of this input after the Level slider: what goes into the mix.");
        tips.SetToolTip(live, "This input is live right now: Discord hears it.");
        tips.SetToolTip(always, "In Auto-switch mode, an always-live input is mixed in all the time instead of competing with the mics.\n" +
                                "Use it for the guitar cable: otherwise your mic, hearing the room, keeps \"winning\" and the guitar is never live.\n" +
                                (Edition.HasPedalboard ? "Ticked automatically for the guitar (the Rocksmith cable or the Pedalboard's guitar input)."
                                                       : "Ticked automatically for the Rocksmith cable.") +
                                " (In Mix mode every input is live anyway.)");

        micRows.Add(new MicRow { Combo = combo, Gain = gain, GainLabel = gainLabel, Meter = meter, Live = live, AlwaysLive = always });
        var panel = Column(Row(name, combo, remove), Row(Caption("Level"), gain, gainLabel, meter, live), always);
        panel.Margin = new Padding(0, 0, 0, 6);
        return panel;
    }

    // ---------------- tooltips ----------------

    void AddTooltips()
    {
        tips.SetToolTip(allRadio, "Copy everything that plays on the chosen device.");
        tips.SetToolTip(sourceCombo, "The device whose sound is copied. \"Windows default output\" follows when you switch the default.");
        tips.SetToolTip(appsRadio, "Copy only the ticked apps (and the processes they start), whichever device they play on.");
        tips.SetToolTip(appsList, "Apps currently playing sound. Tick the ones to copy.");
        tips.SetToolTip(refreshButton, "Re-scan devices and apps playing sound.");
        tips.SetToolTip(monitorCombo, "The second device that receives the copy, e.g. your headphones.");
        tips.SetToolTip(delayBar, "Delays the copy so it lines up with a slower source device (e.g. Bluetooth headphones as the Windows default). Applies instantly; arrow keys move 1 ms.");
        tips.SetToolTip(volumeBar, "Volume of the copy only. The source device keeps its own volume.");
        tips.SetToolTip(latencyBar, "How much audio is queued on the output devices.\nMinimum = least lag (Windows low-latency mode where supported).\nRaise it if you hear crackles. Also used by the mic mixer and the pedalboard.");
        tips.SetToolTip(enabledBox, "Turns the speaker copy on or off.");

        tips.SetToolTip(addMicButton, $"Add another input (up to {MicMixer.MaxInputs}).");
        tips.SetToolTip(mixRadio, "All inputs are live at the same time. Good for different people, or voice + guitar cable.");
        tips.SetToolTip(autoRadio, "Only the input that hears you clearly louder is live (switches in about 0.3 s).\nAvoids the hollow, doubled sound of two mics picking up the same voice.");
        tips.SetToolTip(cableDownloadButton, $"Opens {VbCable.DownloadPage} in your browser. {Edition.Name} never downloads or installs anything itself.");
        tips.SetToolTip(updateOptInBox, "Off = no internet access at all.\nOn (after you allow it) = once a day, one HTTPS request to vb-audio.com to read the latest version number.");
        tips.SetToolTip(checkNowButton, "Check for a newer VB-CABLE now (only when online checks are allowed).");
        tips.SetToolTip(micEnabledBox, "Turns the mic mixer on or off. When off, Discord just gets silence from \"CABLE Output\".");
        tips.SetToolTip(listenBox, "Hear exactly what the mixer sends into VB-CABLE (your mics, the guitar and the voice effects): what Discord hears.\n" +
                                   "Handy for trying effects without Discord's Mic Test.");
        tips.SetToolTip(listenCombo, "Where to listen. Use headphones: on speakers your mic would pick itself up again (echo or howl).");

    }

    // ---------------- values ----------------

    void LoadValues()
    {
        loading = true;
        var s = app.Settings;
        allRadio.Checked = s.Mode == MonitorMode.AllAudio;
        appsRadio.Checked = !allRadio.Checked;
        delayBar.Value = Math.Clamp(s.CopyDelayMs, delayBar.Minimum, delayBar.Maximum);
        UpdateDelayLabel();
        volumeBar.Value = Math.Clamp(s.Volume, 0, 100);
        volumeLabel.Text = volumeBar.Value + "%";
        latencyBar.Value = Math.Clamp(s.LatencyMs, latencyBar.Minimum, latencyBar.Maximum);
        latencyLabel.Text = LatencyText(latencyBar.Value);
        enabledBox.Checked = s.Enabled;

        micEnabledBox.Checked = s.MicEnabled;
        mixRadio.Checked = s.MicMode == MicMode.Mix;
        autoRadio.Checked = !mixRadio.Checked;
        updateOptInBox.Checked = s.UpdateCheckOptIn;
        listenBox.Checked = s.ListenEnabled;
        loading = false;

        ShowFx(s.Fx);
        ShowPedals();
        sourceCombo.Enabled = allRadio.Checked;
        appsList.Enabled = appsRadio.Checked;
        UpdateCableInfo();
        UpdateStatus();
    }

    // ---------------- events ----------------

    void HookSpeakerEvents()
    {
        monitorCombo.SelectedIndexChanged += (_, _) => Commit();
        sourceCombo.SelectedIndexChanged += (_, _) => Commit();
        allRadio.CheckedChanged += (_, _) => { sourceCombo.Enabled = allRadio.Checked; appsList.Enabled = !allRadio.Checked; if (allRadio.Checked) Commit(); };
        appsRadio.CheckedChanged += (_, _) => { if (appsRadio.Checked) Commit(); };
        appsList.ItemCheck += (_, _) => BeginInvoke(Commit);   // fires before the check state changes
        enabledBox.CheckedChanged += (_, _) => Commit();
        refreshButton.Click += (_, _) => { LoadDevices(); LoadApps(); };

        delayBar.ValueChanged += (_, _) =>
        {
            UpdateDelayLabel();
            if (loading) return;
            app.SetCopyDelay(delayBar.Value, save: false);   // live, so it can be tuned by ear
            delayDebounce.Stop();
            delayDebounce.Start();
        };
        delayDebounce.Tick += (_, _) => { delayDebounce.Stop(); app.SetCopyDelay(delayBar.Value, save: true); };

        volumeBar.ValueChanged += (_, _) =>
        {
            volumeLabel.Text = volumeBar.Value + "%";
            if (!loading) app.SetVolume(volumeBar.Value);
        };

        latencyBar.ValueChanged += (_, _) =>
        {
            latencyLabel.Text = LatencyText(latencyBar.Value);
            if (loading) return;
            latencyDebounce.Stop();   // restart the audio only once the slider settles
            latencyDebounce.Start();
        };
        latencyDebounce.Tick += (_, _) =>
        {
            latencyDebounce.Stop();
            app.Settings.LatencyMs = latencyBar.Value;
            app.Apply();
            app.ApplyMic();   // the mic mixer and the pedalboard use the same output buffer setting
            app.ApplyPedal(restartMic: false);
            app.ApplyListen();
        };
    }

    void HookMicEvents()
    {
        micEnabledBox.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            app.Settings.MicEnabled = micEnabledBox.Checked;
            app.ApplyMic();
        };
        addMicButton.Click += (_, _) =>
        {
            if (app.Settings.Mics.Count >= MicMixer.MaxInputs) return;
            app.Settings.Mics.Add(new MicEntry());
            app.Settings.Save();
            RebuildMicRows();
        };
        mixRadio.CheckedChanged += (_, _) =>
        {
            foreach (var row in micRows) row.AlwaysLive.Enabled = autoRadio.Checked;
            if (!loading && mixRadio.Checked) app.SetMicMode(MicMode.Mix);
        };
        autoRadio.CheckedChanged += (_, _) => { if (!loading && autoRadio.Checked) app.SetMicMode(MicMode.AutoSwitch); };
        micGainDebounce.Tick += (_, _) => { micGainDebounce.Stop(); app.Settings.Save(); };
        listenBox.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            app.Settings.ListenEnabled = listenBox.Checked;
            app.ApplyListen();
        };
        listenCombo.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            app.Settings.ListenDeviceId = (listenCombo.SelectedItem as DeviceItem)?.Id ?? "";
            app.ApplyListen();
        };
        listenCombo.MouseWheel += WheelScrollsWindow;

        updateOptInBox.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            if (updateOptInBox.Checked)
            {
                var answer = MessageBox.Show(this,
                    $"Automatic update checks let {Edition.Name} connect to the internet.\n\n" +
                    "About once a day it makes one secure (HTTPS) request to vb-audio.com to read the latest " +
                    "VB-CABLE version number. Nothing about you or your PC is sent, and nothing is downloaded " +
                    "or installed. You can switch this off at any time.\n\n" +
                    $"Allow {Edition.Name} to access the internet for update checks?",
                    "Allow internet access?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    loading = true;
                    updateOptInBox.Checked = false;
                    loading = false;
                    return;
                }
            }
            app.SetUpdateCheckOptIn(updateOptInBox.Checked);
            UpdateCableInfo();
        };
    }

    void Commit()
    {
        if (loading || IsDisposed) return;
        var s = app.Settings;
        s.MonitorDeviceId = (monitorCombo.SelectedItem as DeviceItem)?.Id ?? "";
        s.SourceDeviceId = (sourceCombo.SelectedItem as DeviceItem)?.Id ?? "";
        s.Mode = appsRadio.Checked ? MonitorMode.SelectedApps : MonitorMode.AllAudio;
        s.Apps = appsList.CheckedItems.Cast<string>().Select(ProcessUtil.Normalize).ToList();
        s.Enabled = enabledBox.Checked;
        app.Apply();
    }

    // ---------------- devices ----------------

    void LoadDevices()
    {
        loading = true;
        var render = new List<DeviceItem>();
        var capture = new List<DeviceItem>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                render.Add(new DeviceItem(d.ID, d.FriendlyName));
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                if (!VbCable.IsCable(d.FriendlyName))   // "CABLE Output" as an input would feed back into itself
                    capture.Add(new DeviceItem(d.ID, d.FriendlyName));
        }
        catch { }
        captureDevices = capture;

        Fill(monitorCombo, render, app.Settings.MonitorDeviceId, null);
        Fill(sourceCombo, render, app.Settings.SourceDeviceId, new DeviceItem("", "Windows default output (follows changes)"));
        for (int i = 0; i < micRows.Count && i < app.Settings.Mics.Count; i++)
            Fill(micRows[i].Combo, capture, app.Settings.Mics[i].DeviceId, null);
        LoadPedalDevices(render, capture);
        Fill(listenCombo, render.Where(d => !VbCable.IsCable(d.Name)).ToList(), app.Settings.ListenDeviceId, new DeviceItem("", "(pick your headphones)"));
        loading = false;
    }

    /// <summary>A device was plugged in or out: reload the lists, unless the user has one of them open right now.</summary>
    void OnDevicesChanged()
    {
        if (IsDisposed) return;
        if (AllCombos(this).Any(c => c.DroppedDown)) { BeginInvoke(() => { if (!IsDisposed) devicesRetry.Start(); }); return; }
        LoadDevices();
    }

    readonly System.Windows.Forms.Timer devicesRetry = new() { Interval = 1500 };

    static IEnumerable<ComboBox> AllCombos(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is ComboBox combo) yield return combo;
            foreach (var inner in AllCombos(c)) yield return inner;
        }
    }

    void Fill(ComboBox combo, List<DeviceItem> devices, string selectedId, DeviceItem? first)
    {
        combo.Items.Clear();
        if (first != null) combo.Items.Add(first);
        foreach (var d in devices) combo.Items.Add(d);
        if (!string.IsNullOrEmpty(selectedId) && !combo.Items.Cast<DeviceItem>().Any(d => d.Id == selectedId))
            combo.Items.Add(new DeviceItem(selectedId,
                app.Settings.DeviceFingerprints.TryGetValue(selectedId, out var print)
                    ? $"{print.Name} (disconnected)"
                    : "(disconnected device)"));
        var item = combo.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == selectedId);
        if (item != null) combo.SelectedItem = item;
        else combo.SelectedIndex = -1;
    }

    void LoadApps()
    {
        loading = true;
        var selected = new HashSet<string>(app.Settings.Apps.Select(ProcessUtil.Normalize));
        var names = new SortedSet<string>(ProcessUtil.GetAudioApps());
        names.UnionWith(selected);
        // Apps that (indirectly) started this one can't be captured without hearing ourselves in a loop.
        names.ExceptWith(ProcessUtil.AncestorNames());
        appsList.Items.Clear();
        foreach (var n in names) appsList.Items.Add(n + ".exe", selected.Contains(n));
        loading = false;
    }

    void UpdateCableInfo()
    {
        bool found;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            found = VbCable.FindCableInput(enumerator) != null;
        }
        catch { found = false; }
        cableLabel.ForeColor = found ? SystemColors.ControlText : Color.Firebrick;
        cableLabel.Text = found
            ? "VB-CABLE found. The mixed inputs go to \"CABLE Input\"; Discord listens on \"CABLE Output\"."
            : "VB-CABLE is not installed. Install it from the download page below (it's a free driver from " +
              $"VB-Audio; installing needs admin rights and a restart). {Edition.Name} picks it up automatically.";
        var installed = VbCable.InstalledVersion();
        cableVersionLabel.Text = "Installed version: " + (installed?.ToString() ?? (found ? "unknown" : "not installed"));
        checkNowButton.Enabled = app.Settings.UpdateCheckOptIn;
    }

    static void OpenDownloadPage()
    {
        // Fixed HTTPS address, opened in the user's own browser.
        try { Process.Start(new ProcessStartInfo(VbCable.DownloadPage) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, Edition.Name); }
    }

    void UpdateMeters()
    {
        if (IsDisposed || !Visible) return;
        var (levels, active) = app.MicLevels();
        bool mix = app.Settings.MicMode == MicMode.Mix;
        for (int i = 0; i < micRows.Count; i++)
        {
            float level = i < levels.Length ? levels[i] : 0;
            micRows[i].Meter.Value = ToMeter(level);
            bool allLive = mix || active < 0 || micRows[i].AlwaysLive.Checked;
            micRows[i].Live.Text = app.Settings.MicEnabled && (active == i || (allLive && level > 0)) ? "● live" : "";
        }
    }

    /// <summary>Peak 0..1 to a 0..100 meter on a dB scale (-60 dB .. 0 dB).</summary>
    static int ToMeter(float peak) =>
        peak <= 0.001f ? 0 : Math.Clamp((int)((20 * Math.Log10(peak) + 60) / 60 * 100), 0, 100);

    static string LatencyText(int ms) => ms == 0 ? "Minimum" : ms + " ms";

    void UpdateDelayLabel() =>
        delayLabel.Text = delayBar.Value == 0 ? "No extra delay" : $"Copy delayed by {delayBar.Value} ms";

    void UpdateStatus()
    {
        if (IsDisposed) return;
        statusLabel.Text = "Status: " + app.Status;
        micStatusLabel.Text = "Status: " + app.MicStatus;
        updateStatusLabel.Text = app.UpdateCheckStatus;
        listenStatusLabel.Text = "Status: " + app.ListenStatus;
        if (listenBox.Checked != app.Settings.ListenEnabled)
        {
            bool wasLoadingForListen = loading;
            loading = true;
            listenBox.Checked = app.Settings.ListenEnabled;
            loading = wasLoadingForListen;
        }
        UpdatePedalStatus();
        UpdateFunStatus();
        bool wasLoading = loading;
        loading = true;
        if (enabledBox.Checked != app.Settings.Enabled) enabledBox.Checked = app.Settings.Enabled;
        if (micEnabledBox.Checked != app.Settings.MicEnabled) micEnabledBox.Checked = app.Settings.MicEnabled;
        if (mixRadio.Checked != (app.Settings.MicMode == MicMode.Mix))
        {
            mixRadio.Checked = app.Settings.MicMode == MicMode.Mix;
            autoRadio.Checked = !mixRadio.Checked;
        }
        loading = wasLoading;
    }
}
