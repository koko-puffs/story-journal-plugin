using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// A margin note, not a pop-up (Arrival, Who's that?): a narrow card with the red margin rule down its left,
/// sliding in 14px from the left as it fades in (420ms), holding a while, then fading (600ms). One at a time.
/// </summary>
public abstract class MarginNote : CardWindow
{
    protected MarginNote(string name, Journal journal)
        : base(name, journal, false)
    {
    }

    protected override Vector2 From => new(-14, 0);

    protected override Vector2 LeaveTo => Vector2.Zero;

    protected override float LeaveSeconds => 0.6f;

    protected override float Pad => 16;

    /// <summary>The margin rule down the card's left, inside its edge.</summary>
    protected static void MarginRule()
    {
        var win = ImGui.GetWindowPos();
        var h = ImGui.GetWindowHeight();
        Ink.Rule(Kit.Dl, win.X + S(10), win.Y + S(14), win.Y + h - S(14));
    }
}

/// <summary>
/// An arrival card: the first time you reach a place (every time, with the whispers chatty), two sentences
/// from the Codex, a way to its Codex entry and a way to have fewer. Never in combat, a duty or a cutscene
/// (it waits for a calm moment).
/// </summary>
public sealed class ArrivalWindow : MarginNote
{
    private ArrivalCard? card;

    public ArrivalWindow(Journal journal)
        : base("Story Journal · Arrival###SjArrival", journal)
    {
    }

    public Action<string>? OpenCodex { get; set; }

    protected override float CardWidth => 300;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.03f, 0.24f), false);

    public override void PreOpenCheck()
    {
        if (journal.Arrival is { } c) card = c;
        Want(journal.Arrival != null);
        if (!IsOpen) card = null;
    }

    protected override void DrawCard()
    {
        if (card is not { } c) return;
        MarginRule();
        Ink.KickerRow(c.First ? "You arrived · first time" : "You arrived", Palette.Ribbon);
        Kit.Gap(4);
        foreach (var line in Lines.Of(c.Place, Kit.Width, Face.Display, 19))
        {
            var a = Kit.At;
            Txt.Line(Kit.Dl, a, line, Face.Display, 19, Palette.Ink, 25);
            Kit.Reserve(a, Kit.Width, S(25));
        }
        Kit.Gap(4);
        Lines.Para(c.Whisper, Palette.Body, 13, 20, Face.Read);
        Kit.Gap(10);
        var at = Kit.At;
        var w = Kit.Width;
        if (Kit.Link("##arrival-more", at, "More in the Codex", 12))
        {
            var place = c.Place;
            Later(() =>
            {
                OpenCodex?.Invoke(place);
                journal.Arrival = null;
            });
        }
        var fewer = "Fewer of these";
        var fw = Txt.Measure(fewer, Face.Sans, 12).X;
        var (clicked, hovered, _) = Kit.Hit("##arrival-fewer", new Vector2(at.X + w - fw, at.Y), new Vector2(fw, S(17)));
        Txt.Line(Kit.Dl, new Vector2(at.X + w - fw, at.Y), fewer, Face.Sans, 12, hovered ? Palette.Ink : Palette.Muted, 17);
        Kit.Reserve(at, w, S(17));
        if (clicked)
        {
            Later(() =>
            {
                journal.Config.WhispersHere = journal.Whispers == Whispers.Chatty ? (int)Whispers.Some : (int)Whispers.Quiet;
                journal.Config.Save();
                journal.Arrival = null;
                journal.AddToast("Fewer margin notes", journal.Whispers == Whispers.Quiet ? "Only when you ask, from now on." : "Only new places, from now on.", "Settings changes it back", ToastKind.Plain);
            });
        }
    }
}

/// <summary>
/// Who's that?: target someone you've met and see when and where you met them, what you know, and what you
/// wrote about them. Strangers show nothing.
/// </summary>
public sealed class WhoWindow : MarginNote
{
    private WhoCard? card;

    public WhoWindow(Journal journal)
        : base("Story Journal · Who's that?###SjWho", journal)
    {
    }

    public Action<string>? OpenCodex { get; set; }

    protected override float CardWidth => 290;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.44f, 0.22f), false);

    public override void PreOpenCheck()
    {
        if (journal.Who is { } c) card = c;
        Want(journal.Who != null);
        if (!IsOpen) card = null;
    }

    protected override void DrawCard()
    {
        if (card is not { } who) return;
        MarginRule();
        var top = Kit.At;
        if (Kit.IconButton("##who-x", new Vector2(top.X + Kit.Width - S(20), top.Y - S(4)), Icon.X, "Close", 20, Btn.Quiet)) Later(() => journal.Who = null);
        Ink.KickerRow("Someone you've met", Palette.Ribbon);
        Kit.Gap(4);
        foreach (var line in Lines.Of(who.Entry?.Name ?? who.Name, Kit.Width, Face.Display, 19))
        {
            var a = Kit.At;
            Txt.Line(Kit.Dl, a, line, Face.Display, 19, Palette.Ink, 25);
            Kit.Reserve(a, Kit.Width, S(25));
        }
        Kit.Gap(4);
        var here = journal.Story.Now?.Progress.Current?.Place is { Length: > 0 } p && string.Equals(p, who.Met.Place, StringComparison.OrdinalIgnoreCase);
        var s = Kit.At;
        Ink.Stamp(Kit.Dl, s, $"Met · {Ui.Ago(who.Met.At)}" + (here ? " · here" : who.Met.Place.Length > 0 ? $" · {who.Met.Place}" : string.Empty));
        Kit.Reserve(s, Kit.Width, S(20));
        Kit.Gap(8);
        if (who.Layers.Count > 0) Lines.Para(who.Layers[0].Text, Palette.Body, 13, 20, Face.Read);
        else if (who.Lines > 0) Parts.Quiet(who.Lines == 1 ? "You saved a line of theirs." : $"You saved {who.Lines} lines of theirs.");
        if (who.Notes.Count > 0)
        {
            Kit.Gap(8);
            var l = Kit.At;
            Kit.Divider(l.X, l.X + Kit.Width, l.Y);
            Kit.Reserve(l, Kit.Width, 1);
            Kit.Gap(8);
            Ink.KickerRow("You wrote", Palette.Quill);
            Kit.Gap(2);
            foreach (var n in who.Notes.Take(2))
            {
                Lines.Para(n.Body, Palette.QuillInk, 13, 19, Face.ReadItalic);
                Kit.Gap(4);
            }
        }
        if (who.Entry != null)
        {
            Kit.Gap(8);
            var at = Kit.At;
            if (Kit.Link("##who-codex", at, "In the Codex", 12))
            {
                var name = who.Entry.Name;
                Later(() => OpenCodex?.Invoke(name));
            }
            Kit.Reserve(at, Kit.Width, S(17));
        }
    }
}

/// <summary>
/// The capture toasts: a line saved, a photo taken, a note kept, a theory sealed or answered, a link
/// made. Up to four, each for a few seconds, top right, out of the way; nothing in them to press, and
/// clicks go through to the game.
/// </summary>
public sealed class ToastWindow : Window, IDisposable
{
    private const float Width = 300;

    private readonly Journal journal;
    private readonly Fonts fonts;
    private int colors;
    private int vars;
    private bool disposed;

    public ToastWindow(Journal journal)
        : base("Story Journal · Toasts###SjToasts", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                                                   | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
                                                   | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground
                                                   | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings)
    {
        this.journal = journal;
        fonts = Ui.AcquireFonts();
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        DisableWindowSounds = true;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    public override void PreOpenCheck() => IsOpen = journal.Toasts.Count > 0;

    public override void PreDraw()
    {
        var vp = ImGuiHelpers.MainViewport;
        Position = new Vector2(vp.Pos.X + vp.Size.X - S(Width) - S(24), vp.Pos.Y + vp.Size.Y * 0.14f);
        PositionCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        vars = 2;
        colors = 0;
    }

    public override void PostDraw()
    {
        if (vars > 0) ImGui.PopStyleVar(vars);
        if (colors > 0) ImGui.PopStyleColor(colors);
        vars = colors = 0;
    }

    public override void Draw()
    {
        Kit.ResetFrame();
        Theme.PushInside();
        var origin = ImGui.GetCursorScreenPos();
        Kit.PushBox(origin.X, S(Width));
        try
        {
            using (fonts.Push(Face.Sans, 14))
            {
                var now = DateTime.UtcNow;
                foreach (var t in journal.Toasts.ToArray()) DrawToast(t, now);
            }
        }
        finally
        {
            Kit.PopBox();
            Theme.Pop();
        }
    }

    /// <summary>
    /// A toast: what was kept, top right, a few seconds. Its tile is the motif of what it kept: a quote, a camera,
    /// a seal. It slides in 14px from the right as it fades in, and fades as it goes.
    /// </summary>
    private static void DrawToast(Toast t, DateTime now)
    {
        var age = (now - t.At).TotalSeconds;
        var left = 6 - age;
        var into = Ink.Still ? 1f : Ease.Settle((float)Math.Clamp(age / Durations.Turn, 0, 1));
        var old = Palette.Opacity;
        Palette.Opacity = Ink.Still ? 1 : (float)Math.Min(into, Math.Clamp(left / 0.4, 0, 1));
        var at = Kit.At + new Vector2(S(14) * (1 - into), 0);
        var w = Kit.Width;
        var tile = S(32);
        var inner = w - S(12) * 2 - tile - S(12);
        var bodyColor = t.Kind is ToastKind.Quill ? Palette.QuillInk : Palette.Body;
        var bodyFace = t.Kind is ToastKind.Quill or ToastKind.Seal ? Face.ReadItalic : Face.Sans;
        var h = MathF.Max(tile + S(12) * 2, S(12) * 2 + Txt.Natural(Face.SansSemibold, 13)
                + (t.Body != null ? S(2) + Lines.Height(t.Body, inner, bodyFace, 12, 17) : 0)
                + (t.Note != null ? S(2) + Lines.Height(t.Note, inner, Face.Sans, 11, 15) : 0));
        var dl = Kit.Dl;
        dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(Palette.WithAlpha(Palette.LeafRaised, 0.97f)), S(12));
        dl.AddRect(at, at + new Vector2(w, h), Palette.U32(t.Kind == ToastKind.Bad ? Palette.DangerEdge : Palette.LineStrong), S(12));
        var tp = at + new Vector2(S(12), S(12));
        switch (t.Kind)
        {
            case ToastKind.Seal:
                Ink.Seal(dl, tp + new Vector2(tile / 2), tile / 2 - S(2), Shapes.Seed(t.Title + t.At.Ticks), false, null, Icon.Feather);
                break;
            default:
            {
                var (icon, ink, fill, edge) = t.Kind switch
                {
                    ToastKind.Quill => (Icon.Quote, Palette.Quill, Palette.QuillSoft, Palette.QuillLine),
                    ToastKind.Photo => (Icon.Camera, Palette.Body, Palette.Hover, Palette.LineStrong),
                    ToastKind.Good => (Icon.Check, Palette.Good, Palette.SuccessWash, Palette.SuccessEdge),
                    ToastKind.Bad => (Icon.AlertTriangle, Palette.Danger, Palette.DangerWash, Palette.DangerEdge),
                    _ => (Icon.BookOpen, Palette.Ribbon, Palette.RibbonSoft, Palette.RibbonLine),
                };
                dl.AddRectFilled(tp, tp + new Vector2(tile), Palette.U32(fill), S(9));
                dl.AddRect(tp, tp + new Vector2(tile), Palette.U32(edge), S(9));
                Icons.Draw(dl, icon, tp + new Vector2((tile - S(16)) / 2), S(16), ink);
                break;
            }
        }
        var x = tp.X + tile + S(12);
        var y = at.Y + S(12);
        Txt.Line(dl, new Vector2(x, y), Txt.Fit(t.Title, Face.SansSemibold, 13, inner), Face.SansSemibold, 13, Palette.Ink);
        y += Txt.Natural(Face.SansSemibold, 13);
        if (t.Body != null) y += S(2) + Lines.Draw(dl, new Vector2(x, y + S(2)), inner, t.Body, bodyFace, 12, bodyColor, 17);
        if (t.Note != null) Lines.Draw(dl, new Vector2(x, y + S(2)), inner, t.Note, Face.Sans, 11, Palette.Muted, 15);
        Kit.Reserve(Kit.At, w, h);
        Kit.Gap(8);
        Palette.Opacity = old;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ReleaseFonts();
    }
}

/// <summary>
/// The quick note (its key, or <c>/sj note</c> on its own): an index card, its ruled lines and red margin, your
/// words in candle gold as you type. It knows who you're looking at, so About fills itself. Kept in tonight's
/// entry, or sealed as a theory to settle later.
/// </summary>
public sealed class QuickNoteWindow : CardWindow
{
    private string text = string.Empty;
    private string subject = string.Empty;
    private bool focus;

    public QuickNoteWindow(Journal journal)
        : base("Story Journal · Quick note###SjQuickNote", journal)
    {
        RespectCloseHotkey = true;
    }

    protected override float CardWidth => 380;

    protected override float Pad => 18;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.3f), true);

    /// <summary>Opens it, about someone or something when given (else whoever you're looking at, if you've met them).</summary>
    public void Show(string about = "")
    {
        subject = about.Length > 0 ? about : Game.InGame.TargetName() ?? string.Empty;
        focus = true;
        IsOpen = true;
        BringToFront();
    }

    protected override void DrawCard()
    {
        var win = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();
        var winH = ImGui.GetWindowHeight();
        var dl = Kit.Dl;
        // The index card: ruled lines every 26px, the red margin 38px in.
        for (var y = win.Y + S(52); y < win.Y + winH - S(8); y += S(26))
            dl.AddLine(new Vector2(win.X + 1, y), new Vector2(win.X + winW - 1, y), Palette.U32(Palette.Rgba(255, 240, 225, 0.05f)), 1f);
        dl.AddLine(new Vector2(win.X + S(38), win.Y + 1), new Vector2(win.X + S(38), win.Y + winH - 1), Palette.U32(Palette.RibbonLine), 1f);

        var at = Kit.At;
        var w = Kit.Width;
        Icons.Draw(dl, Icon.Feather, at + new Vector2(S(24), S(1)), S(13), Palette.Quill);
        Ink.Kicker(dl, at + new Vector2(S(42), S(1)), "A quick note", Palette.Quill);
        if (Kit.IconButton("##note-x", new Vector2(at.X + w - S(22), at.Y - S(4)), Icon.X, "Close", 22, Btn.Quiet)) Later(Clear);
        Kit.Reserve(at, w, S(20));
        Kit.Gap(10);
        if (focus)
        {
            ImGui.SetKeyboardFocusHere();
            focus = false;
        }
        // Your words, on the lines, in candle gold.
        var f = Kit.At;
        var fx = f.X + S(30);
        var fw = w - S(30);
        using (Txt.Fonts.Push(Face.ReadItalic, 15))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.QuillInk);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, S(2)));
            ImGui.SetCursorScreenPos(new Vector2(fx, f.Y));
            ImGui.InputTextMultiline("##note-text", ref text, 2000, new Vector2(fw, S(26) * 3), ImGuiInputTextFlags.None);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }
        if (text.Length == 0)
            Txt.Line(dl, new Vector2(fx, f.Y + S(2)), "What's on your mind about the story…", Face.ReadItalic, 15, Palette.Faint, 22);
        Kit.Reserve(f, w, S(26) * 3);
        Kit.Gap(10);
        var about = Kit.At;
        Icons.Draw(dl, Icon.Users, about + new Vector2(S(30), S(5)), S(13), Palette.Muted);
        Txt.Line(dl, about + new Vector2(S(48), 0), "About", Face.Sans, 12, Palette.Muted, 24);
        Kit.Field("##note-about", about + new Vector2(S(90), 0), w - S(90), ref subject, "no one in particular", null, 60, ImGuiInputTextFlags.None, Face.SansMedium, 12, Palette.Ink, false, 24);
        Kit.Reserve(about, w, S(24));
        Kit.Gap(12);
        var empty = text.Trim().Length == 0;
        Kit.PushBox(Kit.Left + S(30), w - S(30));
        var pressed = Parts.Buttons("##note-b", new (string, Btn, Icon?)[]
        {
            (journal.Session != null ? "Add to tonight's entry" : "Keep it", Btn.Glass, null),
            ("Seal as theory", Btn.Quill, null),
        });
        Kit.PopBox();
        if (pressed == 0 && !empty)
        {
            var body = text;
            var who = subject.Trim();
            Later(() =>
            {
                if (journal.AddNote(body, who)) Clear();
            });
        }
        else if (pressed == 1 && !empty)
        {
            var body = text;
            var who = subject.Trim();
            Later(() =>
            {
                if (journal.SealTheory(body, 10, who)) Clear();
            });
        }
    }

    private void Clear()
    {
        text = string.Empty;
        subject = string.Empty;
        IsOpen = false;
    }
}
