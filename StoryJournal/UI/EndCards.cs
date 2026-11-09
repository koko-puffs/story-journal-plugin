using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// The end of the evening (at logout, or <c>/sj end</c>): close tonight's page. How far the evening moved you
/// (the darker part of the ribbon is where you started), its numbers, a title to keep or type over (in your own
/// candle gold), how it felt in one press, and a line for next time (it comes back on Welcome back). Close the
/// page sends them with the evening; Skip doesn't.
/// </summary>
public sealed class WrapUpWindow : CardWindow
{
    private Guid shownFor;
    private string title = string.Empty;
    private string note = string.Empty;
    private readonly HashSet<int> moods = new();
    private WrapUpCard? card;

    public WrapUpWindow(Journal journal)
        : base("Story Journal · Tonight's page###SjWrapUp", journal)
    {
    }

    protected override float CardWidth => 400;

    protected override float Pad => 20;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.12f), true);

    public override void PreOpenCheck()
    {
        if (journal.WrapUp is { } c) card = c;
        Want(journal.WrapUp != null);
        if (!IsOpen) card = null;
    }

    protected override void DrawCard()
    {
        if (card is not { } c) return;
        if (c.Session.Id != shownFor)
        {
            shownFor = c.Session.Id;
            title = c.Title ?? string.Empty;
            note = string.Empty;
            moods.Clear();
        }
        var top = Kit.At;
        var w = Kit.Width;
        var day = (c.Session.EndedAt ?? c.Session.LastAt).ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture);
        Ink.Kicker(Kit.Dl, top, $"The end of the evening · {day}");
        var win = ImGui.GetWindowPos();
        if (Kit.IconButton("##wrap-x", new Vector2(win.X + ImGui.GetWindowWidth() - S(12) - S(22), win.Y + S(9)), Icon.X, "Close", 22, Btn.Quiet)) Later(() => journal.WrapUp = null);
        Kit.Reserve(top, w, S(18));
        Kit.Gap(4);
        var tt = Kit.At;
        Txt.Line(Kit.Dl, tt, "Close tonight's page", Face.Display, 22, Palette.Ink, 30);
        Kit.Reserve(tt, w, S(30));

        // How far it moved you: the darker part is where you started.
        if (c.ExpansionName.Length > 0)
        {
            Kit.Gap(10);
            var b = Kit.At;
            // The folios in Fraunces (then muted, now in ink); the track filled to now, the darker part where you started.
            var before = $"{c.PercentBefore}%";
            var now = $"{c.PercentNow}%";
            var bw = Txt.Measure(before, Face.Display, 13).X + S(10);
            var nw = Txt.Measure(now, Face.Display, 13).X + S(10);
            Txt.Line(Kit.Dl, b, before, Face.Display, 13, Palette.Muted, 18);
            Txt.Line(Kit.Dl, new Vector2(b.X + w - nw + S(10), b.Y), now, Face.Display, 13, Palette.Ink, 18);
            var tx = b.X + bw;
            var tw = w - bw - nw;
            var ty = b.Y + S(6.5f);
            Kit.Progress(new Vector2(tx, ty), tw, c.PercentNow / 100f, 5);
            var p0 = tw * Math.Clamp(c.PercentBefore / 100f, 0, 1);
            if (p0 > 0) Kit.Dl.AddRectFilled(new Vector2(tx, ty), new Vector2(tx + MathF.Max(p0, S(5)), ty + S(5)), Palette.U32(Palette.RibbonDark), S(2.5f));
            Kit.Reserve(b, w, S(18));
        }
        Kit.Gap(12);
        var length = (c.Session.EndedAt ?? c.Session.LastAt) - c.Session.StartedAt;
        var third = c.Numbers.People > 0 ? (Ui.Count(c.Numbers.People), c.Numbers.People == 1 ? "new person" : "new people")
            : (Ui.Count(c.Numbers.Cutscenes), c.Numbers.Cutscenes == 1 ? "cutscene" : "cutscenes");
        StatTiles(new (string, string)[] { (Ui.Duration(length), "played"), (Ui.Count(c.Numbers.Quests), c.Numbers.Quests == 1 ? "quest" : "quests"), third });

        Kit.Gap(14);
        Label(c.Title != null ? "Title it (we suggested one; type over it)" : "Title it (the Chronicle and the Storybook use it)");
        var f = Kit.At;
        var tagW = c.Title != null && title == c.Title ? Txt.Measure("suggested", Face.SansMedium, 10).X + S(12) : 0;
        // Your title in Literata's italic and candle gold, on the quill's edge; "suggested" until you type over it.
        Kit.Field("##wrap-title", f, w, ref title, "Give tonight a name", null, 80, ImGuiInputTextFlags.None, Face.ReadItalic, 15, Palette.QuillInk, false, 34, Palette.QuillLine);
        if (tagW > 0) Txt.Line(Kit.Dl, new Vector2(f.X + w - tagW, f.Y), "suggested", Face.SansMedium, 10, Palette.Faint, 34);
        Kit.Reserve(f, w, S(34));
        Kit.Gap(10);
        Label("How did it feel?");
        Feel();
        Kit.Gap(10);
        Label("A thought for next time (it's on your Welcome back)");
        var n = Kit.At;
        Kit.Field("##wrap-note", n, w, ref note, "Ask him about the camp before going up…", null, 2000, ImGuiInputTextFlags.None, Face.ReadItalic, 14, Palette.QuillInk, false, 34, Palette.QuillLine);
        Kit.Reserve(n, w, S(34));
        Kit.Gap(16);
        var pressed = Parts.Buttons("##wrap-b", new (string, Btn, Icon?)[] { ("Close the page", Btn.Primary, null), ("→Skip", Btn.Quiet, null) });
        if (pressed == 0)
        {
            var t = title;
            var nn = note;
            var m = moods.OrderBy(i => i).Select(i => Sessions.MoodChoices[i]).ToList();
            Later(() => journal.SaveWrapUp(c, t, m, nn));
        }
        else if (pressed == 1) Later(() => journal.WrapUp = null);
    }

    /// <summary>A field's label: Geist 600, 11px, muted, 6px over it.</summary>
    private static void Label(string words)
    {
        var at = Kit.At;
        Txt.Line(Kit.Dl, at, Txt.Fit(words, Face.SansSemibold, 11, Kit.Width), Face.SansSemibold, 11, Palette.Muted, 14);
        Kit.Reserve(at, Kit.Width, S(14));
        Kit.Gap(6);
    }

    /// <summary>
    /// How it felt, in one press each: outlined pills (28 tall, Geist 600 12px, muted), a pressed one in the ribbon's
    /// soft fill, edge and red. Up to six.
    /// </summary>
    private void Feel()
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(28);
        var x = at.X;
        var y = at.Y;
        var dl = Kit.Dl;
        for (var i = 0; i < Sessions.MoodChoices.Count; i++)
        {
            var word = Sessions.MoodChoices[i];
            var bw = Txt.Measure(word, Face.SansSemibold, 12).X + S(10.4f) * 2;
            if (x > at.X && x + bw > at.X + w)
            {
                x = at.X;
                y += h + S(4);
            }
            var p = new Vector2(x, y);
            var on = moods.Contains(i);
            var (clicked, hovered, _) = Kit.Hit(Ids.Of("##wrap-feel", i), p, new Vector2(bw, h));
            if (on) dl.AddRectFilled(p, p + new Vector2(bw, h), Palette.U32(Palette.RibbonSoft), h / 2);
            else if (hovered) dl.AddRectFilled(p, p + new Vector2(bw, h), Palette.U32(Palette.LineFaint), h / 2);
            dl.AddRect(p + new Vector2(0.5f), p + new Vector2(bw, h) - new Vector2(0.5f), Palette.U32(on ? Palette.RibbonLine : hovered ? Palette.LineHover : Palette.LineStrong), h / 2);
            Txt.Line(dl, p + new Vector2(S(10.4f), 0), word, Face.SansSemibold, 12, on ? Palette.Ribbon : hovered ? Palette.Ink : Palette.Muted, 28);
            Kit.FocusRing(p, p + new Vector2(bw, h), h / 2);
            if (clicked)
            {
                var k = i;
                Later(() =>
                {
                    if (!moods.Remove(k) && moods.Count < 6) moods.Add(k);
                });
            }
            x += bw + S(4);
        }
        Kit.Reserve(at, w, y + h - at.Y);
    }

    /// <summary>Number tiles: a figure in Fraunces over its word.</summary>
    internal static void StatTiles((string Figure, string Word)[] tiles)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var gap = S(8);
        var tw = (w - gap * (tiles.Length - 1)) / tiles.Length;
        var th = S(56);
        for (var i = 0; i < tiles.Length; i++)
        {
            var p = at + new Vector2(i * (tw + gap), 0);
            Kit.Dl.AddRectFilled(p, p + new Vector2(tw, th), Palette.U32(Palette.Ground2), S(9));
            Kit.Dl.AddRect(p + new Vector2(0.5f), p + new Vector2(tw, th) - new Vector2(0.5f), Palette.U32(Palette.Line), S(9));
            Txt.Line(Kit.Dl, p + new Vector2(S(10), S(8)), Txt.Fit(tiles[i].Figure, Face.Display, 19, tw - S(20)), Face.Display, 19, Palette.Ink, 24);
            Txt.Line(Kit.Dl, p + new Vector2(S(10), S(32)), Txt.Fit(tiles[i].Word, Face.Sans, 11, tw - S(20)), Face.Sans, 11, Palette.Muted, 16);
        }
        Kit.Reserve(at, w, th);
    }
}

/// <summary>
/// A chapter gets a seal: the wax stamp with its numeral, pressed once (900ms), the chapter's name, the last
/// words of its last quest, its numbers, and the theories whose time has come, settled right there. A photo
/// for its page, or Keep.
/// </summary>
public sealed class MilestoneWindow : CardWindow
{
    private readonly Anim press = new(Durations.Wax, Ease.Linear);
    private MilestoneCard? card;

    public MilestoneWindow(Journal journal)
        : base("Story Journal · A chapter closes###SjMilestone", journal)
    {
    }

    protected override float CardWidth => 360;

    protected override float Pad => 20;

    protected override Vector4 Edge => Palette.LineStrong;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.12f), true);

    public override void PreOpenCheck()
    {
        if (journal.Milestone is { } c)
        {
            if (card == null || card.At != c.At) press.Start(Ink.Now, 0.25f);
            card = c;
        }
        Want(journal.Milestone != null);
        if (!IsOpen) card = null;
    }

    /// <summary>The closed chapter's number in its expansion, when the story's reading has its chapters.</summary>
    private int? Number(MilestoneCard c)
    {
        if (journal.Story.Now?.Progress is not { } p) return null;
        var byName = p.Chapters.FirstOrDefault(x => x.Name == c.Chapter);
        if (byName == null) return null;
        var i = 0;
        foreach (var x in p.Chapters)
        {
            if (x.Expansion != byName.Expansion) continue;
            i++;
            if (ReferenceEquals(x, byName)) return i;
        }
        return null;
    }

    protected override void DrawCard()
    {
        if (card is not { } c) return;
        var win = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();
        // The band behind the seal: 86px, from a dark wine (#2a1a1d) to the leaf, a line under it.
        Kit.Leaf(win + new Vector2(1, 1), win + new Vector2(winW - 1, S(86)), Palette.Hex(0x2a1a1d), Palette.Leaf, S(Palette.RadiusLg) - 1, 1f);
        Kit.Dl.AddLine(new Vector2(win.X + 1, win.Y + S(86) + 0.5f), new Vector2(win.X + winW - 1, win.Y + S(86) + 0.5f), Palette.U32(Palette.Line), 1f);
        var at = Kit.At;
        var w = Kit.Width;
        var t = press.Time(Ink.Now, Ink.Still);
        // The seal (72px) overlapping the band's foot, pressed once (1.5 → 0.9 → 1, 900ms).
        var r = S(36);
        var centre = new Vector2(at.X + w / 2, win.Y + S(86 - 40 + 36));
        var mark = Number(c) is { } n ? Roman.Of(n).ToUpperInvariant() : c.ExpansionDone ? "✦" : "";
        Ink.Seal(Kit.Dl, centre, r, Shapes.Seed(c.Chapter), false, mark.Length > 0 && mark != "✦" ? mark : null, mark.Length > 0 && mark != "✦" ? null : Icon.Stamp,
            Ink.PressScale(t), MathF.Min(1, t * 2.2f));
        Kit.Reserve(at, w, centre.Y + r - at.Y);
        Kit.Gap(12);
        Centred(c.ExpansionDone ? "AN EXPANSION ENDS" : "A CHAPTER CLOSES", Face.SansSemibold, 10, Palette.Ribbon, true);
        Kit.Gap(4);
        foreach (var line in Lines.Of(c.Chapter, w, Face.Display, 22)) Centred(line, Face.Display, 22, Palette.Ink);
        if (c.LastLine is { Length: > 0 } last)
        {
            Kit.Gap(6);
            foreach (var line in Lines.Of($"“{last}”", w - S(16), Face.ReadItalic, 14)) Centred(line, Face.ReadItalic, 14, Palette.Body, false, 21.7f);
            Kit.Gap(2);
            Centred("the last words of its last quest", Face.SansMedium, 11, Palette.Faint);
        }
        else if (c.Quest.Length > 0)
        {
            Kit.Gap(6);
            Centred($"its last quest, {c.Quest}", Face.SansMedium, 11, Palette.Faint);
        }
        // Its numbers between two lines: the figures in Fraunces and ink, their words muted.
        Kit.Gap(14);
        var l = Kit.At;
        Kit.Dl.AddLine(new Vector2(l.X, l.Y + 0.5f), new Vector2(l.X + w, l.Y + 0.5f), Palette.U32(Palette.Line), 1f);
        Kit.Reserve(l, w, 1);
        Kit.Gap(10);
        var counts = new List<(string Figure, string Word)>();
        if (c.Quests > 0) counts.Add((Ui.Count(c.Quests), c.Quests == 1 ? "quest" : "quests"));
        if (c.Expansion.Length > 0) counts.Add((string.Empty, c.Expansion));
        Counts(counts, w);
        Kit.Gap(10);
        var l2 = Kit.At;
        Kit.Dl.AddLine(new Vector2(l2.X, l2.Y + 0.5f), new Vector2(l2.X + w, l2.Y + 0.5f), Palette.U32(Palette.Line), 1f);
        Kit.Reserve(l2, w, 1);

        if (c.Theories.Count > 0)
        {
            // Time to settle a theory: on the ground's second ink, each with its seal, your words, and the answers.
            Kit.Gap(14);
            Kit.BeginPanel("##mile-th", 10, 12, 10, Palette.Ground2, Palette.Line, 10);
            Ink.KickerRow("Time to settle a theory", null, 14);
            Kit.Gap(6);
            var i = 0;
            foreach (var th in c.Theories.Take(3))
            {
                var a = Kit.At;
                var sr = S(13);
                var h = Lines.Height(th.Body, Kit.Width - sr * 2 - S(10), Face.ReadItalic, 13, 18.85f);
                var rowH = MathF.Max(sr * 2, h);
                Ink.Seal(Kit.Dl, a + new Vector2(sr, rowH / 2), sr, Shapes.Seed(th.Id.ToString()), false, null, Icon.Feather, 1f, 1f, Palette.Ground2);
                Lines.Draw(Kit.Dl, new Vector2(a.X + sr * 2 + S(10), a.Y + (rowH - h) / 2), Kit.Width - sr * 2 - S(10), th.Body, Face.ReadItalic, 13, Palette.QuillInk, 18.85f);
                Kit.Reserve(a, Kit.Width, rowH);
                Kit.Gap(8);
                var pressed = Parts.Buttons(Ids.Of("##mile-t", i++), TheoryAnswers.Settled
                    .Select(s => (TheoryAnswers.Button(s), s == TheoryAnswers.Changed ? Btn.Quiet : Btn.Glass, (Icon?)null)).ToList(), BtnSize.Tiny);
                if (TheoryAnswers.At(pressed) is { } status)
                {
                    var id = th.Id;
                    Later(() =>
                    {
                        journal.AnswerTheory(id, status);
                        if (journal.Milestone is { } m) journal.Milestone = m with { Theories = m.Theories.Where(x => x.Id != id).ToList() };
                    });
                }
                Kit.Gap(8);
            }
            Kit.EndPanel();
        }
        Kit.Gap(14);
        var b = Kit.At;
        var keepW = Kit.ButtonWidth("Keep", BtnSize.Sm, null, Btn.Primary);
        var photoW = Kit.ButtonWidth("A photo for its page", BtnSize.Sm, Icon.Camera);
        var x = b.X + (w - keepW - photoW - S(8)) / 2;
        if (Kit.Button("##mile-photo", new Vector2(x, b.Y), "A photo for its page", Btn.Glass, BtnSize.Sm, Icon.Camera))
        {
            var caption = c.Chapter;
            Later(() =>
            {
                journal.Milestone = null;
                journal.TakePhoto(caption);
            });
        }
        if (Kit.Button("##mile-keep", new Vector2(x + photoW + S(8), b.Y), "Keep", Btn.Primary, BtnSize.Sm)) Later(() => journal.Milestone = null);
        Kit.Reserve(b, w, Kit.ButtonHeight(BtnSize.Sm));
    }

    private static void Centred(string text, Face face, int px, Vector4 color, bool spaced = false, float lineCss = 0)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = lineCss > 0 ? S(lineCss) : Txt.Natural(face, px) + S(2);
        if (spaced)
        {
            var sw = Txt.SpacedWidth(text, face, px, 0.14f);
            Txt.Spaced(Kit.Dl, new Vector2(at.X + (w - sw) / 2, at.Y), text, face, px, color, 0.14f);
        }
        else
        {
            var shown = Txt.Fit(text, face, px, w);
            var tw = Txt.Measure(shown, face, px).X;
            Txt.Line(Kit.Dl, new Vector2(at.X + (w - tw) / 2, at.Y), shown, face, px, color, h / S(1));
        }
        Kit.Reserve(at, w, h);
    }

    /// <summary>A chapter's numbers on a line, centred, 18px apart: each figure in Fraunces and ink, 4px before its word.</summary>
    private static void Counts(List<(string Figure, string Word)> counts, float w)
    {
        if (counts.Count == 0) return;
        var at = Kit.At;
        var widths = counts.Select(c => (c.Figure.Length > 0 ? Txt.Measure(c.Figure, Face.Display, 15).X + S(4) : 0) + Txt.Measure(c.Word, Face.Sans, 12).X).ToList();
        var total = widths.Sum() + S(18) * (counts.Count - 1);
        var x = at.X + (w - total) / 2;
        for (var i = 0; i < counts.Count; i++)
        {
            var (figure, word) = counts[i];
            if (figure.Length > 0) x += Txt.Line(Kit.Dl, new Vector2(x, at.Y), figure, Face.Display, 15, Palette.Ink, 20) + S(4);
            x += Txt.Line(Kit.Dl, new Vector2(x, at.Y), word, Face.Sans, 12, Palette.Muted, 20) + S(18);
        }
        Kit.Reserve(at, w, S(20));
    }
}
