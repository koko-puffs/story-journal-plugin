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
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
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

    private const float RailWide = 172;
    private const float RailNarrow = 52;
    private const float PanePad = 18;

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
                    DrawTitle(win, size.X, top);
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

    /// <summary>
    /// The title bar, as the mock-up's: the small ribbon before "Story Journal" (Geist 600, 13px), and on the right who's
    /// playing (on Now) or the open place, in muted words. Drawn over Dalamud's own title (its buttons on the right are
    /// left alone).
    /// </summary>
    private void DrawTitle(Vector2 win, float width, float top)
    {
        var tb = top - win.Y;
        if (tb < S(12)) return;
        var dl = Kit.Dl;
        dl.PushClipRect(win, new Vector2(win.X + width, top), false);
        // Dalamud's buttons and the close sit at the right, each about the bar's height wide.
        var right = win.X + width - tb * 3.2f;
        dl.AddRectFilled(win + new Vector2(1, 1), new Vector2(right, top - 1), Palette.U32(Palette.Footer), ImGui.GetStyle().WindowRounding - 1, ImDrawFlags.RoundCornersTopLeft);
        Ink.Ribbon(dl, new Vector2(win.X + S(12), win.Y + (tb - S(14)) / 2), S(8), S(14));
        var x = win.X + S(26);
        x += Txt.Line(dl, new Vector2(x, win.Y), "Story Journal", Face.SansSemibold, 13, Palette.Ink, tb / S(1));
        var small = tab switch
        {
            MainTab.Now => journal.Player is { } p ? (p.World.Length > 0 ? $"{p.Name} · {p.World}" : p.Name) : null,
            MainTab.Session => "This evening",
            MainTab.Codex => "Codex",
            MainTab.Notebook => "Notebook",
            _ => "Settings",
        };
        if (small is { Length: > 0 })
        {
            var room = right - S(10) - (x + S(16));
            if (room > S(30))
            {
                var shown = Txt.Fit(small, Face.SansMedium, 11, room);
                var sw = Txt.Measure(shown, Face.SansMedium, 11).X;
                Txt.Line(dl, new Vector2(right - S(10) - sw, win.Y), shown, Face.SansMedium, 11, Palette.Muted, tb / S(1));
            }
        }
        dl.PopClipRect();
    }

    private void DrawRail(Vector2 at, float w, float h, bool narrow)
    {
        var dl = Kit.Dl;
        var pad = S(narrow ? 8 : 10);
        var itemH = S(34);
        var y = at.Y + S(12);
        var x = at.X + pad;
        var iw = w - pad * 2;
        float? barY = null;
        for (var i = 0; i < Places.Length; i++)
        {
            var (t, label, icon) = Places[i];
            var pos = new Vector2(x, y);
            var on = tab == t;
            var (clicked, hovered, _) = Kit.Hit(Ids.Of("##rail", i), pos, new Vector2(iw, itemH));
            // The open place: raised, its picture in the ribbon's red, the red bar inside its left edge (drawn below, sliding).
            if (on || hovered) dl.AddRectFilled(pos, pos + new Vector2(iw, itemH), Palette.U32(on ? Palette.LeafRaised : Palette.RowHover), S(8));
            if (on) barY = y;
            var ink = on || hovered ? Palette.Ink : Palette.Muted;
            var iconAt = narrow ? new Vector2(x + (iw - S(15)) / 2, y + (itemH - S(15)) / 2) : new Vector2(x + S(10), y + (itemH - S(15)) / 2);
            Icons.Draw(dl, icon, iconAt, S(15), on ? Palette.Ribbon : ink);
            if (!narrow)
            {
                Txt.Line(dl, new Vector2(x + S(35), y), label, Face.SansSemibold, 13, ink, 34);
                var badge = Badge(t);
                if (badge == "•")
                {
                    // The evening under way: a live dot with the ribbon's soft ring.
                    var c = new Vector2(x + iw - S(13), y + itemH / 2);
                    dl.AddCircleFilled(c, S(3.5f + 3), Palette.U32(Palette.RibbonSoft), 16);
                    Kit.Dot(c, Palette.Ribbon, 7);
                }
                else if (badge != null)
                {
                    var bw = Txt.Measure(badge, Face.SansMedium, 12).X;
                    Txt.Line(dl, new Vector2(x + iw - S(10) - bw, y), badge, Face.SansMedium, 12, Palette.Faint, 34);
                }
            }
            else if (Badge(t) == "•") Kit.Dot(new Vector2(x + iw - S(7), y + S(8)), Palette.Ribbon, 6);
            // The evening's live dot says how long it's been under way.
            var underWay = t == MainTab.Session && journal.Session is { } live ? $"Under way: {Ui.Duration(DateTime.UtcNow - live.StartedAt)}" : null;
            if (hovered && narrow) Kit.Tooltip(underWay != null ? $"{label} · {underWay.ToLowerInvariant()}" : label);
            else if (hovered && underWay != null) Kit.Tooltip(underWay);
            if (clicked) Later(() => tab = t);
            y += itemH + S(2);
        }

        // The foot: the story's progress, then Settings (and the site).
        var footTop = at.Y + h - S(narrow ? 56 : 126);
        dl.AddLine(new Vector2(x, footTop + 0.5f), new Vector2(x + iw, footTop + 0.5f), Palette.U32(Palette.Line), 1f);
        var fy = footTop + S(12);
        var fx = x + S(4);
        var fw = iw - S(8);
        if (!narrow && journal.Story.Now?.Progress.ExpansionNow is { } ex)
        {
            Txt.Line(dl, new Vector2(fx, fy), Txt.Fit(ex.Name, Face.Display, 13, fw), Face.Display, 13, Palette.Ink, 16);
            fy += S(22);
            // "Chapter iv · 60%": the numeral in Fraunces, as the site's small capitals.
            var cx = fx;
            if (ChapterNumber() is { } n)
            {
                cx += Txt.Line(dl, new Vector2(cx, fy), "Chapter ", Face.Sans, 11, Palette.Muted, 14);
                cx += Ink.Roman(dl, new Vector2(cx, fy), Roman.Of(n), 13, Palette.Muted, 14);
                cx += Txt.Line(dl, new Vector2(cx, fy), " · ", Face.Sans, 11, Palette.Muted, 14);
            }
            Txt.Line(dl, new Vector2(cx, fy), $"{Progress.Percent(ex.Done, ex.Total)}%", Face.Sans, 11, Palette.Muted, 14);
            fy += S(20);
            Kit.Progress(new Vector2(fx, fy), fw, ex.Total > 0 ? (float)ex.Done / ex.Total : 0, 4);
            fy += S(14);
        }
        else if (!narrow) fy += S(56);
        var bh = S(28);
        if (narrow)
        {
            var gp = new Vector2(x + (iw - S(36)) / 2, at.Y + h - S(12) - S(30));
            var on = tab == MainTab.Settings;
            var (clicked, hovered, _) = Kit.Hit("##rail-gear", gp, new Vector2(S(36), S(30)));
            if (on || hovered) dl.AddRectFilled(gp, gp + new Vector2(S(36), S(30)), Palette.U32(on ? Palette.LeafRaised : Palette.RowHover), S(8));
            Icons.Draw(dl, Icon.Gear, gp + new Vector2((S(36) - S(15)) / 2, (S(30) - S(15)) / 2), S(15), on ? Palette.Ribbon : hovered ? Palette.Ink : Palette.Muted);
            if (hovered) Kit.Tooltip("Settings");
            if (clicked) Later(() => tab = MainTab.Settings);
            if (on) barY = gp.Y - (itemH - S(30)) / 2;
        }
        else
        {
            var siteW = bh;
            var setW = iw - siteW - S(6);
            if (Kit.Button("##rail-settings", new Vector2(x, fy), "Settings", tab == MainTab.Settings ? Btn.Picked : Btn.Glass, BtnSize.Sm, Icon.Gear, setW, false, null, bh))
                Later(() => tab = MainTab.Settings);
            if (Kit.IconButton("##rail-site", new Vector2(x + setW + S(6), fy), Icon.ArrowRight, "Your journal on kokos-tools.com", 28))
                Dalamud.Utility.Util.OpenLink(SiteLink.SiteUrl);
        }

        // The red bar inside the open place's left edge (the site's 3px inset), sliding when it moves (220ms, settle).
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
            var bx = narrow ? x + (iw - S(36)) / 2 : x;
            var bt = narrow && tab == MainTab.Settings ? by + (itemH - S(30)) / 2 : by;
            var bb = narrow && tab == MainTab.Settings ? bt + S(30) : by + itemH;
            var bw = narrow ? S(36) : iw;
            if (MathF.Abs(by - to) < 0.5f)
            {
                // At rest: the inset follows the place's rounded corners, as the site's box-shadow does (a "[").
                dl.PushClipRect(new Vector2(bx, bt), new Vector2(bx + S(8), bb), true);
                dl.AddRectFilled(new Vector2(bx, bt), new Vector2(bx + bw, bb), Palette.U32(Palette.Ribbon), S(8));
                dl.AddRectFilled(new Vector2(bx + S(3), bt), new Vector2(bx + bw + S(3), bb), Palette.U32(Palette.LeafRaised), S(8));
                dl.PopClipRect();
            }
            else dl.AddRectFilled(new Vector2(bx, bt), new Vector2(bx + S(3), bb), Palette.U32(Palette.Ribbon), S(1.5f));
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
        Kit.Reserve(start, 1, S(16));
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
        Kit.BeginPanel(id, 16, 18, 16, Palette.Leaf, Palette.Line, Palette.RadiusMd);
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
