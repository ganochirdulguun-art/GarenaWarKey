using System.Windows;
using LexusWarKey.Core;
using LexusWarKey.Views;

namespace LexusWarKey;

public partial class App : Application
{
    private Mutex? _singleInstance;

    /// <summary>The signed-in Discord session, shared across the app.</summary>
    public static AuthService Auth { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Garena.mn платформоос "--embedded" аргументтай асаасан бол шигтгэсэн горим (цонхгүй, локал API).
        EmbeddedHost.TryInit(e.Args);

        // Two instances would both hook the keyboard and double every remap.
        _singleInstance = new Mutex(initiallyOwned: false, @"Local\LexusWarKey");
        bool acquired;
        try { acquired = _singleInstance.WaitOne(TimeSpan.Zero, false); }
        catch (AbandonedMutexException) { acquired = true; }

        if (!acquired)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            if (EmbeddedHost.IsEmbedded)
            {
                // Standalone WarKey аль хэдийн ажиллаж байна — чимээгүй гарна (код 3), платформ шалтгааныг харуулна.
                Environment.Exit(3);
                return;
            }
            MessageBox.Show("Garena.mn WarKey аль хэдийн ажиллаж байна.", "Garena.mn WarKey",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        // Discord login is required to use the app. A cached token lets the player back in
        // without re-authenticating (and keeps working offline); only a missing token forces
        // the login window. An expired token is caught later by the heartbeat.
        Auth = new AuthService();
        Auth.LoadToken();

        if (EmbeddedHost.IsEmbedded)
        {
            // Платформын нэвтрэлтийг env-ээс авна (диск дээр хадгалахгүй); тусдаа login цонх гарахгүй.
            var tok = EmbeddedHost.SessionToken;
            if (string.IsNullOrEmpty(tok))
            {
                Environment.Exit(4);
                return;
            }
            Auth.UseSessionToken(tok);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Цонхыг үүсгэнэ (VM = hook, skill writer, overlay энд амьдарна) гэхдээ ХЭЗЭЭ Ч харуулахгүй.
            var hidden = new MainWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized };
            MainWindow = hidden;
            if (hidden.DataContext is ViewModels.MainViewModel vm)
                EmbeddedHost.Start(vm);
            else
                Environment.Exit(5);
            return;
        }

        if (!Auth.IsLoggedIn)
        {
            // No main window exists yet, so WPF would treat the login window as the app's MainWindow
            // and, under OnMainWindowClose, shut the whole app down the moment login succeeds and it
            // closes. Hold shutdown until the real window is up, then restore the normal mode.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var login = new LoginWindow(Auth);
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
