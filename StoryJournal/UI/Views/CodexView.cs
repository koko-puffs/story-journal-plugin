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
        var gates = story?.Gates(journal.Prefs.Spoilers) ?? CodexGates.None(journal.Prefs.Spoilers);
        var seen = story?.Seen ?? new HashSet<string>();
        var term = codexAsk.Trim();
        var rows = new List<CodexRow>();
        // Thousands of entries (every person who speaks in a quest): the people met looked up by name, once.
        var metByName = new Dictionary<string, MetPerson>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in memory?.People ?? (IEnumerable<MetPerson>)Array.Empty<MetPerson>()) metByName.TryAdd(p.Name, p);
        var entries = term.Length > 0 ? codex.Ask(term, seen, gates) : codex.Entries.Where(e => codex.Visible(e, seen, gates));
        var shownMet = new HashSet<MetPerson>();
        foreach (var e in entries)
        {
            MetPerson? met = null;
            foreach (var n in e.Names)
                if (metByName.TryGetValue(n, out met)) break;
            if (met != null) shownMet.Add(met);
            rows.Add(new CodexRow(e.Name, e.Kind, e, met, null));
        }
        // People met and places been that the Codex has no entry for still show, with where and when.
        if (memory != null)
        {
            foreach (var p in memory.People)
            {
                if (shownMet.Contains(p) || codex.Find(p.Name) != null) continue;
                if (term.Length > 0 && !p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new CodexRow(p.Name, "person", null, p, null));
            }
            foreach (var id in memory.Visited)
            {
                var (name, region, _) = Game.Places.Territory(id);
                if (name.Length == 0 || codex.Place(name) != null || rows.Any(r => r.Entry == null && r.Name == name)) continue;
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
        "chapter" => 2,
        "lore" => 3,
        "item" => 4,
        "collection" => 6,
        _ => 5,
    };

    private static string KindWord(string kind) => kind switch
    {
        "person" => "PEOPLE",
        "place" => "PLACES",
        "chapter" => "CHAPTERS",
        "lore" => "LORE",
        "item" => "KEY ITEMS",
        "collection" => "COLLECTIONS",
        _ => "WORDS",
    };

    private static Icon KindIcon(string kind) => kind switch
    {
        "person" => Icon.Users,
        "place" => Icon.MapPin,
        "chapter" => Icon.Bookmark,
        "item" => Icon.Package,
        "collection" => Icon.Sparkles,
        _ => Icon.BookOpen,
    };

    /// <summary>Where a layer's words are from, in small words under them (version 2's <c>source</c>).</summary>
    private static string? SourceWords(string source) => source switch
    {
        "codex" => "the game's codex",
        "synopsis" => "the chapter's synopsis",
        "title" => "their title",
        "record" => "a field record",
        "notes" => "the variant dungeons' notes",
        "sight" => "the sightseeing log",
        "fate" => "a FATE",
        "item" => "a key item",
        "deity" => "the Twelve",
        "card" => "a Triple Triad card",
        "minion" => "a minion",
        "mount" => "a mount",
        "song" => "an orchestrion roll",
        "restoration" => "the Restoration's notices",
        "occult" => "the Occult Crescent",
        "" => null,
        _ => source,
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

    private (string? Open, List<CodexRow>? Rows) openFor;
    private HashSet<object> openRows = new(ReferenceEqualityComparer.Instance);

    /// <summary>Is a row open (the name opened is its name or an alias)? Worked out once per name opened and list of rows, not per row each frame.</summary>
    private bool RowOpen(CodexRow row)
    {
        if (codexOpen is not { } name) return false;
        if (openFor.Open != name || !ReferenceEquals(openFor.Rows, codexRows))
        {
            openFor = (name, codexRows);
            openRows = new HashSet<object>(codexRows.Where(r => string.Equals(name, r.Name, StringComparison.OrdinalIgnoreCase)
                                                               || r.Entry?.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) == true),
                ReferenceEqualityComparer.Instance);
        }
        return openRows.Contains(row);
    }

    private void CodexRowDraw(CodexRow row, int index, StoryState? story)
    {
        var open = RowOpen(row);
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(34);
        // Thousands of rows with spoilers off: those out of sight only take their space.
        if (!open && !Dalamud.Bindings.ImGui.ImGui.IsRectVisible(at, at + new Vector2(w, h)))
        {
            Kit.Reserve(at, w, h);
            return;
        }
        var (clicked, hovered, _) = Kit.Hit(Ids.Of("##codex-row", index), at, new Vector2(w, h));
        if (hovered || open) Kit.Dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(open ? Palette.AccentRow : Palette.RowHover), S(10));
        var icon = KindIcon(row.Kind);
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
            var codex = journal.Codex.Codex;
            var spoilers = journal.Prefs.Spoilers;
            var layers = story != null ? codex.Layers(entry, story.Corpus, story.Seen, story.Gates(spoilers))
                : spoilers == Spoilers.Off ? entry.Layers.ToList() : new List<CodexLayer>();
            if (entry.Subtitle is { Length: > 0 } subtitle)
            {
                Parts.Quiet(subtitle);
                Kit.Gap(4);
            }
            if (layers.Count == 0) Parts.Quiet("More unlocks as the story goes on.");
            foreach (var l in layers)
            {
                Parts.Story(l.Text);
                if (SourceWords(l.Source) is { } from) Parts.Quiet(from);
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
