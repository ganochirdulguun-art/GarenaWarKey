using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LexusWarKey.Core;

public enum HeartbeatResult { Ok, Unauthorized, Banned, Offline }

/// <summary>Discord sign-in for the desktop app. Reuses the platform server's OAuth "poll" flow: the
/// app opens the browser to the Discord authorize URL with a random session id, then polls the server
/// until the session yields a JWT. The token is cached (DPAPI-encrypted, per Windows user) so the
/// player logs in once, and a periodic heartbeat marks them online in the admin dashboard.</summary>
public sealed class AuthService
{
    public const string ServerUrl = "https://garenamn-production.up.railway.app";   // Garena.mn платформын сервер (Discord нэвтрэлт + /warkey/heartbeat)

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _tokenPath;
    private readonly string _entitledPath;

    public string? Token { get; private set; }
    public string? Username { get; private set; }
    public string? DiscordId { get; private set; }

    /// <summary>Сервер сүүлд мэдэгдсэн эрх: GarenaSystem тэмцээний түүхтэй (эсвэл эзэн/админ)
    /// хэрэглэгч бол WarKey-г хаана ч ашиглана. Түүхгүй бол зөвхөн платформтой хослуулна.</summary>
    public bool Entitled { get; private set; }

    public bool IsLoggedIn => !string.IsNullOrEmpty(Token);

    public AuthService(string? rootOverride = null)
    {
        var root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LexusWarKey");
        try { Directory.CreateDirectory(root); } catch { }
        _tokenPath = Path.Combine(root, "auth.dat");
        _entitledPath = Path.Combine(root, "entitled.dat");
    }

    public void LoadToken()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return;
            var enc = File.ReadAllBytes(_tokenPath);
            var raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            SetToken(Encoding.UTF8.GetString(raw));
        }
        catch { Token = null; }
        // Сүүлд серверээс баталсан эрхийг кэшээс сэргээнэ — офлайн (PC төвд LAN) үед ч
        // тэмцээний түүхтэй хэрэглэгч ажиллуулах боломжтой байхын тулд.
        try { Entitled = File.Exists(_entitledPath) && File.ReadAllText(_entitledPath).Trim() == "1"; }
        catch { /* кэш эмзэг биш */ }
    }

    private void CacheEntitled(bool value)
    {
        Entitled = value;
        try { File.WriteAllText(_entitledPath, value ? "1" : "0"); } catch { }
    }

    public void SaveToken(string token)
    {
        SetToken(token);
        try
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tokenPath, enc);
        }
        catch { }
    }

    public void ClearToken()
    {
        Token = null;
        Username = null;
        DiscordId = null;
        Entitled = false;
        try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); } catch { }
        try { if (File.Exists(_entitledPath)) File.Delete(_entitledPath); } catch { }
    }

    private void SetToken(string token)
    {
        Token = token;
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return;
            using var doc = JsonDocument.Parse(DecodeJwtPart(parts[1]));
            if (doc.RootElement.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String)
                Username = u.GetString();
            if (doc.RootElement.TryGetProperty("discord_id", out var d))
                DiscordId = d.ValueKind == JsonValueKind.String ? d.GetString() : d.ToString();
        }
        catch { /* display fields are best-effort; the token itself is still valid */ }
    }

    private static string DecodeJwtPart(string part)
    {
        var s = part.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }

    /// <summary>Opens the browser to Discord OAuth and polls for the resulting token. Returns true once
    /// the token is captured and stored; false on timeout, cancel, or if the browser cannot open.</summary>
    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        var session = Guid.NewGuid().ToString("N");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"{ServerUrl}/auth/discord?state={session}") { UseShellExecute = true });
        }
        catch { return false; }

        // Poll ~3 minutes (the server keeps the pending token for 10).
        for (var i = 0; i < 90 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(2000, ct);
            try
            {
                var resp = await Http.GetAsync($"{ServerUrl}/auth/poll/{session}", ct);
                if (!resp.IsSuccessStatusCode)
                    continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    SaveToken(t.GetString()!);
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* transient; keep polling */ }
        }
        return false;
    }

    /// <summary>Marks the user online and reports the app version. Distinguishes an expired/invalid
    /// token (needs re-login) from a network failure (stay logged in, try again later).</summary>
    public async Task<HeartbeatResult> HeartbeatAsync(string version)
    {
        if (string.IsNullOrEmpty(Token))
            return HeartbeatResult.Unauthorized;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/warkey/heartbeat");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            req.Content = new StringContent(JsonSerializer.Serialize(new { version }), Encoding.UTF8, "application/json");
            var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                // A ban comes back as 403 { "error": "banned" }; anything else 403/401 is a bad token.
                try
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() == "banned")
                        return HeartbeatResult.Banned;
                }
                catch { /* fall through to unauthorized */ }
                return HeartbeatResult.Unauthorized;
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return HeartbeatResult.Unauthorized;
            // Амжилттай хариунаас эрхийг (entitled) уншина — WarKey-ийн gate үүнд тулгуурлана.
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("entitled", out var e))
                        CacheEntitled(e.ValueKind == JsonValueKind.True);
                }
                catch { /* entitled уншилт эмзэг биш; хуучин утга хэвээр */ }
            }
            return HeartbeatResult.Ok;
        }
        catch
        {
            return HeartbeatResult.Offline;
        }
    }
}
