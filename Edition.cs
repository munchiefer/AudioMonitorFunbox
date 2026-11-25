namespace AudioMonitor;

/// <summary>
/// Two builds come from the same sources:
///   Sonic Funbox (default): everything, including the Pedalboard tab.
///   Sonic Funbox Lite (built with -p:Edition=Monitor, which defines MONITOR_EDITION): the same without the Pedalboard.
/// Both share the settings file, the single-instance lock and the "Start with Windows" entry, so only one runs
/// at a time and switching between them keeps every choice. Static readonly (not const), so the compiler doesn't
/// warn about code that one of the editions never runs.
/// </summary>
static class Edition
{
#if MONITOR_EDITION
    public static readonly bool HasPedalboard = false;
    public static readonly string Name = "Sonic Funbox Lite";
#else
    public static readonly bool HasPedalboard = true;
    public static readonly string Name = "Sonic Funbox";
#endif
    /// <summary>Shared by both editions: whichever starts second sees it taken and exits. Kept from the
    /// pre-2.1 names on purpose, so an old copy and a new one can never run together.</summary>
    public const string InstanceLockName = "AudioMonitor_SingleInstance_7f3c1e";
}
