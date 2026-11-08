using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
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

/// <summary>Who hello is for: the character's key, name and world, and the main story's reading when there is one.</summary>
public sealed record HelloFor(string Key, string Name, string World, JsonObject? Progress);

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
/// written in the game, kept until the site has it.</item>
/// <item>Photos, one at a time, once the site has their session.</item>
/// </list>
/// Calls run off the framework thread (and so does the queue's disk work after them); their answers come
/// back to it in <see cref="Tick"/>. Nothing goes before the plugin's linked. The token is never logged.
/// Each kind of call is paced well under the site's hourly limits (hello 240, sync 600, note 240,
/// theory 120, photo 120).
/// </summary>
public sealed class SiteSync : IDisposable
{
    private static readonly TimeSpan HelloEvery = TimeSpan.FromMinutes(15);

    private readonly Configuration config;
    private readonly EventQueue queue;
    private readonly SiteSession site;
    private readonly ConcurrentQueue<Action> answers = new();

    private readonly Pace hello = new(TimeSpan.FromSeconds(20), HelloEvery);
    private readonly Pace sync = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    private readonly Pace notes = new(TimeSpan.FromSeconds(4));
    private readonly Pace photos = new(TimeSpan.FromSeconds(8), backoffFirst: TimeSpan.FromSeconds(30), backoffMost: TimeSpan.FromMinutes(10));

    /// <summary>Goes up with each link: an answer for an earlier one is let go.</summary>
    private int generation;

    private int batchSize = EventQueue.MaxBatch;

    /// <summary>An event refused on its own as bad data: its session is tried alone next, to see which is at fault.</summary>
    private JournalEvent? probing;

    private DateTime? offUntil;

    /// <summary>The main story's reading as the site last took it, by character.</summary>
    private readonly ConcurrentDictionary<string, string> progressSent = new();

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
        photos.Reset();
        offUntil = null;
        probing = null;
        batchSize = EventQueue.MaxBatch;
        progressSent.Clear();
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
    public void KickNotes() => notes.Changed();

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
        if (who != null && hello.Due(now)) SayHello(who, now);
        if (sync.Due(now)) Sync(now, who);
        if (now - lookedAt >= TimeSpan.FromSeconds(1))
        {
            lookedAt = now;
            if (config.PendingNotes.Count > 0 || config.PendingAnswers.Count > 0 || config.Characters.Values.Any(c => c.Theories.Any(t => !t.Sent)))
                notes.Changed();
            if (queue.PhotoCount > 0) photos.Changed();
        }
        if (notes.Due(now)) SendNotes(now);
        if (photos.Due(now)) SendPhoto(now);
    }

    private DateTime lookedAt;

    // ---------------------------------------------------------------------------------------------
    // Hello.

    private void SayHello(HelloFor who, DateTime now)
    {
        var gen = generation;
        var character = new JsonObject { ["key"] = who.Key, ["name"] = who.Name, ["world"] = who.World };
        if (who.Progress != null) character["progress"] = who.Progress.DeepClone();
        var progressText = who.Progress?.ToJsonString(Json.Compact);
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

    private void Sync(DateTime now, HelloFor? who)
    {
        var gen = generation;
        EventBatch? batch;
        if (probing is { } probe)
        {
            batch = queue.Session(probe.Session) is { } ps ? new EventBatch(ps.Character, ps, Array.Empty<JournalEvent>()) : null;
            if (batch == null) probing = null;
        }
        else batch = queue.NextBatch(batchSize);

        // Nothing queued: the session under way goes alone when the main story's reading changed.
        JsonObject? progress = null;
        string? progressText = null;
        if (who?.Progress != null)
        {
            progressText = who.Progress.ToJsonString(Json.Compact);
            if (progressSent.TryGetValue(who.Key, out var last) && last == progressText) progressText = null;
        }
        if (batch == null && progressText != null && OpenSession?.Invoke() is { } open && open.Character == who!.Key)
            batch = new EventBatch(open.Character, open, Array.Empty<JournalEvent>());
        if (batch == null)
        {
            sync.Done(true, now);
            return;
        }
        if (progressText != null && who != null && batch.Character == who.Key) progress = who.Progress;
        else progressText = null;

        var args = new JsonObject
        {
            ["p_key"] = batch.Character,
            ["p_session"] = batch.Session.ToSite(),
            ["p_events"] = batch.ToSite(),
            ["p_progress"] = progress?.DeepClone(),
        };
        var wasProbe = probing;
        site.Call("sj_plugin_sync", args, TimeSpan.FromSeconds(30)).ContinueWith(t =>
        {
            var r = t.IsCompletedSuccessfully ? t.Result : new RpcResult(false, null, "Couldn't reach the site.", Unreachable: true);
            var step = SyncPolicy.Decide(r, batch.Events.Count);
            // The queue's disk work here, on the worker.
            try
            {
                switch (step)
                {
                    case SyncStep.Acknowledge:
                        queue.Acknowledge(batch);
                        if (wasProbe != null) queue.SetAside(new[] { wasProbe }, "data");
                        break;
                    case SyncStep.SetAside:
                        queue.SetAside(batch.Events, r.Hint ?? "size");
                        break;
                    case SyncStep.RefuseSession:
                        queue.RefuseSession(batch.Session.Id, r.Hint ?? "data");
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
                        if (progressText != null) progressSent[batch.Character] = progressText;
                        if (Problem != null)
                        {
                            Problem = null;
                            Version++;
                        }
                        // More waiting: carry on at the pace's gap.
                        if (queue.Count > 0) sync.Changed();
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
                        probing = null;
                        batchSize = EventQueue.MaxBatch;
                        sync.Done(true, at);
                        sync.Changed();
                        break;
                    case SyncStep.Relink:
                    case SyncStep.Off:
                    case SyncStep.Rate:
                    case SyncStep.Retry:
                        Refused(r, sync);
                        break;
                }
            });
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------------------------------------
    // Notes, theories and answers: one call at a time, the oldest first.

    private void SendNotes(DateTime now)
    {
        var gen = generation;
        if (config.PendingAnswers.FirstOrDefault() is { } answer)
        {
            Send("sj_plugin_theory_answer", new JsonObject { ["p_id"] = answer.Id.ToString("D"), ["p_status"] = answer.Status }, gen, ok =>
            {
                if (ok) config.PendingAnswers.Remove(answer);
            });
            return;
        }
        if (config.PendingNotes.FirstOrDefault() is { } note)
        {
            var body = new JsonObject { ["id"] = note.Id.ToString("D"), ["body"] = note.Body };
            if (note.Session is { } s && queue.Session(s) is { Known: true }) body["session"] = s.ToString("D");
            if (note.Subject.Length > 0) body["subject"] = Json.Tidy(note.Subject, 60);
            Send("sj_plugin_note", new JsonObject { ["p_key"] = note.Character, ["p_note"] = body }, gen, ok =>
            {
                if (ok) config.PendingNotes.Remove(note);
            });
            return;
        }
        foreach (var (key, memory) in config.Characters)
        {
            if (memory.Theories.FirstOrDefault(t => !t.Sent) is not { } theory) continue;
            var body = new JsonObject { ["id"] = theory.Id.ToString("D"), ["body"] = theory.Body };
            if (theory.Subject.Length > 0) body["subject"] = Json.Tidy(theory.Subject, 60);
            // The main-story index to ask at, as this PC worked it out (the site's own reading may be a
            // sync behind, so the relative count alone could drift); the count too, for older sites.
            if (theory.CheckAfter > 0)
            {
                body["checkAfter"] = theory.CheckAfter;
                if (theory.DueIndex > 0) body["checkAt"] = theory.DueIndex;
            }
            Send("sj_plugin_theory", new JsonObject { ["p_key"] = key, ["p_theory"] = body }, gen, ok =>
            {
                if (ok) theory.Sent = true;
            });
            return;
        }
        notes.Done(true, now);
    }

    /// <summary>
    /// One of the notes' calls: done (or refused for good as bad data: then it's let go, said once) leaves the
    /// list; anything else waits (<c>gone</c>: the site doesn't know the character until its first sync).
    /// </summary>
    private void Send(string function, JsonObject args, int gen, Action<bool> done)
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
                    done(true);
                    config.Save();
                    notes.Done(true, at);
                    notes.Changed();
                    return;
                }
                if (r.Hint == "gone")
                {
                    // The character or the theory isn't there yet (or any more): after the next sync.
                    if (function == "sj_plugin_theory_answer")
                    {
                        done(true);
                        config.Save();
                    }
                    notes.Done(false, at);
                    sync.Changed();
                    return;
                }
                Refused(r, notes);
            });
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------------------------------------
    // Photos.

    private void SendPhoto(DateTime now)
    {
        var gen = generation;
        var photo = queue.NextPhoto(p => queue.Session(p.Session) is { Known: true });
        if (photo == null)
        {
            // Their sessions haven't reached the site yet: the queue first.
            photos.Done(true, now);
            sync.Changed();
            return;
        }
        var token = site.Token;
        var http = site.Http;
        var cancel = site.Closing;
        Task.Run(async () =>
        {
            var bytes = queue.PhotoBytes(photo.Id);
            var r = bytes == null
                ? new PhotoResult(PhotoOutcome.Refused, "The photo's file is gone from this PC.", "image")
                : await PhotoUpload.SendAsync(http, token, photo.Character, photo, bytes, cancel).ConfigureAwait(false);
            try
            {
                if (r.Outcome == PhotoOutcome.Kept) queue.PhotoDone(photo.Id);
                else if (r.Outcome == PhotoOutcome.Refused) queue.PhotoDone(photo.Id, r.Hint ?? "image");
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal couldn't update its photos on this PC");
            }
            answers.Enqueue(() =>
            {
                if (gen != generation) return;
                var at = DateTime.UtcNow;
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
        });
    }

    public void Dispose() => site.Dispose();
}
