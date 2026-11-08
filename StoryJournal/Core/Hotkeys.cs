using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace StoryJournal.Core;

/// <summary>A key and its modifiers ("Ctrl+J"), the key as a Windows virtual-key code (Dalamud's <c>VirtualKey</c>).</summary>
public readonly record struct Hotkey(int Key, bool Ctrl, bool Shift, bool Alt)
{
    public override string ToString() => Hotkeys.Format(this);
}

/// <summary>
/// The keys the player sets for saving a line, taking a photo and a quick note, written as words
/// ("F11", "Ctrl+J", "Ctrl+Shift+F12") and read into a key and its modifiers. Pure.
/// </summary>
public static class Hotkeys
{
    private static readonly Dictionary<string, int> Named = MakeNames();

    private static Dictionary<string, int> MakeNames()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i <= 24; i++) d["F" + i.ToString(CultureInfo.InvariantCulture)] = 0x70 + i - 1;
        for (var c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (var n = 0; n <= 9; n++)
        {
            d[n.ToString(CultureInfo.InvariantCulture)] = 0x30 + n;
            d["Num" + n.ToString(CultureInfo.InvariantCulture)] = 0x60 + n;
        }
        d["Insert"] = 0x2D;
        d["Delete"] = 0x2E;
        d["Home"] = 0x24;
        d["End"] = 0x23;
        d["PageUp"] = 0x21;
        d["PageDown"] = 0x22;
        d["Pause"] = 0x13;
        d["Space"] = 0x20;
        d["Up"] = 0x26;
        d["Down"] = 0x28;
        d["Left"] = 0x25;
        d["Right"] = 0x27;
        return d;
    }

    private static readonly Dictionary<string, string> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ins"] = "Insert", ["Del"] = "Delete", ["PgUp"] = "PageUp", ["PgDn"] = "PageDown", ["PgDown"] = "PageDown",
        ["Numpad0"] = "Num0", ["Numpad1"] = "Num1", ["Numpad2"] = "Num2", ["Numpad3"] = "Num3", ["Numpad4"] = "Num4",
        ["Numpad5"] = "Num5", ["Numpad6"] = "Num6", ["Numpad7"] = "Num7", ["Numpad8"] = "Num8", ["Numpad9"] = "Num9",
    };

    /// <summary>The key a name is ("F11" → 0x7A), or null.</summary>
    public static int? KeyOf(string name)
    {
        var n = name.Trim();
        if (Spellings.TryGetValue(n, out var s)) n = s;
        return Named.TryGetValue(n, out var vk) ? vk : null;
    }

    /// <summary>The name of a key (0x7A → "F11"), or null.</summary>
    public static string? NameOf(int key) => Named.FirstOrDefault(kv => kv.Value == key).Key;

    /// <summary>
    /// Reads "Ctrl+Shift+F12" (any case, "Control" and "Alt" too, with + or spaces between): null when
    /// it's empty ("none": the key is off) or isn't a key with modifiers.
    /// </summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(new[] { '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false;
        int? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    ctrl = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
                case "alt":
                    alt = true;
                    continue;
                case "none":
                case "off":
                    return null;
            }
            if (key != null) return null;
            key = KeyOf(part);
            if (key == null) return null;
        }
        return key is { } k ? new Hotkey(k, ctrl, shift, alt) : null;
    }

    /// <summary>The key as words, modifiers first: "Ctrl+Shift+F12".</summary>
    public static string Format(Hotkey key)
    {
        var sb = new StringBuilder();
        if (key.Ctrl) sb.Append("Ctrl+");
        if (key.Shift) sb.Append("Shift+");
        if (key.Alt) sb.Append("Alt+");
        sb.Append(NameOf(key.Key) ?? "?");
        return sb.ToString();
    }

    /// <summary>A key as set, tidied ("ctrl + j" → "Ctrl+J"); empty for none, null when it isn't a key.</summary>
    public static string? Normalise(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty
        : Parse(text) is { } k ? Format(k) : null;

    /// <summary>
    /// Was the key just pressed? It's down with exactly its modifiers now, and wasn't a moment ago
    /// (<paramref name="wasDown"/>), so holding it fires once.
    /// </summary>
    public static bool Pressed(Hotkey key, bool keyDown, bool ctrl, bool shift, bool alt, bool wasDown) =>
        keyDown && !wasDown && ctrl == key.Ctrl && shift == key.Shift && alt == key.Alt;
}
