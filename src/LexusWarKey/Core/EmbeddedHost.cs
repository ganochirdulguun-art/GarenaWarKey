using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using LexusWarKey.ViewModels;
using LexusWarKey.Windows;

namespace LexusWarKey.Core;

/// <summary>Garena.mn платформд ШИГТГЭСЭН (embedded) горим. Платформ клиент WarKey-г
/// <c>--embedded</c> аргументтай, env-ээр (GARENA_WARKEY_TOKEN = платформын нэвтрэлт,
/// GARENA_WARKEY_PORT, GARENA_WARKEY_SECRET) далд асаана. Энд 127.0.0.1 дээр жижиг локал HTTP API
/// нээж платформын "WarKey" таб inventory/skill/quickchat тохиргоог уншиж/бичнэ. Гадаад сүлжээнд
/// огт нээгдэхгүй (loopback), хүсэлт бүр нууц толгойтой. Standalone горимд огт ашиглагдахгүй.</summary>
public static class EmbeddedHost
{
    public static bool IsEmbedded { get; private set; }
    public static int Port { get; private set; }

    private static string _secret = "";
    private static TcpListener? _listener;
    private static System.Windows.Threading.DispatcherTimer? _watchdog;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool TryInit(string[] args)
    {
        if (!args.Any(a => string.Equals(a, "--embedded", StringComparison.OrdinalIgnoreCase)))
            return false;
        IsEmbedded = true;
        // Env (энгийн асаалт) эсвэл аргумент (админ эрхээр Start-Process -Verb RunAs — env дамждаггүй)
        string Arg(string name)
        {
            for (var i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }
        var portStr = Arg("--port"); if (string.IsNullOrEmpty(portStr)) portStr = Environment.GetEnvironmentVariable("GARENA_WARKEY_PORT") ?? "";
        Port = int.TryParse(portStr, out var p) && p > 0 ? p : 47831;
        _secret = Arg("--secret"); if (string.IsNullOrEmpty(_secret)) _secret = Environment.GetEnvironmentVariable("GARENA_WARKEY_SECRET") ?? "";
        var tok = Arg("--token"); if (!string.IsNullOrEmpty(tok)) _argToken = tok;
        return true;
    }

    private static string? _argToken;

    /// <summary>Платформын JWT (env эсвэл --token аргумент; диск дээр хадгалахгүй).</summary>
    public static string? SessionToken => _argToken ?? Environment.GetEnvironmentVariable("GARENA_WARKEY_TOKEN");

    public static void Start(MainViewModel vm)
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        var t = new Thread(() => AcceptLoop(vm)) { IsBackground = true, Name = "warkey-local-api" };
        t.Start();

        // Watchdog: платформ 5с тутам presence бичдэг; ~30с бичихгүй (хаагдсан/унасан) бол өөрөө унтарна.
        var stale = 0;
        _watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _watchdog.Tick += (_, _) =>
        {
            if (PlatformPresence.IsActive()) { stale = 0; return; }
            if (++stale >= 3)
            {
                DiagnosticLog.Write("embedded: платформ идэвхгүй → унтарна");
                Application.Current?.Shutdown(0);
            }
        };
        _watchdog.Start();
        DiagnosticLog.Write($"embedded: local API 127.0.0.1:{Port}");
    }

    private static void AcceptLoop(MainViewModel vm)
    {
        while (_listener is { } l)
        {
            TcpClient client;
            try { client = l.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client, vm));
        }
    }

    private static void Handle(TcpClient client, MainViewModel vm)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                var stream = client.GetStream();
                var (method, path, headers, body) = ReadRequest(stream);
                if (method is null)
                {
                    Respond(stream, 400, new { ok = false, error = "bad request" });
                    return;
                }
                if (!SecretOk(headers))
                {
                    Respond(stream, 401, new { ok = false, error = "secret" });
                    return;
                }
                object result;
                try
                {
                    result = Route(vm, method, path, body);
                    Respond(stream, 200, result);
                }
                catch (ArgumentException ex) { Respond(stream, 400, new { ok = false, error = ex.Message }); }
                catch (InvalidOperationException ex) { Respond(stream, 409, new { ok = false, error = ex.Message }); }
                catch (KeyNotFoundException ex) { Respond(stream, 404, new { ok = false, error = ex.Message }); }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"embedded api {method} {path}: {ex.GetType().Name}: {ex.Message}");
                    Respond(stream, 500, new { ok = false, error = ex.Message });
                }
            }
            catch { /* холболтын алдаа — дараагийн хүсэлт */ }
        }
    }

    private static bool SecretOk(Dictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(_secret))
            return false;
        if (!headers.TryGetValue("x-warkey-secret", out var got) || string.IsNullOrEmpty(got))
            return false;
        var a = Encoding.UTF8.GetBytes(got);
        var b = Encoding.UTF8.GetBytes(_secret);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // Хүсэлтийг VM-ийн (UI) thread дээр гүйцэтгэнэ — WPF объектууд зөвхөн тэндээс өөрчлөгддөг.
    private static object OnUi(Func<object> fn)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return fn();
        object? r = null; Exception? err = null;
        d.Invoke(() => { try { r = fn(); } catch (Exception ex) { err = ex; } });
        if (err is not null) throw err;
        return r!;
    }

    private static object Route(MainViewModel vm, string method, string path, JsonElement body)
    {
        static int Int(JsonElement b, string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        static string Str(JsonElement b, string k) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        switch (method, path)
        {
            case ("GET", "/state"):
                return OnUi(vm.ApiState);
            case ("POST", "/inventory"):
                return OnUi(() => { vm.ApiSetInventory(Int(body, "slot"), Int(body, "vk")); return vm.ApiState(); });
            case ("POST", "/skill"):
                return OnUi(() => { vm.ApiSetSkill(Str(body, "id"), Str(body, "letter")); return vm.ApiState(); });
            case ("POST", "/chat/add"):
                return OnUi(() => { vm.ApiChatAdd(Int(body, "vk"), Str(body, "message")); return vm.ApiState(); });
            case ("POST", "/chat/remove"):
                return OnUi(() => { vm.ApiChatRemove(Int(body, "index")); return vm.ApiState(); });
            case ("POST", "/chat/setkey"):
                return OnUi(() => { vm.ApiChatSetKey(Int(body, "index"), Int(body, "vk")); return vm.ApiState(); });
            case ("POST", "/chat/setmessage"):
                return OnUi(() => { vm.ApiChatSetMessage(Int(body, "index"), Str(body, "message")); return vm.ApiState(); });
            case ("POST", "/overlay"):
                return OnUi(() => { vm.ApiToggleOverlay(); return vm.ApiState(); });
            case ("POST", "/shutdown"):
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Application.Current?.Shutdown(0)));
                return new { ok = true };
            default:
                throw new KeyNotFoundException($"{method} {path}");
        }
    }

    private static (string? method, string path, Dictionary<string, string> headers, JsonElement body) ReadRequest(NetworkStream s)
    {
        var buf = new MemoryStream();
        var tmp = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0 && buf.Length < 65536)
        {
            var n = s.Read(tmp, 0, tmp.Length);
            if (n <= 0) break;
            buf.Write(tmp, 0, n);
            headerEnd = IndexOf(buf.GetBuffer(), (int)buf.Length, "\r\n\r\n"u8.ToArray());
        }
        if (headerEnd < 0)
            return (null, "", new(), default);
        var head = Encoding.ASCII.GetString(buf.GetBuffer(), 0, headerEnd);
        var lines = head.Split("\r\n");
        var req = lines[0].Split(' ');
        if (req.Length < 2)
            return (null, "", new(), default);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ln in lines.Skip(1))
        {
            var i = ln.IndexOf(':');
            if (i > 0) headers[ln[..i].Trim()] = ln[(i + 1)..].Trim();
        }
        var len = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var l) ? Math.Min(l, 65536) : 0;
        var bodyStart = headerEnd + 4;
        var have = (int)buf.Length - bodyStart;
        while (have < len)
        {
            var n = s.Read(tmp, 0, Math.Min(tmp.Length, len - have));
            if (n <= 0) break;
            buf.Write(tmp, 0, n);
            have += n;
        }
        JsonElement body = default;
        if (len > 0)
        {
            try { body = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf.GetBuffer(), bodyStart, Math.Min(len, have))).RootElement.Clone(); }
            catch { body = default; }
        }
        var path = req[1];
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];
        return (req[0].ToUpperInvariant(), path, headers, body);
    }

    private static int IndexOf(byte[] hay, int len, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= len; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static void Respond(NetworkStream s, int status, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: null\r\n\r\n");
        s.Write(head, 0, head.Length);
        s.Write(json, 0, json.Length);
        s.Flush();
    }
}
