using System;
using System.Collections.Generic;
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

    private static readonly string[] WhisperWords = { "Quiet", "Sometimes", "Chatty" };

    private void DrawSettings()
    {
        PlaceHead("Settings", "The journal in game");
        Kit.Gap(14);

        // Linked or not.
        var linked = journal.Sync.Link;
        if (linked is LinkState.Unlinked or LinkState.Refused)
        {
            Kit.BeginPanel("##set-link", 16, 18, 16, Palette.Leaf, Palette.Line, Palette.RadiusXl);
            var refused = linked is LinkState.Refused r ? r.Why : null;
            Txt.Line(Kit.Dl, Kit.At, "Not linked", Face.SansSemibold, 14, Palette.Ink, 20);
            Kit.Gap(20);
            Parts.Quiet(refused ?? "Everything is kept on this PC until you link: make a token on the site and paste it here.", refused != null ? Palette.Danger : null);
            Kit.Gap(10);
            DrawTokenField("##set-token");
            Kit.EndPanel();
        }
        else
        {
            var off = journal.Sync.SwitchedOff;
            Kit.BeginPanel("##set-link", 14, 18, 14, Palette.Over(Palette.SuccessWash, Palette.Leaf), Palette.SuccessEdge, Palette.RadiusXl);
            var at = Kit.At;
            var w = Kit.Width;
            var account = linked is LinkState.Linked l ? l.Account : "…";
            Kit.Dot(at + new Vector2(S(4), S(10)), off ? Palette.Warn : Palette.Good, 8);
            var unlinkW = Kit.ButtonWidth("Unlink", BtnSize.Sm, null, Btn.Quiet);
            Txt.Line(Kit.Dl, at + new Vector2(S(18), 0), Txt.Fit(off ? "Switched off on the site for now" : linked is LinkState.Linking ? "Linking…" : $"Linked to the site as {account}",
                Face.SansSemibold, 14, w - S(18) - unlinkW - S(10)), Face.SansSemibold, 14, Palette.Ink, 20);
            var sub = new List<string>();
            if (journal.Player is { World.Length: > 0 } p) sub.Add(p.World);
            sub.Add(journal.Sync.SyncedAt is { } synced ? $"last sent {Ui.Ago(synced)}" : "nothing sent yet");
            if (SiteLink.LooksRight(config.Token)) sub.Add($"token {SiteLink.Masked(config.Token)}");
            Txt.Line(Kit.Dl, at + new Vector2(S(18), S(20)), Txt.Fit(string.Join(" · ", sub), Face.Sans, 12, w - S(18) - unlinkW - S(10)), Face.Sans, 12, Palette.Muted, 17);
            if (Kit.Button("##set-unlink", new Vector2(at.X + w - unlinkW, at.Y + S(4)), "Unlink", Btn.Quiet, BtnSize.Sm)) Later(() => journal.Link(string.Empty));
            Kit.Reserve(at, w, S(38));
            Kit.EndPanel();
        }

        // Spoilers: the site's one setting.
        Kit.Gap(20);
        Ink.KickerRow("Spoilers");
        Kit.Gap(8);
        var prefs = journal.Prefs;
        {
            var at = Kit.At;
            var w = Kit.Width;
            var name = prefs.Spoilers switch { Spoilers.Off => "I don't mind", Spoilers.Gentle => "Gentle", _ => "Strict" };
            var says = prefs.Spoilers switch
            {
                Spoilers.Off => "Everything shows. One setting, shared with the site.",
                Spoilers.Gentle => "Duties ahead show their names; nothing of the story. One setting, shared with the site.",
                _ => "Nothing ahead shows: what's ahead is inked out. One setting, shared with the site.",
            };
            Icons.Draw(Kit.Dl, Icon.Check, at + new Vector2(0, S(4)), S(15), Palette.Good);
            var bw = Kit.ButtonWidth("On the site", BtnSize.Sm, Icon.ArrowRight);
            Txt.Line(Kit.Dl, at + new Vector2(S(26), 0), name, Face.SansSemibold, 14, Palette.Ink, 20);
            var h = S(20) + Lines.Draw(Kit.Dl, at + new Vector2(S(26), S(20)), w - S(26) - bw - S(12), says, Face.Sans, 12, Palette.Muted, 17);
            if (Kit.Button("##set-spoilers", new Vector2(at.X + w - bw, at.Y + S(2)), "On the site", Btn.Glass, BtnSize.Sm, Icon.ArrowRight))
                Dalamud.Utility.Util.OpenLink(SiteLink.SiteUrl);
            Kit.Reserve(at, w, MathF.Max(h, Kit.ButtonHeight(BtnSize.Sm)));
        }

        // What comes to you.
        Kit.Gap(22);
        Ink.KickerRow("What comes to you");
        Kit.Gap(4);
        Kit.BeginPanel("##set-come", 2, 0, 2, Palette.Leaf, Palette.Line, Palette.RadiusXl);
        var row = 0;
        if (Parts.SwitchRow(row++, "##set-welcome", "Welcome back", $"After {(prefs.WelcomeHours > 0 ? Ui.Plural(prefs.WelcomeHours, "hour", "hours") : "any time")} away (the site's setting): the recap and the next step.", config.ShowWelcome))
            Later(() => Flip(() => config.ShowWelcome = !config.ShowWelcome));
        var here = config.WhispersHere is >= 0 and <= 2 ? config.WhispersHere : (int)prefs.Whispers;
        var picked = Parts.SegmentedRow(row++, "##set-whispers", "Margin notes", $"New places, and people you've met. The site says {WhisperWords[(int)prefs.Whispers].ToLowerInvariant()}; here it can be quieter.", WhisperWords, here);
        if (picked != here) Later(() => Flip(() => config.WhispersHere = picked >= (int)prefs.Whispers ? -1 : picked));
        if (Parts.SwitchRow(row++, "##set-tracker", "The ribbon tracker", "The next step, hanging from the top of the screen.", config.ShowTracker))
            Later(() => Flip(() => config.ShowTracker = !config.ShowTracker));
        var ends = config.ShowWrapUp && config.ShowMilestones;
        if (Parts.SwitchRow(row++, "##set-ends", "End of the evening, milestones", "A title, how it felt; a seal when a chapter closes.", ends))
            Later(() => Flip(() =>
            {
                config.ShowWrapUp = !ends;
                config.ShowMilestones = !ends;
            }));
        if (Parts.SwitchRow(row++, "##set-partners", "Pace lock on the tracker", "A warning before you go past a story partner who asked you to wait (partners are made on the site).", config.TrackerPartners))
            Later(() => Flip(() => config.TrackerPartners = !config.TrackerPartners));
        if (Parts.SwitchRow(row++, "##set-chat", "Lines in chat", "When an evening starts, recording pauses, a photo's taken with the HUD hidden.", config.ChatMessages))
            Later(() => Flip(() => config.ChatMessages = !config.ChatMessages));
        if (Parts.ButtonRow(row++, "##set-show-welcome", "Welcome back now", "Opens the card whatever the time away.", "Show", Btn.Glass, null, journal.Sync.Hello == null))
            Later(journal.ShowWelcome);
        Kit.EndPanel();

        // Quiet times.
        Kit.Gap(22);
        Ink.KickerRow("Quiet times");
        Kit.Gap(4);
        Kit.BeginPanel("##set-quiet", 2, 0, 2, Palette.Leaf, Palette.Line, Palette.RadiusXl);
        var tick = Parts.SetRow(0, "Never in combat, duties or cutscenes", "Always: anything that comes waits for a calm moment.", S(16), S(16));
        Icons.Draw(Kit.Dl, Icon.Check, tick, S(16), Palette.Good);
        if (Parts.SwitchRow(1, "##set-party", "Hold it all while I'm in a party", "Cards wait until you're on your own.", config.HoldInParty))
            Later(() => Flip(() => config.HoldInParty = !config.HoldInParty));
        if (Parts.SwitchRow(2, "##set-calm", "Calm", "Nothing moves: windows, cards and the ribbon simply appear. (Dalamud's reduced motion does the same.)", config.Calm))
            Later(() => Flip(() =>
            {
                config.Calm = !config.Calm;
                Ink.Calm = config.Calm;
            }));
        Kit.EndPanel();

        // Recording.
        Kit.Gap(22);
        Ink.KickerRow("Recording");
        Kit.Gap(4);
        Kit.BeginPanel("##set-rec", 2, 0, 2, Palette.Leaf, Palette.Line, Palette.RadiusXl);
        row = 0;
        if (Parts.SwitchRow(row++, "##set-story", "Quests, cutscenes, places, duties and levels", "Written as they happen. Off: only what you save yourself.", config.RecordStory))
            Later(() => Flip(() => config.RecordStory = !config.RecordStory));
        if (Parts.SwitchRow(row++, "##set-side", "Side content too", "Side quests, and duties the main story doesn't unlock (roulettes and the like). Off keeps the journal about the story.", config.RecordSideContent))
            Later(() => Flip(() => config.RecordSideContent = !config.RecordSideContent));
        if (Parts.SwitchRow(row++, "##set-people", "People met", "The name of whoever speaks to you in a quest, the first time. Other players are never written down.", config.RecordPeople))
            Later(() => Flip(() => config.RecordPeople = !config.RecordPeople));
        // The clock takes the new minutes at its next look (SessionClock.IdleFor keeps them to these).
        var idle = (int)SessionClock.IdleFor(config.IdleMinutes).TotalMinutes;
        var step = Parts.StepperRow(row++, "##set-idle", "A new evening after", "Minutes with nothing happening (the evening ends when it was last heard of).", $"{idle} min",
            idle > SessionClock.LeastIdleMinutes, idle < SessionClock.MostIdleMinutes);
        if (step != 0) Later(() => Flip(() => config.IdleMinutes = (int)SessionClock.IdleFor(idle + step * 5).TotalMinutes));
        if (Parts.ButtonRow(row++, "##set-pause", config.Paused ? "Recording is paused" : "Pause recording", "Or /sj pause and /sj resume. What's queued still goes.",
                config.Paused ? "Resume" : "Pause", config.Paused ? Btn.Primary : Btn.Glass, config.Paused ? Icon.Play : Icon.Pause))
            Later(() => journal.Pause(!config.Paused));
        Kit.EndPanel();

        // Keys.
        Kit.Gap(22);
        Ink.KickerRow("Keys");
        Kit.Gap(4);
        Kit.BeginPanel("##set-keys", 14, 16, 14, Palette.Leaf, Palette.Line, Palette.RadiusXl);
        KeyField("##set-key-line", "Save the line", ref lineKey, config.LineKey, v => config.LineKey = v);
        Kit.Gap(10);
        KeyField("##set-key-photo", "Photo", ref photoKey, config.PhotoKey, v => config.PhotoKey = v);
        Kit.Gap(10);
        KeyField("##set-key-note", "Quick note", ref noteKey, config.NoteKey, v => config.NoteKey = v);
        Kit.Gap(8);
        Parts.Quiet("Any key or combination (F11, Ctrl+J, Ctrl+Shift+F12); empty for none. Each also has a command for macros: /sj line, /sj photo, /sj note.");
        Kit.EndPanel();

        // Offline.
        Kit.Gap(22);
        Ink.KickerRow("Offline");
        Kit.Gap(4);
        Kit.BeginPanel("##set-off", 14, 16, 14, Palette.Leaf, Palette.Line, Palette.RadiusXl);
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
        Kit.Gap(12);
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
