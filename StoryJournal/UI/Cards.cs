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
/// An arrival card: a sentence or two about a place the first time you reach it (every time, with the
/// whispers chatty), from the Codex. Never in combat, a duty or a cutscene (it waits for a calm
/// moment). More opens its Codex entry; Fewer of these turns the whispers down here.
/// </summary>
public sealed class ArrivalWindow : CardWindow
{
    public ArrivalWindow(Journal journal)
        : base("Story Journal · Arrival###SjArrival", journal, false)
    {
    }

    public Action<string>? OpenCodex { get; set; }

    protected override float CardWidth => 330;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.02f, 0.26f), false);

    public override void PreOpenCheck() => IsOpen = journal.Arrival != null;

    protected override void DrawCard()
    {
        if (journal.Arrival is not { } card) return;
        if (Parts.Head("##arrival-x", card.Place, true, card.First ? "· first visit" : null)) Later(() => journal.Arrival = null);
        Kit.Gap(6);
        Parts.Story(card.Whisper);
        Kit.Gap(10);
        var pressed = Parts.Buttons("##arrival-b", new (string, Btn, Icon?)[] { ("More", Btn.Glass, null), ("Fewer of these", Btn.Quiet, null) });
        if (pressed == 0)
        {
            var place = card.Place;
            Later(() =>
            {
                OpenCodex?.Invoke(place);
                journal.Arrival = null;
            });
        }
        else if (pressed == 1)
        {
            Later(() =>
            {
                journal.Config.WhispersHere = journal.Whispers == Whispers.Chatty ? (int)Whispers.Some : (int)Whispers.Quiet;
                journal.Config.Save();
                journal.Arrival = null;
                journal.AddToast("Fewer whispers", journal.Whispers == Whispers.Quiet ? "Only when you ask, from now on." : "Only new places, from now on.", "Settings changes it back", ToastKind.Plain);
            });
        }
    }
}

/// <summary>
/// Who's that?: target someone you've met and see when and where you met them, what the Codex says of
/// them so far, and what you wrote about them. Strangers show nothing.
/// </summary>
public sealed class WhoWindow : CardWindow
{
    public WhoWindow(Journal journal)
        : base("Story Journal · Who's that?###SjWho", journal, false)
    {
    }

    public Action<string>? OpenCodex { get; set; }

    protected override float CardWidth => 270;

    protected override float Pad => 14;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.44f, 0.22f), false);

    public override void PreOpenCheck() => IsOpen = journal.Who != null;

    protected override void DrawCard()
    {
        if (journal.Who is not { } who) return;
        if (Parts.Head("##who-x", "Who's that?")) Later(() => journal.Who = null);
        Kit.Gap(6);
        Lines.Para(who.Entry?.Name ?? who.Name, Palette.Ink, 14, 19, Face.SansSemibold);
        if (who.Layers.Count > 0)
        {
            Kit.Gap(2);
            Parts.Quiet(who.Layers[0].Text);
        }
        Kit.Gap(6);
        var where = who.Met.Place.Length > 0 ? $", in {who.Met.Place}" : string.Empty;
        var extra = new List<string>();
        if (who.Lines > 0) extra.Add(who.Lines == 1 ? "you saved a line of theirs" : $"you saved {who.Lines} lines of theirs");
        if (who.Notes.Count > 0) extra.Add(who.Notes.Count == 1 ? "wrote a note about them" : $"wrote {who.Notes.Count} notes about them");
        Lines.Para($"Met {Ui.Ago(who.Met.At)}{where}." + (extra.Count > 0 ? $" You {Core.Welcome.Join(extra)}." : string.Empty), Palette.Soft, 13, 19);
        foreach (var n in who.Notes.Take(2))
        {
            Kit.Gap(6);
            Parts.Quote($"“{n.Body}”");
        }
        if (who.Entry != null)
        {
            Kit.Gap(8);
            var at = Kit.At;
            if (Kit.Link("##who-codex", at, "Codex card", 12))
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

    private static void DrawToast(Toast t, DateTime now)
    {
        var left = 6 - (now - t.At).TotalSeconds;
        var old = Palette.Opacity;
        Palette.Opacity = ReducedMotion ? 1 : (float)Math.Clamp(left / 0.4, 0, 1);
        var at = Kit.At;
        var w = Kit.Width;
        var inner = w - S(14) * 2 - S(28);
        var bodyColor = t.Kind == ToastKind.Quill ? Palette.Quill : Palette.Soft;
        var h = S(12) * 2 + Txt.Natural(Face.SansSemibold, 13)
                + (t.Body != null ? S(2) + Lines.Height(t.Body, inner, Face.Sans, 13, 18) : 0)
                + (t.Note != null ? S(2) + Lines.Height(t.Note, inner, Face.Sans, 11, 16) : 0);
        var dl = Kit.Dl;
        var edge = t.Kind switch
        {
            ToastKind.Good => Palette.SuccessEdge,
            ToastKind.Bad => Palette.DangerEdge,
            ToastKind.Quill => Palette.WithAlpha(Palette.Quill, 0.45f),
            _ => Palette.AccentEdge,
        };
        dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(Palette.WithAlpha(Palette.Over(Palette.Float, Palette.Ground), 0.96f)), S(14));
        dl.AddRect(at, at + new Vector2(w, h), Palette.U32(edge), S(14));
        var icon = t.Kind switch
        {
            ToastKind.Quill => Icon.Quote,
            ToastKind.Good => Icon.Check,
            ToastKind.Bad => Icon.AlertTriangle,
            _ => Icon.BookOpen,
        };
        var ink = t.Kind switch
        {
            ToastKind.Good => Palette.Success,
            ToastKind.Bad => Palette.Danger,
            ToastKind.Quill => Palette.Quill,
            _ => Palette.Accent,
        };
        Icons.Draw(dl, icon, at + new Vector2(S(14), S(13)), S(18), ink);
        var x = at.X + S(14) + S(28);
        var y = at.Y + S(12);
        Txt.Line(dl, new Vector2(x, y), Txt.Fit(t.Title, Face.SansSemibold, 13, inner), Face.SansSemibold, 13, Palette.Ink);
        y += Txt.Natural(Face.SansSemibold, 13);
        if (t.Body != null) y += S(2) + Lines.Draw(dl, new Vector2(x, y + S(2)), inner, t.Body, Face.Sans, 13, bodyColor, 18);
        if (t.Note != null) Lines.Draw(dl, new Vector2(x, y + S(2)), inner, t.Note, Face.Sans, 11, Palette.Muted, 16);
        Kit.Reserve(at, w, h);
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
/// The quick note (its key, or <c>/sj note</c> on its own): a line kept in this session's entry, or
/// sealed as a theory to settle later.
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

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.3f), true);

    /// <summary>Opens it, about someone or something when given.</summary>
    public void Show(string about = "")
    {
        subject = about;
        focus = true;
        IsOpen = true;
        BringToFront();
    }

    protected override void DrawCard()
    {
        if (Parts.Head("##note-x", subject.Length > 0 ? $"A note about {subject}" : "Quick note")) Later(() => IsOpen = false);
        Kit.Gap(10);
        if (focus)
        {
            ImGui.SetKeyboardFocusHere();
            focus = false;
        }
        Parts.Field("##note-text", ref text, "What's on your mind about the story…", 2000);
        Kit.Gap(8);
        Parts.Quiet(journal.Session != null ? "Saved to this session's entry; a question (ending in ?) stays a loose thread until it's answered." : "Saved to your journal.");
        Kit.Gap(10);
        var empty = text.Trim().Length == 0;
        var pressed = Parts.Buttons("##note-b", new (string, Btn, Icon?)[]
        {
            ("Save note", empty ? Btn.Glass : Btn.Primary, null),
            ("Seal as theory", Btn.Glass, Icon.Stamp),
            ("→Cancel", Btn.Quiet, null),
        });
        if (pressed == 0 && !empty)
        {
            var body = text;
            var about = subject;
            Later(() =>
            {
                if (journal.AddNote(body, about)) Clear();
            });
        }
        else if (pressed == 1 && !empty)
        {
            var body = text;
            var about = subject;
            Later(() =>
            {
                if (journal.SealTheory(body, 10, about)) Clear();
            });
        }
        else if (pressed == 2) Later(Clear);
    }

    private void Clear()
    {
        text = string.Empty;
        subject = string.Empty;
        IsOpen = false;
    }
}
