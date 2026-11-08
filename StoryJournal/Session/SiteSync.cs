using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StoryJournal.Core;

namespace StoryJournal.Session;

/// <summary>Where the link to the site stands.</summary>
public abstract record LinkState
{
    private LinkState()
    {
    }

    public sealed record Unlinked : LinkState;

    public sealed record Linking : LinkState;

    public sealed record Linked(string Account) : LinkState;

    /// <summary>The site said no to the token, in words for players ("Link again in Settings with a new one.").</summary>
    public sealed record Refused(string Why) : LinkState;

    public bool IsLinked => this is Linked;
}

/// <summary>Who hello is for: the character's key, name and world, and the main story's reading (and its text, as sent) when there is one.</summary>
public sealed record HelloFor(string Key, string Name, string World, JsonObject? Progress, string? ProgressText = null);

/// <summary>
/// The plugin and the site, kept in step in the background, through the site's functions for the plugin
/// (<c>sj_plugin_…</c>, <see cref="SiteRpc"/>) and its photo function:
/// <list type="bullet">
/// <item><c>sj_plugin_hello</c>: at login, after linking again, and every 15 minutes; its answer
/// (preferences, partners, theories due, the last sessions, notes) is kept for the windows.</item>
/// <item><c>sj_plugin_sync</c>: the queue, a session's events at a time (≤ 200, ≤ 500 KB), every 30 s
/// and soon after something happens (a finished quest at once), with the main story's reading when it
/// changed. The site's refusals are followed as <see cref="SyncPolicy"/> says.</item>
/// <item><c>sj_plugin_note</c>, <c>sj_plugin_theory</c>, <c>sj_plugin_theory_answer</c>: what's
/// written in the game, kept until the site has it, in the order <see cref="Outbox"/> says.</item>
/// <item>Photos, one at a time, once the site has their session.</item>
/// </list>
/// Calls run off the framework thread (and so does the queue's disk work for them: the batch is made
/// and weighed on the worker); their answers come back to it in <see cref="Tick"/>. Nothing goes before
/// the plugin's linked. The token is never logged. Each kind of call is paced under the site's hourly
/// limits (hello 240, sync 600, note 240, theory 120 with the answers, photo 120): at most 4 hellos,
/// 360 syncs, 200 notes, 100 theories and answers and 100 photos an hour.
/// </summary>
public sealed class SiteSync : IDisposable
{
    private static readonly TimeSpan HelloEvery = TimeSpan.FromMinutes(15);

    /// <summary>How long something answered "gone" rests before it's tried again (unless its character is synced sooner).</summary>
    private static readonly TimeSpan GoneRest = TimeSpan.FromMinutes(10);

    private readonly Configuration config;
    private readonly EventQueue queue;
    private readonly SiteSession site;
    private readonly ConcurrentQueue<Action> answers = new();

    private readonly Pace hello = new(TimeSpan.FromSeconds(20), HelloEvery);
    private readonly Pace sync = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    private readonly Pace notes = new(TimeSpan.FromSeconds(4), perHour: 200);
    private readonly Pace theories = new(TimeSpan.FromSeconds(4), perHour: 100);
    private readonly Pace photos = new(TimeSpan.FromSeconds(8), backoffFirst: TimeSpan.FromSeconds(30), backoffMost: TimeSpan.FromMinutes(10), perHour: 100);

    /// <summary>Goes up with each link: an answer for an earlier one is let go.</summary>
    private int generation;

    private int batchSize = EventQueue.MaxBatch;

    /// <summary>An event refused on its own as bad data: its session is tried alone next, to see which is at fault.</summary>
    private JournalEvent? probing;

    private DateTime? offUntil;

    /// <summary>The main story's reading as the site last took it, by character.</summary>
    private readonly ConcurrentDictionary<string, string> progressSent = new();

    /// <summary>A reading the site refused with the queue (<see cref="SyncStep.WithoutProgress"/>): not sent again until it changes.</summary>
    private string? progressHeld;

    /// <summary>When the site last took a hello or a sync for each character (since the plugin loaded).</summary>
    private readonly Dictionary<string, DateTime> heard = new();

    /// <summary>Notes and theories answered "gone": their character, and since when they rest.</summary>
    private readonly Dictionary<Guid, (string Character, DateTime Since)> resting = new();

    /// <summary>Photos answered "gone" (their session not on the site), and how many times.</summary>
    private readonly ConcurrentDictionary<Guid, int> photoGone = new();

    /// <summary>The kinds of call whose start failed, said once in the log each.</summary>
    private readonly HashSet<string> startFailed = new();

    public SiteSync(Configuration config, EventQueue queue)
    {
        this.config = config;
        this.queue = queue;
        site = new SiteSession(() => config.Token);
        Relinked();
    }

    /// <summary>The connection, shared (the Codex's fetch uses it too).</summary>
    public SiteSession Site => site;

    public LinkState Link { get; private set; } = new LinkState.Unlinked();

    /// <summary>The site's last answer to hello (null until there's been one).</summary>
    public HelloAnswer? Hello { get; private set; }

    /// <summary>When hello last answered, and for which character.</summary>
    public DateTime? HelloAt { get; private set; }

    public string? HelloCharacter { get; private set; }

    /// <summary>The tool's switched off on the site: what's queued waits.</summary>
    public bool SwitchedOff => offUntil is { } until && DateTime.UtcNow < until;

    /// <summary>The last problem worth saying (null when the last call went through).</summary>
    public string? Problem { get; private set; }

    /// <summary>When the queue last went through.</summary>
    public DateTime? SyncedAt { get; private set; }

    /// <summary>Goes up when <see cref="Link"/>, <see cref="Hello"/> or the problem changes.</summary>
    public int Version { get; private set; }

    /// <summary>A hello answered (framework thread).</summary>
    public event Action<HelloAnswer>? HelloArrived;

    /// <summary>Something was said that's worth a toast (framework thread).</summary>
    public event Action<string>? Said;

    /// <summary>The site refused a session outright (framework thread): if it's the one under way, it ends.</summary>
    public event Action<Guid>? SessionRefused;

    /// <summary>Who to say hello for, and the session to send the main story's reading with (framework thread).</summary>
    public Func<HelloFor?>? Character { get; set; }

    public Func<SessionRecord?>? OpenSession { get; set; }

    /// <summary>The token changed (linked, unlinked): start again.</summary>
    public void Relinked()
    {
        generation++;
        hello.Reset();
        sync.Reset();
        notes.Reset();
        theories.Reset();
        photos.Reset();
        offUntil = null;
        probing = null;
        progressHeld = null;
        batchSize = EventQueue.MaxBatch;
        progressSent.Clear();
        heard.Clear();
        resting.Clear();
        photoGone.Clear();
        Problem = null;
        Link = SiteLink.LooksRight(config.Token) ? new LinkState.Linking() : new LinkState.Unlinked();
        if (Link is LinkState.Linking)
        {
            hello.Changed();
            sync.Changed();
        }
        Version++;
    }

    /// <summary>A new character logged in: hello for it.</summary>
    public void LoggedIn()
    {
        hello.Reset();
        hello.Changed();
        sync.Changed();
    }

    /// <summary>Something happened: the queue goes soon (within the pace's gap).</summary>
    public void Kick() => sync.Changed();

    /// <summary>Notes, theories or answers to send.</summary>
    public void KickNotes()
    {
        notes.Changed();
        theories.Changed();
    }

    private bool Sending => site.Linked && Link is not LinkState.Refused && !SwitchedOff;

    /// <summary>Framework thread, every frame: answers in, then whatever call is due goes out.</summary>
    public void Tick(DateTime now)
    {
        while (answers.TryDequeue(out var a))
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal: handling the site's answer failed");
            }
        }
        if (!Sending || site.Closed) return;

        var who = Character?.Invoke();
        if (who != null) hello.Run(now, () => SayHello(who, now), ex => StartFailed("hello", ex));
        sync.Run(now, () => Sync(now, who), ex => StartFailed("sync", ex));
        if (now - lookedAt >= TimeSpan.FromSeconds(1))
        {
            lookedAt = now;
            if (config.PendingNotes.Count > 0) notes.Changed();
            if (config.PendingAnswers.Count > 0 || config.Characters.Values.Any(c => c.Theories.Any(t => !t.Sent && !t.Aside))) theories.Changed();
            if (queue.PhotoCount > 0) photos.Changed();
        }
        notes.Run(now, () => SendNotes(now), ex => StartFailed("note", ex));
        theories.Run(now, () => SendTheories(now), ex => StartFailed("theory", ex));
        photos.Run(now, () => SendPhoto(now), ex => StartFailed("photo", ex));
    }

    private DateTime lookedAt;

    /// <summary>A call that couldn't even start, or be made (the queue's disk, say): it's tried again after a backoff; said once in the log (framework thread).</summary>
    private void StartFailed(string what, Exception ex)
    {
        if (startFailed.Add(what)) Services.Log.Error(ex, $"Story Journal couldn't make a {what} call to the site; it's tried again later");
    }

    /// <summary>The site took a hello or a sync for this character just now.</summary>
    private void Heard(string character) => heard[character] = DateTime.UtcNow;

    // ---------------------------------------------------------------------------------------------
    // Hello.

    private void SayHello(HelloFor who, DateTime now)
    {
        var gen = generation;
        var character = new JsonObject { ["key"] = who.Key, ["name"] = who.Name, ["world"] = who.World };
        if (who.Progress != null) character["progress"] = who.Progress.DeepClone();
        var progressText = who.ProgressText;
        site.Call("sj_plugin_hello", new JsonObject { ["p_character"] = character }).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : new RpcResult(false, null, "Couldn't reach the site.", Unreachable: true);
            var answer = r.Ok ? HelloAnswer.Read(r.Body) : null;
            answers.Enqueue(() =>
            {
                if (gen != generation) return;
                if (answer != null)
                {
                    hello.Done(true, DateTime.UtcNow);
                    Hello = answer;
                    HelloAt = DateTime.UtcNow;
                    HelloCharacter = who.Key;
                    Heard(who.Key);
                    if (progressText != null) progressSent[who.Key] = progressText;
                    var first = Link is not LinkState.Linked;
                    Link = new LinkState.Linked(answer.Account);
                    Problem = null;
                    Version++;
                    if (first) Said?.Invoke($"Journal linked to {answer.Account}");
                    HelloArrived?.Invoke(answer);
                    return;
                }
                Refused(r, hello);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>A refusal, followed: the token (link again), switched off (wait), too often (wait), else a backoff.</summary>
    private void Refused(RpcResult r, Pace pace)
    {
        var now = DateTime.UtcNow;
        Problem = r.Why;
        Version++;
        if (r.TokenRefused || r.Hint == "token")
        {
            pace.Done(false, now);
            Link = new LinkState.Refused(r.Why ?? "The site doesn't know this token. Link again in Settings with a new one.");
            Services.Log.Warning("Story Journal: the site refused the token; link again");
            return;
        }
        if (r.Hint == "off")
        {
            pace.Done(false, now);
            offUntil = now + SyncPolicy.OffWait;
            return;
        }
        if (r.Hint == "rate")
        {
            pace.RateLimited(now, SyncPolicy.RateWait);
            return;
        }
        pace.Done(false, now);
    }

    // ---------------------------------------------------------------------------------------------
    // The queue.

    /// <summary>
    /// One sync: on the framework thread only what's cheap is read (who, the reading's text, the session
    /// under way); the batch is made and weighed on the worker, then sent.
    /// </summary>
    private void Sync(DateTime now, HelloFor? who)
    {
        var gen = generation;
        var probe = probing;
        var size = batchSize;
        // The main story's reading goes when it changed since the site took it (and isn't one it refused).
        string? progressText = null;
        if (who?.ProgressText is { } text && who.Progress != null && text != progressHeld
            && !(progressSent.TryGetValue(who.Key, out var last) && last == text))
            progressText = text;
        var open = progressText != null ? OpenSession?.Invoke()?.Copy() : null;
        Task.Run(async () =>
        {
            try
            {
                await SyncOnWorker(gen, probe, size, who, progressText, open).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // (The queue's disk, say.) Tried again after the backoff; said once.
                answers.Enqueue(() =>
                {
                    StartFailed("sync", ex);
                    if (gen == generation) sync.Done(false, DateTime.UtcNow);
                });
            }
        });
    }

    private async Task SyncOnWorker(int gen, JournalEvent? probe, int size, HelloFor? who, string? progressText, SessionRecord? open)
    {
        EventBatch? batch;
        if (probe != null)
            batch = queue.Session(probe.Session) is { Refused: false } ps ? new EventBatch(ps.Character, ps, Array.Empty<JournalEvent>()) : null;
        else batch = queue.NextBatch(size);

        // Nothing queued: the session under way goes alone when the main story's reading changed.
        if (batch == null && probe == null && progressText != null && open != null && open.Character == who!.Key && !open.Refused)
            batch = new EventBatch(open.Character, open, Array.Empty<JournalEvent>());
        if (batch == null)
        {
            answers.Enqueue(() =>
            {
                if (gen != generation) return;
                if (probe != null) probing = null;
                sync.Nothing();
            });
            return;
        }
        // The reading read again from its text: the worker's own (the framework thread's is shared).
        JsonObject? progress = null;
        if (progressText != null && who != null && batch.Character == who.Key) progress = Json.Parse(progressText) as JsonObject;
        if (progress == null) progressText = null;

        var args = new JsonObject
        {
            ["p_key"] = batch.Character,
            ["p_session"] = batch.Session.ToSite(),
            ["p_events"] = batch.ToSite(),
            ["p_progress"] = progress,
        };
        var r = await site.Call("sj_plugin_sync", args, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var step = SyncPolicy.Decide(r, batch.Events.Count, progress != null);
        // The queue's disk work here, on the worker.
        var photosAside = 0;
        try
        {
            switch (step)
            {
                case SyncStep.Acknowledge:
                    queue.Acknowledge(batch);
                    if (probe != null) queue.SetAside(new[] { probe }, "data");
                    break;
                case SyncStep.SetAside:
                    queue.SetAside(batch.Events, r.Hint ?? "size");
                    break;
                case SyncStep.RefuseSession:
                    photosAside = queue.RefuseSession(batch.Session.Id, r.Hint ?? "data");
                    break;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Story Journal couldn't update its queue on this PC");
        }
        answers.Enqueue(() =>
        {
            if (gen != generation) return;
            var at = DateTime.UtcNow;
            switch (step)
            {
                case SyncStep.Acknowledge:
                    sync.Done(true, at);
                    probing = null;
                    batchSize = Math.Min(EventQueue.MaxBatch, batchSize * 2);
                    SyncedAt = at;
                    Heard(batch.Character);
                    if (progressText != null) progressSent[batch.Character] = progressText;
                    if (Problem != null)
                    {
                        Problem = null;
                        Version++;
                    }
                    // More waiting: carry on at the pace's gap. Notes and theories that waited for this go too.
                    if (queue.Count > 0) sync.Changed();
                    KickNotes();
                    break;
                case SyncStep.WithoutProgress:
                    Services.Log.Warning($"Story Journal: the site refused the main story's reading with the queue ({r.Hint}); the queue goes without it");
                    progressHeld = progressText;
                    sync.Done(true, at);
                    sync.Changed();
                    break;
                case SyncStep.Split:
                    batchSize = SyncPolicy.Half(batch.Events.Count);
                    sync.Done(true, at);
                    sync.Changed();
                    break;
                case SyncStep.SetAside:
                    Services.Log.Warning($"Story Journal: the site refused an event ({r.Hint}); it's kept aside on this PC");
                    batchSize = EventQueue.MaxBatch;
                    sync.Done(true, at);
                    sync.Changed();
                    break;
                case SyncStep.Probe:
                    probing = batch.Events[0];
                    sync.Done(true, at);
                    sync.Changed();
                    break;
                case SyncStep.RefuseSession:
                    Services.Log.Warning($"Story Journal: the site refused a session ({r.Why}); its events are kept aside on this PC");
                    Said?.Invoke(r.Why ?? "The site refused a session.");
                    if (photosAside > 0)
                        Said?.Invoke(photosAside == 1
                            ? "A photo from that session can't go to the site. It's kept on this PC (queue/aside-photos)."
                            : $"{photosAside} photos from that session can't go to the site. They're kept on this PC (queue/aside-photos).");
                    probing = null;
                    batchSize = EventQueue.MaxBatch;
                    sync.Done(true, at);
                    sync.Changed();
                    SessionRefused?.Invoke(batch.Session.Id);
                    break;
                case SyncStep.Relink:
                case SyncStep.Off:
                case SyncStep.Rate:
                case SyncStep.Retry:
                    Refused(r, sync);
                    break;
            }
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Notes, theories and answers: one call at a time each, the oldest first, in the order Outbox says.

    /// <summary>Resting after a "gone": until its character's next sync, or a while.</summary>
    private bool Resting(Guid id, DateTime now)
    {
        if (!resting.TryGetValue(id, out var r)) return false;
        if (now - r.Since < GoneRest && !(heard.TryGetValue(r.Character, out var h) && h > r.Since)) return true;
        resting.Remove(id);
        return false;
    }

    private void SendNotes(DateTime now)
    {
        var gen = generation;
        var list = config.PendingNotes;
        var states = list.Select(n => new NoteState(n.Session is { } s ? queue.Standing(s) : SessionStanding.Missing, Resting(n.Id, now))).ToList();
        var choice = Outbox.NextNote(states, out var waitingForSync);
        // A note waits for its session to reach the site: the queue goes first.
        if (waitingForSync) sync.Changed();
        if (choice is not { } c)
        {
            notes.Nothing();
            return;
        }
        var note = list[c.Index];
        var body = new JsonObject { ["id"] = note.Id.ToString("D"), ["body"] = note.Body };
        if (c.WithSession && note.Session is { } session) body["session"] = session.ToString("D");
        if (note.Subject.Length > 0) body["subject"] = Json.Tidy(note.Subject, 60);
        Send("sj_plugin_note", new JsonObject { ["p_key"] = note.Character, ["p_note"] = body }, gen, notes,
            () => config.PendingNotes.Remove(note),
            () =>
            {
                note.GoneTries++;
                if (Outbox.Gone(note.GoneTries, heard.ContainsKey(note.Character)) == GoneStep.Wait)
                {
                    resting[note.Id] = (note.Character, DateTime.UtcNow);
                    return;
                }
                config.PendingNotes.Remove(note);
                KeepAside(new JsonObject { ["note"] = body.DeepClone(), ["character"] = note.Character });
                Said?.Invoke("The site no longer has that note's character. The note is kept on this PC (queue/aside.jsonl).");
            });
    }

    private void SendTheories(DateTime now)
    {
        var gen = generation;
        var mine = new Dictionary<Guid, (string Key, SealedTheory Theory)>();
        var waiting = new List<(string Key, SealedTheory Theory)>();
        foreach (var (character, memory) in config.Characters)
        {
            foreach (var t in memory.Theories)
            {
                mine[t.Id] = (character, t);
                if (!t.Sent && !t.Aside) waiting.Add((character, t));
            }
        }
        // An answer to a theory kept aside here can never reach the site: let go.
        if (config.PendingAnswers.RemoveAll(a => mine.TryGetValue(a.Id, out var m) && m.Theory.Aside) > 0) config.Save();
        var states = waiting.Select(w => new TheoryState(w.Theory.Sent, !w.Theory.AwaitingIndex, Resting(w.Theory.Id, now))).ToList();
        var answerFor = config.PendingAnswers
            .Select(a => mine.TryGetValue(a.Id, out var m) ? (m.Theory.Sent ? AnswerFor.Sent : AnswerFor.Unsent) : AnswerFor.Site)
            .ToList();
        if (Outbox.NextTheoryCall(states, answerFor) is not { } c)
        {
            theories.Nothing();
            return;
        }
        if (c.Answer)
        {
            var answer = config.PendingAnswers[c.Index];
            // Gone: the theory isn't on the site (deleted there): the answer is let go.
            Send("sj_plugin_theory_answer", new JsonObject { ["p_id"] = answer.Id.ToString("D"), ["p_status"] = answer.Status }, gen, theories,
                () => config.PendingAnswers.Remove(answer),
                () => config.PendingAnswers.Remove(answer));
            return;
        }
        var (key, theory) = waiting[c.Index];
        var body = new JsonObject { ["id"] = theory.Id.ToString("D"), ["body"] = theory.Body };
        if (theory.Subject.Length > 0) body["subject"] = Json.Tidy(theory.Subject, 60);
        // The main-story index to ask at, as this PC worked it out (the site's own reading may be a
        // sync behind, so the relative count alone could drift); the count too, for older sites.
        if (theory.CheckAfter > 0)
        {
            body["checkAfter"] = theory.CheckAfter;
            if (theory.DueIndex > 0) body["checkAt"] = theory.DueIndex;
        }
        Send("sj_plugin_theory", new JsonObject { ["p_key"] = key, ["p_theory"] = body }, gen, theories,
            () => theory.Sent = true,
            () =>
            {
                theory.GoneTries++;
                if (Outbox.Gone(theory.GoneTries, heard.ContainsKey(key)) == GoneStep.Wait)
                {
                    resting[theory.Id] = (key, DateTime.UtcNow);
                    return;
                }
                theory.Aside = true;
                KeepAside(new JsonObject { ["theory"] = body.DeepClone(), ["character"] = key });
                Said?.Invoke("The site no longer has that theory's character. It's kept on this PC, and still asked about here.");
            });
    }

    private void KeepAside(JsonObject what)
    {
        Task.Run(() =>
        {
            try
            {
                queue.KeepAside(what, "gone");
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal couldn't keep a note or theory aside on this PC");
            }
        });
    }

    /// <summary>
    /// One of the notes' or theories' calls: done (or refused for good as bad data: then it's let go, said
    /// once) leaves the list; "gone" is the caller's (<paramref name="gone"/>: rest, or set aside), and
    /// what comes after goes on; anything else waits for the pace's backoff.
    /// </summary>
    private void Send(string function, JsonObject args, int gen, Pace pace, Action ok, Action gone)
    {
        site.Call(function, args).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : new RpcResult(false, null, "Couldn't reach the site.", Unreachable: true);
            answers.Enqueue(() =>
            {
                if (gen != generation) return;
                var at = DateTime.UtcNow;
                if (r.Ok || r.Hint == "data")
                {
                    if (!r.Ok)
                    {
                        Services.Log.Warning($"Story Journal: the site refused a note or theory: {r.Why}");
                        Said?.Invoke(r.Why ?? "The site refused that.");
                    }
                    ok();
                    config.Save();
                    pace.Done(true, at);
                    pace.Changed();
                    return;
                }
                if (r.Hint == "gone")
                {
                    // Not there (yet, or any more): it rests or is set aside, and the rest go on; the
                    // next sync tells the site about the character.
                    gone();
                    config.Save();
                    pace.Done(true, at);
                    pace.Changed();
                    sync.Changed();
                    return;
                }
                Refused(r, pace);
            });
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------------------------------------
    // Photos.

    private void SendPhoto(DateTime now)
    {
        var gen = generation;
        var photo = queue.NextPhoto(p => queue.Standing(p.Session) == SessionStanding.Known);
        if (photo == null)
        {
            // Their sessions haven't reached the site yet: the queue first.
            photos.Nothing();
            sync.Changed();
            return;
        }
        var token = site.Token;
        var http = site.Http;
        var cancel = site.Closing;
        Task.Run(async () =>
        {
            try
            {
                await SendPhotoOnWorker(gen, photo, token, http, cancel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                answers.Enqueue(() =>
                {
                    StartFailed("photo", ex);
                    if (gen == generation) photos.Done(false, DateTime.UtcNow);
                });
            }
        });
    }

    private async Task SendPhotoOnWorker(int gen, QueuedPhoto photo, string token, HttpClient http, CancellationToken cancel)
    {
        var bytes = queue.PhotoBytes(photo.Id);
        var r = bytes == null
            ? new PhotoResult(PhotoOutcome.Refused, "The photo's file is gone from this PC.", "image")
            : await PhotoUpload.SendAsync(http, token, photo.Character, photo, bytes, cancel).ConfigureAwait(false);
        // Gone again and again though its session reached the site: the site lost the session (or the
        // character); kept aside after a few tries rather than waiting for ever.
        var givenUp = false;
        if (r.Outcome == PhotoOutcome.Later && r.Hint == "gone")
            givenUp = Outbox.PhotoGone(photoGone.AddOrUpdate(photo.Id, 1, (_, n) => n + 1)) == GoneStep.SetAside;
        try
        {
            if (r.Outcome == PhotoOutcome.Kept) queue.PhotoDone(photo.Id);
            else if (r.Outcome == PhotoOutcome.Refused) queue.PhotoDone(photo.Id, r.Hint ?? "image");
            else if (givenUp) queue.PhotoDone(photo.Id, "gone");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Story Journal couldn't update its photos on this PC");
        }
        answers.Enqueue(() =>
        {
            if (gen != generation) return;
            var at = DateTime.UtcNow;
            if (givenUp)
            {
                photos.Done(true, at);
                Services.Log.Warning("Story Journal: the site never found a photo's session; the photo is kept aside on this PC");
                Said?.Invoke("The site can't find that photo's session. The photo is kept on this PC (queue/aside-photos).");
                return;
            }
            switch (r.Outcome)
            {
                case PhotoOutcome.Kept:
                    photos.Done(true, at);
                    Said?.Invoke("Photo added to the journal");
                    break;
                case PhotoOutcome.Refused:
                    photos.Done(true, at);
                    Services.Log.Warning($"Story Journal: the site refused a photo: {r.Why}");
                    Said?.Invoke(r.Why ?? "The site refused a photo.");
                    break;
                case PhotoOutcome.Rate:
                    photos.RateLimited(at, TimeSpan.FromMinutes(15));
                    break;
                case PhotoOutcome.Token:
                    Refused(new RpcResult(false, null, r.Why, "token", true), photos);
                    break;
                default:
                    photos.Done(false, at);
                    break;
            }
        });
    }

    public void Dispose() => site.Dispose();
}
