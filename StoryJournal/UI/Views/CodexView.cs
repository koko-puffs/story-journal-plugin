using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
        codexRows = rows.OrderBy(r => KindOrder(r.Kind)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        codexCount = codexRows.Count(r => r.Entry != null);
        return codexRows;
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

    /// <summary>How many entries the Codex shows you now (the rail's count).</summary>
    private int codexCount;

    /// <summary>
    /// The Codex: Ask the journal on top (or <c>/sj ask</c>), who and what is near you as chips, the open entry as
    /// a person's page (what you learned and when, the rest locked until you get there), then everything so far
    /// by kind.
    /// </summary>
    private void DrawCodex()
    {
        var story = journal.Story.Now;
        var memory = journal.Memory;
        var at = Kit.At;
        var w = Kit.Width;
        var hint = "/sj ask";
        var hw = Txt.Measure(hint, Face.Mono, 11).X + S(12);
        Kit.Field("##codex-ask", at, w, ref codexAsk, "Ask the journal: a name, a place, a word", Icon.Search, 60);
        if (codexAsk.Length == 0)
        {
            var kp = new Vector2(at.X + w - hw - S(10), at.Y + (S(36) - S(20)) / 2);
            Kit.Dl.AddRect(kp, kp + new Vector2(hw, S(20)), Palette.U32(Palette.LineStrong), S(5));
            Txt.Line(Kit.Dl, kp + new Vector2(S(6), 0), hint, Face.Mono, 11, Palette.Faint, 20);
        }
        Kit.Reserve(at, w, S(36));
        var rows = CodexRows(story, memory);

        // Near you: the people in the quest, where you are, who you last met.
        var near = NearYou(story, memory);
        if (near.Count > 0)
        {
            Kit.Gap(12);
            var row = Kit.At;
            var x = row.X + Ink.Kicker(Kit.Dl, row + new Vector2(0, S(6)), "Near you") + S(12);
            var y = row.Y;
            for (var i = 0; i < near.Count; i++)
            {
                var name = near[i];
                var on = codexOpen != null && string.Equals(codexOpen, name, StringComparison.OrdinalIgnoreCase);
                var cw = Kit.ChipW(name);
                if (x + cw > row.X + w && x > row.X + S(90))
                {
                    x = row.X + S(78);
                    y += S(30);
                }
                var p = new Vector2(x, y);
                var (clicked, hovered, _) = Kit.Hit(Ids.Of("##codex-near", i), p, new Vector2(cw, S(24)));
                Kit.Chip(p, name, on ? ChipKind.Accent : hovered ? ChipKind.Quiet : ChipKind.Plain);
                if (clicked) Later(() => { codexOpen = on ? null : name; });
                x += cw + S(6);
            }
            Kit.Reserve(row, w, y - row.Y + S(24));
        }

        // The open entry, as a page.
        if (codexOpen != null && rows.FirstOrDefault(RowOpen) is { } open)
        {
            Kit.Gap(14);
            DrawPersonPage(open, story);
        }
        Kit.Gap(10);
        Parts.Quiet(journal.Prefs.Spoilers == Spoilers.Off
            ? "Spoilers are off (on the site): every entry and every layer shows."
            : "From your Codex · nothing past your last finished quest.");

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
        var n = 0;
        foreach (var r in rows)
        {
            if (r.Kind != kind)
            {
                kind = r.Kind;
                Kit.Gap(16);
                Ink.KickerRow(KindWord(kind));
                Kit.Gap(4);
            }
            CodexRowDraw(r, n++);
        }
    }

    private (int Story, int People, int Places) nearFor;
    private List<string> nearNames = new();

    /// <summary>Who and what is near: the quest's people, the place you're in, who you last met (each once).</summary>
    private List<string> NearYou(StoryState? story, CharacterMemory? memory)
    {
        var key = (journal.Story.Version, memory?.People.Count ?? 0, memory?.Visited.Count ?? 0);
        if (key == nearFor) return nearNames;
        nearFor = key;
        var list = new List<string>();
        void Add(string? n)
        {
            if (n is { Length: > 0 } && !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        }
        foreach (var p in story?.People ?? (IReadOnlyList<string>)Array.Empty<string>()) Add(p);
        Add(story?.Progress.Current?.Place);
        foreach (var p in (memory?.People ?? new List<MetPerson>()).AsEnumerable().Reverse().Take(3)) Add(p.Name);
        return nearNames = list.Take(6).ToList();
    }

    /// <summary>
    /// An entry's page: its picture's place (an arch with the initial), the kind and subtitle, the name, when it
    /// entered your story, each thing you learned beside the day you learned it, and the rest locked.
    /// </summary>
    private void DrawPersonPage(CodexRow row, StoryState? story)
    {
        Kit.BeginPanel("##codex-page", 18, 18, 18, Palette.Leaf, Palette.Line, Palette.RadiusXl);
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var narrow = w < S(380);
        var picW = narrow ? 0 : S(84);
        if (!narrow)
        {
            var pic = at;
            var ph = S(104);
            // The arch: a round top over a rounded foot.
            dl.AddRectFilledMultiColor(pic + new Vector2(0, picW / 2), pic + new Vector2(picW, ph), Palette.U32(Palette.Hex(0x3a2a26)), Palette.U32(Palette.Hex(0x3a2a26)), Palette.U32(Palette.Hex(0x1d1715)), Palette.U32(Palette.Hex(0x1d1715)));
            dl.AddCircleFilled(pic + new Vector2(picW / 2, picW / 2), picW / 2, Palette.U32(Palette.Hex(0x3a2a26)), 40);
            var initial = row.Name.Length > 0 ? row.Name[..1].ToUpperInvariant() : "?";
            var isz = Txt.Measure(initial, Face.DisplayItalic, 22);
            Txt.Draw(dl, pic + new Vector2((picW - isz.X) / 2, ph - isz.Y - S(10)), initial, Face.DisplayItalic, 22, Palette.Rgba(244, 236, 226, 0.78f));
        }
        var x = at.X + (narrow ? 0 : picW + S(18));
        var tw = w - (x - at.X);
        Kit.PushBox(x, tw);
        var kind = row.Kind switch { "person" => "A person", "place" => "A place", "chapter" => "A chapter", "item" => "A key item", _ => "The world" };
        var sub = row.Entry?.Subtitle;
        Ink.KickerRow(sub is { Length: > 0 } ? $"{kind} · {Txt.Fit(sub, Face.SansSemibold, 11, tw * 0.6f)}" : kind);
        Kit.Gap(2);
        foreach (var line in Lines.Of(row.Name, tw, Face.Display, 19))
        {
            var a = Kit.At;
            Txt.Line(dl, a, line, Face.Display, 19, Palette.Ink, 26);
            Kit.Reserve(a, tw, S(26));
        }
        if (row.Met is { } met)
        {
            Kit.Gap(4);
            var a = Kit.At;
            var ch = ChapterNumber() is { } n ? $" · ch. {Roman.Of(n)}" : string.Empty;
            Ink.Stamp(dl, a, $"Met{ch} · {Ui.Ago(met.At)}");
            Kit.Reserve(a, tw, S(20));
        }
        Kit.Gap(10);
        var shown = 0;
        var total = 0;
        if (row.Entry is { } entry)
        {
            var codex = journal.Codex.Codex;
            var spoilers = journal.Prefs.Spoilers;
            var layers = story != null ? codex.Layers(entry, story.Corpus, story.Seen, story.Gates(spoilers))
                : spoilers == Spoilers.Off ? entry.Layers.ToList() : new List<CodexLayer>();
            total = entry.Layers.Count;
            shown = layers.Count;
            var day = row.Met is { } m2 ? m2.At.ToLocalTime().ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture) : null;
            var dayW = day != null ? Txt.Measure("Www 00 Www", Face.Mono, 11).X + S(12) : 0;
            var first = true;
            foreach (var l in layers)
            {
                var a = Kit.At;
                if (day != null && first) Txt.Line(dl, a + new Vector2(0, S(2)), day, Face.Mono, 11, Palette.Faint, 18);
                var h = Lines.Draw(dl, new Vector2(a.X + dayW, a.Y), tw - dayW, l.Text, Face.Read, 13, Palette.Body, 20);
                if (SourceWords(l.Source) is { } from) h += Lines.Draw(dl, new Vector2(a.X + dayW, a.Y + h), tw - dayW, from, Face.Sans, 11, Palette.Faint, 16);
                Kit.Reserve(a, tw, h);
                Kit.Gap(8);
                first = false;
            }
            if (layers.Count == 0) Parts.Quiet("More unlocks as the story goes on.");
        }
        else if (row.Met is { } metOnly)
            Lines.Para($"You met them {Ui.Ago(metOnly.At)}{(metOnly.Place.Length > 0 ? $", in {metOnly.Place}" : string.Empty)}. The Codex has no page for them yet.", Palette.Body, 13, 20, Face.Read);
        if (total > shown)
        {
            Kit.Gap(4);
            var a = Kit.At;
            var bh = S(34);
            Kit.Dashed(a, a + new Vector2(tw, bh), S(10), Palette.LineStrong, 1f);
            Icons.Draw(dl, Icon.Lock, a + new Vector2(S(12), (bh - S(13)) / 2), S(13), Palette.Faint);
            var who = row.Kind == "person" ? "Their page" : "Its page";
            Txt.Line(dl, a + new Vector2(S(32), 0), Txt.Fit($"{who} fills in as you play: more of it the further you get.", Face.Sans, 12, tw - S(44)), Face.Sans, 12, Palette.Muted, 34);
            Kit.Reserve(a, tw, bh);
        }
        var notes = (journal.Sync.Hello?.Notes ?? Array.Empty<SiteNote>())
            .Where(nn => string.Equals(nn.Subject, row.Name, StringComparison.OrdinalIgnoreCase)
                         || row.Entry?.Names.Any(x2 => string.Equals(x2, nn.Subject, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        if (notes.Count > 0)
        {
            Kit.Gap(10);
            Ink.KickerRow("You wrote", Palette.Quill);
            Kit.Gap(2);
            foreach (var nn in notes.Take(2)) Ruled(() => Lines.Para(nn.Body, Palette.QuillInk, 13, 19, Face.ReadItalic), true);
        }
        Kit.Gap(10);
        var b = Parts.Buttons("##codex-page-b", new (string, Btn, Icon?)[]
        {
            ($"A note about {Txt.Fit(row.Name, Face.SansMedium, 13, S(140))}", Btn.Quill, Icon.Feather),
            ("→On the site", Btn.Quiet, Icon.ExternalLink),
        }, BtnSize.Tiny);
        if (b == 0)
        {
            var name = row.Entry?.Aliases.FirstOrDefault() ?? row.Name;
            Later(() => OpenNote?.Invoke(name));
        }
        else if (b == 1) Dalamud.Utility.Util.OpenLink(row.Entry != null ? SiteLink.CodexEntryUrl(row.Entry.Key) : SiteLink.SiteUrl + "/codex");
        Kit.PopBox();
        var end = ImGui.GetCursorScreenPos().Y;
        Kit.Reserve(at, w, MathF.Max(end - at.Y, narrow ? 0 : S(104)));
        Kit.EndPanel();
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

    /// <summary>A row of the list: its kind's picture, the name, when you met them or where it is; pressed, it opens as the page above.</summary>
    private void CodexRowDraw(CodexRow row, int index)
    {
        var open = RowOpen(row);
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(34);
        // Thousands of rows with spoilers off: those out of sight only take their space.
        if (!ImGui.IsRectVisible(at, at + new Vector2(w, h)))
        {
            Kit.Reserve(at, w, h);
            return;
        }
        var (clicked, hovered, _) = Kit.Hit(Ids.Of("##codex-row", index), at, new Vector2(w, h));
        if (hovered || open) Kit.Dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(open ? Palette.AccentRow : Palette.RowHover), S(10));
        if (open) Kit.Dl.AddRectFilled(at + new Vector2(0, S(8)), at + new Vector2(S(3), h - S(8)), Palette.U32(Palette.Ribbon), S(2));
        Icons.Draw(Kit.Dl, KindIcon(row.Kind), at + new Vector2(S(10), (h - S(14)) / 2), S(14), row.Entry != null ? Palette.Ribbon : Palette.Muted);
        var note = row.Met != null ? $"met {Ui.Ago(row.Met.At)}" : row.Place;
        var nw = note != null ? Txt.Measure(note, Face.Sans, 12).X : 0;
        Txt.Line(Kit.Dl, at + new Vector2(S(32), 0), Txt.Fit(row.Name, Face.Display, 15, w - S(44) - nw), Face.Display, 15, Palette.Ink, 34);
        if (note != null) Txt.Line(Kit.Dl, new Vector2(at.X + w - S(10) - nw, at.Y), note, Face.Sans, 12, Palette.Muted, 34);
        Kit.Reserve(at, w, h);
        if (clicked)
        {
            var name = row.Name;
            Later(() => codexOpen = open ? null : name);
        }
    }
}
