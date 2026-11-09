using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using StoryJournal.Core;
using StoryJournal.Game;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// The tracker is a ribbon: it hangs from the top of the screen like a bookmark, with how far through the
/// expansion, the next step and where, the next duty (inked out on strict spoilers), and a partner's pace lock.
/// It unrolls with its weight (650ms) when it first shows, when it's unfolded, and when the site's "Flag it in
/// game" drops it; its chevron folds it to a short tab with the percentage. Drag its head along the top edge to
/// move it. Its title opens the journal. Not in cutscenes or on loading screens. Off in Settings.
/// </summary>
public sealed class TrackerWindow : Window, IDisposable
{
    private const float Width = 290;
    private const float TabWidth = 46;
    private const float Notch = 18;

    private readonly Journal journal;
    private readonly Fonts fonts;
    private readonly Anim unroll = new(Durations.Ink, Ease.Weight);

    /// <summary>A quest done: the ribbon rolls up (160ms, leaving) before it drops again with the new step.</summary>
    private readonly Anim rollUp = new(0.16f, Ease.Leave);

    private uint? questShown;

    /// <summary>The progress line filling a little when a quest's done (650ms, the ink curve): from where it was.</summary>
    private readonly Anim fill = new(Durations.Ink, Ease.Ink);

    private float fillFrom = float.NaN;
    private float fillTo = float.NaN;
    private readonly Memo<(int Done, int Total, int Ex)> head = new();
    private DateTime? droppedSeen;
    private bool wasFolded;
    private bool dragging;
    private float lastHeight = 140;
    private bool disposed;
    private int pushed;

    public TrackerWindow(Journal journal)
        : base("Story Journal · Tracker###SjTracker", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar
                                                      | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
                                                      | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove)
    {
        this.journal = journal;
        fonts = Ui.AcquireFonts();
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        DisableWindowSounds = true;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    public Action? Open { get; set; }

    private bool Folded => journal.Memory?.TrackerFolded ?? false;

    public override void PreOpenCheck()
    {
        var want = journal.Config.ShowTracker && journal.Player != null && journal.Story.Now is { Progress.Expansions.Count: > 0 }
                   && !InGame.InCutscene && !InGame.Loading;
        if (want && !IsOpen) unroll.Start(Ink.Now, 0.1f);
        // The site's "Flag it in game" drops it again.
        if (journal.TrackerDropped is { } dropped && dropped != droppedSeen)
        {
            droppedSeen = dropped;
            unroll.Start(Ink.Now);
        }
        if (wasFolded && !Folded) unroll.Start(Ink.Now);
        wasFolded = Folded;
        // A new step's quest (one finished, the next taken): up, then down again with it.
        var quest = journal.Story.Now?.Current?.Id;
        if (want && IsOpen && quest != null && questShown != null && quest != questShown && !Ink.Still)
        {
            rollUp.Start(Ink.Now);
            unroll.Start(Ink.Now, 0.16f);
        }
        if (quest != null) questShown = quest;
        IsOpen = want;
    }

    public override void PreDraw()
    {
        var vp = ImGuiHelpers.MainViewport;
        var w = S(Folded ? TabWidth : Width);
        var x = vp.Pos.X + vp.Size.X * Math.Clamp(journal.Config.TrackerAt, 0.02f, 0.98f) - w / 2;
        Position = new Vector2(Math.Clamp(x, vp.Pos.X, vp.Pos.X + vp.Size.X - w), vp.Pos.Y);
        PositionCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        pushed = 2;
    }

    public override void PostDraw()
    {
        if (pushed > 0) ImGui.PopStyleVar(pushed);
        pushed = 0;
    }

    public override void Draw()
    {
        if (journal.Story.Now is not { } story || story.Progress.ExpansionNow is not { } ex) return;
        Kit.ResetFrame();
        Theme.PushInside();
        try
        {
            using (fonts.Push(Face.Sans, 13))
            {
                if (Folded) DrawTab(ex);
                else DrawRibbon(story, ex);
            }
        }
        finally
        {
            Theme.Pop();
        }
    }

    /// <summary>Folded: a short tab with the chevron and the percentage.</summary>
    private void DrawTab(ExpansionCount ex)
    {
        var at = ImGui.GetCursorScreenPos();
        var w = S(TabWidth);
        var h = S(60);
        var dl = ImGui.GetWindowDrawList();
        var v = Unrolled();
        Ink.Ribbon(dl, at, w, h, v, Palette.RibbonDeep, S(12) / h);
        var (clicked, hovered, _) = Kit.Hit("##tracker-unfold", at, new Vector2(w, h - S(12)));
        Icons.Draw(dl, Icon.ChevronDown, at + new Vector2((w - S(13)) / 2, S(9)), S(13), Palette.Rgba(255, 255, 255, hovered ? 1f : 0.8f));
        var pct = $"{Progress.Percent(ex.Done, ex.Total)}%";
        var pw = Txt.Measure(pct, Face.SansSemibold, 11).X;
        Txt.Line(dl, at + new Vector2((w - pw) / 2, S(26)), pct, Face.SansSemibold, 11, Palette.White, 12);
        if (hovered) Kit.Tooltip("Unfold the tracker");
        if (clicked && journal.Memory is { } m)
        {
            m.TrackerFolded = false;
            journal.MemoryChanged();
        }
        ImGui.Dummy(new Vector2(w, h));
    }

    /// <summary>How far down the ribbon is now (0..1, a little past 1 as it drops): rolling up, else unrolling.</summary>
    private float Unrolled()
    {
        var now = Ink.Now;
        if (rollUp.Started && !rollUp.Done(now, Ink.Still)) return 1 - rollUp.Value(now, Ink.Still);
        return unroll.Value(now, Ink.Still);
    }

    private void DrawRibbon(StoryState story, ExpansionCount ex)
    {
        var at = ImGui.GetCursorScreenPos();
        var w = S(Width);
        var dl = ImGui.GetWindowDrawList();
        var v = Unrolled();
        var full = S(lastHeight);
        Ink.Ribbon(dl, at, w, full, v, Palette.RibbonDeep, S(Notch) / full);
        var shown = MathF.Max(0, full * MathF.Min(v, 1) - S(Notch));
        // What's on it wipes in with the ribbon (a clip, never a fade: a word is covered or whole).
        dl.PushClipRect(at, at + new Vector2(w, MathF.Max(1, shown)), true);
        var pad = S(16);
        var x = at.X + pad;
        var iw = w - pad * 2;
        var y = at.Y + S(12);
        var white = Palette.White;
        var soft = Palette.Rgba(255, 228, 232, 0.85f);

        // The head: the volume and how far, the fold. Dragged, it moves along the top.
        var title = head.Get((ex.Done, ex.Total, ex.Id), _ => $"{ex.Name} · {Progress.Percent(ex.Done, ex.Total)}%");
        var (_, headHover, held) = Kit.Hit("##tracker-drag", new Vector2(x, y), new Vector2(iw - S(24), S(16)));
        Ink.Kicker(dl, new Vector2(x, y + S(2)), Txt.Fit(title, Face.SansSemibold, 10, iw - S(30)), soft, 10);
        if (headHover) Kit.Tooltip("Drag to move it along the top · press the step to open the journal");
        if (held && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
        {
            dragging = true;
            var vp = ImGuiHelpers.MainViewport;
            journal.Config.TrackerAt = Math.Clamp((ImGui.GetMousePos().X - vp.Pos.X) / vp.Size.X, 0.05f, 0.95f);
        }
        else if (dragging && !held)
        {
            dragging = false;
            journal.Config.Save();
        }
        var (fold, foldHover, _) = Kit.Hit("##tracker-fold", new Vector2(x + iw - S(18), y), new Vector2(S(18), S(18)));
        Icons.Draw(dl, Icon.ChevronDown, new Vector2(x + iw - S(16), y + S(1)), S(14), Palette.Rgba(255, 255, 255, foldHover ? 1f : 0.75f));
        if (foldHover) Kit.Tooltip("Fold it to a tab");
        if (fold && journal.Memory is { } mem)
        {
            mem.TrackerFolded = true;
            journal.MemoryChanged();
        }
        y += S(20);
        // The progress line: 3px, a shadow of a track, white to where you are.
        var track = new Vector2(x, y);
        dl.AddRectFilled(track, track + new Vector2(iw, S(3)), Palette.U32(Palette.Rgba(0, 0, 0, 0.3f)), S(1.5f));
        var target = ex.Total > 0 ? (float)ex.Done / ex.Total : 0;
        if (float.IsNaN(fillTo)) fillFrom = fillTo = target;
        else if (MathF.Abs(target - fillTo) > 0.0001f)
        {
            fillFrom = fill.Started ? fill.Between(fillFrom, fillTo, Ink.Now, Ink.Still) : fillTo;
            fillTo = target;
            fill.Start(Ink.Now, 0.16f);
        }
        var frac = fill.Started ? fill.Between(fillFrom, fillTo, Ink.Now, Ink.Still) : fillTo;
        if (frac > 0) dl.AddRectFilled(track, track + new Vector2(MathF.Max(S(3), iw * frac), S(3)), Palette.U32(white), S(1.5f));
        y += S(13);

        // The step and where.
        string step;
        string? where = null;
        if (story.Progress.Complete) step = "The main story is done. Well played.";
        else if (story.Current is not { } q) step = "Reading the main story…";
        else
        {
            step = !story.CurrentTaken ? $"Take {q.Name}" : story.Objectives.Count > 0 ? story.Objectives[0] : q.Name;
            var place = q.Place;
            if (story.Progress.Current?.Spot is { } spot) place = place.Length > 0 ? $"{place} · {spot}" : spot.ToString();
            where = place.Length > 0 ? place : null;
        }
        var (openClicked, openHover, _) = Kit.Hit("##tracker-open", new Vector2(x, y), new Vector2(iw, S(24)));
        foreach (var line in Lines.Of(step, iw, Face.Display, 17))
        {
            Txt.Line(dl, new Vector2(x, y), line, Face.Display, 17, openHover ? white : Palette.Rgba(255, 255, 255, 0.96f), 23);
            y += S(23);
        }
        if (openClicked && Open != null) Open();
        if (where != null)
        {
            Txt.Line(dl, new Vector2(x, y), Txt.Fit(where, Face.Sans, 12, iw), Face.Sans, 12, soft, 17);
            y += S(17);
        }

        // Then: the next duty, as far as the spoilers allow (strict: what kind, inked; gentle and off: its name).
        var ahead = story.Progress.Ahead;
        if (ahead.Count > 0)
        {
            y += S(10);
            dl.AddLine(new Vector2(x, y), new Vector2(x + iw, y), Palette.U32(Palette.Rgba(255, 255, 255, 0.2f)), 1f);
            y += S(10);
            var a = ahead[0];
            var when = a.QuestsAway <= 1 ? "after this quest" : $"~{a.QuestsAway} quests";
            var tx = x + Txt.Line(dl, new Vector2(x, y), "Then ", Face.Sans, 12, soft, 20);
            var label = journal.Prefs.Spoilers == Spoilers.Strict ? $"a {DutyKind(a.Type)} · {when}" : $"{a.Duty} · {when}";
            var lw = Txt.Measure(label, Face.SansMedium, 11).X + S(14);
            var box = new Vector2(tx, y);
            if (journal.Prefs.Spoilers == Spoilers.Strict)
            {
                // Inked out, on the ribbon: a darker hatched bar with a light dashed edge.
                var end = box + new Vector2(lw, S(20));
                dl.AddRectFilled(box, end, Palette.U32(Palette.Rgba(0, 0, 0, 0.25f)), S(4));
                foreach (var (h0, h1) in Shapes.Hatch(box, end, S(8))) dl.AddLine(h0, h1, Palette.U32(Palette.Rgba(255, 255, 255, 0.05f)), S(3));
                Kit.Dashed(box, end, S(4), Palette.Rgba(255, 255, 255, 0.3f), 1f);
            }
            Txt.Line(dl, box + new Vector2(S(7), 0), label, Face.SansMedium, 11, white, 20);
            y += S(22);
        }

        // The pace lock.
        if (journal.Config.TrackerPartners && journal.Sync.Hello is { Partners.Count: > 0 } hello)
        {
            // A partner's pace lock, in a warm gold on the red.
            var gold = Palette.Hex(0xffe2b0);
            foreach (var warning in PaceLock.Check(hello.Partners, story.Progress.Index, story.NextIndex))
            {
                y += S(4);
                Icons.Draw(dl, Icon.Hourglass, new Vector2(x, y + S(2)), S(12), gold);
                y += Lines.Draw(dl, new Vector2(x + S(18), y), iw - S(18), PaceLock.Words(warning, story.Progress.Index), Face.Sans, 11, gold, 16);
            }
        }
        if (journal.Config.Paused)
        {
            y += S(4);
            Txt.Line(dl, new Vector2(x, y), "Recording paused · /sj resume", Face.Sans, 11, soft, 16);
            y += S(16);
        }
        dl.PopClipRect();
        y += S(14) + S(Notch);
        lastHeight = (y - at.Y) / S(1);
        ImGui.Dummy(new Vector2(w, MathF.Max(S(40), full * MathF.Min(1, v))));
    }

    private static string DutyKind(string type)
    {
        var t = type.ToLowerInvariant();
        if (t.Contains("dungeon")) return "dungeon";
        if (t.Contains("trial")) return "trial";
        if (t.Contains("raid")) return "raid";
        return "duty";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ReleaseFonts();
    }
}
