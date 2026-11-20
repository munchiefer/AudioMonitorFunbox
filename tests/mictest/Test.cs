// 1.6.1 checks: "Start with Windows" as a Startup-folder shortcut, and the one-time move from the old registry
// value. Runs ONLY against a scratch Startup folder and a test registry key (Startup.StartupDir / LegacyRunKey
// are redirected before anything else): the real Startup folder and the real Run key are never touched.
// No TrayApp, no settings, no sound. Arg 0 = scratch folder.
using Microsoft.Win32;
using AudioMonitor;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

string scratch = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "startup_test");
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string funbox = Path.Combine(root, "portable versions", "AudioFunbox.exe");
string monitor = Path.Combine(root, "portable versions", "AudioMonitor.exe");
if (!File.Exists(funbox) || !File.Exists(monitor)) { Console.WriteLine($"portable exes not found under {root}"); return 2; }

const string TestKey = @"Software\AudioMonitorFunboxTest\Run";
Startup.StartupDir = Path.Combine(scratch, "Startup");
Startup.LegacyRunKey = TestKey;
Startup.CurrentExe = funbox;
if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
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

    // Old registry value pointing at an existing exe (the other edition): kept as it was, as a shortcut.
    SetLegacy($"\"{monitor}\"");
    Startup.Repair();
    Check(GetLegacy() == null, "old registry value is removed");
    Check(Same(Startup.ReadShortcutTarget(lnk), monitor), "old value's exe (still there) becomes the shortcut's target");
    Check(!Startup.IsEnabled(), "this edition shows off: Windows starts the other one");
    Startup.Set(false);

    // Old registry value pointing at a file that's gone (the broken case after exes moved): this exe takes over.
    SetLegacy("\"X:\\gone\\AudioMonitor.exe\"");
    Startup.Repair();
    Check(GetLegacy() == null && Same(Startup.ReadShortcutTarget(lnk), funbox), "broken old value: shortcut to this exe instead, value removed");

    // Shortcut whose exe disappeared: repaired.
    Startup.WriteShortcut(lnk, Path.Combine(scratch, "moved_away.exe"));
    Startup.Repair();
    Check(Same(Startup.ReadShortcutTarget(lnk), funbox), "shortcut to a missing exe is pointed at this exe");

    // Shortcut to the other edition, which exists: left alone.
    Startup.WriteShortcut(lnk, monitor);
    Startup.Repair();
    Check(Same(Startup.ReadShortcutTarget(lnk), monitor), "shortcut to the other (existing) edition is left alone");

    // Tick in this edition takes it over.
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
