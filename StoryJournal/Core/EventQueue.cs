using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

/// <summary>A photo waiting to be sent: its picture is a file beside it (<c>&lt;id&gt;.jpg</c>).</summary>
public sealed record QueuedPhoto(Guid Id, Guid Session, string Character, DateTime At, string Caption, int Width, int Height)
{
    public JsonObject ToDisk() => new()
    {
        ["id"] = Id.ToString("D"),
        ["session"] = Session.ToString("D"),
        ["character"] = Character,
        ["at"] = Json.Time(At),
        ["caption"] = Caption,
        ["width"] = Width,
        ["height"] = Height,
    };

    public static QueuedPhoto? FromDisk(JsonNode? n)
    {
        if (n is not JsonObject o || Json.Uuid(o["id"]) is not { } id || Json.Uuid(o["session"]) is not { } session) return null;
        if (Json.Str(o["character"]) is not { } character || !SiteLink.IsKey(character) || Json.ReadTime(o["at"]) is not { } at) return null;
        return new QueuedPhoto(id, session, character, at, Json.Str(o["caption"], string.Empty), Json.Int(o["width"]), Json.Int(o["height"]));
    }
}

/// <summary>A batch to send: one character's events from one session, oldest first.</summary>
public sealed record EventBatch(string Character, SessionRecord Session, IReadOnlyList<JournalEvent> Events)
{
    /// <summary>The events as the site takes them (<c>p_events</c>).</summary>
    public JsonArray ToSite()
    {
        var a = new JsonArray();
        foreach (var e in Events) a.Add(e.ToSite());
        return a;
    }
}

/// <summary>
/// What's waiting for the site, kept on this PC so nothing is lost when the site can't be reached,
/// the game closes or the plugin is reloaded: events (one JSON line each in <c>events.jsonl</c>,
/// appended as they happen), the sessions they belong to (<c>sessions.json</c>), photos (a JPEG and its
/// details each, in <c>photos/</c>), and what the site refused outright (<c>aside.jsonl</c>, kept, never
/// sent again). An event leaves only when the site has said it has it. Files are rewritten whole
/// through a temporary file, so a crash mid-write leaves the old one. Thread-safe: events come from
/// the framework thread, acknowledgements from the sync's worker.
/// </summary>
public sealed class EventQueue
{
    /// <summary>The most events in a batch (the site takes 200).</summary>
    public const int MaxBatch = 200;

    /// <summary>The most a batch's events may weigh (the site takes 600,000 bytes; this leaves room).</summary>
    public const int MaxBatchBytes = 500_000;

    /// <summary>Ended sessions with nothing left to send are forgotten after this many are kept.</summary>
    private const int KeepSessions = 40;

    private readonly object gate = new();
    private readonly string dir;
    private readonly List<JournalEvent> events = new();
    private readonly List<SessionRecord> sessions = new();
    private readonly List<QueuedPhoto> photos = new();
    private int aside;

    public EventQueue(string dir)
    {
        this.dir = dir;
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(PhotoDir);
        Load();
    }

    private string EventsFile => Path.Combine(dir, "events.jsonl");

    private string SessionsFile => Path.Combine(dir, "sessions.json");

    private string AsideFile => Path.Combine(dir, "aside.jsonl");

    private string PhotoDir => Path.Combine(dir, "photos");

    /// <summary>Goes up with every change (the window redraws its counts then).</summary>
    public int Version { get; private set; }

    /// <summary>A line that couldn't be read (written by a newer plugin, or damaged): kept aside, said once in the log.</summary>
    public event Action<string>? Problem;

    public int Count
    {
        get
        {
            lock (gate) return events.Count;
        }
    }

    public int PhotoCount
    {
        get
        {
            lock (gate) return photos.Count;
        }
    }

    /// <summary>How many the site refused outright (kept in <c>aside.jsonl</c>).</summary>
    public int AsideCount
    {
        get
        {
            lock (gate) return aside;
        }
    }

    private void Load()
    {
        lock (gate)
        {
            if (File.Exists(EventsFile))
            {
                var bad = new List<string>();
                foreach (var line in File.ReadAllLines(EventsFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (JournalEvent.FromDisk(Json.Parse(line)) is { } e) events.Add(e);
                    else bad.Add(line);
                }
                // Read twice (a crash between an append and a rewrite): once each.
                var seen = new HashSet<Guid>();
                events.RemoveAll(e => !seen.Add(e.Id));
                if (bad.Count > 0)
                {
                    File.AppendAllLines(AsideFile, bad.Select(b => "{\"unreadable\":" + JsonValue.Create(b)!.ToJsonString() + "}"));
                    RewriteEvents();
                    Problem?.Invoke($"{bad.Count} queued line(s) couldn't be read; kept in aside.jsonl");
                }
            }
            if (File.Exists(SessionsFile) && Json.Parse(File.ReadAllText(SessionsFile)) is JsonArray list)
                foreach (var n in list)
                    if (SessionRecord.FromDisk(n) is { } s && sessions.All(x => x.Id != s.Id))
                        sessions.Add(s);
            foreach (var meta in Directory.EnumerateFiles(PhotoDir, "*.json"))
            {
                if (QueuedPhoto.FromDisk(Json.Parse(SafeRead(meta))) is { } p && File.Exists(PhotoFile(p.Id))) photos.Add(p);
            }
            photos.Sort((a, b) => a.At.CompareTo(b.At));
            if (File.Exists(AsideFile)) aside = File.ReadLines(AsideFile).Count(l => !string.IsNullOrWhiteSpace(l));
        }
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Events.

    /// <summary>Adds an event (written to disk at once, before this returns).</summary>
    public void Add(JournalEvent e)
    {
        lock (gate)
        {
            if (events.Any(x => x.Id == e.Id)) return;
            File.AppendAllText(EventsFile, e.ToDisk().ToJsonString(Json.Compact) + "\n", Encoding.UTF8);
            events.Add(e);
            Version++;
        }
    }

    /// <summary>The events waiting, oldest first (a copy).</summary>
    public List<JournalEvent> Pending()
    {
        lock (gate) return events.ToList();
    }

    /// <summary>A session's events still waiting (a copy).</summary>
    public List<JournalEvent> PendingFor(Guid session)
    {
        lock (gate) return events.Where(e => e.Session == session).ToList();
    }

    /// <summary>
    /// The next batch: the oldest waiting event's session, its events in order up to <paramref name="max"/>
    /// and <see cref="MaxBatchBytes"/>; or, with no events waiting, a session whose details are still to
    /// tell the site (its end, its title), with none. Null when there's nothing to send.
    /// </summary>
    public EventBatch? NextBatch(int max = MaxBatch)
    {
        max = Math.Clamp(max, 1, MaxBatch);
        lock (gate)
        {
            foreach (var first in events)
            {
                var session = sessions.FirstOrDefault(s => s.Id == first.Session) ?? Orphan(first);
                if (session.Refused) continue;
                var batch = new List<JournalEvent>();
                var bytes = 2;
                foreach (var e in events)
                {
                    if (e.Session != first.Session) continue;
                    var size = Json.Bytes(e.ToSite()) + 1;
                    // One event on its own always goes (the site says if it's too big: then it's set aside).
                    if (batch.Count > 0 && (batch.Count >= max || bytes + size > MaxBatchBytes)) break;
                    batch.Add(e);
                    bytes += size;
                }
                return new EventBatch(session.Character, session.Copy(), batch);
            }
            var dirty = sessions.FirstOrDefault(s => s.Dirty && !s.Refused);
            return dirty == null ? null : new EventBatch(dirty.Character, dirty.Copy(), Array.Empty<JournalEvent>());
        }
    }

    /// <summary>
    /// Events whose session's details were lost (sessions.json damaged): a session is made for them from
    /// their own times, ended at the last, so they still reach the site.
    /// </summary>
    private SessionRecord Orphan(JournalEvent first)
    {
        var theirs = events.Where(e => e.Session == first.Session).ToList();
        var start = theirs.Min(e => e.At);
        var end = theirs.Max(e => e.At);
        var s = new SessionRecord { Id = first.Session, Character = first.Character, StartedAt = start, LastAt = end, EndedAt = end };
        sessions.Add(s);
        SaveSessions();
        return s;
    }

    /// <summary>
    /// The site has these (and the session as it was sent): they leave the queue, and the session is
    /// marked known and, unless it changed since, told.
    /// </summary>
    public void Acknowledge(EventBatch batch)
    {
        lock (gate)
        {
            var ids = batch.Events.Select(e => e.Id).ToHashSet();
            if (ids.Count > 0)
            {
                events.RemoveAll(e => ids.Contains(e.Id));
                RewriteEvents();
            }
            if (sessions.FirstOrDefault(s => s.Id == batch.Session.Id) is { } s)
            {
                s.Known = true;
                if (Same(s, batch.Session))
                {
                    s.Dirty = false;
                    if (s.Title != null || s.Moods != null || s.Note != null) s.WordsSent = true;
                }
            }
            Forget();
            SaveSessions();
            Version++;
        }
    }

    /// <summary>The session's details as sent are still its details (nothing changed while the call was out).</summary>
    private static bool Same(SessionRecord now, SessionRecord sent) =>
        now.EndedAt == sent.EndedAt && now.LastAt == sent.LastAt && now.Title == sent.Title && now.Note == sent.Note
        && (now.Moods ?? new List<string>()).SequenceEqual(sent.Moods ?? new List<string>());

    /// <summary>The site refused these outright: kept aside (never sent again, never deleted).</summary>
    public void SetAside(IEnumerable<JournalEvent> refused, string why)
    {
        lock (gate)
        {
            var list = refused.ToList();
            if (list.Count == 0) return;
            var ids = list.Select(e => e.Id).ToHashSet();
            File.AppendAllLines(AsideFile, list.Select(e =>
            {
                var o = e.ToDisk();
                o["why"] = why;
                return o.ToJsonString(Json.Compact);
            }), Encoding.UTF8);
            aside += list.Count;
            events.RemoveAll(e => ids.Contains(e.Id));
            RewriteEvents();
            Version++;
        }
    }

    /// <summary>The site refused a session itself: it and all its events are set aside.</summary>
    public void RefuseSession(Guid id, string why)
    {
        List<JournalEvent> theirs;
        lock (gate)
        {
            if (sessions.FirstOrDefault(s => s.Id == id) is { } s)
            {
                s.Refused = true;
                s.Dirty = false;
                SaveSessions();
            }
            theirs = events.Where(e => e.Session == id).ToList();
        }
        SetAside(theirs, why);
    }

    private void RewriteEvents() => WriteAtomically(EventsFile, string.Concat(events.Select(e => e.ToDisk().ToJsonString(Json.Compact) + "\n")));

    // ---------------------------------------------------------------------------------------------
    // Sessions.

    /// <summary>Keeps a session's details (new, or changed): written at once.</summary>
    public void PutSession(SessionRecord session)
    {
        lock (gate)
        {
            var i = sessions.FindIndex(s => s.Id == session.Id);
            var copy = session.Copy();
            if (i >= 0)
            {
                copy.Known = sessions[i].Known || copy.Known;
                copy.Refused = sessions[i].Refused || copy.Refused;
                // The card's words already sent stay sent, unless they've changed since.
                if (sessions[i].WordsSent && copy.Title == sessions[i].Title && copy.Note == sessions[i].Note
                    && (copy.Moods ?? new List<string>()).SequenceEqual(sessions[i].Moods ?? new List<string>()))
                    copy.WordsSent = true;
                sessions[i] = copy;
            }
            else sessions.Add(copy);
            SaveSessions();
            Version++;
        }
    }

    public SessionRecord? Session(Guid id)
    {
        lock (gate) return sessions.FirstOrDefault(s => s.Id == id)?.Copy();
    }

    /// <summary>The newest session of a character that hasn't ended (a reload picks it up again).</summary>
    public SessionRecord? OpenSession(string character)
    {
        lock (gate) return sessions.Where(s => s.Character == character && !s.Ended && !s.Refused).OrderByDescending(s => s.StartedAt).FirstOrDefault()?.Copy();
    }

    /// <summary>A character's sessions kept here, newest first.</summary>
    public List<SessionRecord> Sessions(string character)
    {
        lock (gate) return sessions.Where(s => s.Character == character).OrderByDescending(s => s.StartedAt).Select(s => s.Copy()).ToList();
    }

    /// <summary>Old sessions with nothing left to tell the site are let go (only the newest are kept).</summary>
    private void Forget()
    {
        var waiting = events.Select(e => e.Session).Concat(photos.Select(p => p.Session)).ToHashSet();
        var done = sessions.Where(s => s.Ended && !s.Dirty && !waiting.Contains(s.Id)).OrderByDescending(s => s.StartedAt).Skip(KeepSessions).Select(s => s.Id).ToHashSet();
        if (done.Count > 0) sessions.RemoveAll(s => done.Contains(s.Id));
    }

    private void SaveSessions()
    {
        var a = new JsonArray();
        foreach (var s in sessions) a.Add(s.ToDisk());
        WriteAtomically(SessionsFile, a.ToJsonString(Json.Compact));
    }

    // ---------------------------------------------------------------------------------------------
    // Photos.

    private string PhotoFile(Guid id) => Path.Combine(PhotoDir, id.ToString("N") + ".jpg");

    private string PhotoMeta(Guid id) => Path.Combine(PhotoDir, id.ToString("N") + ".json");

    /// <summary>Keeps a photo until the site has it (the picture first, then its details, so a half-kept one isn't read).</summary>
    public void AddPhoto(QueuedPhoto photo, byte[] jpeg)
    {
        lock (gate)
        {
            File.WriteAllBytes(PhotoFile(photo.Id), jpeg);
            WriteAtomically(PhotoMeta(photo.Id), photo.ToDisk().ToJsonString(Json.Compact));
            photos.Add(photo);
            Version++;
        }
    }

    /// <summary>The oldest photo waiting (skipping those whose session the site doesn't know yet, when told).</summary>
    public QueuedPhoto? NextPhoto(Func<QueuedPhoto, bool>? ready = null)
    {
        lock (gate) return photos.FirstOrDefault(p => ready == null || ready(p));
    }

    public byte[]? PhotoBytes(Guid id)
    {
        try
        {
            return File.ReadAllBytes(PhotoFile(id));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The site has it (or refused it outright, <paramref name="refused"/>: then it's kept aside, not deleted).</summary>
    public void PhotoDone(Guid id, string? refused = null)
    {
        lock (gate)
        {
            if (refused != null)
            {
                var asideDir = Path.Combine(dir, "aside-photos");
                Directory.CreateDirectory(asideDir);
                TryMove(PhotoFile(id), Path.Combine(asideDir, id.ToString("N") + ".jpg"));
                TryMove(PhotoMeta(id), Path.Combine(asideDir, id.ToString("N") + ".json"));
                aside++;
            }
            else
            {
                TryDelete(PhotoFile(id));
                TryDelete(PhotoMeta(id));
            }
            photos.RemoveAll(p => p.Id == id);
            Version++;
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (File.Exists(from)) File.Move(from, to, true);
        }
        catch (IOException)
        {
            // Left where it is: read again next time.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Read again next time and sent again: the site adds nothing for a photo it has.
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);
        File.Move(tmp, path, true);
    }
}
