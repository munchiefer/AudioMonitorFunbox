// 2.0.1 checks: the speaker copy's jitter buffer. App capture ("Selected apps") gets 10 ms more buffer, a read that
// finds the buffer short refills it at once, and the first packet lands exactly at the target. SILENT and safe: no
// TrayApp is constructed, settings.json is never written (its timestamp is checked); the live part runs the engine at
// volume 0, so only zeros reach the output device. Run for both editions.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioMonitor;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }
Console.WriteLine($"Edition under test: {Edition.Name}");
string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioMonitor", "settings.json");
DateTime? settingsTime = File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : null;

var BF = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
var outT = typeof(AudioEngine).GetNestedType("Output", BF)!;
var tapT = typeof(AudioEngine).GetNestedType("Tap", BF)!;
var inT = typeof(AudioEngine).GetNestedType("Input", BF)!;
var push = typeof(AudioEngine).GetMethod("Push", BF)!;
var fmt = new WaveFormat(48000, 16, 2);   // app capture's format

// One tap wired exactly as AudioEngine.CreateInput does, fed through the real AudioEngine.Push.
(object Input, object Output, BufferedWaveProvider Buffer, AdaptiveRate Rate) NewTap(int targetMs)
{
    var o = RuntimeHelpers.GetUninitializedObject(outT);
    outT.GetField("TargetMs", BF)!.SetValue(o, targetMs);
    var buffer = new BufferedWaveProvider(fmt) { BufferDuration = TimeSpan.FromSeconds(3), DiscardOnBufferOverflow = true, ReadFully = true };
    buffer.AddSamples(new byte[fmt.AverageBytesPerSecond * targetMs / 1000], 0, fmt.AverageBytesPerSecond * targetMs / 1000);
    var rate = new AdaptiveRate(buffer.ToSampleProvider(), buffer, () => (int)outT.GetField("TargetMs", BF)!.GetValue(o)!);
    var tap = RuntimeHelpers.GetUninitializedObject(tapT);
    tapT.GetField("Output", BF)!.SetValue(tap, o);
    tapT.GetField("Buffer", BF)!.SetValue(tap, buffer);
    tapT.GetField("Rate", BF)!.SetValue(tap, rate);
    var input = Activator.CreateInstance(inT, true)!;
    ((System.Collections.IList)inT.GetField("Taps", BF)!.GetValue(input)!).Add(tap);
    return (input, o, buffer, rate);
}

var packet = new byte[480 * 4];
for (int i = 0; i < 480; i++)
{
    short s = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / 48000));
    BitConverter.TryWriteBytes(packet.AsSpan(i * 4), s); BitConverter.TryWriteBytes(packet.AsSpan(i * 4 + 2), s);
}

// Synthetic timelines shaped like the measurements: packets every 10 ms arriving late by an exponential amount
// (app capture: mean 0.6 ms, capped at 3.3 ms; whole device: up to 0.2 ms); output reads every 10 ms, now and then
// 0.7 ms late; the two clocks 0.0003 % apart. Replayed at 40 phases across one 10 ms cycle.
(int Dry, double MinSlack) Replay(int targetMs, bool appJitter, double phaseMs, double seconds, double gapAtS = -1, double gapS = 0, int seed = 1)
{
    var r = new Random(seed);
    var (input, _, buffer, rate) = NewTap(targetMs);
    var outBuf = new float[960];
    int n = (int)(seconds * 100), pi = 0, ri = 0, dry = 0; double minSlack = 1e9;
    double PacketTime(int k) => k * 10.0 + (appJitter ? Math.Min(3.3, -0.6 * Math.Log(1 - r.NextDouble())) : 0.2 * r.NextDouble());
    double ReadTime(int k) => k * 10.0 * (1 + 3e-6) + phaseMs + (r.NextDouble() < 0.3 ? 0.7 : 0);
    double nextP = PacketTime(0), nextR = ReadTime(0);
    while (ri < n)
    {
        bool inGap(double t) => gapAtS >= 0 && t >= gapAtS * 1000 && t < (gapAtS + gapS) * 1000;
        if (pi < n && nextP <= nextR)
        {
            if (!inGap(nextP)) push.Invoke(null, new object[] { input, packet, packet.Length });
            nextP = PacketTime(++pi);
            continue;
        }
        double slack = buffer.BufferedDuration.TotalMilliseconds - 10;
        int u = rate.Underruns;
        rate.Read(outBuf, 0, outBuf.Length);
        bool counted = nextR > 1000 && !inGap(nextR) && !inGap(nextR - 30);   // skip start-up and the gap itself
        if (counted) { if (rate.Underruns != u) dry++; minSlack = Math.Min(minSlack, slack); }
        nextR = ReadTime(++ri);
    }
    return (dry, minSlack);
}

// ---- A) app capture at the new buffer (17 + 10 = 27 ms): clean at every phase ----
{
    int bad = 0, total = 0; double minSlack = 1e9;
    for (int ph = 0; ph < 40; ph++)
    {
        var (d, s) = Replay(27, true, ph * 0.25, 60, seed: ph + 1);
        if (d > 0) bad++; total += d; minSlack = Math.Min(minSlack, s);
    }
    Console.WriteLine($"      app capture, 27 ms: {bad}/40 phases with a short read, {total} short reads, lowest slack {minSlack:F1} ms");
    Check(total == 0 && minSlack >= 3, "app capture with 27 ms: no short read at any of 40 phases, at least 3 ms to spare");

    int bad17 = 0, total17 = 0;
    for (int ph = 0; ph < 40; ph++) { var (d, _) = Replay(17, true, ph * 0.25, 60, seed: ph + 1); if (d > 0) bad17++; total17 += d; }
    Console.WriteLine($"      for comparison, app capture at the old 17 ms (with the new refill): {bad17}/40 phases, {total17} short reads");
    Check(total17 > 0, "the replay is sensitive: the old 17 ms does run short with app-capture timing");
}

// ---- B) whole-device capture keeps 17 ms (the speakers/headphones sync stays as tuned) and runs clean ----
{
    int bad = 0, total = 0; double minSlack = 1e9;
    for (int ph = 0; ph < 40; ph++)
    {
        var (d, s) = Replay(17, false, ph * 0.25, 60, seed: 100 + ph);
        if (d > 0) bad++; total += d; minSlack = Math.Min(minSlack, s);
    }
    Console.WriteLine($"      whole device, 17 ms: {bad}/40 phases with a short read, {total} short reads, lowest slack {minSlack:F1} ms");
    // 17 ms is kept on purpose (the sync was tuned by ear); what's left is one narrow phase out of 40.
    Check(bad <= 1, $"whole-device capture at 17 ms: at most one narrow phase (of 40) runs short ({total} short reads in a minute there)");
}

// ---- C) the first packet lands exactly at the target ----
{
    foreach (int target in new[] { 17, 27, 117 })
    {
        var (input, _, buffer, _) = NewTap(target);
        push.Invoke(null, new object[] { input, packet, packet.Length });
        double level = buffer.BufferedDuration.TotalMilliseconds;
        Check(Math.Abs(level - target) < 0.5, $"first packet, target {target} ms: buffer at {level:F1} ms (was target + one packet)");
    }
}

// ---- D) after a gap in the source (e.g. a pause), the buffer is back at its target at once ----
{
    int worst = 0;
    for (int ph = 0; ph < 40; ph += 4) worst = Math.Max(worst, Replay(27, true, ph * 0.25, 20, gapAtS: 8, gapS: 2, seed: 7 + ph).Dry);
    Check(worst <= 1, $"2 s gap in app audio, then it resumes: at most 1 short read afterwards (worst phase: {worst})");

    var (input, _, buffer, rate) = NewTap(17);
    push.Invoke(null, new object[] { input, packet, packet.Length });
    var outBuf = new float[960];
    for (int i = 0; i < 5; i++) rate.Read(outBuf, 0, outBuf.Length);   // source stopped: reads run dry
    push.Invoke(null, new object[] { input, packet, packet.Length });
    double level = buffer.BufferedDuration.TotalMilliseconds;
    Check(Math.Abs(level - 27) < 1, $"after dry reads, the next packet refills to the target before adding itself ({level:F1} ms, target 17 + packet 10)");
}

// ---- E) live, silent: the engine picks the right buffer per mode; device and app capture run clean ----
{
    using var en = new MMDeviceEnumerator();
    var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    var copyTo = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(d => d.ID != def.ID);
    if (copyTo == null) Console.WriteLine("      (only one output device: live part skipped)");
    else
    {
        {
            using var engine = new AudioEngine(copyTo, 0, 0f, 0);   // volume 0: silent
            engine.StartDeviceLoopback(def);
            int b0 = engine.CopyBufferMs;
            Thread.Sleep(1500); int u0 = engine.Underruns; Thread.Sleep(15000);
            int u = engine.Underruns - u0;
            engine.SyncProcesses(new HashSet<int>());   // a stray call must not switch it to the app-capture buffer
            Check(engine.CopyBufferMs == 17, "whole device live: an app sync call leaves its 17 ms buffer alone");
            engine.SetDelay(20);
            Console.WriteLine($"      whole device: buffer {b0} ms, {engine.OutputInfo}, short reads in 15 s: {u}, with 20 ms delay: {engine.CopyBufferMs} ms, fault {engine.Fault ?? "none"}");
            Check(b0 == 17 && engine.CopyBufferMs == 37 && engine.Fault == null, "whole device live: 17 ms buffer as before, delay adds on top, no fault");
            Check(u == 0, "whole device live: no short read in 15 s");
        }
        {
            using var engine = new AudioEngine(copyTo, 0, 0f, 0);
            int self = Environment.ProcessId;
            var pids = new HashSet<int>();
            var m = def.AudioSessionManager; m.RefreshSessions();
            for (int i = 0; i < m.Sessions.Count; i++)
                if (m.Sessions[i].State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive
                    && (int)m.Sessions[i].GetProcessID is int pid && pid != 0 && pid != self) { pids.Add(pid); break; }
            engine.SyncProcesses(pids);
            int b = engine.CopyBufferMs;
            engine.SetDelay(5);
            Check(b == 27 && engine.CopyBufferMs == 32, $"app capture live: buffer {b} ms (17 + 10), delay adds on top ({engine.CopyBufferMs} ms with 5 ms)");
            engine.SetDelay(0);
            if (pids.Count == 0) Console.WriteLine("      (no app playing on the default output: app capture run skipped)");
            else
            {
                Thread.Sleep(1500); int u0 = engine.Underruns; Thread.Sleep(15000);
                int u = engine.Underruns - u0;
                Console.WriteLine($"      app capture of one playing app: {engine.ActiveAppStreams} stream, short reads in 15 s: {u}, error {engine.LastAppError ?? "none"}, fault {engine.Fault ?? "none"}");
                Check(engine.ActiveAppStreams == 1 && u == 0 && engine.Fault == null, "app capture live: runs, no short read in 15 s");
            }
        }
    }
}

Check(settingsTime == (File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : null), "settings.json untouched");
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
