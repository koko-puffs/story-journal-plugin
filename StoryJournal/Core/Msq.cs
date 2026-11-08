using System;
using System.Collections.Generic;
using System.Linq;

namespace StoryJournal.Core;

/// <summary>
/// A main-story quest as the catalog knows it, read from the game's sheets (Game/MsqReader) into plain
/// values so the story's order and counts are worked out (and tested) without the game.
/// </summary>
/// <param name="Id">The Quest sheet's row (65536 and up).</param>
/// <param name="Previous">Quests it needs first (only those in the catalog count).</param>
/// <param name="AnyPrevious">Any one of <paramref name="Previous"/> will do (the starting cities' three roads meet so).</param>
/// <param name="Locks">Quests that shut this one out once taken (the Grand Companies' choice).</param>
/// <param name="Chapter">Its chapter's id (0: none known).</param>
/// <param name="Duty">The duty it unlocks (a ContentFinderCondition row; 0: none).</param>
public sealed record MsqQuest(
    uint Id,
    string Name,
    int Level,
    int Expansion,
    IReadOnlyList<uint> Previous,
    bool AnyPrevious = false,
    IReadOnlyList<uint>? Locks = null,
    uint Chapter = 0,
    uint Duty = 0,
    string? DutyName = null,
    string? DutyType = null,
    int DutyLevel = 0,
    string Place = "")
{
    public IReadOnlyList<uint> LockedBy => Locks ?? Array.Empty<uint>();
}

/// <summary>A chapter of the story (New Game+'s chapters: <c>QuestRedoChapterUI</c>).</summary>
public sealed record MsqChapter(uint Id, string Name, int Expansion);

/// <summary>An expansion (<c>ExVersion</c>): 0 A Realm Reborn, 1 Heavensward…</summary>
public sealed record MsqExpansion(int Id, string Name);

/// <summary>Quest ids as the game's two kinds of address give them.</summary>
public static class QuestIds
{
    /// <summary>The Quest sheet's rows start here; the game's ushort ids are the row less this.</summary>
    public const uint Offset = 65536;

    /// <summary>A quest's sheet row from either form (0 stays 0: no quest).</summary>
    public static uint Row(uint id) => id == 0 ? 0 : id < Offset ? id + Offset : id;

    /// <summary>A quest's ushort id, as QuestManager and the scenario tree take it.</summary>
    public static ushort Short(uint id) => (ushort)(id >= Offset ? id - Offset : id);

    /// <summary>The text sheet of a quest's journal: "quest/" + the first three digits of the number ending its Id + "/" + Id.</summary>
    public static string? TextSheet(string? questKey)
    {
        if (string.IsNullOrEmpty(questKey)) return null;
        var i = questKey.LastIndexOf('_');
        if (i < 0 || questKey.Length - i - 1 < 3) return null;
        var digits = questKey[(i + 1)..];
        if (!digits.All(char.IsAsciiDigit)) return null;
        return $"quest/{digits[..3]}/{questKey}";
    }
}

/// <summary>
/// The main story, read once per game version: its quests in the story's order (each after the quests
/// it needs, then by expansion, level and id), its chapters and expansions. A player's own road
/// through it (<see cref="Path"/>) leaves out the branches they can't be on. Pure.
/// </summary>
public sealed class MsqCatalog
{
    private readonly Dictionary<uint, int> index = new();
    private readonly Dictionary<uint, MsqChapter> chapters;
    private readonly Dictionary<int, MsqExpansion> expansions;
    private readonly Dictionary<uint, List<uint>> children = new();

    public MsqCatalog(IEnumerable<MsqQuest> quests, IEnumerable<MsqChapter>? chapterRows = null, IEnumerable<MsqExpansion>? expansionRows = null)
    {
        var all = new Dictionary<uint, MsqQuest>();
        foreach (var q in quests)
            if (q.Id != 0) all[q.Id] = q;
        Order = Sort(all);
        for (var i = 0; i < Order.Count; i++) index[Order[i].Id] = i;
        foreach (var q in Order)
            foreach (var p in q.Previous.Where(index.ContainsKey).Distinct())
            {
                if (!children.TryGetValue(p, out var list)) children[p] = list = new List<uint>();
                list.Add(q.Id);
            }
        chapters = (chapterRows ?? Array.Empty<MsqChapter>()).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        expansions = (expansionRows ?? Array.Empty<MsqExpansion>()).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Every main-story quest in the story's order (every branch: the same for every player).</summary>
    public IReadOnlyList<MsqQuest> Order { get; }

    public int Count => Order.Count;

    public bool Contains(uint id) => index.ContainsKey(QuestIds.Row(id));

    /// <summary>A quest's place in the story's order: how many main-story quests come before it (-1: not one).</summary>
    public int IndexOf(uint id) => index.TryGetValue(QuestIds.Row(id), out var i) ? i : -1;

    public MsqQuest? Quest(uint id) => index.TryGetValue(QuestIds.Row(id), out var i) ? Order[i] : null;

    public MsqChapter? Chapter(uint id) => chapters.TryGetValue(id, out var c) ? c : null;

    public string ExpansionName(int id) => expansions.TryGetValue(id, out var e) ? e.Name : $"Expansion {id + 1}";

    /// <summary>The expansions, in order: those named, and any a quest belongs to.</summary>
    public IReadOnlyList<MsqExpansion> Expansions =>
        expansions.Keys.Concat(Order.Select(q => q.Expansion)).Distinct().OrderBy(e => e).Select(e => new MsqExpansion(e, ExpansionName(e))).ToList();

    /// <summary>Quests that need this one (the chain's next steps).</summary>
    public IReadOnlyList<uint> Next(uint id) => children.TryGetValue(QuestIds.Row(id), out var c) ? c : Array.Empty<uint>();

    /// <summary>
    /// The story's order: a quest after every quest it needs (those in the catalog), and among those ready
    /// at once the earlier expansion first, then the lower level, then the lower id. A loop in the
    /// prerequisites (shouldn't happen) is broken by putting what's left in that same order.
    /// </summary>
    private static List<MsqQuest> Sort(Dictionary<uint, MsqQuest> all)
    {
        static (int, int, uint) Key(MsqQuest q) => (q.Expansion, q.Level, q.Id);
        var waiting = new Dictionary<uint, int>();
        var after = new Dictionary<uint, List<uint>>();
        foreach (var q in all.Values)
        {
            var prereqs = q.Previous.Where(p => p != q.Id && all.ContainsKey(p)).Distinct().ToList();
            waiting[q.Id] = q.AnyPrevious && prereqs.Count > 0 ? 1 : prereqs.Count;
            foreach (var p in prereqs)
            {
                if (!after.TryGetValue(p, out var list)) after[p] = list = new List<uint>();
                list.Add(q.Id);
            }
        }
        var ready = new SortedSet<(int, int, uint)>(all.Values.Where(q => waiting[q.Id] == 0).Select(Key));
        var order = new List<MsqQuest>(all.Count);
        var placed = new HashSet<uint>();
        while (order.Count < all.Count)
        {
            if (ready.Count == 0)
            {
                // A loop: the first of what's left goes next.
                var rest = all.Values.Where(q => !placed.Contains(q.Id)).OrderBy(Key).First();
                ready.Add(Key(rest));
            }
            var key = ready.Min;
            ready.Remove(key);
            var quest = all[key.Item3];
            if (!placed.Add(quest.Id)) continue;
            order.Add(quest);
            if (!after.TryGetValue(quest.Id, out var next)) continue;
            foreach (var n in next)
            {
                if (placed.Contains(n) || waiting[n] <= 0) continue;
                waiting[n]--;
                if (waiting[n] == 0) ready.Add(Key(all[n]));
            }
        }
        return order;
    }

    /// <summary>All the quests a quest needs, however far back (itself included).</summary>
    private HashSet<uint> Ancestry(uint id)
    {
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var q = stack.Pop();
            if (!seen.Add(q) || Quest(q) is not { } quest) continue;
            foreach (var p in quest.Previous)
                if (index.ContainsKey(p)) stack.Push(p);
        }
        return seen;
    }

    /// <summary>
    /// The player's road through the story: <paramref name="done"/> the quests finished,
    /// <paramref name="started"/> those taken and not finished. Left off it: a quest not done that has
    /// a done quest after it on its chain (a branch the player went past without: another starting
    /// city's road); a quest shut out by one taken (<see cref="MsqQuest.Locks"/>: another Grand Company);
    /// and, where roads meet with any one of them enough, the roads not taken once one of them is begun.
    /// </summary>
    public MsqPath Path(IReadOnlySet<uint> done, IReadOnlySet<uint>? started = null)
    {
        started ??= new HashSet<uint>();
        bool Begun(uint q) => done.Contains(q) || started.Contains(q);
        var off = new HashSet<uint>();

        // A done quest further down its chain: walked up from every done quest at once, each quest
        // reached is one with a done quest after it.
        var below = new HashSet<uint>();
        var walk = new Queue<uint>(done.Where(index.ContainsKey));
        while (walk.Count > 0)
        {
            var q = Quest(walk.Dequeue());
            if (q == null) continue;
            foreach (var p in q.Previous)
                if (index.ContainsKey(p) && below.Add(p)) walk.Enqueue(p);
        }
        foreach (var id in below)
            if (!done.Contains(id)) off.Add(id);

        foreach (var q in Order)
        {
            if (done.Contains(q.Id)) continue;
            if (q.LockedBy.Any(Begun)) off.Add(q.Id);
        }

        // Roads that meet where any one will do: once one is begun, the others' own quests are off.
        foreach (var q in Order)
        {
            if (!q.AnyPrevious) continue;
            var roads = q.Previous.Where(index.ContainsKey).Distinct().ToList();
            if (roads.Count < 2) continue;
            var ancestry = roads.ToDictionary(r => r, Ancestry);
            var taken = roads.Where(r => ancestry[r].Any(Begun)).ToList();
            if (taken.Count == 0) continue;
            var keep = taken.SelectMany(r => ancestry[r]).ToHashSet();
            foreach (var r in roads.Except(taken))
                foreach (var a in ancestry[r])
                    if (!keep.Contains(a) && !Begun(a)) off.Add(a);
        }

        return new MsqPath(this, done, off);
    }
}

/// <summary>A player's road through the main story, and where they are on it.</summary>
public sealed class MsqPath
{
    private readonly MsqCatalog catalog;
    private readonly IReadOnlySet<uint> done;
    private readonly HashSet<uint> off;

    internal MsqPath(MsqCatalog catalog, IReadOnlySet<uint> done, HashSet<uint> off)
    {
        this.catalog = catalog;
        this.done = done;
        this.off = off;
        Quests = catalog.Order.Where(q => !off.Contains(q.Id)).ToList();
    }

    /// <summary>The quests on the player's road, in the story's order.</summary>
    public IReadOnlyList<MsqQuest> Quests { get; }

    public bool OnPath(uint id) => catalog.Contains(id) && !off.Contains(QuestIds.Row(id));

    public bool IsDone(uint id) => done.Contains(QuestIds.Row(id));

    public int Total => Quests.Count;

    public int Done => Quests.Count(q => done.Contains(q.Id));

    /// <summary>Every quest on the road is done.</summary>
    public bool Complete => Quests.Count > 0 && Quests.All(q => done.Contains(q.Id));

    /// <summary>
    /// The quest the player is on: the game's own (the scenario tree's) when it's on the road and not
    /// done, else the first quest on the road not done; null when the story is finished.
    /// </summary>
    public MsqQuest? Current(uint gameCurrent)
    {
        if (gameCurrent != 0 && catalog.Quest(gameCurrent) is { } q && !done.Contains(q.Id)) return q;
        return Quests.FirstOrDefault(x => !done.Contains(x.Id));
    }

    /// <summary>
    /// The number every spoiler check compares: how many main-story quests come before the current one in
    /// the story's order (the same quest gives every player the same number); every quest's count once
    /// the story is finished.
    /// </summary>
    public int Index(MsqQuest? current) => current == null ? (Complete ? catalog.Count : 0) : catalog.IndexOf(current.Id);

    /// <summary>The quests not done on the road from <paramref name="current"/> on (it first).</summary>
    public IEnumerable<MsqQuest> Remaining(MsqQuest? current)
    {
        if (current == null) yield break;
        var from = catalog.IndexOf(current.Id);
        foreach (var q in Quests)
        {
            if (catalog.IndexOf(q.Id) < from || done.Contains(q.Id)) continue;
            yield return q;
        }
    }

    /// <summary>The road's quest before <paramref name="current"/> (the last one finished, as a rule).</summary>
    public MsqQuest? Previous(MsqQuest? current)
    {
        if (current == null) return Quests.LastOrDefault(q => done.Contains(q.Id));
        var at = catalog.IndexOf(current.Id);
        return Quests.LastOrDefault(q => catalog.IndexOf(q.Id) < at && done.Contains(q.Id));
    }

    /// <summary>The road's next quest after <paramref name="current"/> (for the pace lock).</summary>
    public MsqQuest? After(MsqQuest? current)
    {
        if (current == null) return null;
        var at = catalog.IndexOf(current.Id);
        return Quests.FirstOrDefault(q => catalog.IndexOf(q.Id) > at && !done.Contains(q.Id));
    }

    /// <summary>How far through each expansion: every expansion, those not reached with nothing done.</summary>
    public List<ExpansionCount> Expansions()
    {
        var list = new List<ExpansionCount>();
        foreach (var e in catalog.Expansions)
        {
            var quests = Quests.Where(q => q.Expansion == e.Id).ToList();
            if (quests.Count == 0) continue;
            list.Add(new ExpansionCount(e.Id, e.Name, quests.Count(q => done.Contains(q.Id)), quests.Count));
        }
        return list;
    }

    /// <summary>
    /// How far through each chapter, in the story's order. A chapter's name only once it's reached (a
    /// quest in it done, or the current one): a chapter's name ahead is a spoiler.
    /// </summary>
    public List<ChapterCount> Chapters(MsqQuest? current)
    {
        var list = new List<ChapterCount>();
        var seen = new Dictionary<uint, int>();
        foreach (var q in Quests)
        {
            if (q.Chapter == 0) continue;
            if (!seen.TryGetValue(q.Chapter, out var i))
            {
                var info = catalog.Chapter(q.Chapter);
                seen[q.Chapter] = i = list.Count;
                list.Add(new ChapterCount(q.Chapter, info?.Name, info?.Expansion ?? q.Expansion, 0, 0, false));
            }
            var c = list[i];
            var isDone = done.Contains(q.Id);
            list[i] = c with
            {
                Done = c.Done + (isDone ? 1 : 0),
                Total = c.Total + 1,
                Reached = c.Reached || isDone || q.Id == current?.Id,
            };
        }
        return list.Select(c => c.Reached ? c : c with { Name = null }).ToList();
    }

    /// <summary>
    /// Finishing <paramref name="quest"/> closed its chapter: every quest of the chapter on the road is
    /// done (it counted as done).
    /// </summary>
    public bool ChapterDone(MsqQuest quest) =>
        quest.Chapter != 0 && Quests.Where(q => q.Chapter == quest.Chapter).All(q => q.Id == quest.Id || done.Contains(q.Id));

    /// <summary>Finishing <paramref name="quest"/> finished its expansion's story.</summary>
    public bool ExpansionDone(MsqQuest quest) =>
        Quests.Where(q => q.Expansion == quest.Expansion).All(q => q.Id == quest.Id || done.Contains(q.Id));

    /// <summary>The next duties (at most <paramref name="max"/>) the story unlocks from <paramref name="current"/> on.</summary>
    public List<Ahead> Ahead(MsqQuest? current, int max = 3)
    {
        var list = new List<Ahead>();
        var away = 0;
        foreach (var q in Remaining(current))
        {
            away++;
            if (q.Duty == 0 || string.IsNullOrWhiteSpace(q.DutyName)) continue;
            list.Add(new Ahead(away, q.DutyLevel, q.DutyName!, q.DutyType ?? "duty"));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>
    /// A good place to stop: the end of the current chapter, or just before the next duty, whichever comes
    /// first (the chapter's end when they're the same quest); null when neither is ahead.
    /// </summary>
    public NextStop? Stop(MsqQuest? current)
    {
        if (current == null) return null;
        int? chapterEnd = null, duty = null;
        var away = 0;
        foreach (var q in Remaining(current))
        {
            away++;
            if (current.Chapter != 0 && q.Chapter == current.Chapter) chapterEnd = away;
            if (duty == null && q.Duty != 0) duty = away;
        }
        if (chapterEnd == null && duty == null) return null;
        if (duty == null || (chapterEnd != null && chapterEnd <= duty)) return new NextStop(chapterEnd!.Value, "chapter");
        return new NextStop(duty.Value, "duty");
    }
}

public sealed record ExpansionCount(int Id, string Name, int Done, int Total);

public sealed record ChapterCount(uint Id, string? Name, int Expansion, int Done, int Total, bool Reached);

/// <summary>A duty ahead: <see cref="QuestsAway"/> counts the current quest as 1.</summary>
public sealed record Ahead(int QuestsAway, int Level, string Duty, string Type);

/// <summary>A good place to stop: after <see cref="QuestsAway"/> quests; <see cref="Reason"/> "duty" or "chapter".</summary>
public sealed record NextStop(int QuestsAway, string Reason);
