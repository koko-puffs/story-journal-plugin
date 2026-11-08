using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StoryJournal.Core;

/// <summary>
/// Small readers and writers for the site's JSON: forgiving when reading (a field that's missing, null
/// or of another type gives the default, never an exception), exact when writing (camelCase names,
/// times in ISO 8601 UTC, text tidied as the site tidies it).
/// </summary>
public static class Json
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>A time as the site reads it: ISO 8601 in UTC, to the millisecond ("2026-11-08T20:02:13.512Z").</summary>
    public static string Time(DateTime at)
    {
        var utc = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>A time the site wrote, in UTC; null when it isn't one.</summary>
    public static DateTime? ReadTime(JsonNode? n)
    {
        if (Str(n) is not { Length: > 0 } s) return null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;
    }

    /// <summary>
    /// Text as the site keeps it (<c>sj_text</c>): control characters out, runs of spaces as one,
    /// trimmed, at most <paramref name="max"/> letters (0: no limit).
    /// </summary>
    public static string Tidy(string? text, int max = 0)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var s = Spaces.Replace(sb.ToString(), " ").Trim();
        if (max > 0 && s.Length > max)
        {
            s = s[..max];
            // Not half a surrogate pair at the end.
            if (char.IsHighSurrogate(s[^1])) s = s[..^1];
            s = s.TrimEnd();
        }
        return s;
    }

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string Str(JsonNode? n, string fallback) => Str(n) ?? fallback;

    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        // (Nodes made here hold the type they were made with.)
        if (v.TryGetValue<uint>(out var u)) return u;
        if (v.TryGetValue<ulong>(out var ul)) return ul > long.MaxValue ? long.MaxValue : (long)ul;
        if (v.TryGetValue<ushort>(out var us)) return us;
        if (v.TryGetValue<short>(out var sh)) return sh;
        if (v.TryGetValue<byte>(out var by)) return by;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)Math.Floor(d);
        if (v.TryGetValue<decimal>(out var m)) return (long)Math.Floor(m);
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public static int Int(JsonNode? n, int fallback = 0) => Long(n) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : fallback;

    public static bool Bool(JsonNode? n, bool fallback = false)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s == "true" || (s != "false" && fallback);
        return fallback;
    }

    public static Guid? Uuid(JsonNode? n) => Str(n) is { } s && SiteLink.IsUuid(s) && Guid.TryParse(s, out var g) ? g : null;

    /// <summary>Every string in an array (others left out).</summary>
    public static List<string> Strings(JsonNode? n)
    {
        var list = new List<string>();
        if (n is not JsonArray a) return list;
        foreach (var item in a)
            if (Str(item) is { } s) list.Add(s);
        return list;
    }

    public static IEnumerable<JsonObject> Objects(JsonNode? n)
    {
        if (n is not JsonArray a) yield break;
        foreach (var item in a)
            if (item is JsonObject o) yield return o;
    }

    public static JsonArray Array(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }

    /// <summary>The UTF-8 size of a node written compactly.</summary>
    public static int Bytes(JsonNode? n) => n == null ? 4 : Encoding.UTF8.GetByteCount(n.ToJsonString(Compact));

    /// <summary>Parses JSON text; null when it isn't JSON.</summary>
    public static JsonNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
