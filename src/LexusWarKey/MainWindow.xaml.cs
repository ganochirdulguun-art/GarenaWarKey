using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LexusWarKey.ViewModels;

namespace LexusWarKey;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private bool _reallyExiting;
    private bool _hintShown;
    private bool _relogging;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => _tray = new TrayIcon(this, ExitForReal);
        if (Vm is { } vm)
        {
            vm.ReloginRequested += OnReloginRequested;
            vm.BannedDetected += OnBannedDetected;
        }
    }

    private bool _banned;

    // The server reported this account is banned from WarKey: tell the user and exit.
    private void OnBannedDetected()
    {
        Dispatcher.Invoke(() =>
        {
            if (_banned)
                return;
            _banned = true;
            if (Core.EmbeddedHost.IsEmbedded) { Application.Current.Shutdown(5); return; }
            App.Auth.ClearToken();
            MessageBox.Show(
                "Таны Discord акаунтыг Garena.mn WarKey ашиглахыг хориглосон байна.\nАдминтай холбогдоно уу.",
                "Garena.mn WarKey", MessageBoxButton.OK, MessageBoxImage.Warning);
            ExitForReal();
        });
    }

    // The token expired or the player logged out: clear it and require Discord sign-in again.
    // Cancelling the login closes the app, matching the required-login gate at startup.
    private void OnReloginRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (_relogging)
                return;
            _relogging = true;
            if (Core.EmbeddedHost.IsEmbedded) { Application.Current.Shutdown(6); return; }   // платформ шинэ токентой дахин асаана
            App.Auth.ClearToken();

            var login = new Views.LoginWindow(App.Auth);
            var ok = login.ShowDialog();
            _relogging = false;

            if (ok == true)
                Vm?.RefreshAccount();
            else
                ExitForReal();
        });
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // The OS title bar is hidden (WindowStyle=None), so the header doubles as the drag area.
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // Matches the old close button: hide to the tray (the app keeps remapping).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vm.HandleCaptureKey(vk))
            e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExiting)
        {
            e.Cancel = true;
            _tray?.HideWindow();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray?.ShowFirstHideHint();
            }
            return;
        }

        base.OnClosing(e);
    }

    private void ExitForReal()
    {
        _reallyExiting = true;
        Close();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        Vm?.Shutdown();
        _tray?.Dispose();
        base.OnClosed(e);
    }
}
