using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace AudioMonitor;

/// <summary>Finds VB-Audio's free "VB-CABLE" virtual audio cable and reads its installed driver version.</summary>
static class VbCable
{
    public const string DownloadPage = "https://vb-audio.com/Cable/";

    /// <summary>The playback side ("CABLE Input") the mic mixer writes into, or null if not installed.</summary>
    public static MMDevice? FindCableInput(MMDeviceEnumerator enumerator)
    {
        try
        {
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => IsCable(d.FriendlyName) && d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    /// <summary>
    /// True for VB-CABLE's own devices. "CABLE Output" must never be used as a mic input: the mixer writes
    /// into the same cable, so it would hear itself in an endless, ever-louder feedback loop.
    /// </summary>
    public static bool IsCable(string friendlyName) =>
        friendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)
        || friendlyName.StartsWith("CABLE ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Installed driver version, read from the Windows driver registry (no admin rights needed).</summary>
    public static Version? InstalledVersion()
    {
        const string MediaClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(MediaClass);
            if (classKey == null) return null;
            Version? best = null;
            foreach (var name in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(name, out _)) continue;   // only the numbered driver instances
                try
                {
                    using var key = classKey.OpenSubKey(name);
                    string desc = key?.GetValue("DriverDesc") as string ?? "";
                    if (!desc.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Version.TryParse(key?.GetValue("DriverVersion") as string, out var v) && (best == null || v > best)) best = v;
                }
                catch { /* some driver keys are locked down; skip them */ }
            }
            return best;
        }
        catch { return null; }
    }
}

/// <summary>
/// Optional (opt-in) online check for the latest VB-CABLE version. Only used when the user has explicitly
/// allowed internet access. It makes one HTTPS GET to <see cref="VbCable.DownloadPage"/>, sends nothing
/// about the user, reads the version number from the page's structured product data, and never downloads
/// or installs anything.
/// </summary>
static class UpdateChecker
{
    static readonly HttpClient Http = CreateClient();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "The handler is owned by the process-lifetime HttpClient (disposeHandler: true).")]
    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,               // never follow the request anywhere else
            UseCookies = false,
            UseDefaultCredentials = false,           // never send Windows credentials
            CheckCertificateRevocationList = true,   // reject revoked HTTPS certificates
        };
        try
        {
            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = 2 * 1024 * 1024,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"SonicFunbox/{typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3)}");
            return client;
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    /// <summary>Latest version published on VB-Audio's site, or an error message (never a guess).</summary>
    public static async Task<(Version? Latest, string? Error)> CheckAsync()
    {
        try
        {
            using var response = await Http.GetAsync(VbCable.DownloadPage).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return (null, $"couldn't check (server answered {(int)response.StatusCode})");
            string html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseVersion(html) is { } v ? (v, null) : (null, "couldn't check (version not found on the page)");
        }
        catch (TaskCanceledException) { return (null, "couldn't check (timed out)"); }
        catch (Exception ex) { return (null, "couldn't check (" + ex.GetType().Name + ")"); }
    }

    /// <summary>Reads schema.org "softwareVersion" from the page's JSON-LD block.</summary>
    internal static Version? ParseVersion(string html)
    {
        var m = Regex.Match(html, "\"softwareVersion\"\\s*:\\s*\"(\\d{1,4}(?:\\.\\d{1,6}){1,3})\"", RegexOptions.None, TimeSpan.FromSeconds(1));
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }
}
