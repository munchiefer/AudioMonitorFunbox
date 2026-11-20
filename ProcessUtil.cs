using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioMonitor;

static class ProcessUtil
{
    public static string Normalize(string? name)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        return name.EndsWith(".exe") ? name[..^4] : name;
    }

    /// <summary>
    /// True while Rocksmith (2011, 2014 / Remastered, Rocksmith+) runs. It takes the Real Tone cable for itself
    /// (exclusive mode), so the pedalboard and mic mixer step aside meanwhile.
    /// </summary>
    public static bool IsRocksmithRunning()
    {
        var all = Process.GetProcesses();
        try { return all.Any(p => p.ProcessName.StartsWith("Rocksmith", StringComparison.OrdinalIgnoreCase)); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    /// <summary>Names of processes that currently have an audio session on any output device.</summary>
    public static List<string> GetAudioApps()
    {
        var names = new HashSet<string>();
        int self = Environment.ProcessId;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        int pid = (int)sessions[i].GetProcessID;
                        if (pid == 0 || pid == self) continue;
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            names.Add(Normalize(process.ProcessName));
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return names.OrderBy(n => n).ToList();
    }

    /// <summary>
    /// For each selected app name, returns the "root" processes (whose parent is not the same app),
    /// so capturing each root's process tree covers the whole app exactly once.
    /// </summary>
    public static HashSet<int> ResolveRootPids(IEnumerable<string> appNames)
    {
        var wanted = new HashSet<string>(appNames.Select(Normalize));
        var result = new HashSet<int>();
        if (wanted.Count == 0) return result;

        var procs = Snapshot();
        var nameByPid = new Dictionary<int, string>();
        var parentByPid = new Dictionary<int, int>();
        foreach (var p in procs) { nameByPid[p.Pid] = p.Name; parentByPid[p.Pid] = p.ParentPid; }

        // Capturing a process tree that contains this app would re-capture our own output: an endless,
        // ever-louder feedback loop. So never capture this process or any of its ancestors
        // (e.g. explorer, a terminal or a launcher that started us).
        var forbidden = new HashSet<int> { Environment.ProcessId };
        for (int pid = Environment.ProcessId; parentByPid.TryGetValue(pid, out var parent) && parent != 0 && forbidden.Add(parent); pid = parent) { }

        foreach (var p in procs)
        {
            if (forbidden.Contains(p.Pid) || !wanted.Contains(p.Name)) continue;
            if (nameByPid.TryGetValue(p.ParentPid, out var parentName) && parentName == p.Name) continue;
            result.Add(p.Pid);
        }
        return result;
    }

    /// <summary>Names of this app's ancestor processes (these can't be captured, see <see cref="ResolveRootPids"/>).</summary>
    public static HashSet<string> AncestorNames()
    {
        var procs = Snapshot();
        var byPid = new Dictionary<int, (int Parent, string Name)>();
        foreach (var p in procs) byPid[p.Pid] = (p.ParentPid, p.Name);
        var names = new HashSet<string>();
        var seen = new HashSet<int> { Environment.ProcessId };
        for (int pid = Environment.ProcessId; byPid.TryGetValue(pid, out var e) && e.Parent != 0 && seen.Add(e.Parent); pid = e.Parent)
            if (byPid.TryGetValue(e.Parent, out var parent)) names.Add(parent.Name);
        return names;
    }

    static List<(int Pid, int ParentPid, string Name)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return list;
            do list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, Normalize(entry.szExeFile)));
            while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
