using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LexusWarKey.Core;
using LexusWarKey.Views;
using LexusWarKey.Windows;

namespace LexusWarKey.ViewModels;

public sealed record CaptureRequest(Action<int> Assign, Action Cancel);

public sealed partial class KeyMapRow : ObservableObject
{
    private readonly KeyMap _model;
    private readonly Action _onChanged;
    private readonly Action<KeyMapRow, bool> _beginCapture;

    public KeyMapRow(string label, KeyMap model, Action onChanged, Action<KeyMapRow, bool> beginCapture)
    {
        Label = label;
        _model = model;
        _onChanged = onChanged;
        _beginCapture = beginCapture;
    }

    public KeyMap Model => _model;
    public string Label { get; }

    [ObservableProperty] private bool _isCapturingFrom;
    [ObservableProperty] private bool _isCapturingTo;

    public string FromDisplay => IsCapturingFrom ? "..." : _model.FromVk == 0 ? "+" : VirtualKeys.NameOf(_model.FromVk);
    public string ToDisplay => IsCapturingTo ? "..." : _model.ToVk == 0 ? "-" : VirtualKeys.NameOf(_model.ToVk);

    /// <summary>True once the player's key is set — the tile lights up.</summary>
    public bool IsSet => _model.FromVk != 0;

    partial void OnIsCapturingFromChanged(bool value) { OnPropertyChanged(nameof(FromDisplay)); OnPropertyChanged(nameof(IsSet)); }
    partial void OnIsCapturingToChanged(bool value) => OnPropertyChanged(nameof(ToDisplay));

    [RelayCommand] private void CaptureFrom() => _beginCapture(this, true);
    [RelayCommand] private void CaptureTo() => _beginCapture(this, false);

    public void NotifyModelChanged()
    {
        OnPropertyChanged(nameof(FromDisplay));
        OnPropertyChanged(nameof(ToDisplay));
        OnPropertyChanged(nameof(IsSet));
    }

    public void SetKey(bool isFrom, int vk)
    {
        if (isFrom)
        {
            _model.FromVk = vk;
            if (vk == 0)
                _model.ToVk = 0;
            _model.Enabled = vk != 0;
        }
        else
        {
            _model.ToVk = vk;
            _model.Enabled = _model.FromVk != 0;
        }

        NotifyModelChanged();
        _onChanged();
    }
}

/// <summary>One row of the flat QuickChat table: a trigger key and one message. The same key may
/// appear on several rows — pressing it sends each of those messages in order.</summary>
public sealed partial class QuickChatEntryRow : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action<QuickChatEntryRow> _remove;
    private readonly Action<QuickChatEntryRow> _beginCapture;

    public QuickChatEntryRow(int hotkeyVk, string message, Action onChanged,
                             Action<QuickChatEntryRow> remove, Action<QuickChatEntryRow> beginCapture)
    {
        _hotkeyVk = hotkeyVk;
        _message = message;
        _onChanged = onChanged;
        _remove = remove;
        _beginCapture = beginCapture;
    }

    [ObservableProperty] private int _hotkeyVk;
    [ObservableProperty] private string _message;
    [ObservableProperty] private bool _isCapturing;

    public string HotkeyDisplay => IsCapturing ? "..." : HotkeyVk == 0 ? "-" : VirtualKeys.NameOf(HotkeyVk);

    partial void OnHotkeyVkChanged(int value) => OnPropertyChanged(nameof(HotkeyDisplay));
    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(HotkeyDisplay));
    partial void OnMessageChanged(string value) => _onChanged();

    public void SetHotkey(int vk)
    {
        HotkeyVk = vk;
        _onChanged();
    }

    [RelayCommand] private void Capture() => _beginCapture(this);
    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One row of the live skills list: a skill detected on the command card, its default
/// letter, and the letter the player assigned to it (click to set).</summary>
public sealed partial class DetectedSkillRow : ObservableObject
{
    private readonly Action<DetectedSkillRow> _beginCapture;

    public DetectedSkillRow(string id, string name, char defaultLetter, string assigned,
                            Action<DetectedSkillRow> beginCapture)
    {
        Id = id;
        Name = name;
        Default = defaultLetter.ToString();
        _assigned = assigned;
        _beginCapture = beginCapture;
    }

    public string Id { get; }
    public string Name { get; }
    public string Default { get; }

    [ObservableProperty] private string _assigned;
    [ObservableProperty] private string _currentLetter = "";
    [ObservableProperty] private bool _isCapturing;

    public string AssignedDisplay => IsCapturing ? "..." : string.IsNullOrEmpty(Assigned) ? "-" : Assigned;

    /// <summary>The letter is applied by the background writer on its next pass, so between the
    /// assignment and the game actually showing it, the row sits in "applying". Once the card reads
    /// back the assigned letter it flips to "applied". No assignment at all is idle.</summary>
    public bool IsApplying => !IsCapturing && !string.IsNullOrEmpty(Assigned) &&
                              !string.Equals(CurrentLetter, Assigned, StringComparison.OrdinalIgnoreCase);

    public bool IsApplied => !string.IsNullOrEmpty(Assigned) &&
                             string.Equals(CurrentLetter, Assigned, StringComparison.OrdinalIgnoreCase);

    partial void OnAssignedChanged(string value) => NotifyStatus();
    partial void OnCurrentLetterChanged(string value) => NotifyStatus();
    partial void OnIsCapturingChanged(bool value) => NotifyStatus();

    private void NotifyStatus()
    {
        OnPropertyChanged(nameof(AssignedDisplay));
        OnPropertyChanged(nameof(IsApplying));
        OnPropertyChanged(nameof(IsApplied));
    }

    [RelayCommand] private void Assign() => _beginCapture(this);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly GameWindowWatcher _watcher;
    private readonly RemapEngine _engine;
    private readonly KeyboardHookService _hook;
    private readonly WarKeyProfile _profile;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private readonly OverlayConfigSession _overlaySession;

    private static readonly TimeSpan StuckChatLine = TimeSpan.FromSeconds(20);

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private bool _statusIsLive;
    [ObservableProperty] private bool _hasStatus;
    // Апп түгжигдсэн эсэх: тэмцээний түүхгүй + платформ идэвхгүй → бүх функц зогсоно.
    [ObservableProperty] private bool _locked;
    [ObservableProperty] private string _problemText = "";
    [ObservableProperty] private bool _hasProblems;
    [ObservableProperty] private bool _isCapturing;

    // While a rebind is open, let the mouse hook capture wheel/side-button triggers too.
    // (Only ever fires after construction, so _hook is set.)
    partial void OnIsCapturingChanged(bool value) => _hook.CaptureMode = value;

    private CaptureRequest? _capture;
    private OverlayWindow? _overlay;

    private readonly AbilityData? _abilities;
    private readonly LiveHotkeyReader? _liveReader;
    private readonly SkillHotkeyWriter? _skillWriter;
    private readonly System.Windows.Threading.DispatcherTimer? _autoTimer;
    private int _autoPassRunning;
    private bool _war3NeedsAdmin;
    private readonly bool _isElevated = IsElevated();

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The live list of skills detected in the current match (empty until a hero is up).</summary>
    public ObservableCollection<DetectedSkillRow> DetectedSkills { get; } = new();
    public ObservableCollection<KeyMapRow> InventoryRows { get; }
    /// <summary>The flat QuickChat table (key + message per row). A key repeated across rows sends all
    /// of its messages in order.</summary>
    public ObservableCollection<QuickChatEntryRow> ChatEntries { get; } = new();

    // The "add" bar at the top of the QuickChat tab.
    [ObservableProperty] private int _newHotkeyVk;
    [ObservableProperty] private string _newMessage = "";
    private bool _newHotkeyCapturing;

    public string NewHotkeyDisplay => _newHotkeyCapturing ? "..." : NewHotkeyVk == 0 ? "Товч" : VirtualKeys.NameOf(NewHotkeyVk);

    partial void OnNewHotkeyVkChanged(int value) => OnPropertyChanged(nameof(NewHotkeyDisplay));

    [ObservableProperty] private bool _hasDetectedSkills;
    [ObservableProperty] private string _skillsHint = "Start a match and select your hero to see your skills.";

    /// <summary>The signed-in Discord name, shown in the header.</summary>
    [ObservableProperty] private string _accountName = "";

    public bool HasAccount => !string.IsNullOrEmpty(AccountName);

    partial void OnAccountNameChanged(string value) => OnPropertyChanged(nameof(HasAccount));

    // ---- Auto-update -----------------------------------------------------------------------------
    private readonly UpdateService _updater = new();
    private UpdateInfo? _pendingUpdate;

    /// <summary>Short text next to the update button (download progress, "ready", "up to date").</summary>
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>A verified new exe is downloaded and waiting; the button now restarts into it.</summary>
    [ObservableProperty] private bool _updateReady;

    [ObservableProperty] private bool _updateBusy;

    public string UpdateGlyph => UpdateReady ? "⬇" : "↻";   // ⬇ ready to install, ↻ check

    partial void OnUpdateReadyChanged(bool value) => OnPropertyChanged(nameof(UpdateGlyph));

    private System.Windows.Threading.DispatcherTimer? _heartbeatTimer;

    /// <summary>Raised when the session must be re-established: the stored token expired (the heartbeat
    /// came back unauthorized) or the player pressed log out. The window handles it by showing login.</summary>
    public event Action? ReloginRequested;

    /// <summary>Raised when the server reports this account is banned from WarKey.</summary>
    public event Action? BannedDetected;

    public string VersionText
    {
        get
        {
            var v = typeof(MainViewModel).Assembly.GetName().Version;
            return v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public MainViewModel()
    {
        _store = new ProfileStore(log: DiagnosticLog.Write);
        _profile = _store.Load();
        _profile.NormaliseSlots();

        // Skill-ийн үсэг тоглолт бүрд шинээр тааруулагддаг байх ёстой (эзний шийдвэр
        // 2026-08-30): өмнөх session-оос үлдсэн үсэг дараагийн тоглолтод дамжвал
        // тоглогчийг төөрөгдүүлдэг тул апп асахад цэвэрлэнэ.
        if (_profile.SkillLetters.Count > 0)
        {
            DiagnosticLog.Write($"startup: өмнөх session-ий {_profile.SkillLetters.Count} skill үсэг цэвэрлэв");
            _profile.SkillLetters.Clear();
            _store.Save(_profile);
        }

        DiagnosticLog.Write($"startup; skill binds={_profile.Skills.Count(m => m.ClaimsKey)}, warning={_store.LoadWarning ?? "none"}");

        _watcher = new GameWindowWatcher();
        // activated: түгжигдсэн (эрхгүй + платформгүй) үед бүх remap идэвхгүй
        _engine = new RemapEngine(() => _profile, _watcher.IsGameFocused, () => !Locked);
        _engine.ChatOpenChanged += open =>
            DiagnosticLog.Write(open
                ? "chat line opened; remapping suspended"
                : "chat line closed; remapping live");

        // The ability table is embedded; if it somehow fails to load, the mid-match fix feature is
        // disabled but the rest of the app runs. Key remapping never depends on it.
        try
        {
            _abilities = AbilityData.LoadEmbedded();
            _liveReader = new LiveHotkeyReader(_abilities);
            _skillWriter = new SkillHotkeyWriter(_liveReader);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"ability table load failed; skill writer disabled: {ex.GetType().Name}: {ex.Message}");
        }

        _hook = new KeyboardHookService(_engine);
        _hook.OverlayToggleRequested += () => Application.Current?.Dispatcher.BeginInvoke(new Action(ToggleOverlay));
        _hook.ConfigKeyPressed += vk => Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnOverlayKey(vk)));
        // Mouse triggers (wheel / side buttons) reach a main-window rebind through here.
        _hook.CaptureInput += vk => Application.Current?.Dispatcher.BeginInvoke(new Action(() => HandleCaptureKey(vk)));

        _overlaySession = new OverlayConfigSession(
            _profile,
            () => DetectedSkills.Select(r => new OverlaySkill(r.Id, r.Name, r.Default.Length > 0 ? r.Default[0] : '?')).ToList(),
            Save,
            Family);

        _isEnabled = _profile.Enabled;
        InventoryRows = new ObservableCollection<KeyMapRow>(
            _profile.Inventory.Select((m, i) => new KeyMapRow($"{i + 1}", m, Save, BeginKeyCapture)));
        RebuildChatEntries();

        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"keyboard hook refused: {ex.GetType().Name}: {ex.Message}");
        }

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        // The writer polls the game a few seconds apart while it is in front; each pass reads memory
        // on a worker thread, so this interval is the responsiveness, not a cost the game pays.
        if (_skillWriter is not null)
        {
            _autoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _autoTimer.Tick += (_, _) => OnAutoTick();
            _autoTimer.Start();
        }

        AccountName = App.Auth?.Username ?? "";
        _heartbeatTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _heartbeatTimer.Tick += (_, _) => _ = SendHeartbeatAsync();
        _heartbeatTimer.Start();
        _ = SendHeartbeatAsync();   // mark online right away

        _ = CheckForUpdatesOnStartupAsync();   // auto-download a newer version in the background

        Save();
        RefreshStatus();
        RefreshConflicts();
    }

    /// <summary>Reports the app as online to the server. An unauthorized reply means the token is no
    /// longer valid, so we ask the window to re-login; a network error is ignored (offline grace).</summary>
    private async Task SendHeartbeatAsync()
    {
        if (App.Auth is not { } auth)
            return;
        var version = VersionText.TrimStart('v');
        var result = await auth.HeartbeatAsync(version);
        if (result == Core.HeartbeatResult.Banned)
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => BannedDetected?.Invoke()));
        else if (result == Core.HeartbeatResult.Unauthorized)
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ReloginRequested?.Invoke()));
        else if (result == Core.HeartbeatResult.Ok)
            Application.Current?.Dispatcher.BeginInvoke(new Action(UpdateGate));   // шинэ entitled-ийг тусгах
    }

    /// <summary>Re-reads the signed-in name after a login/re-login.</summary>
    public void RefreshAccount() => AccountName = App.Auth?.Username ?? "";

    [RelayCommand]
    private void Logout()
    {
        App.Auth?.ClearToken();
        ReloginRequested?.Invoke();
    }

    /// <summary>On startup, quietly check GitHub and pre-download a newer version so the button only has
    /// to restart. Any failure (offline, dev build, no release) just leaves the button in its resting
    /// "check" state.</summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        UpdateService.CleanupOldExe();
        var info = await _updater.CheckAsync();
        if (info is null)
            return;

        _pendingUpdate = info;
        UpdateBusy = true;
        UpdateStatus = $"v{info.Version} татаж байна…";
        var ok = await _updater.DownloadAsync(info);
        UpdateBusy = false;
        if (!ok)
        {
            UpdateStatus = "";
            return;
        }

        // Downloaded and verified — install and restart on our own, no click needed. A short pause
        // lets the "restarting" note show; the manual button stays as a fallback if the swap fails.
        UpdateReady = true;
        UpdateStatus = $"v{info.Version} — дахин эхэлж байна…";
        await Task.Delay(1500);
        if (_updater.ApplyAndRestart())
            Application.Current?.Shutdown();
        else
            UpdateStatus = $"v{info.Version} бэлэн — дахин эхлүүлэх";
    }

    /// <summary>The update button. If a new version is already downloaded, install and restart into it;
    /// otherwise check now and download whatever is newer.</summary>
    [RelayCommand]
    private async Task Update()
    {
        if (UpdateBusy)
            return;

        if (UpdateReady)
        {
            if (_updater.ApplyAndRestart())
                Application.Current?.Shutdown();
            else
                UpdateStatus = "Шинэчлэлт амжилтгүй — гараар татна уу";
            return;
        }

        UpdateBusy = true;
        UpdateStatus = "Шалгаж байна…";
        var info = await _updater.CheckAsync();
        if (info is null)
        {
            UpdateBusy = false;
            UpdateStatus = "Хамгийн сүүлийн хувилбар";
            return;
        }

        _pendingUpdate = info;
        UpdateStatus = $"v{info.Version} татаж байна…";
        var ok = await _updater.DownloadAsync(info);
        UpdateBusy = false;
        if (ok)
        {
            UpdateReady = true;
            UpdateStatus = $"v{info.Version} бэлэн — дахин эхлүүлэх";
        }
        else
        {
            UpdateStatus = "Татаж чадсангүй";
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        _profile.Enabled = value;
        _engine.ResetChatState();
        Save();
        RefreshStatus();
    }

    private void BeginKeyCapture(KeyMapRow row, bool isFrom)
    {
        CancelCapture();
        if (isFrom) row.IsCapturingFrom = true; else row.IsCapturingTo = true;
        IsCapturing = true;

        _capture = new CaptureRequest(
            vk =>
            {
                // Inventory trigger keys go through the profile so the key is freed from any skill or
                // other slot first - one physical key is only ever bound in one place.
                var invIndex = isFrom && vk != 0 ? _profile.Inventory.IndexOf(row.Model) : -1;
                if (invIndex >= 0)
                {
                    _profile.SetInventoryKey(invIndex, vk);
                    RefreshRowsFromProfile();
                    RefreshDetectedFromProfile();
                    row.NotifyModelChanged();
                    Save();
                }
                else
                {
                    row.SetKey(isFrom, vk);
                }
                ClearFlags();
            },
            ClearFlags);

        void ClearFlags()
        {
            row.IsCapturingFrom = false;
            row.IsCapturingTo = false;
            IsCapturing = false;
            _capture = null;
            RefreshConflicts();
        }
    }

    // Re-capture the hotkey of an existing row.
    private void BeginChatCapture(QuickChatEntryRow row)
    {
        CancelCapture();
        row.IsCapturing = true;
        IsCapturing = true;

        _capture = new CaptureRequest(
            vk => { row.SetHotkey(vk); Clear(); },
            Clear);

        void Clear()
        {
            row.IsCapturing = false;
            IsCapturing = false;
            _capture = null;
            RefreshConflicts();
        }
    }

    // Capture the hotkey for the "add" bar.
    [RelayCommand]
    private void CaptureNewHotkey()
    {
        CancelCapture();
        _newHotkeyCapturing = true;
        IsCapturing = true;
        OnPropertyChanged(nameof(NewHotkeyDisplay));

        _capture = new CaptureRequest(
            vk => { NewHotkeyVk = vk; Clear(); },
            Clear);

        void Clear()
        {
            _newHotkeyCapturing = false;
            IsCapturing = false;
            _capture = null;
            OnPropertyChanged(nameof(NewHotkeyDisplay));
        }
    }

    private void RebuildChatEntries()
    {
        ChatEntries.Clear();
        foreach (var macro in _profile.ChatMacros)
            foreach (var message in macro.Messages)
                ChatEntries.Add(new QuickChatEntryRow(macro.HotkeyVk, message, SyncChatEntries, RemoveChatEntry, BeginChatCapture));
    }

    /// <summary>Rebuilds the grouped ChatMacros from the flat table: rows sharing a key become one
    /// macro whose messages are those rows' messages, in table order.</summary>
    private void SyncChatEntries()
    {
        _profile.ChatMacros = ChatEntries
            .Where(e => e.HotkeyVk != 0)
            .GroupBy(e => e.HotkeyVk)
            .Select(g => new ChatMacro { HotkeyVk = g.Key, Messages = g.Select(e => e.Message).ToList() })
            .ToList();
        Save();
    }

    [RelayCommand]
    private void AddChatEntry()
    {
        if (NewHotkeyVk == 0 || string.IsNullOrWhiteSpace(NewMessage))
            return;
        ChatEntries.Add(new QuickChatEntryRow(NewHotkeyVk, NewMessage.Trim(), SyncChatEntries, RemoveChatEntry, BeginChatCapture));
        NewMessage = "";   // keep the key so several messages can be added to it in a row
        SyncChatEntries();
    }

    private void RemoveChatEntry(QuickChatEntryRow row)
    {
        ChatEntries.Remove(row);
        SyncChatEntries();
    }

    public bool HandleCaptureKey(int vk)
    {
        if (_capture is null)
            return false;
        if (vk == VirtualKeys.Escape)
        {
            _capture.Cancel();
            return true;
        }

        // Enter belongs to Warcraft's chat line and RemapEngine never touches it. As a trigger
        // it would store a cell that looks bound and never fires; as a target it would type
        // into the game's chat instead of casting. Swallow it and keep waiting for a real key.
        if (vk == VirtualKeys.Enter)
            return true;

        _capture.Assign(vk == VirtualKeys.Back ? 0 : vk);
        return true;
    }

    public void CancelCapture() => _capture?.Cancel();

    [RelayCommand]
    private void ToggleOverlay()
    {
        if (_overlay is { IsVisible: true })
        {
            CloseOverlay();
            return;
        }

        UpdateGate();
        if (Locked)
        {
            // Тэмцээний түүхгүй + платформгүй үед overlay-г нээхгүй, шалтгааныг тайлбарлана.
            System.Windows.MessageBox.Show(
                LockNoticeText,
                "Garena.mn WarKey",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        _overlaySession.Reset();
        EnsureOverlay();
        _hook.ConfigMode = true;
        RenderOverlay();
        _overlay!.PlaceAt(_profile.OverlayLeft, _profile.OverlayTop);
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        _overlay = new OverlayWindow();
        _overlay.SlotClicked += index =>
        {
            _overlaySession.SelectTarget(index);
            RenderOverlay();
        };
        _overlay.Moved += (left, top) =>
        {
            _profile.OverlayLeft = left;
            _profile.OverlayTop = top;
            Save();
        };
    }

    private void CloseOverlay()
    {
        _hook.ConfigMode = false;
        _overlaySession.Reset();
        _overlay?.Hide();

        // Deliberately NOT resetting the chat tracker. The overlay swallows every key while it
        // is open, so it cannot have changed whether Warcraft's chat line is open — but the
        // player may well have opened chat, then pressed Ctrl+F6 to fix a binding mid-fight.
        // Declaring "chat closed" there inverts the tracker: the player goes back to typing
        // into a prompt that is still open, and every letter both mangles the message and
        // casts an ability. Leaving the tracker alone keeps whatever was true before.
    }

    private void OnOverlayKey(int vk)
    {
        if (!_overlaySession.HandleKey(vk))
        {
            CloseOverlay();
            return;
        }
        RenderOverlay();
    }

    private void RenderOverlay()
    {
        var (skills, inventory) = BuildOverlayRows();
        _overlay?.ShowSlots(skills, inventory, _overlaySession.Prompt);
    }

    private (List<OverlaySlot> Skills, List<OverlaySlot> Inventory) BuildOverlayRows()
    {
        var selected = _overlaySession.Step == OverlayStep.ChoosingTarget ? -1 : _overlaySession.SelectedIndex;

        var detected = _overlaySession.Skills;
        var skills = new List<OverlaySlot>();
        for (var i = 0; i < detected.Count; i++)
        {
            var assigned = _overlaySession.AssignedOf(detected[i].Id);
            // Reuse the same apply status the main window shows, matched by ability id.
            var live = DetectedSkills.FirstOrDefault(r => r.Id == detected[i].Id);
            skills.Add(Row(i, detected[i].Name, detected[i].Default.ToString(),
                           string.IsNullOrEmpty(assigned) ? "-" : assigned, selected,
                           live?.IsApplying ?? false, live?.IsApplied ?? false));
        }

        // Inventory keeps its own 2x3 shape below the skills; the numbers continue after the skills so
        // Ctrl+F6 can still pick a slot by number. Inventory is instant, so a set slot is simply ready.
        var inventory = new List<OverlaySlot>();
        for (var j = 0; j < _profile.Inventory.Count; j++)
        {
            var index = detected.Count + j;
            var inv = _profile.Inventory[j];
            inventory.Add(Row(index, $"Item {j + 1}", VirtualKeys.NameOf(inv.ToVk),
                             inv.FromVk == 0 ? "-" : VirtualKeys.NameOf(inv.FromVk), selected,
                             applying: false, applied: inv.FromVk != 0));
        }
        return (skills, inventory);

        static OverlaySlot Row(int index, string name, string def, string assigned, int selected,
                               bool applying, bool applied) =>
            new(index, (index + 1).ToString(), name, def, assigned,
                index == selected ? "#40FFFFFF" : "#66000000",
                index == selected ? "#FFFFFFFF" : "#4DFFFFFF",
                applying, applied);
    }

    // ---- Automatic in-game skill layer ----------------------------------------------------------
    //
    // While Warcraft is in front, a background pass reads the command card, writes each detected
    // skill's assigned letter onto it, and updates the live skills list. A small in-game overlay
    // shows each skill and the letter it now uses.

    private string _lastTickLog = "";

    // ---- Тоглолт дуусахад skill үсэгнүүдийг автоматаар цэвэрлэх төлөв ----
    // Өмнөх тоглолтын тохиргоо дараагийнхад дамжихгүй (эзний шийдвэр 2026-08-30).
    private bool _wasInMatch;
    private DateTime? _noSkillsSince;
    private DateTime? _idleSince;
    private static readonly TimeSpan MatchOverAfter = TimeSpan.FromMinutes(4);

    /// <summary>Тоглолт дууссан гэж үзээд бүх skill үсэг цэвэрлэнэ. 4 минутын босго нь
    /// DotA-ийн хамгийн урт амиа хүлээх хугацаанаас урт тул тоглолтын ДУНД цэвэрлэгдэхгүй.</summary>
    private void ResetSkillAssignments(string reason)
    {
        _wasInMatch = false;
        _noSkillsSince = null;
        _idleSince = null;
        if (_profile.SkillLetters.Count == 0)
            return;
        DiagnosticLog.Write($"тоглолт дууссан ({reason}) — {_profile.SkillLetters.Count} skill үсэг автоматаар цэвэрлэгдэв");
        _profile.SkillLetters.Clear();
        foreach (var row in DetectedSkills)
            row.Assigned = "";
        Save();
    }

    private void TickLog(string message)
    {
        if (message == _lastTickLog)
            return;
        _lastTickLog = message;
        DiagnosticLog.Write(message);
    }

    /// <summary>The player's assigned letter per ability id, from the profile.</summary>
    private IReadOnlyDictionary<string, char> DesiredById()
    {
        var map = new Dictionary<string, char>(StringComparer.Ordinal);
        foreach (var (id, letter) in _profile.SkillLetters)
            if (!string.IsNullOrEmpty(letter) && char.ToUpperInvariant(letter[0]) is >= 'A' and <= 'Z')
                map[id] = char.ToUpperInvariant(letter[0]);
        return map;
    }

    private void OnAutoTick()
    {
        if (_skillWriter is null || !IsEnabled || Locked)
        {
            TickLog($"idle: writer={( _skillWriter is not null)}, enabled={IsEnabled}, locked={Locked}, elevated={_isElevated}");
            return;
        }
        if (!_watcher.IsGameFocused())
        {
            if (_wasInMatch)
            {
                if (!GameWindowWatcher.IsGameProcessRunning())
                    ResetSkillAssignments("Warcraft хаагдсан");
                else
                {
                    _idleSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - _idleSince > MatchOverAfter)
                        ResetSkillAssignments("тоглоом удаан идэвхгүй");
                }
            }
            TickLog("waiting for Warcraft to be the focused window");
            return;
        }
        _idleSince = null;
        TickLog("Warcraft focused, polling command card");
        if (Interlocked.CompareExchange(ref _autoPassRunning, 1, 0) != 0)
            return;   // a pass is still going; don't stack them

        var desired = DesiredById();
        System.Threading.Tasks.Task.Run(() =>
        {
            SkillHotkeyWriter.PassResult result;
            try { result = _skillWriter.RunPass(desired); }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"pass threw: {ex.GetType().Name}: {ex.Message}");
                Interlocked.Exchange(ref _autoPassRunning, 0);
                return;
            }
            Interlocked.Exchange(ref _autoPassRunning, 0);
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _war3NeedsAdmin = result.Status == LiveHotkeyReader.Status.NeedAdmin;
                // Тоглолтын амьдралын мөчлөг: карт харагдвал тоглолтод байна;
                // war3 хаагдвал шууд, карт удаан алга болвол (менюнд гарсан) reset.
                if (result.Status == LiveHotkeyReader.Status.Ok && result.Skills.Count > 0)
                {
                    _wasInMatch = true;
                    _noSkillsSince = null;
                }
                else if (_wasInMatch && result.Status == LiveHotkeyReader.Status.NotRunning)
                {
                    ResetSkillAssignments("Warcraft хаагдсан");
                }
                else if (_wasInMatch && result.Status == LiveHotkeyReader.Status.NoSkills)
                {
                    _noSkillsSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - _noSkillsSince > MatchOverAfter)
                        ResetSkillAssignments("тоглолт дууссан");
                }
                SyncDetectedSkills(result.Skills);
                RefreshConflicts();
            }));
        });
    }

    /// <summary>Reconciles the live skills list with what the last pass detected: updates letters in
    /// place, adds newly-seen skills, drops ones that left the card. Editing is preserved because rows
    /// are matched by id rather than rebuilt.</summary>
    private void SyncDetectedSkills(IReadOnlyList<DetectedSkill> detected)
    {
        var byId = detected.ToDictionary(d => d.Ability.Id, StringComparer.Ordinal);

        for (var i = DetectedSkills.Count - 1; i >= 0; i--)
            if (!byId.ContainsKey(DetectedSkills[i].Id))
                DetectedSkills.RemoveAt(i);

        foreach (var d in detected)
        {
            var row = DetectedSkills.FirstOrDefault(r => r.Id == d.Ability.Id);
            var assigned = _profile.SkillLetters.GetValueOrDefault(d.Ability.Id, "");
            if (row is null)
            {
                row = new DetectedSkillRow(d.Ability.Id, d.Ability.Name, d.Ability.Letter, assigned, BeginSkillCapture);
                DetectedSkills.Add(row);
            }
            else if (row.Assigned != assigned && !row.IsCapturing)
            {
                row.Assigned = assigned;
            }
            row.CurrentLetter = d.CurrentLetter.ToString();
        }

        HasDetectedSkills = DetectedSkills.Count > 0;

        // Keep the in-game Ctrl+F6 list in sync with what was just detected.
        if (_overlay is { IsVisible: true })
            RenderOverlay();
    }

    /// <summary>All ability ids that are the same skill as <paramref name="id"/> (multi-icon states),
    /// so a hotkey letter set on the visible icon applies to every state.</summary>
    private IReadOnlyList<string> Family(string id) => _abilities?.IdsWithSameName(id) ?? new[] { id };

    private void BeginSkillCapture(DetectedSkillRow row)
    {
        CancelCapture();
        row.IsCapturing = true;
        IsCapturing = true;

        _capture = new CaptureRequest(
            vk =>
            {
                if (vk == 0)   // Backspace: clear the assignment
                {
                    _profile.ClearSkillLetterFamily(Family(row.Id));
                }
                else if (vk is >= 'A' and <= 'Z')
                {
                    _profile.SetSkillLetterFamily(Family(row.Id), ((char)vk).ToString());
                }
                Clear();
            },
            Clear);

        void Clear()
        {
            row.IsCapturing = false;
            IsCapturing = false;
            _capture = null;
            // Reflect the change across the list: a letter removed from another skill, or an inventory
            // key freed because this skill just claimed that physical key.
            RefreshDetectedFromProfile();
            RefreshRowsFromProfile();
            Save();
            RefreshConflicts();
        }
    }

    private void RefreshRowsFromProfile()
    {
        foreach (var row in InventoryRows)
            row.NotifyModelChanged();
    }

    /// <summary>Pulls each detected skill's assigned letter back from the profile, so a letter freed
    /// by binding it elsewhere (another skill or an inventory slot) disappears from its old row.</summary>
    private void RefreshDetectedFromProfile()
    {
        foreach (var row in DetectedSkills)
            if (!row.IsCapturing)
                row.Assigned = _profile.SkillLetters.GetValueOrDefault(row.Id, "");
    }

    private void Save()
    {
        _profile.NormaliseSlots();
        try { _store.Save(_profile); } catch { }
        RefreshConflicts();
    }

    private void RefreshConflicts()
    {
        var problems = new List<string>();

        if (_store.LoadWarning is { } warning)
            problems.Add(warning);
        if (!_hook.IsInstalled)
            problems.Add("Keyboard hook is not running. Close and reopen the app.");
        if (_war3NeedsAdmin && !_isElevated)
            problems.Add("Warcraft is running elevated but this app is not - skills can't be read. Run LexusWarKey as administrator (right-click -> Run as administrator), matching how the game is launched.");

        problems.AddRange(RemapEngine.FindDeadBindings(_profile));

        var conflicts = RemapEngine.FindConflicts(_profile);
        if (conflicts.Count > 0)
            problems.Add("One trigger key is assigned in more than one place: " + string.Join(", ", conflicts.Select(VirtualKeys.NameOf)));

        // Two current skills sharing a letter would collide - Warcraft only answers the first.
        var dupLetters = DetectedSkills
            .Where(r => !string.IsNullOrEmpty(r.Assigned))
            .GroupBy(r => r.Assigned).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupLetters.Count > 0)
            problems.Add($"Two skills share the same key ({string.Join(", ", dupLetters)}). Give each a different letter.");

        HasProblems = problems.Count > 0;
        ProblemText = string.Join("\n", problems.Select(p => "- " + p));
    }

    // Апп ашиглах эрхийг шинэчилнэ: эзэн/тэмцээний түүхтэй бол хаана ч; үгүй бол зөвхөн
    // Garena.mn платформ энэ PC дээр ажиллаж байх үед. Түгжигдвэл remap/skill-бичих/overlay зогсоно.
    private void UpdateGate()
    {
        bool entitled = App.Auth?.Entitled ?? false;
        bool platform = Core.PlatformPresence.IsActive();
        Locked = !(entitled || platform);
    }

    public const string LockNoticeText =
        "Энэхүү тоглоом дотроо шууд үсэг тааруулдаг WarKey нь зөвхөн Garena.mn платформыг ашиглаж байх үед ажиллана. GarenaSystem-д тэмцээний түүхтэй бол GameRanger/LAN дээр ч чөлөөтэй ашиглана.";

    private void RefreshStatus()
    {
        UpdateGate();
        var focused = _watcher.IsGameFocused();
        StatusIsLive = IsEnabled && !Locked && focused && !_engine.ChatOpen;

        if (!focused)
            _engine.ResetChatState();
        else if (_engine.ChatOpenFor > StuckChatLine)
        {
            DiagnosticLog.Write($"chat line forced shut after {_engine.ChatOpenFor.TotalSeconds:F0}s");
            _engine.ResetChatState();
        }

        if (focused && IsEnabled)
        {
            try
            {
                _hook.ReArmIfSilent(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"keyboard hook re-arm failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (Locked)
        {
            StatusText = "🔒 Платформ шаардлагатай";
            StatusDetail = LockNoticeText;
        }
        else if (!IsEnabled)
        {
            StatusText = "Disabled";
            StatusDetail = "";
        }
        else if (focused && _engine.ChatOpen)
        {
            StatusText = "Chat open";
            StatusDetail = "Remapping is paused";
        }
        else if (!_hook.IsInstalled)
        {
            StatusText = "Keyboard hook failed";
            StatusDetail = "Close and reopen the app";
        }
        else
        {
            StatusText = "";
            StatusDetail = "";
        }

        HasStatus = StatusText.Length > 0;
        RefreshConflicts();
    }

    public void Shutdown()
    {
        _statusTimer.Stop();
        _autoTimer?.Stop();
        _heartbeatTimer?.Stop();
        _overlay?.Close();
        _hook.Dispose();
        Save();
    }
}
