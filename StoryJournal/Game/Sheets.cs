using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
/// <item>Their order's inputs: <c>PreviousQuest</c> (and <c>PreviousQuestJoin</c> 2 read as "any one
/// will do"), <c>QuestLock</c>, the expansion, the level.</item>
/// <item>Chapters: the scenario tree's <c>QuestChapter</c> (or the quest's own <c>QuestChapter</c> row),
/// its <c>Redo</c> row's <c>QuestRedoChapterUI.ChapterName</c>; when that names too few, a chapter starts
/// at each <c>QuestRedoChapterUI.Quest</c> and runs on to the next.</item>
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
        foreach (var tree in Services.Data.GetExcelSheet<ScenarioTree>())
        {
            if (questSheet.GetRowOrDefault(tree.RowId) is not { } q || q.Name.IsEmpty) continue;
            rows[q.RowId] = (q, tree.QuestChapter.RowId);
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
                source = "the journal's Main Scenario section";
            }
        }

        var chapterSheet = Services.Data.GetExcelSheet<QuestChapter>();
        var redoUi = Services.Data.GetExcelSheet<QuestRedoChapterUI>();
        var chapters = new Dictionary<uint, MsqChapter>();
        var quests = new List<MsqQuest>();
        var named = 0;
        foreach (var (q, chapterRow) in rows.Values)
        {
            uint chapter = 0;
            try
            {
                var redo = 0u;
                if (chapterRow != 0 && chapterSheet.GetRowOrDefault(chapterRow) is { } c1) redo = c1.Redo.RowId;
                if (redo == 0 && chapterSheet.GetRowOrDefault(q.RowId) is { } c2) redo = c2.Redo.RowId;
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
            quests.Add(Read(q, chapter));
        }

        var expansions = new List<MsqExpansion>();
        foreach (var ex in Services.Data.GetExcelSheet<ExVersion>())
            if (!ex.Name.IsEmpty) expansions.Add(new MsqExpansion((int)ex.RowId, ex.Name.ExtractText()));

        var catalog = new MsqCatalog(quests, chapters.Values, expansions);
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
                catalog = new MsqCatalog(rebuilt, chapters.Values, expansions);
                source += ", chapters from where each begins";
            }
        }
        Services.Log.Information($"Story Journal read the main story from {source}: {catalog.Count} quests, {chapters.Count} chapters, {expansions.Count} expansions");
        return catalog;
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
