namespace AudioMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, Edition.InstanceLockName, out bool firstInstance);
        if (!firstInstance)
        {
            // Either edition may hold the lock: say so, instead of quietly doing nothing.
            MessageBox.Show("Sonic Funbox or Sonic Funbox Lite is already running (only one of them runs at a time).\n\n" +
                            "Use its tray icon, or exit it there first to start this one.",
                            Edition.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Low-latency audio runs in ~3 ms steps: avoid long blocking garbage collections.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        ApplicationConfiguration.Initialize();
        // Never show the raw .NET crash dialog; report briefly and keep the tray app alive.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, Edition.Name,MessageBoxButtons.OK, MessageBoxIcon.Warning);
        using var app = new TrayApp();
        Application.Run(app);
    }
}
