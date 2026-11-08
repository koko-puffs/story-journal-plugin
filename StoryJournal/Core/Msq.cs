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
/// <param name="Order">The game's own place for it in the story (ScenarioTree's <c>Unknown2</c>: 1, 2… on its
/// path; quests that are alternatives of each other share one; 0: not known).</param>
/// <param name="StartPath">Its path (ScenarioTree's <c>Type</c>): 0 the main line; 1, 2, 3 a starting city's
/// opening (Limsa Lominsa, Gridania, Ul'dah), which comes before the main line.</param>
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
    string Place = "",
    int Order = 0,
    int StartPath = 0)
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
/// The main story, read once per game version: its quests in the story's order, its chapters and
/// expansions. The order is the game's own (<see cref="MsqQuest.Order"/>: the starting cities' openings
/// first, then the main line, each by its order, then by id) when the quests carry it, else worked out
/// from prerequisites (<see cref="GameOrder"/>). A player's own road through it (<see cref="Path"/>)
/// leaves out the openings of the other cities and the alternatives and branches they aren't on. Pure.
/// </summary>
public sealed class MsqCatalog
{
    private readonly Dictionary<uint, int> position = new();
    private readonly Dictionary<uint, MsqChapter> chapters;
    private readonly Dictionary<int, MsqExpansion> expansions;
    private readonly Dictionary<uint, List<uint>> children = new();
    private readonly Dictionary<uint, IReadOnlyList<uint>> alternatives = new();
    private readonly Dictionary<uint, int> townPaths;

    /// <param name="townPaths">The <c>Town</c> rows (a character's starting city) and the opening path each
    /// starts (<see cref="MsqQuest.StartPath"/>); a town not in it is one the catalog can't place.</param>
    public MsqCatalog(IEnumerable<MsqQuest> quests, IEnumerable<MsqChapter>? chapterRows = null, IEnumerable<MsqExpansion>? expansionRows = null,
        IReadOnlyDictionary<uint, int>? townPaths = null)
    {
        var all = new Dictionary<uint, MsqQuest>();
        foreach (var q in quests)
            if (q.Id != 0) all[q.Id] = q;
        var byPrerequisites = Sort(all);
        GameOrder = Usable(all.Values);
        Order = GameOrder ? ByGameOrder(byPrerequisites) : byPrerequisites;
        for (var i = 0; i < Order.Count; i++) position[Order[i].Id] = i;
        foreach (var q in Order)
            foreach (var p in q.Previous.Where(position.ContainsKey).Distinct())
            {
                if (!children.TryGetValue(p, out var list)) children[p] = list = new List<uint>();
                list.Add(q.Id);
            }
        if (GameOrder)
            foreach (var g in Order.Where(q => q.Order > 0).GroupBy(q => (q.StartPath, q.Order)))
            {
                if (g.Count() < 2) continue;
                var ids = g.Select(q => q.Id).OrderBy(id => id).ToList();
                foreach (var id in ids) alternatives[id] = ids;
            }
        StartPaths = Order.Where(q => q.StartPath > 0).Select(q => q.StartPath).Distinct().OrderBy(p => p).ToList();
        OpeningSize = StartPaths.Count == 0 ? 0 : StartPaths.Max(p =>
            Order.Where(q => q.StartPath == p).Select(q => Alternatives(q.Id) is { Count: > 0 } a ? a[0] : q.Id).Distinct().Count());
        chapters = (chapterRows ?? Array.Empty<MsqChapter>()).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        expansions = (expansionRows ?? Array.Empty<MsqExpansion>()).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        this.townPaths = townPaths?.Where(t => StartPaths.Contains(t.Value)).ToDictionary(t => t.Key, t => t.Value) ?? new Dictionary<uint, int>();
    }

    /// <summary>Every main-story quest in the story's order (every path and branch: the same for every player).</summary>
    public IReadOnlyList<MsqQuest> Order { get; }

    /// <summary>
    /// The order is the game's own (<see cref="MsqQuest.Order"/>); false: the quests didn't carry a usable
    /// one (none, or too few, or nearly all the same), and it was worked out from their prerequisites.
    /// </summary>
    public bool GameOrder { get; }

    /// <summary>The starting cities' opening paths there are (<see cref="MsqQuest.StartPath"/> 1, 2, 3).</summary>
    public IReadOnlyList<int> StartPaths { get; }

    /// <summary>
    /// The longest opening as a road counts it (each set of alternatives once): the indexes the openings
    /// take (0 to this less 1), so the main line's first quest has the same index whichever city began
    /// (0 when there are no openings).
    /// </summary>
    public int OpeningSize { get; }

    public int Count => Order.Count;

    public bool Contains(uint id) => position.ContainsKey(QuestIds.Row(id));

    /// <summary>
    /// A quest's place in the catalog's order, every path and branch counted (-1: not one). Not the index
    /// the site compares: that's the player's road's (<see cref="MsqPath.IndexOf"/>).
    /// </summary>
    public int Position(uint id) => position.TryGetValue(QuestIds.Row(id), out var i) ? i : -1;

    public MsqQuest? Quest(uint id) => position.TryGetValue(QuestIds.Row(id), out var i) ? Order[i] : null;

    /// <summary>The quests that share this one's place in the game's order (it among them, by id); empty when none do.</summary>
    public IReadOnlyList<uint> Alternatives(uint id) => alternatives.TryGetValue(QuestIds.Row(id), out var a) ? a : Array.Empty<uint>();

    /// <summary>The opening path a starting city (a <c>Town</c> row) begins; 0 when it isn't known.</summary>
    public int StartPathOf(uint town) => townPaths.TryGetValue(town, out var p) ? p : 0;

    public MsqChapter? Chapter(uint id) => chapters.TryGetValue(id, out var c) ? c : null;

    public string ExpansionName(int id) => expansions.TryGetValue(id, out var e) ? e.Name : $"Expansion {id + 1}";

    /// <summary>The expansions, in order: those named, and any a quest belongs to.</summary>
    public IReadOnlyList<MsqExpansion> Expansions =>
        expansions.Keys.Concat(Order.Select(q => q.Expansion)).Distinct().OrderBy(e => e).Select(e => new MsqExpansion(e, ExpansionName(e))).ToList();

    /// <summary>Quests that need this one (the chain's next steps).</summary>
    public IReadOnlyList<uint> Next(uint id) => children.TryGetValue(QuestIds.Row(id), out var c) ? c : Array.Empty<uint>();

    /// <summary>
    /// The game's order is usable: nine quests in ten or more carry one, and they don't nearly all share a
    /// few (at least half as many places as quests that carry one).
    /// </summary>
    private static bool Usable(IEnumerable<MsqQuest> quests)
    {
        var list = quests.ToList();
        var ordered = list.Where(q => q.Order > 0).ToList();
        if (ordered.Count == 0 || ordered.Count * 10 < list.Count * 9) return false;
        return ordered.Select(q => (q.StartPath, q.Order)).Distinct().Count() * 2 >= ordered.Count;
    }

    /// <summary>Where a path comes: the openings (1, 2, 3) before the main line (0).</summary>
    private static int PathRank(int path) => path > 0 ? path : int.MaxValue;

    /// <summary>
    /// The game's order: the openings, then the main line; on each, by its order, then by id. A quest
    /// without one goes just after the latest-placed quest it needs (on that quest's path), or at the
    /// start of its own path when it needs none with a place.
    /// </summary>
    private static List<MsqQuest> ByGameOrder(List<MsqQuest> byPrerequisites)
    {
        var place = new Dictionary<uint, (int Rank, int Order, int After)>();
        foreach (var q in byPrerequisites)
        {
            if (q.Order > 0)
            {
                place[q.Id] = (PathRank(q.StartPath), q.Order, 0);
                continue;
            }
            var latest = (Rank: PathRank(q.StartPath), Order: 0, After: 1);
            foreach (var p in q.Previous)
                if (place.TryGetValue(p, out var at) && (at.Rank, at.Order).CompareTo((latest.Rank, latest.Order)) > 0)
                    latest = (at.Rank, at.Order, 1);
            place[q.Id] = latest;
        }
        return byPrerequisites.OrderBy(q => place[q.Id]).ThenBy(q => q.Id).ToList();
    }

    /// <summary>
    /// The prerequisites' order: a quest after every quest it needs (those in the catalog), and among
    /// those ready at once the earlier expansion first, then the lower level, then the lower id. A loop in
    /// the prerequisites (shouldn't happen) is broken by putting what's left in that same order.
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
                if (position.ContainsKey(p)) stack.Push(p);
        }
        return seen;
    }

    /// <summary>
    /// A main-story quest's finishing as its event tells it (its index on <paramref name="path"/>, its
    /// chapter, whether it closed its chapter or expansion there), from the catalog alone: the story
    /// needn't have been read yet (just after login). Null when it isn't one of the main story's.
    /// </summary>
    public QuestFinished? Finished(uint quest, MsqPath path, IReadOnlyList<string> journal, IReadOnlyList<string> people)
    {
        if (Quest(quest) is not { } msq) return null;
        var chapter = msq.Chapter != 0 ? Chapter(msq.Chapter)?.Name : null;
        return new QuestFinished(msq.Id, msq.Name, msq.Level, true, path.IndexOf(msq.Id), msq.Expansion, chapter,
            path.ChapterDone(msq), path.ExpansionDone(msq), msq.Place, journal, people);
    }

    /// <summary>
    /// The opening the player is on: the one with the most of its quests done (on a tie, the starting
    /// city's if it's one of them, else the first); with none done, the starting city's
    /// (<paramref name="startPath"/>, from <see cref="StartPathOf"/>); else the one with the most taken; 0
    /// when it can't be told (then every opening counts).
    /// </summary>
    public int OwnStartPath(IReadOnlySet<uint> done, IReadOnlySet<uint> started, int startPath)
    {
        if (StartPaths.Count == 0) return 0;
        int Most(Func<uint, bool> counted)
        {
            var counts = Order.Where(q => q.StartPath > 0 && counted(q.Id)).GroupBy(q => q.StartPath).ToDictionary(g => g.Key, g => g.Count());
            if (counts.Count == 0) return 0;
            var most = counts.Values.Max();
            var tied = counts.Where(c => c.Value == most).Select(c => c.Key).OrderBy(p => p).ToList();
            return tied.Contains(startPath) ? startPath : tied[0];
        }
        var fromDone = Most(done.Contains);
        if (fromDone != 0) return fromDone;
        if (StartPaths.Contains(startPath)) return startPath;
        return Most(started.Contains);
    }

    /// <summary>
    /// The player's road through the story: <paramref name="done"/> the quests finished,
    /// <paramref name="started"/> those taken and not finished, <paramref name="startPath"/> the opening
    /// their starting city begins (0: not known). Left off it:
    /// <list type="bullet">
    /// <item>the other cities' openings (<see cref="OwnStartPath"/>);</item>
    /// <item>a quest not done that has a done quest after it on its chain (a branch the player went past
    /// without);</item>
    /// <item>a quest shut out by one taken (<see cref="MsqQuest.Locks"/>: another Grand Company);</item>
    /// <item>where roads meet with any one of them enough, the roads not taken once one of them is begun;</item>
    /// <item>with the game's order, of quests sharing a place (alternatives), all but the one(s) done; with
    /// none done, the one(s) taken; with none taken, the first by id whose prerequisites are on the road
    /// (else the first by id).</item>
    /// </list>
    /// </summary>
    public MsqPath Path(IReadOnlySet<uint> done, IReadOnlySet<uint>? started = null, int startPath = 0)
    {
        started ??= new HashSet<uint>();
        bool Begun(uint q) => done.Contains(q) || started.Contains(q);
        var off = new HashSet<uint>();

        var own = OwnStartPath(done, started, startPath);
        if (own != 0)
            foreach (var q in Order)
                if (q.StartPath > 0 && q.StartPath != own) off.Add(q.Id);

        // A done quest further down its chain: walked up from every done quest at once, each quest
        // reached is one with a done quest after it.
        var below = new HashSet<uint>();
        var walk = new Queue<uint>(done.Where(position.ContainsKey));
        while (walk.Count > 0)
        {
            var q = Quest(walk.Dequeue());
            if (q == null) continue;
            foreach (var p in q.Previous)
                if (position.ContainsKey(p) && below.Add(p)) walk.Enqueue(p);
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
            var roads = q.Previous.Where(position.ContainsKey).Distinct().ToList();
            if (roads.Count < 2) continue;
            var ancestry = roads.ToDictionary(r => r, Ancestry);
            var taken = roads.Where(r => ancestry[r].Any(Begun)).ToList();
            if (taken.Count == 0) continue;
            var keep = taken.SelectMany(r => ancestry[r]).ToHashSet();
            foreach (var r in roads.Except(taken))
                foreach (var a in ancestry[r])
                    if (!keep.Contains(a) && !Begun(a)) off.Add(a);
        }

        // Alternatives (quests sharing a place in the game's order), in the story's order, so a later
        // one's prerequisites are already settled.
        var settled = new HashSet<uint>();
        bool OnRoad(uint p) => done.Contains(p) || !off.Contains(p);
        bool Reachable(MsqQuest q)
        {
            var needs = q.Previous.Where(p => p != q.Id && position.ContainsKey(p)).Distinct().ToList();
            if (needs.Count == 0) return true;
            return q.AnyPrevious ? needs.Any(OnRoad) : needs.All(OnRoad);
        }
        foreach (var q in Order)
        {
            var group = Alternatives(q.Id);
            if (group.Count == 0 || !settled.Add(group[0])) continue;
            var open = group.Where(id => !off.Contains(id)).ToList();
            var keep = open.Where(done.Contains).ToList();
            if (keep.Count == 0) keep = open.Where(started.Contains).ToList();
            if (keep.Count == 0) keep = open.Where(id => Reachable(Quest(id)!)).Take(1).ToList();
            if (keep.Count == 0) keep = open.Take(1).ToList();
            foreach (var id in group)
                if (!keep.Contains(id)) off.Add(id);
        }

        return new MsqPath(this, done, off, own);
    }
}

/// <summary>A player's road through the main story, and where they are on it.</summary>
public sealed class MsqPath
{
    private readonly MsqCatalog catalog;
    private readonly IReadOnlySet<uint> done;
    private readonly HashSet<uint> off;
    private readonly int[] before;

    internal MsqPath(MsqCatalog catalog, IReadOnlySet<uint> done, HashSet<uint> off, int startPath)
    {
        this.catalog = catalog;
        this.done = done;
        this.off = off;
        StartPath = startPath;
        Quests = catalog.Order.Where(q => !off.Contains(q.Id)).ToList();
        // Each path's road quests counted on their own: an opening's from 0, the main line's from the
        // longest opening's size.
        before = new int[catalog.Order.Count];
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < before.Length; i++)
        {
            var q = catalog.Order[i];
            var path = q.StartPath > 0 ? q.StartPath : 0;
            counts.TryGetValue(path, out var count);
            before[i] = (path > 0 ? 0 : catalog.OpeningSize) + count;
            if (!off.Contains(q.Id)) counts[path] = count + 1;
        }
        MainLineIndexEnd = catalog.OpeningSize + counts.GetValueOrDefault(0);
        // An alternative off the road is where its place on the road is (the first of them: they're side by side).
        for (var i = 0; i < before.Length; i++)
        {
            var id = catalog.Order[i].Id;
            if (off.Contains(id) && catalog.Alternatives(id) is { Count: > 0 } group) before[i] = before[catalog.Position(group[0])];
        }
    }

    /// <summary>The index after the main line's last quest on the road (the finished story's).</summary>
    private int MainLineIndexEnd { get; }

    /// <summary>The starting city's opening the road takes (<see cref="MsqCatalog.OwnStartPath"/>; 0: every one, it couldn't be told).</summary>
    public int StartPath { get; }

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
    /// A quest's index (-1: not a main-story quest). In an opening: how many of that opening's quests on
    /// the road come before it (0 to <see cref="MsqCatalog.OpeningSize"/> less 1). On the main line:
    /// <see cref="MsqCatalog.OpeningSize"/> and how many main-line quests on the road come before it, so
    /// a main-line quest has the same index whichever city began. A quest off the road (an alternative
    /// not taken) has the index of its place. The same for the same quests done: what comes before the
    /// current quest is done, or settled by what's done (one of each set of alternatives).
    /// </summary>
    public int IndexOf(uint id) => catalog.Position(id) is var at && at >= 0 ? before[at] : -1;

    /// <summary>
    /// The number every spoiler check compares: the current quest's index (<see cref="IndexOf"/>); once
    /// the story is finished, the index just past the main line's last quest.
    /// </summary>
    public int Index(MsqQuest? current) => current == null ? (Complete ? MainLineIndexEnd : 0) : IndexOf(current.Id);

    /// <summary>The quests not done on the road from <paramref name="current"/> on (it first).</summary>
    public IEnumerable<MsqQuest> Remaining(MsqQuest? current)
    {
        if (current == null) yield break;
        var from = catalog.Position(current.Id);
        foreach (var q in Quests)
        {
            if (catalog.Position(q.Id) < from || done.Contains(q.Id)) continue;
            yield return q;
        }
    }

    /// <summary>The road's quest before <paramref name="current"/> (the last one finished, as a rule).</summary>
    public MsqQuest? Previous(MsqQuest? current)
    {
        if (current == null) return Quests.LastOrDefault(q => done.Contains(q.Id));
        var at = catalog.Position(current.Id);
        return Quests.LastOrDefault(q => catalog.Position(q.Id) < at && done.Contains(q.Id));
    }

    /// <summary>The road's next quest after <paramref name="current"/> (for the pace lock).</summary>
    public MsqQuest? After(MsqQuest? current)
    {
        if (current == null) return null;
        var at = catalog.Position(current.Id);
        return Quests.FirstOrDefault(q => catalog.Position(q.Id) > at && !done.Contains(q.Id));
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

/// <summary>
/// Quests finished, noticed two ways and written once: from the quest log (a quest gone from it that the
/// game says is done) and, as a backstop, from the main story's quests done, read now and then (a quest
/// taken and finished between two looks at the log is never seen in it). The first reading after login
/// is the baseline: what's done then isn't new. Pure.
/// </summary>
public sealed class Finishes
{
    private HashSet<uint>? before;
    private readonly HashSet<uint> claimed = new();

    /// <summary>A new login: the next reading is the baseline again.</summary>
    public void Reset()
    {
        before = null;
        claimed.Clear();
    }

    /// <summary>
    /// More newly done at once than anyone finishes between two readings (they're read when the log changes,
    /// and once a minute): the last reading was the game not ready yet, not quests finished. A new baseline.
    /// </summary>
    public const int MostAtOnce = 3;

    /// <summary>The quests done in this reading that weren't in the last (none for the baseline, nor past <see cref="MostAtOnce"/>).</summary>
    public List<uint> NewlyDone(IReadOnlySet<uint> done)
    {
        var now = done.Select(QuestIds.Row).ToHashSet();
        if (before == null)
        {
            before = now;
            return new List<uint>();
        }
        var list = now.Where(q => !before.Contains(q)).OrderBy(q => q).ToList();
        before = now;
        return list.Count > MostAtOnce ? new List<uint>() : list;
    }

    /// <summary>Claims a quest's finish to write it: false when it's been claimed already (the other way saw it first).</summary>
    public bool Claim(uint quest) => claimed.Add(QuestIds.Row(quest));
}
