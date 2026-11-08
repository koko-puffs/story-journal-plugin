using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StoryJournal.Core;

/// <summary>A layer of a Codex entry: shown once its entry is seen, every name in <see cref="Needs"/> is seen, and the expansion's reached.</summary>
public sealed record CodexLayer(string Text, IReadOnlyList<string> Needs, int? Expansion);

/// <summary>An entry of the Codex (<c>codex.json</c>, shared with the site): a person, place, lore or word.</summary>
public sealed record CodexEntry(string Key, string Kind, string Name, IReadOnlyList<string> Aliases, string? Zone, IReadOnlyList<CodexLayer> Layers, string? Whisper)
{
    /// <summary>Every name it's seen by: its own and its aliases.</summary>
    public IEnumerable<string> Names => new[] { Name }.Concat(Aliases).Where(n => !string.IsNullOrWhiteSpace(n));
}

/// <summary>
/// The Codex: the site's hand-written words about people, places and lore, unlocked only as far as
/// the player has seen. An entry is seen once any of its names appears, as a whole word whatever its
/// case, in what the player has seen (the journal of quests they've finished, the current quest's
/// journal so far, the people they've met, the places they've been). Pure, and safe to share between threads.
/// </summary>
public sealed class Codex
{
    /// <summary>Each name's pattern, made once (the framework thread and the story's worker both ask).</summary>
    private readonly ConcurrentDictionary<string, Regex> patterns = new(StringComparer.OrdinalIgnoreCase);

    public Codex(IReadOnlyList<CodexEntry> entries) => Entries = entries;

    public static Codex Empty { get; } = new(Array.Empty<CodexEntry>());

    public IReadOnlyList<CodexEntry> Entries { get; }

    /// <summary>Reads <c>codex.json</c> (<c>{ version: 1, entries: [...] }</c>); empty when it isn't one.</summary>
    public static Codex Read(string? text)
    {
        if (Json.Parse(text) is not JsonObject o) return Empty;
        var entries = new List<CodexEntry>();
        foreach (var e in Json.Objects(o["entries"]))
        {
            var key = Json.Str(e["key"]);
            var name = Json.Str(e["name"]);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name)) continue;
            var layers = Json.Objects(e["layers"])
                .Select(l => new CodexLayer(Json.Str(l["text"], string.Empty), Json.Strings(l["needs"]), Json.Long(l["expansion"]) is { } x ? (int)x : null))
                .Where(l => l.Text.Length > 0)
                .ToList();
            entries.Add(new CodexEntry(key, Json.Str(e["kind"], "lore"), name, Json.Strings(e["aliases"]), Json.Str(e["zone"]), layers, Json.Str(e["whisper"])));
        }
        return new Codex(entries);
    }

    /// <summary>Curly apostrophes as straight ones, so "Y’shtola" and "Y'shtola" are the same name.</summary>
    public static string Plain(string text) => text.Replace('’', '\'').Replace('‘', '\'');

    private Regex Pattern(string name)
    {
        if (patterns.TryGetValue(name, out var r)) return r;
        // Whole word: no letter or digit just before or after (a name with an apostrophe or a space inside still matches whole).
        var escaped = Regex.Escape(Plain(name.Trim()));
        r = new Regex($@"(?<![\p{{L}}\p{{N}}]){escaped}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (patterns.Count > 4000) patterns.Clear();
        return patterns[name] = r;
    }

    /// <summary>Is <paramref name="name"/> in <paramref name="text"/> as a whole word, whatever its case?</summary>
    public bool Mentions(string text, string name) => !string.IsNullOrWhiteSpace(name) && Pattern(name).IsMatch(Plain(text));

    /// <summary>Is the name anywhere in what's been seen?</summary>
    public bool Seen(IReadOnlyList<string> corpus, string name) => corpus.Any(t => Mentions(t, name));

    /// <summary>The keys of every entry seen in <paramref name="corpus"/>.</summary>
    public HashSet<string> SeenKeys(IReadOnlyList<string> corpus)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
            if (e.Names.Any(n => Seen(corpus, n))) keys.Add(e.Key);
        return keys;
    }

    /// <summary>
    /// The layers of an entry to show: none unless it's seen; then each whose needs are all seen and whose
    /// expansion is reached. With spoilers off, every layer.
    /// </summary>
    public List<CodexLayer> Layers(CodexEntry entry, IReadOnlyList<string> corpus, IReadOnlySet<string> seenKeys, int expansionReached, Spoilers spoilers)
    {
        if (spoilers == Spoilers.Off) return entry.Layers.ToList();
        if (!seenKeys.Contains(entry.Key)) return new List<CodexLayer>();
        return entry.Layers.Where(l => l.Needs.All(n => Seen(corpus, n)) && (l.Expansion is not { } x || expansionReached >= x)).ToList();
    }

    /// <summary>The entry named <paramref name="name"/> (its name or an alias, whatever the case), if any.</summary>
    public CodexEntry? Find(string name)
    {
        var plain = Plain(name.Trim());
        return Entries.FirstOrDefault(e => e.Names.Any(n => string.Equals(Plain(n), plain, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// "Ask the journal": the entries a word typed matches (a name or alias that is it, or starts with it),
    /// only those seen unless spoilers are off.
    /// </summary>
    public List<CodexEntry> Ask(string term, IReadOnlySet<string> seenKeys, Spoilers spoilers)
    {
        var t = Plain(term.Trim());
        if (t.Length < 2) return new List<CodexEntry>();
        return Entries
            .Where(e => spoilers == Spoilers.Off || seenKeys.Contains(e.Key))
            .Select(e => (Entry: e, Rank: e.Names.Select(n => Plain(n)).Min(n =>
                string.Equals(n, t, StringComparison.OrdinalIgnoreCase) ? 0
                : n.StartsWith(t, StringComparison.OrdinalIgnoreCase) ? 1
                : n.Contains(t, StringComparison.OrdinalIgnoreCase) ? 2 : 9)))
            .Where(x => x.Rank < 9)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>The place entry for a zone (its <c>zone</c> the game's name exactly, whatever the case).</summary>
    public CodexEntry? Place(string zone) =>
        string.IsNullOrWhiteSpace(zone) ? null
        : Entries.FirstOrDefault(e => e.Kind == "place" && e.Zone != null && string.Equals(Plain(e.Zone), Plain(zone.Trim()), StringComparison.OrdinalIgnoreCase));

    /// <summary>The arrival card's sentence for a zone, when its entry has one.</summary>
    public string? Arrival(string zone) => Place(zone)?.Whisper is { Length: > 0 } w ? w : null;

    /// <summary>The names of the people met that a text mentions (whole word), in the order they were met.</summary>
    public List<string> Named(string text, IEnumerable<string> people) => people.Where(p => Mentions(text, p)).Distinct().ToList();
}
