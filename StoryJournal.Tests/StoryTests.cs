using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

public class JournalTextTests
{
    private static readonly (string, string)[] Rows =
    {
        ("TEXT_MANFST001_00655_TODO_01", "Speak with Ser Haurchefant."),
        ("TEXT_MANFST001_00655_SEQ_02", "Ser Haurchefant  welcomed you\nto the camp ."),
        ("TEXT_MANFST001_00655_SEQ_00", "Minfilia asks you to go north."),
        ("TEXT_MANFST001_00655_SEQ_01", "You reached Camp Dragonhead."),
        ("TEXT_MANFST001_00655_SEQ_03", "   "),
        ("TEXT_MANFST001_00655_TODO_00", "Travel to Coerthas."),
        ("TEXT_MANFST001_00655_TODO_02", "Report to Minfilia."),
        ("TEXT_MANFST001_00655_ACTOR0", "Haurchefant"),
        ("", "nothing"),
    };

    [Fact]
    public void KeysAreRead()
    {
        Assert.Equal(("SEQ", 2), JournalText.Parse("TEXT_MANFST001_00655_SEQ_02"));
        Assert.Equal(("TODO", 10), JournalText.Parse("text_subfst001_01234_todo_10"));
        Assert.Null(JournalText.Parse("TEXT_MANFST001_00655_ACTOR0"));
        Assert.Null(JournalText.Parse(null));
    }

    [Fact]
    public void EntriesAreInTheirOrderAndTidy()
    {
        var all = JournalText.Whole(Rows);
        Assert.Equal(new[] { "Minfilia asks you to go north.", "You reached Camp Dragonhead.", "Ser Haurchefant welcomed you to the camp." }, all);
    }

    [Fact]
    public void TheJournalGoesAsFarAsTheStep()
    {
        Assert.Equal(new[] { "Minfilia asks you to go north." }, JournalText.Journal(Rows, 1));
        Assert.Equal(new[] { "Minfilia asks you to go north." }, JournalText.Journal(Rows, 0));
        Assert.Equal(2, JournalText.Journal(Rows, 2).Count);
        Assert.Equal(3, JournalText.Whole(Rows).Count);
    }

    /// <summary>A quest as the game has it: three to-dos (steps 1, 2 and 255) and four entries, the last written for handing it in.</summary>
    private static readonly (string, string)[] HandIn =
    {
        ("TEXT_MANFST009_00700_SEQ_00", "Alphinaud asks you to meet him in the Highlands."),
        ("TEXT_MANFST009_00700_SEQ_01", "You found Alphinaud at Camp Dragonhead."),
        ("TEXT_MANFST009_00700_SEQ_02", "Alphinaud is waiting for your report."),
        ("TEXT_MANFST009_00700_SEQ_03", "You told Alphinaud everything, and he resolved to leave Ishgard for good."),
        ("TEXT_MANFST009_00700_TODO_00", "Travel to the Highlands."),
        ("TEXT_MANFST009_00700_TODO_01", "Speak with Alphinaud."),
        ("TEXT_MANFST009_00700_TODO_02", "Report to Alphinaud."),
    };

    [Fact]
    public void ReadyToHandInIsNotFinished()
    {
        var steps = new[] { 1, 2, 255, 0, 0 };
        // At the last step the entry for reaching it shows, never the one written for handing it in.
        var atLast = JournalText.Journal(HandIn, JournalText.Final, steps);
        Assert.Equal(3, atLast.Count);
        Assert.Equal("Alphinaud is waiting for your report.", atLast[^1]);
        Assert.DoesNotContain(atLast, l => l.Contains("resolved", StringComparison.Ordinal));
        // Without the steps: every entry but the last.
        Assert.Equal(atLast, JournalText.Journal(HandIn, JournalText.Final));
        // Finished: all of it.
        Assert.Equal(4, JournalText.Whole(HandIn).Count);
        Assert.Equal(2, JournalText.Journal(HandIn, 2, steps).Count);
    }

    [Fact]
    public void ObjectivesAreTheStepsToDos()
    {
        var steps = new[] { 1, 2, 255 };
        Assert.Equal(new[] { "Travel to Coerthas." }, JournalText.Objectives(Rows, steps, 1));
        Assert.Equal(new[] { "Speak with Ser Haurchefant." }, JournalText.Objectives(Rows, steps, 2));
        Assert.Equal(new[] { "Report to Minfilia." }, JournalText.Objectives(Rows, steps, 255));
        Assert.Empty(JournalText.Objectives(Rows, steps, 7));
        Assert.Equal(1, JournalText.TodoFor(steps, 2));
        Assert.Equal(-1, JournalText.TodoFor(steps, 9));
    }

    [Theory]
    [InlineData("Speak with  .", "Speak with.")]
    [InlineData("  a\tb\r\nc  ", "a b c")]
    [InlineData("Well , then !", "Well, then!")]
    [InlineData(null, "")]
    public void LinesAreTidied(string? raw, string tidy) => Assert.Equal(tidy, JournalText.Tidy(raw));
}

public class HelloTests
{
    private const string Answer = """
    {
      "account": "Koko Puffs",
      "prefs": { "spoilers": "gentle", "whispers": "chatty", "welcomeHours": 0, "recapVoice": "plain" },
      "partners": [
        { "id": "11111111-1111-4111-8111-111111111111", "name": "Mira", "character": "Mira Moon", "msqIndex": 118, "ahead": -2, "level": 40,
          "seenAt": "2026-11-07T21:00:00Z", "lock": true, "where": "Into the Snow", "expansion": "A Realm Reborn" },
        { "id": "22222222-2222-4222-8222-222222222222", "name": "Tam", "character": null, "msqIndex": 300, "ahead": 180, "level": null,
          "seenAt": null, "lock": false }
      ],
      "theories": [ { "id": "33333333-3333-4333-8333-333333333333", "body": "Haurchefant is hiding something.", "sealedAt": "2026-11-01T20:00:00Z", "sealedQuest": "Talk of the Highlands" } ],
      "sealed": 4,
      "recent": [
        { "id": "44444444-4444-4444-8444-444444444444", "startedAt": "2026-11-06T19:00:00Z", "endedAt": "2026-11-06T21:30:00Z", "title": "", "note": "", "moods": ["cosy"],
          "quests": [ { "id": 66052, "name": "Talk of the Highlands", "chapter": "Into the Snow", "chapterDone": false, "last": "Ser Haurchefant took you in at Camp Dragonhead." } ],
          "places": ["Coerthas Central Highlands"], "duties": [], "people": ["Haurchefant"] },
        { "id": "55555555-5555-4555-8555-555555555555", "startedAt": "2026-11-04T19:00:00Z", "endedAt": "2026-11-04T22:00:00Z", "title": "The Mountain Falls", "note": "", "moods": [],
          "quests": [
            { "id": 66045, "name": "The Mountain Falls", "chapter": "The Mountain Falls", "chapterDone": true, "last": "You brought Titan down at the Navel." },
            { "id": 66050, "name": "Northward", "chapter": "Into the Snow", "chapterDone": false, "last": "The Scions sent you north: the Ixal are gathering crystal in Coerthas." } ],
          "places": [], "duties": ["the Navel"], "people": [] }
      ],
      "notes": [
        { "id": "66666666-6666-4666-8666-666666666666", "body": "Why is he so kind to strangers?", "subject": "Haurchefant", "at": "2026-11-06T20:00:00Z" },
        { "id": "77777777-7777-4777-8777-777777777777", "body": "Older note", "subject": "", "at": "2026-11-01T20:00:00Z" }
      ]
    }
    """;

    [Fact]
    public void HellosAnswerIsRead()
    {
        var h = HelloAnswer.Read(Answer);
        Assert.Equal("Koko Puffs", h.Account);
        Assert.Equal(new Prefs(Spoilers.Gentle, Whispers.Chatty, 0, "plain"), h.Prefs);
        Assert.Equal(2, h.Partners.Count);
        Assert.True(h.Partners[0].Lock);
        Assert.Equal(118, h.Partners[0].MsqIndex);
        Assert.Null(h.Partners[1].Level);
        Assert.Null(h.Partners[1].Where);
        Assert.Single(h.Theories);
        Assert.Equal(4, h.Sealed);
        Assert.Equal(2, h.Recent.Count);
        Assert.Equal(66045u, h.Recent[1].Quests[0].Id);
        Assert.True(h.Recent[1].Quests[0].ChapterDone);
        Assert.Equal(2, h.Notes.Count);
    }

    [Fact]
    public void AnAnswerThatIsntOneGivesDefaults()
    {
        var h = HelloAnswer.Read("not json");
        Assert.Equal(Prefs.Default, h.Prefs);
        Assert.Empty(h.Recent);
        Assert.Equal(Prefs.Default, Prefs.Read(null));
        Assert.Equal(720, Prefs.Read(System.Text.Json.Nodes.JsonNode.Parse("{\"welcomeHours\":9999}")).WelcomeHours);
    }

    [Fact]
    public void TheGlanceIsTheLastThreeLinesOldestFirst()
    {
        var h = HelloAnswer.Read(Answer);
        Assert.Equal(new[]
        {
            "You brought Titan down at the Navel.",
            "The Scions sent you north: the Ixal are gathering crystal in Coerthas.",
            "Ser Haurchefant took you in at Camp Dragonhead.",
        }, Welcome.Glance(h.Recent));
    }

    [Fact]
    public void WithoutQuestsTheGlanceSaysWhatElseHappened()
    {
        var s = new RecentSession(Guid.NewGuid(), DateTime.UtcNow, null, "", "", Array.Empty<string>(), Array.Empty<RecentQuest>(),
            new[] { "Ishgard" }, new[] { "the Vault" }, new[] { "Aymeric", "Lucia", "Estinien", "Alphinaud" });
        var since = s.StartedAt.AddDays(-3);
        Assert.Equal(new[] { "You cleared the Vault.", "You reached Ishgard for the first time.", "You met Aymeric, Lucia and Estinien." }, Welcome.Glance(new[] { s }, journalSince: since));
        Assert.Empty(Welcome.Glance(Array.Empty<RecentSession>()));
    }

    [Fact]
    public void InTheJournalsFirstSessionNoPlaceIsTheFirstTime()
    {
        // Started mid-story: everything the journal sees in its first session is "first" to it, not to you.
        var started = DateTime.UtcNow.AddHours(-2);
        var s = new RecentSession(Guid.NewGuid(), started, null, "", "", Array.Empty<string>(), Array.Empty<RecentQuest>(),
            new[] { "Ishgard" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(new[] { "You reached Ishgard." }, Welcome.Glance(new[] { s }, journalSince: started));
        Assert.Equal(new[] { "You reached Ishgard." }, Welcome.Glance(new[] { s }));
        Assert.Equal(new[] { "You reached Ishgard for the first time." }, Welcome.Glance(new[] { s }, journalSince: started.AddDays(-1)));
    }

    [Fact]
    public void TheCardShowsAfterLongEnoughAway()
    {
        var ended = new DateTime(2026, 11, 6, 21, 30, 0, DateTimeKind.Utc);
        Assert.False(Welcome.Due(ended, ended.AddHours(19), 20, true));
        Assert.True(Welcome.Due(ended, ended.AddHours(20), 20, true));
        Assert.True(Welcome.Due(ended, ended.AddMinutes(1), 0, true));
        Assert.False(Welcome.Due(ended, ended.AddDays(9), 20, false));
        Assert.False(Welcome.Due(null, ended, 20, true));
        var h = HelloAnswer.Read(Answer);
        Assert.Equal(ended, Welcome.LastEnded(h.Recent));
        Assert.Equal("Why is he so kind to strangers?", Welcome.LastNote(h.Notes)!.Body);
    }

    [Fact]
    public void DueTheoriesAreTheSitesAndThoseSealedHereWhoseTimeHasCome()
    {
        var h = HelloAnswer.Read(Answer);
        var mine = new[]
        {
            new LocalTheory(Guid.NewGuid(), "Due now", 120, DateTime.UtcNow, "x"),
            new LocalTheory(Guid.NewGuid(), "Not yet", 130, DateTime.UtcNow, "x"),
            new LocalTheory(Guid.NewGuid(), "Answered", 100, DateTime.UtcNow, "x", true),
            new LocalTheory(h.Theories[0].Id, "The same as the site's", 100, DateTime.UtcNow, "x"),
        };
        var due = Welcome.DueTheories(h.Theories, mine, 120);
        Assert.Equal(new[] { "Haurchefant is hiding something.", "Due now" }, due.Select(t => t.Body).ToArray());
        Assert.Single(Welcome.DueTheories(h.Theories, mine, 120, new HashSet<Guid> { h.Theories[0].Id }));
        // Sealed again: not asked until the next chapter closes (then it's let back in).
        Assert.Equal(new[] { "Haurchefant is hiding something." },
            Welcome.DueTheories(h.Theories, mine, 120, snoozed: new HashSet<Guid> { mine[0].Id }).Select(t => t.Body).ToArray());
    }

    [Fact]
    public void ThePaceLockWarnsBeforeGoingPast()
    {
        var mira = new Partner(Guid.NewGuid(), "Mira", null, 118, -2, 40, null, true, null, null);
        var tam = new Partner(Guid.NewGuid(), "Tam", null, 125, 5, 40, null, true, null, null);
        var off = new Partner(Guid.NewGuid(), "Off", null, 10, -110, 40, null, false, null, null);
        // I'm at 120: past Mira already; Tam is at 125, and the next quest is 121: fine.
        var w = PaceLock.Check(new[] { mira, tam, off }, 120, 121);
        Assert.Single(w);
        Assert.True(w[0].Past);
        Assert.Equal("Pace lock: Mira is 2 quests behind", PaceLock.Words(w[0], 120));
        // At 125 with Tam: the next quest would take me past.
        w = PaceLock.Check(new[] { tam }, 125, 126);
        Assert.False(Assert.Single(w).Past);
        Assert.Equal("Pace lock: wait for Tam after this quest", PaceLock.Words(w[0], 125));
        Assert.Empty(PaceLock.Check(new[] { tam }, 124, 125));
        Assert.Empty(PaceLock.Check(new[] { tam }, 124, null));
    }
}

public class CodexTests
{
    private const string File = """
    {
      "version": 1,
      "entries": [
        { "key": "haurchefant", "kind": "person", "name": "Ser Haurchefant Greystone", "aliases": ["Haurchefant"], "zone": null,
          "layers": [ { "text": "Commands Camp Dragonhead.", "needs": [] }, { "text": "A knight of House Fortemps.", "needs": ["Ishgard"], "expansion": 1 } ] },
        { "key": "coerthas", "kind": "place", "name": "Coerthas Central Highlands", "aliases": ["Coerthas"], "zone": "Coerthas Central Highlands",
          "layers": [ { "text": "Snow since the Calamity.", "needs": [] } ], "whisper": "Before the Calamity these highlands had seasons." },
        { "key": "yshtola", "kind": "person", "name": "Y'shtola", "aliases": [], "layers": [ { "text": "A Scion.", "needs": [] } ] },
        { "key": "ishgard", "kind": "place", "name": "Ishgard", "aliases": [], "zone": "Foundation", "layers": [ { "text": "The holy see.", "needs": [] } ] },
        { "key": "", "kind": "word", "name": "No key" },
        { "key": "ixal", "kind": "lore", "name": "Ixal", "aliases": ["Ixali"], "layers": [] }
      ]
    }
    """;

    private static readonly Codex X = Codex.Read(File);

    [Fact]
    public void TheFileIsRead()
    {
        Assert.Equal(5, X.Entries.Count);
        Assert.Equal(2, X.Entries[0].Layers.Count);
        Assert.Equal(1, X.Entries[0].Layers[1].Expansion);
        Assert.Empty(Codex.Read("nope").Entries);
        Assert.Empty(Codex.Read("{\"entries\": 5}").Entries);
    }

    [Theory]
    [InlineData("You met Haurchefant at the camp.", true)]
    [InlineData("HAURCHEFANT smiled.", false)]
    [InlineData("Haurchefant's camp.", true)]
    [InlineData("The Haurchefants of the world.", false)]
    [InlineData("SirHaurchefant", false)]
    public void NamesMatchAsWholeWords(string text, bool seen) => Assert.Equal(seen, X.Mentions(text, "Haurchefant"));

    [Fact]
    public void AProperNounMatchesAsWrittenAndALowerCaseWordInAnyCase()
    {
        // "the Echo" is the gift; "an echo of" is only a word, and mustn't unlock the Echo's card.
        Assert.True(X.Mentions("You have the Echo.", "Echo"));
        Assert.False(X.Mentions("An echo of the past.", "Echo"));
        Assert.True(X.Mentions("Thick with Aether here.", "aether"));
        Assert.True(X.Mentions("thick with aether here.", "aether"));
    }

    [Fact]
    public void CurlyApostrophesAreTheSame()
    {
        Assert.True(X.Mentions("Y’shtola nods.", "Y'shtola"));
        Assert.True(X.Mentions("Y'shtola nods.", "Y’shtola"));
    }

    [Fact]
    public void AnEntryIsSeenByAnyOfItsNames()
    {
        var corpus = new[] { "You reached Coerthas.", "Haurchefant took you in." };
        var seen = X.SeenKeys(corpus);
        Assert.Contains("haurchefant", seen);
        Assert.Contains("coerthas", seen);
        Assert.DoesNotContain("ishgard", seen);
        Assert.DoesNotContain("yshtola", seen);
    }

    [Fact]
    public void LayersUnlockWithTheirNeedsAndExpansion()
    {
        var entry = X.Entries[0];
        var corpus = new List<string> { "Haurchefant took you in." };
        var seen = X.SeenKeys(corpus);
        Assert.Single(X.Layers(entry, corpus, seen, new CodexGates(Spoilers.Strict, 0)));
        corpus.Add("The gates of Ishgard stay shut.");
        seen = X.SeenKeys(corpus);
        Assert.Single(X.Layers(entry, corpus, seen, new CodexGates(Spoilers.Strict, 0)));
        Assert.Equal(2, X.Layers(entry, corpus, seen, new CodexGates(Spoilers.Gentle, 1)).Count);
        // Not seen: nothing, unless spoilers are off.
        Assert.Empty(X.Layers(X.Entries[2], corpus, seen, new CodexGates(Spoilers.Strict, 5)));
        Assert.Single(X.Layers(X.Entries[2], corpus, seen, new CodexGates(Spoilers.Off, 0)));
    }

    [Fact]
    public void ArrivalsComeFromThePlacesZone()
    {
        Assert.Equal("Before the Calamity these highlands had seasons.", X.Arrival("Coerthas Central Highlands"));
        Assert.Equal("Before the Calamity these highlands had seasons.", X.Arrival("coerthas central highlands"));
        Assert.Null(X.Arrival("Foundation"));
        Assert.Null(X.Arrival("Mor Dhona"));
        Assert.Null(X.Arrival(""));
    }

    [Fact]
    public void AskingFindsOnlyWhatsBeenSeen()
    {
        var seen = X.SeenKeys(new[] { "Haurchefant and Ishgard." });
        Assert.Equal("haurchefant", X.Ask("haurch", seen, CodexGates.None(Spoilers.Strict))[0].Key);
        Assert.Equal("ishgard", Assert.Single(X.Ask("Ishgard", seen, CodexGates.None(Spoilers.Strict))).Key);
        Assert.Empty(X.Ask("Y'shtola", seen, CodexGates.None(Spoilers.Strict)));
        Assert.Single(X.Ask("Y’shtola", seen, CodexGates.None(Spoilers.Off)));
        Assert.Empty(X.Ask("x", seen, CodexGates.None(Spoilers.Off)));
        Assert.Equal("haurchefant", X.Find("ser haurchefant greystone")!.Key);
    }

    [Fact]
    public void PeopleNamedInAJournalAreFound()
    {
        var named = X.Named("Ser Haurchefant welcomed you; Minfilia sent word.", new[] { "Minfilia", "Haurchefant", "Alphinaud", "Haurchefant" });
        Assert.Equal(new[] { "Minfilia", "Haurchefant" }, named);
    }

    [Fact]
    public void VersionOneIsStillRead()
    {
        Assert.Equal(1, X.FileVersion);
        // A version 1 layer always waits for its entry to be seen, its expansion too.
        Assert.All(X.Entries.SelectMany(e => e.Layers), l => Assert.True(l.NeedsSeen));
        Assert.Empty(X.GateQuests);
        Assert.Empty(X.Layers(X.Entries[0], new List<string>(), new HashSet<string>(), new CodexGates(Spoilers.Strict, 5, true, _ => true, _ => true)));
    }

    [Fact]
    public void UnknownVersionsAreNotRead() => Assert.Empty(Codex.Read("""{ "version": 3, "entries": [ { "key": "a", "name": "A", "layers": [] } ] }""").Entries);
}

public class CodexV2Tests
{
    private const uint Q1 = 65537 + 1000;
    private const uint Q2 = 65537 + 2000;

    private const string File = """
    {
      "version": 2,
      "entries": [
        { "key": "alphinaud", "kind": "person", "name": "Alphinaud", "aliases": [], "subtitle": "Scion",
          "layers": [
            { "text": "A young scholar.", "source": "codex" },
            { "text": "Grandson of Louisoix.", "source": "codex", "quest": 66537 },
            { "text": "Head of the Crystal Braves.", "source": "codex", "quest": 2000 },
            { "text": "Alphinaud Leveilleur", "source": "title" }
          ] },
        { "key": "ul-dah", "kind": "place", "name": "Ul'dah - Steps of Nald", "aliases": ["Ul'dah"], "zone": "Ul'dah - Steps of Nald",
          "layers": [ { "text": "The Sapphire Avenue Exchange.", "source": "sight" }, { "text": "A FATE in the desert.", "source": "fate", "expansion": 0 } ] },
        { "key": "limsa", "kind": "place", "name": "Limsa Lominsa", "aliases": [], "zone": "Limsa Lominsa Lower Decks",
          "layers": [ { "text": "Behind a gate.", "source": "sight", "quest": 66537 }, { "text": "The Drowning Wench.", "source": "sight" } ], "whisper": "Salt and sails." },
        { "key": "the-ultimate-weapon", "kind": "chapter", "name": "The Ultimate Weapon", "aliases": [],
          "layers": [ { "text": "The synopsis.", "source": "synopsis", "chapter": 42 }, { "text": "With its quest.", "source": "synopsis", "chapter": 43, "quest": 66537 } ] },
        { "key": "bozja", "kind": "lore", "name": "Bozja", "aliases": [], "layers": [ { "text": "Notes.", "source": "notes", "expansion": 5 } ] },
        { "key": "occult", "kind": "lore", "name": "Occult Crescent", "aliases": [], "layers": [ { "text": "Lore.", "source": "occult", "complete": true } ] },
        { "key": "card-ifrit", "kind": "collection", "name": "Ifrit", "aliases": [], "layers": [ { "text": "A card.", "source": "card", "spoilers": "off" } ] },
        { "key": "aether", "kind": "word", "name": "aether", "aliases": [], "layers": [ { "text": "The stuff of life.", "source": "codex" } ] }
      ]
    }
    """;

    private static readonly Codex X = Codex.Read(File);

    private static CodexEntry E(string key) => X.Entries.Single(e => e.Key == key);

    private static readonly HashSet<string> NoneSeen = new();

    private static CodexGates G(Spoilers spoilers = Spoilers.Strict, int expansion = 0, bool complete = false, uint[]? quests = null, uint[]? chapters = null) =>
        new(spoilers, expansion, complete, q => (quests ?? Array.Empty<uint>()).Contains(QuestIds.Row(q)), c => (chapters ?? Array.Empty<uint>()).Contains(c));

    private static List<string> Texts(string key, IReadOnlySet<string> seen, CodexGates gates) =>
        X.Layers(E(key), new List<string>(), seen, gates).Select(l => l.Text).ToList();

    [Fact]
    public void TheFileIsRead()
    {
        Assert.Equal(2, X.FileVersion);
        Assert.Equal(8, X.Entries.Count);
        var a = E("alphinaud");
        Assert.Equal("Scion", a.Subtitle);
        Assert.Equal("codex", a.Layers[0].Source);
        Assert.True(a.Layers[0].NeedsSeen);
        Assert.Equal(66537u, a.Layers[1].Quest);
        Assert.False(a.Layers[1].NeedsSeen);
        // A quest given as the game's short id is the sheet's row.
        Assert.Equal(QuestIds.Row(2000), a.Layers[2].Quest);
        Assert.Equal(new HashSet<uint> { 66537, QuestIds.Row(2000) }, X.GateQuests.ToHashSet());
        Assert.True(E("card-ifrit").Layers[0].SpoilersOff);
        Assert.True(E("occult").Layers[0].Complete);
        Assert.Equal(42u, E("the-ultimate-weapon").Layers[0].Chapter);
    }

    [Fact]
    public void AGateThatCantBeReadKeepsTheLayerShut()
    {
        // As the site's model/codex.ts: a gate written but not readable shuts the layer to all but
        // spoilers off; it never opens it to everyone once its entry's seen.
        var x = Codex.Read("""
        { "version": 2, "entries": [ { "key": "odd", "kind": "lore", "name": "Odd", "aliases": [], "layers": [
            { "text": "Quest 0.", "source": "codex", "quest": 0 },
            { "text": "Quest as words.", "source": "codex", "quest": "66537" },
            { "text": "Chapter -1.", "source": "synopsis", "chapter": -1 },
            { "text": "Expansion -1.", "source": "notes", "expansion": -1 },
            { "text": "Complete, yes.", "source": "occult", "complete": "yes" },
            { "text": "Spoilers strict.", "source": "card", "spoilers": "strict" },
            { "text": "No gate.", "source": "codex", "quest": null, "complete": false },
            { "text": "Quest 66537.", "source": "codex", "quest": 66537 }
        ] } ] }
        """);
        var e = x.Entries.Single();
        var seen = new HashSet<string> { "odd" };
        var all = G(Spoilers.Gentle, 5, true, new uint[] { 66537 }, new uint[] { 1 });
        Assert.Equal(new[] { "No gate.", "Quest 66537." }, x.Layers(e, new List<string>(), seen, all).Select(l => l.Text));
        Assert.All(e.Layers.Take(6), l => Assert.True(l.SpoilersOff));
        Assert.Equal(8, x.Layers(e, new List<string>(), NoneSeen, G(Spoilers.Off)).Count);
    }

    [Fact]
    public void ALayerWithNoGateWaitsForItsEntryToBeSeen()
    {
        Assert.Empty(Texts("aether", NoneSeen, G()));
        var seen = X.SeenKeys(new[] { "Thick with Aether here." });
        Assert.Contains("aether", seen);
        Assert.Equal(new[] { "The stuff of life." }, Texts("aether", seen, G()));
    }

    [Fact]
    public void AQuestGateOpensWhenTheQuestIsFinished()
    {
        var seen = new HashSet<string> { "alphinaud" };
        Assert.Equal(new[] { "A young scholar.", "Alphinaud Leveilleur" }, Texts("alphinaud", seen, G()));
        Assert.Equal(new[] { "A young scholar.", "Grandson of Louisoix.", "Alphinaud Leveilleur" }, Texts("alphinaud", seen, G(quests: new uint[] { 66537 })));
        Assert.Equal(4, Texts("alphinaud", seen, G(quests: new uint[] { 66537, QuestIds.Row(2000) })).Count);
        // A gated layer doesn't wait for the name to be seen: the game opens it with the quest.
        Assert.Equal(new[] { "Grandson of Louisoix." }, Texts("alphinaud", NoneSeen, G(quests: new uint[] { 66537 })));
        Assert.True(X.Visible(E("alphinaud"), NoneSeen, G(quests: new uint[] { 66537 })));
        Assert.False(X.Visible(E("alphinaud"), NoneSeen, G()));
    }

    [Fact]
    public void AChapterGateOpensWhenTheChapterIsFinishedOrWithItsQuest()
    {
        Assert.Empty(Texts("the-ultimate-weapon", NoneSeen, G()));
        Assert.Equal(new[] { "The synopsis." }, Texts("the-ultimate-weapon", NoneSeen, G(chapters: new uint[] { 42 })));
        // With a quest too, the quest decides (the plugin knows every quest, not every chapter).
        Assert.Equal(new[] { "With its quest." }, Texts("the-ultimate-weapon", NoneSeen, G(quests: new uint[] { 66537 })));
        Assert.Empty(Texts("the-ultimate-weapon", NoneSeen, G(chapters: new uint[] { 43 })));
        // Nothing to tell chapters by: shut, unless spoilers are off.
        Assert.Empty(X.Layers(E("the-ultimate-weapon"), new List<string>(), NoneSeen, CodexGates.None(Spoilers.Gentle)));
        Assert.Equal(2, X.Layers(E("the-ultimate-weapon"), new List<string>(), NoneSeen, CodexGates.None(Spoilers.Off)).Count);
    }

    [Fact]
    public void AnExpansionGateOpensOnceItsReached()
    {
        Assert.Empty(Texts("bozja", NoneSeen, G(expansion: 4)));
        Assert.Equal(new[] { "Notes." }, Texts("bozja", NoneSeen, G(expansion: 5)));
        // An expansion gate of 0 is open from the start.
        Assert.Equal(new[] { "A FATE in the desert." }, Texts("ul-dah", NoneSeen, G()));
    }

    [Fact]
    public void ACompleteGateOpensWithTheWholeMainStory()
    {
        Assert.Empty(Texts("occult", NoneSeen, G(expansion: 5)));
        Assert.Equal(new[] { "Lore." }, Texts("occult", NoneSeen, G(expansion: 5, complete: true)));
    }

    [Fact]
    public void CollectionsAndSpoilerLayersOnlyWithSpoilersOff()
    {
        var seen = new HashSet<string> { "card-ifrit" };
        Assert.Empty(Texts("card-ifrit", seen, G(Spoilers.Gentle, 5, true)));
        Assert.False(X.Visible(E("card-ifrit"), seen, G(Spoilers.Gentle, 5, true)));
        Assert.Empty(X.Ask("Ifrit", seen, G(Spoilers.Strict)));
        Assert.Equal(new[] { "A card." }, Texts("card-ifrit", NoneSeen, G(Spoilers.Off)));
        Assert.True(X.Visible(E("card-ifrit"), NoneSeen, G(Spoilers.Off)));
        Assert.Single(X.Ask("Ifrit", NoneSeen, G(Spoilers.Off)));
    }

    [Fact]
    public void SpoilersOffShowsEverything()
    {
        Assert.All(X.Entries, e => Assert.Equal(e.Layers.Count, X.Layers(e, new List<string>(), NoneSeen, G(Spoilers.Off)).Count));
        Assert.All(X.Entries, e => Assert.True(X.Visible(e, NoneSeen, G(Spoilers.Off))));
    }

    [Fact]
    public void ArrivalsUseTheWhisperElseTheFirstSightOpen()
    {
        Assert.Equal("Salt and sails.", X.Arrival("Limsa Lominsa Lower Decks"));
        Assert.Equal("The Sapphire Avenue Exchange.", X.Arrival("Ul'dah - Steps of Nald"));
        Assert.Equal("The Sapphire Avenue Exchange.", X.Arrival("Ul’dah - Steps of Nald", G()));
        Assert.Null(X.Arrival("Gridania"));
    }

    [Fact]
    public void WhosThatFindsAPersonByAnyWordOfTheirName()
    {
        Assert.Equal("alphinaud", X.Person("Alphinaud")!.Key);
        Assert.Null(X.Person("Alisaie"));
        var v1 = Codex.Read("""{ "version": 1, "entries": [ { "key": "h", "kind": "person", "name": "Ser Haurchefant Greystone", "layers": [] } ] }""");
        Assert.Equal("h", v1.Person("Haurchefant")!.Key);
        Assert.Null(v1.Person("Greyston"));
    }

    [Fact]
    public void AByteOrderMarkIsFine()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(File)).ToArray();
        Assert.Equal(8, Codex.Read(bytes).Entries.Count);
    }

    /// <summary>The Codex from the game: about 5,000 entries (thousands of people), read and matched in good time.</summary>
    [Fact]
    public void AGameSizedCodexIsQuick()
    {
        var rng = new Random(7);
        string Word(int i) => "N" + new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + rng.Next(26))).ToArray()) + i;
        var json = new System.Text.StringBuilder("{\"version\":2,\"entries\":[");
        var names = new List<string>();
        for (var i = 0; i < 5000; i++)
        {
            var name = i % 3 == 0 ? $"{Word(i)} {Word(i + 1)}" : Word(i);
            names.Add(name);
            if (i > 0) json.Append(',');
            json.Append($"{{\"key\":\"k{i}\",\"kind\":\"{(i % 5 == 0 ? "place" : "person")}\",\"name\":\"{name}\",\"aliases\":[\"{Word(i)}\"],");
            json.Append("\"layers\":[");
            json.Append($"{{\"text\":\"{string.Join(' ', Enumerable.Repeat("Some words about them, as the game tells it.", 8))}\",\"source\":\"codex\"}},");
            json.Append($"{{\"text\":\"A page behind a quest.\",\"source\":\"codex\",\"quest\":{66000 + i}}},");
            json.Append("{\"text\":\"A title\",\"source\":\"title\"}]");
            json.Append(i % 5 == 0 ? ",\"whisper\":\"A sentence.\"}" : "}");
        }
        json.Append("]}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(json.ToString());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var codex = Codex.Read(bytes);
        var read = clock.Elapsed;
        Assert.Equal(5000, codex.Entries.Count);

        // A late-game corpus: 900 finished quests' journals, a few hundred of them naming someone.
        var corpus = Enumerable.Range(0, 900)
            .Select(i => string.Join(' ', Enumerable.Repeat("You set out across the plains, and the wind was cold.", 12)) + (i % 3 == 0 ? $" {names[i * 5]} waits." : string.Empty))
            .ToList();
        clock.Restart();
        var seen = codex.SeenKeys(corpus);
        var matched = clock.Elapsed;
        Assert.Equal(300, seen.Count);

        clock.Restart();
        var gates = new CodexGates(Spoilers.Strict, 5, false, q => q % 2 == 0);
        var visible = codex.Entries.Count(e => codex.Visible(e, seen, gates));
        var asked = codex.Ask("Na", seen, gates);
        var listed = clock.Elapsed;
        Assert.True(visible >= seen.Count);

        Assert.True(read < TimeSpan.FromSeconds(2), $"read in {read.TotalMilliseconds:0} ms");
        Assert.True(matched < TimeSpan.FromSeconds(3), $"matched in {matched.TotalMilliseconds:0} ms");
        Assert.True(listed < TimeSpan.FromSeconds(1), $"listed in {listed.TotalMilliseconds:0} ms");
        Console.WriteLine($"5,000 entries ({bytes.Length / 1024} KB): read {read.TotalMilliseconds:0} ms, seen {matched.TotalMilliseconds:0} ms, listed {listed.TotalMilliseconds:0} ms ({asked.Count} asked)");
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("F11", 0x7A, false, false, false)]
    [InlineData("Ctrl+J", 0x4A, true, false, false)]
    [InlineData("ctrl + shift + f12", 0x7B, true, true, false)]
    [InlineData("Alt+Num5", 0x65, false, false, true)]
    [InlineData("Control+PgDn", 0x22, true, false, false)]
    [InlineData("Shift+7", 0x37, false, true, false)]
    public void KeysAreRead(string text, int key, bool ctrl, bool shift, bool alt) =>
        Assert.Equal(new Hotkey(key, ctrl, shift, alt), Hotkeys.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+J+K")]
    [InlineData("Hyper+J")]
    [InlineData(null)]
    public void WhatIsntAKeyIsNone(string? text) => Assert.Null(Hotkeys.Parse(text));

    [Fact]
    public void KeysAreWrittenModifiersFirst()
    {
        Assert.Equal("Ctrl+Shift+F12", Hotkeys.Format(new Hotkey(0x7B, true, true, false)));
        Assert.Equal("Ctrl+J", Hotkeys.Normalise("j + ctrl"));
        Assert.Equal(string.Empty, Hotkeys.Normalise("none"));
        Assert.Null(Hotkeys.Normalise("Ctrl+Nope"));
    }

    [Fact]
    public void APressFiresOnceWithExactlyItsModifiers()
    {
        var k = Hotkeys.Parse("Ctrl+J")!.Value;
        Assert.True(Hotkeys.Pressed(k, true, true, false, false, false));
        Assert.False(Hotkeys.Pressed(k, true, true, false, false, true));
        Assert.False(Hotkeys.Pressed(k, true, false, false, false, false));
        Assert.False(Hotkeys.Pressed(k, true, true, true, false, false));
        Assert.False(Hotkeys.Pressed(k, false, true, false, false, false));
    }
}

public class JsonTests
{
    [Fact]
    public void TextIsTidiedAsTheSiteTidiesIt()
    {
        Assert.Equal("a b c", Json.Tidy(" a\u0001b \n\t c "));
        Assert.Equal("abc", Json.Tidy("abcdef", 3));
        Assert.Equal(string.Empty, Json.Tidy(null));
    }

    [Fact]
    public void ReadingIsForgiving()
    {
        var o = System.Text.Json.Nodes.JsonNode.Parse("{\"n\": 3.7, \"s\": \"12\", \"b\": \"true\", \"t\": \"2026-11-08T20:00:00Z\", \"x\": [1, \"a\", null]}")!;
        Assert.Equal(3, Json.Int(o["n"]));
        Assert.Equal(12, Json.Int(o["s"]));
        Assert.Equal(7, Json.Int(o["missing"], 7));
        Assert.True(Json.Bool(o["b"]));
        Assert.Equal(new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc), Json.ReadTime(o["t"]));
        Assert.Equal(DateTimeKind.Utc, Json.ReadTime(o["t"])!.Value.Kind);
        Assert.Equal(new[] { "a" }, Json.Strings(o["x"]));
        Assert.Null(Json.Parse("{oops"));
    }
}
