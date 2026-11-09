using System;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using StoryJournal.Core;
using Xunit;

namespace StoryJournal.Tests;

/// <summary>The site's motion in the plugin: the four curves, an animation's clock, the shapes, the roman numerals.</summary>
public class MotionTests
{
    private static readonly Func<float, float>[] Curves = { Ease.Settle, Ease.Ink, Ease.Weight, Ease.Leave };

    [Fact]
    public void EveryCurveStartsAtNothingAndEndsAtAll()
    {
        foreach (var c in Curves)
        {
            Assert.Equal(0f, c(0f));
            Assert.Equal(1f, c(1f));
            Assert.Equal(0f, c(-1f));
            Assert.Equal(1f, c(2f));
        }
    }

    [Fact]
    public void TheCurvesHaveTheirCharacters()
    {
        // Settle: fast, then easing off (well past half by a quarter of the time).
        Assert.True(Ease.Settle(0.25f) > 0.5f);
        // Ink: slow to start, slow to lift (symmetric enough round the middle).
        Assert.True(Ease.Ink(0.15f) < 0.1f);
        Assert.True(Ease.Ink(0.85f) > 0.9f);
        // Leave: gathers speed.
        Assert.True(Ease.Leave(0.5f) < 0.5f);
        // Weight: one small overshoot past the end, then back.
        var peak = Enumerable.Range(1, 99).Select(i => Ease.Weight(i / 100f)).Max();
        Assert.InRange(peak, 1.01f, 1.2f);
    }

    [Fact]
    public void SettleIsCssCubicBezier()
    {
        // cubic-bezier(0.2, 0.7, 0.2, 1) at x = 0.5 is 0.9296 (solved by bisection, independently).
        Assert.InRange(Ease.Settle(0.5f), 0.9285f, 0.9305f);
        Assert.InRange(Ease.Bezier(0.25f, 0.1f, 0.25f, 1f, 0.5f), 0.79f, 0.81f); // CSS "ease"
    }

    [Fact]
    public void AnAnimationRunsOnItsClockAndSkipsWhenCalm()
    {
        var a = new Anim(0.42f, Ease.Linear);
        Assert.False(a.Started);
        Assert.Equal(1f, a.Value(5)); // never started: simply there
        a.Start(10, 0.08f);
        Assert.Equal(0f, a.Value(10), 4);
        Assert.Equal(0f, a.Value(10.08), 4);
        Assert.InRange(a.Value(10.29), 0.49f, 0.51f);
        Assert.True(a.Done(10.5));
        Assert.Equal(1f, a.Value(10.1, skip: true));
        Assert.Equal(15f, a.Between(10, 20, 10.29), 1);
        a.StartOnce(99);
        Assert.Equal(0f, a.Time(10.08), 4); // StartOnce left it as it was
        a.Reset();
        Assert.False(a.Started);
    }

    [Fact]
    public void TheRibbonIsFivePointsWithItsFootCutInAV()
    {
        var p = Shapes.Ribbon(new Vector2(10, 0), 20, 40);
        Assert.Equal(5, p.Length);
        Assert.Equal(new Vector2(10, 0), p[0]);
        Assert.Equal(new Vector2(30, 0), p[1]);
        Assert.Equal(new Vector2(30, 40), p[2]);
        Assert.Equal(new Vector2(20, 40 - 40 * 0.22f), p[3]);
        Assert.Equal(new Vector2(10, 40), p[4]);
        // Half unrolled: half as long, the V no deeper than the ribbon.
        var half = Shapes.Ribbon(Vector2.Zero, 20, 40, 0.5f);
        Assert.Equal(20, half[2].Y);
        var tiny = Shapes.Ribbon(Vector2.Zero, 20, 40, 0.1f);
        Assert.True(tiny[3].Y >= 0);
    }

    [Fact]
    public void ASealsOutlineIsTheSameEveryTimeForItsSeed()
    {
        var a = Shapes.SealOutline(Vector2.Zero, 10, 42);
        var b = Shapes.SealOutline(Vector2.Zero, 10, 42);
        var c = Shapes.SealOutline(Vector2.Zero, 10, 43);
        Assert.Equal(20, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        foreach (var p in a) Assert.InRange(p.Length(), 9.2f, 10.8f);
        Assert.Equal(Shapes.Seed("Talk of the Highlands"), Shapes.Seed("Talk of the Highlands"));
        Assert.Equal(5, Shapes.Crack(Vector2.Zero, 10).Length);
    }

    [Fact]
    public void TheHatchStaysInItsBox()
    {
        var min = new Vector2(5, 5);
        var max = new Vector2(65, 25);
        var lines = Shapes.Hatch(min, max, 8);
        Assert.NotEmpty(lines);
        foreach (var (a, b) in lines)
        {
            foreach (var p in new[] { a, b })
            {
                Assert.InRange(p.X, min.X - 0.01f, max.X + 0.01f);
                Assert.InRange(p.Y, min.Y - 0.01f, max.Y + 0.01f);
            }
        }
        Assert.Empty(Shapes.Hatch(min, min, 8));
    }

    [Theory]
    [InlineData(1, "i")]
    [InlineData(4, "iv")]
    [InlineData(9, "ix")]
    [InlineData(14, "xiv")]
    [InlineData(40, "xl")]
    [InlineData(0, "0")]
    public void ChaptersAreRomanNumerals(int n, string roman) => Assert.Equal(roman, Roman.Of(n));
}

/// <summary>The site's "Flag it in game": the poll's answer, and whether to act on it.</summary>
public class FlagPollTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheAnswerReadsAsAMoment()
    {
        Assert.Equal(new DateTime(2026, 11, 9, 19, 58, 30, DateTimeKind.Utc), FlagPoll.Read("""{"flag":"2026-11-09T19:58:30.123+00:00"}""")?.AddMilliseconds(-123));
        Assert.Equal(DateTimeKind.Utc, FlagPoll.Read("""{"flag":"2026-11-09T21:58:30+02:00"}""")!.Value.Kind);
        Assert.Equal(new DateTime(2026, 11, 9, 19, 58, 30, DateTimeKind.Utc), FlagPoll.Read("""{"flag":"2026-11-09T21:58:30+02:00"}"""));
        Assert.Null(FlagPoll.Read("""{"flag":null}"""));
        Assert.Null(FlagPoll.Read("{}"));
        Assert.Null(FlagPoll.Read("""{"flag":"soon"}"""));
        Assert.Null(FlagPoll.Read("""{"flag":12}"""));
        Assert.Null(FlagPoll.Read("not json"));
        Assert.Null(FlagPoll.Read((string?)null));
        Assert.Null(FlagPoll.Read(JsonNode.Parse("[1]")));
    }

    [Fact]
    public void AFlagIsActedOnOnceWhileItsFresh()
    {
        var asked = Now.AddMinutes(-1);
        Assert.True(FlagPoll.Wanted(asked, null, Now));
        Assert.True(FlagPoll.Wanted(asked, asked.AddMinutes(-5), Now));
        // Acted on already, or older.
        Assert.False(FlagPoll.Wanted(asked, asked, Now));
        Assert.False(FlagPoll.Wanted(asked, asked.AddSeconds(1), Now));
        // Too old for tonight, none, or far in the future.
        Assert.False(FlagPoll.Wanted(Now.AddMinutes(-16), null, Now));
        Assert.False(FlagPoll.Wanted(null, null, Now));
        Assert.False(FlagPoll.Wanted(Now.AddMinutes(10), null, Now));
        // A site clock a little ahead is fine.
        Assert.True(FlagPoll.Wanted(Now.AddMinutes(1), null, Now));
        Assert.Equal(TimeSpan.FromSeconds(30), FlagPoll.Every);
    }

    [Fact]
    public void AFlagThatWaitedPastItsFreshnessIsLetGo()
    {
        // Asked during a long duty: still waiting 16 minutes on, it's no longer for now.
        var asked = Now.AddMinutes(-1);
        Assert.True(FlagPoll.Wanted(asked, null, Now.AddMinutes(10)));
        Assert.False(FlagPoll.Wanted(asked, null, Now.AddMinutes(15)));
        // A PC clock 20 minutes slow: judged on the site's clock (from the answer's `now`), still fresh.
        var slow = TimeSpan.FromMinutes(20);
        Assert.True(FlagPoll.Wanted(asked, null, Now.AddMinutes(1) - slow, slow));
        Assert.False(FlagPoll.Wanted(asked, null, Now.AddMinutes(1) - slow));
        // …and one 30 minutes fast: still fresh too.
        Assert.True(FlagPoll.Wanted(asked, null, Now.AddMinutes(31), TimeSpan.FromMinutes(-30)));
        Assert.Equal(new DateTime(2026, 11, 9, 20, 2, 0, DateTimeKind.Utc), FlagPoll.ReadNow("{\"flag\":null,\"now\":\"2026-11-09T20:02:00Z\"}"));
        Assert.Null(FlagPoll.ReadNow("{\"flag\":null}"));
    }

    [Fact]
    public void ThePollFollowsTheSitesAnswer()
    {
        Assert.Equal(PollStep.Done, FlagPoll.Decide(new RpcResult(true, "{\"flag\":null}", null)));
        // The token, or switched off on the site: as every call (relink, or wait).
        Assert.Equal(PollStep.Refused, FlagPoll.Decide(SiteRpc.Read(HttpStatusCode.BadRequest, """{"message":"No.","hint":"token"}""")));
        Assert.Equal(PollStep.Refused, FlagPoll.Decide(SiteRpc.Read(HttpStatusCode.Forbidden, """{"message":"Off.","hint":"off"}""")));
        // Too often, or a key the site won't take (it won't next time either): wait a while, not every 30 s.
        Assert.Equal(PollStep.Hold, FlagPoll.Decide(SiteRpc.Read(HttpStatusCode.BadRequest, """{"message":"Too often.","hint":"rate"}""")));
        Assert.Equal(PollStep.Hold, FlagPoll.Decide(SiteRpc.Read(HttpStatusCode.BadRequest, """{"message":"Which?","hint":"data"}""")));
        // No answer, the site's trouble, an older site without the call: back off quietly.
        Assert.Equal(PollStep.Retry, FlagPoll.Decide(new RpcResult(false, null, "Couldn't reach the site.", Unreachable: true)));
        Assert.Equal(PollStep.Retry, FlagPoll.Decide(SiteRpc.Read(HttpStatusCode.InternalServerError, "oops")));
        Assert.Equal(PollStep.Retry, FlagPoll.Decide(SiteRpc.Read(HttpStatusCode.NotFound, "{}")));
    }
}

/// <summary>How a theory is settled: called, partly, missed, changed (and sealed again).</summary>
public class TheoryAnswerTests
{
    [Fact]
    public void TheAnswersAreTheSitesFour()
    {
        Assert.Equal(new[] { "called", "partly", "missed", "changed" }, TheoryAnswers.Settled);
        foreach (var s in TheoryAnswers.Settled.Append("sealed")) Assert.True(TheoryAnswers.Valid(s));
        Assert.False(TheoryAnswers.Valid("right"));
        Assert.False(TheoryAnswers.Valid(null));
        Assert.Equal("partly", TheoryAnswers.At(1));
        Assert.Null(TheoryAnswers.At(4));
        Assert.Null(TheoryAnswers.At(-1));
        Assert.Equal(1, TheoryAnswers.IndexOf("partly"));
        Assert.Equal(-1, TheoryAnswers.IndexOf("sealed"));
    }

    [Fact]
    public void EachHasItsWords()
    {
        Assert.Equal(new[] { "Called it", "Partly", "Missed it", "Changed my mind" }, TheoryAnswers.Settled.Select(TheoryAnswers.Button));
        Assert.Equal(new[] { "Called it", "Partly", "Not quite", "Changed my mind" }, TheoryAnswers.Settled.Select(TheoryAnswers.Stamp));
        Assert.Equal("Partly right.", TheoryAnswers.Toast("partly"));
        Assert.Equal("Sealed again.", TheoryAnswers.Toast("sealed"));
    }

    [Fact]
    public void TheStoryAnsweringATheoryIsToldOnceAndOnlyAsItHappens()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var none = new System.Collections.Generic.HashSet<Guid>();
        // The first look (at login): what's due already isn't news.
        Assert.Empty(TheoryAnswers.JustAnswered(-1, 120, new[] { a }, none));
        // A quest finished brings b due: told; a was due before: not again.
        Assert.Equal(new[] { b }, TheoryAnswers.JustAnswered(120, 121, new[] { a, b }, new System.Collections.Generic.HashSet<Guid> { a }));
        // The reading didn't move (the site's answer came, a theory settled): nothing told.
        Assert.Empty(TheoryAnswers.JustAnswered(121, 121, new[] { a, b }, none));
        // Twice in the list: once.
        Assert.Equal(new[] { b }, TheoryAnswers.JustAnswered(121, 122, new[] { b, b }, none));
    }
}
