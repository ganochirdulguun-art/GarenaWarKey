using LexusWarKey.Core;
using LexusWarKey.Windows;

namespace LexusWarKey.ViewModels;

/// <summary>Платформд шигтгэсэн горимын локал API-д зориулсан гадаад үйлдлүүд (EmbeddedHost).
/// Бүгд UI thread дээр дуудагдана; цонхны товчнуудтай ЯГ ижил дотоод замыг (SetInventoryKey,
/// SetSkillLetterFamily, ChatEntries) ашигладаг тул standalone горимын үйлдэлтэй адил үр дүнтэй.</summary>
public sealed partial class MainViewModel
{
    public object ApiState()
    {
        return new
        {
            ok = true,
            version = VersionText,
            account = AccountName,
            entitled = App.Auth?.Entitled ?? false,
            locked = Locked,
            lockNotice = Locked ? LockNoticeText : "",
            enabled = IsEnabled,
            status = StatusText,
            statusDetail = StatusDetail,
            live = StatusIsLive,
            problems = ProblemText,
            hookInstalled = _hook.IsInstalled,
            elevated = _isElevated,
            war3NeedsAdmin = _war3NeedsAdmin && !_isElevated,   // WC3 админ эрхээр ажиллаж байна — энгийн эрхийн WarKey хүрч чадахгүй
            gameRunning = GameWindowWatcher.IsGameProcessRunning(),
            gameFocused = _watcher.IsGameFocused(),
            overlayOpen = _overlay is { IsVisible: true },
            inventory = _profile.Inventory.Select((m, i) => new
            {
                slot = i, fromVk = m.FromVk, from = m.FromVk == 0 ? "" : VirtualKeys.NameOf(m.FromVk),
                toVk = m.ToVk, to = VirtualKeys.NameOf(m.ToVk), enabled = m.Enabled,
            }).ToList(),
            skills = DetectedSkills.Select(r => new
            {
                id = r.Id, name = r.Name, @default = r.Default, assigned = r.Assigned,
                current = r.CurrentLetter, applying = r.IsApplying, applied = r.IsApplied,
            }).ToList(),
            hasSkills = HasDetectedSkills,
            skillsHint = SkillsHint,
            chat = ChatEntries.Select((e, i) => new
            {
                index = i, vk = e.HotkeyVk, key = e.HotkeyVk == 0 ? "" : VirtualKeys.NameOf(e.HotkeyVk), message = e.Message,
            }).ToList(),
        };
    }

    public void ApiSetInventory(int slot, int vk)
    {
        if (slot < 0 || slot >= _profile.Inventory.Count)
            throw new ArgumentException("slot 0–5 байх ёстой");
        if (vk == VirtualKeys.Enter || vk == VirtualKeys.Escape)
            throw new ArgumentException("Enter/Esc товч ашиглах боломжгүй");
        CancelCapture();
        if (vk == 0)
        {
            _profile.Inventory[slot].FromVk = 0;
            _profile.Inventory[slot].Enabled = false;
        }
        else
        {
            _profile.SetInventoryKey(slot, vk);   // нэг товч зөвхөн нэг газар (skill-ээс ч чөлөөлнө)
        }
        RefreshRowsFromProfile();
        RefreshDetectedFromProfile();
        Save();
        RefreshConflicts();
    }

    public void ApiSetSkill(string id, string letter)
    {
        var row = DetectedSkills.FirstOrDefault(r => r.Id == id)
                  ?? throw new KeyNotFoundException("skill олдсонгүй (тоглоомд hero сонгосны дараа гарна)");
        CancelCapture();
        if (string.IsNullOrWhiteSpace(letter))
        {
            _profile.ClearSkillLetterFamily(Family(row.Id));
        }
        else
        {
            var c = char.ToUpperInvariant(letter.Trim()[0]);
            if (c is < 'A' or > 'Z')
                throw new ArgumentException("A–Z үсэг оруулна уу");
            _profile.SetSkillLetterFamily(Family(row.Id), c.ToString());
        }
        RefreshDetectedFromProfile();
        RefreshRowsFromProfile();
        Save();
        RefreshConflicts();
    }

    public void ApiChatAdd(int vk, string message)
    {
        if (vk == 0 || string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("товч ба мессеж хоёулаа хэрэгтэй");
        if (vk == VirtualKeys.Enter || vk == VirtualKeys.Escape)
            throw new ArgumentException("Enter/Esc товч ашиглах боломжгүй");
        CancelCapture();
        ChatEntries.Add(new QuickChatEntryRow(vk, message.Trim(), SyncChatEntries, RemoveChatEntry, BeginChatCapture));
        SyncChatEntries();
    }

    public void ApiChatRemove(int index)
    {
        if (index < 0 || index >= ChatEntries.Count)
            throw new ArgumentException("index буруу");
        CancelCapture();
        RemoveChatEntry(ChatEntries[index]);
    }

    public void ApiChatSetKey(int index, int vk)
    {
        if (index < 0 || index >= ChatEntries.Count)
            throw new ArgumentException("index буруу");
        if (vk == VirtualKeys.Enter || vk == VirtualKeys.Escape)
            throw new ArgumentException("Enter/Esc товч ашиглах боломжгүй");
        CancelCapture();
        ChatEntries[index].SetHotkey(vk);
    }

    public void ApiChatSetMessage(int index, string message)
    {
        if (index < 0 || index >= ChatEntries.Count)
            throw new ArgumentException("index буруу");
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("мессеж хоосон");
        ChatEntries[index].Message = message.Trim();   // OnMessageChanged → SyncChatEntries
    }

    /// <summary>Overlay-г нээх/хаах (Ctrl+F6-тай адил). Түгжигдсэн бол MessageBox биш алдаа буцаана —
    /// шигтгэсэн горимд цонх байхгүй тул платформын таб шалтгааныг харуулна.</summary>
    public void ApiToggleOverlay()
    {
        if (_overlay is { IsVisible: true })
        {
            ToggleOverlay();
            return;
        }
        UpdateGate();
        if (Locked)
            throw new InvalidOperationException(LockNoticeText);
        ToggleOverlay();
    }
}
