using System.Diagnostics;

namespace LexusWarKey.Windows;

/// <summary>Answers whether Warcraft III is the window currently receiving keyboard input.</summary>
public sealed class GameWindowWatcher
{
    private static readonly string[] GameProcessNames = { "war3", "Warcraft III", "Frozen Throne" };

    /// <summary>Тоглоомын процесс ажиллаж байгаа эсэх — фокусаас үл хамааран.
    /// Тоглолт дууссаныг (war3 хаагдсаныг) илрүүлэхэд ашиглана.</summary>
    public static bool IsGameProcessRunning()
    {
        foreach (var name in GameProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            var any = procs.Length > 0;
            foreach (var p in procs)
                p.Dispose();
            if (any)
                return true;
        }
        return false;
    }

    private readonly object _gate = new();
    private uint _cachedPid;
    private bool _cachedIsGame;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public bool IsGameFocused()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && CachedIsGame(pid);
    }

    private bool CachedIsGame(uint pid)
    {
        lock (_gate)
        {
            if (pid == _cachedPid && (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < 2)
                return _cachedIsGame;

            _cachedPid = pid;
            _cachedAtUtc = DateTime.UtcNow;
            _cachedIsGame = ResolveIsGame(pid);
            return _cachedIsGame;
        }
    }

    private static bool ResolveIsGame(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return GameProcessNames.Any(n => string.Equals(n, process.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
