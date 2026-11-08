using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

/// <summary>The kinds of event the site keeps (<c>sj_events.kind</c>), as the migration lists them.</summary>
public static class EventKind
{
    public const string Quest = "quest";
    public const string Accept = "accept";
    public const string Cutscene = "cutscene";
    public const string Zone = "zone";
    public const string Duty = "duty";
    public const string Level = "level";
    public const string Person = "person";
    public const string Line = "line";

    /// <summary>Photos reach the site only through the photo function, never in a sync (it leaves them out).</summary>
    public const string Photo = "photo";

    public static readonly IReadOnlyList<string> Synced = new[] { Quest, Accept, Cutscene, Zone, Duty, Level, Person, Line };

    public static bool IsSynced(string? kind) => kind != null && Synced.Contains(kind);
}

/// <summary>
/// Something that happened, as the plugin saw it: its id (made here, so a resend adds nothing), its
/// kind and time, the data the site keeps for its kind (<c>sj_clean_event</c>), and which session and
/// character it belongs to (the queue's, not sent inside the event: a sync names them once).
/// </summary>
public sealed record JournalEvent(Guid Id, string Kind, DateTime At, JsonObject Data, Guid Session, string Character)
{
    /// <summary>As the site takes it in <c>p_events</c>: <c>{ id, kind, at, data }</c>.</summary>
    public JsonObject ToSite() => new()
    {
        ["id"] = Id.ToString("D"),
        ["kind"] = Kind,
        ["at"] = Json.Time(At),
        ["data"] = Data.DeepClone(),
    };

    /// <summary>As the queue keeps it on disk: the site's shape, with its session and character.</summary>
    public JsonObject ToDisk()
    {
        var o = ToSite();
        o["session"] = Session.ToString("D");
        o["character"] = Character;
        return o;
    }

    /// <summary>Reads one the queue kept; null when the line isn't one.</summary>
    public static JournalEvent? FromDisk(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        if (Json.Uuid(o["id"]) is not { } id || Json.Uuid(o["session"]) is not { } session) return null;
        if (Json.Str(o["kind"]) is not { Length: > 0 } kind || Json.ReadTime(o["at"]) is not { } at) return null;
        if (Json.Str(o["character"]) is not { } character || !SiteLink.IsKey(character)) return null;
        var data = o["data"] as JsonObject ?? new JsonObject();
        return new JournalEvent(id, kind, at, (JsonObject)data.DeepClone(), session, character);
    }
}

/// <summary>A main-story or side quest finished, as the <c>quest</c> event carries it.</summary>
/// <param name="MsqIndex">Its place in the main story's order (main-story quests only).</param>
/// <param name="Journal">The game's journal entries for it, in order.</param>
/// <param name="People">People the plugin has met who are named in its journal.</param>
public sealed record QuestFinished(
    uint Id,
    string Name,
    int Level,
    bool Msq,
    int? MsqIndex,
    int Expansion,
    string? Chapter,
    bool ChapterDone,
    bool ExpansionDone,
    string Place,
    IReadOnlyList<string> Journal,
    IReadOnlyList<string> People);

/// <summary>
/// The events' data, each in exactly the shape the site keeps for its kind (the migration's
/// <c>sj_clean_event</c>): camelCase names, the optional fields left out rather than null, text tidied
/// and cut to the site's lengths here so nothing is cut in the middle of a word there.
/// </summary>
public static class Events
{
    /// <summary>The most journal entries a quest event carries, and the longest each may be (the site's).</summary>
    public const int JournalCount = 12;

    public const int JournalLength = 1200;

    public static JournalEvent Make(string kind, JsonObject data, DateTime at, Guid session, string character) =>
        new(Guid.NewGuid(), kind, at, data, session, character);

    public static JsonObject Quest(QuestFinished q)
    {
        var o = new JsonObject
        {
            ["id"] = q.Id,
            ["name"] = Json.Tidy(q.Name, 120),
            ["level"] = Math.Clamp(q.Level, 0, 200),
            ["msq"] = q.Msq,
        };
        if (q.MsqIndex is { } index) o["msqIndex"] = Math.Clamp(index, 0, 100000);
        o["expansion"] = Math.Clamp(q.Expansion, 0, 50);
        if (!string.IsNullOrWhiteSpace(q.Chapter)) o["chapter"] = Json.Tidy(q.Chapter, 120);
        if (q.ChapterDone) o["chapterDone"] = true;
        if (q.ExpansionDone) o["expansionDone"] = true;
        o["place"] = Json.Tidy(q.Place, 80);
        o["journal"] = Json.Array(JournalForSite(q.Journal));
        o["people"] = Json.Array(q.People.Select(p => Json.Tidy(p, 60)).Where(p => p.Length > 0).Distinct().Take(20));
        return o;
    }

    /// <summary>
    /// A quest's journal as the site keeps it: 12 entries of 1,200 letters at most. A longer journal keeps
    /// its first entry (how the quest began) and its last eleven (how it ended, which the recaps read),
    /// rather than losing the end as the site's own cut would.
    /// </summary>
    public static List<string> JournalForSite(IReadOnlyList<string> journal)
    {
        var tidy = journal.Select(j => Json.Tidy(j, JournalLength)).Where(j => j.Length > 0).ToList();
        if (tidy.Count <= JournalCount) return tidy;
        var kept = new List<string> { tidy[0] };
        kept.AddRange(tidy.Skip(tidy.Count - (JournalCount - 1)));
        return kept;
    }

    public static JsonObject Accept(uint id, string name, bool msq) => new()
    {
        ["id"] = id,
        ["name"] = Json.Tidy(name, 120),
        ["msq"] = msq,
    };

    public static JsonObject Cutscene(uint? questId, string place, int seconds)
    {
        var o = new JsonObject();
        if (questId is { } q and > 0) o["questId"] = q;
        o["place"] = Json.Tidy(place, 80);
        o["seconds"] = Math.Clamp(seconds, 0, 36000);
        return o;
    }

    public static JsonObject Zone(uint id, string name, string region, bool first) => new()
    {
        ["id"] = id,
        ["name"] = Json.Tidy(name, 80),
        ["region"] = Json.Tidy(region, 80),
        ["first"] = first,
    };

    public static JsonObject Duty(uint id, string name, string type, bool cleared, int seconds) => new()
    {
        ["id"] = id,
        ["name"] = Json.Tidy(name, 120),
        ["type"] = Json.Tidy(type, 40),
        ["cleared"] = cleared,
        ["seconds"] = Math.Clamp(seconds, 0, 86400),
    };

    public static JsonObject Level(string job, int level) => new()
    {
        ["job"] = Json.Tidy(job, 8),
        ["level"] = Math.Clamp(level, 1, 200),
    };

    public static JsonObject Person(string name, string place, uint? questId)
    {
        var o = new JsonObject
        {
            ["name"] = Json.Tidy(name, 60),
            ["place"] = Json.Tidy(place, 80),
        };
        if (questId is { } q and > 0) o["questId"] = q;
        return o;
    }

    public static JsonObject Line(string speaker, string text, uint? questId, string place)
    {
        var o = new JsonObject
        {
            ["speaker"] = Json.Tidy(speaker, 60),
            ["text"] = Json.Tidy(text, 1000),
        };
        if (questId is { } q and > 0) o["questId"] = q;
        o["place"] = Json.Tidy(place, 80);
        return o;
    }

    /// <summary>
    /// The game's content types (the <c>ContentType</c> sheet's rows) by their English names, so a duty's
    /// type is the same words whatever the client's language (the site reads "dungeon", "trial", "raid" and
    /// "guildhest" in them).
    /// </summary>
    private static readonly Dictionary<uint, string> ContentTypes = new()
    {
        [2] = "Dungeons",
        [3] = "Guildhests",
        [4] = "Trials",
        [5] = "Raids",
        [6] = "PvP",
        [7] = "Quest Battles",
        [9] = "Treasure Hunt",
        [16] = "Disciples of the Land",
        [19] = "Gold Saucer",
        [21] = "Deep Dungeons",
        [26] = "Eureka",
        [27] = "The Masked Carnivale",
        [28] = "Ultimate Raids",
        [29] = "Save the Queen",
        [30] = "Variant and Criterion Dungeons",
        [37] = "Chaotic Alliance Raid",
        [38] = "Occult Crescent",
    };

    /// <summary>
    /// The duty's type in a word, as the site shows it ("dungeon", "trial", "raid"), from the game's
    /// content type's row (its English name); "duty" for one not known here.
    /// </summary>
    public static string DutyType(uint contentType) => ContentTypes.TryGetValue(contentType, out var name) ? DutyType(name) : "duty";

    /// <summary>A content type's English name in a word, lower case and singular ("Dungeons" → "dungeon").</summary>
    public static string DutyType(string? contentType)
    {
        var t = Json.Tidy(contentType).ToLowerInvariant();
        if (t.Length == 0) return "duty";
        if (t.EndsWith("ies", StringComparison.Ordinal)) return t[..^3] + "y";
        if (t.EndsWith('s') && !t.EndsWith("ss", StringComparison.Ordinal)) return t[..^1];
        return t;
    }
}
