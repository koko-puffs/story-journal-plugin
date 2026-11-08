using System;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    private string? lineKey;
    private string? photoKey;
    private string? noteKey;

    private static readonly string[] WhisperWords = { "Quiet", "Some", "Chatty" };

    private void DrawSettings()
    {
        // Account.
        Parts.Heading("ACCOUNT", 0);
        Kit.BeginPanel("##set-account", 4, 0, 4);
        var linked = journal.Sync.Link;
        var who = journal.Player is { } p ? $" · character {p.Name}{(p.World.Length > 0 ? $" ({p.World})" : string.Empty)}" : string.Empty;
        var status = linked switch
        {
            LinkState.Linked l => $"Linked to {l.Account} on kokos-tools.com{who}",
            LinkState.Linking => "Linking…",
            LinkState.Refused r => r.Why,
            _ => "Not linked: everything is kept on this PC until you link.",
        };
        if (journal.Sync.SwitchedOff) status = "Story Journal is switched off on the site for now. Your journal is kept on this PC until it's back.";
        var tokenNote = SiteLink.LooksRight(config.Token) ? $"Token {SiteLink.Masked(config.Token)}" : null;
        if (linked is LinkState.Unlinked or LinkState.Refused)
        {
            Parts.SetRow(0, "kokos-tools.com", status, 0, 0);
            Kit.PushBox(Kit.Left + S(16), Kit.Width - S(32));
            DrawTokenField("##set-token");
            Kit.Gap(14);
            Kit.PopBox();
        }
        else if (Parts.ButtonRow(0, "##set-unlink", "kokos-tools.com", status + (tokenNote != null ? $"\n{tokenNote}" : string.Empty), "Unlink", Btn.Danger))
            Later(() => journal.Link(string.Empty));
        Kit.EndPanel();

        // Recording.
        Parts.Heading("RECORDING", 22);
        Kit.BeginPanel("##set-rec", 4, 0, 4);
        var row = 0;
        if (Parts.SwitchRow(row++, "##set-story", "Quests, cutscenes, places, duties and levels", "Written as they happen. Off: only what you save yourself.", config.RecordStory))
            Later(() => Flip(() => config.RecordStory = !config.RecordStory));
        if (Parts.SwitchRow(row++, "##set-side", "Side content too", "Side quests, and duties the main story doesn't unlock (roulettes and the like). Off keeps the journal about the story.", config.RecordSideContent))
            Later(() => Flip(() => config.RecordSideContent = !config.RecordSideContent));
        if (Parts.SwitchRow(row++, "##set-people", "People met", "The name of whoever speaks to you in a quest, the first time. Other players are never written down.", config.RecordPeople))
            Later(() => Flip(() => config.RecordPeople = !config.RecordPeople));
        // The clock takes the new minutes at its next look (SessionClock.IdleFor keeps them to these).
        var idle = (int)SessionClock.IdleFor(config.IdleMinutes).TotalMinutes;
        var step = Parts.StepperRow(row++, "##set-idle", "A new session after", "Minutes with nothing happening (the session ends when it was last heard of).", $"{idle} min",
            idle > SessionClock.LeastIdleMinutes, idle < SessionClock.MostIdleMinutes);
        if (step != 0) Later(() => Flip(() => config.IdleMinutes = (int)SessionClock.IdleFor(idle + step * 5).TotalMinutes));
        if (Parts.ButtonRow(row++, "##set-pause", config.Paused ? "Recording is paused" : "Pause recording", "Or /sj pause and /sj resume. What's queued still goes.",
                config.Paused ? "Resume" : "Pause", config.Paused ? Btn.Primary : Btn.Glass, config.Paused ? Icon.Play : Icon.Pause))
            Later(() => journal.Pause(!config.Paused));
        Kit.EndPanel();

        // Spoilers and whispers: the site's, shared.
        Parts.Heading("SPOILERS AND WHISPERS", 22);
        Kit.BeginPanel("##set-spoil", 4, 0, 4);
        var prefs = journal.Prefs;
        var spoilerWords = prefs.Spoilers switch
        {
            Spoilers.Off => "I don't mind: everything shows.",
            Spoilers.Gentle => "Gentle: the names of duties ahead show; nothing of the story.",
            _ => "Strict: nothing ahead of you shows (duties ahead behind Show).",
        };
        if (Parts.ButtonRow(0, "##set-spoilers", "Spoilers", spoilerWords + " One setting, shared with the site: change it there.", "On the site", Btn.Glass, Icon.ExternalLink))
            Dalamud.Utility.Util.OpenLink(SiteLink.TokensUrl);
        var here = config.WhispersHere is >= 0 and <= 2 ? config.WhispersHere : (int)prefs.Whispers;
        var picked = Parts.SegmentedRow(1, "##set-whispers", "How chatty", $"The site says {WhisperWords[(int)prefs.Whispers].ToLowerInvariant()}; here it can be quieter. Never in combat, duties or cutscenes.", WhisperWords, here);
        if (picked != here)
            Later(() => Flip(() => config.WhispersHere = picked >= (int)prefs.Whispers ? -1 : picked));
        Kit.EndPanel();

        // Windows.
        Parts.Heading("WINDOWS", 22);
        Kit.BeginPanel("##set-win", 4, 0, 4);
        row = 0;
        if (Parts.SwitchRow(row++, "##set-tracker", "The tracker", "Progress and what's next, under the game's quest list. Drag it where you like.", config.ShowTracker))
            Later(() => Flip(() => config.ShowTracker = !config.ShowTracker));
        if (Parts.SwitchRow(row++, "##set-partners", "Pace lock on the tracker", "A warning before you go past a story partner who asked you to wait (partners are made on the site).", config.TrackerPartners))
            Later(() => Flip(() => config.TrackerPartners = !config.TrackerPartners));
        if (Parts.SwitchRow(row++, "##set-welcome", "Welcome back", $"After {Ui.Plural(prefs.WelcomeHours, "hour", "hours")} away (the site's setting): the recap and the next step.", config.ShowWelcome))
            Later(() => Flip(() => config.ShowWelcome = !config.ShowWelcome));
        if (Parts.SwitchRow(row++, "##set-wrap", "End of session", "At logout and /sj end: a title, how it felt, a thought for next time.", config.ShowWrapUp))
            Later(() => Flip(() => config.ShowWrapUp = !config.ShowWrapUp));
        if (Parts.SwitchRow(row++, "##set-mile", "Milestones", "A card when a chapter closes, with theories to settle.", config.ShowMilestones))
            Later(() => Flip(() => config.ShowMilestones = !config.ShowMilestones));
        if (Parts.SwitchRow(row++, "##set-chat", "Lines in chat", "When a session starts, recording pauses, a photo's taken with the HUD hidden.", config.ChatMessages))
            Later(() => Flip(() => config.ChatMessages = !config.ChatMessages));
        if (Parts.ButtonRow(row++, "##set-show-welcome", "Welcome back now", "Opens the card whatever the time away.", "Show", Btn.Glass, null, journal.Sync.Hello == null))
            Later(journal.ShowWelcome);
        Kit.EndPanel();

        // Keys.
        Parts.Heading("KEYS", 22);
        Kit.BeginPanel("##set-keys", 14, 16, 14);
        KeyField("##set-key-line", "Save the line", ref lineKey, config.LineKey, v => config.LineKey = v);
        Kit.Gap(10);
        KeyField("##set-key-photo", "Photo", ref photoKey, config.PhotoKey, v => config.PhotoKey = v);
        Kit.Gap(10);
        KeyField("##set-key-note", "Quick note", ref noteKey, config.NoteKey, v => config.NoteKey = v);
        Kit.Gap(8);
        Parts.Quiet("Any key or combination (F11, Ctrl+J, Ctrl+Shift+F12); empty for none. Each also has a command for macros: /sj line, /sj photo, /sj note.");
        Kit.EndPanel();

        // Offline.
        Parts.Heading("OFFLINE", 22);
        Kit.BeginPanel("##set-off", 14, 16, 14);
        DrawQueueLine();
        if (journal.Queue.AsideCount > 0)
            Parts.Quiet($"{Ui.Plural(journal.Queue.AsideCount, "thing", "things")} the site refused outright, kept aside on this PC (the plugin's config folder, queue/aside.jsonl).");
        var codex = journal.Codex;
        var codexWords = codex.Codex.Entries.Count > 0
            ? $"The Codex: {Ui.Count(codex.Codex.Entries.Count)} entries{(codex.FetchedAt is { } f ? $", fetched {Ui.Ago(f)}" : string.Empty)}."
            : "The Codex hasn't been fetched yet.";
        Parts.Quiet(codexWords + (codex.Problem != null ? " " + codex.Problem : string.Empty));
        Kit.Gap(8);
        var b = Parts.Buttons("##set-off-b", new (string, Btn, Icon?)[] { ("Send now", Btn.Glass, Icon.Upload), ("Fetch the Codex again", Btn.Quiet, Icon.Refresh) }, BtnSize.Tiny);
        if (b == 0) Later(journal.Sync.Kick);
        else if (b == 1) Later(codex.FetchNow);
        Kit.EndPanel();
        Kit.Gap(10);
        Parts.Quiet("Story Journal records what happens to your character only, with your own words and photos when you ask. It never reads chat, and only the line you save leaves the game.");
    }

    private void Flip(Action change)
    {
        change();
        config.Save();
    }

    /// <summary>A key's field: written as words, kept when it reads as a key (or is empty: none).</summary>
    private void KeyField(string id, string label, ref string? editing, string current, Action<string> set)
    {
        editing ??= current;
        var at = Kit.At;
        var w = Kit.Width;
        var lw = S(120);
        Txt.Line(Kit.Dl, at, label, Face.SansMedium, 14, Palette.Ink, 36);
        var text = editing;
        if (Kit.Field(id, new Vector2(at.X + lw, at.Y), w - lw, ref text, "none", Icon.Keyboard, 32)) editing = text;
        var typing = Dalamud.Bindings.ImGui.ImGui.IsItemActive();
        Kit.Reserve(at, w, S(36));
        var normal = Hotkeys.Normalise(editing);
        if (normal == null)
        {
            Kit.Gap(4);
            var a2 = Kit.At;
            Txt.Line(Kit.Dl, new Vector2(a2.X + lw, a2.Y), "That isn't a key: try F11 or Ctrl+J.", Face.Sans, 12, Palette.Danger, 16);
            Kit.Reserve(a2, w, S(16));
        }
        else if (normal != current && !typing)
        {
            set(normal);
            config.Save();
            editing = normal;
        }
    }
}
