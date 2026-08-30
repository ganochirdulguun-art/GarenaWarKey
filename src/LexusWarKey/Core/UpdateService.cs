using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LexusWarKey.Core;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Sha256);

/// <summary>Self-update against the public GitHub releases. Checks the latest release, downloads the
/// portable exe, verifies its SHA256 (published in the release body, fetched from the API — a separate
/// channel from the CDN download), then swaps it in place of the running exe and restarts.</summary>
public sealed class UpdateService
{
    public const string Repo = "ganochirdulguun-art/GarenaWarKey";
    private const string AssetName = "GarenaWarKey.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("GarenaWarKey-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public Version CurrentVersion { get; } =
        NormaliseVersion(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Latest release, or null if up to date or on any error (offline, rate-limited, …).</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var version = ParseVersion(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
            if (version is null || version <= CurrentVersion)
                return null;

            string? url = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                    if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
            if (string.IsNullOrEmpty(url))
                return null;

            string? sha = null;
            if (root.TryGetProperty("body", out var body))
            {
                var m = Regex.Match(body.GetString() ?? "", "SHA256:\\s*([0-9A-Fa-f]{64})");
                if (m.Success)
                    sha = m.Groups[1].Value;
            }
            return new UpdateInfo(version, url!, sha);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExeDir()
    {
        var exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetDirectoryName(exe);
    }

    private static string DownloadPath(string dir) => Path.Combine(dir, "GarenaWarKey.download.exe");
    private static string OldPath(string dir) => Path.Combine(dir, "GarenaWarKey.old.exe");

    /// <summary>Downloads and verifies the new exe next to the current one, without touching the running
    /// exe yet. Returns false (and leaves nothing behind) on any failure, incl. a hash mismatch.</summary>
    public async Task<bool> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (ExeDir() is not { } dir)
            return false;   // running via `dotnet run`, not a packaged exe — nothing to self-update
        var download = DownloadPath(dir);
        try
        {
            var bytes = await Http.GetByteArrayAsync(info.DownloadUrl, ct);

            if (!string.IsNullOrEmpty(info.Sha256))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;   // tampered or corrupt — refuse
            }

            await File.WriteAllBytesAsync(download, bytes, ct);
            return true;
        }
        catch
        {
            try { if (File.Exists(download)) File.Delete(download); } catch { }
            return false;
        }
    }

    /// <summary>Swaps the downloaded exe in for the running one and launches it. The caller then exits.
    /// Windows allows renaming a running exe, so the swap is: current → .old, download → current.</summary>
    public bool ApplyAndRestart()
    {
        var exe = Environment.ProcessPath;
        if (ExeDir() is not { } dir || string.IsNullOrEmpty(exe) || !File.Exists(DownloadPath(dir)))
            return false;

        var old = OldPath(dir);
        try
        {
            if (File.Exists(old)) { try { File.Delete(old); } catch { } }
            File.Move(exe!, old);
            File.Move(DownloadPath(dir), exe!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe!) { UseShellExecute = true });
            return true;
        }
        catch
        {
            // Roll back a half-done swap so the app still launches.
            try { if (!File.Exists(exe!) && File.Exists(old)) File.Move(old, exe!); } catch { }
            return false;
        }
    }

    /// <summary>Removes the previous exe a successful update left behind. Call once at startup.</summary>
    public static void CleanupOldExe()
    {
        if (ExeDir() is not { } dir)
            return;
        try { if (File.Exists(OldPath(dir))) File.Delete(OldPath(dir)); } catch { }
    }

    private static Version NormaliseVersion(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        return Version.TryParse(tag.TrimStart('v', 'V'), out var v) ? NormaliseVersion(v) : null;
    }
}
