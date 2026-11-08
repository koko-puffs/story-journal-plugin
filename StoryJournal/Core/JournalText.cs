using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace StoryJournal.Core;

/// <summary>
/// A quest's own words, from its text sheet (<c>quest/006/ManFst001_00655</c>): rows keyed
/// <c>TEXT_MANFST001_00655_SEQ_00</c> (the journal's entries, one a step) and <c>…_TODO_00</c> (the
/// objectives), the text beside each. Pure: the rows are read from the game as (key, text) pairs.
/// </summary>
public static class JournalText
{
    /// <summary>The sequence the game gives a quest whose last step is reached (ready to hand in).</summary>
    public const int Final = 255;

    private static readonly Regex Key = new(@"_(SEQ|TODO)_(\d{1,3})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    private static readonly Regex SpaceBefore = new(@"\s+([,.!?;:…])", RegexOptions.CultureInvariant);

    /// <summary>"SEQ" or "TODO" and the number of a row's key; null when it's neither.</summary>
    public static (string Kind, int Number)? Parse(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var m = Key.Match(key.Trim());
        if (!m.Success) return null;
        return (m.Groups[1].Value.ToUpperInvariant(), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A line tidied: runs of spaces and line breaks as one space, no space before punctuation (where a
    /// name the game fills in was left out), trimmed.
    /// </summary>
    public static string Tidy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = Spaces.Replace(text, " ").Trim();
        return SpaceBefore.Replace(s, "$1");
    }

    /// <summary>Every journal entry, in order of its number (empty ones left out).</summary>
    public static List<(int Number, string Text)> Entries(IEnumerable<(string Key, string Text)> rows, string kind = "SEQ")
    {
        var list = new List<(int, string)>();
        foreach (var (key, text) in rows)
        {
            if (Parse(key) is not { } k || k.Kind != kind) continue;
            var t = Tidy(text);
            if (t.Length > 0) list.Add((k.Number, t));
        }
        return list.OrderBy(e => e.Item1).GroupBy(e => e.Item1).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// The journal as far as the quest's <paramref name="sequence"/>: the entries numbered below it (the
    /// game's journal shows one entry per step reached; the first as soon as the quest's taken), all of
    /// them once it's finished or at its last step (<see cref="Final"/>).
    /// </summary>
    public static List<string> Journal(IEnumerable<(string Key, string Text)> rows, int sequence)
    {
        var entries = Entries(rows);
        if (sequence >= Final) return entries.Select(e => e.Text).ToList();
        var upTo = Math.Max(1, sequence);
        return entries.Where(e => e.Number < upTo).Select(e => e.Text).ToList();
    }

    /// <summary>The whole journal of a finished quest.</summary>
    public static List<string> Whole(IEnumerable<(string Key, string Text)> rows) => Journal(rows, Final);

    /// <summary>
    /// The objectives for the step the quest is on: the to-do lines whose step (the Quest sheet's
    /// <c>ToDoCompleteSeq</c>, one per to-do, in order) is <paramref name="sequence"/>.
    /// </summary>
    public static List<string> Objectives(IEnumerable<(string Key, string Text)> rows, IReadOnlyList<int> todoSequences, int sequence)
    {
        var todos = Entries(rows, "TODO");
        var list = new List<string>();
        foreach (var (n, text) in todos)
            if (n < todoSequences.Count && todoSequences[n] == sequence) list.Add(text);
        return list;
    }

    /// <summary>The to-do whose step is <paramref name="sequence"/> (for the map flag): its index, or -1.</summary>
    public static int TodoFor(IReadOnlyList<int> todoSequences, int sequence)
    {
        for (var i = 0; i < todoSequences.Count; i++)
            if (todoSequences[i] == sequence) return i;
        return -1;
    }
}
