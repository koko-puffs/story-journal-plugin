using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

/// <summary>
/// The main story's catalog on a small story shaped like the real one: three starting cities' roads
/// meeting at one quest (any one of them will do), two chapters, a Grand Company choice (each shuts out
/// the other), duties unlocked along the way, and a second expansion.
/// </summary>
public class MsqTests
{
    // Gridania, Limsa and Ul'dah's roads (two quests each), then the meeting.
    private const uint G1 = 70001, G2 = 70002, L1 = 70011, L2 = 70012, U1 = 70021, U2 = 70022;
    private const uint Meet = 70100, A = 70101, B = 70102, Gca = 70110, Gcb = 70111, C = 70120, D = 70121, E = 70200, F = 70201;

    private static MsqCatalog Story() => new(
        new[]
        {
            // Out of order on purpose: the catalog sorts.
            new MsqQuest(Meet, "The Meeting", 15, 0, new[] { G2, L2, U2 }, AnyPrevious: true, Chapter: 1),
            new MsqQuest(G1, "Gridania 1", 1, 0, Array.Empty<uint>(), Chapter: 1),
            new MsqQuest(G2, "Gridania 2", 5, 0, new[] { G1 }, Chapter: 1),
            new MsqQuest(L1, "Limsa 1", 1, 0, Array.Empty<uint>(), Chapter: 1),
            new MsqQuest(L2, "Limsa 2", 5, 0, new[] { L1 }, Chapter: 1),
            new MsqQuest(U1, "Ul'dah 1", 1, 0, Array.Empty<uint>(), Chapter: 1),
            new MsqQuest(U2, "Ul'dah 2", 5, 0, new[] { U1 }, Chapter: 1),
            new MsqQuest(A, "Into the Snow", 16, 0, new[] { Meet }, Chapter: 2, Duty: 4, DutyName: "The Thousand Maws", DutyType: "dungeon", DutyLevel: 16),
            new MsqQuest(B, "The Company You Keep", 20, 0, new[] { A }, Chapter: 2),
            new MsqQuest(Gca, "Join the Maelstrom", 20, 0, new[] { B }, Locks: new[] { Gcb }, Chapter: 2),
            new MsqQuest(Gcb, "Join the Adders", 20, 0, new[] { B }, Locks: new[] { Gca }, Chapter: 2),
            new MsqQuest(C, "Sworn In", 21, 0, new[] { Gca, Gcb }, AnyPrevious: true, Chapter: 2),
            new MsqQuest(D, "The Mountain Falls", 34, 0, new[] { C }, Chapter: 2, Duty: 56, DutyName: "The Navel", DutyType: "trial", DutyLevel: 34),
            new MsqQuest(E, "Heavensward", 50, 1, new[] { D }, Chapter: 3, Duty: 90, DutyName: "The Vault", DutyType: "dungeon", DutyLevel: 57),
            new MsqQuest(F, "Ishgard", 51, 1, new[] { E }, Chapter: 3),
        },
        new[] { new MsqChapter(1, "Beginnings", 0), new MsqChapter(2, "The Mountain Falls", 0), new MsqChapter(3, "Into Ishgard", 1) },
        new[] { new MsqExpansion(0, "A Realm Reborn"), new MsqExpansion(1, "Heavensward") });

    private static HashSet<uint> Set(params uint[] ids) => ids.ToHashSet();

    [Fact]
    public void PrerequisitesComeFirstThenExpansionLevelAndId()
    {
        var order = Story().Order.Select(q => q.Id).ToList();
        Assert.Equal(new[] { G1, L1, U1, G2, L2, U2, Meet, A, B, Gca, Gcb, C, D, E, F }, order);
    }

    [Fact]
    public void TheIndexIsTheSameForEveryone()
    {
        var story = Story();
        Assert.Equal(6, story.IndexOf(Meet));
        Assert.Equal(6, story.IndexOf(QuestIds.Short(Meet)));
        Assert.Equal(-1, story.IndexOf(12345));
        Assert.True(story.Contains(QuestIds.Short(D)));
    }

    [Fact]
    public void QuestIdsComeInBothForms()
    {
        Assert.Equal(70001u, QuestIds.Row(70001 - 65536));
        Assert.Equal(70001u, QuestIds.Row(70001));
        Assert.Equal(0u, QuestIds.Row(0));
        Assert.Equal((ushort)(70001 - 65536), QuestIds.Short(70001));
        Assert.Equal("quest/006/ManFst001_00655", QuestIds.TextSheet("ManFst001_00655"));
        Assert.Equal("quest/012/SubFst001_01234", QuestIds.TextSheet("SubFst001_01234"));
        Assert.Null(QuestIds.TextSheet("NoNumber"));
        Assert.Null(QuestIds.TextSheet(""));
    }

    [Fact]
    public void AFreshCharacterCountsEveryRoadUntilOneIsBegun()
    {
        var path = Story().Path(Set());
        Assert.Equal(15, path.Total);
        // Gridania's first quest taken: the other cities' roads are off.
        path = Story().Path(Set(), Set(G1));
        Assert.Equal(11, path.Total);
        Assert.True(path.OnPath(G2));
        Assert.False(path.OnPath(L1));
        Assert.False(path.OnPath(U2));
    }

    [Fact]
    public void ADoneQuestFurtherOnTakesTheOtherBranchesOff()
    {
        // Done up to Sworn In through the Maelstrom; nothing said of the start (finished before the plugin knew).
        var path = Story().Path(Set(G1, G2, Meet, A, B, Gca, C));
        Assert.False(path.OnPath(L1));
        Assert.False(path.OnPath(U2));
        Assert.False(path.OnPath(Gcb));
        Assert.True(path.OnPath(G1));
        Assert.Equal(10, path.Total);
        Assert.Equal(7, path.Done);
    }

    [Fact]
    public void AChoiceShutsOutTheOther()
    {
        var path = Story().Path(Set(G1, G2, Meet, A, B), Set(Gcb));
        Assert.False(path.OnPath(Gca));
        Assert.True(path.OnPath(Gcb));
    }

    [Fact]
    public void TheCurrentQuestIsTheGamesOrTheFirstNotDone()
    {
        var story = Story();
        var path = story.Path(Set(G1, G2, Meet), Set(A));
        Assert.Equal(A, path.Current(QuestIds.Short(A))!.Id);
        Assert.Equal(A, path.Current(0)!.Id);
        // The game's quest is done (it shows the last one finished): the next one instead.
        Assert.Equal(A, path.Current(Meet)!.Id);
        Assert.Equal(story.IndexOf(A), path.Index(path.Current(0)));
    }

    [Fact]
    public void AFinishedStoryHasNoCurrentQuest()
    {
        var story = Story();
        var path = story.Path(Set(G1, G2, Meet, A, B, Gca, C, D, E, F));
        Assert.True(path.Complete);
        Assert.Null(path.Current(0));
        Assert.Equal(story.Count, path.Index(null));
        Assert.Empty(path.Ahead(null));
        Assert.Null(path.Stop(null));
    }

    [Fact]
    public void ExpansionsAndChaptersAreCounted()
    {
        var path = Story().Path(Set(G1, G2, Meet, A), Set(B));
        var current = path.Current(B);
        var ex = path.Expansions();
        Assert.Equal(2, ex.Count);
        Assert.Equal(new ExpansionCount(0, "A Realm Reborn", 4, 9), ex[0]);
        Assert.Equal(new ExpansionCount(1, "Heavensward", 0, 2), ex[1]);
        var chapters = path.Chapters(current);
        Assert.Equal(3, chapters.Count);
        Assert.Equal("Beginnings", chapters[0].Name);
        Assert.Equal((3, 3), (chapters[0].Done, chapters[0].Total));
        Assert.Equal("The Mountain Falls", chapters[1].Name);
        Assert.Equal((1, 6), (chapters[1].Done, chapters[1].Total));
        // Not reached: its name is a spoiler.
        Assert.Null(chapters[2].Name);
        Assert.Equal(1, chapters[2].Expansion);
    }

    [Fact]
    public void FinishingTheLastQuestOfAChapterClosesIt()
    {
        var story = Story();
        var path = story.Path(Set(G1, G2, Meet, A, B, Gca, C));
        Assert.True(path.ChapterDone(story.Quest(D)!));
        Assert.True(path.ExpansionDone(story.Quest(D)!));
        Assert.False(path.ChapterDone(story.Quest(E)!));
        var early = story.Path(Set(G1));
        Assert.True(early.ChapterDone(story.Quest(G2)!) == false);
    }

    [Fact]
    public void DutiesAheadCountTheCurrentQuestAsOne()
    {
        var path = Story().Path(Set(G1, G2, Meet), Set(A));
        var ahead = path.Ahead(path.Current(A));
        Assert.Equal(3, ahead.Count);
        Assert.Equal(new Ahead(1, 16, "The Thousand Maws", "dungeon"), ahead[0]);
        // (Both Grand Companies count until one is chosen.)
        Assert.Equal(new Ahead(6, 34, "The Navel", "trial"), ahead[1]);
        Assert.Equal(new Ahead(7, 57, "The Vault", "dungeon"), ahead[2]);
        Assert.Single(path.Ahead(path.Current(A), 1));
    }

    [Fact]
    public void AGoodPlaceToStopIsTheNearerOfAChaptersEndAndADuty()
    {
        // On B: the Navel (duty) is 5 away, and the chapter ends with the same quest: the chapter's end.
        var path = Story().Path(Set(G1, G2, Meet, A), Set(B));
        Assert.Equal(new NextStop(5, "chapter"), path.Stop(path.Current(B)));
        // On Meet: the chapter ends with it (1), before Into the Snow's dungeon (2): the chapter.
        var start = Story().Path(Set(G1, G2), Set(Meet));
        Assert.Equal(new NextStop(1, "chapter"), start.Stop(start.Current(Meet)));
        // In Heavensward: the chapter runs on to F (2); the Vault is unlocked by E (1).
        var hw = Story().Path(Set(G1, G2, Meet, A, B, Gca, C, D));
        Assert.Equal(new NextStop(1, "duty"), hw.Stop(hw.Current(0)));
    }

    [Fact]
    public void ThePreviousAndNextQuestsAreOnTheRoad()
    {
        var path = Story().Path(Set(G1, G2, Meet, A, B, Gca), Set(C));
        var cur = path.Current(C);
        Assert.Equal(Gca, path.Previous(cur)!.Id);
        Assert.Equal(D, path.After(cur)!.Id);
        Assert.Equal(new[] { C, D, E, F }, path.Remaining(cur).Select(q => q.Id).ToArray());
    }

    [Fact]
    public void ALoopInThePrerequisitesDoesntHang()
    {
        var loop = new MsqCatalog(new[]
        {
            new MsqQuest(80001, "One", 1, 0, new uint[] { 80002 }),
            new MsqQuest(80002, "Two", 1, 0, new uint[] { 80001 }),
            new MsqQuest(80003, "Three", 1, 0, new uint[] { 80002 }),
        });
        Assert.Equal(3, loop.Count);
        Assert.Equal(80001u, loop.Order[0].Id);
    }

    [Fact]
    public void TheProgressReadingPutsItTogether()
    {
        var story = Story();
        var path = story.Path(Set(G1, G2, Meet, A), Set(B));
        var cur = path.Current(B);
        var p = Progress.Read(story, path, cur, 20, "GLA", 2, new[] { "Entry 0." }, new[] { "Speak with Minfilia." }, "You came north.", new[] { "Minfilia" });
        Assert.Equal(story.IndexOf(B), p.Index);
        Assert.Equal(4, p.Done);
        Assert.Equal(11, p.Total);
        Assert.Equal("The Mountain Falls", p.Current!.Chapter);
        Assert.Equal("A Realm Reborn", p.Current.ExpansionName);
        Assert.Equal(0, p.ExpansionNow!.Id);
        Assert.Equal(2, p.Ahead.Count);
        var json = p.ToSite();
        Assert.Equal(B, (uint)json["current"]!["id"]!);
    }
}
