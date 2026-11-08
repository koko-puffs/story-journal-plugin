using System.Net;
using System.Text.Json.Nodes;
using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

/// <summary>The link to the site: the token's shape, the character's key, and the site's answers in words.</summary>
public class SiteLinkTests
{
    private const string Token = "sjp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";

    [Fact]
    public void ATokenIsSjpAndFortyThreeLetters()
    {
        Assert.Equal(47, Token.Length);
        Assert.True(SiteLink.LooksRight(Token));
        Assert.False(SiteLink.LooksRight("mlp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ"));
        Assert.False(SiteLink.LooksRight("sjp_short"));
        Assert.False(SiteLink.LooksRight(Token + "x"));
        Assert.False(SiteLink.LooksRight("sjp_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO+Q"));
        Assert.False(SiteLink.LooksRight(null));
    }

    [Fact]
    public void APastedTokenIsCleaned()
    {
        Assert.Equal(Token, SiteLink.Clean($"  \"{Token}\"\n"));
        Assert.Equal(Token, SiteLink.Clean($"`{Token}`"));
        Assert.Equal(string.Empty, SiteLink.Clean(null));
    }

    [Fact]
    public void ATokenIsShownMasked()
    {
        Assert.Equal("sjp_••••••••NOPQ", SiteLink.Masked(Token));
        Assert.DoesNotContain("abcdef", SiteLink.Masked(Token));
        Assert.Equal(string.Empty, SiteLink.Masked(""));
        Assert.Equal("••••••••", SiteLink.Masked("short"));
    }

    [Fact]
    public void ACharactersKeyIsSaltedAndFitsTheSite()
    {
        var key = SiteLink.CharacterKey(18014398509481984UL, "salt-one");
        Assert.Equal(32, key.Length);
        Assert.True(SiteLink.IsKey(key));
        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.Equal(key, SiteLink.CharacterKey(18014398509481984UL, "salt-one"));
        Assert.NotEqual(key, SiteLink.CharacterKey(18014398509481984UL, "salt-two"));
        Assert.NotEqual(key, SiteLink.CharacterKey(18014398509481985UL, "salt-one"));
        Assert.Matches("^[0-9a-f]{32}$", SiteLink.NewSalt());
    }

    [Fact]
    public void KeysAreCheckedAsTheSiteChecksThem()
    {
        Assert.True(SiteLink.IsKey("abcdefghijklmnop"));
        Assert.False(SiteLink.IsKey("short"));
        Assert.False(SiteLink.IsKey("has space in it!!"));
        Assert.False(SiteLink.IsKey(new string('a', 65)));
    }

    [Fact]
    public void AddressesAreTheSites()
    {
        Assert.Equal("https://upjcxjuuzxujqddtwehn.supabase.co/functions/v1/sj-plugin-photo", SiteLink.PhotoUrl);
        Assert.Equal("https://www.kokos-tools.com/story-journal?settings=story-journal", SiteLink.TokensUrl);
        Assert.Equal("https://www.kokos-tools.com/story-journal/codex.json", SiteLink.CodexUrl);
        Assert.Equal("https://www.kokos-tools.com/story-journal/recap", SiteLink.RecapUrl);
    }
}

public class SiteRpcTests
{
    [Fact]
    public void ASuccessIsTheFunctionsJson()
    {
        var r = SiteRpc.Read(HttpStatusCode.OK, "{\"ok\":true,\"added\":3}");
        Assert.True(r.Ok);
        Assert.Equal("{\"ok\":true,\"added\":3}", r.Body);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("rate")]
    [InlineData("size")]
    [InlineData("data")]
    [InlineData("gone")]
    public void TheSitesHintsAreInItsOwnWords(string hint)
    {
        var r = SiteRpc.Read(HttpStatusCode.BadRequest, $"{{\"message\":\"Worded for you.\",\"hint\":\"{hint}\",\"code\":\"22023\"}}");
        Assert.False(r.Ok);
        Assert.Equal("Worded for you.", r.Why);
        Assert.Equal(hint, r.Hint);
        Assert.False(r.TokenRefused);
    }

    [Fact]
    public void ARefusedTokenSaysLinkAgain()
    {
        var r = SiteRpc.Read(HttpStatusCode.BadRequest, "{\"message\":\"That token was revoked, or never made. Make a new one on the site.\",\"hint\":\"token\",\"code\":\"28000\"}");
        Assert.True(r.TokenRefused);
        Assert.Contains("Link again", r.Why);
        Assert.Equal("token", r.Hint);
    }

    [Fact]
    public void TheDatabasesOwnWordsAreNotShown()
    {
        var r = SiteRpc.Read(HttpStatusCode.BadRequest, "{\"message\":\"null value in column \\\"x\\\" violates not-null constraint\",\"code\":\"23502\"}");
        Assert.DoesNotContain("column", r.Why);
        Assert.Null(r.Hint);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(429, true)]
    [InlineData(404, false)]
    public void ProblemsOnTheSiteAreWorthTryingAgain(int status, bool again)
    {
        var r = SiteRpc.Read((HttpStatusCode)status, "<html>oops</html>");
        Assert.False(r.Ok);
        Assert.Equal(again, r.Unreachable);
        Assert.Equal(status == 404, r.NotFound);
    }

    [Fact]
    public async Task ACallWithoutATokenNeverLeaves()
    {
        using var http = new HttpClient();
        var r = await SiteRpc.CallAsync(http, "sj_plugin_hello", SiteRpc.Args("not-a-token"), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(r.TokenRefused);
    }

    [Fact]
    public async Task OnlyThePluginsFunctionsAreCalled()
    {
        using var http = new HttpClient();
        await Assert.ThrowsAsync<ArgumentException>(() => SiteRpc.PostAsync(http, "sj_partner_lock", "{}", CancellationToken.None));
    }
}

public class SyncPolicyTests
{
    private static RpcResult Refused(string hint) => SiteRpc.Read(HttpStatusCode.BadRequest, $"{{\"message\":\"x\",\"hint\":\"{hint}\"}}");

    [Fact]
    public void EachHintHasItsStep()
    {
        Assert.Equal(SyncStep.Acknowledge, SyncPolicy.Decide(new RpcResult(true, "{}", null), 10));
        Assert.Equal(SyncStep.Relink, SyncPolicy.Decide(Refused("token"), 10));
        Assert.Equal(SyncStep.Off, SyncPolicy.Decide(Refused("off"), 10));
        Assert.Equal(SyncStep.Rate, SyncPolicy.Decide(Refused("rate"), 10));
        Assert.Equal(SyncStep.Retry, SyncPolicy.Decide(Refused("gone"), 10));
        Assert.Equal(SyncStep.Retry, SyncPolicy.Decide(new RpcResult(false, null, "Couldn't reach the site.", Unreachable: true), 10));
    }

    [Fact]
    public void TooMuchIsSplitDownToTheOneAtFault()
    {
        Assert.Equal(SyncStep.Split, SyncPolicy.Decide(Refused("size"), 200));
        Assert.Equal(SyncStep.SetAside, SyncPolicy.Decide(Refused("size"), 1));
        Assert.Equal(SyncStep.Split, SyncPolicy.Decide(Refused("data"), 2));
        Assert.Equal(SyncStep.Probe, SyncPolicy.Decide(Refused("data"), 1));
        Assert.Equal(SyncStep.RefuseSession, SyncPolicy.Decide(Refused("data"), 0));
        Assert.Equal(100, SyncPolicy.Half(200));
        Assert.Equal(1, SyncPolicy.Half(1));
    }

    [Fact]
    public void WithTheReadingAlongItsTriedWithoutItFirst()
    {
        // The reading may be what's too big (or what the site won't take): the events aren't split for it.
        Assert.Equal(SyncStep.WithoutProgress, SyncPolicy.Decide(Refused("size"), 200, withProgress: true));
        Assert.Equal(SyncStep.WithoutProgress, SyncPolicy.Decide(Refused("size"), 1, withProgress: true));
        Assert.Equal(SyncStep.WithoutProgress, SyncPolicy.Decide(Refused("data"), 0, withProgress: true));
        Assert.Equal(SyncStep.Split, SyncPolicy.Decide(Refused("size"), 200, withProgress: false));
        Assert.Equal(SyncStep.Rate, SyncPolicy.Decide(Refused("rate"), 200, withProgress: true));
        Assert.Equal(1, SyncPolicy.Half(1));
    }
}

public class PhotoUploadTests
{
    [Fact]
    public void AKeptPhotoIsItsIdAndData()
    {
        var r = PhotoUpload.Read(HttpStatusCode.OK, "{\"id\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"data\":{\"path\":\"u/x.jpg\",\"width\":1920,\"height\":1080,\"caption\":\"\"}}");
        Assert.Equal(PhotoOutcome.Kept, r.Outcome);
    }

    [Theory]
    [InlineData(401, "token", PhotoOutcome.Token)]
    [InlineData(403, "off", PhotoOutcome.Later)]
    [InlineData(429, "rate", PhotoOutcome.Rate)]
    [InlineData(404, "gone", PhotoOutcome.Later)]
    [InlineData(400, "image", PhotoOutcome.Refused)]
    [InlineData(400, "data", PhotoOutcome.Refused)]
    [InlineData(502, "upload", PhotoOutcome.Later)]
    [InlineData(500, "server", PhotoOutcome.Later)]
    public void TheFunctionsHintsAreFollowed(int status, string hint, PhotoOutcome outcome)
    {
        var r = PhotoUpload.Read((HttpStatusCode)status, $"{{\"message\":\"Said for you.\",\"hint\":\"{hint}\"}}");
        Assert.Equal(outcome, r.Outcome);
        Assert.StartsWith("Said for you.", r.Why);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void TurnedAwayWithoutTheTokenHintIsNotAnUnlink(int status)
    {
        // A gateway's 401 (no hint, or not JSON) isn't the site saying the token's no good.
        Assert.Equal(PhotoOutcome.Later, PhotoUpload.Read((HttpStatusCode)status, "{\"message\":\"Invalid JWT\"}").Outcome);
        Assert.Equal(PhotoOutcome.Later, PhotoUpload.Read((HttpStatusCode)status, "<html>401</html>").Outcome);
        Assert.False(SiteRpc.Read((HttpStatusCode)status, "{\"message\":\"Invalid JWT\"}").TokenRefused);
    }

    [Fact]
    public async Task TheFormHasEveryFieldTheFunctionReads()
    {
        var id = Guid.NewGuid();
        var session = Guid.NewGuid();
        using var form = PhotoUpload.Form("sjp_x", "abcdefghijklmnop", session, id, new DateTime(2026, 11, 8, 20, 2, 0, DateTimeKind.Utc), "  At the   Navel ", new byte[] { 0xff, 0xd8 });
        var names = form.Select(c => c.Headers.ContentDisposition!.Name!.Trim('"')).ToList();
        Assert.Equal(new[] { "token", "character", "session", "id", "at", "caption", "image" }, names);
        var caption = await form.First(c => c.Headers.ContentDisposition!.Name!.Trim('"') == "caption").ReadAsStringAsync();
        Assert.Equal("At the Navel", caption);
        var at = await form.First(c => c.Headers.ContentDisposition!.Name!.Trim('"') == "at").ReadAsStringAsync();
        Assert.Equal("2026-11-08T20:02:00.000Z", at);
        Assert.Equal("image/jpeg", form.Last().Headers.ContentType!.MediaType);
    }

    [Fact]
    public void PicturesAreMadeToFit()
    {
        Assert.Equal((2560, 1440), PhotoUpload.Fit(3840, 2160, 2560));
        Assert.Equal((1920, 1080), PhotoUpload.Fit(1920, 1080, 2560));
        Assert.Null(PhotoUpload.Fit(40, 40, 2560));
    }
}

public class PaceTests
{
    private static readonly DateTime T = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ACallThatCantStartIsntWaitedForForEver()
    {
        var pace = new Pace(TimeSpan.FromSeconds(10));
        pace.Changed();
        Exception? heard = null;
        // Starting it throws (the queue's disk, say): it counts as failed, not as on its way.
        Assert.True(pace.Run(T, () => throw new IOException("disk"), ex => heard = ex));
        Assert.IsType<IOException>(heard);
        Assert.False(pace.Busy);
        // Tried again after the backoff.
        Assert.False(pace.Due(T.AddSeconds(2)));
        Assert.True(pace.Due(T.AddSeconds(15)));
    }

    [Fact]
    public void AnHourlyLimitKeepsUnderTheSites()
    {
        var pace = new Pace(TimeSpan.FromSeconds(4), perHour: 3);
        var at = T;
        for (var i = 0; i < 3; i++)
        {
            pace.Changed();
            Assert.True(pace.Due(at));
            pace.Done(true, at);
            at = at.AddSeconds(5);
        }
        pace.Changed();
        Assert.False(pace.Due(at));
        Assert.False(pace.Due(T.AddMinutes(59)));
        // An hour after the first, one more may go; what was waiting goes then.
        Assert.True(pace.Due(T.AddHours(1)));
    }

    [Fact]
    public void NothingSentIsntCounted()
    {
        var pace = new Pace(TimeSpan.FromSeconds(4), perHour: 1);
        pace.Changed();
        Assert.True(pace.Due(T));
        pace.Nothing();
        pace.Changed();
        Assert.True(pace.Due(T.AddSeconds(5)));
    }
}

public class OutboxTests
{
    [Fact]
    public void ANoteWaitsForItsSessionToReachTheSite()
    {
        var notes = new[] { new NoteState(SessionStanding.Waiting), new NoteState(SessionStanding.Known) };
        var c = Outbox.NextNote(notes, out var waiting);
        // The one whose session the site has goes, linked to it; the other waits, and the queue goes first.
        Assert.Equal(new NoteChoice(1, true), c);
        Assert.True(waiting);
        Assert.Null(Outbox.NextNote(new[] { new NoteState(SessionStanding.Waiting) }, out waiting));
        Assert.True(waiting);
    }

    [Fact]
    public void ANoteWhoseSessionWontReachTheSiteGoesWithoutIt()
    {
        Assert.Equal(new NoteChoice(0, false), Outbox.NextNote(new[] { new NoteState(SessionStanding.Refused) }, out var waiting));
        Assert.False(waiting);
        Assert.Equal(new NoteChoice(0, false), Outbox.NextNote(new[] { new NoteState(SessionStanding.Missing) }, out _));
    }

    [Fact]
    public void ANoteRestingAfterGoneDoesntHoldUpTheRest()
    {
        var notes = new[] { new NoteState(SessionStanding.Known, Resting: true), new NoteState(SessionStanding.Missing) };
        Assert.Equal(new NoteChoice(1, false), Outbox.NextNote(notes, out _));
    }

    [Fact]
    public void TheoriesGoBeforeAnswers()
    {
        var theories = new[] { new TheoryState(Sent: true), new TheoryState(Sent: false) };
        var answers = new[] { AnswerFor.Site };
        Assert.Equal(new TheoryChoice(false, 1), Outbox.NextTheoryCall(theories, answers));
        // Once it's sent, the answers go.
        theories[1] = new TheoryState(Sent: true);
        Assert.Equal(new TheoryChoice(true, 0), Outbox.NextTheoryCall(theories, answers));
    }

    [Fact]
    public void AnAnswerWaitsForItsTheory()
    {
        // Answered before it went (offline, say): the site wouldn't know it ("gone"), so it waits.
        var theories = new[] { new TheoryState(Sent: false, Ready: false) };
        Assert.Null(Outbox.NextTheoryCall(theories, new[] { AnswerFor.Unsent }));
        Assert.Equal(new TheoryChoice(true, 1), Outbox.NextTheoryCall(theories, new[] { AnswerFor.Unsent, AnswerFor.Sent }));
        // Resting after "gone", or its ask-at point not worked out yet: it waits too.
        Assert.Null(Outbox.NextTheoryCall(new[] { new TheoryState(false, Resting: true) }, Array.Empty<AnswerFor>()));
    }

    [Fact]
    public void GoneIsWaitedOutThenSetAside()
    {
        Assert.Equal(GoneStep.Wait, Outbox.Gone(1, characterHeardOf: false));
        Assert.Equal(GoneStep.SetAside, Outbox.Gone(Outbox.GoneTries, characterHeardOf: false));
        // The site had the character this time and still says gone: it dropped it.
        Assert.Equal(GoneStep.SetAside, Outbox.Gone(1, characterHeardOf: true));
        Assert.Equal(GoneStep.Wait, Outbox.PhotoGone(Outbox.GoneTries - 1));
        Assert.Equal(GoneStep.SetAside, Outbox.PhotoGone(Outbox.GoneTries));
    }
}

