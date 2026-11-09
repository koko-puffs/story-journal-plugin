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
    IReadOnlyList<string> People,
    MapSpot? Spot = null);

/// <summary>The current step's place as the game's map shows it ("X 11.0 Y 15.3"), one decimal.</summary>
public sealed record MapSpot(float X, float Y)
{
    public static MapSpot Of(float x, float y) => new(MathF.Round(x, 1), MathF.Round(y, 1));

    public JsonObject ToJson() => new() { ["x"] = Math.Round((double)X, 1), ["y"] = Math.Round((double)Y, 1) };

    /// <summary>"X 11.0 Y 15.3".</summary>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"X {X:0.0} Y {Y:0.0}");
}

/// <summary>
/// The plugin's reading of the main story (<c>sj_characters.progress</c>), in exactly the shape the
/// site reads (the brief's <c>Progress</c>): the job and level, how far through the main story, each
/// expansion and chapter, the current quest with its journal so far, the duties ahead and a good place
/// to stop. Kept under the site's 32 KB (as it measures it).
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
    /// <summary>
    /// The most a reading may be as the site measures it (<see cref="Json.JsonbBytes"/>, jsonb's text: the
    /// site keeps 32,768 bytes); a reading is made to fit under this, which leaves room.
    /// </summary>
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
            // Where the step is, as the map shows it: only when the game says.
            if (q.Spot is { } spot) ((JsonObject)o["current"]!)["spot"] = spot.ToJson();
        }
        else o["current"] = null;
        var ahead = new JsonArray();
        foreach (var a in Ahead) ahead.Add(new JsonObject { ["questsAway"] = a.QuestsAway, ["level"] = a.Level, ["duty"] = a.Duty, ["type"] = a.Type });
        o["ahead"] = ahead;
        o["nextStop"] = NextStop is { } s ? new JsonObject { ["questsAway"] = s.QuestsAway, ["reason"] = s.Reason } : null;
        if (PluginVersion is { Length: > 0 and <= 20 } v) o["plugin"] = v;
        return o;
    }

    /// <summary>
    /// The reading as sent, made to fit <see cref="MaxBytes"/> as the site measures it: first the chapters
    /// of expansions not reached go (their names are null already), then the chapters altogether, then the
    /// current quest's earliest journal entries (the latest say where you are).
    /// </summary>
    /// <summary>The plugin's own version ("0.2.0"), sent with the reading so the site can say which plugin said hello. Set once at load.</summary>
    public static string? PluginVersion { get; set; }

    public JsonObject ToSite()
    {
        var p = this;
        var json = p.ToJson();
        if (Json.JsonbBytes(json) > MaxBytes && p.ExpansionNow is { } now)
        {
            p = p with { Chapters = p.Chapters.Where(c => c.Expansion <= now.Id).ToList() };
            json = p.ToJson();
        }
        if (Json.JsonbBytes(json) > MaxBytes)
        {
            p = p with { Chapters = Array.Empty<ChapterCount>() };
            json = p.ToJson();
        }
        while (Json.JsonbBytes(json) > MaxBytes && p.Current is { Journal.Count: > 1 } q)
        {
            p = p with { Current = q with { Journal = q.Journal.Skip(1).ToList() } };
            json = p.ToJson();
        }
        return json;
    }

    /// <summary>
    /// Reads the main story: the road's counts, the current quest (its journal, objectives, people, the
    /// step's spot on the map when known), the duties ahead and where to stop. <paramref name="current"/> is the catalog's quest the player's on;
    /// <paramref name="currentText"/> its journal so far and objectives, read from the game.
    /// </summary>
    public static Progress Read(MsqCatalog catalog, MsqPath path, MsqQuest? current, int level, string job,
        int sequence, IReadOnlyList<string> journal, IReadOnlyList<string> objectives, string? previousLine, IReadOnlyList<string> people,
        MapSpot? spot = null)
    {
        CurrentQuest? cur = null;
        if (current != null)
        {
            var chapter = current.Chapter != 0 ? catalog.Chapter(current.Chapter)?.Name : null;
            cur = new CurrentQuest(current.Id, current.Name, current.Level, current.Place, sequence, chapter, current.Expansion,
                catalog.ExpansionName(current.Expansion), objectives, journal, previousLine, people, spot);
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
