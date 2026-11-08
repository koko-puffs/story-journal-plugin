using System;
using System.Linq;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// Welcome back: the first thing after you log in when you've been away as long as the site's
/// preference says (a day, unless set). Three lines of story at a glance, the next step with the flag
/// on your map in one click, your last thought from the end of a session (or your last unresolved
/// note), and the way to the longer recap and the people in it. It closes itself as you walk away.
/// </summary>
public sealed class WelcomeWindow : CardWindow
{
    public WelcomeWindow(Journal journal)
        : base("Story Journal · Welcome back###SjWelcome", journal)
    {
    }

    /// <summary>Opens the main window on a tab (Who's who: the Codex).</summary>
    public Action<MainTab>? Open { get; set; }

    protected override float CardWidth => 400;

    public override void PreOpenCheck() => IsOpen = journal.Welcome != null;

    protected override void DrawCard()
    {
        if (journal.Welcome is not { } card) return;
        if (Parts.Head("##welcome-x", "Welcome back")) Later(() => journal.Welcome = null);
        Kit.Gap(4);
        var sessions = card.Sessions > 0 ? $" · {Ui.Plural(card.Sessions, "recent session", "recent sessions")}" : string.Empty;
        Parts.Quiet($"Last played {Ui.Day(card.LastPlayed)}, {Ui.Ago(card.LastPlayed)}{sessions}");

        Parts.Heading("PREVIOUSLY", 12);
        if (card.Lines.Count > 0) Parts.Bullets(card.Lines);
        else Parts.Quiet("Nothing written yet: your next sessions will fill this.");

        Parts.Rule(10);
        Parts.Heading("NEXT", 0);
        if (card.Quest != null)
        {
            Lines.Para(card.Quest, Palette.Ink, 14, 20, Face.SansSemibold);
            if (card.Next.Count > 0) Parts.Story(string.Join(" ", card.Next));
            else Parts.Quiet("Take it from the scenario guide.");
        }
        else Parts.Quiet("The main story's next step will show here once it's read.");
        if (card.Note is { } note)
        {
            Kit.Gap(8);
            Parts.Quote($"Your note: “{note.Body}”");
        }
        Kit.Gap(12);
        var pressed = Parts.Buttons("##welcome-b", new (string, Btn, Icon?)[]
        {
            ("Flag on map", Btn.Primary, Icon.Flag),
            ("Longer recap", Btn.Glass, null),
            ("Who's who", Btn.Glass, null),
            ("→Got it", Btn.Quiet, null),
        });
        switch (pressed)
        {
            case 0:
                Later(journal.FlagNext);
                break;
            case 1:
                Dalamud.Utility.Util.OpenLink(SiteLink.RecapUrl);
                break;
            case 2:
                Later(() => Open?.Invoke(MainTab.Codex));
                break;
            case 3:
                Later(() => journal.Welcome = null);
                break;
        }
        Kit.Gap(10);
        var at = Kit.At;
        var hours = journal.Prefs.WelcomeHours;
        var (clicked, hovered, _) = Kit.Hit("##welcome-show", at, new System.Numerics.Vector2(Kit.Width, S(18)));
        Kit.TickBox(at, journal.Config.ShowWelcome, hovered);
        var words = hours <= 0 ? "Show every time I log in (set on the site)" : $"Show after {Ui.Plural(hours, "hour", "hours")} or more away (set on the site)";
        Txt.Line(Kit.Dl, new System.Numerics.Vector2(at.X + S(26), at.Y), Txt.Fit(words, Face.Sans, 12, Kit.Width - S(26)), Face.Sans, 12, Palette.Muted, 18);
        Kit.Reserve(at, Kit.Width, S(18));
        if (clicked)
        {
            journal.Config.ShowWelcome = !journal.Config.ShowWelcome;
            journal.Config.Save();
        }
    }
}
