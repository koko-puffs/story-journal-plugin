using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

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
/// through a temporary file, flushed to disk before it takes the old one's place, so a crash mid-write
/// leaves the old one. Thread-safe: events come from the framework thread, acknowledgements from the
/// sync's worker; the framework thread never waits for a rewrite (it's written outside the lock, and
/// what's added meanwhile goes into it before it replaces the old file), and the counts are read
/// without the lock.
/// </summary>
public sealed class EventQueue
{
    /// <summary>The most events in a batch (the site takes 200).</summary>
    public const int MaxBatch = 200;

    /// <summary>The most a batch's events may weigh as the site measures them (it takes 600,000 bytes; this leaves room).</summary>
    public const int MaxBatchBytes = 500_000;

    /// <summary>Ended sessions with nothing left to send are forgotten after this many are kept.</summary>
    private const int KeepSessions = 40;

    private readonly object gate = new();

    /// <summary>One rewrite of <c>events.jsonl</c> at a time (taken before <see cref="gate"/>, never inside it).</summary>
    private readonly object writing = new();

    private readonly string dir;
    private readonly List<JournalEvent> events = new();

    /// <summary>Each event's line as it's kept on disk (made once, when it's added).</summary>
    private readonly Dictionary<Guid, string> lines = new();

    private readonly List<SessionRecord> sessions = new();
    private readonly List<QueuedPhoto> photos = new();
    private readonly List<string> loadProblems = new();

    /// <summary>Lines appended while a rewrite is being written (they go into it before it replaces the file).</summary>
    private List<string>? addedWhileWriting;

    private int sessionsWanted;
    private int sessionsWritten;

    /// <summary>The worker writing <c>sessions.json</c>, while it runs (set and cleared under the lock).</summary>
    private Task? sessionsWriter;

    private int count;
    private int photoCount;
    private int aside;
    private int version;

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
    public int Version => Volatile.Read(ref version);

    /// <summary>
    /// What went wrong reading the queue when it was opened (lines that couldn't be read, written by a
    /// newer plugin or damaged: kept aside). Read once whoever says it is listening.
    /// </summary>
    public IReadOnlyList<string> LoadProblems => loadProblems;

    public int Count => Volatile.Read(ref count);

    public int PhotoCount => Volatile.Read(ref photoCount);

    /// <summary>How many the site refused outright (kept in <c>aside.jsonl</c> and <c>aside-photos/</c>).</summary>
    public int AsideCount => Volatile.Read(ref aside);

    private void Changed()
    {
        count = events.Count;
        photoCount = photos.Count;
        version++;
    }

    private void Load()
    {
        var rewrite = false;
        lock (gate)
        {
            EndWithNewline(EventsFile);
            EndWithNewline(AsideFile);
            if (File.Exists(EventsFile))
            {
                var bad = new List<string>();
                foreach (var line in File.ReadAllLines(EventsFile))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (JournalEvent.FromDisk(Json.Parse(line)) is { } e)
                    {
                        // Read twice (a crash between an append and a rewrite): once each.
                        if (lines.ContainsKey(e.Id)) continue;
                        events.Add(e);
                        lines[e.Id] = e.ToDisk().ToJsonString(Json.Compact) + "\n";
                    }
                    else bad.Add(line);
                }
                if (bad.Count > 0)
                {
                    AppendAside(bad.Select(b => "{\"unreadable\":" + JsonValue.Create(b)!.ToJsonString() + "}"));
                    rewrite = true;
                    loadProblems.Add($"{bad.Count} queued line(s) couldn't be read; kept in aside.jsonl");
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
            // A photo whose session's details were lost (sessions.json damaged): a session made from its time.
            var lost = false;
            foreach (var p in photos)
            {
                if (sessions.Any(x => x.Id == p.Session)) continue;
                sessions.Add(new SessionRecord { Id = p.Session, Character = p.Character, StartedAt = p.At, LastAt = p.At, EndedAt = p.At });
                lost = true;
            }
            if (lost) SaveSessions();
            if (File.Exists(AsideFile)) aside = File.ReadLines(AsideFile).Count(l => !string.IsNullOrWhiteSpace(l));
            Changed();
        }
        if (rewrite) RewriteEvents();
        SetAsidePhotosOfRefused();
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

    /// <summary>
    /// A file that a crash left without its last line's end gets one, so the next line appended isn't
    /// joined to that one (the half line is then set aside alone, or read whole).
    /// </summary>
    private static void EndWithNewline(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (f.Length == 0) return;
            f.Seek(-1, SeekOrigin.End);
            if (f.ReadByte() == '\n') return;
            f.Seek(0, SeekOrigin.End);
            f.WriteByte((byte)'\n');
            f.Flush(true);
        }
        catch (FileNotFoundException)
        {
            // Nothing kept yet.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Events.

    /// <summary>
    /// Adds an event (written to disk at once, before this returns). One for a session the site refused
    /// goes straight aside: the site would refuse it too.
    /// </summary>
    public void Add(JournalEvent e)
    {
        var line = e.ToDisk().ToJsonString(Json.Compact) + "\n";
        lock (gate)
        {
            if (lines.ContainsKey(e.Id)) return;
            if (sessions.FirstOrDefault(s => s.Id == e.Session) is { Refused: true })
            {
                AppendAside(new[] { AsideLine(e, "session") });
                aside++;
                Changed();
                return;
            }
            File.AppendAllText(EventsFile, line, Encoding.UTF8);
            addedWhileWriting?.Add(line);
            events.Add(e);
            lines[e.Id] = line;
            Changed();
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
    /// tell the site (its end, its title), with none. Null when there's nothing to send. Events left
    /// waiting for a session the site refused are set aside first. A worker's: it weighs the events (as
    /// the site will) outside the lock.
    /// </summary>
    public EventBatch? NextBatch(int max = MaxBatch)
    {
        max = Math.Clamp(max, 1, MaxBatch);
        SetAsideOfRefused();
        SessionRecord session;
        List<JournalEvent> theirs;
        lock (gate)
        {
            var first = events.FirstOrDefault(e => !IsRefused(e.Session));
            if (first == null)
            {
                var dirty = sessions.FirstOrDefault(s => s.Dirty && !s.Refused);
                return dirty == null ? null : new EventBatch(dirty.Character, dirty.Copy(), Array.Empty<JournalEvent>());
            }
            session = (sessions.FirstOrDefault(s => s.Id == first.Session) ?? Orphan(first)).Copy();
            theirs = events.Where(e => e.Session == first.Session).Take(max).ToList();
        }
        var batch = new List<JournalEvent>();
        var bytes = 2;
        foreach (var e in theirs)
        {
            var size = Json.JsonbBytes(e.ToSite()) + 2;
            // One event on its own always goes (the site says if it's too big: then it's set aside).
            if (batch.Count > 0 && bytes + size > MaxBatchBytes) break;
            batch.Add(e);
            bytes += size;
        }
        return new EventBatch(session.Character, session, batch);
    }

    private bool IsRefused(Guid session) => sessions.FirstOrDefault(s => s.Id == session) is { Refused: true };

    /// <summary>Events still waiting for a session the site refused (recorded after it said so): set aside.</summary>
    private void SetAsideOfRefused()
    {
        List<JournalEvent> refused;
        lock (gate) refused = events.Where(e => IsRefused(e.Session)).ToList();
        if (refused.Count > 0) SetAside(refused, "session");
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
        var ids = batch.Events.Select(e => e.Id).ToHashSet();
        lock (gate)
        {
            if (ids.Count > 0) Remove(ids);
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
            Changed();
        }
        if (ids.Count > 0) RewriteEvents();
    }

    private void Remove(HashSet<Guid> ids)
    {
        events.RemoveAll(e => ids.Contains(e.Id));
        foreach (var id in ids) lines.Remove(id);
    }

    /// <summary>The session's details as sent are still its details (nothing changed while the call was out).</summary>
    private static bool Same(SessionRecord now, SessionRecord sent) =>
        now.EndedAt == sent.EndedAt && now.LastAt == sent.LastAt && now.Title == sent.Title && now.Note == sent.Note
        && (now.Moods ?? new List<string>()).SequenceEqual(sent.Moods ?? new List<string>());

    private static string AsideLine(JournalEvent e, string why)
    {
        var o = e.ToDisk();
        o["why"] = why;
        return o.ToJsonString(Json.Compact);
    }

    /// <summary>The site refused these outright: kept aside (never sent again, never deleted).</summary>
    public void SetAside(IEnumerable<JournalEvent> refused, string why)
    {
        var list = refused.ToList();
        if (list.Count == 0) return;
        lock (gate)
        {
            var ids = list.Select(e => e.Id).Where(lines.ContainsKey).ToHashSet();
            if (ids.Count == 0) return;
            AppendAside(list.Where(e => ids.Contains(e.Id)).Select(e => AsideLine(e, why)));
            aside += ids.Count;
            Remove(ids);
            Changed();
        }
        RewriteEvents();
    }

    /// <summary>Something else the site will never take (a note, a theory): kept aside with why, never sent again.</summary>
    public void KeepAside(JsonObject what, string why)
    {
        var o = (JsonObject)what.DeepClone();
        o["why"] = why;
        lock (gate)
        {
            AppendAside(new[] { o.ToJsonString(Json.Compact) });
            aside++;
            Changed();
        }
    }

    /// <summary>
    /// The site refused a session itself: it, all its events and its photos are set aside (and anything
    /// recorded for it later goes aside too). Says how many photos went.
    /// </summary>
    public int RefuseSession(Guid id, string why)
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
        return SetAsidePhotosOfRefused();
    }

    /// <summary>Lines added to <c>aside.jsonl</c> (under the lock).</summary>
    private void AppendAside(IEnumerable<string> list) =>
        File.AppendAllText(AsideFile, string.Concat(list.Select(l => l + "\n")), Encoding.UTF8);

    /// <summary>
    /// <c>events.jsonl</c> written again from what's waiting, outside the lock: what's waiting is copied
    /// under it, written to a temporary file and flushed, then (under the lock again) the lines appended
    /// meanwhile go in too and it takes the old file's place. A crash at any point leaves a file with
    /// everything waiting in it (and at worst some the site has already, which it adds nothing for).
    /// </summary>
    private void RewriteEvents()
    {
        lock (writing)
        {
            string[] snapshot;
            var added = new List<string>();
            lock (gate)
            {
                snapshot = events.Select(e => lines[e.Id]).ToArray();
                addedWhileWriting = added;
            }
            var tmp = EventsFile + ".tmp";
            try
            {
                using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var line in snapshot) f.Write(Encoding.UTF8.GetBytes(line));
                    f.Flush(true);
                }
                lock (gate)
                {
                    if (added.Count > 0)
                    {
                        using var f = new FileStream(tmp, FileMode.Append, FileAccess.Write, FileShare.None);
                        f.Write(Encoding.UTF8.GetBytes(string.Concat(added)));
                        f.Flush(true);
                    }
                    File.Move(tmp, EventsFile, true);
                    addedWhileWriting = null;
                }
            }
            finally
            {
                lock (gate) addedWhileWriting = null;
            }
        }
    }

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
                if (copy.Refused) copy.Dirty = false;
                // The card's words already sent stay sent, unless they've changed since.
                if (sessions[i].WordsSent && copy.Title == sessions[i].Title && copy.Note == sessions[i].Note
                    && (copy.Moods ?? new List<string>()).SequenceEqual(sessions[i].Moods ?? new List<string>()))
                    copy.WordsSent = true;
                sessions[i] = copy;
            }
            else sessions.Add(copy);
            SaveSessions();
            Changed();
        }
    }

    public SessionRecord? Session(Guid id)
    {
        lock (gate) return sessions.FirstOrDefault(s => s.Id == id)?.Copy();
    }

    /// <summary>Where a session stands with the site (for a note or a photo that names it).</summary>
    public SessionStanding Standing(Guid id)
    {
        lock (gate)
        {
            return sessions.FirstOrDefault(s => s.Id == id) switch
            {
                null => SessionStanding.Missing,
                { Refused: true } => SessionStanding.Refused,
                { Known: true } => SessionStanding.Known,
                _ => SessionStanding.Waiting,
            };
        }
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

    /// <summary>
    /// <c>sessions.json</c> is to be written again (under the lock): it's written on a worker, from the
    /// sessions as they are then, so the framework thread (which keeps sessions as they change) never
    /// waits for the disk. Changes made while it's written are written after. Everything here reads the
    /// sessions from memory; only opening the queue reads the file.
    /// </summary>
    private void SaveSessions()
    {
        sessionsWanted++;
        sessionsWriter ??= Task.Run(WriteSessions);
    }

    private void WriteSessions()
    {
        while (true)
        {
            int wanted;
            List<SessionRecord> copies;
            lock (gate)
            {
                if (sessionsWritten == sessionsWanted)
                {
                    sessionsWriter = null;
                    return;
                }
                wanted = sessionsWanted;
                copies = sessions.Select(x => x.Copy()).ToList();
            }
            try
            {
                var a = new JsonArray();
                foreach (var x in copies) a.Add(x.ToDisk());
                WriteAtomically(SessionsFile, a.ToJsonString(Json.Compact));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Written with the next change (and events and photos whose session's details are lost
                // still go, with a session made from their times).
            }
            lock (gate) sessionsWritten = wanted;
        }
    }

    /// <summary>Waits until <c>sessions.json</c> holds every change so far (unloading; tests).</summary>
    public void Flush()
    {
        Task? writer;
        lock (gate) writer = sessionsWriter;
        writer?.Wait();
    }

    // ---------------------------------------------------------------------------------------------
    // Photos.

    private string PhotoFile(Guid id) => Path.Combine(PhotoDir, id.ToString("N") + ".jpg");

    private string PhotoMeta(Guid id) => Path.Combine(PhotoDir, id.ToString("N") + ".json");

    /// <summary>
    /// Keeps a photo until the site has it (the picture first, then its details, so a half-kept one isn't
    /// read). One for a session the site refused is kept aside at once.
    /// </summary>
    public void AddPhoto(QueuedPhoto photo, byte[] jpeg)
    {
        lock (gate)
        {
            File.WriteAllBytes(PhotoFile(photo.Id), jpeg);
            WriteAtomically(PhotoMeta(photo.Id), photo.ToDisk().ToJsonString(Json.Compact));
            photos.Add(photo);
            Changed();
        }
        if (IsRefusedLocked(photo.Session)) SetAsidePhotosOfRefused();
    }

    private bool IsRefusedLocked(Guid session)
    {
        lock (gate) return IsRefused(session);
    }

    /// <summary>The oldest photo waiting (skipping those whose session the site doesn't know yet, when told).</summary>
    public QueuedPhoto? NextPhoto(Func<QueuedPhoto, bool>? ready = null)
    {
        List<QueuedPhoto> list;
        lock (gate) list = photos.ToList();
        return list.FirstOrDefault(p => ready == null || ready(p));
    }

    /// <summary>Photos waiting for a session the site refused: kept aside (the site would never take them). How many.</summary>
    public int SetAsidePhotosOfRefused()
    {
        List<Guid> ids;
        lock (gate) ids = photos.Where(p => IsRefused(p.Session)).Select(p => p.Id).ToList();
        foreach (var id in ids) PhotoDone(id, "session");
        return ids.Count;
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
            if (photos.All(p => p.Id != id)) return;
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
            Changed();
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

    /// <summary>A file written whole: to a temporary file, flushed to disk, then put in the old one's place.</summary>
    private static void WriteAtomically(string path, string text)
    {
        var tmp = path + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(Encoding.UTF8.GetBytes(text));
            f.Flush(true);
        }
        File.Move(tmp, path, true);
    }
}
