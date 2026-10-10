namespace FastFlow.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A global keyboard shortcut: any combination of Ctrl / Alt / Shift / Win plus one key,
/// e.g. "Ctrl+Alt+1" or "Ctrl+Alt+Shift+F5". Stored in workflows.json as that text.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, Keys Key)
{
    private static readonly KeysConverter Converter = new();

    public bool IsEmpty => Key == Keys.None;

    /// <summary>Function keys may be used on their own; every other key needs at least one modifier.</summary>
    public bool IsValid => !IsEmpty && !IsModifierKey(Key)
                           && (Modifiers != HotkeyModifiers.None || Key is >= Keys.F1 and <= Keys.F24);

    public static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.Menu or Keys.LMenu or Keys.RMenu
        or Keys.LWin or Keys.RWin;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (!IsEmpty)
            parts.Add(Converter.ConvertToInvariantString(Key) ?? Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = HotkeyModifiers.None;
        var key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Ctrl; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key != Keys.None)
                        return false; // only one non-modifier key is allowed
                    try
                    {
                        key = Converter.ConvertFromInvariantString(raw) is Keys k ? k & Keys.KeyCode : Keys.None;
                    }
                    catch
                    {
                        return false;
                    }
                    break;
            }
        }

        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }
}
