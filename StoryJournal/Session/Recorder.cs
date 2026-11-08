using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.DutyState;
using StoryJournal.Core;
using StoryJournal.Game;

namespace StoryJournal.Session;

/// <summary>
/// What happens in the game, noticed by cheap polling on the framework thread (twice a second) and
/// written down as events through <see cref="Journal.Record"/>:
/// <list type="bullet">
/// <item>quests taken (a new id in QuestManager's list) and finished (an id gone from it that the game
/// says is complete; one gone and not complete was abandoned, and isn't written); a main-story quest
/// taken and finished between two looks never shows in the list, so the main story's quests done (read
/// now and then) are a backstop: one newly done is written if the list didn't see it (each once,
/// <see cref="Finishes"/>);</item>
/// <item>cutscenes, with how long they lasted (the cutscene conditions, from rise to fall);</item>
/// <item>zones (a new territory once the loading screen's gone), <c>first</c> when the character's never
/// been there since the journal began;</item>
/// <item>duties, cleared or left, with how long (IDutyState's start and completion, and leaving the duty);</item>
/// <item>levels gained on the job being played;</item>
/// <item>people met: the speaker of a quest's dialogue (the <c>Talk</c> window), the first time.</item>
/// </list>
/// At login (or when the plugin loads) a baseline is read first, so what's already so (quests in the
/// log, the zone you're in, your level) isn't written as new. While paused, nothing is written, but the
/// baseline follows the game so resuming doesn't write what happened meanwhile. Moving counts as being
/// there, for the session's idle clock.
/// </summary>
public sealed class Recorder
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(500);

    private readonly Journal journal;
    private DateTime lastLook;
    private bool baseline;
    private Dictionary<uint, int> accepted = new();
    private uint territory;
    private (int Level, uint Job) level;
    private DateTime? cutsceneSince;
    private uint cutsceneQuest;
    private string cutscenePlace = string.Empty;
    private (uint Cfc, DateTime Since, bool Cleared)? duty;
    private Vector3? lastPosition;

    /// <summary>Quests gone from the log, not yet seen complete: the game may mark them a moment after.</summary>
    private readonly Dictionary<uint, DateTime> vanished = new();

    /// <summary>Finishes written (each once, however it was noticed), and the main story's quests done as last seen.</summary>
    private readonly Finishes finishes = new();

    private int doneVersion = -1;

    public Recorder(Journal journal)
    {
        this.journal = journal;
        Services.Duty.DutyStarted += OnDutyStarted;
        Services.Duty.DutyCompleted += OnDutyCompleted;
    }

    /// <summary>The quests in the log as last read (sheet row → step).</summary>
    public IReadOnlyDictionary<uint, int> Accepted => accepted;

    /// <summary>Forget the baseline (a new login): the next look reads it again.</summary>
    public void Reset()
    {
        baseline = false;
        vanished.Clear();
        finishes.Reset();
        doneVersion = -1;
        cutsceneSince = null;
        duty = null;
        lastPosition = null;
    }

    private Configuration Config => journal.Config;

    private bool Writing => !Config.Paused && Config.RecordStory;

    public void Tick(DateTime now, PlayerNow player)
    {
        if (now - lastLook < Every) return;
        lastLook = now;
        if (InGame.Loading) return;
        if (!baseline)
        {
            Baseline(player);
            return;
        }
        Moved(now);
        Zone(now, player);
        QuestLog(now);
        DoneBackstop(now);
        Cutscene(now, player);
        DutyLeft(now);
        Levels(now, player);
    }

    private void Baseline(PlayerNow player)
    {
        accepted = Quests.Accepted();
        territory = player.Territory;
        level = (player.Level, player.JobId);
        if (territory != 0) journal.Memory?.Visited.Add(territory);
        cutsceneSince = InGame.InCutscene ? DateTime.UtcNow : null;
        lastPosition = InGame.Position;
        baseline = true;
        finishes.Reset();
        // The next reading of the quests done (asked for here) is the baseline for the backstop.
        doneVersion = -1;
        journal.Story.DoneChanged();
    }

    private void Moved(DateTime now)
    {
        var pos = InGame.Position;
        if (pos is not { } p) return;
        if (lastPosition is { } last && Vector3.DistanceSquared(last, p) > 0.25f) journal.Activity(now);
        lastPosition = p;
    }

    private void Zone(DateTime now, PlayerNow player)
    {
        var id = player.Territory;
        if (id == 0 || id == territory) return;
        territory = id;
        // A duty's own territory is the duty's event, not a place.
        if (InGame.InDuty) return;
        var (name, region, _) = Places.Territory(id);
        if (name.Length == 0) return;
        var memory = journal.Memory;
        var first = memory != null && !memory.Visited.Contains(id);
        memory?.Visited.Add(id);
        if (first) journal.MemoryChanged();
        if (Writing) journal.Record(EventKind.Zone, Events.Zone(id, name, region, first), now);
        journal.Arrived(name, region, first);
    }

    private void QuestLog(DateTime now)
    {
        var nowAccepted = Quests.Accepted();
        if (nowAccepted.Count == 0 && accepted.Count > 3)
        {
            // An empty log where there were quests: the game's not ready (a moment after loading), not every quest finished.
            return;
        }
        var catalog = journal.Story.Catalog;
        var changed = false;
        foreach (var (id, _) in nowAccepted)
        {
            if (accepted.ContainsKey(id)) continue;
            changed = true;
            var msq = catalog?.Contains(id) == true;
            if (Writing && (msq || Config.RecordSideContent))
            {
                var name = catalog?.Quest(id)?.Name ?? Quests.Info(id).Name;
                journal.Record(EventKind.Accept, Events.Accept(id, name, msq), now);
            }
        }
        foreach (var (id, _) in accepted)
        {
            if (nowAccepted.ContainsKey(id)) continue;
            changed = true;
            vanished[id] = now;
        }
        foreach (var (id, since) in vanished.ToList())
        {
            if (nowAccepted.ContainsKey(id))
            {
                vanished.Remove(id);
                continue;
            }
            if (Quests.IsComplete(id))
            {
                vanished.Remove(id);
                Finished(id, now);
                journal.Story.DoneChanged();
            }
            // Not complete after a while: abandoned, and not written.
            else if (now - since > TimeSpan.FromSeconds(10)) vanished.Remove(id);
        }
        if (!changed && nowAccepted.All(kv => accepted.TryGetValue(kv.Key, out var s) && s == kv.Value)) return;
        accepted = nowAccepted;
        if (changed) journal.Story.DoneChanged();
    }

    /// <summary>
    /// A quest finished, however it was noticed: written once (the journal waits for the main story's
    /// catalog when it isn't read yet, to know whether it's one of the main story's).
    /// </summary>
    private void Finished(uint id, DateTime now)
    {
        if (!finishes.Claim(id)) return;
        var catalog = journal.Story.Catalog;
        var msq = catalog?.Contains(id) == true;
        if (Writing && (msq || Config.RecordSideContent || (catalog == null && !journal.Story.Broken))) journal.QuestFinished(id, now);
    }

    /// <summary>
    /// The backstop: main-story quests newly done since the last reading of them (the first after login is
    /// the baseline), written if the log didn't see them finish.
    /// </summary>
    private void DoneBackstop(DateTime now)
    {
        var story = journal.Story;
        if (story.Catalog == null || story.DoneVersion == doneVersion) return;
        var first = doneVersion < 0;
        doneVersion = story.DoneVersion;
        var newly = finishes.NewlyDone(story.Done);
        if (first) return;
        foreach (var id in newly)
        {
            // Still in the log's leaving list: the log's own check writes it.
            if (vanished.ContainsKey(id)) continue;
            Finished(id, now);
        }
    }

    private void Cutscene(DateTime now, PlayerNow player)
    {
        var watching = InGame.InCutscene;
        if (watching && cutsceneSince == null)
        {
            cutsceneSince = now;
            var current = journal.Story.Now;
            cutsceneQuest = current is { CurrentTaken: true, Current: { } q } ? q.Id : 0;
            cutscenePlace = Places.Territory(player.Territory).Name;
            journal.Activity(now);
        }
        else if (!watching && cutsceneSince is { } since)
        {
            cutsceneSince = null;
            var seconds = (int)Math.Round((now - since).TotalSeconds);
            // A blink of the condition (a door, a short event) isn't a cutscene.
            if (seconds >= 3 && Writing) journal.Record(EventKind.Cutscene, Events.Cutscene(cutsceneQuest == 0 ? null : cutsceneQuest, cutscenePlace, seconds), now);
        }
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        try
        {
            var cfc = args.ContentFinderCondition.RowId;
            if (cfc == 0) cfc = Places.Territory(args.TerritoryType.RowId).Duty;
            // Already noticed on the way in (it began when you arrived): kept.
            if (cfc != 0 && duty?.Cfc != cfc) duty = (cfc, DateTime.UtcNow, false);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the duty");
        }
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (duty is { } d) duty = d with { Cleared = true };
    }

    private void DutyLeft(DateTime now)
    {
        if (duty == null && InGame.InDuty)
        {
            // In a duty without its start heard (loaded into one under way, or its start isn't announced): from now.
            try
            {
                var cfc = Services.Duty.ContentFinderCondition.RowId;
                if (cfc == 0) cfc = Places.Territory(Services.ClientState.TerritoryType).Duty;
                if (cfc != 0) duty = (cfc, now, false);
            }
            catch (Exception ex)
            {
                Services.Log.Debug(ex, "Story Journal couldn't read the duty");
            }
        }
        if (duty is not { } d) return;
        if (InGame.InDuty) return;
        duty = null;
        if (!Writing) return;
        var msqDuty = journal.Story.Catalog?.Order.Any(q => q.Duty == d.Cfc) == true;
        if (!msqDuty && !Config.RecordSideContent) return;
        var (name, type, _) = Places.Duty(d.Cfc);
        if (name.Length == 0) return;
        journal.Record(EventKind.Duty, Events.Duty(d.Cfc, name, type, d.Cleared, (int)Math.Round((now - d.Since).TotalSeconds)), now);
    }

    private void Levels(DateTime now, PlayerNow player)
    {
        if (player.JobId != level.Job)
        {
            // Another job: its level is where it is, not a level gained.
            level = (player.Level, player.JobId);
            return;
        }
        if (player.Level <= level.Level)
        {
            level = (player.Level, player.JobId);
            return;
        }
        level = (player.Level, player.JobId);
        if (Writing && player.Job.Length > 0) journal.Record(EventKind.Level, Events.Level(player.Job, player.Level), now);
    }

    /// <summary>A line of dialogue (framework thread): its speaker met for the first time, during a quest's talk or a cutscene.</summary>
    public void Heard(TalkLine line, PlayerNow? player)
    {
        if (!baseline || player == null || !Config.RecordPeople || Config.Paused) return;
        var name = JournalText.Tidy(line.Speaker);
        if (name.Length < 2 || name.Contains('?') || name == player.Name || !InGame.InQuestTalk) return;
        var memory = journal.Memory;
        if (memory == null || memory.People.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return;
        var place = Places.Territory(player.Territory).Name;
        var quest = journal.Story.Now is { CurrentTaken: true, Current: { } q } ? q.Id : 0;
        memory.People.Add(new MetPerson { Name = name, Place = place, At = line.At, QuestId = quest });
        journal.MemoryChanged();
        journal.Record(EventKind.Person, Events.Person(name, place, quest == 0 ? null : quest), line.At);
    }

    public void Dispose()
    {
        Services.Duty.DutyStarted -= OnDutyStarted;
        Services.Duty.DutyCompleted -= OnDutyCompleted;
    }
}
