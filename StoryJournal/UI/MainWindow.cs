using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>The journal window's places: the site's, down the rail; Settings at its foot.</summary>
public enum MainTab
{
    Now,
    Session,
    Codex,
    Notebook,
    Settings,
}

/// <summary>
/// The journal (<c>/sj</c>), "Ink &amp; Ribbon": a rail down the left with the site's places (Now, This evening,
/// Codex, Notebook), Settings at its foot under the story's progress, and the open place on the right. The
/// rail folds to its pictures when the window is narrow; its red bar slides to the place you pick (220ms).
/// Everything shown is read from <see cref="Journal"/> each frame; what you press goes back to it after the
/// frame's drawing is done.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private readonly Journal journal;
    private readonly Configuration config;
    private readonly Fonts fonts;
    private readonly List<Action> later = new();
    private MainTab tab;
    private bool disposed;

    public MainWindow(Journal journal)
        : base("Story Journal###StoryJournalMain")
    {
        this.journal = journal;
        config = journal.Config;
        fonts = Ui.AcquireFonts();
        Size = new Vector2(880, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 420), MaximumSize = new Vector2(1600, 2400) };
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    /// <summary>Opens the quick note (set by the plugin).</summary>
    public Action<string>? OpenNote { get; set; }

    public void ShowMain() => ShowTab(MainTab.Now);

    public void ShowSettings() => ShowTab(MainTab.Settings);

    public void ShowTab(MainTab which)
    {
        tab = which;
        IsOpen = true;
        BringToFront();
    }

    /// <summary>Opens the Notebook, on its theories or its notes.</summary>
    public void ShowNotebook(bool theories = true)
    {
        notebookTheories = theories;
        ShowTab(MainTab.Notebook);
    }

    /// <summary>Opens the Codex on an entry or a name ("More in the Codex", Who's that?'s card, /sj ask).</summary>
    public void ShowCodex(string name)
    {
        codexAsk = name;
        codexOpen = name;
        ShowTab(MainTab.Codex);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ReleaseFonts();
    }

    private bool firstSizeFitted;

    public override void PreDraw()
    {
        if (!firstSizeFitted)
        {
            firstSizeFitted = true;
            var screen = ImGui.GetMainViewport().Size;
            var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            if (screen.Y > 0 && scale > 0 && Size is { } size)
            {
                var w = MathF.Min(size.X, MathF.Max(440, screen.X * 0.9f / scale));
                var h = MathF.Min(size.Y, MathF.Max(420, screen.Y * 0.9f / scale));
                Size = new Vector2(w, h);
            }
        }
        Theme.PushFrame();
    }

    public override void PostDraw() => Theme.Pop();

    // ---------------------------------------------------------------------------------------------
    // The rail and the pane.

    private const float RailWide = 168;
    private const float RailNarrow = 56;
    private const float PanePad = 22;

    /// <summary>The rail's red bar: where it was and where it's going (it slides, 220ms settle).</summary>
    private readonly Anim barMove = new(Durations.Step, Ease.Settle);
    private float barFrom;
    private float barTo = float.NaN;

    public override void Draw()
    {
        Kit.ResetFrame();
        Frame.ResetFrame();
        Theme.PushInside();
        try
        {
            using (fonts.Push(Face.Sans, 14))
            {
                try
                {
                    var win = ImGui.GetWindowPos();
                    var size = ImGui.GetWindowSize();
                    var top = ImGui.GetCursorScreenPos().Y;
                    var bottom = win.Y + size.Y;
                    var narrow = size.X < S(600);
                    var railW = S(narrow ? RailNarrow : RailWide);
                    var dl = Kit.Dl;
                    // The rail: the ground's ink, a hairline on its right.
                    dl.AddRectFilled(new Vector2(win.X, top), new Vector2(win.X + railW, bottom), Palette.U32(Palette.InkGround), ImGui.GetStyle().WindowRounding, ImDrawFlags.RoundCornersBottomLeft);
                    dl.AddLine(new Vector2(win.X + railW - 0.5f, top), new Vector2(win.X + railW - 0.5f, bottom), Palette.U32(Palette.Line), 1f);
                    // The faint line under Dalamud's title bar.
                    dl.AddLine(new Vector2(win.X + 1, top + 0.5f), new Vector2(win.X + size.X - 1, top + 0.5f), Palette.U32(Palette.Line), 1f);
                    DrawRail(new Vector2(win.X, top), railW, bottom - top, narrow);
                    DrawPane(new Vector2(win.X + railW, top), size.X - railW, bottom - top);
                }
                finally
                {
                    Frame.Unwind();
                    Frame.DrawToast();
                }
            }
        }
        finally
        {
            Theme.Pop();
        }

        if (later.Count == 0) return;
        var actions = later.ToArray();
        later.Clear();
        foreach (var a in actions)
        {
            try
            {
                a();
            }
            catch (Exception ex)
            {
                Services.Log.Error(ex, "Story Journal: an action from the window failed");
                Frame.Toast("Something went wrong (see /xllog)");
            }
        }
    }

    /// <summary>Runs after this frame's drawing is finished.</summary>
    private void Later(Action action) => later.Add(action);

    private static readonly (MainTab Tab, string Label, Icon Icon)[] Places =
    {
        (MainTab.Now, "Now", Icon.Sun),
        (MainTab.Session, "This evening", Icon.BookText),
        (MainTab.Codex, "Codex", Icon.Library),
        (MainTab.Notebook, "Notebook", Icon.NotebookPen),
    };

    private void DrawRail(Vector2 at, float w, float h, bool narrow)
    {
        var dl = Kit.Dl;
        var pad = S(narrow ? 8 : 10);
        var itemH = S(36);
        var y = at.Y + S(14);
        var x = at.X + pad;
        var iw = w - pad * 2;
        float? barY = null;
        for (var i = 0; i < Places.Length; i++)
        {
            var (t, label, icon) = Places[i];
            var pos = new Vector2(x, y);
            var on = tab == t;
            var (clicked, hovered, _) = Kit.Hit(Ids.Of("##rail", i), pos, new Vector2(iw, itemH));
            if (on || hovered) dl.AddRectFilled(pos, pos + new Vector2(iw, itemH), Palette.U32(on ? Palette.Hover : Palette.RowHover), S(10));
            if (on) barY = y;
            var ink = on ? Palette.Ink : hovered ? Palette.Ink : Palette.Muted;
            var iconAt = narrow ? new Vector2(x + (iw - S(17)) / 2, y + (itemH - S(17)) / 2) : new Vector2(x + S(12), y + (itemH - S(16)) / 2);
            Icons.Draw(dl, icon, iconAt, S(narrow ? 17 : 16), on ? Palette.Ribbon : ink);
            if (!narrow)
            {
                Txt.Line(dl, new Vector2(x + S(38), y), label, Face.SansMedium, 13, ink, 36);
                var badge = Badge(t);
                if (badge == "•") Kit.Dot(new Vector2(x + iw - S(14), y + itemH / 2), Palette.Ribbon, 7);
                else if (badge != null)
                {
                    var bw = Txt.Measure(badge, Face.Mono, 11).X;
                    Txt.Line(dl, new Vector2(x + iw - S(12) - bw, y), badge, Face.Mono, 11, Palette.Faint, 36);
                }
            }
            else if (Badge(t) == "•") Kit.Dot(new Vector2(x + iw - S(8), y + S(9)), Palette.Ribbon, 6);
            if (hovered && narrow) Kit.Tooltip(label);
            if (clicked) Later(() => tab = t);
            y += itemH + S(4);
        }

        // The foot: the story's progress, then Settings (and the site).
        var footTop = at.Y + h - S(narrow ? 60 : 132);
        dl.AddLine(new Vector2(x, footTop), new Vector2(x + iw, footTop), Palette.U32(Palette.Line), 1f);
        var fy = footTop + S(14);
        if (!narrow && journal.Story.Now?.Progress.ExpansionNow is { } ex)
        {
            Txt.Line(dl, new Vector2(x + S(2), fy), Txt.Fit(ex.Name, Face.Display, 13, iw), Face.Display, 13, Palette.Ink, 18);
            fy += S(20);
            var chapter = ChapterNumber() is { } n ? $"Chapter {Roman.Of(n)} · " : string.Empty;
            Txt.Line(dl, new Vector2(x + S(2), fy), Txt.Fit($"{chapter}{Progress.Percent(ex.Done, ex.Total)}%", Face.Sans, 11, iw), Face.Sans, 11, Palette.Muted, 15);
            fy += S(19);
            Kit.Progress(new Vector2(x + S(2), fy), iw - S(4), ex.Total > 0 ? (float)ex.Done / ex.Total : 0, 4);
            fy += S(14);
        }
        else if (!narrow) fy += S(53);
        var bh = S(32);
        if (narrow)
        {
            var gp = new Vector2(x + (iw - bh) / 2, at.Y + h - S(14) - bh);
            if (tab == MainTab.Settings) dl.AddRectFilled(gp - new Vector2(S(4)), gp + new Vector2(bh + S(4)), Palette.U32(Palette.Hover), S(10));
            if (Kit.IconButton("##rail-gear", gp, Icon.Gear, "Settings", 32, Btn.Quiet)) Later(() => tab = MainTab.Settings);
            if (tab == MainTab.Settings) barY = gp.Y - S(2);
        }
        else
        {
            var siteW = bh;
            var setW = iw - siteW - S(6);
            if (Kit.Button("##rail-settings", new Vector2(x, fy), "Settings", tab == MainTab.Settings ? Btn.Picked : Btn.Glass, BtnSize.Sm, Icon.Gear, setW, false, null, bh))
                Later(() => tab = MainTab.Settings);
            if (Kit.IconButton("##rail-site", new Vector2(x + setW + S(6), fy), Icon.ArrowRight, "Your journal on kokos-tools.com", 32))
                Dalamud.Utility.Util.OpenLink(SiteLink.SiteUrl);
        }

        // The red bar on the left of the open place, sliding when it moves.
        if (barY is { } to)
        {
            if (float.IsNaN(barTo))
            {
                barFrom = barTo = to;
            }
            else if (MathF.Abs(to - barTo) > 0.5f)
            {
                barFrom = barMove.Started ? barMove.Between(barFrom, barTo, Ink.Now, Ink.Still) : barTo;
                barTo = to;
                barMove.Start(Ink.Now);
            }
            var by = barMove.Started ? barMove.Between(barFrom, barTo, Ink.Now, Ink.Still) : barTo;
            dl.AddRectFilled(new Vector2(x - S(4), by + S(8)), new Vector2(x - S(1), by + itemH - S(8)), Palette.U32(Palette.Ribbon), S(2));
        }
    }

    /// <summary>A place's mark on the rail: the evening's live dot, the Codex's count, the Notebook's theories due.</summary>
    private string? Badge(MainTab t) => t switch
    {
        MainTab.Session => journal.Session != null ? "•" : null,
        MainTab.Codex => CodexRows(journal.Story.Now, journal.Memory) is { Count: > 0 } && codexCount > 0 ? Ui.Count(codexCount) : null,
        MainTab.Notebook => NotebookCount() is > 0 and var n ? Ui.Count(n) : null,
        _ => null,
    };

    /// <summary>The number of the chapter you're in, within its expansion (1, 2, …), when the reading has chapters.</summary>
    private int? ChapterNumber()
    {
        if (journal.Story.Now?.Progress is not { Current: { Chapter: { } name } cur } p) return null;
        var i = 0;
        foreach (var c in p.Chapters)
        {
            if (c.Expansion != cur.Expansion) continue;
            i++;
            if (c.Name == name) return i;
        }
        return null;
    }

    private void DrawPane(Vector2 at, float w, float h)
    {
        var quickBar = tab == MainTab.Now && journal.Player != null && journal.Story.Now is { Current: not null };
        var barH = quickBar ? S(68) : 0;
        ImGui.SetCursorScreenPos(at);
        ImGui.BeginChild("##sj-pane", new Vector2(w, MathF.Max(S(40), h - barH)), false, ImGuiWindowFlags.None);
        Frame.Open.Push(false);
        var start = ImGui.GetCursorScreenPos();
        Kit.PushBox(start.X + S(PanePad), ImGui.GetContentRegionAvail().X - S(PanePad) * 2);
        Kit.Reserve(start, 1, S(18));
        switch (tab)
        {
            case MainTab.Session:
                DrawSession();
                break;
            case MainTab.Codex:
                DrawCodex();
                break;
            case MainTab.Notebook:
                DrawNotebook();
                break;
            case MainTab.Settings:
                DrawSettings();
                break;
            default:
                DrawNow();
                break;
        }
        Kit.Gap(24);
        Kit.PopBox();
        if (Frame.Open.Count > 0) Frame.Open.Pop();
        ImGui.EndChild();
        if (quickBar)
        {
            var barTop = at.Y + h - barH;
            Kit.PushBox(at.X + S(PanePad), w - S(PanePad) * 2);
            ImGui.SetCursorScreenPos(new Vector2(at.X, barTop + S(8)));
            DrawQuickBar();
            Kit.PopBox();
        }
    }

    /// <summary>A box with an icon well, a title and words, for an empty place or a state to say.</summary>
    private static void Empty(string id, Icon icon, string title, string words)
    {
        Kit.BeginPanel(id, 18, 20, 18, Palette.Leaf, Palette.Line, Palette.RadiusXl);
        var at = Kit.At;
        var w = Kit.Width;
        Kit.IconWell(at, S(36), icon, quiet: true);
        var x = at.X + S(36 + 14);
        Txt.Line(Kit.Dl, new Vector2(x, at.Y), Txt.Fit(title, Face.Display, 17, w - (x - at.X)), Face.Display, 17, Palette.Ink, 22);
        var hh = S(24) + Lines.Draw(Kit.Dl, new Vector2(x, at.Y + S(24)), w - (x - at.X), words, Face.Sans, 13, Palette.Muted, 19) + S(2);
        Kit.Reserve(at, w, MathF.Max(S(36), hh));
        Kit.EndPanel();
    }

    /// <summary>A place's kicker over its title (the open place's head).</summary>
    private static void PlaceHead(string kicker, string title, Vector4? kickerColor = null)
    {
        Ink.KickerRow(kicker, kickerColor);
        Kit.Gap(4);
        var at = Kit.At;
        Txt.Line(Kit.Dl, at, Txt.Fit(title, Face.Display, 22, Kit.Width), Face.Display, 22, Palette.Ink, 30);
        Kit.Reserve(at, Kit.Width, S(30));
    }
}
