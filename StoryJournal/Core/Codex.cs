using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace StoryJournal.Core;

/// <summary>
/// A layer of a Codex entry: a piece of text and the gates it's behind. Every gate it has must be met
/// for it to show (<see cref="Codex.Shows"/>); one with no gate shows once its entry is seen.
/// </summary>
/// <param name="Text">The words.</param>
/// <param name="Needs">Version 1: names that must be seen too.</param>
/// <param name="Expansion">The expansion that must be reached (ExVersion: 0 A Realm Reborn … 5 Dawntrail).</param>
/// <param name="Source">Where the words are from (version 2: <c>codex</c>, <c>synopsis</c>, <c>title</c>, <c>sight</c>…; empty in version 1).</param>
/// <param name="Quest">The quest (Quest sheet row) that must be finished.</param>
/// <param name="Chapter">The chapter (QuestRedoChapterUI row) that must be finished.</param>
/// <param name="Complete">The whole main story must be done.</param>
/// <param name="SpoilersOff">Only with spoilers off (the collections: cards, minions, mounts, songs).</param>
/// <param name="NeedsSeen">Shows only once its entry is seen: every version 1 layer, and a version 2 layer with no gate.</param>
public sealed record CodexLayer(
    string Text,
    IReadOnlyList<string> Needs,
    int? Expansion,
    string Source = "",
    uint? Quest = null,
    uint? Chapter = null,
    bool Complete = false,
    bool SpoilersOff = false,
    bool NeedsSeen = true);

/// <summary>
/// An entry of the Codex (<c>codex.json</c>, made from the game's files by kokos-tools): a person,
/// place, lore, word, key item, chapter or collection.
/// </summary>
public sealed record CodexEntry(
    string Key,
    string Kind,
    string Name,
    IReadOnlyList<string> Aliases,
    string? Zone,
    IReadOnlyList<CodexLayer> Layers,
    string? Whisper,
    string? Subtitle = null,
    string? Image = null)
{
    /// <summary>Every name it's seen by: its own and its aliases.</summary>
    public IEnumerable<string> Names => new[] { Name }.Concat(Aliases).Where(n => !string.IsNullOrWhiteSpace(n));
}

/// <summary>
/// Where the player is, as the Codex's gates ask it. <paramref name="QuestDone"/> answers for any quest
/// (sheet row or game id); <paramref name="ChapterDone"/> for a main-story chapter (QuestRedoChapterUI
/// row: its last quest on the player's road finished). Either may be left out: then nothing is finished.
/// </summary>
public sealed record CodexGates(
    Spoilers Spoilers,
    int ExpansionReached = 0,
    bool Complete = false,
    Func<uint, bool>? QuestDone = null,
    Func<uint, bool>? ChapterDone = null)
{
    /// <summary>Nothing reached, nothing finished: only what's been seen shows.</summary>
    public static CodexGates None(Spoilers spoilers) => new(spoilers);
}

/// <summary>
/// The Codex: the game's own words about people, places, lore, key items and chapters (kokos-tools
/// takes them from the game's files: <c>codex.json</c>), each behind the gate the game gives it, and
/// shown only as far as the player has got. An entry is seen once any of its names appears, as a whole
/// word, in what the player has seen (the journal of quests they've finished, the current quest's journal
/// so far, the people they've met, the places they've been). Reads version 1 (the hand-written Codex)
/// and version 2. Pure, and safe to share between threads (nothing in it changes once made).
/// </summary>
public sealed class Codex
{
    /// <summary>Each name, made ready for matching: its plain form, its words, and whether it's a proper noun.</summary>
    private sealed record Name(string Plain, string[] Words, bool Proper);

    private readonly Name[][] names;
    private readonly Dictionary<string, CodexEntry> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CodexEntry> byZone = new(StringComparer.OrdinalIgnoreCase);

    public Codex(IReadOnlyList<CodexEntry> entries, int fileVersion = 1)
    {
        Entries = entries;
        FileVersion = fileVersion;
        names = new Name[entries.Count][];
        var quests = new HashSet<uint>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            names[i] = e.Names.Select(n => Prepare(n)).ToArray();
            foreach (var n in names[i]) byName.TryAdd(n.Plain, e);
            if (e.Kind == "place" && !string.IsNullOrWhiteSpace(e.Zone)) byZone.TryAdd(Plain(e.Zone.Trim()), e);
            foreach (var l in e.Layers)
                if (l.Quest is { } q && q != 0) quests.Add(QuestIds.Row(q));
        }
        GateQuests = quests;
    }

    public static Codex Empty { get; } = new(Array.Empty<CodexEntry>());

    public IReadOnlyList<CodexEntry> Entries { get; }

    /// <summary>The file's version: 1 (hand-written) or 2 (from the game).</summary>
    public int FileVersion { get; }

    /// <summary>Every quest a layer waits for (sheet rows): the plugin reads which are finished.</summary>
    public IReadOnlySet<uint> GateQuests { get; }

    // ---------------------------------------------------------------------------------------------
    // Reading.

    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Reads <c>codex.json</c> (version 1 or 2); empty when it isn't one.</summary>
    public static Codex Read(string? text) => string.IsNullOrEmpty(text) ? Empty : Read(Encoding.UTF8.GetBytes(text));

    /// <summary>Reads <c>codex.json</c> from its bytes (UTF-8, a byte order mark or not); empty when it isn't one.</summary>
    public static Codex Read(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) utf8 = utf8[3..];
        try
        {
            using var doc = JsonDocument.Parse(utf8, Options);
            return Read(doc.RootElement);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    private static Codex Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Empty;
        var version = root.TryGetProperty("version", out var v) ? Long(v) ?? 1 : 1;
        if (version is not (1 or 2)) return Empty;
        if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) return Empty;

        // The same few words (kinds, sources) thousands of times: kept once each.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Pooled(string s) => pool.TryGetValue(s, out var p) ? p : pool[s] = s;

        var entries = new List<CodexEntry>(list.GetArrayLength());
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var key = Str(e, "key");
            var name = Str(e, "name");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) continue;
            var layers = new List<CodexLayer>();
            if (e.TryGetProperty("layers", out var ls) && ls.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in ls.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Object || Str(l, "text") is not { Length: > 0 } text) continue;
                    var expansion = l.TryGetProperty("expansion", out var x) && Long(x) is { } xv ? (int)Math.Clamp(xv, 0, 99) : (int?)null;
                    var needs = Strings(l, "needs");
                    if (version == 1)
                    {
                        layers.Add(new CodexLayer(text, needs, expansion));
                        continue;
                    }
                    // A gate written but not readable (0, a string, a negative number, "complete": "yes",
                    // "spoilers": "strict") keeps the layer shut to all but spoilers off, as the site's
                    // model/codex.ts does: never opened to everyone.
                    var unreadable = false;
                    var quest = Gate(l, "quest", 1, ref unreadable) is { } qv && qv <= uint.MaxValue ? QuestIds.Row((uint)qv) : (uint?)null;
                    var chapter = Gate(l, "chapter", 1, ref unreadable) is { } cv && cv <= uint.MaxValue ? (uint)cv : (uint?)null;
                    if (Gate(l, "expansion", 0, ref unreadable) is { } ev) expansion = (int)Math.Min(ev, 99);
                    else expansion = null;
                    var complete = l.TryGetProperty("complete", out var done) && done.ValueKind == JsonValueKind.True;
                    if (l.TryGetProperty("complete", out var cp) && cp.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) unreadable = true;
                    var off = Str(l, "spoilers") == "off";
                    if (l.TryGetProperty("spoilers", out var sp) && sp.ValueKind != JsonValueKind.Null && !off) unreadable = true;
                    off |= unreadable;
                    var gated = quest != null || chapter != null || expansion != null || complete || off;
                    layers.Add(new CodexLayer(text, needs, expansion, Pooled(Str(l, "source") ?? string.Empty), quest, chapter, complete, off, !gated));
                }
            }
            entries.Add(new CodexEntry(key, Pooled(Str(e, "kind") ?? "lore"), name, Strings(e, "aliases"), Str(e, "zone"), layers.Count > 0 ? layers : Array.Empty<CodexLayer>(),
                Str(e, "whisper"), Str(e, "subtitle"), Str(e, "image")));
        }
        return new Codex(entries, (int)version);
    }

    /// <summary>
    /// A version 2 gate's number, as the site reads it: an integer of at least <paramref name="least"/>. Absent
    /// or null: no gate. Anything else: no number, and <paramref name="unreadable"/> is set.
    /// </summary>
    private static long? Gate(JsonElement layer, string property, long least, ref bool unreadable)
    {
        if (!layer.TryGetProperty(property, out var g) || g.ValueKind == JsonValueKind.Null) return null;
        if (g.ValueKind == JsonValueKind.Number && g.TryGetInt64(out var n) && n >= least) return n;
        unreadable = true;
        return null;
    }

    private static string? Str(JsonElement o, string property) =>
        o.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement o, string property)
    {
        if (!o.TryGetProperty(property, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() == 0) return Array.Empty<string>();
        var list = new List<string>(a.GetArrayLength());
        foreach (var s in a.EnumerateArray())
            if (s.ValueKind == JsonValueKind.String && s.GetString() is { } t) list.Add(t);
        return list.Count > 0 ? list : Array.Empty<string>();
    }

    private static long? Long(JsonElement n) => n.ValueKind switch
    {
        JsonValueKind.Number when n.TryGetInt64(out var l) => l,
        JsonValueKind.Number when n.TryGetDouble(out var d) && double.IsFinite(d) => (long)Math.Floor(d),
        JsonValueKind.String when long.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    // ---------------------------------------------------------------------------------------------
    // Matching names.

    /// <summary>Curly apostrophes as straight ones, so "Y’shtola" and "Y'shtola" are the same name.</summary>
    public static string Plain(string text) => text.Replace('’', '\'').Replace('‘', '\'');

    /// <summary>Does the name start with a capital letter (a proper noun, matched as written)?</summary>
    public static bool ProperNoun(string name) => name.Length > 0 && char.IsUpper(name[0]);

    /// <summary>A letter or a digit (any script): what a whole word mustn't touch.</summary>
    private static bool WordChar(char c) => CharUnicodeInfo.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>The runs of letters and digits in a text.</summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var word = i < text.Length && WordChar(text[i]);
            if (word && start < 0) start = i;
            else if (!word && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }

    private static Name Prepare(string name)
    {
        var plain = Plain(name.Trim());
        var proper = ProperNoun(plain);
        var words = Words(plain).Select(w => proper ? w : w.ToLowerInvariant()).Distinct().ToArray();
        return new Name(plain, words, proper);
    }

    /// <summary>
    /// Is <paramref name="name"/> (already plain) in <paramref name="text"/> (already plain) as a whole word
    /// (no letter or digit just before or after; a name with an apostrophe or a space inside still matches
    /// whole)? A name written with a capital is a proper noun and matches only as written ("Echo", not "an
    /// echo of"); one written in lower case ("aether", "primal") in any case. The site's model/codex.ts does the same.
    /// </summary>
    private static bool Holds(string text, string name, bool proper)
    {
        if (name.Length == 0) return false;
        var how = proper ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = text.IndexOf(name, how); i >= 0; i = i + 1 < text.Length ? text.IndexOf(name, i + 1, how) : -1)
        {
            var end = i + name.Length;
            if ((i == 0 || !WordChar(text[i - 1])) && (end >= text.Length || !WordChar(text[end]))) return true;
        }
        return false;
    }

    /// <summary>
    /// Is <paramref name="name"/> in <paramref name="text"/> as a whole word: as written when it starts with a capital,
    /// in any case when it doesn't?
    /// </summary>
    public bool Mentions(string text, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var plain = Plain(name.Trim());
        return Holds(Plain(text), plain, ProperNoun(plain));
    }

    /// <summary>Is the name anywhere in what's been seen?</summary>
    public bool Seen(IReadOnlyList<string> corpus, string name) => corpus.Any(t => Mentions(t, name));

    /// <summary>
    /// The keys of every entry seen in <paramref name="corpus"/>. Thousands of names against hundreds of
    /// journals: the corpus's words are gathered once, and only a name whose every word is among them is
    /// looked for in the texts.
    /// </summary>
    public HashSet<string> SeenKeys(IReadOnlyList<string> corpus)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var texts = corpus.Select(Plain).ToList();
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in texts)
            foreach (var w in Words(t))
                if (exact.Add(w)) lower.Add(w.ToLowerInvariant());
        for (var i = 0; i < Entries.Count; i++)
        {
            foreach (var n in names[i])
            {
                var words = n.Proper ? exact : lower;
                if (!n.Words.All(words.Contains)) continue;
                if (!texts.Any(t => Holds(t, n.Plain, n.Proper))) continue;
                keys.Add(Entries[i].Key);
                break;
            }
        }
        return keys;
    }

    // ---------------------------------------------------------------------------------------------
    // What shows.

    /// <summary>Is every gate of a layer met (its entry's seen-ness aside)?</summary>
    public static bool GatesMet(CodexLayer layer, CodexGates gates)
    {
        if (gates.Spoilers == Spoilers.Off) return true;
        if (layer.SpoilersOff) return false;
        if (layer.Quest is { } q && gates.QuestDone?.Invoke(q) != true) return false;
        // A chapter: finished as the plugin can tell (a main-story chapter's last quest on the player's road);
        // a layer that also names a quest goes by that quest, which the plugin always knows.
        if (layer.Chapter is { } c && layer.Quest == null && gates.ChapterDone?.Invoke(c) != true) return false;
        if (layer.Expansion is { } x && gates.ExpansionReached < x) return false;
        if (layer.Complete && !gates.Complete) return false;
        return true;
    }

    /// <summary>
    /// Does a layer show? With spoilers off, always. Otherwise when every gate it has is met; one with no
    /// gate (and every version 1 layer) only once its entry is seen, and a version 1 layer's needs seen too.
    /// </summary>
    public bool Shows(CodexEntry entry, CodexLayer layer, IReadOnlyList<string> corpus, IReadOnlySet<string> seenKeys, CodexGates gates)
    {
        if (gates.Spoilers == Spoilers.Off) return true;
        if (!GatesMet(layer, gates)) return false;
        if (!layer.NeedsSeen) return true;
        return seenKeys.Contains(entry.Key) && layer.Needs.All(n => Seen(corpus, n));
    }

    /// <summary>The layers of an entry to show, in the file's order (<see cref="Shows"/>). Collections: none unless spoilers are off.</summary>
    public List<CodexLayer> Layers(CodexEntry entry, IReadOnlyList<string> corpus, IReadOnlySet<string> seenKeys, CodexGates gates)
    {
        if (gates.Spoilers == Spoilers.Off) return entry.Layers.ToList();
        if (entry.Kind == "collection") return new List<CodexLayer>();
        return entry.Layers.Where(l => Shows(entry, l, corpus, seenKeys, gates)).ToList();
    }

    /// <summary>
    /// Is an entry in the player's Codex? With spoilers off, every one. Otherwise once it's seen, or once a
    /// layer of it that waits for a gate (not for being seen) is open; never a collection.
    /// </summary>
    public bool Visible(CodexEntry entry, IReadOnlySet<string> seenKeys, CodexGates gates)
    {
        if (gates.Spoilers == Spoilers.Off) return true;
        if (entry.Kind == "collection") return false;
        return seenKeys.Contains(entry.Key) || entry.Layers.Any(l => !l.NeedsSeen && GatesMet(l, gates));
    }

    /// <summary>The entry named <paramref name="name"/> (its name or an alias, whatever the case), if any.</summary>
    public CodexEntry? Find(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : byName.GetValueOrDefault(Plain(name.Trim()));

    /// <summary>
    /// "Ask the journal": the entries a word typed matches (a name or alias that is it, or starts with it,
    /// or holds it), only those in the player's Codex (<see cref="Visible"/>).
    /// </summary>
    public List<CodexEntry> Ask(string term, IReadOnlySet<string> seenKeys, CodexGates gates)
    {
        var t = Plain(term.Trim());
        if (t.Length < 2) return new List<CodexEntry>();
        return Entries
            .Select((e, i) => (Entry: e, Rank: names[i].Length == 0 ? 9 : names[i].Min(n =>
                string.Equals(n.Plain, t, StringComparison.OrdinalIgnoreCase) ? 0
                : n.Plain.StartsWith(t, StringComparison.OrdinalIgnoreCase) ? 1
                : n.Plain.Contains(t, StringComparison.OrdinalIgnoreCase) ? 2 : 9)))
            .Where(x => x.Rank < 9 && Visible(x.Entry, seenKeys, gates))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>The place entry for a zone (its <c>zone</c> the game's name exactly, whatever the case).</summary>
    public CodexEntry? Place(string zone) =>
        string.IsNullOrWhiteSpace(zone) ? null : byZone.GetValueOrDefault(Plain(zone.Trim()));

    /// <summary>
    /// The arrival card's sentence for a zone: its place entry's <c>whisper</c>, else its first sight (a
    /// <c>sight</c> layer) whose gates are met; the player is there, so the place counts as seen.
    /// </summary>
    public string? Arrival(string zone, CodexGates? gates = null)
    {
        if (Place(zone) is not { } place) return null;
        if (place.Whisper is { Length: > 0 } w) return w;
        var g = gates ?? CodexGates.None(Spoilers.Strict);
        return place.Layers.FirstOrDefault(l => l.Source == "sight" && l.Needs.Count == 0 && GatesMet(l, g))?.Text;
    }

    /// <summary>The names of the people met that a text mentions (whole word), in the order they were met.</summary>
    public List<string> Named(string text, IEnumerable<string> people) => people.Where(p => Mentions(text, p)).Distinct().ToList();

    /// <summary>
    /// The person entry for someone met: by name or alias, else the first whose name holds theirs as a whole
    /// word ("Haurchefant" in "Ser Haurchefant Greystone").
    /// </summary>
    public CodexEntry? Person(string met)
    {
        if (Find(met) is { } e) return e;
        if (string.IsNullOrWhiteSpace(met)) return null;
        var plain = Plain(met.Trim());
        var proper = ProperNoun(plain);
        for (var i = 0; i < Entries.Count; i++)
            if (Entries[i].Kind == "person" && names[i].Any(n => Holds(n.Plain, plain, proper)))
                return Entries[i];
        return null;
    }
}
