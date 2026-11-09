using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StoryJournal.Core;
using StoryJournal.Game;

namespace StoryJournal.Session;

/// <summary>
/// Where the player is in the main story, worked out on a worker from what the framework thread read:
/// their road (<see cref="MsqPath"/>), the current quest with its journal so far and its objectives, the
/// last entry of the quest before, the people met who are in it, the reading the site keeps
/// (<see cref="Progress"/>), and what the Codex may show (the entries seen).
/// </summary>
public sealed record StoryState(
    MsqCatalog Catalog,
    MsqPath Path,
    MsqQuest? Current,
    bool CurrentTaken,
    int Sequence,
    IReadOnlyList<string> Journal,
    IReadOnlyList<string> Objectives,
    string? PreviousLine,
    IReadOnlyList<string> People,
    Progress Progress,
    string ProgressJson,
    IReadOnlyList<string> Corpus,
    IReadOnlySet<string> Seen,
    int? NextIndex,
    IReadOnlySet<uint> QuestsDone)
{
    /// <summary>The expansion reached (the current quest's, else the last with anything done).</summary>
    public int ExpansionReached => Progress.ExpansionNow?.Id ?? 0;

    /// <summary>
    /// The Codex's gates as this reading answers them: the quests finished (the main story's and those the
    /// Codex waits for), a main-story chapter finished (its last quest on the road), the expansion reached,
    /// the main story done.
    /// </summary>
    public CodexGates Gates(Spoilers spoilers) =>
        new(spoilers, ExpansionReached, Progress.Complete, id => QuestsDone.Contains(QuestIds.Row(id)), Path.ChapterFinished);
}

/// <summary>What the worker is given: the framework thread's reading of the game, copied.</summary>
internal sealed record StoryInputs(
    IReadOnlySet<uint> Done,
    IReadOnlyDictionary<uint, int> Accepted,
    uint ScenarioCurrent,
    uint StartTown,
    int Level,
    string Job,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Places,
    Codex Codex,
    IReadOnlySet<uint> GateDone);

/// <summary>
/// The main story, kept up to date: the catalog read once from the sheets when the plugin loads (on a
/// worker), the main story's quests done read from the game (framework thread, cheap: a bit each), and
/// <see cref="StoryState"/> worked out again on a worker whenever any of it changes. Quests' text is
/// read from the sheets on the worker and kept.
/// </summary>
public sealed class Story : IDisposable
{
    private readonly Task<MsqCatalog?> building;
    private readonly ConcurrentQueue<(int Generation, StoryState State)> results = new();
    private readonly ConcurrentDictionary<uint, string> wholeJournal = new();
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopping;
    private Task? refreshing;
    private int generation;
    private string signature = string.Empty;
    private HashSet<uint> done = new();
    private HashSet<uint> gateDone = new();
    private int gateVersion = -1;
    private DateTime doneReadAt = DateTime.MinValue;
    private bool doneStale = true;

    public Story()
    {
        stopping = stop.Token;
        building = Task.Run(() =>
        {
            try
            {
                return MsqReader.Build();
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal couldn't read the main story from the game's sheets");
                return (MsqCatalog?)null;
            }
        });
    }

    /// <summary>The main story's catalog, once read (null until then, or if it couldn't be).</summary>
    public MsqCatalog? Catalog => building.IsCompletedSuccessfully ? building.Result : null;

    /// <summary>The catalog couldn't be read (the window says so).</summary>
    public bool Broken => building.IsCompleted && Catalog == null;

    /// <summary>The story now (null until the first reading).</summary>
    public StoryState? Now { get; private set; }

    /// <summary>Goes up with each new reading.</summary>
    public int Version { get; private set; }

    /// <summary>The main story's quests done, as last read.</summary>
    public IReadOnlySet<uint> Done => done;

    /// <summary>Goes up each time which quests are done is read (the recorder's backstop for finishes it missed).</summary>
    public int DoneVersion { get; private set; }

    /// <summary>Another character: the reading under way and the last are let go (they were the other's).</summary>
    public void Reset()
    {
        generation++;
        while (results.TryDequeue(out _))
        {
        }
        Now = null;
        Version++;
        signature = string.Empty;
        done = new HashSet<uint>();
        gateDone = new HashSet<uint>();
        doneStale = true;
    }

    /// <summary>A quest was finished or the log changed: read which are done again.</summary>
    public void DoneChanged() => doneStale = true;

    /// <summary>
    /// Framework thread, about once a second: takes in a finished reading, reads which main-story quests
    /// are done (when that may have changed, and once a minute anyway), and starts a new reading when
    /// anything it depends on changed.
    /// </summary>
    public void Tick(DateTime now, PlayerNow player, IReadOnlyDictionary<uint, int> accepted, IReadOnlyList<string> people, IReadOnlyList<string> places, Codex codex, int codexVersion)
    {
        while (results.TryDequeue(out var reading))
        {
            if (reading.Generation != generation) continue;
            Now = reading.State;
            Version++;
        }
        if (Catalog is not { } catalog) return;
        if (doneStale || now - doneReadAt > TimeSpan.FromMinutes(1) || codexVersion != gateVersion)
        {
            done = Quests.Done(catalog.Order.Select(q => q.Id));
            // The quests the Codex's layers wait for (side quests among them): a bit each, as the main story's.
            gateDone = Quests.Done(codex.GateQuests.Where(q => !catalog.Contains(q)));
            gateVersion = codexVersion;
            doneReadAt = now;
            doneStale = false;
            DoneVersion++;
        }
        var (scenario, _) = Quests.Scenario();
        var sig = string.Join('|', done.Count, done.Sum(d => (long)d), gateDone.Count, gateDone.Sum(d => (long)d), string.Join(',', accepted.OrderBy(a => a.Key).Select(a => $"{a.Key}:{a.Value}")),
            scenario, player.StartTown, player.Level, player.Job, people.Count, places.Count, codexVersion);
        if (sig == signature || refreshing is { IsCompleted: false }) return;
        signature = sig;
        var inputs = new StoryInputs(done.ToHashSet(), new Dictionary<uint, int>(accepted), scenario, player.StartTown, player.Level, player.Job, people.ToList(), places.ToList(), codex, gateDone.ToHashSet());
        var gen = generation;
        refreshing = Task.Run(() =>
        {
            try
            {
                if (!stopping.IsCancellationRequested) results.Enqueue((gen, Read(catalog, inputs)));
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal couldn't work out where you are in the story");
            }
        });
    }

    /// <summary>Asks for a new reading at the next tick (something the signature doesn't see changed).</summary>
    public void Refresh() => signature = string.Empty;

    /// <summary>A finished quest's whole journal, joined (read once and kept).</summary>
    public string WholeJournal(uint quest) => wholeJournal.GetOrAdd(QuestIds.Row(quest), q => string.Join("\n", JournalText.Whole(JournalReader.Rows(q))));

    private StoryState Read(MsqCatalog catalog, StoryInputs i)
    {
        var started = i.Accepted.Keys.ToHashSet();
        var path = catalog.Path(i.Done, started, catalog.StartPathOf(i.StartTown));
        var current = path.Current(i.ScenarioCurrent);
        var taken = current != null && i.Accepted.ContainsKey(current.Id);
        var sequence = current != null && i.Accepted.TryGetValue(current.Id, out var s) ? s : 0;
        IReadOnlyList<string> journal = Array.Empty<string>();
        IReadOnlyList<string> objectives = Array.Empty<string>();
        MapSpot? spot = null;
        if (current != null && taken)
        {
            spot = MapFlag.Spot(current.Id, sequence);
            var rows = JournalReader.Rows(current.Id);
            var steps = JournalReader.TodoSteps(current.Id);
            journal = JournalText.Journal(rows, sequence, steps);
            objectives = JournalText.Objectives(rows, steps, sequence);
        }
        var previous = path.Previous(current);
        string? previousLine = null;
        if (previous != null)
        {
            var whole = WholeJournal(previous.Id);
            previousLine = whole.Length > 0 ? whole.Split('\n')[^1] : null;
        }
        var people = i.Codex.Named(string.Join("\n", journal), i.People);
        var progress = Progress.Read(catalog, path, current, i.Level, i.Job, sequence, journal, objectives, previousLine, people, spot);
        var json = progress.ToSite().ToJsonString(Json.Compact);

        // What the player has seen: the journal of every main-story quest they've finished, the current
        // quest's so far, the people met and the places been.
        var corpus = new List<string>();
        foreach (var q in path.Quests)
        {
            if (stopping.IsCancellationRequested) break;
            if (!i.Done.Contains(q.Id)) continue;
            var text = WholeJournal(q.Id);
            if (text.Length > 0) corpus.Add(q.Name + "\n" + text);
        }
        if (journal.Count > 0) corpus.Add(string.Join("\n", journal));
        corpus.AddRange(i.People);
        corpus.AddRange(i.Places);
        var seen = i.Codex.SeenKeys(corpus);
        var next = path.After(current) is { } after ? path.IndexOf(after.Id) : (int?)null;
        var questsDone = i.Done.Concat(i.GateDone).ToHashSet();
        return new StoryState(catalog, path, current, taken, sequence, journal, objectives, previousLine, people, progress, json, corpus, seen, next, questsDone);
    }

    /// <summary>
    /// A quest's event, made on a worker (its journal is read from the sheets). <paramref name="catalog"/>,
    /// <paramref name="done"/>, <paramref name="started"/> and <paramref name="startTown"/> are read on the
    /// framework thread when it was finished, so a main-story quest is told as one (its index, its chapter) even before the story's
    /// first reading after login. The road is returned too (for the chapter's card).
    /// </summary>
    public (QuestFinished Event, MsqPath? Path) Finished(uint quest, MsqCatalog? catalog, IReadOnlySet<uint>? done, IReadOnlySet<uint> started, uint startTown, IReadOnlyList<string> people)
    {
        var rows = JournalReader.Rows(quest);
        var journal = JournalText.Whole(rows);
        var named = Codex.Empty.Named(string.Join("\n", journal), people);
        if (catalog != null && done != null && catalog.Contains(quest))
        {
            var path = catalog.Path(done, started, catalog.StartPathOf(startTown));
            if (catalog.Finished(quest, path, journal, named) is { } msq) return (msq, path);
        }
        var (name, level, expansion, place) = Quests.Info(quest);
        return (new QuestFinished(QuestIds.Row(quest), name, level, false, null, expansion, null, false, false, place, journal, named), null);
    }

    public void Dispose() => stop.Cancel();
}
