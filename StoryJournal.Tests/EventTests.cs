using System.Text.Json.Nodes;
using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

/// <summary>
/// The events and the main story's reading, in exactly the shapes the site's database keeps (the
/// migration's <c>sj_clean_event</c>, <c>sj_put_session</c> and the brief's <c>Progress</c>): a field the
/// site doesn't know is dropped there, and one missing becomes a default, so the names are checked here.
/// </summary>
public class EventShapeTests
{
    private static string[] Keys(JsonObject o) => o.Select(kv => kv.Key).ToArray();

    private static readonly QuestFinished Titan = new(
        66045, "The Mountain Falls", 34, true, 211, 0, "The Mountain Falls", true, false, "Outer La Noscea",
        new[] { "Minfilia asks you to…", "  You  brought Titan down\nat the Navel. " }, new[] { "Minfilia", "Minfilia" });

    [Fact]
    public void AQuestHasTheSitesFields()
    {
        var o = Events.Quest(Titan);
        Assert.Equal(new[] { "id", "name", "level", "msq", "msqIndex", "expansion", "chapter", "chapterDone", "place", "journal", "people" }, Keys(o));
        Assert.Equal(66045, Json.Int(o["id"]));
        Assert.True((bool)o["msq"]!);
        Assert.Equal(211, (int)o["msqIndex"]!);
        Assert.Equal("You brought Titan down at the Navel.", (string)o["journal"]![1]!);
        Assert.Single(o["people"]!.AsArray());
    }

    [Fact]
    public void AQuestsOptionalFieldsAreLeftOut()
    {
        var side = Titan with { Msq = false, MsqIndex = null, Chapter = null, ChapterDone = false, ExpansionDone = false, People = Array.Empty<string>() };
        var o = Events.Quest(side);
        Assert.Equal(new[] { "id", "name", "level", "msq", "expansion", "place", "journal", "people" }, Keys(o));
        var last = Events.Quest(Titan with { ExpansionDone = true });
        Assert.True((bool)last["expansionDone"]!);
    }

    [Fact]
    public void ALongJournalKeepsItsBeginningAndItsEnd()
    {
        var journal = Enumerable.Range(0, 15).Select(i => $"Entry {i}.").ToList();
        var kept = Events.JournalForSite(journal);
        Assert.Equal(Events.JournalCount, kept.Count);
        Assert.Equal("Entry 0.", kept[0]);
        Assert.Equal("Entry 14.", kept[^1]);
        Assert.Equal("Entry 4.", kept[1]);
        var huge = Events.JournalForSite(new[] { new string('a', 5000), "", "   " });
        Assert.Single(huge);
        Assert.Equal(Events.JournalLength, huge[0].Length);
    }

    [Fact]
    public void TheOtherKindsHaveTheSitesFields()
    {
        Assert.Equal(new[] { "id", "name", "msq" }, Keys(Events.Accept(66045, "The Mountain Falls", true)));
        Assert.Equal(new[] { "questId", "place", "seconds" }, Keys(Events.Cutscene(66045, "The Navel", 95)));
        Assert.Equal(new[] { "place", "seconds" }, Keys(Events.Cutscene(null, "Ul'dah", 12)));
        Assert.Equal(new[] { "id", "name", "region", "first" }, Keys(Events.Zone(155, "Coerthas Central Highlands", "Coerthas", true)));
        Assert.Equal(new[] { "id", "name", "type", "cleared", "seconds" }, Keys(Events.Duty(56, "the Navel", "trial", true, 600)));
        Assert.Equal(new[] { "job", "level" }, Keys(Events.Level("PLD", 41)));
        Assert.Equal(new[] { "name", "place", "questId" }, Keys(Events.Person("Haurchefant", "Camp Dragonhead", 66052)));
        Assert.Equal(new[] { "name", "place" }, Keys(Events.Person("Haurchefant", "Camp Dragonhead", null)));
        Assert.Equal(new[] { "speaker", "text", "questId", "place" }, Keys(Events.Line("Ser Haurchefant", "Your Coerthan welcome…", 66052, "Camp Dragonhead")));
    }

    [Fact]
    public void NumbersAreKeptToTheSitesRanges()
    {
        Assert.Equal(36000, (int)Events.Cutscene(null, "x", 99999)["seconds"]!);
        Assert.Equal(86400, (int)Events.Duty(1, "x", "trial", false, 999999)["seconds"]!);
        Assert.Equal(1, (int)Events.Level("PLD", 0)["level"]!);
        Assert.Equal(8, ((string)Events.Level("TOOLONGJOBNAME", 5)["job"]!).Length);
    }

    [Fact]
    public void AnEventGoesToTheSiteAsIdKindAtData()
    {
        var at = new DateTime(2026, 11, 8, 20, 2, 13, 512, DateTimeKind.Utc);
        var e = new JournalEvent(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), EventKind.Level, at, Events.Level("PLD", 41), Guid.NewGuid(), "abcdefghijklmnop");
        var o = e.ToSite();
        Assert.Equal(new[] { "id", "kind", "at", "data" }, Keys(o));
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", (string)o["id"]!);
        Assert.Equal("2026-11-08T20:02:13.512Z", (string)o["at"]!);
        var back = JournalEvent.FromDisk(JsonNode.Parse(e.ToDisk().ToJsonString()));
        Assert.NotNull(back);
        Assert.Equal(e.Id, back!.Id);
        Assert.Equal(e.Session, back.Session);
        Assert.Equal(at, back.At);
        Assert.Equal(41, (int)back.Data["level"]!);
    }

    [Theory]
    [InlineData("Dungeons", "dungeon")]
    [InlineData("Trials", "trial")]
    [InlineData("Raids", "raid")]
    [InlineData("Guildhests", "guildhest")]
    [InlineData("Treasure Hunt", "treasure hunt")]
    [InlineData("Deep Dungeons", "deep dungeon")]
    [InlineData("", "duty")]
    public void DutyTypesAreOneWord(string content, string word) => Assert.Equal(word, Events.DutyType(content));

    [Theory]
    [InlineData(2u, "dungeon")]
    [InlineData(3u, "guildhest")]
    [InlineData(4u, "trial")]
    [InlineData(5u, "raid")]
    [InlineData(28u, "ultimate raid")]
    [InlineData(30u, "variant and criterion dungeon")]
    [InlineData(37u, "chaotic alliance raid")]
    [InlineData(9999u, "duty")]
    public void DutyTypesAreTheSameWordsInEveryLanguage(uint contentType, string word)
    {
        // By the content type's row, never its name in the client's language ("Donjons", "Prüfungen"…).
        Assert.Equal(word, Events.DutyType(contentType));
        // The site's own reading of it (firsts.ts, spoilers.ts) finds what it looks for.
        if (contentType is 2 or 30) Assert.Contains("dungeon", word);
        if (contentType is 5 or 28 or 37) Assert.Contains("raid", word);
    }

    [Fact]
    public void ASessionHasTheSitesFields()
    {
        var s = new SessionRecord { Character = "abcdefghijklmnop", StartedAt = new DateTime(2026, 11, 8, 19, 0, 0, DateTimeKind.Utc), LastAt = new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc) };
        Assert.Equal(new[] { "id", "startedAt", "lastAt" }, Keys(s.ToSite()));
        s.EndedAt = s.LastAt;
        s.Title = "The Mountain Falls";
        s.Moods = new List<string> { "Cosy", "Funny", "not a mood!" };
        s.Note = "Ask the Scions about the Ixal.";
        var o = s.ToSite();
        Assert.Equal(new[] { "id", "startedAt", "endedAt", "lastAt", "title", "moods", "note" }, Keys(o));
        Assert.Equal(new[] { "cosy", "funny" }, o["moods"]!.AsArray().Select(m => (string)m!).ToArray());
    }

    [Fact]
    public void MoodsAreKeptAsTheSiteKeepsThem()
    {
        var moods = Sessions.CleanMoods(new[] { "Cosy", "cosy", "Heart-warming", "42", "Sad ", "Exciting", "Funny", "Slow", "Tense" });
        Assert.Equal(new[] { "cosy", "heart-warming", "sad", "exciting", "funny", "slow" }, moods);
        Assert.All(moods, m => Assert.Matches("^[a-z][a-z -]{0,19}$", m));
    }
}

public class ProgressShapeTests
{
    private static Progress Sample(int journalEntries = 3, int chapters = 4) => new(
        41, "PLD", false, 112, 186, 120,
        new[] { new ExpansionCount(0, "A Realm Reborn", 112, 186), new ExpansionCount(1, "Heavensward", 0, 120) },
        Enumerable.Range(1, chapters).Select(i => new ChapterCount((uint)i, i <= 2 ? $"Chapter {i}" : null, i < 3 ? 0 : 1, 5, 10, i <= 2)).ToList(),
        new CurrentQuest(66052, "Talk of the Highlands", 41, "Camp Dragonhead", 2, "Into the Snow", 0, "A Realm Reborn",
            new[] { "Speak with Ser Haurchefant." }, Enumerable.Range(0, journalEntries).Select(i => $"Entry {i} " + new string('x', 1100)).ToList(),
            "You brought Titan down.", new[] { "Haurchefant" }),
        new[] { new Ahead(4, 44, "The Howling Eye", "trial") },
        new NextStop(3, "chapter"));

    [Fact]
    public void TheReadingHasTheBriefsShape()
    {
        var o = Sample().ToJson();
        Assert.Equal(new[] { "level", "job", "complete", "msq", "expansions", "chapters", "current", "ahead", "nextStop" }, o.Select(kv => kv.Key).ToArray());
        Assert.Equal(new[] { "done", "total", "index" }, o["msq"]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(120, (int)o["msq"]!["index"]!);
        Assert.Equal(new[] { "id", "name", "done", "total" }, o["expansions"]![0]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(new[] { "id", "name", "expansion", "done", "total" }, o["chapters"]![0]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Null(o["chapters"]![3]!["name"]);
        Assert.True(o["chapters"]![3]!.AsObject().ContainsKey("name"));
        Assert.Equal(
            new[] { "id", "name", "level", "place", "sequence", "chapter", "expansion", "expansionName", "objectives", "journal", "previous", "people" },
            o["current"]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(new[] { "questsAway", "level", "duty", "type" }, o["ahead"]![0]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal("chapter", (string)o["nextStop"]!["reason"]!);
    }

    [Fact]
    public void TheStepsSpotGoesWithTheCurrentQuestWhenKnown()
    {
        var p = Sample();
        var withSpot = p with { Current = p.Current! with { Spot = MapSpot.Of(11.04f, 15.26f) } };
        var o = withSpot.ToJson();
        var spot = o["current"]!["spot"]!.AsObject();
        Assert.Equal(new[] { "x", "y" }, spot.Select(kv => kv.Key).ToArray());
        Assert.Equal(11.0, (double)spot["x"]!, 3);
        Assert.Equal(15.3, (double)spot["y"]!, 3);
        Assert.Equal("X 11.0 Y 15.3", withSpot.Current!.Spot!.ToString());
        // Unknown: left out, not null.
        Assert.False(p.ToJson()["current"]!.AsObject().ContainsKey("spot"));
    }

    [Fact]
    public void NothingCurrentIsNull()
    {
        var o = (Sample() with { Current = null, NextStop = null, Complete = true }).ToJson();
        Assert.True(o.ContainsKey("current"));
        Assert.Null(o["current"]);
        Assert.Null(o["nextStop"]);
    }

    [Fact]
    public void AReadingIsMadeToFitTheSite()
    {
        var big = Sample(journalEntries: 40, chapters: 300);
        Assert.True(Json.Bytes(big.ToJson()) > Progress.MaxBytes);
        var sent = big.ToSite();
        Assert.True(Json.JsonbBytes(sent) <= Progress.MaxBytes);
        // The latest entries are kept.
        Assert.StartsWith("Entry 39", (string)sent["current"]!["journal"]!.AsArray()[^1]!);
    }

    [Fact]
    public void TheSitesMeasureIsJsonbsText()
    {
        // As Postgres writes it: {"a": 1, "b": [1, 2, {}], "c": "x\"é\n\u0001</", "d": null, "e": [], "g": 1000} is 80 bytes.
        var o = new JsonObject
        {
            ["a"] = 1, ["b"] = new JsonArray(1, 2, new JsonObject()), ["c"] = "x\"é\n\u0001</", ["d"] = null, ["e"] = new JsonArray(), ["g"] = 1000,
        };
        Assert.Equal(80, Json.JsonbBytes(o));
    }

    [Fact]
    public void AReadingUnderTheLimitCompactButOverItAsJsonbIsTrimmedChaptersFirst()
    {
        // Many chapters: under 30,000 bytes written compactly (as it used to be measured), but jsonb's
        // spaces take it past the 32,768 the site keeps.
        var p = Sample(journalEntries: 3, chapters: 450);
        Assert.True(Json.Bytes(p.ToJson()) <= 30_000, Json.Bytes(p.ToJson()).ToString());
        Assert.True(Json.JsonbBytes(p.ToJson()) > 32_768, Json.JsonbBytes(p.ToJson()).ToString());
        var sent = p.ToSite();
        Assert.True(Json.JsonbBytes(sent) <= Progress.MaxBytes);
        // The chapters went, the journal stayed whole.
        Assert.Equal(3, sent["current"]!["journal"]!.AsArray().Count);
        Assert.True(sent["chapters"]!.AsArray().Count < 450);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(112, 186, 60)]
    [InlineData(185, 186, 99)]
    [InlineData(186, 186, 100)]
    public void PercentNeverSaysAllUntilItIs(int done, int total, int percent) => Assert.Equal(percent, Progress.Percent(done, total));
}
