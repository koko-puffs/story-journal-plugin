using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    private string codexAsk = string.Empty;
    private string? codexOpen;

    /// <summary>A row of the Codex tab: an entry (when the Codex has one), or someone met or somewhere been that it hasn't.</summary>
    private sealed record CodexRow(string Name, string Kind, CodexEntry? Entry, MetPerson? Met, string? Place);

    private List<CodexRow> codexRows = new();
    private (int Story, int Codex, int People, int Places, string Ask, Spoilers Spoilers) codexFor;

    private List<CodexRow> CodexRows(StoryState? story, CharacterMemory? memory)
    {
        var key = (journal.Story.Version, journal.Codex.Version, memory?.People.Count ?? 0, memory?.Visited.Count ?? 0, codexAsk.Trim(), journal.Prefs.Spoilers);
        if (key == codexFor) return codexRows;
        codexFor = key;
        var codex = journal.Codex.Codex;
        var spoilers = journal.Prefs.Spoilers;
        var seen = story?.Seen ?? new HashSet<string>();
        var term = codexAsk.Trim();
        var rows = new List<CodexRow>();
        var entries = term.Length > 0 ? codex.Ask(term, seen, spoilers)
            : codex.Entries.Where(e => spoilers == Spoilers.Off || seen.Contains(e.Key)).OrderBy(e => KindOrder(e.Kind)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var e in entries)
        {
            var met = memory?.People.FirstOrDefault(p => e.Names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)));
            rows.Add(new CodexRow(e.Name, e.Kind, e, met, null));
        }
        // People met and places been that the Codex has no entry for still show, with where and when.
        if (memory != null)
        {
            foreach (var p in memory.People)
            {
                if (rows.Any(r => r.Met == p) || codex.Find(p.Name) != null) continue;
                if (term.Length > 0 && !p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new CodexRow(p.Name, "person", null, p, null));
            }
            foreach (var id in memory.Visited)
            {
                var (name, region, _) = Game.Places.Territory(id);
                if (name.Length == 0 || codex.Place(name) != null || rows.Any(r => r.Name == name)) continue;
                if (term.Length > 0 && !name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new CodexRow(name, "place", null, null, region));
            }
        }
        return codexRows = rows.OrderBy(r => KindOrder(r.Kind)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int KindOrder(string kind) => kind switch
    {
        "person" => 0,
        "place" => 1,
        "lore" => 2,
        _ => 3,
    };

    private static string KindWord(string kind) => kind switch
    {
        "person" => "PEOPLE",
        "place" => "PLACES",
        "lore" => "LORE",
        _ => "WORDS",
    };

    private void DrawCodex()
    {
        var story = journal.Story.Now;
        var memory = journal.Memory;
        Parts.Field("##codex-ask", ref codexAsk, "Ask the journal: a name or a word from the story", 60, Dalamud.Bindings.ImGui.ImGuiInputTextFlags.None, Icon.Search);
        Kit.Gap(6);
        Parts.Quiet(journal.Prefs.Spoilers == Spoilers.Off
            ? "Spoilers are off (on the site): every entry and every layer shows."
            : "From your Codex · nothing past your last finished quest.");
        var rows = CodexRows(story, memory);
        if (rows.Count == 0)
        {
            Kit.Gap(14);
            Empty("##codex-empty", Icon.BookOpen, codexAsk.Trim().Length > 0 ? "Nothing by that name yet" : "Your Codex is empty for now",
                codexAsk.Trim().Length > 0
                    ? "Only what you've seen in your story shows here. Keep playing."
                    : journal.Codex.Codex.Entries.Count == 0
                        ? "The Codex's words come from kokos-tools.com; they'll be fetched once the site can be reached. People and places you meet show here meanwhile."
                        : "People, places and lore unlock as they appear in your story.");
            return;
        }
        string? kind = null;
        var i = 0;
        foreach (var row in rows)
        {
            if (row.Kind != kind)
            {
                kind = row.Kind;
                Parts.Heading(KindWord(kind), 16);
            }
            CodexRowDraw(row, i++, story);
        }
    }

    private void CodexRowDraw(CodexRow row, int index, StoryState? story)
    {
        var open = codexOpen != null && (string.Equals(codexOpen, row.Name, StringComparison.OrdinalIgnoreCase)
                                         || row.Entry?.Names.Any(n => string.Equals(n, codexOpen, StringComparison.OrdinalIgnoreCase)) == true);
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(34);
        var (clicked, hovered, _) = Kit.Hit(Ids.Of("##codex-row", index), at, new Vector2(w, h));
        if (hovered || open) Kit.Dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(open ? Palette.AccentRow : Palette.RowHover), S(10));
        var icon = row.Kind switch { "person" => Icon.Users, "place" => Icon.MapPin, _ => Icon.BookOpen };
        Icons.Draw(Kit.Dl, icon, at + new Vector2(S(10), (h - S(14)) / 2), S(14), row.Entry != null ? Palette.Accent : Palette.Muted);
        var note = row.Met != null ? $"met {Ui.Ago(row.Met.At)}" : row.Place;
        var nw = note != null ? Txt.Measure(note, Face.Sans, 12).X : 0;
        Txt.Line(Kit.Dl, at + new Vector2(S(32), 0), Txt.Fit(row.Name, Face.SansMedium, 14, w - S(44) - nw), Face.SansMedium, 14, Palette.Ink, 34);
        if (note != null) Txt.Line(Kit.Dl, new Vector2(at.X + w - S(10) - nw, at.Y), note, Face.Sans, 12, Palette.Muted, 34);
        Kit.Reserve(at, w, h);
        if (clicked)
        {
            var name = row.Name;
            Later(() => codexOpen = open ? null : name);
        }
        if (!open) return;

        // The entry, as far as you've seen.
        Kit.PushBox(at.X + S(32), w - S(42));
        Kit.Gap(4);
        if (row.Entry is { } entry)
        {
            var layers = story != null
                ? journal.Codex.Codex.Layers(entry, story.Corpus, story.Seen, story.ExpansionReached, journal.Prefs.Spoilers)
                : new List<CodexLayer>();
            if (layers.Count == 0) Parts.Quiet("More unlocks as the story goes on.");
            foreach (var l in layers)
            {
                Parts.Story(l.Text);
                Kit.Gap(6);
            }
            if (entry.Aliases.Count > 0) Parts.Quiet("Also: " + string.Join(", ", entry.Aliases));
        }
        if (row.Met is { } met)
        {
            Kit.Gap(4);
            Parts.Quiet($"You met them {Ui.Ago(met.At)}{(met.Place.Length > 0 ? $", in {met.Place}" : string.Empty)}.");
        }
        var notes = (journal.Sync.Hello?.Notes ?? Array.Empty<SiteNote>())
            .Where(n => string.Equals(n.Subject, row.Name, StringComparison.OrdinalIgnoreCase)
                        || row.Entry?.Names.Any(x => string.Equals(x, n.Subject, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        foreach (var n in notes)
        {
            Kit.Gap(6);
            Parts.Quote($"“{n.Body}”", n.At is { } nat ? $"your note, {Ui.Ago(nat)}" : "your note");
        }
        Kit.Gap(8);
        var b = Parts.Buttons(Ids.Of("##codex-b", index), new (string, Btn, Icon?)[]
        {
            ($"A note about {Txt.Fit(row.Name, Face.SansMedium, 13, S(160))}", Btn.Glass, Icon.MessageSquare),
            ("→On the site", Btn.Quiet, Icon.ExternalLink),
        }, BtnSize.Tiny);
        if (b == 0)
        {
            var name = row.Entry?.Aliases.FirstOrDefault() ?? row.Name;
            Later(() => OpenNote?.Invoke(name));
        }
        else if (b == 1) Dalamud.Utility.Util.OpenLink(row.Entry != null ? SiteLink.CodexEntryUrl(row.Entry.Key) : SiteLink.SiteUrl + "/codex");
        Kit.Gap(10);
        Kit.PopBox();
    }
}
