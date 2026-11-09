using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

/// <summary>What the poll does after the site's answer (<see cref="FlagPoll.Decide"/>).</summary>
public enum PollStep
{
    /// <summary>Answered: the flag (if any) is looked at, and the next poll goes at the usual pace.</summary>
    Done,

    /// <summary>Too often (<c>rate</c>), or a key the site doesn't take (<c>data</c>: it won't next time either): wait a while.</summary>
    Hold,

    /// <summary>The token's no good, or Story Journal is switched off (<c>off</c>, <c>full</c>): as every call.</summary>
    Refused,

    /// <summary>No answer, or the site's trouble: back off, quietly (the poll's trouble isn't the journal's to show).</summary>
    Retry,
}

/// <summary>
/// "Flag it in game" from the site: the site keeps when you last asked (Today's button), and the plugin asks
/// about every <see cref="Every"/> with <c>sj_plugin_poll(p_token, p_key)</c>, answered <c>{ "flag": ISO | null }</c>
/// (or refused with the hints the other calls use: <c>token</c>, <c>off</c>, <c>rate</c>). A flag newer than the
/// last one this character acted on, and not older than <see cref="Fresh"/> (a request from yesterday isn't
/// for tonight), is acted on once the game is calm: the map flag on the current step, and the tracker's
/// ribbon dropped. Pure.
/// </summary>
public static class FlagPoll
{
    /// <summary>What the poll does after the site's answer (pure, tested).</summary>
    public static PollStep Decide(RpcResult r)
    {
        if (r.Ok) return PollStep.Done;
        if (r.TokenRefused || r.Hint is "token" or "off" or "full") return PollStep.Refused;
        if (r.Hint is "rate" or "data") return PollStep.Hold;
        return PollStep.Retry;
    }

    /// <summary>How often the plugin asks, while you're logged in and linked.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>How old a request can be and still be acted on.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(15);

    /// <summary>The request's moment in an answer's text, or null (none, not JSON, or nothing that reads as a time).</summary>
    public static DateTime? Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return Read(JsonNode.Parse(body));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The request's moment in an answer, or null (none, or nothing that reads as a time).</summary>
    public static DateTime? Read(JsonNode? body)
    {
        if (body is not JsonObject o) return null;
        if (o["flag"] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
            : null;
    }

    /// <summary>The site's own time in an answer (`now`), or null (an older site, or nothing that reads as a time).</summary>
    public static DateTime? ReadNow(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonNode.Parse(body) is JsonObject o ? Time(o["now"]) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static DateTime? Time(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
        && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
            : null;

    /// <summary>
    /// Whether a request at <paramref name="flag"/> (the site's time) is to be acted on, the character
    /// having last acted on <paramref name="lastActed"/> (also the site's time: the request's own, so the
    /// same one is never acted on twice). Freshness is judged on the site's clock: <paramref name="skew"/>
    /// is how far the site's clock is ahead of this PC's (from the answer's `now`; zero when unknown), so a
    /// PC clock that's off doesn't lose or replay a flag.
    /// </summary>
    public static bool Wanted(DateTime? flag, DateTime? lastActed, DateTime nowUtc, TimeSpan skew = default)
    {
        if (flag is not { } f) return false;
        if (lastActed is { } last && f <= last) return false;
        var siteNow = nowUtc + skew;
        // A clock a little ahead on the site is fine; one far in the future isn't a request for now.
        if (f - siteNow > TimeSpan.FromMinutes(5)) return false;
        return siteNow - f <= Fresh;
    }
}
