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
    private readonly Dictionary<Guid, int> percentAtStart = new();
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
        Queue.Problem += p => Services.Log.Warning($"Story Journal: {p}");
        Sync = new SiteSync(config, Queue) { Character = HelloFor, OpenSession = () => clock?.Current };
        Sync.HelloArrived += OnHello;
        Sync.Said += t => AddToast(t, null, null, ToastKind.Plain);
        Codex = new CodexStore(dir, Sync.Site.Http);
        Story = new Story();
        Photos = new Photos(Queue);
        Photos.Taken += OnPhoto;
        Photos.Failed += why => AddToast("No photo", why, null, ToastKind.Bad);
        clock = new SessionClock(TimeSpan.FromMinutes(Math.Clamp(config.IdleMinutes, 5, 240)));
        Recorder = new Recorder(this);
        talk = new TalkReader();
        talk.Line += OnTalk;
        Services.ClientState.Login += OnLogin;
        Services.ClientState.Logout += OnLogout;
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

    public int SessionPhotos { get; private set; }

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
        characterKey = player.Key;
        var memory = Config.Memory(player.Key);
        memory.Name = player.Name;
        memory.World = player.World;
        MemoryChanged();
        placesSeen = null;
        Recorder.Reset();
        welcomeDecided = false;
        sessionEvents.Clear();
        SessionPhotos = 0;
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
            Queue.PutSession(session);
            lastPut = at;
            sessionEvents.Clear();
            SessionPhotos = 0;
            if (Story.Now?.Progress.ExpansionNow is { } e) percentAtStart[session.Id] = Progress.Percent(e.Done, e.Total);
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
        Sync.Kick();
    }

    /// <summary>A quest was finished: its event is made on a worker (its journal read from the sheets), then written.</summary>
    public void QuestFinished(uint quest, DateTime at)
    {
        if (characterKey == null || Config.Paused) return;
        var state = Story.Now;
        var people = Memory?.People.Select(p => p.Name).ToList() ?? new List<string>();
        Task.Run(() =>
        {
            QuestFinished? q = null;
            try
            {
                q = Story.Finished(quest, state, people);
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, $"Story Journal couldn't read quest {quest} for its event");
            }
            if (q == null) return;
            fromWorkers.Enqueue(() =>
            {
                Record(EventKind.Quest, Events.Quest(q), at);
                Story.DoneChanged();
                if (q.Msq && q.ChapterDone && Config.ShowMilestones && q.Chapter != null) Chapter(q, state);
            });
        });
    }

    /// <summary>A chapter closed: its card waits for a calm moment.</summary>
    private void Chapter(QuestFinished q, StoryState? state)
    {
        var expansion = state?.Catalog.ExpansionName(q.Expansion) ?? string.Empty;
        var count = state?.Path.Quests.Count(x => state.Catalog.Quest(q.Id) is { } mq && x.Chapter == mq.Chapter) ?? 0;
        milestoneWaiting = new MilestoneCard(q.Chapter!, expansion, q.Name, q.Journal.LastOrDefault(), count, q.ExpansionDone, DueTheories(), DateTime.UtcNow);
    }

    /// <summary>Theories to ask about now (the site's due ones, and those sealed here whose time has come).</summary>
    public List<DueTheory> DueTheories()
    {
        var memory = Memory;
        if (memory == null) return new List<DueTheory>();
        var local = memory.Theories.Where(t => t.DueIndex > 0).Select(t => new LocalTheory(t.Id, t.Body, t.DueIndex, t.SealedAt, t.SealedQuest, t.Answered)).ToList();
        return Core.Welcome.DueTheories(Sync.Hello?.Theories ?? Array.Empty<DueTheory>(), local, Story.Now?.Progress.Index ?? 0, memory.Answered);
    }

    /// <summary>The session ended: told to the site, and (at logout or /sj end) its card shown.</summary>
    private void Ended(SessionRecord ended, bool card)
    {
        Queue.PutSession(ended);
        Sync.Kick();
        if (!card || !Config.ShowWrapUp) return;
        var events = sessionEvents.Where(e => e.Session == ended.Id).ToList();
        var length = (ended.EndedAt ?? ended.LastAt) - ended.StartedAt;
        if (events.Count == 0 && length < TimeSpan.FromMinutes(10)) return;
        var numbers = Sessions.Count(events, SessionPhotos);
        var now = Story.Now?.Progress.ExpansionNow;
        var nowPercent = now != null ? Progress.Percent(now.Done, now.Total) : 0;
        var before = percentAtStart.TryGetValue(ended.Id, out var p) ? p : nowPercent;
        WrapUp = new WrapUpCard(ended, numbers, Sessions.SuggestTitle(events), now?.Name ?? string.Empty, before, nowPercent, DateTime.UtcNow);
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
        SessionPhotos++;
        Sync.Kick();
        var today = SessionPhotos;
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
        var index = Story.Now?.Progress.Index ?? 0;
        memory.Theories.Add(new SealedTheory
        {
            Id = Guid.NewGuid(), Body = body, Subject = subject, CheckAfter = Math.Clamp(checkAfter, 0, 5000),
            DueIndex = checkAfter > 0 ? index + checkAfter : 0, SealedAt = DateTime.UtcNow, SealedQuest = Story.Now?.Current?.Name ?? string.Empty,
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
            memory.Answered.Add(id);
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
        if (Codex.Codex.Arrival(name) is not { } whisper) return;
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
        var entry = codex.Find(met.Name) ?? codex.Entries.FirstOrDefault(e => e.Kind == "person" && e.Names.Any(n => codex.Mentions(n, met.Name)));
        var state = Story.Now;
        var layers = entry != null && state != null ? codex.Layers(entry, state.Corpus, state.Seen, state.ExpansionReached, Prefs.Spoilers) : new List<CodexLayer>();
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
        return new WelcomeCard(Core.Welcome.LastEnded(recent) ?? DateTime.UtcNow, recent.Count, Core.Welcome.Glance(recent), state?.Current?.Name, next, lastNote, InGame.Position);
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
        if (Story.Now is { } s) progress = JsonNode.Parse(s.ProgressJson) as JsonObject;
        helloForVersion = Story.Version;
        return helloFor = new HelloFor(p.Key, p.Name, p.World, progress);
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
