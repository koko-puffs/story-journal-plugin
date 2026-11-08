using System;

namespace StoryJournal.Core;

/// <summary>What the sync does after an answer to <c>sj_plugin_sync</c>.</summary>
public enum SyncStep
{
    /// <summary>The site has them: they leave the queue.</summary>
    Acknowledge,

    /// <summary>The token's no good: stop, and ask the player to link again.</summary>
    Relink,

    /// <summary>The tool's switched off on the site: keep everything, try again in a while.</summary>
    Off,

    /// <summary>Too often: wait, then carry on.</summary>
    Rate,

    /// <summary>
    /// Too much, or something the site won't take, with the main story's reading along: the same again
    /// without the reading, to see whether it's the reading (it then isn't sent again until it changes).
    /// </summary>
    WithoutProgress,

    /// <summary>Too much at once, or something in it the site won't take: send half as many.</summary>
    Split,

    /// <summary>One event on its own is refused: it's set aside (kept, never sent again).</summary>
    SetAside,

    /// <summary>One event on its own is refused as bad data: try the session alone, to see whether it's the session.</summary>
    Probe,

    /// <summary>The session alone is refused: it and its events are set aside.</summary>
    RefuseSession,

    /// <summary>No answer, or a problem on the site: try again after a backoff.</summary>
    Retry,
}

/// <summary>
/// How the sync answers the site's refusals (its hints, the migration's): <c>token</c> relink; <c>off</c>
/// keep the queue and retry later; <c>rate</c> back off; <c>size</c> and <c>data</c> first try once
/// without the main story's reading when it went along (it's measured against the site's limit too, and
/// may be what's at fault), then split the batch down to the event at fault and set that one aside (and,
/// for <c>data</c>, see whether it's the session itself); <c>gone</c> and anything else retry later. Pure.
/// </summary>
public static class SyncPolicy
{
    public static readonly TimeSpan OffWait = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan RateWait = TimeSpan.FromMinutes(5);

    public static SyncStep Decide(RpcResult result, int eventsSent, bool withProgress = false)
    {
        if (result.Ok) return SyncStep.Acknowledge;
        if (result.TokenRefused || result.Hint == "token") return SyncStep.Relink;
        switch (result.Hint)
        {
            case "off":
                return SyncStep.Off;
            case "rate":
                return SyncStep.Rate;
            case "size" or "data" when withProgress:
                return SyncStep.WithoutProgress;
            case "size":
                return eventsSent > 1 ? SyncStep.Split : eventsSent == 1 ? SyncStep.SetAside : SyncStep.RefuseSession;
            case "data":
                return eventsSent > 1 ? SyncStep.Split : eventsSent == 1 ? SyncStep.Probe : SyncStep.RefuseSession;
            default:
                return SyncStep.Retry;
        }
    }

    /// <summary>The next batch's size after a split: half, at least one.</summary>
    public static int Half(int sent) => Math.Max(1, sent / 2);
}
