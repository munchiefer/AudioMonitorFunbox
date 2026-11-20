namespace AudioMonitor;

/// <summary>
/// Two builds come from the same sources:
///   AudioFunbox (default): everything, including the Pedalboard tab.
///   AudioMonitor (built with -p:Edition=Monitor, which defines MONITOR_EDITION): the same without the Pedalboard.
/// Both share the settings file, the single-instance lock and the "Start with Windows" entry, so only one runs
/// at a time and switching between them keeps every choice. Static readonly (not const), so the compiler doesn't
/// warn about code that one of the editions never runs.
/// </summary>
static class Edition
{
#if MONITOR_EDITION
    public static readonly bool HasPedalboard = false;
    public static readonly string Name = "AudioMonitor";
#else
    public static readonly bool HasPedalboard = true;
    public static readonly string Name = "AudioFunbox";
#endif
    /// <summary>Shared by both editions: whichever starts second sees it taken and exits.</summary>
    public const string InstanceLockName = "AudioMonitor_SingleInstance_7f3c1e";
}
