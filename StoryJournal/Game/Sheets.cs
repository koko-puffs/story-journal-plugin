using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using StoryJournal.Core;

namespace StoryJournal.Game;

/// <summary>
/// The main story read from the game's sheets into a <see cref="MsqCatalog"/>, once each time the plugin
/// loads, on a worker (the sheets are read-only, so this never touches the framework thread):
/// <list type="bullet">
/// <item>The quests: the <c>ScenarioTree</c> rows that are quests (a row's id is its quest's); should
/// that give too few, the quests of the Main Scenario section of the journal instead.</item>
/// <item>Their order: the scenario tree's <c>Unknown2</c> (the game's order, shared by alternatives) and
/// <c>Type</c> (0 the main line; 1, 2, 3 the starting cities' openings); and, for the road's rules and
/// should that order be unusable, <c>PreviousQuest</c> (and <c>PreviousQuestJoin</c> 2 read as "any one
/// will do"), <c>QuestLock</c>, the expansion, the level.</item>
/// <item>Which opening each starting city (<c>Town</c>) begins: the <c>ScenarioType</c> row whose letter
/// starts the town's English name (L, G, U: Limsa Lominsa 1, Gridania 2, Ul'dah 3).</item>
/// <item>Chapters: the <c>QuestChapter</c> row whose <c>Quest</c> is the quest (its rows are numbered in
/// their own order, not by quest: 0.2.0 took the scenario tree's <c>QuestChapter</c> for a row number and
/// put nearly the whole story in the first chapter), its <c>Redo</c> row's
/// <c>QuestRedoChapterUI.ChapterName</c>; when that names too few, a chapter starts at each
/// <c>QuestRedoChapterUI.Quest</c> and runs on to the next.</item>
/// <item>The duty each unlocks: <c>InstanceContentUnlock</c> → <c>InstanceContent.ContentFinderCondition</c>.</item>
/// </list>
/// What's unverified in the game (the join's values, the chapter rows' ids) is said in DETAILS.md.
/// </summary>
public static class MsqReader
{
    /// <summary>Fewer main-story quests than this from the scenario tree: it isn't what was expected, and the journal's section is read instead.</summary>
    private const int Expected = 300;

    public static MsqCatalog Build()
    {
        var questSheet = Services.Data.GetExcelSheet<Quest>();
        var rows = new Dictionary<uint, (Quest Quest, uint ChapterRow)>();
        var places = new Dictionary<uint, (int Order, int Path)>();
        foreach (var tree in Services.Data.GetExcelSheet<ScenarioTree>())
        {
            if (questSheet.GetRowOrDefault(tree.RowId) is not { } q || q.Name.IsEmpty) continue;
            rows[q.RowId] = (q, tree.QuestChapter.RowId);
            var path = tree.Type.RowId is >= 1 and <= 3 ? (int)tree.Type.RowId : 0;
            places[q.RowId] = (tree.Unknown2, path);
        }
        var source = "the scenario tree";
        if (rows.Count < Expected)
        {
            var fromJournal = new Dictionary<uint, (Quest, uint)>();
            foreach (var q in questSheet)
                if (!q.Name.IsEmpty && IsMainScenario(q)) fromJournal[q.RowId] = (q, 0u);
            if (fromJournal.Count > rows.Count)
            {
                rows = fromJournal;
                places.Clear();
                source = "the journal's Main Scenario section";
            }
        }

        var redoUi = Services.Data.GetExcelSheet<QuestRedoChapterUI>();
        // Each quest's chapter (QuestRedoChapterUI row), by QuestChapter's Quest: the first row naming it.
        var redoOf = new Dictionary<uint, uint>();
        foreach (var row in Services.Data.GetExcelSheet<QuestChapter>())
            if (row.Quest.RowId != 0 && row.Redo.RowId != 0) redoOf.TryAdd(row.Quest.RowId, row.Redo.RowId);
        var chapters = new Dictionary<uint, MsqChapter>();
        var quests = new List<MsqQuest>();
        var named = 0;
        foreach (var (q, _) in rows.Values)
        {
            uint chapter = 0;
            try
            {
                var redo = redoOf.TryGetValue(q.RowId, out var r) ? r : 0u;
                if (redo != 0 && redoUi.GetRowOrDefault(redo) is { } ui && !ui.ChapterName.IsEmpty)
                {
                    chapter = redo;
                    named++;
                    if (!chapters.ContainsKey(redo)) chapters[redo] = new MsqChapter(redo, ChapterName(ui), (int)q.Expansion.RowId);
                }
            }
            catch (Exception ex)
            {
                Services.Log.Debug(ex, $"Story Journal: no chapter for quest {q.RowId}");
            }
            var (order, startPath) = places.TryGetValue(q.RowId, out var at) ? at : (0, 0);
            quests.Add(Read(q, chapter) with { Order = order, StartPath = startPath });
        }

        var expansions = new List<MsqExpansion>();
        foreach (var ex in Services.Data.GetExcelSheet<ExVersion>())
            if (!ex.Name.IsEmpty) expansions.Add(new MsqExpansion((int)ex.RowId, ex.Name.ExtractText()));

        var towns = TownPaths();
        var catalog = new MsqCatalog(quests, chapters.Values, expansions, towns);
        if (named < catalog.Count / 2)
        {
            // Too few named through the chapter rows: chapters start at each chapter's own quest instead.
            var starts = new Dictionary<uint, (uint Id, string Name)>();
            foreach (var ui in redoUi)
                if (!ui.ChapterName.IsEmpty && ui.Quest.RowId != 0 && !starts.ContainsKey(ui.Quest.RowId))
                    starts[ui.Quest.RowId] = (ui.RowId, ChapterName(ui));
            if (starts.Count > 0)
            {
                chapters.Clear();
                var current = 0u;
                var rebuilt = new List<MsqQuest>();
                foreach (var q in catalog.Order)
                {
                    if (starts.TryGetValue(q.Id, out var s))
                    {
                        current = s.Id;
                        if (!chapters.ContainsKey(s.Id)) chapters[s.Id] = new MsqChapter(s.Id, s.Name, q.Expansion);
                    }
                    rebuilt.Add(q with { Chapter = current });
                }
                catalog = new MsqCatalog(rebuilt, chapters.Values, expansions, towns);
                source += ", chapters from where each begins";
            }
        }
        var ordering = catalog.GameOrder ? "the game's order" : "the order of prerequisites (the game's order unusable)";
        Services.Log.Information($"Story Journal read the main story from {source}: {catalog.Count} quests, {chapters.Count} chapters, {expansions.Count} expansions, in {ordering}; starting cities' openings: {string.Join(", ", towns.Select(t => $"town {t.Key} → {t.Value}"))}");
        return catalog;
    }

    /// <summary>
    /// Which opening (<c>ScenarioType</c> 1, 2, 3, a letter each: L, G, U) each starting city (<c>Town</c>)
    /// begins: the one town whose English name starts with the type's letter. A type no single town
    /// matches is left out (the road is then told from the quests done).
    /// </summary>
    private static Dictionary<uint, int> TownPaths()
    {
        var map = new Dictionary<uint, int>();
        try
        {
            var towns = Services.Data.GetExcelSheet<Town>(ClientLanguage.English)
                .Select(t => (t.RowId, Name: t.Name.ExtractText().Trim()))
                .Where(t => t.RowId != 0 && t.Name.Length > 0)
                .ToList();
            foreach (var type in Services.Data.GetExcelSheet<ScenarioType>())
            {
                if (type.RowId is < 1 or > 3) continue;
                var letter = type.Type.ExtractText().Trim();
                if (letter.Length != 1) continue;
                var matches = towns.Where(t => t.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1) map[matches[0].RowId] = (int)type.RowId;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "Story Journal couldn't tell which opening each starting city begins");
        }
        return map;
    }

    private static string ChapterName(QuestRedoChapterUI ui) => JournalText.Tidy(ui.ChapterName.ExtractText());

    /// <summary>The quest is in the journal's Main Scenario section (its genre's category's section is the first, or is so named).</summary>
    private static bool IsMainScenario(Quest q)
    {
        try
        {
            if (!q.JournalGenre.IsValid) return false;
            var category = q.JournalGenre.Value.JournalCategory;
            if (!category.IsValid) return false;
            var section = category.Value.JournalSection;
            if (!section.IsValid) return false;
            return section.RowId == 0 || section.Value.Name.ExtractText().Contains("Main Scenario", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MsqQuest Read(Quest q, uint chapter)
    {
        var previous = q.PreviousQuest.Where(p => p.RowId != 0).Select(p => p.RowId).ToList();
        var locks = q.QuestLock.Where(p => p.RowId != 0).Select(p => p.RowId).ToList();
        uint duty = 0;
        string? dutyName = null, dutyType = null;
        var dutyLevel = 0;
        try
        {
            if (q.InstanceContentUnlock.RowId != 0 && q.InstanceContentUnlock.IsValid)
            {
                var cfc = q.InstanceContentUnlock.Value.ContentFinderCondition;
                if (cfc.RowId != 0 && cfc.IsValid)
                {
                    duty = cfc.RowId;
                    (dutyName, dutyType, dutyLevel) = Places.Duty(cfc.RowId);
                    if (string.IsNullOrEmpty(dutyName)) duty = 0;
                }
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, $"Story Journal: no duty for quest {q.RowId}");
        }
        var place = q.PlaceName.IsValid ? q.PlaceName.Value.Name.ExtractText() : string.Empty;
        var level = q.ClassJobLevel.Count > 0 ? q.ClassJobLevel[0] : 0;
        return new MsqQuest(q.RowId, q.Name.ExtractText(), level, (int)q.Expansion.RowId, previous, q.PreviousQuestJoin == 2 && previous.Count > 1,
            locks, chapter, duty, dutyName, dutyType, dutyLevel, place);
    }
}

/// <summary>
/// A quest's own words from its text sheet (<c>quest/006/ManFst001_00655</c>: key in column 0, text in
/// column 1), read on a worker and kept (a quest's text doesn't change while the game runs).
/// </summary>
public static class JournalReader
{
    private static readonly ConcurrentDictionary<uint, List<(string Key, string Text)>> Cache = new();

    /// <summary>The (key, text) rows of a quest's text sheet; empty when it can't be read.</summary>
    public static List<(string Key, string Text)> Rows(uint questRow)
    {
        questRow = QuestIds.Row(questRow);
        if (Cache.TryGetValue(questRow, out var cached)) return cached;
        var rows = new List<(string, string)>();
        try
        {
            if (Services.Data.GetExcelSheet<Quest>().GetRowOrDefault(questRow) is { } q && QuestIds.TextSheet(q.Id.ExtractText()) is { } name)
            {
                var sheet = Services.Data.GetExcelSheet<RawRow>(null, name);
                foreach (var row in sheet)
                {
                    var key = row.ReadStringColumn(0).ExtractText();
                    if (key.Length == 0) continue;
                    rows.Add((key, row.ReadStringColumn(1).ExtractText()));
                }
            }
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, $"Story Journal couldn't read quest {questRow}'s text");
        }
        if (Cache.Count > 4000) Cache.Clear();
        Cache[questRow] = rows;
        return rows;
    }

    /// <summary>The steps (<c>ToDoCompleteSeq</c>) of a quest's to-dos, in order.</summary>
    public static List<int> TodoSteps(uint questRow)
    {
        var list = new List<int>();
        try
        {
            if (Services.Data.GetExcelSheet<Quest>().GetRowOrDefault(QuestIds.Row(questRow)) is { } q)
                foreach (var t in q.TodoParams) list.Add(t.ToDoCompleteSeq);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, $"Story Journal couldn't read quest {questRow}'s steps");
        }
        return list;
    }
}
