namespace LexusWarKey.Core;

public enum RemapAction
{
    PassThrough,
    SendKey,
    SendChat,
}

public sealed record RemapDecision(
    RemapAction Action,
    int SendVk = 0,
    IReadOnlyList<string>? ChatLines = null)
{
    public static readonly RemapDecision PassThrough = new(RemapAction.PassThrough);
}

/// <summary>Pure decision logic: one physical key becomes one Warcraft skill letter.
/// QuickChat is the only non-remap action and it fires from two explicit user-configured slots.</summary>
public sealed class RemapEngine
{
    private readonly Func<WarKeyProfile> _profile;
    private readonly Func<bool> _gameFocused;
    private readonly Func<bool> _activated;
    private readonly Func<long> _nowMs;

    public RemapEngine(Func<WarKeyProfile> profile, Func<bool> gameFocused, Func<bool>? activated = null,
                       Func<long>? nowMs = null)
    {
        _profile = profile;
        _gameFocused = gameFocused;
        // activated=false бол апп эрхгүй/платформгүй → бүх remap идэвхгүй (pass-through)
        _activated = activated ?? (() => true);
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Set while the app itself is typing QuickChat, so its injected keys are not remapped.</summary>
    public bool SuspendedForTyping { get; set; }

    /// <summary>True while Warcraft's own chat line is open. The game exposes no state for this,
    /// so the app infers it from the player's physical Enter/Escape presses.</summary>
    public bool ChatOpen { get; private set; }

    public TimeSpan ChatOpenFor =>
        ChatOpen ? TimeSpan.FromMilliseconds(_nowMs() - _chatOpenedMs) : TimeSpan.Zero;

    private long _chatOpenedMs;
    private readonly HashSet<int> _observedHeld = new();

    public event Action<bool>? ChatOpenChanged;

    public void ObserveKey(int vk, bool isKeyDown)
    {
        if (!isKeyDown)
        {
            _observedHeld.Remove(vk);
            return;
        }

        if (!_gameFocused())
        {
            SetChatOpen(false);
            return;
        }

        if (!_observedHeld.Add(vk))
            return;
        if (SuspendedForTyping)
            return;

        if (vk == VirtualKeys.Enter)
            SetChatOpen(!ChatOpen);
        else if (vk == VirtualKeys.Escape)
            SetChatOpen(false);
    }

    public void ResetChatState() => SetChatOpen(false);

    private void SetChatOpen(bool value)
    {
        if (ChatOpen == value)
            return;

        ChatOpen = value;
        if (value)
            _chatOpenedMs = _nowMs();
        ChatOpenChanged?.Invoke(value);
    }

    public RemapDecision Decide(int vk, bool isKeyDown, bool ctrlHeld, bool altHeld)
    {
        var profile = _profile();
        if (!_activated())
            return RemapDecision.PassThrough;
        if (!profile.Enabled || SuspendedForTyping)
            return RemapDecision.PassThrough;
        if (!_gameFocused())
            return RemapDecision.PassThrough;
        if (vk is VirtualKeys.Enter or VirtualKeys.Escape)
            return RemapDecision.PassThrough;
        if (ChatOpen)
            return RemapDecision.PassThrough;

        // QuickChat slots each hold one or more messages. Modifiers are ignored so Ctrl+F6
        // remains the app's own in-game editor shortcut and Warcraft modifier actions survive.
        if (isKeyDown && !ctrlHeld && !altHeld)
        {
            var macro = profile.ChatMacros.FirstOrDefault(m => m.IsUsable && m.HotkeyVk == vk);
            if (macro is not null)
                return new RemapDecision(RemapAction.SendChat, ChatLines: macro.UsableMessages.ToArray());
        }

        // Inventory is a static key->key map (item slot keys don't change between matches).
        var item = profile.Inventory.FirstOrDefault(m => m.IsUsable && m.FromVk == vk);
        if (item is not null)
            return new RemapDecision(RemapAction.SendKey, item.ToVk);

        // Skills are NOT remapped here: the app writes each skill's chosen hotkey letter straight into
        // the game's memory, so the player presses that letter and Warcraft casts natively - nothing
        // to translate at cast time.
        return RemapDecision.PassThrough;
    }

    public static IReadOnlyList<string> FindDeadBindings(WarKeyProfile profile)
    {
        var problems = new List<string>();

        if (!profile.Enabled)
            problems.Add("App is disabled; no key remaps will run.");

        // Skills are position-based (the letter is resolved live) and every inventory slot's game key
        // is pre-filled to its numpad hotkey, so neither has a "missing target" to warn about.

        return problems;
    }

    public static IReadOnlyList<int> FindConflicts(WarKeyProfile profile)
    {
        var sources = profile.Skills.Concat(profile.Inventory)
            .Where(m => m.ClaimsKey).Select(m => m.FromVk)
            .Concat(profile.ChatMacros.Where(m => m.IsUsable).Select(m => m.HotkeyVk));

        return sources.GroupBy(vk => vk).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    }
}
