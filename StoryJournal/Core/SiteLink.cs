using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StoryJournal.Core;

/// <summary>
/// The link to Story Journal on kokos-tools.com: a token made on the site (Settings → Story Journal →
/// The plugin), "sjp_" and 43 letters, kept in the plugin's settings and never logged. The plugin calls
/// the site's database functions for it (<c>sj_plugin_…</c>, through <see cref="SiteRpc"/>) with the
/// site's publishable key, as the site itself would; the token says whose account it is.
/// </summary>
public static class SiteLink
{
    /// <summary>The site's database (Supabase): its address and its publishable key, public by design.</summary>
    public const string SupabaseUrl = "https://upjcxjuuzxujqddtwehn.supabase.co";

    public const string PublishableKey = "sb_publishable_A-5zpDIk5O6xyKTicipcuA_M3J0sGVP";

    /// <summary>Story Journal on the site.</summary>
    public const string SiteUrl = "https://www.kokos-tools.com/story-journal";

    /// <summary>Where a token is made: Settings → Story Journal → The plugin.</summary>
    public const string TokensUrl = "https://www.kokos-tools.com/story-journal?settings=story-journal";

    /// <summary>The longer recap (the Welcome back card's button).</summary>
    public const string RecapUrl = "https://www.kokos-tools.com/story-journal/recap";

    /// <summary>The Codex, made from the game's files by kokos-tools and shared by the site and the plugin (version 2).</summary>
    public const string CodexUrl = "https://assets.kokos-tools.com/story/en/codex.json";

    /// <summary>The edge function that takes the plugin's photos.</summary>
    public const string PhotoUrl = SupabaseUrl + "/functions/v1/sj-plugin-photo";

    /// <summary>A session's own page on the site (its entry in the Chronicle).</summary>
    public static string SessionUrl(Guid id) => $"{SiteUrl}/chronicle/{id:D}";

    /// <summary>A Codex entry on the site.</summary>
    public static string CodexEntryUrl(string key) => $"{SiteUrl}/codex/{Uri.EscapeDataString(key)}";

    public const string Prefix = "sjp_";

    private static readonly Regex Shape = new(@"^sjp_[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant);

    private static readonly Regex Uuid = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);

    private static readonly Regex Key = new(@"^[A-Za-z0-9_-]{16,64}$", RegexOptions.CultureInvariant);

    /// <summary>A token as pasted, without the spaces, quotes or line breaks that come with a copy.</summary>
    public static string Clean(string? pasted) => (pasted ?? string.Empty).Trim().Trim('"', '\'', '`').Trim();

    /// <summary>Does it look like a token the site makes ("sjp_" and 43 letters)?</summary>
    public static bool LooksRight(string? token) => token != null && Shape.IsMatch(token);

    /// <summary>A token shown without giving it away: "sjp_••••••••3xQz".</summary>
    public static string Masked(string? token) =>
        string.IsNullOrEmpty(token) ? string.Empty
        : token.Length <= 8 ? new string('•', 8)
        : (token.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix : string.Empty) + new string('•', 8) + token[^4..];

    public static bool IsUuid(string? id) => id != null && Uuid.IsMatch(id);

    /// <summary>A character key the site takes (<c>^[A-Za-z0-9_-]{16,64}$</c>).</summary>
    public static bool IsKey(string? key) => key != null && Key.IsMatch(key);

    /// <summary>
    /// A character's key for the site: the SHA-256 of its content id and this install's own salt, as 32
    /// hex letters. The site never sees the content id, and another install (with another salt) gives
    /// the same character another key.
    /// </summary>
    public static string CharacterKey(ulong contentId, string salt)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"story-journal:{contentId}:{salt}"));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }

    /// <summary>A new install's salt: 32 random hex letters.</summary>
    public static string NewSalt() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
