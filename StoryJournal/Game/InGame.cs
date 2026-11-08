using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using StoryJournal.Core;

namespace StoryJournal.Game;

/// <summary>The character you're playing, as read now.</summary>
/// <param name="Key">Its key for the site (<see cref="SiteLink.CharacterKey"/>).</param>
public sealed record PlayerNow(string Key, string Name, string World, int Level, string Job, uint JobId, uint Territory);

/// <summary>
/// What the game is doing, read on the framework thread: whether you're logged in and loaded, in
/// combat, a duty, a cutscene or a loading screen; who you are and where. Every read is guarded: before
/// login, during loading or after a patch moved something, it answers "no" rather than throwing.
/// </summary>
public static class InGame
{
    private static bool Flag(ConditionFlag flag)
    {
        try
        {
            return Services.Condition[flag];
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Logged in, the character loaded and its content id known.</summary>
    public static bool LoggedIn =>
        Services.ClientState.IsLoggedIn && Services.PlayerState.IsLoaded && Services.PlayerState.ContentId != 0;

    /// <summary>Between areas (a loading screen), or logging out: nothing is read then.</summary>
    public static bool Loading => Flag(ConditionFlag.BetweenAreas) || Flag(ConditionFlag.BetweenAreas51) || Flag(ConditionFlag.LoggingOut);

    public static bool InCombat => Flag(ConditionFlag.InCombat);

    public static bool InDuty => Flag(ConditionFlag.BoundByDuty) || Flag(ConditionFlag.BoundByDuty56) || Flag(ConditionFlag.BoundByDuty95);

    /// <summary>Watching a cutscene (a quest's scene, or one replayed).</summary>
    public static bool InCutscene => Flag(ConditionFlag.OccupiedInCutSceneEvent) || Flag(ConditionFlag.WatchingCutscene78) || Flag(ConditionFlag.WatchingCutscene);

    /// <summary>Talking to someone in a quest, or in a cutscene (not a shopkeeper's greeting: that's only "in an event").</summary>
    public static bool InQuestTalk => Flag(ConditionFlag.OccupiedInQuestEvent) || Flag(ConditionFlag.OccupiedInCutSceneEvent);

    /// <summary>A card may show: not in combat, a duty, a cutscene, a loading screen or Group Pose.</summary>
    public static bool Calm => LoggedIn && !InCombat && !InDuty && !InCutscene && !Loading && !Services.ClientState.IsGPosing;

    /// <summary>Where the character stands (null when there's none).</summary>
    public static Vector3? Position
    {
        get
        {
            try
            {
                return Services.Objects.LocalPlayer?.Position;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>The character now, or null when you aren't logged in (or it isn't loaded).</summary>
    public static PlayerNow? Player(string salt)
    {
        if (!LoggedIn) return null;
        try
        {
            var ps = Services.PlayerState;
            var job = ps.ClassJob;
            var abbr = job.IsValid ? job.Value.Abbreviation.ExtractText() : string.Empty;
            var world = ps.HomeWorld.IsValid ? ps.HomeWorld.Value.Name.ExtractText() : string.Empty;
            return new PlayerNow(SiteLink.CharacterKey(ps.ContentId, salt), ps.CharacterName, world, ps.Level, abbr, job.RowId, Services.ClientState.TerritoryType);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the character");
            return null;
        }
    }
}

/// <summary>Places and duties by their game ids: names from the sheets (framework thread or a worker: the sheets are read-only).</summary>
public static class Places
{
    /// <summary>A territory's place name and its region ("Camp Dragonhead", "Coerthas"), and the duty it is (0: none).</summary>
    public static (string Name, string Region, uint Duty) Territory(uint id)
    {
        try
        {
            if (Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(id) is not { } t) return (string.Empty, string.Empty, 0);
            var name = t.PlaceName.IsValid ? t.PlaceName.Value.Name.ExtractText() : string.Empty;
            var region = t.PlaceNameRegion.IsValid ? t.PlaceNameRegion.Value.Name.ExtractText() : string.Empty;
            return (name, region, t.ContentFinderCondition.RowId);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, $"Story Journal couldn't read territory {id}");
            return (string.Empty, string.Empty, 0);
        }
    }

    /// <summary>A duty's name (as a title: "The Navel"), its type in a word in English whatever the client's language ("trial") and its level.</summary>
    public static (string Name, string Type, int Level) Duty(uint cfc)
    {
        try
        {
            if (cfc == 0 || Services.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfc) is not { } c) return (string.Empty, "duty", 0);
            // The type by its row, not its name: the name is in the client's language, the site reads English.
            return (Sessions.Capitalised(c.Name.ExtractText()), Events.DutyType(c.ContentType.RowId), c.ClassJobLevelRequired);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, $"Story Journal couldn't read duty {cfc}");
            return (string.Empty, "duty", 0);
        }
    }
}
