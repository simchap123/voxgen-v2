using System;
using System.Collections.Generic;
using System.Text;

namespace VoxGen.Desktop.Hotkeys;

/// <summary>
/// Modifier-key bitmask. Mirrors the Win32 MOD_* constants used by
/// <c>RegisterHotKey</c>, but is layered on a managed enum so callers
/// don't have to import P/Invoke just to talk about modifiers.
///
/// PRD §8.3 — hotkey is user-configurable; the parser tolerates the
/// common ways a user might type modifiers (Ctrl/Control, Cmd/Win/Windows).
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Shift = 1 << 2,
    Win = 1 << 3,
}

/// <summary>
/// Strongly-typed parsed hotkey. Round-trips with <see cref="ToString"/> via <see cref="Parse"/>.
///
/// Modifier-only hotkeys (e.g. <c>"RightAlt"</c>) set <see cref="VirtualKeyCode"/> = 0 and
/// reflect the bound modifier in <see cref="Modifiers"/>. The presence/absence of a main key
/// tells <c>Win32HotkeyService</c> which Win32 path to use:
///   - main key present  => <c>RegisterHotKey</c> + message-only window receiving WM_HOTKEY
///   - main key absent   => <c>SetWindowsHookEx WH_KEYBOARD_LL</c> (RegisterHotKey can't bind a bare modifier)
/// </summary>
/// <param name="Modifiers">Bitmask of modifier keys that must be held.</param>
/// <param name="VirtualKeyCode">Win32 VK code of the main key, or 0 for modifier-only bindings.</param>
/// <param name="DisplayName">Canonical display form, e.g. "Ctrl+Shift+Space" or "RightAlt".</param>
public sealed record HotkeyDefinition(
    HotkeyModifiers Modifiers,
    uint VirtualKeyCode,
    string DisplayName)
{
    /// <summary>True if there is no main key — bound to a bare modifier (e.g. RightAlt).</summary>
    public bool IsModifierOnly => VirtualKeyCode == 0;

    /// <summary>
    /// Round-trippable canonical form. Modifiers are emitted in a fixed order
    /// (Ctrl, Shift, Alt, Win) so two definitions that mean the same thing
    /// serialize identically.
    /// </summary>
    public override string ToString() => DisplayName;

    /// <summary>
    /// Parses strings like <c>"RightAlt"</c>, <c>"Ctrl+Shift+Space"</c>, <c>"F13"</c>, <c>"Win+Alt+V"</c>.
    /// Lenient on whitespace and case. Accepts <c>Ctrl</c>/<c>Control</c> and <c>Cmd</c>/<c>Win</c>/<c>Windows</c> as aliases.
    /// </summary>
    /// <exception cref="FormatException">If the string is empty, contains an unknown token, or has more than one main key.</exception>
    public static HotkeyDefinition Parse(string value)
    {
        if (value is null) throw new FormatException("Hotkey value is null.");
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new FormatException("Hotkey value is empty.");

        var rawTokens = trimmed.Split('+');
        var modifiers = HotkeyModifiers.None;
        uint mainKey = 0;
        string? mainKeyDisplay = null;

        foreach (var raw in rawTokens)
        {
            var token = raw.Trim();
            if (token.Length == 0)
                throw new FormatException($"Empty segment in hotkey '{value}'.");

            // Modifier?
            if (TryParseModifier(token, out var mod))
            {
                if ((modifiers & mod) != 0)
                    throw new FormatException($"Modifier '{token}' specified twice in hotkey '{value}'.");
                modifiers |= mod;
                continue;
            }

            // Modifier-only token? (e.g. "RightAlt", "LeftShift" — modifier keys with a side qualifier.)
            // Only valid when it's the ONLY token in the string. "Ctrl+RightAlt" is nonsense.
            if (TryParseSidedModifier(token, out var sided, out var sidedDisplay))
            {
                if (rawTokens.Length != 1)
                    throw new FormatException($"Sided modifier '{token}' must be the only key in the hotkey (got '{value}').");
                return new HotkeyDefinition(sided, VirtualKeyCode: 0, DisplayName: sidedDisplay);
            }

            // Main key.
            if (mainKey != 0)
                throw new FormatException($"More than one main key in hotkey '{value}' (saw '{mainKeyDisplay}' and '{token}').");

            if (!TryParseMainKey(token, out mainKey, out mainKeyDisplay))
                throw new FormatException($"Unknown key '{token}' in hotkey '{value}'.");
        }

        if (mainKey == 0 && modifiers == HotkeyModifiers.None)
            throw new FormatException($"Hotkey '{value}' has neither a modifier nor a main key.");

        // Plain "Ctrl"/"Alt"/"Shift"/"Win" with no main key is also a modifier-only binding,
        // but we resolve it to a sided variant for clarity (left side, by convention).
        if (mainKey == 0)
        {
            return new HotkeyDefinition(modifiers, 0, BuildModifierOnlyDisplay(modifiers));
        }

        var display = BuildDisplay(modifiers, mainKeyDisplay!);
        return new HotkeyDefinition(modifiers, mainKey, display);
    }

    public static bool TryParse(string value, out HotkeyDefinition? definition)
    {
        try
        {
            definition = Parse(value);
            return true;
        }
        catch (FormatException)
        {
            definition = null;
            return false;
        }
    }

    // ----- helpers -----

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                modifier = HotkeyModifiers.Control; return true;
            case "alt":
                modifier = HotkeyModifiers.Alt; return true;
            case "shift":
                modifier = HotkeyModifiers.Shift; return true;
            case "win":
            case "windows":
            case "cmd":
            case "meta":
                modifier = HotkeyModifiers.Win; return true;
            default:
                modifier = HotkeyModifiers.None;
                return false;
        }
    }

    private static bool TryParseSidedModifier(string token, out HotkeyModifiers modifier, out string display)
    {
        switch (token.ToLowerInvariant())
        {
            case "leftalt":   modifier = HotkeyModifiers.Alt;     display = "LeftAlt";   return true;
            case "rightalt":  modifier = HotkeyModifiers.Alt;     display = "RightAlt";  return true;
            case "leftctrl":
            case "leftcontrol":
                modifier = HotkeyModifiers.Control; display = "LeftCtrl";  return true;
            case "rightctrl":
            case "rightcontrol":
                modifier = HotkeyModifiers.Control; display = "RightCtrl"; return true;
            case "leftshift": modifier = HotkeyModifiers.Shift;   display = "LeftShift"; return true;
            case "rightshift":modifier = HotkeyModifiers.Shift;   display = "RightShift";return true;
            case "leftwin":
            case "leftwindows":
                modifier = HotkeyModifiers.Win;     display = "LeftWin";   return true;
            case "rightwin":
            case "rightwindows":
                modifier = HotkeyModifiers.Win;     display = "RightWin";  return true;
            default:
                modifier = HotkeyModifiers.None;
                display = "";
                return false;
        }
    }

    private static bool TryParseMainKey(string token, out uint vk, out string display)
    {
        // Single ASCII letter or digit — VK codes for those map directly to their uppercase ASCII value.
        if (token.Length == 1)
        {
            char c = token[0];
            if (c >= 'a' && c <= 'z') c = (char)(c - 32);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                vk = c;
                display = c.ToString();
                return true;
            }
        }

        // Function keys F1..F24 — Win32 VK_F1 = 0x70.
        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f'))
        {
            if (int.TryParse(token.AsSpan(1), out var n) && n >= 1 && n <= 24)
            {
                vk = (uint)(0x70 + n - 1);
                display = "F" + n;
                return true;
            }
        }

        // Named keys.
        if (NamedKeys.TryGetValue(token.ToLowerInvariant(), out var pair))
        {
            vk = pair.vk;
            display = pair.display;
            return true;
        }

        vk = 0;
        display = "";
        return false;
    }

    /// <summary>
    /// Subset of Win32 VK_* constants we accept as main keys.
    /// Keep this list small and focused — every entry has to round-trip through ToString().
    /// </summary>
    private static readonly Dictionary<string, (uint vk, string display)> NamedKeys = new(StringComparer.Ordinal)
    {
        ["space"]      = (0x20, "Space"),
        ["enter"]      = (0x0D, "Enter"),
        ["return"]     = (0x0D, "Enter"),
        ["tab"]        = (0x09, "Tab"),
        ["escape"]     = (0x1B, "Escape"),
        ["esc"]        = (0x1B, "Escape"),
        ["backspace"]  = (0x08, "Backspace"),
        ["delete"]     = (0x2E, "Delete"),
        ["insert"]     = (0x2D, "Insert"),
        ["home"]       = (0x24, "Home"),
        ["end"]        = (0x23, "End"),
        ["pageup"]     = (0x21, "PageUp"),
        ["pagedown"]   = (0x22, "PageDown"),
        ["up"]         = (0x26, "Up"),
        ["down"]       = (0x28, "Down"),
        ["left"]       = (0x25, "Left"),
        ["right"]      = (0x27, "Right"),
        ["capslock"]   = (0x14, "CapsLock"),
        ["printscreen"]= (0x2C, "PrintScreen"),
        ["pause"]      = (0x13, "Pause"),
        ["scrolllock"] = (0x91, "ScrollLock"),
    };

    private static string BuildDisplay(HotkeyModifiers mods, string mainKey)
    {
        var sb = new StringBuilder();
        AppendModifiers(sb, mods);
        if (sb.Length > 0) sb.Append('+');
        sb.Append(mainKey);
        return sb.ToString();
    }

    private static string BuildModifierOnlyDisplay(HotkeyModifiers mods)
    {
        // Single-modifier bindings use the bare modifier name (canonical "Ctrl"/"Alt"/"Shift"/"Win").
        // Multi-modifier modifier-only bindings (uncommon) are rendered with '+' joins.
        var sb = new StringBuilder();
        AppendModifiers(sb, mods);
        return sb.ToString();
    }

    private static void AppendModifiers(StringBuilder sb, HotkeyModifiers mods)
    {
        // Fixed canonical order so equivalents serialize identically.
        if ((mods & HotkeyModifiers.Control) != 0) Append(sb, "Ctrl");
        if ((mods & HotkeyModifiers.Shift)   != 0) Append(sb, "Shift");
        if ((mods & HotkeyModifiers.Alt)     != 0) Append(sb, "Alt");
        if ((mods & HotkeyModifiers.Win)     != 0) Append(sb, "Win");

        static void Append(StringBuilder sb, string token)
        {
            if (sb.Length > 0) sb.Append('+');
            sb.Append(token);
        }
    }
}
