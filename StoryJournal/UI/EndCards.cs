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

    protected override float CardWidth => 420;

    protected override float Pad => 22;

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
        if (Kit.IconButton("##wrap-x", new Vector2(top.X + w - S(22), top.Y - S(4)), Icon.X, "Close", 22, Btn.Quiet)) Later(() => journal.WrapUp = null);
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
            var before = $"{c.PercentBefore}%";
            var now = $"{c.PercentNow}%";
            var bw = Txt.Measure(before, Face.Mono, 11).X + S(10);
            var nw = Txt.Measure(now, Face.Mono, 11).X + S(10);
            Txt.Line(Kit.Dl, b, before, Face.Mono, 11, Palette.Muted, 16);
            Txt.Line(Kit.Dl, new Vector2(b.X + w - nw + S(10), b.Y), now, Face.Mono, 11, Palette.Ink, 16);
            var tx = b.X + bw;
            var tw = w - bw - nw;
            var ty = b.Y + S(6);
            Kit.Dl.AddRectFilled(new Vector2(tx, ty), new Vector2(tx + tw, ty + S(5)), Palette.U32(Palette.Track), S(3));
            var p0 = tw * Math.Clamp(c.PercentBefore / 100f, 0, 1);
            var p1 = tw * Math.Clamp(c.PercentNow / 100f, 0, 1);
            if (p0 > 0) Kit.Dl.AddRectFilled(new Vector2(tx, ty), new Vector2(tx + p0, ty + S(5)), Palette.U32(Palette.RibbonDark), S(3));
            if (p1 > p0) Kit.Dl.AddRectFilled(new Vector2(tx + p0, ty), new Vector2(tx + p1, ty + S(5)), Palette.U32(Palette.Ribbon), S(3));
            Kit.Reserve(b, w, S(16));
        }
        Kit.Gap(12);
        var length = (c.Session.EndedAt ?? c.Session.LastAt) - c.Session.StartedAt;
        var third = c.Numbers.People > 0 ? (Ui.Count(c.Numbers.People), c.Numbers.People == 1 ? "new person" : "new people")
            : (Ui.Count(c.Numbers.Cutscenes), c.Numbers.Cutscenes == 1 ? "cutscene" : "cutscenes");
        StatTiles(new (string, string)[] { (Ui.Duration(length), "played"), (Ui.Count(c.Numbers.Quests), c.Numbers.Quests == 1 ? "quest" : "quests"), third });

        Kit.Gap(12);
        Parts.Quiet(c.Title != null ? "Title it (we suggested one; type over it)" : "Title it (the Chronicle and the Storybook use it)");
        Kit.Gap(4);
        var f = Kit.At;
        var tagW = c.Title != null && title == c.Title ? Txt.Measure("suggested", Face.Sans, 11).X + S(16) : 0;
        Kit.Field("##wrap-title", f, w, ref title, "Give tonight a name", null, 80, ImGuiInputTextFlags.None, Face.ReadItalic, 15, Palette.QuillInk);
        if (tagW > 0) Txt.Line(Kit.Dl, new Vector2(f.X + w - tagW, f.Y), "suggested", Face.Sans, 11, Palette.Faint, 36);
        Kit.Reserve(f, w, S(36));
        Kit.Gap(10);
        Parts.Quiet("How did it feel?");
        Kit.Gap(4);
        var flipped = Parts.PickChips("##wrap-mood", Sessions.MoodChoices, i => moods.Contains(i));
        if (flipped >= 0 && !moods.Remove(flipped) && moods.Count < 6) moods.Add(flipped);
        Kit.Gap(10);
        Parts.Quiet("A thought for next time (it's on your Welcome back)");
        Kit.Gap(4);
        var n = Kit.At;
        Kit.Field("##wrap-note", n, w, ref note, "Ask him about the camp before going up…", null, 2000, ImGuiInputTextFlags.None, Face.ReadItalic, 14, Palette.QuillInk);
        Kit.Reserve(n, w, S(36));
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
            Kit.Dl.AddRectFilled(p, p + new Vector2(tw, th), Palette.U32(Palette.Ground2), S(10));
            Kit.Dl.AddRect(p + new Vector2(0.5f), p + new Vector2(tw, th) - new Vector2(0.5f), Palette.U32(Palette.Line), S(10));
            Txt.Line(Kit.Dl, p + new Vector2(S(12), S(8)), Txt.Fit(tiles[i].Figure, Face.Display, 19, tw - S(20)), Face.Display, 19, Palette.Ink, 24);
            Txt.Line(Kit.Dl, p + new Vector2(S(12), S(32)), Txt.Fit(tiles[i].Word, Face.Sans, 11, tw - S(20)), Face.Sans, 11, Palette.Muted, 16);
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

    protected override float CardWidth => 380;

    protected override float Pad => 22;

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
        // A darker band behind the seal.
        Kit.Dl.AddRectFilled(win, win + new Vector2(winW, S(90)), Palette.U32(Palette.InkGround), S(Palette.RadiusLg), ImDrawFlags.RoundCornersTop);
        var at = Kit.At;
        var w = Kit.Width;
        var t = press.Time(Ink.Now, Ink.Still);
        var r = S(36);
        var centre = new Vector2(at.X + w / 2, at.Y + S(48));
        var mark = Number(c) is { } n ? Roman.Of(n).ToUpperInvariant() : c.ExpansionDone ? "✦" : "";
        Ink.Seal(Kit.Dl, centre, r, Shapes.Seed(c.Chapter), false, mark.Length > 0 && mark != "✦" ? mark : null, mark.Length > 0 && mark != "✦" ? null : Icon.Stamp,
            Ink.PressScale(t), MathF.Min(1, t * 2.2f));
        Kit.Reserve(at, w, S(96));
        Kit.Gap(10);
        Centred(c.ExpansionDone ? "AN EXPANSION ENDS" : "A CHAPTER CLOSES", Face.SansSemibold, 10, Palette.Ribbon, true);
        Kit.Gap(4);
        foreach (var line in Lines.Of(c.Chapter, w, Face.Display, 22)) Centred(line, Face.Display, 22, Palette.Ink);
        if (c.LastLine is { Length: > 0 } last)
        {
            Kit.Gap(8);
            foreach (var line in Lines.Of($"“{last}”", w - S(24), Face.ReadItalic, 14)) Centred(line, Face.ReadItalic, 14, Palette.Body);
            Kit.Gap(2);
            Centred("the last words of its last quest", Face.Sans, 11, Palette.Faint);
        }
        Kit.Gap(12);
        var l = Kit.At;
        Kit.Divider(l.X, l.X + w, l.Y);
        Kit.Reserve(l, w, 1);
        Kit.Gap(10);
        var numbers = c.Quests > 0 ? $"{Ui.Plural(c.Quests, "quest", "quests")} · the last, {c.Quest}" : c.Quest;
        if (c.Expansion.Length > 0) numbers += $" · {c.Expansion}";
        foreach (var line in Lines.Of(numbers, w, Face.Sans, 12)) Centred(line, Face.Sans, 12, Palette.Muted);
        Kit.Gap(10);
        var l2 = Kit.At;
        Kit.Divider(l2.X, l2.X + w, l2.Y);
        Kit.Reserve(l2, w, 1);

        if (c.Theories.Count > 0)
        {
            Kit.Gap(12);
            Kit.BeginPanel("##mile-th", 12, 14, 12, Palette.Leaf, Palette.Line, Palette.RadiusLg);
            Ink.KickerRow("Time to settle a theory");
            Kit.Gap(8);
            var i = 0;
            foreach (var th in c.Theories.Take(3))
            {
                var a = Kit.At;
                var sr = S(13);
                Ink.Seal(Kit.Dl, a + new Vector2(sr, sr), sr, Shapes.Seed(th.Id.ToString()), false, null, Icon.Feather);
                var h = Lines.Draw(Kit.Dl, new Vector2(a.X + sr * 2 + S(10), a.Y), Kit.Width - sr * 2 - S(10), th.Body, Face.ReadItalic, 13, Palette.QuillInk, 19);
                Kit.Reserve(a, Kit.Width, MathF.Max(sr * 2, h));
                Kit.Gap(6);
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

    private static void Centred(string text, Face face, int px, Vector4 color, bool spaced = false)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = Txt.Natural(face, px);
        if (spaced)
        {
            var sw = Txt.SpacedWidth(text, face, px, 0.14f);
            Txt.Spaced(Kit.Dl, new Vector2(at.X + (w - sw) / 2, at.Y), text, face, px, color, 0.14f);
        }
        else
        {
            var shown = Txt.Fit(text, face, px, w);
            var tw = Txt.Measure(shown, face, px).X;
            Txt.Draw(Kit.Dl, new Vector2(at.X + (w - tw) / 2, at.Y), shown, face, px, color);
        }
        Kit.Reserve(at, w, h + S(2));
    }
}
