using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using StoryJournal.Core;

namespace StoryJournal.Game;

/// <summary>
/// The quest log, read from the game's QuestManager on the framework thread: the quests taken and the
/// step each is on, whether a quest is finished, and the main story's quest from the scenario tree.
/// Quest ids here are the Quest sheet's rows (65536 and up); the game's ushort ids are converted both
/// ways (<see cref="QuestIds"/>). Every read is guarded and answers "nothing" when the game isn't ready.
/// </summary>
public static unsafe class Quests
{
    /// <summary>The quests taken now, each with its step (the game's sequence), by sheet row.</summary>
    public static Dictionary<uint, int> Accepted()
    {
        var list = new Dictionary<uint, int>();
        try
        {
            var qm = QuestManager.Instance();
            if (qm == null) return list;
            foreach (var q in qm->NormalQuests)
            {
                if (q.QuestId == 0) continue;
                list[QuestIds.Row(q.QuestId)] = q.Sequence;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the quest log");
        }
        return list;
    }

    /// <summary>Is a quest finished (sheet row or game id)?</summary>
    public static bool IsComplete(uint id)
    {
        try
        {
            return id != 0 && QuestManager.IsQuestComplete(QuestIds.Short(id));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The scenario tree's main-story quest: the one on the selected road (its path index), or, when none
    /// is taken, the last finished. Sheet rows; 0 when the game doesn't say.
    /// </summary>
    public static (uint Current, uint LastDone) Scenario()
    {
        try
        {
            var agent = AgentScenarioTree.Instance();
            if (agent == null || agent->Data == null) return (0, 0);
            var ids = agent->Data->MainScenarioQuestIds;
            if (ids.Length == 0) return (0, 0);
            var path = agent->Data->MSQPathIndex;
            uint current = path < ids.Length && path < 3 ? ids[path] : ids[0];
            uint last = ids.Length > 3 ? ids[3] : (ushort)0;
            return (QuestIds.Row(current), QuestIds.Row(last));
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the scenario tree");
            return (0, 0);
        }
    }

    /// <summary>Which of these quests are finished (the main story's, checked one by one: cheap).</summary>
    public static HashSet<uint> Done(IEnumerable<uint> ids)
    {
        var done = new HashSet<uint>();
        foreach (var id in ids)
            if (IsComplete(id)) done.Add(id);
        return done;
    }

    /// <summary>A quest's name and level from the sheet (for side quests the catalog doesn't hold).</summary>
    public static (string Name, int Level, int Expansion, string Place) Info(uint row)
    {
        try
        {
            if (Services.Data.GetExcelSheet<Quest>().GetRowOrDefault(QuestIds.Row(row)) is not { } q) return (string.Empty, 0, 0, string.Empty);
            var place = q.PlaceName.IsValid ? q.PlaceName.Value.Name.ExtractText() : string.Empty;
            var level = q.ClassJobLevel.Count > 0 ? q.ClassJobLevel[0] : 0;
            return (q.Name.ExtractText(), level, (int)q.Expansion.RowId, place);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, $"Story Journal couldn't read quest {row}");
            return (string.Empty, 0, 0, string.Empty);
        }
    }
}

/// <summary>
/// The current objective's place on the map: the quest's to-do for its step names a Level row (where
/// to go); the game's flag is set there and the map opened. Framework thread. Skipped quietly when the
/// to-do has no place (some steps don't).
/// </summary>
public static unsafe class MapFlag
{
    /// <summary>The Level row a quest's step points at (where to go), or null with why. Sheets only: any thread.</summary>
    public static Level? StepLevel(uint questRow, int sequence, out string why)
    {
        why = string.Empty;
        if (Services.Data.GetExcelSheet<Quest>().GetRowOrDefault(QuestIds.Row(questRow)) is not { } quest)
        {
            why = "The game doesn't know that quest.";
            return null;
        }
        var steps = new List<int>();
        foreach (var t in quest.TodoParams) steps.Add(t.ToDoCompleteSeq);
        var todo = JournalText.TodoFor(steps, sequence);
        if (todo < 0 || todo >= quest.TodoParams.Count)
        {
            why = "This step has no place on the map.";
            return null;
        }
        foreach (var loc in quest.TodoParams[todo].ToDoLocation)
        {
            if (loc.RowId != 0 && loc.IsValid && loc.Value.Territory.IsValid && loc.Value.Map.IsValid) return loc.Value;
        }
        why = "This step has no place on the map.";
        return null;
    }

    /// <summary>
    /// The step's place as the game's map shows it (X, Y to one decimal), or null when the game doesn't say.
    /// Sheets only, so it's read with the rest of the story on the worker. Guarded: null on anything unexpected.
    /// </summary>
    public static MapSpot? Spot(uint questRow, int sequence)
    {
        try
        {
            if (StepLevel(questRow, sequence, out _) is not { } l) return null;
            var map = Dalamud.Utility.MapUtil.WorldToMap(new Vector2(l.X, l.Z), l.Map.Value);
            if (!float.IsFinite(map.X) || !float.IsFinite(map.Y) || map.X <= 0 || map.Y <= 0 || map.X > 50 || map.Y > 50) return null;
            return MapSpot.Of(map.X, map.Y);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't read the step's map spot");
            return null;
        }
    }

    /// <summary>Flags the place of a quest's step; false (with why) when it can't.</summary>
    public static bool Set(uint questRow, int sequence, out string why)
    {
        why = string.Empty;
        try
        {
            if (StepLevel(questRow, sequence, out why) is not { } l) return false;
            var agent = AgentMap.Instance();
            if (agent == null)
            {
                why = "The map isn't ready.";
                return false;
            }
            agent->SetFlagMapMarker(l.Territory.RowId, l.Map.RowId, new Vector3(l.X, l.Y, l.Z));
            agent->OpenMapByMapId(l.Map.RowId, l.Territory.RowId, false);
            return true;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(ex, "Story Journal couldn't set the map flag");
            why = "Couldn't set the flag (see /xllog).";
            return false;
        }
    }
}
