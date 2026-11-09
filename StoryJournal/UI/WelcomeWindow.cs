using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// Welcome back: the first thing after you log in when you've been away as long as the site's preference says
/// (a day, unless set). A page from your journal: the ribbon marks it as yours, the day and how long away, the
/// greeting, the recap in the reading face with a drop cap (three sentences at most), your own thought from the
/// end of the last evening in candle gold right above the step it was about, then the step. One way forward:
/// Flag it is the one filled button; Later closes it, and so does walking away. It rises 18px as it fades in
/// (420ms) and leaves up 8px (200ms).
/// </summary>
public sealed class WelcomeWindow : CardWindow
{
    private WelcomeCard? card;

    /// <summary>The ribbon drops once each time it opens.</summary>
    private readonly Anim ribbon = new(Durations.Ink, Ease.Weight);

    public WelcomeWindow(Journal journal)
        : base("Story Journal · Welcome back###SjWelcome", journal)
    {
    }

    /// <summary>Opens the main window on a place.</summary>
    public Action<MainTab>? Open { get; set; }

    protected override float CardWidth => 440;

    protected override float Pad => 26;

    protected override Vector4 Edge => Palette.LineStrong;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.2f), true);

    public override void PreOpenCheck()
    {
        if (journal.Welcome is { } c)
        {
            if (card == null || !ReferenceEquals(card, c)) ribbon.Start(Ink.Now, 0.2f);
            card = c;
        }
        Want(journal.Welcome != null);
        if (!IsOpen) card = null;
    }

    protected override void DrawCard()
    {
        if (card is not { } c) return;
        var dl = Kit.Dl;
        var at = Kit.At;
        var w = Kit.Width;
        var win = Dalamud.Bindings.ImGui.ImGui.GetWindowPos();
        var winW = Dalamud.Bindings.ImGui.ImGui.GetWindowWidth();
        // The red edge along the top, and the ribbon hanging from it.
        dl.AddRectFilled(win, win + new Vector2(winW, S(3)), Palette.U32(Palette.Ribbon), S(Palette.RadiusLg), Dalamud.Bindings.ImGui.ImDrawFlags.RoundCornersTop);
        Ink.Ribbon(dl, new Vector2(win.X + S(Pad), win.Y), S(24), S(48), ribbon.Value(Ink.Now, Ink.Still));
        if (Kit.IconButton("##welcome-x", new Vector2(at.X + w - S(24), at.Y - S(12)), Icon.X, "Close", 24, Btn.Quiet)) Later(() => journal.Welcome = null);
        Kit.Gap(36);

        var last = c.LastPlayed.ToLocalTime();
        var away = DateTime.UtcNow - c.LastPlayed;
        var awayWords = away.TotalDays >= 2 ? $"{(int)away.TotalDays} days away" : away.TotalHours >= 20 ? "a day away" : Ui.Ago(c.LastPlayed);
        Ink.KickerRow($"{last.ToString("dddd d MMMM", CultureInfo.InvariantCulture)} · {awayWords}");
        Kit.Gap(6);
        var name = journal.Player?.Name is { Length: > 0 } n ? n.Split(' ')[0] : null;
        var hello = name != null ? $"Welcome back, {name}." : "Welcome back.";
        foreach (var line in Lines.Of(hello, w, Face.Display, 28))
        {
            var a = Kit.At;
            Txt.Line(dl, a, line, Face.Display, 28, Palette.Ink, 34);
            Kit.Reserve(a, w, S(34));
        }
        Kit.Gap(10);

        // The recap, three sentences at most, with a drop cap, ruled.
        var recap = string.Join(" ", c.Lines.Take(3));
        if (recap.Length > 0)
        {
            var r = Kit.At;
            var h = Ink.DropCap(dl, new Vector2(r.X + S(22), r.Y), w - S(22), recap, Face.Read, 15, Palette.Body, 23);
            Ink.Rule(dl, r.X, r.Y + S(3), r.Y + h - S(3));
            Kit.Reserve(r, w, h);
        }
        else Parts.Quiet("Nothing written yet: your next evenings fill this.");

        // Your thought from last time.
        if (c.Note is { } note)
        {
            Kit.Gap(14);
            Kit.BeginPanel("##welcome-note", 12, 14, 12, Palette.Over(Palette.QuillSoft, Palette.Leaf), Palette.QuillLine, Palette.RadiusMd);
            Ink.KickerRow("Your thought from last time", Palette.Quill, 14);
            Kit.Gap(4);
            Lines.Para(note.Body, Palette.QuillInk, 14, 21, Face.ReadItalic);
            Kit.EndPanel();
        }

        // The step.
        Kit.Gap(12);
        var s = Kit.At;
        var sh = S(58);
        dl.AddRectFilled(s, s + new Vector2(w, sh), Palette.U32(Palette.Ground2), S(Palette.RadiusMd));
        dl.AddRect(s + new Vector2(0.5f), s + new Vector2(w, sh) - new Vector2(0.5f), Palette.U32(Palette.Line), S(Palette.RadiusMd));
        var tile = S(32);
        var tc = s + new Vector2(S(14) + tile / 2, sh / 2);
        dl.AddCircleFilled(tc, tile / 2, Palette.U32(Palette.RibbonSoft), 24);
        dl.AddCircle(tc, tile / 2 - 0.5f, Palette.U32(Palette.RibbonLine), 24, 1f);
        Icons.Draw(dl, Icon.MapPin, tc - new Vector2(S(8)), S(16), Palette.Ribbon);
        var tx = s.X + S(14) + tile + S(14);
        var step = c.Next.Count > 0 ? c.Next[0] : c.Quest ?? "The next step shows here once the story's read";
        var under = c.Quest != null && c.Next.Count > 0
            ? c.Quest + (journal.Story.Now?.Progress.Current?.Place is { Length: > 0 } place ? $" · {place}" : string.Empty)
            : "Take it from the scenario guide";
        Txt.Line(dl, new Vector2(tx, s.Y + S(10)), Txt.Fit(step, Face.SansSemibold, 14, w - (tx - s.X) - S(12)), Face.SansSemibold, 14, Palette.Ink, 20);
        Txt.Line(dl, new Vector2(tx, s.Y + S(31)), Txt.Fit(under, Face.Sans, 12, w - (tx - s.X) - S(12)), Face.Sans, 12, Palette.Muted, 17);
        Kit.Reserve(s, w, sh);

        // One way forward.
        Kit.Gap(16);
        var pressed = Parts.Buttons("##welcome-b", new (string, Btn, Icon?)[]
        {
            ("Flag it", Btn.Primary, Icon.Flag),
            ("Open the journal", Btn.Glass, Icon.BookOpen),
            ("→Later", Btn.Quiet, null),
        });
        switch (pressed)
        {
            case 0:
                Later(() =>
                {
                    journal.FlagNext();
                    journal.Welcome = null;
                });
                break;
            case 1:
                Later(() =>
                {
                    Open?.Invoke(MainTab.Now);
                    journal.Welcome = null;
                });
                break;
            case 2:
                Later(() => journal.Welcome = null);
                break;
        }
        Kit.Gap(14);
        var f = Kit.At;
        var words = "Only what you've seen · the whole recap is on the site";
        var fw = Txt.Measure(words, Face.Sans, 11).X;
        var (clicked, hovered, _) = Kit.Hit("##welcome-site", new Vector2(f.X + (w - fw) / 2, f.Y), new Vector2(fw, S(16)));
        Txt.Line(dl, new Vector2(f.X + (w - fw) / 2, f.Y), words, Face.Sans, 11, hovered ? Palette.Ink : Palette.Faint, 16);
        if (clicked) Dalamud.Utility.Util.OpenLink(SiteLink.RecapUrl);
        Kit.Reserve(f, w, S(16));
    }
}
