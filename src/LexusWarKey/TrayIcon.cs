using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace LexusWarKey;

/// <summary>Puts the app in the notification area so it can keep remapping while staying out
/// of the taskbar. Closing the window hides it here rather than quitting.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;

    public TrayIcon(Window window, Action onExit)
    {
        _window = window;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Нээх", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Гарах", null, (_, _) => onExit());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Garena.mn WarKey",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
                return new Icon(stream);
        }
        catch
        {
            // fall through to the process icon
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application;
        }
        catch
        {
            // ignored
        }
        return SystemIcons.Application;
    }

    public void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.ShowInTaskbar = true;
        _window.Activate();
    }

    /// <summary>Hide the window and leave only the tray icon behind.</summary>
    public void HideWindow()
    {
        _window.Hide();
        _window.ShowInTaskbar = false;
    }

    public void ShowFirstHideHint()
    {
        try
        {
            _icon.BalloonTipTitle = "Garena.mn WarKey";
            _icon.BalloonTipText = "Апп энд ажилласаар байна. Нээхийн тулд 2 удаа дар.";
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
            // balloons can be disabled by policy
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
