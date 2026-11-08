using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace StoryJournal.Game;

/// <summary>A line of dialogue as the game's <c>Talk</c> window showed it.</summary>
public sealed record TalkLine(string Speaker, string Text, DateTime At);

/// <summary>
/// The game's dialogue window (<c>Talk</c>): who's speaking and what they say, read each time it's set up
/// or its text changes (through Dalamud's addon lifecycle, so nothing is hooked by hand). Only the last
/// line is kept, in memory: it leaves the game only when the player saves it (<c>/sj line</c>). The
/// speaker is the window's name node (<c>AtkTextNode220</c>), the words its text node
/// (<c>AtkTextNode228</c>), falling back to its first two values.
/// </summary>
public sealed unsafe class TalkReader : IDisposable
{
    private const string Addon = "Talk";

    public TalkReader()
    {
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Addon, OnTalk);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, Addon, OnTalk);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, Addon, OnTalk);
    }

    /// <summary>The last line shown (null until one is).</summary>
    public TalkLine? Last { get; private set; }

    /// <summary>A new line was shown (framework thread).</summary>
    public event Action<TalkLine>? Line;

    private void OnTalk(AddonEvent type, AddonArgs args)
    {
        try
        {
            var talk = (AddonTalk*)args.Addon.Address;
            if (talk == null) return;
            var speaker = NodeText(talk->AtkTextNode220);
            var text = NodeText(talk->AtkTextNode228);
            if (text.Length == 0 && talk->AtkUnitBase.AtkValues != null && talk->AtkUnitBase.AtkValuesCount >= 2)
            {
                var values = talk->AtkUnitBase.AtkValues;
                if (AtkText.IsText(values[0].Type)) text = AtkText.Text(values[0].String.Value);
                if (speaker.Length == 0 && AtkText.IsText(values[1].Type)) speaker = AtkText.Text(values[1].String.Value);
            }
            if (text.Length == 0) return;
            if (Last is { } last && last.Text == text && last.Speaker == speaker) return;
            var line = new TalkLine(speaker, text, DateTime.UtcNow);
            Last = line;
            Line?.Invoke(line);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the dialogue window");
        }
    }

    private static string NodeText(AtkTextNode* node)
    {
        if (node == null) return string.Empty;
        try
        {
            return AtkText.Text(node->NodeText.StringPtr.Value);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, Addon, OnTalk);
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, Addon, OnTalk);
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, Addon, OnTalk);
    }
}
