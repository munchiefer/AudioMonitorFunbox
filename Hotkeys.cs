using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMonitor;

/// <summary>A hotkey: a keyboard key or mouse button (middle, Mouse 4, Mouse 5), with optional modifiers.</summary>
public sealed class HotkeyBinding
{
    /// <summary>Windows virtual-key code. Mouse buttons: 4 = middle, 5 = Mouse 4 (back), 6 = Mouse 5 (forward).</summary>
    public int Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    public bool IsMouse => Key is 4 or 5 or 6;

    /// <summary>Left/right mouse buttons and bare modifier keys can't be hotkeys (they'd fire constantly).</summary>
    public static bool IsValidKey(int vk) =>
        vk is >= 3 and <= 254 && vk is not (0x10 or 0x11 or 0x12) && vk is not (>= 0xA0 and <= 0xA5) && vk is not (0x5B or 0x5C);

    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    public HotkeyBinding? Sanitized() => IsValidKey(Key) ? this : null;

    public bool SameAs(HotkeyBinding? other) =>
        other != null && other.Key == Key && other.Ctrl == Ctrl && other.Alt == Alt && other.Shift == Shift && other.Win == Win;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    static string KeyName(int vk) => vk switch
    {
        4 => "Middle mouse",
        5 => "Mouse 4",
        6 => "Mouse 5",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        0x14 => "Caps Lock", 0x0D => "Enter", 0x08 => "Backspace", 0x20 => "Space", 0x09 => "Tab",
        0x21 => "Page Up", 0x22 => "Page Down", 0x2D => "Insert", 0x2E => "Delete", 0x13 => "Pause", 0x91 => "Scroll Lock",
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xBA => ";", 0xDE => "'",
        0xBC => ",", 0xBE => ".", 0xBF => "/",
        _ => ((Keys)vk).ToString(),
    };
}

/// <summary>
/// Decides what a key/button press means: the on/off hotkey fires once per press (key repeat ignored);
/// push-to-talk is held from the press of its key until that key is released. Extra modifiers held at the
/// time (e.g. Shift to sprint in a game) don't stop a hotkey from working. Pure logic, so it can be tested.
/// </summary>
sealed class HotkeyMatcher
{
    readonly bool[] down = new bool[256];
    HotkeyBinding? toggle, pushToTalk;
    public bool PushToTalkHeld { get; private set; }
    public event Action? TogglePressed;
    public event Action<bool>? PushToTalkChanged;

    public void SetBindings(HotkeyBinding? toggleKey, HotkeyBinding? pushToTalkKey)
    {
        toggle = toggleKey;
        pushToTalk = pushToTalkKey;
        if (PushToTalkHeld && (pushToTalk == null || !down[pushToTalk.Key])) Release();
    }

    public void OnInput(int vk, bool isDown, bool ctrl, bool alt, bool shift, bool win)
    {
        if (vk <= 0 || vk > 255) return;
        bool wasDown = down[vk];
        down[vk] = isDown;
        if (isDown && !wasDown)
        {
            if (Matches(toggle, vk, ctrl, alt, shift, win)) TogglePressed?.Invoke();
            if (!PushToTalkHeld && Matches(pushToTalk, vk, ctrl, alt, shift, win))
            {
                PushToTalkHeld = true;
                PushToTalkChanged?.Invoke(true);
            }
        }
        else if (!isDown && wasDown && PushToTalkHeld && pushToTalk?.Key == vk) Release();
    }

    void Release()
    {
        PushToTalkHeld = false;
        PushToTalkChanged?.Invoke(false);
    }

    static bool Matches(HotkeyBinding? b, int vk, bool ctrl, bool alt, bool shift, bool win) =>
        b != null && b.Key == vk && (!b.Ctrl || ctrl) && (!b.Alt || alt) && (!b.Shift || shift) && (!b.Win || win);
}

/// <summary>
/// Global hotkeys via Windows' low-level keyboard/mouse listeners, on a thread of their own (so a busy UI can
/// never delay anyone's typing). They only *listen*: every key and click still goes to the app in front, and
/// nothing is recorded or stored beyond "is my hotkey down". The listeners are installed only while a hotkey
/// needs them: none at all if no hotkey is set, and the mouse one only for a mouse-button hotkey.
/// </summary>
sealed class GlobalHotkeys : IDisposable
{
    const int WhKeyboardLl = 13, WhMouseLl = 14, WmQuit = 0x0012, WmRefresh = 0x8001;

    readonly SynchronizationContext ui;
    readonly HotkeyMatcher matcher = new();
    readonly LowLevelProc keyboardProc, mouseProc;   // kept referenced: Windows calls them
    readonly ManualResetEventSlim started = new();
    readonly object gate = new();
    HotkeyBinding? toggle, pushToTalk;
    volatile Action<HotkeyBinding?>? capture;
    Thread? thread;
    volatile uint threadId;
    IntPtr keyboardHook, mouseHook;

    public event Action? TogglePressed;
    public event Action<bool>? PushToTalkChanged;

    public GlobalHotkeys()
    {
        ui = SynchronizationContext.Current ?? new SynchronizationContext();
        keyboardProc = KeyboardProc;
        mouseProc = MouseProc;
        matcher.TogglePressed += () => ui.Post(_ => TogglePressed?.Invoke(), null);
        matcher.PushToTalkChanged += held => ui.Post(_ => PushToTalkChanged?.Invoke(held), null);
    }

    public void SetBindings(HotkeyBinding? toggleKey, HotkeyBinding? pushToTalkKey)
    {
        lock (gate)
        {
            toggle = toggleKey;
            pushToTalk = pushToTalkKey;
            matcher.SetBindings(toggleKey, pushToTalkKey);
        }
        Refresh();
    }

    /// <summary>The next key or mouse button (middle/4/5) pressed anywhere becomes the result; Esc = cancel (null).</summary>
    public void BeginCapture(Action<HotkeyBinding?> done)
    {
        capture = done;
        Refresh();
    }

    public void CancelCapture()
    {
        capture = null;
        Refresh();
    }

    void Refresh()
    {
        bool needed;
        lock (gate) needed = capture != null || toggle != null || pushToTalk != null;
        if (thread == null)
        {
            if (!needed) return;
            thread = new Thread(Run) { IsBackground = true, Name = "Hotkeys" };
            thread.Start();
            started.Wait(2000);
        }
        PostThreadMessageW(threadId, WmRefresh, IntPtr.Zero, IntPtr.Zero);
    }

    void Run()
    {
        threadId = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // create this thread's message queue before anyone posts to it
        started.Set();
        UpdateHooks();
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            if (msg.Message == WmRefresh) UpdateHooks();
        SetHook(ref keyboardHook, false, WhKeyboardLl, keyboardProc);
        SetHook(ref mouseHook, false, WhMouseLl, mouseProc);
    }

    void UpdateHooks()
    {
        bool capturing = capture != null, keyboard, mouse;
        lock (gate)
        {
            keyboard = capturing || (toggle != null && !toggle.IsMouse) || (pushToTalk != null && !pushToTalk.IsMouse);
            mouse = capturing || toggle?.IsMouse == true || pushToTalk?.IsMouse == true;
        }
        SetHook(ref keyboardHook, keyboard, WhKeyboardLl, keyboardProc);
        SetHook(ref mouseHook, mouse, WhMouseLl, mouseProc);
    }

    static void SetHook(ref IntPtr hook, bool wanted, int type, LowLevelProc proc)
    {
        if (wanted && hook == IntPtr.Zero) hook = SetWindowsHookExW(type, proc, GetModuleHandleW(null), 0);
        else if (!wanted && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    static (bool Ctrl, bool Alt, bool Shift, bool Win) Modifiers() =>
        (Down(0x11), Down(0x12), Down(0x10), Down(0x5B) || Down(0x5C));

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32(), vk = Marshal.ReadInt32(lParam);
            bool isDown = message is 0x100 or 0x104, isUp = message is 0x101 or 0x105;
            if (isDown && capture is { } done && !HotkeyBinding.IsModifier(vk))
            {
                Finish(done, vk == 0x1B ? null : vk);
                return 1;   // while setting a hotkey, that key press is used up (it doesn't also type or click)
            }
            if (isDown || isUp) Feed(vk, isDown);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            int vk = message switch
            {
                0x207 or 0x208 => 4,   // middle button down/up
                0x20B or 0x20C => (Marshal.ReadInt32(lParam, 8) >> 16) == 1 ? 5 : 6,   // X buttons: Mouse 4 / Mouse 5
                _ => 0,
            };
            if (vk != 0)
            {
                bool isDown = message is 0x207 or 0x20B;
                if (isDown && capture is { } done) { Finish(done, vk); return 1; }
                Feed(vk, isDown);
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void Feed(int vk, bool isDown)
    {
        var (ctrl, alt, shift, win) = Modifiers();
        lock (gate) matcher.OnInput(vk, isDown, ctrl, alt, shift, win);
    }

    void Finish(Action<HotkeyBinding?> done, int? vk)
    {
        capture = null;
        HotkeyBinding? result = null;
        if (vk is int key)
        {
            var (ctrl, alt, shift, win) = Modifiers();
            result = new HotkeyBinding { Key = key, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };
        }
        ui.Post(_ => done(result), null);
        PostThreadMessageW(threadId, WmRefresh, IntPtr.Zero, IntPtr.Zero);   // drop listeners no longer needed
    }

    public void Dispose()
    {
        if (thread != null)
        {
            PostThreadMessageW(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            thread.Join(1000);
        }
        started.Dispose();
    }

    // ---- interop ----

    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr Hwnd;
        public int Message;
        public IntPtr WParam, LParam;
        public int Time, X, Y;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, LowLevelProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetMessageW(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostThreadMessageW(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr GetModuleHandleW(string? name);
}

/// <summary>
/// Short confirmation blip on the Windows default output (your speakers/headphones only, never into VB-CABLE):
/// rising = voice effects on, falling = off.
/// </summary>
static class Blip
{
    public static void Play(bool on)
    {
        WasapiOut? player = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
#pragma warning disable CA2000 // disposed when playback ends (PlaybackStopped below), or in the catch
            player = new WasapiOut(device, AudioClientShareMode.Shared, true, 50);
#pragma warning restore CA2000
            player.Init(new Tone(on, mix.SampleRate, mix.Channels));
            var context = SynchronizationContext.Current;
            var p = player;
            player.PlaybackStopped += (_, _) =>
            {
                if (context != null) context.Post(_ => p.Dispose(), null);
                else p.Dispose();
            };
            player.Play();
        }
        catch
        {
            player?.Dispose();   // a missing blip is no reason to bother anyone
        }
    }

    sealed class Tone : ISampleProvider
    {
        readonly int rate, channels, length;
        readonly double first, second;
        int position;

        public Tone(bool on, int rate, int channels)
        {
            this.rate = rate;
            this.channels = channels;
            length = rate * 110 / 1000;
            (first, second) = on ? (880.0, 1320.0) : (660.0, 440.0);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = Math.Min(count / channels, length - position);
            if (frames <= 0) return 0;
            int half = length / 2, fade = rate * 5 / 1000;
            for (int i = 0; i < frames; i++, position++)
            {
                int local = position < half ? position : position - half;
                double hz = position < half ? first : second;
                float env = Math.Min(1f, Math.Min(local, half - local) / (float)fade);
                float v = 0.15f * env * (float)Math.Sin(2 * Math.PI * hz * position / rate);
                for (int c = 0; c < channels; c++) buffer[offset + i * channels + c] = v;
            }
            return frames * channels;
        }
    }
}
