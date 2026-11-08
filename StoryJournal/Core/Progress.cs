using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

/// <summary>The quest the player is on, as <c>progress.current</c> has it.</summary>
public sealed record CurrentQuest(
    uint Id,
    string Name,
    int Level,
    string Place,
    int Sequence,
    string? Chapter,
    int Expansion,
    string ExpansionName,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> Journal,
    string? Previous,
    IReadOnlyList<string> People);

/// <summary>
/// The plugin's reading of the main story (<c>sj_characters.progress</c>), in exactly the shape the
/// site reads (the brief's <c>Progress</c>): the job and level, how far through the main story, each
/// expansion and chapter, the current quest with its journal so far, the duties ahead and a good place
/// to stop. Kept under the site's 32 KB.
/// </summary>
public sealed record Progress(
    int Level,
    string Job,
    bool Complete,
    int Done,
    int Total,
    int Index,
    IReadOnlyList<ExpansionCount> Expansions,
    IReadOnlyList<ChapterCount> Chapters,
    CurrentQuest? Current,
    IReadOnlyList<Ahead> Ahead,
    NextStop? NextStop)
{
    /// <summary>The most the site keeps (32,768 bytes); a reading is made to fit well under it.</summary>
    public const int MaxBytes = 30_000;

    /// <summary>The expansion the player's in now (the current quest's, else the last with anything done).</summary>
    public ExpansionCount? ExpansionNow =>
        Current != null ? Expansions.FirstOrDefault(e => e.Id == Current.Expansion)
        : Expansions.LastOrDefault(e => e.Done > 0) ?? Expansions.LastOrDefault();

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["level"] = Level,
            ["job"] = Job,
            ["complete"] = Complete,
            ["msq"] = new JsonObject { ["done"] = Done, ["total"] = Total, ["index"] = Math.Clamp(Index, 0, 100000) },
        };
        var ex = new JsonArray();
        foreach (var e in Expansions) ex.Add(new JsonObject { ["id"] = e.Id, ["name"] = e.Name, ["done"] = e.Done, ["total"] = e.Total });
        o["expansions"] = ex;
        var ch = new JsonArray();
        foreach (var c in Chapters)
            ch.Add(new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["expansion"] = c.Expansion, ["done"] = c.Done, ["total"] = c.Total });
        o["chapters"] = ch;
        if (Current is { } q)
        {
            o["current"] = new JsonObject
            {
                ["id"] = q.Id,
                ["name"] = q.Name,
                ["level"] = q.Level,
                ["place"] = q.Place,
                ["sequence"] = q.Sequence,
                ["chapter"] = q.Chapter,
                ["expansion"] = q.Expansion,
                ["expansionName"] = q.ExpansionName,
                ["objectives"] = Json.Array(q.Objectives),
                ["journal"] = Json.Array(q.Journal),
                ["previous"] = q.Previous,
                ["people"] = Json.Array(q.People),
            };
        }
        else o["current"] = null;
        var ahead = new JsonArray();
        foreach (var a in Ahead) ahead.Add(new JsonObject { ["questsAway"] = a.QuestsAway, ["level"] = a.Level, ["duty"] = a.Duty, ["type"] = a.Type });
        o["ahead"] = ahead;
        o["nextStop"] = NextStop is { } s ? new JsonObject { ["questsAway"] = s.QuestsAway, ["reason"] = s.Reason } : null;
        return o;
    }

    /// <summary>
    /// The reading as sent, made to fit <see cref="MaxBytes"/>: the current quest's earliest journal
    /// entries go first (the latest say where you are), then the chapters' names not reached (already
    /// null), then the chapters of expansions not reached, then the chapters altogether.
    /// </summary>
    public JsonObject ToSite()
    {
        var p = this;
        var json = p.ToJson();
        while (Json.Bytes(json) > MaxBytes && p.Current is { Journal.Count: > 1 } q)
        {
            p = p with { Current = q with { Journal = q.Journal.Skip(1).ToList() } };
            json = p.ToJson();
        }
        if (Json.Bytes(json) > MaxBytes && p.ExpansionNow is { } now)
        {
            p = p with { Chapters = p.Chapters.Where(c => c.Expansion <= now.Id).ToList() };
            json = p.ToJson();
        }
        if (Json.Bytes(json) > MaxBytes)
        {
            p = p with { Chapters = Array.Empty<ChapterCount>() };
            json = p.ToJson();
        }
        return json;
    }

    /// <summary>
    /// Reads the main story: the road's counts, the current quest (its journal, objectives, people), the
    /// duties ahead and where to stop. <paramref name="current"/> is the catalog's quest the player's on;
    /// <paramref name="currentText"/> its journal so far and objectives, read from the game.
    /// </summary>
    public static Progress Read(MsqCatalog catalog, MsqPath path, MsqQuest? current, int level, string job,
        int sequence, IReadOnlyList<string> journal, IReadOnlyList<string> objectives, string? previousLine, IReadOnlyList<string> people)
    {
        CurrentQuest? cur = null;
        if (current != null)
        {
            var chapter = current.Chapter != 0 ? catalog.Chapter(current.Chapter)?.Name : null;
            cur = new CurrentQuest(current.Id, current.Name, current.Level, current.Place, sequence, chapter, current.Expansion,
                catalog.ExpansionName(current.Expansion), objectives, journal, previousLine, people);
        }
        return new Progress(level, job, path.Complete, path.Done, path.Total, path.Index(current), path.Expansions(), path.Chapters(current), cur,
            path.Ahead(current), path.Stop(current));
    }

    /// <summary>A whole number percentage, never 100 until it's all done.</summary>
    public static int Percent(int done, int total)
    {
        if (total <= 0) return 0;
        if (done >= total) return 100;
        return Math.Min(99, (int)Math.Floor(done * 100.0 / total));
    }
}
