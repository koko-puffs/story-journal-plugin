using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.UI;
using StoryJournal.Core;

namespace StoryJournal.Game;

/// <summary>
/// The player's keys for saving a line, a photo and a quick note, watched each frame (framework thread):
/// a key fires once when it goes down with exactly its modifiers, and the game doesn't see it then. Not
/// while typing, in the game's chat or a plugin's field. Held down (the keyboard repeating it), it fires
/// once: the key isn't taken as let go until it's been up for a second.
/// </summary>
public sealed class KeyWatch
{
    private readonly Dictionary<string, Hotkey?> parsed = new(StringComparer.Ordinal);
    private static readonly TimeSpan LetGo = TimeSpan.FromSeconds(1.1);

    /// <summary>Keys that fired, and when each was last seen down (cleared each time, so the game never sees it).</summary>
    private readonly Dictionary<int, DateTime> held = new();

    private Hotkey? Parse(string text)
    {
        if (parsed.TryGetValue(text, out var k)) return k;
        if (parsed.Count > 32) parsed.Clear();
        return parsed[text] = Hotkeys.Parse(text);
    }

    private static unsafe bool Typing()
    {
        try
        {
            if (ImGui.GetIO().WantTextInput) return true;
            var atk = RaptureAtkModule.Instance();
            return atk != null && atk->IsTextInputActive();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Was the key set as <paramref name="text"/> just pressed? (Empty: no key.)</summary>
    public bool Pressed(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Parse(text) is not { } key) return false;
        try
        {
            var keys = Services.Keys;
            if (!keys.IsVirtualKeyValid(key.Key)) return false;
            var isDown = keys[key.Key];
            var now = DateTime.UtcNow;
            if (held.TryGetValue(key.Key, out var seen))
            {
                if (isDown)
                {
                    held[key.Key] = now;
                    keys[key.Key] = false;
                    return false;
                }
                if (now - seen < LetGo) return false;
                held.Remove(key.Key);
            }
            if (!isDown || Typing()) return false;
            if (!Hotkeys.Pressed(key, true, keys[VirtualKey.CONTROL], keys[VirtualKey.SHIFT], keys[VirtualKey.MENU], false)) return false;
            // The game doesn't act on it too.
            keys[key.Key] = false;
            held[key.Key] = now;
            return true;
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the keys");
            return false;
        }
    }
}
