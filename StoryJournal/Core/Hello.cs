using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

/// <summary>How much the plugin may show ahead of you: the site's one spoiler setting, shared with it.</summary>
public enum Spoilers
{
    /// <summary>Nothing ahead: duties ahead hidden behind Show.</summary>
    Strict,

    /// <summary>The names of duties ahead shown; nothing of the story.</summary>
    Gentle,

    /// <summary>Everything: every Codex layer, every name.</summary>
    Off,
}

/// <summary>How often the world talks back: quiet (only when asked), some (arrivals), chatty (arrivals and people).</summary>
public enum Whispers
{
    Quiet,
    Some,
    Chatty,
}

/// <summary>The site's preferences for you (<c>sj_prefs</c>, set on the site).</summary>
public sealed record Prefs(Spoilers Spoilers, Whispers Whispers, int WelcomeHours, string RecapVoice)
{
    public static Prefs Default { get; } = new(Spoilers.Strict, Whispers.Some, 20, "story");

    public static Prefs Read(JsonNode? n)
    {
        if (n is not JsonObject o) return Default;
        var spoilers = Json.Str(o["spoilers"]) switch
        {
            "gentle" => Spoilers.Gentle,
            "off" => Spoilers.Off,
            _ => Spoilers.Strict,
        };
        var whispers = Json.Str(o["whispers"]) switch
        {
            "quiet" => Whispers.Quiet,
            "chatty" => Whispers.Chatty,
            _ => Whispers.Some,
        };
        return new Prefs(spoilers, whispers, Math.Clamp(Json.Int(o["welcomeHours"], 20), 0, 720), Json.Str(o["recapVoice"]) == "plain" ? "plain" : "story");
    }
}

/// <summary>A story partner as the site shows them to you.</summary>
/// <param name="Ahead">Main-story quests further than you (negative when behind).</param>
/// <param name="Lock">Your pace lock with them is on.</param>
/// <param name="Where">Where they are, only when that isn't past you.</param>
public sealed record Partner(Guid Id, string Name, string? Character, int MsqIndex, int Ahead, int? Level, DateTime? SeenAt, bool Lock, string? Where, string? Expansion);

/// <summary>A sealed theory the site says is due (its check reached).</summary>
public sealed record DueTheory(Guid Id, string Body, DateTime? SealedAt, string SealedQuest);

/// <summary>A quest in one of the last sessions (<c>last</c>: its last journal entry).</summary>
public sealed record RecentQuest(uint Id, string Name, string? Chapter, bool ChapterDone, string? Last);

/// <summary>One of the last three sessions, as hello tells it.</summary>
public sealed record RecentSession(Guid Id, DateTime StartedAt, DateTime? EndedAt, string Title, string Note, IReadOnlyList<string> Moods,
    IReadOnlyList<RecentQuest> Quests, IReadOnlyList<string> Places, IReadOnlyList<string> Duties, IReadOnlyList<string> People);

/// <summary>A note not yet resolved.</summary>
public sealed record SiteNote(Guid Id, string Body, string Subject, DateTime? At);

/// <summary>
/// The site's answer to <c>sj_plugin_hello</c>: whose account, your preferences, your story partners,
/// theories due, how many are sealed, the last three sessions and your unresolved notes.
/// </summary>
public sealed record HelloAnswer(string Account, Prefs Prefs, IReadOnlyList<Partner> Partners, IReadOnlyList<DueTheory> Theories, int Sealed,
    IReadOnlyList<RecentSession> Recent, IReadOnlyList<SiteNote> Notes)
{
    public static HelloAnswer Read(string? body)
    {
        var o = Json.Parse(body) as JsonObject ?? new JsonObject();
        var partners = Json.Objects(o["partners"]).Select(p => new Partner(
            Json.Uuid(p["id"]) ?? Guid.Empty,
            Json.Str(p["name"], "Your partner"),
            Json.Str(p["character"]),
            Json.Int(p["msqIndex"]),
            Json.Int(p["ahead"]),
            Json.Long(p["level"]) is { } l ? (int)l : null,
            Json.ReadTime(p["seenAt"]),
            Json.Bool(p["lock"]),
            Json.Str(p["where"]),
            Json.Str(p["expansion"]))).ToList();
        var theories = Json.Objects(o["theories"])
            .Where(t => Json.Uuid(t["id"]) != null)
            .Select(t => new DueTheory(Json.Uuid(t["id"])!.Value, Json.Str(t["body"], string.Empty), Json.ReadTime(t["sealedAt"]), Json.Str(t["sealedQuest"], string.Empty)))
            .ToList();
        var recent = Json.Objects(o["recent"]).Select(s => new RecentSession(
            Json.Uuid(s["id"]) ?? Guid.Empty,
            Json.ReadTime(s["startedAt"]) ?? DateTime.MinValue,
            Json.ReadTime(s["endedAt"]),
            Json.Str(s["title"], string.Empty),
            Json.Str(s["note"], string.Empty),
            Json.Strings(s["moods"]),
            Json.Objects(s["quests"]).Select(q => new RecentQuest(
                (uint)Math.Max(0, Json.Long(q["id"]) ?? 0),
                Json.Str(q["name"], string.Empty),
                Json.Str(q["chapter"]),
                Json.Bool(q["chapterDone"]),
                Json.Str(q["last"]))).ToList(),
            Json.Strings(s["places"]),
            Json.Strings(s["duties"]),
            Json.Strings(s["people"]))).ToList();
        var notes = Json.Objects(o["notes"])
            .Where(n => Json.Uuid(n["id"]) != null)
            .Select(n => new SiteNote(Json.Uuid(n["id"])!.Value, Json.Str(n["body"], string.Empty), Json.Str(n["subject"], string.Empty), Json.ReadTime(n["at"])))
            .ToList();
        return new HelloAnswer(Json.Str(o["account"], "your account"), Prefs.Read(o["prefs"]), partners, theories, Json.Int(o["sealed"]), recent, notes);
    }
}

/// <summary>A theory sealed in the game, kept here so it's asked about when its time comes (the site's hello says so too).</summary>
public sealed record LocalTheory(Guid Id, string Body, int DueIndex, DateTime SealedAt, string SealedQuest, bool Answered = false);

/// <summary>The Welcome back card's words, and when it shows.</summary>
public static class Welcome
{
    /// <summary>
    /// Shows after the player's been away <paramref name="welcomeHours"/> or more since their last
    /// session ended (0: every time they log in); not when they've never played with the plugin (there's
    /// nothing to come back to), nor when it's switched off here.
    /// </summary>
    public static bool Due(DateTime? lastEnded, DateTime now, int welcomeHours, bool enabled)
    {
        if (!enabled || lastEnded is not { } last) return false;
        if (welcomeHours <= 0) return true;
        return now - last >= TimeSpan.FromHours(welcomeHours);
    }

    /// <summary>When the last session ended (or was last heard of), newest first.</summary>
    public static DateTime? LastEnded(IReadOnlyList<RecentSession> recent) =>
        recent.Count == 0 ? null : recent.Max(s => s.EndedAt ?? s.StartedAt);

    /// <summary>
    /// Three lines of story at a glance: the last journal lines of the last main-story quests finished
    /// (oldest of the three first), and, when the sessions finished none, what they did instead (a duty
    /// cleared, a place reached, someone met). A place is "for the first time" only in a session after
    /// the first one this journal kept for the character (<paramref name="journalSince"/>, when it
    /// began): <c>first</c> means first since the journal began, so in its first session every place is.
    /// </summary>
    public static List<string> Glance(IReadOnlyList<RecentSession> recent, int max = 3, DateTime? journalSince = null)
    {
        var lines = new List<string>();
        foreach (var s in recent.OrderBy(s => s.StartedAt))
            foreach (var q in s.Quests)
                if (JournalText.Tidy(q.Last) is { Length: > 0 } last && !lines.Contains(last)) lines.Add(last);
        if (lines.Count >= max) return lines.Skip(lines.Count - max).ToList();
        var extra = new List<string>();
        foreach (var s in recent.OrderBy(s => s.StartedAt))
        {
            foreach (var d in s.Duties.Where(d => d.Length > 0)) extra.Add($"You cleared {d}.");
            var firstTime = journalSince is { } since && s.StartedAt > since;
            foreach (var p in s.Places.Where(p => p.Length > 0)) extra.Add(firstTime ? $"You reached {p} for the first time." : $"You reached {p}.");
            if (s.People.Count > 0) extra.Add($"You met {Join(s.People.Take(3).ToList())}.");
        }
        foreach (var e in extra.Distinct())
        {
            if (lines.Count >= max) break;
            lines.Add(e);
        }
        return lines;
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>Your last unresolved note (the newest), if any.</summary>
    public static SiteNote? LastNote(IReadOnlyList<SiteNote> notes) => notes.OrderByDescending(n => n.At ?? DateTime.MinValue).FirstOrDefault();

    /// <summary>
    /// The theories to ask about now: those the site said are due, and those sealed here whose time has
    /// come (<paramref name="msqIndex"/> at or past their due index), each once, not those answered, nor
    /// those sealed again (<paramref name="snoozed"/>: asked again when the next chapter closes).
    /// </summary>
    public static List<DueTheory> DueTheories(IReadOnlyList<DueTheory> fromSite, IReadOnlyList<LocalTheory> local, int msqIndex,
        IReadOnlySet<Guid>? answered = null, IReadOnlySet<Guid>? snoozed = null)
    {
        var list = new List<DueTheory>();
        var ids = new HashSet<Guid>();
        bool Out(Guid id) => answered?.Contains(id) == true || snoozed?.Contains(id) == true;
        foreach (var t in fromSite)
            if (!Out(t.Id) && ids.Add(t.Id)) list.Add(t);
        foreach (var t in local)
            if (!t.Answered && t.DueIndex <= msqIndex && !Out(t.Id) && ids.Add(t.Id))
                list.Add(new DueTheory(t.Id, t.Body, t.SealedAt, t.SealedQuest));
        return list;
    }
}

/// <summary>The pace lock: a warning before you go past a story partner who asked you to wait.</summary>
public static class PaceLock
{
    /// <summary>A partner the lock is about: already past them (<see cref="Past"/>), or about to go past.</summary>
    public sealed record Warning(Partner Partner, bool Past);

    /// <summary>
    /// For each partner with your lock on: you're already past them (their index below yours), or the
    /// next main-story quest would take you past (its index above theirs).
    /// </summary>
    public static List<Warning> Check(IReadOnlyList<Partner> partners, int myIndex, int? nextIndex)
    {
        var list = new List<Warning>();
        foreach (var p in partners)
        {
            if (!p.Lock) continue;
            if (p.MsqIndex < myIndex) list.Add(new Warning(p, true));
            else if (nextIndex is { } next && next > p.MsqIndex) list.Add(new Warning(p, false));
        }
        return list;
    }

    /// <summary>The tracker's line: "Pace lock: Mira is 2 quests behind" or "Pace lock: wait for Mira after this quest".</summary>
    public static string Words(Warning w, int myIndex) =>
        w.Past
            ? $"Pace lock: {w.Partner.Name} is {Quests(myIndex - w.Partner.MsqIndex)} behind"
            : $"Pace lock: wait for {w.Partner.Name} after this quest";

    private static string Quests(int n) => n == 1 ? "1 quest" : $"{n} quests";
}
