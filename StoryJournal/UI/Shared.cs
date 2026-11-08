using System;
using System.Collections.Generic;
using System.Globalization;

namespace StoryJournal.UI;

/// <summary>
/// What the windows share: the fonts (made by the first window, let go with the last), and the words
/// for numbers, times and durations, always in the invariant culture, as the site writes them.
/// </summary>
internal static class Ui
{
    // ---------------------------------------------------------------------------------------------
    // Fonts: every window draws with the same handles, so they're made once.

    private static Fonts? fonts;
    private static int users;

    public static Fonts AcquireFonts()
    {
        fonts ??= new Fonts();
        Txt.Fonts = fonts;
        users++;
        return fonts;
    }

    public static void ReleaseFonts()
    {
        if (users == 0) return;
        if (--users > 0) return;
        fonts?.Dispose();
        fonts = null;
    }

    // ---------------------------------------------------------------------------------------------
    // Numbers and times.

    public static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{Count(n)} {many}";

    /// <summary>"1h 34m", "25m", "40s".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        var h = (int)t.TotalHours;
        return h > 0 ? $"{h}h {t.Minutes}m" : $"{t.Minutes}m";
    }

    /// <summary>"just now", "25 minutes ago", "3 hours ago", "yesterday", "2 days ago", "on 3 Nov".</summary>
    public static string Ago(DateTime utc, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var d = now - (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc);
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return Plural((int)d.TotalMinutes, "minute", "minutes") + " ago";
        if (d < TimeSpan.FromHours(24)) return Plural((int)d.TotalHours, "hour", "hours") + " ago";
        if (d < TimeSpan.FromHours(48)) return "yesterday";
        if (d < TimeSpan.FromDays(14)) return $"{(int)d.TotalDays} days ago";
        return "on " + Local(utc).ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>"Saturday", "today", "yesterday" for the day a session was.</summary>
    public static string Day(DateTime utc)
    {
        var local = Local(utc).Date;
        var today = DateTime.Now.Date;
        if (local == today) return "today";
        if (local == today.AddDays(-1)) return "yesterday";
        if (today - local < TimeSpan.FromDays(7)) return local.ToString("dddd", CultureInfo.InvariantCulture);
        return local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>"20:41" today, else "Sat 20:41".</summary>
    public static string Clock(DateTime utc)
    {
        var local = Local(utc);
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTime Local(DateTime utc) => utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>"in 1 quest", "in ~4 quests".</summary>
    public static string QuestsAway(int n) => n <= 1 ? "after this quest" : $"in ~{n} quests";
}

/// <summary>
/// A string worked out again only when what it shows changes, so a frame doesn't make new strings for
/// numbers that stand still.
/// </summary>
internal sealed class Memo<T> where T : IEquatable<T>
{
    private T? key;
    private string? text;

    public string Get(T value, Func<T, string> make)
    {
        if (text != null && key is { } k && k.Equals(value)) return text;
        key = value;
        return text = make(value);
    }
}

/// <summary>ImGui ids with a number in them ("##chip3"), made once and kept, not every frame.</summary>
internal static class Ids
{
    private static readonly Dictionary<(string Prefix, int N), string> Made = new();

    public static string Of(string prefix, int n)
    {
        if (Made.TryGetValue((prefix, n), out var id)) return id;
        if (Made.Count > 4096) Made.Clear();
        return Made[(prefix, n)] = prefix + n.ToString(CultureInfo.InvariantCulture);
    }
}
