using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

/// <summary>
/// The main story's catalog, ordered by prerequisites (the fallback when the game's order is unusable),
/// on a small story shaped like the real one: three starting cities' roads meeting at one quest (any one
/// of them will do), two chapters, a Grand Company choice (each shuts out the other), duties unlocked
/// along the way, and a second expansion. The game's own order: <see cref="MsqGameOrderTests"/>.
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
        var story = Story();
        Assert.False(story.GameOrder);
        Assert.Equal(new[] { G1, L1, U1, G2, L2, U2, Meet, A, B, Gca, Gcb, C, D, E, F }, story.Order.Select(q => q.Id).ToList());
    }

    [Fact]
    public void APositionIsTheCatalogsAndAnIndexTheRoads()
    {
        var story = Story();
        Assert.Equal(6, story.Position(Meet));
        Assert.Equal(6, story.Position(QuestIds.Short(Meet)));
        Assert.Equal(-1, story.Position(12345));
        Assert.True(story.Contains(QuestIds.Short(D)));
        // On Gridania's road the other cities' quests aren't counted.
        var path = story.Path(Set(G1, G2));
        Assert.Equal(2, path.IndexOf(Meet));
        Assert.Equal(2, path.IndexOf(QuestIds.Short(Meet)));
        Assert.Equal(-1, path.IndexOf(12345));
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
        Assert.Equal(3, path.Index(path.Current(0)));
    }

    [Fact]
    public void AFinishedStoryHasNoCurrentQuest()
    {
        var story = Story();
        var path = story.Path(Set(G1, G2, Meet, A, B, Gca, C, D, E, F));
        Assert.True(path.Complete);
        Assert.Null(path.Current(0));
        Assert.Equal(path.Total, path.Index(null));
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
        Assert.Equal(4, p.Index);
        Assert.Equal(path.IndexOf(B), p.Index);
        Assert.Equal(4, p.Done);
        Assert.Equal(11, p.Total);
        Assert.Equal("The Mountain Falls", p.Current!.Chapter);
        Assert.Equal("A Realm Reborn", p.Current.ExpansionName);
        Assert.Equal(0, p.ExpansionNow!.Id);
        Assert.Equal(2, p.Ahead.Count);
        var json = p.ToSite();
        Assert.Equal(B, (uint)json["current"]!["id"]!);
    }

    [Fact]
    public void AFinishIsToldFromTheCatalogBeforeTheStoryIsRead()
    {
        // Just after login (no reading yet): the catalog and the quests done read then are enough to tell
        // a main-story quest as one, with its index and whether it closed its chapter.
        var story = Story();
        var done = Set(G1, G2, Meet, A, B, Gca, C);
        var path = story.Path(done, Set(D));
        var q = story.Finished(D, path, new[] { "Titan fell." }, Array.Empty<string>())!;
        Assert.True(q.Msq);
        Assert.Equal(path.IndexOf(D), q.MsqIndex);
        Assert.Equal(7, q.MsqIndex);
        Assert.Equal("The Mountain Falls", q.Chapter);
        Assert.True(q.ChapterDone);
        Assert.True(q.ExpansionDone);
        Assert.Null(story.Finished(12345, path, Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void AFinishIsWrittenOnceHoweverItsNoticed()
    {
        var f = new Finishes();
        // The first reading after login is the baseline: nothing in it is new.
        Assert.Empty(f.NewlyDone(Set(G1, G2)));
        // Taken and finished between two looks at the log: the next reading has it.
        Assert.Equal(new[] { Meet }, f.NewlyDone(Set(G1, G2, Meet)));
        Assert.True(f.Claim(Meet));
        // The log saw it too (or saw it first): written once.
        Assert.False(f.Claim(QuestIds.Short(Meet)));
        Assert.Empty(f.NewlyDone(Set(G1, G2, Meet)));
        f.Reset();
        Assert.Empty(f.NewlyDone(Set(G1, G2, Meet, A)));
        Assert.True(f.Claim(Meet));
    }

    [Fact]
    public void ABaselineReadBeforeTheGameWasReadyIsntAFloodOfFinishes()
    {
        var f = new Finishes();
        // Read as nothing done (the game not ready yet), then as it is: not a dozen quests just finished.
        Assert.Empty(f.NewlyDone(Set()));
        Assert.Empty(f.NewlyDone(Set(G1, G2, Meet, A, B, Gca, C)));
        Assert.Equal(new[] { D }, f.NewlyDone(Set(G1, G2, Meet, A, B, Gca, C, D)));
    }
}

/// <summary>
/// The main story in the game's own order (ScenarioTree's <c>Unknown2</c> and <c>Type</c>), on quests as
/// the game has them (their real ids, orders and prerequisites, the chain trimmed): the three starting
/// cities' openings (each with its class variants of "Close to Home" sharing an order), the main line's
/// two "Call of the Sea" (one for Ul'dah, one for Gridania and Limsa), the three Grand Companies and
/// their next quests, Heavensward's start and Dawntrail's end. Ul'dah's opening is the longest (as in the
/// game): five here, so the main line's indexes start at 5 for every city.
/// </summary>
public class MsqGameOrderTests
{
    // Limsa Lominsa's opening (path 1).
    private const uint LComing = 65643, LHome1 = 65644, LHome2 = 65645, LSummerford = 65998, LEnvoy = 66082;
    // Gridania's (path 2).
    private const uint GComing = 65575, GHome1 = 65621, GHome2 = 65659, GHome3 = 65660, GBannock = 65564, GEnvoy = 66043;
    // Ul'dah's (path 3).
    private const uint UComing = 66130, UHome1 = 66104, UHome2 = 66105, UHome3 = 66106, URebuild = 66131, UNothing = 66207, UEnvoy = 66064;
    // The main line (path 0).
    private const uint SeaU = 66209, SeaGL = 66210, Pirates = 65781, Hero = 66047;
    private const uint CompanyTa = 66216, CompanyMa = 66217, CompanyIf = 66218, Wood = 66219, TillSea = 66220, Coin = 66221, Sylph = 66049;
    private const uint Dawn = 65964, Ishgard = 67116, Sights = 67117, Wind1 = 71012, Wind2 = 71013, Wind3 = 71014;

    private static MsqQuest Q(uint id, string name, int level, int order, int path, uint[] previous, bool any = false, uint[]? locks = null, int expansion = 0) =>
        new(id, name, level, expansion, previous, AnyPrevious: any, Locks: locks, Order: order, StartPath: path);

    private static IEnumerable<MsqQuest> Quests() => new[]
    {
        // Out of order on purpose: the catalog sorts.
        Q(Wind3, "Windborne", 100, 1011, 0, new[] { Wind2 }, expansion: 5),
        Q(SeaU, "Call of the Sea", 14, 1, 0, new[] { UEnvoy }),
        Q(SeaGL, "Call of the Sea", 14, 1, 0, new[] { GEnvoy, LEnvoy }, any: true),
        Q(Pirates, "It's Probably Pirates", 15, 2, 0, new[] { SeaU, SeaGL }, any: true),
        Q(Hero, "A Hero in the Making", 20, 17, 0, new[] { Pirates }),
        Q(CompanyTa, "The Company You Keep (Twin Adder)", 20, 18, 0, new[] { Hero }, locks: new[] { CompanyMa, CompanyIf }),
        Q(CompanyMa, "The Company You Keep (Maelstrom)", 20, 18, 0, new[] { Hero }, locks: new[] { CompanyTa, CompanyIf }),
        Q(CompanyIf, "The Company You Keep (Immortal Flames)", 20, 18, 0, new[] { Hero }, locks: new[] { CompanyMa, CompanyTa }),
        Q(Wood, "Wood's Will Be Done", 20, 19, 0, new[] { CompanyTa }),
        Q(TillSea, "Till Sea Swallows All", 20, 19, 0, new[] { CompanyMa }),
        Q(Coin, "For Coin and Country", 20, 19, 0, new[] { CompanyIf }),
        Q(Sylph, "Sylph-management", 20, 20, 0, new[] { Wood, TillSea, Coin }, any: true),
        Q(Dawn, "Before the Dawn", 50, 256, 0, new[] { Sylph }),
        Q(Ishgard, "Coming to Ishgard", 50, 257, 0, new[] { Dawn }, expansion: 1),
        Q(Sights, "Taking in the Sights", 50, 258, 0, new[] { Ishgard }, expansion: 1),
        Q(Wind1, "A Rush of Cold Wind", 100, 1009, 0, new[] { Sights }, expansion: 5),
        Q(Wind2, "The World Locked in Ice", 100, 1010, 0, new[] { Wind1 }, expansion: 5),
        Q(GComing, "Coming to Gridania", 1, 1, 2, Array.Empty<uint>()),
        Q(GHome1, "Close to Home", 1, 2, 2, new[] { GComing }),
        Q(GHome2, "Close to Home", 1, 2, 2, new[] { GComing }),
        Q(GHome3, "Close to Home", 1, 2, 2, new[] { GComing }),
        Q(GBannock, "To the Bannock", 4, 3, 2, new[] { GHome1, GHome2, GHome3 }, any: true),
        Q(GEnvoy, "The Gridanian Envoy", 14, 29, 2, new[] { GBannock }),
        Q(LComing, "Coming to Limsa Lominsa", 1, 1, 1, Array.Empty<uint>()),
        Q(LHome1, "Close to Home", 1, 2, 1, new[] { LComing }),
        Q(LHome2, "Close to Home", 1, 2, 1, new[] { LComing }),
        Q(LSummerford, "On to Summerford", 4, 3, 1, new[] { LHome1, LHome2 }, any: true),
        Q(LEnvoy, "The Lominsan Envoy", 14, 26, 1, new[] { LSummerford }),
        Q(UComing, "Coming to Ul'dah", 1, 1, 3, Array.Empty<uint>()),
        Q(UHome1, "Close to Home", 1, 2, 3, new[] { UComing }),
        Q(UHome2, "Close to Home", 1, 2, 3, new[] { UComing }),
        Q(UHome3, "Close to Home", 1, 2, 3, new[] { UComing }),
        Q(URebuild, "We Must Rebuild", 4, 3, 3, new[] { UHome1, UHome2, UHome3 }, any: true),
        Q(UNothing, "Nothing to See Here", 4, 4, 3, new[] { URebuild }),
        Q(UEnvoy, "The Ul'dahn Envoy", 14, 28, 3, new[] { UNothing }),
    };

    // The Town rows: 1 Limsa Lominsa, 2 Gridania, 3 Ul'dah, as the game has them (ScenarioType 1 L, 2 G, 3 U).
    private static readonly Dictionary<uint, int> Towns = new() { [1] = 1, [2] = 2, [3] = 3 };

    private static MsqCatalog Story(IEnumerable<MsqQuest>? quests = null) =>
        new(quests ?? Quests(), null, new[] { new MsqExpansion(0, "A Realm Reborn"), new MsqExpansion(1, "Heavensward"), new MsqExpansion(5, "Dawntrail") }, Towns);

    private static HashSet<uint> Set(params uint[] ids) => ids.ToHashSet();

    // A Gridanian archer through A Hero in the Making.
    private static readonly uint[] GridaniaToHero = { GComing, GHome2, GBannock, GEnvoy, SeaGL, Pirates, Hero };

    [Fact]
    public void TheOpeningsComeFirstThenTheMainLineEachByTheGamesOrderThenId()
    {
        var story = Story();
        Assert.True(story.GameOrder);
        Assert.Equal(new[]
        {
            LComing, LHome1, LHome2, LSummerford, LEnvoy,
            GComing, GHome1, GHome2, GHome3, GBannock, GEnvoy,
            UComing, UHome1, UHome2, UHome3, URebuild, UNothing, UEnvoy,
            SeaU, SeaGL, Pirates, Hero, CompanyTa, CompanyMa, CompanyIf, Wood, TillSea, Coin, Sylph, Dawn, Ishgard, Sights, Wind1, Wind2, Wind3,
        }, story.Order.Select(q => q.Id).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, story.StartPaths);
        // The longest opening, each "Close to Home" once: Ul'dah's.
        Assert.Equal(5, story.OpeningSize);
        Assert.Equal(new[] { CompanyTa, CompanyMa, CompanyIf }, story.Alternatives(CompanyMa));
        Assert.Equal(new[] { SeaU, SeaGL }, story.Alternatives(QuestIds.Short(SeaGL)));
        Assert.Empty(story.Alternatives(Pirates));
    }

    [Fact]
    public void AStartingCityIsTheTownsOpening()
    {
        var story = Story();
        Assert.Equal(1, story.StartPathOf(1));
        Assert.Equal(2, story.StartPathOf(2));
        Assert.Equal(3, story.StartPathOf(3));
        Assert.Equal(0, story.StartPathOf(0));
        Assert.Equal(0, story.StartPathOf(4));
        // A town given an opening there isn't: not known.
        var odd = new MsqCatalog(Quests(), null, null, new Dictionary<uint, int> { [7] = 4 });
        Assert.Equal(0, odd.StartPathOf(7));
    }

    [Fact]
    public void ANewGridanianCountsGridaniasOpeningAndOneOfEachAlternative()
    {
        var path = Story().Path(Set(), Set(), Story().StartPathOf(2));
        Assert.Equal(2, path.StartPath);
        Assert.Equal(new[] { GComing, GHome1, GBannock, GEnvoy, SeaGL, Pirates, Hero, CompanyTa, Wood, Sylph, Dawn, Ishgard, Sights, Wind1, Wind2, Wind3 },
            path.Quests.Select(q => q.Id).ToArray());
        // Call of the Sea: the one whose prerequisites are on the road (Gridania's), not the lower id (Ul'dah's).
        Assert.False(path.OnPath(SeaU));
        Assert.False(path.OnPath(LComing));
        Assert.Equal(16, path.Total);
        Assert.Equal(GComing, path.Current(0)!.Id);
        Assert.Equal(0, path.Index(path.Current(0)));
        Assert.Equal(3, path.IndexOf(GEnvoy));
        // The main line starts at the longest opening's size, not after Gridania's four.
        Assert.Equal(5, path.IndexOf(SeaGL));
    }

    [Fact]
    public void AnUldahnTakesTheirOwnCallOfTheSeaAndTheirClassesCloseToHome()
    {
        var story = Story();
        var path = story.Path(Set(UComing, UHome2), Set(URebuild), story.StartPathOf(3));
        Assert.Equal(3, path.StartPath);
        Assert.True(path.OnPath(UHome2));
        Assert.False(path.OnPath(UHome1));
        Assert.False(path.OnPath(UHome3));
        Assert.True(path.OnPath(SeaU));
        Assert.False(path.OnPath(SeaGL));
        Assert.Equal(URebuild, path.Current(QuestIds.Short(URebuild))!.Id);
        Assert.Equal(2, path.Index(path.Current(0)));
        Assert.Equal(17, path.Total);
        Assert.Equal(2, path.Done);
    }

    [Fact]
    public void TheOpeningIsTheOneWithQuestsDoneThenTheTownsThenTheOneTaken()
    {
        var story = Story();
        // Limsa's quests done: Limsa's, whatever the town read says.
        Assert.Equal(1, story.Path(Set(LComing, LHome1), null, 2).StartPath);
        Assert.Equal(1, story.Path(Set(LComing), null, 0).StartPath);
        // None done: the town's.
        Assert.Equal(3, story.Path(Set(), Set(GComing), 3).StartPath);
        // No town either: the one taken.
        Assert.Equal(2, story.Path(Set(), Set(GComing), 0).StartPath);
        // Nothing to tell by: every opening counts (4 each), and one of each of the main line's alternatives.
        var fresh = story.Path(Set());
        Assert.Equal(0, fresh.StartPath);
        Assert.Equal(4 + 4 + 5 + 12, fresh.Total);
        Assert.Equal(0, fresh.Index(fresh.Current(0)));
    }

    [Fact]
    public void AGrandCompanyTakenTakesItsOwnNextQuest()
    {
        var story = Story();
        var done = Set(LComing, LHome2, LSummerford, LEnvoy, SeaGL, Pirates, Hero);
        var path = story.Path(done, Set(CompanyMa), 1);
        Assert.True(path.OnPath(CompanyMa));
        Assert.False(path.OnPath(CompanyTa));
        Assert.False(path.OnPath(CompanyIf));
        Assert.True(path.OnPath(TillSea));
        Assert.False(path.OnPath(Wood));
        Assert.False(path.OnPath(Coin));
        // Close to Home's other class variant is off: the quest after it is done.
        Assert.False(path.OnPath(LHome1));
        Assert.Equal(16, path.Total);
        Assert.Equal(8, path.Index(path.Current(QuestIds.Short(CompanyMa))));
        Assert.Equal(TillSea, path.After(path.Current(CompanyMa))!.Id);

        // Finished: the next quest's index is one on, and the event tells the finished one's.
        var after = story.Path(Set(done.Append(CompanyMa).ToArray()), null, 1);
        Assert.Equal(TillSea, after.Current(0)!.Id);
        Assert.Equal(9, after.Index(after.Current(0)));
        Assert.Equal(8, story.Finished(CompanyMa, after, Array.Empty<string>(), Array.Empty<string>())!.MsqIndex);
    }

    // Each city's player through A Hero in the Making (their own Call of the Sea, a class's Close to Home).
    private static readonly uint[] LimsaToHero = { LComing, LHome1, LSummerford, LEnvoy, SeaGL, Pirates, Hero };
    private static readonly uint[] UldahToHero = { UComing, UHome3, URebuild, UNothing, UEnvoy, SeaU, Pirates, Hero };

    [Fact]
    public void TheIndexIsTheSameForTheSameQuestsDone()
    {
        var story = Story();
        foreach (var (own, city, total) in new[] { (1, LimsaToHero, 16), (2, GridaniaToHero, 16), (3, UldahToHero, 17) })
            foreach (var started in new[] { Set(), Set(CompanyTa), Set(CompanyMa), Set(CompanyIf) })
                foreach (var town in new uint[] { 0, 1, 2, 3 })
                {
                    var path = story.Path(Set(city), started, story.StartPathOf(town));
                    Assert.Equal(own, path.StartPath);
                    Assert.Equal(total, path.Total);
                    Assert.Equal(8, path.Index(path.Current(0)));
                    // The scenario tree's quest, whichever Grand Company's it is.
                    foreach (var gc in new[] { CompanyTa, CompanyMa, CompanyIf })
                        Assert.Equal(8, path.Index(path.Current(QuestIds.Short(gc))));
                    Assert.Equal(9, path.IndexOf(Wood));
                }
    }

    [Fact]
    public void EveryCitysPlayerHasTheSameIndexAtTheSameMainLineQuest()
    {
        var story = Story();
        var roads = new[]
        {
            story.Path(Set(LimsaToHero[..^1]), Set(Hero), 1),
            story.Path(Set(GridaniaToHero[..^1]), Set(Hero), 2),
            story.Path(Set(UldahToHero[..^1]), Set(Hero), 3),
        };
        foreach (var path in roads)
        {
            Assert.Equal(Hero, path.Current(QuestIds.Short(Hero))!.Id);
            Assert.Equal(7, path.Index(path.Current(Hero)));
            Assert.Equal(13, path.IndexOf(Sights));
            Assert.Equal(16, path.IndexOf(Wind3));
        }
        // Though their roads' counts differ (Ul'dah's opening is a quest longer).
        Assert.Equal(new[] { 16, 16, 17 }, roads.Select(p => p.Total).ToArray());
        Assert.Equal(new[] { 6, 6, 7 }, roads.Select(p => p.Done).ToArray());
    }

    [Fact]
    public void EveryOpeningsIndexesComeBeforeTheMainLines()
    {
        var story = Story();
        foreach (var (own, first, last) in new[] { (1, LComing, LEnvoy), (2, GComing, GEnvoy), (3, UComing, UEnvoy) })
        {
            var path = story.Path(Set(), null, own);
            var mainFirst = path.Quests.First(q => q.StartPath == 0);
            Assert.Equal(0, path.IndexOf(first));
            Assert.True(path.IndexOf(last) < story.OpeningSize);
            Assert.True(path.IndexOf(last) < path.IndexOf(mainFirst.Id));
            Assert.Equal(story.OpeningSize, path.IndexOf(mainFirst.Id));
            // Every opening quest on the road below every main-line one.
            var openings = path.Quests.Where(q => q.StartPath > 0).Select(q => path.IndexOf(q.Id)).ToList();
            Assert.Equal(Enumerable.Range(0, openings.Count), openings);
        }
        Assert.Equal(3, story.Path(Set(), null, 1).IndexOf(LEnvoy));
        Assert.Equal(4, story.Path(Set(), null, 3).IndexOf(UEnvoy));
        // Nothing to tell the city by: each opening still counts from 0.
        var fresh = story.Path(Set());
        Assert.Equal(new[] { 0, 0, 0 }, new[] { LComing, GComing, UComing }.Select(fresh.IndexOf).ToArray());
        Assert.Equal(5, fresh.IndexOf(SeaU));
    }

    [Fact]
    public void ALaterExpansionCountsOnFromTheRoad()
    {
        var story = Story();
        var done = Set(GridaniaToHero.Concat(new[] { CompanyIf, Coin, Sylph, Dawn, Ishgard, Sights, Wind1 }).ToArray());
        var path = story.Path(done, Set(Wind2), 2);
        var current = path.Current(Wind2)!;
        Assert.Equal(Wind2, current.Id);
        Assert.Equal(15, path.Index(current));
        Assert.Equal(new[]
        {
            new ExpansionCount(0, "A Realm Reborn", 11, 11),
            new ExpansionCount(1, "Heavensward", 2, 2),
            new ExpansionCount(5, "Dawntrail", 1, 3),
        }, path.Expansions());
        Assert.Equal(new[] { Wind2, Wind3 }, path.Remaining(current).Select(q => q.Id).ToArray());

        var finished = story.Path(Set(done.Concat(new[] { Wind2, Wind3 }).ToArray()), null, 2);
        Assert.True(finished.Complete);
        Assert.Null(finished.Current(0));
        // Past the main line's last quest: the same for every city.
        Assert.Equal(17, finished.Index(null));
        Assert.Equal(16, finished.Total);
    }

    [Fact]
    public void AQuestWithoutAnOrderGoesJustAfterWhatItNeeds()
    {
        var extra = new MsqQuest(70000, "A Detour", 15, 0, new[] { Pirates });
        var story = Story(Quests().Append(extra));
        Assert.True(story.GameOrder);
        Assert.Equal(story.Position(Pirates) + 1, story.Position(70000));
        Assert.Equal(story.Position(Hero) - 1, story.Position(70000));
        Assert.Empty(story.Alternatives(70000));
    }

    [Fact]
    public void AnUnusableOrderFallsBackToThePrerequisites()
    {
        // All zero (not read).
        var none = Story(Quests().Select(q => q with { Order = 0 }));
        Assert.False(none.GameOrder);
        Assert.Equal(new[] { GComing, GHome1, LComing }, none.Order.Take(3).Select(q => q.Id).ToArray());
        Assert.Empty(none.Alternatives(CompanyMa));
        // Nearly all the same.
        var same = Story(Quests().Select(q => q with { Order = 1 }));
        Assert.False(same.GameOrder);
        // Too few carry one.
        var few = Story(Quests().Select(q => q.StartPath == 0 ? q with { Order = 0 } : q));
        Assert.False(few.GameOrder);
        // Each prerequisite still comes before what needs it, and the road still takes the town's opening.
        foreach (var q in none.Order)
            foreach (var p in q.Previous.Where(none.Contains))
                if (!q.AnyPrevious) Assert.True(none.Position(p) < none.Position(q.Id));
        var path = none.Path(Set(GridaniaToHero), null, 2);
        Assert.Equal(2, path.StartPath);
        Assert.False(path.OnPath(LComing));
        Assert.Equal(Hero, path.Previous(path.Current(0))!.Id);
    }
}
