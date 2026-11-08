using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using StoryJournal.Core;
using StoryJournal.Game;

namespace StoryJournal.Session;

/// <summary>
/// The plugin's heart, on the framework thread: who's playing, the session under way, what's recorded
/// (<see cref="Recorder"/>) and queued (<see cref="EventQueue"/>), where they are in the story
/// (<see cref="Story"/>), the Codex, the site (<see cref="SiteSync"/>), and the cards the windows show.
/// The windows read it each frame and call its actions; nothing here blocks the framework thread
/// (sheets, the site and photos are read and sent on workers; the queue appends a line to a file).
/// </summary>
public sealed class Journal : IDisposable
{
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentQueue<Action> fromWorkers = new();
    private readonly TalkReader talk;
    private readonly KeyWatch keys = new();
    private readonly SessionClock clock;
    private readonly List<JournalEvent> sessionEvents = new();

    /// <summary>Quests finished before the main story's catalog was read: written once it is (to know which are the story's).</summary>
    private readonly List<(uint Quest, DateTime At)> finishWaiting = new();
    private DateTime lastLook;
    private DateTime lastPut;
    private bool memoryDirty;
    private DateTime memorySavedAt;
    private string? characterKey;
    private bool welcomeDecided;
    private ArrivalCard? arrivalWaiting;
    private MilestoneCard? milestoneWaiting;
    private ulong lastTarget;
    private bool disposed;

    public Journal(Configuration config)
    {
        Config = config;
        if (string.IsNullOrEmpty(config.Salt))
        {
            config.Salt = SiteLink.NewSalt();
            config.Save();
        }
        var dir = Services.PluginInterface.GetPluginConfigDirectory();
        Queue = new EventQueue(Path.Combine(dir, "queue"));
        Sync = new SiteSync(config, Queue) { Character = HelloFor, OpenSession = () => clock?.Current };
        Sync.HelloArrived += OnHello;
        Sync.Said += t => AddToast(t, null, null, ToastKind.Plain);
        Sync.SessionRefused += OnSessionRefused;
        Codex = new CodexStore(dir, Sync.Site.Http);
        Story = new Story();
        Photos = new Photos(Queue);
        Photos.Taken += OnPhoto;
        Photos.Failed += why => AddToast("No photo", why, null, ToastKind.Bad);
        clock = new SessionClock(SessionClock.IdleFor(config.IdleMinutes));
        Recorder = new Recorder(this);
        talk = new TalkReader();
        talk.Line += OnTalk;
        Services.ClientState.Login += OnLogin;
        Services.ClientState.Logout += OnLogout;
        // What went wrong opening the queue, said now that someone's listening.
        foreach (var problem in Queue.LoadProblems)
        {
            Services.Log.Warning($"Story Journal: {problem}");
            AddToast("Some of the queue couldn't be read", problem, null, ToastKind.Bad);
        }
    }

    public Configuration Config { get; }

    public EventQueue Queue { get; }

    public SiteSync Sync { get; }

    public Story Story { get; }

    public CodexStore Codex { get; }

    public Photos Photos { get; }

    public Recorder Recorder { get; }

    /// <summary>The character you're playing (null when you aren't).</summary>
    public PlayerNow? Player { get; private set; }

    /// <summary>What the plugin remembers of the character (the last one played, after logout).</summary>
    public CharacterMemory? Memory => characterKey != null ? Config.Memory(characterKey) : null;

    public string? CharacterKey => characterKey;

    /// <summary>The session under way (null until something happens after login).</summary>
    public SessionRecord? Session => clock.Current;

    /// <summary>What happened in the session under way (since the plugin loaded, and what's still queued from before).</summary>
    public IReadOnlyList<JournalEvent> SessionEvents => sessionEvents;

    /// <summary>The site's preferences (its defaults until hello has answered).</summary>
    public Prefs Prefs => Sync.Hello?.Prefs ?? Prefs.Default;

    /// <summary>How chatty the world is: the site's setting, made quieter here if asked.</summary>
    public Whispers Whispers => Config.WhispersHere is >= 0 and <= 2 && (Whispers)Config.WhispersHere < Prefs.Whispers ? (Whispers)Config.WhispersHere : Prefs.Whispers;

    /// <summary>The last line of dialogue shown (it leaves the game only when you save it).</summary>
    public TalkLine? LastLine => talk.Last;

    // Cards and toasts, for the windows.

    public List<Toast> Toasts { get; } = new();

    public WelcomeCard? Welcome { get; set; }

    public ArrivalCard? Arrival { get; set; }

    public WhoCard? Who { get; set; }

    public MilestoneCard? Milestone { get; set; }

    public WrapUpCard? WrapUp { get; set; }

    /// <summary>Opens the quick note (a window's job; the key and /sj note without words ask for it).</summary>
    public event Action? NoteAsked;

    // ---------------------------------------------------------------------------------------------
    // Each frame.

    public void Tick()
    {
        if (disposed) return;
        var now = DateTime.UtcNow;
        while (fromWorkers.TryDequeue(out var a))
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal: a worker's answer failed");
            }
        }
        Sync.Tick(now);
        Photos.Tick();
        Keys();
        if (now - lastLook < Look) return;
        lastLook = now;

        // Settings' idle minutes, as they are now.
        clock.Idle = SessionClock.IdleFor(Config.IdleMinutes);
        Player = InGame.Player(Config.Salt);
        if (Player is { } player)
        {
            if (player.Key != characterKey) Arrive(player);
            if (!InGame.Loading)
            {
                var memory = Config.Memory(player.Key);
                Story.Tick(now, player, Recorder.Accepted, memory.People.Select(p => p.Name).ToList(), PlacesSeen(memory), Codex.Codex, Codex.Version);
                Recorder.Tick(now, player);
            }
            Cards(now, player);
            StoryRead();
        }
        if (clock.CheckIdle(now) is { } idle) Ended(idle, false);
        if (clock.Current is { } cur && now - lastPut > TimeSpan.FromSeconds(30))
        {
            lastPut = now;
            Queue.PutSession(cur);
        }
        if (memoryDirty && now - memorySavedAt > TimeSpan.FromSeconds(10))
        {
            memoryDirty = false;
            memorySavedAt = now;
            Config.Save();
        }
        Codex.Tick(now);
        Toasts.RemoveAll(t => now - t.At > TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// What waited for the main story to be read: quests finished before its catalog was, theories sealed
    /// before the first reading (their ask-at point), the session's starting percentage.
    /// </summary>
    private void StoryRead()
    {
        if (finishWaiting.Count > 0 && (Story.Catalog != null || Story.Broken))
        {
            var waiting = finishWaiting.ToList();
            finishWaiting.Clear();
            foreach (var (quest, at) in waiting) QuestFinished(quest, at);
        }
        if (Story.Now is not { } state) return;
        if (clock.Current is { Tally.PercentAtStart: null } cur && state.Progress.ExpansionNow is { } e)
            cur.Tally.PercentAtStart = Progress.Percent(e.Done, e.Total);
        if (Memory is { } memory && memory.Theories.Any(t => t.AwaitingIndex))
        {
            foreach (var t in memory.Theories.Where(t => t.AwaitingIndex))
            {
                t.DueIndex = state.Progress.Index + t.CheckAfter;
                if (t.SealedQuest.Length == 0) t.SealedQuest = state.Current?.Name ?? string.Empty;
                t.AwaitingIndex = false;
            }
            Config.Save();
            Sync.KickNotes();
        }
    }

    private List<string>? placesSeen;
    private int placesSeenCount = -1;

    /// <summary>The names of the zones visited (kept until another is added).</summary>
    private List<string> PlacesSeen(CharacterMemory memory)
    {
        if (placesSeen != null && placesSeenCount == memory.Visited.Count) return placesSeen;
        placesSeenCount = memory.Visited.Count;
        return placesSeen = memory.Visited.Select(v => Places.Territory(v).Name).Where(n => n.Length > 0).Distinct().ToList();
    }

    /// <summary>A character's here (logged in, or the plugin loaded with one): its memory, its open session, hello.</summary>
    private void Arrive(PlayerNow player)
    {
        if (characterKey != null && clock.Current is { } other && other.Character != player.Key) Ended(clock.End(DateTime.UtcNow)!, true);
        // Another character: the story read was the other's.
        if (player.Key != lastCharacter) Story.Reset();
        lastCharacter = player.Key;
        characterKey = player.Key;
        var memory = Config.Memory(player.Key);
        memory.Name = player.Name;
        memory.World = player.World;
        MemoryChanged();
        placesSeen = null;
        Recorder.Reset();
        welcomeDecided = false;
        sessionEvents.Clear();
        finishWaiting.Clear();
        if (Queue.OpenSession(player.Key) is { } open)
        {
            if (DateTime.UtcNow - open.LastAt > clock.Idle)
            {
                // Left open by a crash or a reload long ago: it ended when it was last heard of.
                open.EndedAt = open.LastAt;
                open.Dirty = true;
                Queue.PutSession(open);
            }
            else
            {
                clock.Resume(open);
                sessionEvents.AddRange(Queue.PendingFor(open.Id));
            }
        }
        Sync.LoggedIn();
    }

    /// <summary>The character the story was last read for (a login with the same one keeps the reading).</summary>
    private string? lastCharacter;

    private void OnLogin()
    {
        // The character's read at the next look (the login event comes before everything's loaded).
        characterKey = null;
        Welcome = null;
    }

    private void OnLogout(int type, int code)
    {
        if (clock.End(DateTime.UtcNow) is { } ended) Ended(ended, true);
        Welcome = null;
        Who = null;
        Arrival = null;
        Player = null;
        Sync.Kick();
    }

    // ---------------------------------------------------------------------------------------------
    // Recording.

    /// <summary>The character was there (moved, watched): the session's clock hears of it.</summary>
    public void Activity(DateTime at)
    {
        if (characterKey == null || Config.Paused) return;
        Touch(at);
    }

    private SessionRecord Touch(DateTime at)
    {
        var (session, ended) = clock.Activity(at, characterKey!);
        if (ended != null) Ended(ended, false);
        if (session.StartedAt == at && session.LastAt == at && Queue.Session(session.Id) == null)
        {
            if (Story.Now?.Progress.ExpansionNow is { } e) session.Tally.PercentAtStart = Progress.Percent(e.Done, e.Total);
            if (Memory is { JournalSince: null } memory)
            {
                memory.JournalSince = session.StartedAt;
                MemoryChanged();
            }
            Queue.PutSession(session);
            lastPut = at;
            sessionEvents.Clear();
            if (Config.ChatMessages) Services.Chat.Print("Story Journal: a new session started.");
        }
        return session;
    }

    /// <summary>Writes an event in the session under way (one is started if there's none).</summary>
    public void Record(string kind, JsonObject data, DateTime at)
    {
        if (characterKey == null || Config.Paused) return;
        var session = Touch(at);
        var e = Events.Make(kind, data, at, session.Id, session.Character);
        try
        {
            Queue.Add(e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Log.Error(ex, "Story Journal couldn't write an event to its queue on this PC");
            AddToast("Couldn't keep that", "The journal's queue on this PC can't be written (see /xllog).", null, ToastKind.Bad);
            return;
        }
        sessionEvents.Add(e);
        session.Tally.Add(e);
        Sync.Kick();
    }

    /// <summary>
    /// A quest was finished: its event is made on a worker (its journal read from the sheets), then
    /// written. Whether it's the main story's, its index and its chapter come from the catalog and the
    /// quests done read now, so they're right even before the story's first reading after login; before
    /// the catalog itself is read, it waits for it.
    /// </summary>
    public void QuestFinished(uint quest, DateTime at)
    {
        if (characterKey == null || Config.Paused) return;
        var catalog = Story.Catalog;
        if (catalog == null && !Story.Broken)
        {
            finishWaiting.Add((quest, at));
            return;
        }
        var done = catalog != null ? Quests.Done(catalog.Order.Select(q => q.Id)) : null;
        var started = Recorder.Accepted.Keys.ToHashSet();
        var town = Player?.StartTown ?? 0;
        var people = Memory?.People.Select(p => p.Name).ToList() ?? new List<string>();
        var key = characterKey;
        Task.Run(() =>
        {
            QuestFinished? q = null;
            MsqPath? path = null;
            try
            {
                (q, path) = Story.Finished(quest, catalog, done, started, town, people);
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, $"Story Journal couldn't read quest {quest} for its event");
            }
            if (q == null) return;
            fromWorkers.Enqueue(() =>
            {
                if (characterKey != key) return;
                // Waited for the catalog: a side quest only when side content's written.
                if (!q.Msq && !Config.RecordSideContent) return;
                Record(EventKind.Quest, Events.Quest(q), at);
                Story.DoneChanged();
                if (q.Msq && q.ChapterDone && Config.ShowMilestones && q.Chapter != null && catalog != null && path != null) Chapter(q, catalog, path);
            });
        });
    }

    /// <summary>A chapter closed: its card waits for a calm moment. Theories sealed again are asked again here.</summary>
    private void Chapter(QuestFinished q, MsqCatalog catalog, MsqPath path)
    {
        var expansion = catalog.ExpansionName(q.Expansion);
        var count = catalog.Quest(q.Id) is { } mq ? path.Quests.Count(x => x.Chapter == mq.Chapter) : 0;
        if (Memory is { Snoozed.Count: > 0 } memory)
        {
            memory.Snoozed.Clear();
            MemoryChanged();
        }
        milestoneWaiting = new MilestoneCard(q.Chapter!, expansion, q.Name, q.Journal.LastOrDefault(), count, q.ExpansionDone, DueTheories(), DateTime.UtcNow);
    }

    /// <summary>Theories to ask about now (the site's due ones, and those sealed here whose time has come).</summary>
    public List<DueTheory> DueTheories()
    {
        var memory = Memory;
        if (memory == null) return new List<DueTheory>();
        var local = memory.Theories.Where(t => t.DueIndex > 0 && !t.AwaitingIndex).Select(t => new LocalTheory(t.Id, t.Body, t.DueIndex, t.SealedAt, t.SealedQuest, t.Answered)).ToList();
        return Core.Welcome.DueTheories(Sync.Hello?.Theories ?? Array.Empty<DueTheory>(), local, Story.Now?.Progress.Index ?? 0, memory.Answered, memory.Snoozed);
    }

    /// <summary>
    /// The session ended: told to the site, and (at logout or /sj end) its card shown, from what the
    /// session kept of its evening (<see cref="SessionTally"/>: a reload doesn't lose it).
    /// </summary>
    private void Ended(SessionRecord ended, bool card)
    {
        Queue.PutSession(ended);
        Sync.Kick();
        if (!card || !Config.ShowWrapUp) return;
        var numbers = ended.Tally.Numbers;
        var length = (ended.EndedAt ?? ended.LastAt) - ended.StartedAt;
        if (ended.Tally.Events + numbers.Photos == 0 && length < TimeSpan.FromMinutes(10)) return;
        var now = Story.Now?.Progress.ExpansionNow;
        var nowPercent = now != null ? Progress.Percent(now.Done, now.Total) : 0;
        var before = ended.Tally.PercentAtStart ?? nowPercent;
        WrapUp = new WrapUpCard(ended, numbers, ended.Tally.Title, now?.Name ?? string.Empty, before, nowPercent, DateTime.UtcNow);
    }

    /// <summary>
    /// The site refused a session: if it's the one under way, it ends and a new one starts (what happens
    /// next would only be set aside with it).
    /// </summary>
    private void OnSessionRefused(Guid id)
    {
        if (clock.Current?.Id != id || clock.End(DateTime.UtcNow) is not { } ended) return;
        Queue.PutSession(ended);
        sessionEvents.Clear();
        Activity(DateTime.UtcNow);
    }

    // ---------------------------------------------------------------------------------------------
    // What the player does.

    /// <summary>Saves the line on screen (the last line of dialogue).</summary>
    public void SaveLine()
    {
        if (characterKey == null || Player is not { } player)
        {
            AddToast("Not now", "Log in to save a line.", null, ToastKind.Bad);
            return;
        }
        if (talk.Last is not { } line)
        {
            AddToast("No line to save", "Save a line while someone's talking (in a cutscene or a conversation).", null, ToastKind.Bad);
            return;
        }
        if (Config.Paused)
        {
            AddToast("Recording is paused", "/sj resume, then save it again.", null, ToastKind.Bad);
            return;
        }
        var quest = Story.Now is { CurrentTaken: true, Current: { } q } ? q.Id : (uint?)null;
        Record(EventKind.Line, Events.Line(line.Speaker, line.Text, quest, Places.Territory(player.Territory).Name), DateTime.UtcNow);
        var words = line.Text.Length > 90 ? "…" + line.Text[^88..].TrimStart() : line.Text;
        AddToast("Line saved", $"“{words}”", $"{(line.Speaker.Length > 0 ? line.Speaker : "Someone")} · this session", ToastKind.Quill);
    }

    /// <summary>Takes a photo without the HUD, for the session under way.</summary>
    public void TakePhoto(string caption = "")
    {
        if (characterKey == null || Player == null)
        {
            AddToast("Not now", "Log in to take a photo.", null, ToastKind.Bad);
            return;
        }
        if (Config.Paused)
        {
            AddToast("Recording is paused", "/sj resume, then take it again.", null, ToastKind.Bad);
            return;
        }
        var now = DateTime.UtcNow;
        var session = Touch(now);
        Queue.PutSession(session);
        if (!Photos.Take(new PhotoAsk(session.Id, session.Character, caption, now), out var why))
            AddToast("No photo", why, null, ToastKind.Bad);
    }

    private void OnPhoto(QueuedPhoto photo)
    {
        var today = 0;
        if (clock.Current is { } cur && cur.Id == photo.Session)
        {
            today = ++cur.Tally.Photos;
        }
        else if (Queue.Session(photo.Session) is { } s)
        {
            // Its session ended while the picture was made.
            today = ++s.Tally.Photos;
            Queue.PutSession(s);
        }
        Sync.Kick();
        AddToast("Photo taken", null, $"HUD hidden for the shot · added to this session · {Ui(today)} this session", ToastKind.Good);
        if (Services.GameGui.GameUiHidden && Config.ChatMessages)
            Services.Chat.Print("Story Journal: photo taken. It goes to the site with this session.");

        static string Ui(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Asks for the quick note (the key, or /sj note with nothing after it).</summary>
    public void AskNote() => NoteAsked?.Invoke();

    /// <summary>Keeps a note (sent to the site as soon as it can be).</summary>
    public bool AddNote(string body, string subject = "")
    {
        body = body.Trim();
        if (body.Length == 0 || characterKey == null) return false;
        Config.PendingNotes.Add(new PendingNote
        {
            Id = Guid.NewGuid(), Character = characterKey, Session = clock.Current?.Id, Body = body.Length > 2000 ? body[..2000] : body,
            Subject = subject, At = DateTime.UtcNow,
        });
        Config.Save();
        Sync.KickNotes();
        AddToast("Note saved", body.Length > 80 ? body[..78] + "…" : body, "it goes to this session's entry", ToastKind.Plain);
        return true;
    }

    /// <summary>Seals a theory: asked about after <paramref name="checkAfter"/> main-story quests (0: not asked here).</summary>
    public bool SealTheory(string body, int checkAfter, string subject = "")
    {
        body = Json.Tidy(body, 400);
        if (body.Length == 0 || Memory is not { } memory) return false;
        // Its ask-at point counts from where you are: before the story's first reading (just after login)
        // that isn't known, so it's worked out then (and the theory waits to go until it is).
        var state = Story.Now;
        var after = Math.Clamp(checkAfter, 0, 5000);
        memory.Theories.Add(new SealedTheory
        {
            Id = Guid.NewGuid(), Body = body, Subject = subject, CheckAfter = after,
            DueIndex = after > 0 && state != null ? state.Progress.Index + after : 0, AwaitingIndex = after > 0 && state == null,
            SealedAt = DateTime.UtcNow, SealedQuest = state?.Current?.Name ?? string.Empty,
        });
        Config.Save();
        Sync.KickNotes();
        AddToast("Theory sealed", body.Length > 80 ? body[..78] + "…" : body,
            checkAfter > 0 ? $"you'll be asked about it in {checkAfter} main-story quests" : "settle it here or on the site", ToastKind.Quill);
        return true;
    }

    /// <summary>Settles a theory: called, missed or changed (your mind).</summary>
    public void AnswerTheory(Guid id, string status)
    {
        if (status is not ("called" or "missed" or "changed" or "sealed")) return;
        Config.PendingAnswers.RemoveAll(a => a.Id == id);
        Config.PendingAnswers.Add(new PendingAnswer { Id = id, Status = status });
        if (Memory is { } memory)
        {
            // Sealed again: not settled, so asked again (when the next chapter closes), not now.
            if (status == "sealed")
            {
                memory.Answered.Remove(id);
                memory.Snoozed.Add(id);
            }
            else
            {
                memory.Answered.Add(id);
                memory.Snoozed.Remove(id);
            }
            if (memory.Theories.FirstOrDefault(t => t.Id == id) is { } mine) mine.Answered = status != "sealed";
        }
        Config.Save();
        Sync.KickNotes();
        AddToast("Theory answered", status switch
        {
            "called" => "You called it.",
            "missed" => "Not this time.",
            "changed" => "You changed your mind.",
            _ => "Sealed again.",
        }, null, ToastKind.Quill);
    }

    /// <summary>Ends the session now (/sj end): its card, to give it a title.</summary>
    public void EndSession()
    {
        if (clock.End(DateTime.UtcNow) is { } ended) Ended(ended, true);
        else AddToast("No session under way", "One starts with the next thing that happens.", null, ToastKind.Plain);
    }

    /// <summary>The end of session card's words, kept with the session and sent to the site.</summary>
    public void SaveWrapUp(WrapUpCard card, string title, IReadOnlyList<string> moods, string note)
    {
        var s = Queue.Session(card.Session.Id) ?? card.Session.Copy();
        s.Title = Json.Tidy(title, 80);
        s.Moods = Sessions.CleanMoods(moods);
        s.Note = note.Trim();
        s.Dirty = true;
        s.WordsSent = false;
        Queue.PutSession(s);
        Sync.Kick();
        WrapUp = null;
        AddToast("Session saved", s.Title.Length > 0 ? s.Title : null, "it goes to the site's Chronicle", ToastKind.Good);
    }

    public void Pause(bool paused)
    {
        Config.Paused = paused;
        Config.Save();
        if (Config.ChatMessages) Services.Chat.Print(paused ? "Story Journal: recording paused (/sj resume carries on)." : "Story Journal: recording again.");
    }

    /// <summary>Links the plugin with a token from the site (or unlinks it, with an empty one).</summary>
    public void Link(string token)
    {
        Config.Token = SiteLink.Clean(token);
        Config.Save();
        Sync.Relinked();
    }

    /// <summary>Flags the current step's place on the map.</summary>
    public void FlagNext()
    {
        if (Story.Now is not { CurrentTaken: true, Current: { } q } s)
        {
            AddToast("Nothing to flag", "Take the next main-story quest first.", null, ToastKind.Plain);
            return;
        }
        if (!MapFlag.Set(q.Id, s.Sequence, out var why)) AddToast("No flag", why, null, ToastKind.Plain);
    }

    public void MemoryChanged() => memoryDirty = true;

    public void AddToast(string title, string? body, string? note, ToastKind kind)
    {
        Toasts.Add(new Toast(title, body, note, kind, DateTime.UtcNow));
        if (Toasts.Count > 4) Toasts.RemoveAt(0);
    }

    private void Keys()
    {
        if (keys.Pressed(Config.LineKey)) SaveLine();
        if (keys.Pressed(Config.PhotoKey)) TakePhoto();
        if (keys.Pressed(Config.NoteKey)) AskNote();
    }

    private void OnTalk(TalkLine line) => Recorder.Heard(line, Player);

    // ---------------------------------------------------------------------------------------------
    // Cards.

    /// <summary>A place was reached: its card, when the whispers say so and the Codex has a sentence for it.</summary>
    public void Arrived(string name, string region, bool first)
    {
        var w = Whispers;
        if (w == Whispers.Quiet || (w == Whispers.Some && !first)) return;
        if (Codex.Codex.Arrival(name, Story.Now?.Gates(Prefs.Spoilers)) is not { } whisper) return;
        arrivalWaiting = new ArrivalCard(name, region, first, whisper, DateTime.UtcNow);
    }

    private void OnHello(HelloAnswer answer)
    {
        if (welcomeDecided || answer is null || Sync.HelloCharacter != characterKey) return;
        welcomeDecided = true;
        var current = clock.Current?.Id;
        var recent = answer.Recent.Where(s => s.Id != current).ToList();
        if (!Core.Welcome.Due(Core.Welcome.LastEnded(recent), DateTime.UtcNow, answer.Prefs.WelcomeHours, Config.ShowWelcome)) return;
        welcomeWaiting = recent;
    }

    private List<RecentSession>? welcomeWaiting;
    private DateTime welcomeWaitingSince;

    private void Cards(DateTime now, PlayerNow player)
    {
        var calm = InGame.Calm;

        // Welcome back: once the story's read (or after a while without it), when calm; it closes as you walk away.
        if (welcomeWaiting is { } recent && calm)
        {
            if (welcomeWaitingSince == default) welcomeWaitingSince = now;
            if (Story.Now != null || now - welcomeWaitingSince > TimeSpan.FromSeconds(15))
            {
                welcomeWaiting = null;
                welcomeWaitingSince = default;
                Welcome = MakeWelcome(recent);
                if (Memory is { } m)
                {
                    m.WelcomedAt = now;
                    MemoryChanged();
                }
            }
        }
        if (Welcome is { At: { } at } && InGame.Position is { } pos && System.Numerics.Vector3.Distance(at, pos) > 8f) Welcome = null;

        // Arrivals wait for a calm moment (two minutes at most), and show for a while.
        if (arrivalWaiting is { } arrival)
        {
            if (now - arrival.At > TimeSpan.FromMinutes(2)) arrivalWaiting = null;
            else if (calm)
            {
                Arrival = arrival with { At = now };
                arrivalWaiting = null;
            }
        }
        if (Arrival is { } shown && now - shown.At > TimeSpan.FromSeconds(14)) Arrival = null;
        if (!calm) Arrival = null;

        if (milestoneWaiting is { } milestone && calm)
        {
            Milestone = milestone;
            milestoneWaiting = null;
        }

        // Who's that?: someone met, targeted.
        WhoCheck(calm);
    }

    private void WhoCheck(bool calm)
    {
        if (!calm || Whispers == Whispers.Quiet)
        {
            Who = null;
            lastTarget = 0;
            return;
        }
        try
        {
            var target = Services.Targets.Target;
            var id = target?.GameObjectId ?? 0;
            if (id == lastTarget) return;
            lastTarget = id;
            Who = null;
            if (target == null || target.ObjectKind == ObjectKind.Pc) return;
            var name = target.Name.TextValue;
            if (Memory?.People.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } met) return;
            Who = MakeWho(met);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the target");
        }
    }

    /// <summary>Who's that? for someone met: when and where, the Codex's words so far, your notes about them, lines saved.</summary>
    public WhoCard MakeWho(MetPerson met)
    {
        var codex = Codex.Codex;
        var entry = codex.Person(met.Name);
        var state = Story.Now;
        // Their first layer that shows: their title when it does (the game's ENpcResident.Title), else the first.
        var layers = entry != null && state != null ? codex.Layers(entry, state.Corpus, state.Seen, state.Gates(Prefs.Spoilers)) : new List<CodexLayer>();
        if (layers.FindIndex(l => l.Source == "title") is > 0 and var title)
        {
            var t = layers[title];
            layers.RemoveAt(title);
            layers.Insert(0, t);
        }
        var notes = (Sync.Hello?.Notes ?? Array.Empty<SiteNote>()).Where(n => string.Equals(n.Subject, met.Name, StringComparison.OrdinalIgnoreCase)
            || (entry != null && entry.Names.Any(x => string.Equals(n.Subject, x, StringComparison.OrdinalIgnoreCase)))).ToList();
        var lines = sessionEvents.Count(e => e.Kind == EventKind.Line && string.Equals(Json.Str(e.Data["speaker"]), met.Name, StringComparison.OrdinalIgnoreCase));
        return new WhoCard(met.Name, met, entry, layers, notes, lines, DateTime.UtcNow);
    }

    private WelcomeCard MakeWelcome(List<RecentSession> recent)
    {
        var state = Story.Now;
        var next = state?.Objectives.Count > 0 ? state.Objectives : (IReadOnlyList<string>)Array.Empty<string>();
        var lastNote = recent.OrderByDescending(r => r.StartedAt).FirstOrDefault(r => r.Note.Trim().Length > 0) is { } withNote
            ? new SiteNote(Guid.Empty, withNote.Note.Trim(), string.Empty, withNote.EndedAt)
            : Core.Welcome.LastNote(Sync.Hello?.Notes ?? Array.Empty<SiteNote>());
        return new WelcomeCard(Core.Welcome.LastEnded(recent) ?? DateTime.UtcNow, recent.Count, Core.Welcome.Glance(recent, journalSince: Memory?.JournalSince),
            state?.Current?.Name, next, lastNote, InGame.Position);
    }

    /// <summary>Opens the Welcome back card now (the window's button: whatever the time away).</summary>
    public void ShowWelcome()
    {
        var current = clock.Current?.Id;
        Welcome = MakeWelcome((Sync.Hello?.Recent ?? Array.Empty<RecentSession>()).Where(s => s.Id != current).ToList()) with { At = null };
    }

    /// <summary>Who hello is for: the character now, with the main story's reading when it's been read.</summary>
    private HelloFor? HelloFor()
    {
        if (Player is not { } p) return null;
        if (helloFor is { } cached && cached.Key == p.Key && helloForVersion == Story.Version) return cached;
        JsonObject? progress = null;
        string? text = null;
        if (Story.Now is { } s)
        {
            progress = JsonNode.Parse(s.ProgressJson) as JsonObject;
            text = progress != null ? s.ProgressJson : null;
        }
        helloForVersion = Story.Version;
        return helloFor = new HelloFor(p.Key, p.Name, p.World, progress, text);
    }

    /// <summary>Who hello is for, made again only when the story's read again (the sync asks each frame).</summary>
    private HelloFor? helloFor;
    private int helloForVersion = -1;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Services.ClientState.Login -= OnLogin;
        Services.ClientState.Logout -= OnLogout;
        talk.Line -= OnTalk;
        Safely(talk.Dispose);
        Safely(Recorder.Dispose);
        // The session under way stays open: a reload picks it up again (a long gap ends it then).
        if (clock.Current is { } cur) Safely(() => Queue.PutSession(cur));
        Safely(Queue.Flush);
        if (memoryDirty) Safely(Config.Save);
        Safely(Photos.Dispose);
        Safely(Story.Dispose);
        Safely(Codex.Dispose);
        Safely(Sync.Dispose);
    }

    private static void Safely(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Story Journal couldn't let go of something while unloading");
        }
    }
}
