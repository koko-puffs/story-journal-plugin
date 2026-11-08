using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>The journal window's tabs.</summary>
public enum MainTab
{
    Now,
    Session,
    Codex,
    Notes,
    Theories,
    Settings,
}

/// <summary>
/// The journal (<c>/sj</c>), in the look of Koko's plugins and Story Journal's cherry red. Six tabs:
/// Now (where you are, why you're here, who's in it, what's ahead, a quick note), This session (what's
/// been written tonight), Codex (what the world has told you so far, and Ask the journal), Notes,
/// Theories, and Settings. Everything it shows is read from <see cref="Journal"/> each frame; what you
/// press goes back to it after the frame's drawing is done.
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
        Size = new Vector2(520, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 420), MaximumSize = new Vector2(1400, 2400) };
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

    /// <summary>Opens the Codex on an entry or a name ("More" on an arrival card, Who's that?'s Codex card).</summary>
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
            var screenH = ImGui.GetMainViewport().Size.Y;
            var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            if (screenH > 0 && scale > 0 && Size is { } size && size.Y * scale > screenH * 0.9f)
                Size = new Vector2(size.X, MathF.Max(420, screenH * 0.9f / scale));
        }
        Theme.PushFrame();
    }

    public override void PostDraw() => Theme.Pop();

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
                    DrawHeader();
                    Frame.BeginBody("##sj-body", null, 18, 24);
                    switch (tab)
                    {
                        case MainTab.Session:
                            DrawSession();
                            break;
                        case MainTab.Codex:
                            DrawCodex();
                            break;
                        case MainTab.Notes:
                            DrawNotes();
                            break;
                        case MainTab.Theories:
                            DrawTheories();
                            break;
                        case MainTab.Settings:
                            DrawSettings();
                            break;
                        default:
                            DrawNow();
                            break;
                    }
                    Frame.EndBody();
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

    // ---------------------------------------------------------------------------------------------
    // The header: the mark, the name, the link on the right, the tabs under them.

    private readonly TabSpec[] tabs = { new("Now"), new("This session"), new("Codex"), new("Notes"), new("Theories"), new("Settings") };
    private int tabsEvents = -1;
    private int tabsDue = -1;

    private void UpdateTabs()
    {
        var events = journal.SessionEvents.Count;
        if (events != tabsEvents)
        {
            tabsEvents = events;
            tabs[1] = new TabSpec("This session", events > 0 ? Ui.Count(events) : null);
        }
        var due = Due().Count;
        if (due != tabsDue)
        {
            tabsDue = due;
            tabs[4] = new TabSpec("Theories", due > 0 ? $"{due} due" : null);
        }
    }

    private void DrawHeader()
    {
        Frame.BeginHeader(16);
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        Parts.Mark(at, S(32));
        var tx = at.X + S(32 + 12);
        var (linkText, linkColor) = LinkWords();
        var linkW = Txt.Measure(linkText, Face.Sans, 12).X + S(14);
        Txt.Line(dl, new Vector2(tx, at.Y), Txt.Fit("Story Journal", Face.SansSemibold, 18, w - (tx - at.X) - linkW - S(12)), Face.SansSemibold, 18, Palette.Ink, 32);
        var lx = at.X + w - linkW;
        Kit.Dot(new Vector2(lx + S(3.5f), at.Y + S(16)), linkColor);
        Txt.Line(dl, new Vector2(lx + S(14), at.Y), linkText, Face.Sans, 12, journal.Sync.Link is LinkState.Linked ? Palette.Success : Palette.Muted, 32);
        Kit.Reserve(at, w, S(32));
        Kit.Gap(14);
        UpdateTabs();
        // Narrow: the tabs' own words would run past the edge, so they go shorter.
        var total = S(8) + S(3) * (tabs.Length - 1);
        foreach (var t in tabs) total += Kit.TabWidth(t);
        var shown = total <= w ? tabs : new[] { new TabSpec("Now"), new TabSpec("Session", tabs[1].Count), new TabSpec("Codex"), new TabSpec("Notes"), new TabSpec("Theories", tabs[4].Count), new TabSpec("Settings") };
        var picked = Kit.Tabs("##sj-tabs", shown, (int)tab);
        if (picked != (int)tab) tab = (MainTab)picked;
        Frame.EndHeader();
    }

    private (string Text, Vector4 Dot) LinkWords()
    {
        if (journal.Sync.SwitchedOff) return ("Switched off on the site", Palette.Warn);
        return journal.Sync.Link switch
        {
            LinkState.Linked => ("kokos-tools.com", Palette.Success),
            LinkState.Linking => ("Linking…", Palette.Accent),
            LinkState.Refused => ("Link refused", Palette.Danger),
            _ => ("Not linked", Palette.Faint),
        };
    }

    /// <summary>A box with an icon well, a title and words, for an empty tab or a state to say.</summary>
    private static void Empty(string id, Icon icon, string title, string words)
    {
        Kit.BeginPanel(id, 16, 18, 16);
        var at = Kit.At;
        var w = Kit.Width;
        Kit.IconWell(at, S(36), icon, quiet: true);
        var x = at.X + S(36 + 14);
        Txt.Line(Kit.Dl, new Vector2(x, at.Y), Txt.Fit(title, Face.SansSemibold, 15, w - (x - at.X)), Face.SansSemibold, 15, Palette.Ink, 20);
        var h = S(20) + Lines.Draw(Kit.Dl, new Vector2(x, at.Y + S(22)), w - (x - at.X), words, Face.Sans, 13, Palette.Muted, 19) + S(2);
        Kit.Reserve(at, w, MathF.Max(S(36), h));
        Kit.EndPanel();
    }
}
