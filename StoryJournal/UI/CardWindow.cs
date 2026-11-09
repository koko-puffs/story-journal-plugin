using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// A card over the game: a small box of its own (no title bar: its head is drawn inside), sized to what it
/// holds, placed the first time where <see cref="FirstPlace"/> says (then where it's left). It doesn't
/// take focus when it appears, makes no sound and isn't closed by Esc unless it asks to be. What's
/// pressed runs after the frame's drawing.
///
/// It moves as the site's motion says (motion.html, "The plugin moves the same, more quietly"): it comes from
/// <see cref="From"/> (18px below for a page of the journal, 14px to the left for a margin note) and fades in
/// over 420ms (settle), and leaves toward <see cref="LeaveTo"/> over <see cref="LeaveSeconds"/> (quicker than it
/// came). Calm, or reduced motion: it simply appears and goes. A subclass says whether it's wanted with
/// <see cref="Want"/> from its PreOpenCheck, and draws its last card while it leaves.
/// </summary>
public abstract class CardWindow : Window, IDisposable
{
    protected readonly Journal journal;
    protected readonly Fonts fonts;
    private readonly List<Action> later = new();
    private int colors;
    private int vars;
    private bool disposed;

    private readonly Anim appear = new(Durations.Turn, Ease.Settle);
    private Anim leave = new(Durations.Leave, Ease.Leave);
    private bool leaving;
    private Vector2? rest;
    private int alphaPushed;
    private float alpha = 1f;

    /// <summary>Where it comes from (CSS px, from where it rests).</summary>
    protected virtual Vector2 From => new(0, 18);

    /// <summary>Where it goes as it leaves (CSS px).</summary>
    protected virtual Vector2 LeaveTo => new(0, -8);

    protected virtual float LeaveSeconds => Durations.Leave;

    /// <summary>Whether it's on its way out (drawing its last card).</summary>
    protected bool Leaving => leaving;

    /// <summary>
    /// Says whether the card's wanted now (from PreOpenCheck): opens it (it comes in), or lets it go (it leaves,
    /// then closes).
    /// </summary>
    protected void Want(bool wanted)
    {
        var now = Ink.Now;
        if (wanted)
        {
            if (!IsOpen || leaving)
            {
                leaving = false;
                appear.Start(now);
            }
            IsOpen = true;
            return;
        }
        if (!IsOpen) return;
        if (Ink.Still)
        {
            IsOpen = false;
            leaving = false;
            return;
        }
        if (!leaving)
        {
            leaving = true;
            leave = new Anim(LeaveSeconds, Ease.Leave);
            leave.Start(now);
        }
        else if (leave.Done(now))
        {
            leaving = false;
            IsOpen = false;
        }
    }

    protected CardWindow(string name, Journal journal, bool focusable = true)
        : base(name, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                     | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking
                     | (focusable ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoNav))
    {
        this.journal = journal;
        fonts = Ui.AcquireFonts();
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        DisableWindowSounds = true;
        AllowPinning = false;
        AllowClickthrough = false;
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>The card's width in CSS pixels.</summary>
    protected abstract float CardWidth { get; }

    protected virtual float Pad => 16;

    /// <summary>The padding on the left (a margin note's words start right of its rule).</summary>
    protected virtual float PadLeft => Pad;

    protected virtual Vector4 Edge => Palette.LineStrong;

    /// <summary>The card's fill (the leaf at 97%, as the mock-up's cards).</summary>
    protected virtual Vector4 Fill => Palette.WithAlpha(Palette.Card, 0.97f);

    /// <summary>Where it goes the first time: a point in the screen's fraction (0..1) for its top-left, and whether that's its centre instead.</summary>
    protected virtual (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.3f, 0.12f), false);

    /// <summary>Runs after this frame's drawing.</summary>
    protected void Later(Action a) => later.Add(a);

    public override void PreDraw()
    {
        var vp = ImGuiHelpers.MainViewport;
        var (at, centre) = FirstPlace;
        var pos = vp.Pos + vp.Size * at;
        if (centre) pos.X -= S(CardWidth) / 2;
        var now = Ink.Now;
        var still = Ink.Still;
        var coming = appear.Started && !appear.Done(now, still);
        var going = leaving && !still;
        if (coming || going)
        {
            var home = rest ?? pos;
            Vector2 offset;
            if (going)
            {
                var v = leave.Value(now, still);
                offset = LeaveTo * v;
                alpha = 1 - leave.Time(now, still);
            }
            else
            {
                var v = appear.Value(now, still);
                offset = From * (1 - v);
                alpha = MathF.Min(1, appear.Time(now, still) * 1.6f);
            }
            Position = home + new Vector2(S(offset.X), S(offset.Y));
            PositionCondition = ImGuiCond.Always;
        }
        else
        {
            alpha = 1;
            Position = rest ?? pos;
            PositionCondition = rest == null ? ImGuiCond.FirstUseEver : ImGuiCond.Appearing;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(alpha, 0.01f, 1f));
        alphaPushed = 1;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Fill);
        ImGui.PushStyleColor(ImGuiCol.Border, Edge);
        ImGui.PushStyleColor(ImGuiCol.Text, Palette.Ink);
        colors = 3;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, S(Palette.RadiusLg));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        vars = 3;
    }

    public override void PostDraw()
    {
        if (alphaPushed > 0) ImGui.PopStyleVar(alphaPushed);
        alphaPushed = 0;
        if (vars > 0) ImGui.PopStyleVar(vars);
        if (colors > 0) ImGui.PopStyleColor(colors);
        vars = colors = 0;
    }

    public override void Draw()
    {
        Kit.ResetFrame();
        Frame.ResetFrame();
        // Where it rests (it may have been dragged): not while it's moving in or out.
        if (!(appear.Started && !appear.Done(Ink.Now, Ink.Still)) && !leaving) rest = ImGui.GetWindowPos();
        var oldOpacity = Palette.Opacity;
        Palette.Opacity = oldOpacity * Math.Clamp(alpha, 0, 1);
        try
        {
            DrawInside();
        }
        finally
        {
            Palette.Opacity = oldOpacity;
        }
    }

    private void DrawInside()
    {
        Theme.PushInside();
        var origin = ImGui.GetCursorScreenPos();
        Kit.PushBox(origin.X + S(PadLeft), S(CardWidth - Pad - PadLeft));
        try
        {
            using (fonts.Push(Face.Sans, 14))
            {
                Kit.Reserve(origin, S(CardWidth), S(Pad - 2));
                DrawCard();
                Kit.Gap(Pad);
            }
        }
        finally
        {
            Frame.Unwind();
            Kit.PopBox();
            Theme.Pop();
        }
        var end = ImGui.GetCursorScreenPos();
        Kit.Reserve(new Vector2(origin.X, end.Y), S(CardWidth), 0);
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
                Services.Log.Error(ex, "Story Journal: an action from a card failed");
            }
        }
    }

    protected abstract void DrawCard();

    public virtual void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ReleaseFonts();
    }
}
