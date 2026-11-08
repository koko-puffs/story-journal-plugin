using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// A card over the game: a small box of its own (no title bar: its head is drawn inside), sized to what it
/// holds, placed the first time where <see cref="FirstPlace"/> says (then where it's left). It doesn't
/// take focus when it appears, makes no sound and isn't closed by Esc unless it asks to be. What's
/// pressed runs after the frame's drawing.
/// </summary>
public abstract class CardWindow : Window, IDisposable
{
    protected readonly Journal journal;
    protected readonly Fonts fonts;
    private readonly List<Action> later = new();
    private int colors;
    private int vars;
    private bool disposed;

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

    protected virtual Vector4 Edge => Palette.Line;

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
        Position = pos;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Palette.WithAlpha(Palette.Card, 0.97f));
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
        if (vars > 0) ImGui.PopStyleVar(vars);
        if (colors > 0) ImGui.PopStyleColor(colors);
        vars = colors = 0;
    }

    public override void Draw()
    {
        Kit.ResetFrame();
        Frame.ResetFrame();
        Theme.PushInside();
        var origin = ImGui.GetCursorScreenPos();
        Kit.PushBox(origin.X + S(Pad), S(CardWidth - Pad * 2));
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
