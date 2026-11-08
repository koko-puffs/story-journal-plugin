using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

/// <summary>
/// A session: one sitting with one character, login to logout (or to a long idle), its id made here.
/// <see cref="Title"/>, <see cref="Moods"/> and <see cref="Note"/> are the player's, from the end of
/// session card; null until they give them (then the site is told, once).
/// </summary>
public sealed class SessionRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Character { get; init; } = string.Empty;

    public DateTime StartedAt { get; init; }

    public DateTime? EndedAt { get; set; }

    /// <summary>The last time something happened in it (moved, or an event).</summary>
    public DateTime LastAt { get; set; }

    public string? Title { get; set; }

    public List<string>? Moods { get; set; }

    public string? Note { get; set; }

    /// <summary>Something about it (its end, its last time, the card's words) is still to tell the site.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>The site has it (a sync with it went through): photos and notes can name it.</summary>
    public bool Known { get; set; }

    /// <summary>The site refused it outright (its time is off, or it's someone else's): its events are set aside.</summary>
    public bool Refused { get; set; }

    /// <summary>
    /// The site has the card's words: they aren't sent again with the session's later events, so what you
    /// change on the site afterwards stays.
    /// </summary>
    public bool WordsSent { get; set; }

    public bool Ended => EndedAt != null;

    /// <summary>As the site takes it in <c>p_session</c>: <c>{ id, startedAt, endedAt?, lastAt, title?, moods?, note? }</c>.</summary>
    public JsonObject ToSite()
    {
        var o = new JsonObject
        {
            ["id"] = Id.ToString("D"),
            ["startedAt"] = Json.Time(StartedAt),
        };
        if (EndedAt is { } end) o["endedAt"] = Json.Time(end);
        o["lastAt"] = Json.Time(LastAt < StartedAt ? StartedAt : LastAt);
        if (WordsSent) return o;
        if (Title != null) o["title"] = Json.Tidy(Title, 80);
        if (Moods != null) o["moods"] = Json.Array(Sessions.CleanMoods(Moods));
        if (Note != null) o["note"] = Note.Trim().Length > 2000 ? Note.Trim()[..2000] : Note.Trim();
        return o;
    }

    public JsonObject ToDisk()
    {
        var o = Words(ToSite());
        o["character"] = Character;
        o["dirty"] = Dirty;
        o["known"] = Known;
        o["refused"] = Refused;
        o["wordsSent"] = WordsSent;
        return o;
    }

    /// <summary>The card's words on disk, whether or not they're still to send.</summary>
    private JsonObject Words(JsonObject o)
    {
        if (Title != null) o["title"] = Title;
        if (Moods != null) o["moods"] = Json.Array(Moods);
        if (Note != null) o["note"] = Note;
        return o;
    }

    public static SessionRecord? FromDisk(JsonNode? n)
    {
        if (n is not JsonObject o || Json.Uuid(o["id"]) is not { } id || Json.ReadTime(o["startedAt"]) is not { } start) return null;
        if (Json.Str(o["character"]) is not { } character || !SiteLink.IsKey(character)) return null;
        return new SessionRecord
        {
            Id = id,
            Character = character,
            StartedAt = start,
            EndedAt = Json.ReadTime(o["endedAt"]),
            LastAt = Json.ReadTime(o["lastAt"]) ?? start,
            Title = o.ContainsKey("title") ? Json.Str(o["title"]) : null,
            Moods = o.ContainsKey("moods") ? Json.Strings(o["moods"]) : null,
            Note = o.ContainsKey("note") ? Json.Str(o["note"]) : null,
            Dirty = Json.Bool(o["dirty"], true),
            Known = Json.Bool(o["known"]),
            Refused = Json.Bool(o["refused"]),
            WordsSent = Json.Bool(o["wordsSent"]),
        };
    }

    public SessionRecord Copy() => new()
    {
        Id = Id, Character = Character, StartedAt = StartedAt, EndedAt = EndedAt, LastAt = LastAt, Title = Title,
        Moods = Moods?.ToList(), Note = Note, Dirty = Dirty, Known = Known, Refused = Refused, WordsSent = WordsSent,
    };
}

/// <summary>
/// When sessions begin and end: a session starts with the first thing that happens after login (or
/// after a long idle), and ends at logout, at <c>/sj end</c>, or when nothing has happened for
/// <see cref="Idle"/> (30 minutes unless set): then it ends at the last time something did, and the
/// next thing starts a new one. Pure: the clock is passed in, so it's tested.
/// </summary>
public sealed class SessionClock
{
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(30);

    public SessionClock(TimeSpan? idle = null) => Idle = idle ?? DefaultIdle;

    public TimeSpan Idle { get; set; }

    /// <summary>The session under way (null: none, until something happens).</summary>
    public SessionRecord? Current { get; private set; }

    /// <summary>Picks up a session the plugin had under way before it was reloaded (one not ended, for this character).</summary>
    public void Resume(SessionRecord session)
    {
        if (!session.Ended) Current = session;
    }

    /// <summary>
    /// Something happened at <paramref name="now"/> with <paramref name="character"/>: the session it
    /// belongs to (a new one when there's none, it's another character's, or the last went idle), and the
    /// one that ended because of it, if any.
    /// </summary>
    public (SessionRecord Session, SessionRecord? Ended) Activity(DateTime now, string character)
    {
        SessionRecord? ended = null;
        if (Current is { } cur && (cur.Character != character || now - cur.LastAt > Idle))
        {
            ended = End(cur.Character == character ? cur.LastAt : now);
        }
        if (Current == null)
        {
            Current = new SessionRecord { Character = character, StartedAt = now, LastAt = now };
            return (Current, ended);
        }
        if (now > Current.LastAt) Current.LastAt = now;
        Current.Dirty = true;
        return (Current, ended);
    }

    /// <summary>Has the session under way gone idle by <paramref name="now"/>? Then it's ended (at its last time) and returned.</summary>
    public SessionRecord? CheckIdle(DateTime now) =>
        Current is { } cur && now - cur.LastAt > Idle ? End(cur.LastAt) : null;

    /// <summary>Ends the session under way at <paramref name="at"/> (logout, /sj end); null when there's none.</summary>
    public SessionRecord? End(DateTime at)
    {
        if (Current is not { } cur) return null;
        cur.EndedAt = at < cur.StartedAt ? cur.StartedAt : at;
        if (cur.LastAt < cur.EndedAt) cur.LastAt = cur.EndedAt.Value;
        cur.Dirty = true;
        Current = null;
        return cur;
    }
}

/// <summary>The session's words: its moods and a suggested title.</summary>
public static class Sessions
{
    /// <summary>The end of session card's moods (sent lower case, as the site keeps them).</summary>
    public static readonly IReadOnlyList<string> MoodChoices = new[] { "Cosy", "Exciting", "Sad", "Funny", "Confusing", "Slow", "Epic", "Tense" };

    /// <summary>Moods as the site keeps them (<c>sj_moods</c>): up to 6 short words, lower case, letters, spaces and dashes.</summary>
    public static List<string> CleanMoods(IEnumerable<string> moods) =>
        moods.Select(m => Json.Tidy(m, 20).ToLowerInvariant())
            .Where(m => m.Length > 0 && char.IsAsciiLetterLower(m[0]) && m.All(c => char.IsAsciiLetterLower(c) || c is ' ' or '-'))
            .Distinct()
            .Take(6)
            .ToList();

    /// <summary>
    /// A title for a session from what happened in it (as the site's own suggestion): a chapter that
    /// closed, else a duty cleared, else a place reached for the first time, else the last main-story
    /// quest finished; null when nothing in it says anything.
    /// </summary>
    public static string? SuggestTitle(IEnumerable<JournalEvent> events)
    {
        var list = events.OrderBy(e => e.At).ToList();
        var chapter = list.LastOrDefault(e => e.Kind == EventKind.Quest && Json.Bool(e.Data["chapterDone"]) && Json.Str(e.Data["chapter"]) is { Length: > 0 });
        if (chapter != null) return Json.Tidy(Json.Str(chapter.Data["chapter"]), 80);
        var duty = list.LastOrDefault(e => e.Kind == EventKind.Duty && Json.Bool(e.Data["cleared"]) && Json.Str(e.Data["name"]) is { Length: > 0 });
        if (duty != null) return Json.Tidy(Capitalised(Json.Str(duty.Data["name"])!), 80);
        var place = list.FirstOrDefault(e => e.Kind == EventKind.Zone && Json.Bool(e.Data["first"]) && Json.Str(e.Data["name"]) is { Length: > 0 });
        if (place != null) return Json.Tidy("Into " + Json.Str(place.Data["name"]), 80);
        var quest = list.LastOrDefault(e => e.Kind == EventKind.Quest && Json.Bool(e.Data["msq"]) && Json.Str(e.Data["name"]) is { Length: > 0 });
        if (quest != null) return Json.Tidy(Json.Str(quest.Data["name"]), 80);
        return null;
    }

    /// <summary>"the Navel" → "The Navel" (a duty's name starts a title).</summary>
    public static string Capitalised(string s) => s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>The evening in numbers, for the end of session card.</summary>
    public static SessionNumbers Count(IEnumerable<JournalEvent> events, int photos)
    {
        int quests = 0, msq = 0, cutscenes = 0, duties = 0, places = 0, people = 0, lines = 0;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case EventKind.Quest:
                    quests++;
                    if (Json.Bool(e.Data["msq"])) msq++;
                    break;
                case EventKind.Cutscene:
                    cutscenes++;
                    break;
                case EventKind.Duty when Json.Bool(e.Data["cleared"]):
                    duties++;
                    break;
                case EventKind.Zone when Json.Bool(e.Data["first"]):
                    places++;
                    break;
                case EventKind.Person:
                    people++;
                    break;
                case EventKind.Line:
                    lines++;
                    break;
            }
        }
        return new SessionNumbers(quests, msq, cutscenes, duties, places, people, lines, photos);
    }
}

public readonly record struct SessionNumbers(int Quests, int Msq, int Cutscenes, int Duties, int Places, int People, int Lines, int Photos);
