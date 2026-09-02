using System.IO;
using System.Text.Json;

namespace LexusWarKey.Core;

/// <summary>Garena.mn платформ клиент энэ компьютер дээр ажиллаж байгаа эсэхийг илрүүлнэ.
/// Платформ клиент нээлттэй үедээ %LOCALAPPDATA%\Garena.mn\platform-session.json файлд
/// одоогийн цагаа (epoch ms) ~5 сек тутам бичдэг. Энэ файл шинэ (сүүлийн ~20 сек дотор
/// бичигдсэн) бол платформ идэвхтэй гэж үзнэ. Тэмцээний түүхгүй энгийн хэрэглэгч WarKey-г
/// зөвхөн энэ дохио идэвхтэй үед л ашиглаж чадна.</summary>
public static class PlatformPresence
{
    private const long FreshMs = 20_000;

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Garena.mn", "platform-session.json");

    public static bool IsActive()
    {
        try
        {
            if (!File.Exists(SessionPath))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(SessionPath));
            if (!doc.RootElement.TryGetProperty("ts", out var t))
                return false;
            long ts = t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return ts > 0 && (now - ts) <= FreshMs;
        }
        catch
        {
            return false;
        }
    }
}
