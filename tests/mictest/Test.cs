// 1.6.2 checks, run for both editions. SILENT and safe: no TrayApp, no settings, no sound.
// (A) Device cleanup: audio devices are opened and closed (never started, so no audio flows), and opens that
//     must fail (an unknown device) release everything at once: the process handle count stays flat.
// (B) "Start with Windows": the Startup-folder shortcut and the one-time move from the old registry value, against
//     a scratch Startup folder, a test registry key and stand-in exe files (Startup.StartupDir / LegacyRunKey /
//     CurrentExe are redirected before anything else): the real Startup folder and Run key are never touched.
// Arg 0 = scratch folder (default: the temp folder).
using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using AudioMonitor;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
Console.WriteLine($"Edition under test: {Edition.Name}");

// ---- A) device cleanup ----
int Handles() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); using var p = Process.GetCurrentProcess(); return p.HandleCount; }
int HandlesNow() { using var p = Process.GetCurrentProcess(); return p.HandleCount; }
{
    using var en = new MMDeviceEnumerator();
    string render = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
    string? capture = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault()?.ID;
    const string Bogus = "{not-a-device}";

    using (new LowLatencyOutput(render, 0, true)) { }   // warm-up: first use loads DLLs that keep their handles
    try { using var _ = new RawCapture(Bogus, true); } catch { }

    int before = Handles();
    int thrown = 0;
    for (int i = 0; i < 300; i++)
    {
        try { using var r = new RawCapture(Bogus, true); } catch { thrown++; }
        try { using var l = new LowLatencyOutput(Bogus, 0, true); } catch { thrown++; }
    }
    int rightAfter = HandlesNow();   // before any garbage collection: what the old code left lying around
    Check(thrown == 600, "every open of an unknown device fails");
    Check(rightAfter - before < 20, $"failed opens release everything at once ({before} -> {rightAfter} handles)");

    before = Handles();
    for (int i = 0; i < 100; i++)
    {
        using (new LowLatencyOutput(render, 0, true)) { }
        if (capture != null) using (new RawCapture(capture, true)) { }
    }
    int after = Handles();
    Check(after - before < 20, $"open + close (never started) leaves nothing behind ({before} -> {after} handles)");
}

// ---- B) "Start with Windows" ----
string scratch = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "startup_test");
if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
string exes = Path.Combine(scratch, "exes");
Directory.CreateDirectory(exes);
string funbox = Path.Combine(exes, "AudioFunbox.exe"), monitor = Path.Combine(exes, "AudioMonitor.exe");
File.WriteAllBytes(funbox, Array.Empty<byte>());    // stand-ins: a shortcut only needs its target to exist
File.WriteAllBytes(monitor, Array.Empty<byte>());

const string TestKey = @"Software\AudioMonitorFunboxTest\Run";
Startup.StartupDir = Path.Combine(scratch, "Startup");
Startup.LegacyRunKey = TestKey;
Startup.CurrentExe = funbox;
string lnk = Path.Combine(Startup.StartupDir, Startup.ShortcutName);
void SetLegacy(string value) { using var k = Registry.CurrentUser.CreateSubKey(TestKey); k.SetValue("AudioMonitor", value); }
string? GetLegacy() { using var k = Registry.CurrentUser.OpenSubKey(TestKey); return k?.GetValue("AudioMonitor") as string; }
bool Same(string? a, string b) => a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

try
{
    Check(!Startup.IsEnabled(), "nothing set: off");

    Startup.Set(true);
    Check(File.Exists(lnk) && Same(Startup.ReadShortcutTarget(lnk), funbox), "tick: a shortcut to the exe appears in the Startup folder");
    Check(Startup.IsEnabled(), "tick: shows as on");

    Startup.Set(false);
    Check(!File.Exists(lnk) && !Startup.IsEnabled(), "untick: shortcut removed, off");

    SetLegacy($"\"{monitor}\"");
    Startup.Repair();
    Check(GetLegacy() == null, "old registry value is removed");
    Check(Same(Startup.ReadShortcutTarget(lnk), monitor), "old value's exe (still there) becomes the shortcut's target");
    Check(!Startup.IsEnabled(), "this edition shows off: Windows starts the other one");
    Startup.Set(false);

    SetLegacy("\"X:\\gone\\AudioMonitor.exe\"");
    Startup.Repair();
    Check(GetLegacy() == null && Same(Startup.ReadShortcutTarget(lnk), funbox), "broken old value: shortcut to this exe instead, value removed");

    Startup.WriteShortcut(lnk, Path.Combine(scratch, "moved_away.exe"));
    Startup.Repair();
    Check(Same(Startup.ReadShortcutTarget(lnk), funbox), "shortcut to a missing exe is pointed at this exe");

    Startup.WriteShortcut(lnk, monitor);
    Startup.Repair();
    Check(Same(Startup.ReadShortcutTarget(lnk), monitor), "shortcut to the other (existing) edition is left alone");

    Startup.Set(true);
    Check(Same(Startup.ReadShortcutTarget(lnk), funbox) && Startup.IsEnabled(), "ticking here moves it to this edition");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\AudioMonitorFunboxTest", throwOnMissingSubKey: false);
    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
}
Check(Registry.CurrentUser.OpenSubKey(@"Software\AudioMonitorFunboxTest") == null, "test registry key cleaned up");

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
