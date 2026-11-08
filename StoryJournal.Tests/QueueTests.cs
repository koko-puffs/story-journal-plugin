using System.Text.Json.Nodes;
using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

/// <summary>The queue on disk: nothing is lost, nothing is sent twice, and batches keep to the site's limits.</summary>
public sealed class QueueTests : IDisposable
{
    private const string Key = "abcdefghijklmnop";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "sj-queue-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static SessionRecord Session(DateTime at) => new() { Character = Key, StartedAt = at, LastAt = at };

    private static JournalEvent Level(SessionRecord s, int level, DateTime at) => Events.Make(EventKind.Level, Events.Level("PLD", level), at, s.Id, s.Character);

    [Fact]
    public void EventsSurviveARestart()
    {
        var t = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        q.Add(Level(s, 41, t));
        q.Add(Level(s, 42, t.AddMinutes(1)));
        var again = new EventQueue(dir);
        Assert.Equal(2, again.Count);
        Assert.Equal(s.Id, again.Session(s.Id)!.Id);
        Assert.Equal(new[] { 41, 42 }, again.Pending().Select(e => (int)e.Data["level"]!).ToArray());
    }

    [Fact]
    public void AnEventAddedTwiceIsKeptOnce()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        var e = Level(s, 41, t);
        q.Add(e);
        q.Add(e);
        Assert.Equal(1, q.Count);
        // A crash between an append and a rewrite can leave a line twice on disk: read once.
        File.AppendAllText(Path.Combine(dir, "events.jsonl"), e.ToDisk().ToJsonString() + "\n");
        Assert.Equal(1, new EventQueue(dir).Count);
    }

    [Fact]
    public void AcknowledgedEventsLeaveAndOthersStay()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        for (var i = 0; i < 5; i++) q.Add(Level(s, 40 + i, t.AddSeconds(i)));
        var batch = q.NextBatch(3)!;
        Assert.Equal(3, batch.Events.Count);
        Assert.Equal(s.Id, batch.Session.Id);
        // Something happens while the batch is out.
        q.Add(Level(s, 50, t.AddSeconds(10)));
        q.Acknowledge(batch);
        Assert.Equal(3, q.Count);
        Assert.Equal(new[] { 43, 44, 50 }, new EventQueue(dir).Pending().Select(e => (int)e.Data["level"]!).ToArray());
        Assert.True(q.Session(s.Id)!.Known);
    }

    [Fact]
    public void ABatchIsOneSessionsOldestFirst()
    {
        var t = DateTime.UtcNow;
        var a = Session(t);
        var b = Session(t.AddHours(1));
        var q = new EventQueue(dir);
        q.PutSession(a);
        q.PutSession(b);
        q.Add(Level(a, 1, t));
        q.Add(Level(b, 2, t.AddHours(1)));
        q.Add(Level(a, 3, t.AddMinutes(5)));
        var batch = q.NextBatch()!;
        Assert.Equal(a.Id, batch.Session.Id);
        Assert.Equal(new[] { 1, 3 }, batch.Events.Select(e => (int)e.Data["level"]!).ToArray());
        q.Acknowledge(batch);
        Assert.Equal(b.Id, q.NextBatch()!.Session.Id);
    }

    [Fact]
    public void ABatchKeepsUnderTheSitesLimits()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        for (var i = 0; i < 250; i++) q.Add(Level(s, 1, t.AddSeconds(i)));
        Assert.Equal(EventQueue.MaxBatch, q.NextBatch()!.Events.Count);

        var big = new EventQueue(Path.Combine(dir, "big"));
        big.PutSession(s);
        var journal = Enumerable.Range(0, 12).Select(i => new string('j', 1200)).ToList();
        for (var i = 0; i < 60; i++)
            big.Add(Events.Make(EventKind.Quest, Events.Quest(new QuestFinished((uint)(66000 + i), "Q", 1, true, i, 0, null, false, false, "x", journal, Array.Empty<string>())), t, s.Id, Key));
        var batch = big.NextBatch()!;
        Assert.True(batch.Events.Count < 60);
        Assert.True(Json.Bytes(batch.ToSite()) <= EventQueue.MaxBatchBytes);
    }

    [Fact]
    public void ASessionToTellGoesWithNoEvents()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        var batch = q.NextBatch()!;
        Assert.Empty(batch.Events);
        q.Acknowledge(batch);
        Assert.Null(q.NextBatch());
        // Its end, then its title: told again each time.
        s.EndedAt = t.AddHours(1);
        s.Title = "The Mountain Falls";
        s.Dirty = true;
        q.PutSession(s);
        Assert.Equal("The Mountain Falls", q.NextBatch()!.Session.Title);
    }

    [Fact]
    public void TheCardsWordsGoOnceSoTheSitesEditsStay()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        s.EndedAt = t.AddHours(1);
        s.Title = "The Mountain Falls";
        s.Moods = new List<string> { "Cosy" };
        s.Note = "Ask about the Ixal.";
        var q = new EventQueue(dir);
        q.PutSession(s);
        var first = q.NextBatch()!;
        Assert.True(first.Session.ToSite().ContainsKey("title"));
        q.Acknowledge(first);
        // A late event for it: the session goes again, without the words.
        q.Add(Level(s, 41, t.AddMinutes(5)));
        var again = new EventQueue(dir).NextBatch()!;
        Assert.False(again.Session.ToSite().ContainsKey("title"));
        Assert.False(again.Session.ToSite().ContainsKey("note"));
        Assert.Equal("The Mountain Falls", again.Session.Title);
        // Changed in the game: they go again.
        var changed = q.Session(s.Id)!;
        changed.Title = "A new name";
        changed.WordsSent = false;
        changed.Dirty = true;
        q.PutSession(changed);
        Assert.Equal("A new name", (string)q.NextBatch()!.Session.ToSite()["title"]!);
    }

    [Fact]
    public void ASessionChangedWhileSendingIsToldAgain()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        var batch = q.NextBatch()!;
        s.Title = "Later words";
        q.PutSession(s);
        q.Acknowledge(batch);
        Assert.Equal("Later words", q.NextBatch()!.Session.Title);
    }

    [Fact]
    public void RefusedEventsAreKeptAsideNotLost()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        var e = Level(s, 41, t);
        q.Add(e);
        q.Add(Level(s, 42, t));
        q.SetAside(new[] { e }, "size");
        Assert.Equal(1, q.Count);
        Assert.Equal(1, q.AsideCount);
        var aside = File.ReadAllLines(Path.Combine(dir, "aside.jsonl"));
        Assert.Contains(e.Id.ToString("D"), aside[0]);
        Assert.Equal(1, new EventQueue(dir).AsideCount);
    }

    [Fact]
    public void ARefusedSessionTakesItsEventsAside()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        q.PutSession(s);
        q.Add(Level(s, 1, t));
        q.Add(Level(s, 2, t));
        q.RefuseSession(s.Id, "data");
        Assert.Equal(0, q.Count);
        Assert.Equal(2, q.AsideCount);
        Assert.Null(q.NextBatch());
    }

    [Fact]
    public void EventsWhoseSessionWasLostStillGo()
    {
        var t = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);
        var s = Session(t);
        var q = new EventQueue(dir);
        q.Add(Level(s, 1, t));
        q.Add(Level(s, 2, t.AddMinutes(10)));
        var batch = q.NextBatch()!;
        Assert.Equal(s.Id, batch.Session.Id);
        Assert.Equal(t, batch.Session.StartedAt);
        Assert.Equal(t.AddMinutes(10), batch.Session.EndedAt);
    }

    [Fact]
    public void AnUnreadableLineIsSetAsideNotDropped()
    {
        Directory.CreateDirectory(dir);
        var t = DateTime.UtcNow;
        var s = Session(t);
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), "{not json\n" + Level(s, 41, t).ToDisk().ToJsonString() + "\n");
        var q = new EventQueue(dir);
        Assert.Equal(1, q.Count);
        Assert.Contains("unreadable", File.ReadAllText(Path.Combine(dir, "aside.jsonl")));
        Assert.Equal(1, new EventQueue(dir).Count);
    }

    [Fact]
    public void PhotosWaitAsFilesUntilKept()
    {
        var t = DateTime.UtcNow;
        var s = Session(t);
        var q = new EventQueue(dir);
        var photo = new QueuedPhoto(Guid.NewGuid(), s.Id, Key, t, "At the Navel", 1920, 1080);
        q.AddPhoto(photo, new byte[] { 0xff, 0xd8, 0xff });
        var again = new EventQueue(dir);
        Assert.Equal(1, again.PhotoCount);
        var next = again.NextPhoto()!;
        Assert.Equal("At the Navel", next.Caption);
        Assert.Equal(3, again.PhotoBytes(next.Id)!.Length);
        Assert.Null(again.NextPhoto(p => p.Session != s.Id));
        again.PhotoDone(next.Id);
        Assert.Equal(0, new EventQueue(dir).PhotoCount);
    }

    [Fact]
    public void ARefusedPhotoIsKeptAside()
    {
        var q = new EventQueue(dir);
        var photo = new QueuedPhoto(Guid.NewGuid(), Guid.NewGuid(), Key, DateTime.UtcNow, "", 64, 64);
        q.AddPhoto(photo, new byte[] { 1 });
        q.PhotoDone(photo.Id, "image");
        Assert.Equal(0, q.PhotoCount);
        Assert.True(File.Exists(Path.Combine(dir, "aside-photos", photo.Id.ToString("N") + ".jpg")));
    }

    [Fact]
    public void AnOpenSessionIsFoundAgain()
    {
        var t = DateTime.UtcNow;
        var open = Session(t);
        var ended = Session(t.AddHours(-3));
        ended.EndedAt = t.AddHours(-2);
        var q = new EventQueue(dir);
        q.PutSession(ended);
        q.PutSession(open);
        Assert.Equal(open.Id, new EventQueue(dir).OpenSession(Key)!.Id);
        Assert.Null(q.OpenSession("someoneelse12345"));
    }
}

public class SessionClockTests
{
    private const string Key = "abcdefghijklmnop";
    private static readonly DateTime T = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheFirstThingStartsASession()
    {
        var clock = new SessionClock();
        var (s, ended) = clock.Activity(T, Key);
        Assert.Null(ended);
        Assert.Equal(T, s.StartedAt);
        Assert.Same(s, clock.Activity(T.AddMinutes(5), Key).Session);
        Assert.Equal(T.AddMinutes(5), s.LastAt);
    }

    [Fact]
    public void ALongIdleEndsItAtItsLastTime()
    {
        var clock = new SessionClock();
        var first = clock.Activity(T, Key).Session;
        clock.Activity(T.AddMinutes(10), Key);
        Assert.Null(clock.CheckIdle(T.AddMinutes(39)));
        var (second, ended) = clock.Activity(T.AddMinutes(41), Key);
        Assert.Same(first, ended);
        Assert.Equal(T.AddMinutes(10), first.EndedAt);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(T.AddMinutes(41), second.StartedAt);
    }

    [Fact]
    public void IdleIsNoticedWithoutActivity()
    {
        var clock = new SessionClock(TimeSpan.FromMinutes(30));
        clock.Activity(T, Key);
        var ended = clock.CheckIdle(T.AddMinutes(31));
        Assert.NotNull(ended);
        Assert.Equal(T, ended!.EndedAt);
        Assert.Null(clock.Current);
    }

    [Fact]
    public void LogoutEndsItThen()
    {
        var clock = new SessionClock();
        clock.Activity(T, Key);
        var ended = clock.End(T.AddHours(1));
        Assert.Equal(T.AddHours(1), ended!.EndedAt);
        Assert.Equal(T.AddHours(1), ended.LastAt);
        Assert.Null(clock.End(T.AddHours(2)));
    }

    [Fact]
    public void AnotherCharacterStartsAnotherSession()
    {
        var clock = new SessionClock();
        var a = clock.Activity(T, Key).Session;
        var (b, ended) = clock.Activity(T.AddMinutes(1), "zyxwvutsrqponmlk");
        Assert.Same(a, ended);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void AReloadPicksTheSessionUpAgain()
    {
        var clock = new SessionClock();
        var open = new SessionRecord { Character = Key, StartedAt = T, LastAt = T.AddMinutes(20) };
        clock.Resume(open);
        Assert.Same(open, clock.Activity(T.AddMinutes(25), Key).Session);
    }

    [Fact]
    public void ATitleIsSuggestedFromTheEvening()
    {
        var s = Guid.NewGuid();
        JournalEvent E(string kind, JsonObject data, int minute) => new(Guid.NewGuid(), kind, T.AddMinutes(minute), data, s, Key);
        var quest = E(EventKind.Quest, Events.Quest(new QuestFinished(1, "Talk of the Highlands", 41, true, 5, 0, "Into the Snow", false, false, "x", Array.Empty<string>(), Array.Empty<string>())), 1);
        var place = E(EventKind.Zone, Events.Zone(155, "Coerthas Central Highlands", "Coerthas", true), 2);
        var duty = E(EventKind.Duty, Events.Duty(56, "the Howling Eye", "trial", true, 600), 3);
        var chapter = E(EventKind.Quest, Events.Quest(new QuestFinished(2, "The Eye", 44, true, 6, 0, "The Howling Eye", true, false, "x", Array.Empty<string>(), Array.Empty<string>())), 4);
        Assert.Equal("Talk of the Highlands", Sessions.SuggestTitle(new[] { quest }));
        Assert.Equal("Into Coerthas Central Highlands", Sessions.SuggestTitle(new[] { quest, place }));
        Assert.Equal("The Howling Eye", Sessions.SuggestTitle(new[] { quest, place, duty }));
        Assert.Equal("The Howling Eye", Sessions.SuggestTitle(new[] { quest, place, duty, chapter }));
        Assert.Null(Sessions.SuggestTitle(Array.Empty<JournalEvent>()));
        var n = Sessions.Count(new[] { quest, place, duty, chapter }, 2);
        Assert.Equal(2, n.Quests);
        Assert.Equal(1, n.Duties);
        Assert.Equal(1, n.Places);
        Assert.Equal(2, n.Photos);
    }
}
