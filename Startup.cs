using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace AudioMonitor;

/// <summary>
/// "Start with Windows": a shortcut to the exe in your Startup folder (%APPDATA%\Microsoft\Windows\Start Menu\
/// Programs\Startup), named "SonicFunbox". Being a real shortcut, Windows lists it under Startup apps with
/// the app's own name and icon, and can usually follow the exe if it moves. Both editions share it: it starts the
/// edition it was last ticked in. The installer's "Start with Windows" creates the same shortcut.
/// Up to 1.6.0 this was a registry value (HKCU\...\Run, "AudioMonitor") holding the exe's path; it's turned into
/// the shortcut once, then removed.
/// </summary>
static class Startup
{
    public const string ShortcutName = "SonicFunbox.lnk";
    /// <summary>The name up to 2.0.2 (AudioMonitorFunbox); turned into ShortcutName at the first start.</summary>
    const string OldShortcutName = "AudioMonitorFunbox.lnk";
    const string LegacyValueName = "AudioMonitor";

    // Overridable for tests only, so a test never touches the real Startup folder or the real Run key.
    internal static string StartupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    internal static string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string CurrentExe = Environment.ProcessPath ?? "";

    static string ShortcutPath => Path.Combine(StartupDir, ShortcutName);
    static string OldShortcutPath => Path.Combine(StartupDir, OldShortcutName);

    /// <summary>True only if the shortcut exists and starts this exact exe.</summary>
    public static bool IsEnabled() => SamePath(ReadShortcutTarget(ShortcutPath), CurrentExe);

    public static void Set(bool enabled)
    {
        if (enabled) WriteShortcut(ShortcutPath, CurrentExe);
        else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        if (File.Exists(OldShortcutPath)) File.Delete(OldShortcutPath);
        DeleteLegacyValue();
    }

    /// <summary>
    /// At every start: turn an old registry entry into the shortcut, and point a shortcut whose exe no longer exists
    /// at this exe (moved or renamed, e.g. after switching editions). A shortcut to an existing exe is left alone.
    /// </summary>
    public static void Repair()
    {
        try
        {
            MigrateOldShortcut();
            string? legacy = ReadLegacyValue();
            if (legacy != null)
            {
                string path = legacy.Trim().Trim('"');
                if (!File.Exists(ShortcutPath)) WriteShortcut(ShortcutPath, File.Exists(path) ? path : CurrentExe);
                DeleteLegacyValue();
            }
            if (File.Exists(ShortcutPath))
            {
                string? target = ReadShortcutTarget(ShortcutPath);
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) WriteShortcut(ShortcutPath, CurrentExe);
            }
        }
        catch { /* never block the app's start over this */ }
    }

    /// <summary>
    /// Up to 2.0.2 the shortcut was called AudioMonitorFunbox and pointed at AudioFunbox.exe or AudioMonitor.exe.
    /// Replace it with the new name; if it still points at one of the old exes (or at nothing), it now starts
    /// this exe. A shortcut to some other existing exe keeps that target.
    /// </summary>
    static void MigrateOldShortcut()
    {
        if (!File.Exists(OldShortcutPath)) return;
        string? target = ReadShortcutTarget(OldShortcutPath);
        string file = string.IsNullOrEmpty(target) ? "" : Path.GetFileName(target);
        bool oldExe = file.Equals("AudioFunbox.exe", StringComparison.OrdinalIgnoreCase) ||
                      file.Equals("AudioMonitor.exe", StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(ShortcutPath))
            WriteShortcut(ShortcutPath, string.IsNullOrEmpty(target) || oldExe || !File.Exists(target) ? CurrentExe : target);
        File.Delete(OldShortcutPath);
    }

    static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ---------------- the old registry value ----------------

    static string? ReadLegacyValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey);
        return key?.GetValue(LegacyValueName) as string;
    }

    static void DeleteLegacyValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    // ---------------- shortcut (.lnk) files through the Windows shell ----------------

    internal static void WriteShortcut(string lnkPath, string exePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var link = (IShellLinkW)(object)new ShellLink();   // COM: the cast asks Windows for the interface
        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetDescription(Path.GetFileNameWithoutExtension(exePath) + ": start with Windows");
            link.SetIconLocation(exePath, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    internal static string? ReadShortcutTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        var link = (IShellLinkW)(object)new ShellLink();   // COM: the cast asks Windows for the interface
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var buffer = new StringBuilder(1024);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            return buffer.ToString();
        }
        catch { return null; }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    sealed class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
