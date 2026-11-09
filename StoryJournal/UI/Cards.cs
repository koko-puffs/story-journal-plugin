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

    /// <summary>The words start 22px in, right of the rule.</summary>
    protected override float PadLeft => 22;

    /// <summary>The margin rule down the card's left, inside its edge: the ribbon's red, 2px, 11px in, 14px from top and foot.</summary>
    protected static void MarginRule()
    {
        var win = ImGui.GetWindowPos();
        var h = ImGui.GetWindowHeight();
        Kit.Dl.AddRectFilled(new Vector2(win.X + S(11), win.Y + S(14)), new Vector2(win.X + S(13), win.Y + h - S(14)), Palette.U32(Palette.Ribbon), S(1));
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

    protected override float CardWidth => 290;

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
        Kit.Gap(2);
        Lines.Para(c.Whisper, Palette.Body, 13, 20, Face.Read);
        Kit.Gap(10);
        var at = Kit.At;
        var w = Kit.Width;
        var bh = S(26);
        if (Kit.Link("##arrival-more", at + new Vector2(0, (bh - Txt.Natural(Face.SansSemibold, 12)) / 2), "More in the Codex", 12))
        {
            var place = c.Place;
            Later(() =>
            {
                OpenCodex?.Invoke(place);
                journal.Arrival = null;
            });
        }
        // "Fewer of these": a small ghost button, at the right.
        var fewer = "Fewer of these";
        var fw = Kit.ButtonWidth(fewer, BtnSize.Tiny, null, Btn.Quiet);
        var clicked = Kit.Button("##arrival-fewer", new Vector2(at.X + w - fw + S(8), at.Y), fewer, Btn.Quiet, BtnSize.Tiny);
        Kit.Reserve(at, w, bh);
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
        Tether(who.Name);
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
        var stamp = $"Met · {Ui.Ago(who.Met.At)}" + (here ? " · here" : who.Met.Place.Length > 0 ? $" · {who.Met.Place}" : string.Empty);
        // A long place doesn't fit the card's stamp: when, then.
        if (Ink.StampWidth(stamp) > Kit.Width) stamp = $"Met · {Ui.Ago(who.Met.At)}";
        Ink.Stamp(Kit.Dl, s, stamp);
        Kit.Reserve(s, Kit.Width, S(20));
        Kit.Gap(8);
        if (who.Layers.Count > 0) Lines.Para(who.Layers[0].Text, Palette.Body, 13, 20.15f, Face.Read);
        else if (who.Lines > 0) Parts.Quiet(who.Lines == 1 ? "You saved a line of theirs." : $"You saved {who.Lines} lines of theirs.");
        if (who.Notes.Count > 0)
        {
            Kit.Gap(8);
            var l = Kit.At;
            Kit.Dl.AddLine(new Vector2(l.X, l.Y + 0.5f), new Vector2(l.X + Kit.Width, l.Y + 0.5f), Palette.U32(Palette.Line), 1f);
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

    /// <summary>
    /// The red tether from whoever you've targeted to their card (2px, the ribbon's red), under every window, when
    /// they're on the screen to the card's left or right.
    /// </summary>
    private static void Tether(string name)
    {
        if (Game.InGame.TargetOnScreen(name) is not { } from) return;
        var win = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var left = from.X < win.X;
        if (!left && from.X < win.X + size.X) return;
        var to = new Vector2(left ? win.X : win.X + size.X, Math.Clamp(from.Y, win.Y + S(14), win.Y + size.Y - S(14)));
        ImGui.GetBackgroundDrawList().AddLine(from, to, Palette.U32(Palette.Ribbon), S(2));
    }
}

/// <summary>
/// The capture toasts: a line saved, a photo taken, a note kept, a theory sealed or answered, a link
/// made. Up to four, each for a few seconds, top right, out of the way; nothing in them to press, and
/// clicks go through to the game.
/// </summary>
public sealed class ToastWindow : Window, IDisposable
{
    private const float Width = 250;

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
    /// A toast, as the mock-up's: what was kept, top right, a few seconds; near-black (94%), the strong edge, radius 10,
    /// its title in Geist 600 12px and a small line under it. Its tile is the motif of what it kept: a quote, a camera,
    /// a seal (cracked once the story's answered it). It slides in 14px from the right as it fades in (420ms, settle),
    /// and goes quicker (200ms).
    /// </summary>
    private static void DrawToast(Toast t, DateTime now)
    {
        var age = (now - t.At).TotalSeconds;
        var left = Journal.ToastSeconds - age;
        var into = Ink.Still ? 1f : Ease.Settle((float)Math.Clamp(age / Durations.Turn, 0, 1));
        var old = Palette.Opacity;
        Palette.Opacity = Ink.Still ? 1 : (float)Math.Min(into, Math.Clamp(left / Durations.Leave, 0, 1));
        var at = Kit.At + new Vector2(S(14) * (1 - into), 0);
        var w = Kit.Width;
        var tile = S(28);
        var padX = S(10);
        var padY = S(8);
        var inner = w - padX * 2 - tile - S(9);
        var bodyColor = t.Kind is ToastKind.Quill ? Palette.QuillInk : Palette.Body;
        var bodyFace = t.Kind is ToastKind.Quill or ToastKind.Seal ? Face.ReadItalic : Face.Sans;
        var wordsH = Txt.Natural(Face.SansSemibold, 12)
                     + (t.Body != null ? S(1) + Lines.Height(t.Body, inner, bodyFace, 12, 16) : 0)
                     + (t.Note != null ? S(1) + Lines.Height(t.Note, inner, Face.SansMedium, 11, 15) : 0);
        var h = MathF.Max(tile, wordsH) + padY * 2;
        var dl = Kit.Dl;
        dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(Palette.WithAlpha(Palette.Hex(0x0d0b0a), 0.94f)), S(10));
        dl.AddRect(at + new Vector2(0.5f), at + new Vector2(w, h) - new Vector2(0.5f), Palette.U32(t.Kind == ToastKind.Bad ? Palette.DangerEdge : Palette.LineStrong), S(10));
        var tp = at + new Vector2(padX, (h - tile) / 2);
        switch (t.Kind)
        {
            case ToastKind.Seal or ToastKind.Opened:
                Ink.Seal(dl, tp + new Vector2(tile / 2), S(13), Shapes.Seed(t.Title + t.At.Ticks), t.Kind == ToastKind.Opened, null,
                    t.Kind == ToastKind.Opened ? Icon.Check : Icon.Feather, 1f, 1f, Palette.Hex(0x0d0b0a));
                break;
            default:
            {
                var (icon, ink, fill, edge) = t.Kind switch
                {
                    ToastKind.Quill => (Icon.Quote, Palette.Quill, Palette.QuillSoft, Palette.QuillLine),
                    ToastKind.Photo => (Icon.Camera, Palette.Muted, Palette.LeafRaised, Palette.Line),
                    ToastKind.Good => (Icon.Check, Palette.Good, Palette.SuccessWash, Palette.SuccessEdge),
                    ToastKind.Bad => (Icon.AlertTriangle, Palette.Danger, Palette.DangerWash, Palette.DangerEdge),
                    _ => (Icon.BookOpen, Palette.Ribbon, Palette.RibbonSoft, Palette.RibbonLine),
                };
                dl.AddRectFilled(tp, tp + new Vector2(tile), Palette.U32(Palette.Over(fill, Palette.Hex(0x0d0b0a))), S(7));
                dl.AddRect(tp + new Vector2(0.5f), tp + new Vector2(tile) - new Vector2(0.5f), Palette.U32(edge), S(7));
                Icons.Draw(dl, icon, tp + new Vector2((tile - S(14)) / 2), S(14), ink);
                break;
            }
        }
        var x = tp.X + tile + S(9);
        var y = at.Y + (h - wordsH) / 2;
        Txt.Line(dl, new Vector2(x, y), Txt.Fit(t.Title, Face.SansSemibold, 12, inner), Face.SansSemibold, 12, Palette.Ink);
        y += Txt.Natural(Face.SansSemibold, 12);
        if (t.Body != null) y += S(1) + Lines.Draw(dl, new Vector2(x, y + S(1)), inner, t.Body, bodyFace, 12, bodyColor, 16);
        if (t.Note != null) Lines.Draw(dl, new Vector2(x, y + S(1)), inner, t.Note, Face.SansMedium, 11, Palette.Muted, 15);
        Kit.Reserve(Kit.At, w, h);
        Kit.Gap(6);
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

    protected override float CardWidth => 370;

    protected override float Pad => 16;

    /// <summary>An index card: a warmer paper (#1b1713) with the quill's edge.</summary>
    protected override Vector4 Fill => Palette.Hex(0x1b1713);

    protected override Vector4 Edge => Palette.QuillLine;

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
        var at = Kit.At;
        var w = Kit.Width;
        // Your words' lines: the field's own line height (ImGui can't space a field's lines to 26px, so the rules follow
        // the words instead, each under a line of them), ruled across the whole card in the quill's faint gold.
        var line = Txt.Natural(Face.ReadItalic, 15);
        var fieldTop = at.Y + S(20) + S(12);
        var first = fieldTop + S(2) + line;
        var y0 = first - MathF.Floor((first - (win.Y + S(40))) / line) * line;
        dl.PushClipRect(win, win + new Vector2(winW, winH), false);
        for (var y = y0; y < win.Y + winH - S(6); y += line)
            dl.AddLine(new Vector2(win.X + 1, y), new Vector2(win.X + winW - 1, y), Palette.U32(Palette.Rgba(233, 196, 127, 0.1f)), 1f);
        // The red margin, 38px in.
        dl.AddLine(new Vector2(win.X + S(38), win.Y + 1), new Vector2(win.X + S(38), win.Y + winH - 1), Palette.U32(Palette.RibbonLine), 1f);
        dl.PopClipRect();

        Icons.Draw(dl, Icon.Feather, at + new Vector2(S(30), S(3)), S(14), Palette.Quill);
        Ink.Kicker(dl, at + new Vector2(S(30 + 14 + 8), S(4)), "A quick note", Palette.Quill, 10);
        if (Kit.IconButton("##note-x", new Vector2(win.X + winW - S(12) - S(22), win.Y + S(9)), Icon.X, "Close", 22, Btn.Quiet)) Later(Clear);
        Kit.Reserve(at, w, S(20));
        Kit.Gap(12);
        if (focus)
        {
            ImGui.SetKeyboardFocusHere();
            focus = false;
        }
        // Your words, on the lines, in candle gold (Literata's italic, 15px).
        var f = Kit.At;
        var fx = f.X + S(30);
        var fw = w - S(30);
        var fh = line * 4 + S(4);
        using (Txt.Fonts.Push(Face.ReadItalic, 15))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Palette.QuillInk);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, S(2)));
            ImGui.SetCursorScreenPos(new Vector2(fx, f.Y));
            ImGui.InputTextMultiline("##note-text", ref text, 2000, new Vector2(fw, fh), ImGuiInputTextFlags.None);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }
        if (text.Length == 0)
            Txt.Draw(dl, new Vector2(fx, f.Y + S(2)), "What's on your mind about the story…", Face.ReadItalic, 15, Palette.Rgba(243, 227, 194, 0.45f));
        Kit.Reserve(f, w, fh);
        Kit.Gap(8);

        // About: who you're looking at, as a chip you can type over.
        var about = Kit.At;
        var ch = S(21);
        Icons.Draw(dl, Icon.Users, about + new Vector2(S(30), (ch - S(12)) / 2), S(12), Palette.Muted);
        var lx = about.X + S(30 + 12 + 6);
        lx += Txt.Line(dl, new Vector2(lx, about.Y), "About", Face.Sans, 11, Palette.Muted, ch / S(1)) + S(6);
        var shown = subject.Length > 0 ? subject : "no one in particular";
        var cw = MathF.Min(about.X + w - lx, Txt.Measure(shown, Face.SansSemibold, 11).X + S(10) * 2 + S(8));
        dl.AddRectFilled(new Vector2(lx, about.Y), new Vector2(lx + cw, about.Y + ch), Palette.U32(Palette.LeafRaised), ch / 2);
        dl.AddRect(new Vector2(lx, about.Y) + new Vector2(0.5f), new Vector2(lx + cw, about.Y + ch) - new Vector2(0.5f), Palette.U32(Palette.Line), ch / 2);
        Kit.Field("##note-about", new Vector2(lx + S(10), about.Y), cw - S(10), ref subject, "no one in particular", null, 60, ImGuiInputTextFlags.None, Face.SansSemibold, 11, Palette.Body, true, 21);
        Kit.Reserve(about, w, ch);
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
