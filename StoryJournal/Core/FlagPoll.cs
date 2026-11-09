using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace StoryJournal.Core;

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

    /// <summary>Whether a request at <paramref name="flag"/> is to be acted on, the character having last acted on <paramref name="lastActed"/>.</summary>
    public static bool Wanted(DateTime? flag, DateTime? lastActed, DateTime nowUtc)
    {
        if (flag is not { } f) return false;
        if (lastActed is { } last && f <= last) return false;
        // A clock a little ahead on the site is fine; one far in the future isn't a request for now.
        if (f - nowUtc > TimeSpan.FromMinutes(5)) return false;
        return nowUtc - f <= Fresh;
    }
}
