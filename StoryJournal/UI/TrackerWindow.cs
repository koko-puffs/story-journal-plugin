using System;
using System.Collections.Generic;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Game;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// The small tracker under the game's own quest list: how far through the expansion, the next duty
/// (its name only as far as the spoiler setting allows: never on strict), and the pace lock's warning
/// when a partner asked you to wait. Drag it where you like; its book opens the journal. Off in Settings.
/// </summary>
public sealed class TrackerWindow : CardWindow
{
    private readonly Memo<(int Done, int Total, int Ex)> head = new();
    /// <summary>The next duty's line, made again when the duty, how far it is or the spoiler setting changes.</summary>
    private readonly Memo<(int Away, int Spoil, string Duty)> next = new();

    public TrackerWindow(Journal journal)
        : base("Story Journal · Tracker###SjTracker", journal, false)
    {
    }

    public Action? Open { get; set; }

    protected override float CardWidth => 240;

    protected override float Pad => 10;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.78f, 0.36f), false);

    public override void PreOpenCheck() =>
        IsOpen = journal.Config.ShowTracker && journal.Player != null && journal.Story.Now is { Progress.Expansions.Count: > 0 };

    protected override void DrawCard()
    {
        if (journal.Story.Now is not { } story || story.Progress.ExpansionNow is not { } ex) return;
        var at = Kit.At;
        var w = Kit.Width;
        Parts.Mark(at + new Vector2(0, S(2)), S(14));
        var words = head.Get((ex.Done, ex.Total, ex.Id), v => $"{ex.Name} · {Progress.Percent(v.Done, v.Total)}%");
        var (clicked, hovered, _) = Kit.Hit("##tracker-open", at, new Vector2(w, S(18)));
        Txt.Line(Kit.Dl, new Vector2(at.X + S(20), at.Y), Txt.Fit(words, Face.SansSemibold, 13, w - S(20)), Face.SansSemibold, 13, hovered ? Palette.White : Palette.Ink, 18);
        Kit.Reserve(at, w, S(18));
        if (clicked && Open != null) Later(Open);
        Kit.Gap(6);
        var a2 = Kit.At;
        Kit.Progress(a2, w, ex.Total > 0 ? (float)ex.Done / ex.Total : 0, 5);
        Kit.Reserve(a2, w, S(5));
        Kit.Gap(6);
        var ahead = story.Progress.Ahead;
        string line;
        if (story.Progress.Complete) line = "The main story is done. Well played.";
        else if (ahead.Count == 0) line = story.Current?.Name ?? "Reading the main story…";
        else
        {
            var a = ahead[0];
            line = next.Get((a.QuestsAway, (int)journal.Prefs.Spoilers, a.Duty), v => v.Spoil == (int)Spoilers.Strict
                ? (v.Away <= 1 ? "A duty after this quest" : $"{v.Away} quests to the next duty")
                : (v.Away <= 1 ? $"{v.Duty} after this quest" : $"{v.Duty} in {v.Away} quests"));
        }
        Lines.Para(line, Palette.Muted, 12, 17);

        if (journal.Config.TrackerPartners && journal.Sync.Hello is { Partners.Count: > 0 } hello)
        {
            var index = story.Progress.Index;
            foreach (var warning in PaceLock.Check(hello.Partners, index, story.NextIndex))
            {
                Kit.Gap(6);
                var b = Kit.At;
                var text = PaceLock.Words(warning, index);
                var h = Lines.Height(text, w - S(26), Face.Sans, 12, 17) + S(8);
                Kit.Dl.AddRectFilled(b, b + new Vector2(w, h), Palette.U32(Palette.AccentWash), S(8));
                Kit.Dl.AddRect(b, b + new Vector2(w, h), Palette.U32(Palette.AccentEdge), S(8));
                Icons.Draw(Kit.Dl, Icon.Lock, b + new Vector2(S(7), S(5)), S(12), Palette.Accent);
                Lines.Draw(Kit.Dl, b + new Vector2(S(24), S(4)), w - S(28), text, Face.Sans, 12, Palette.AccentSoft, 17);
                Kit.Reserve(b, w, h);
            }
        }
        if (journal.Config.Paused || InGame.InDuty)
        {
            Kit.Gap(4);
            Lines.Para(journal.Config.Paused ? "Recording paused · /sj resume" : "In a duty", Palette.Faint, 11, 15);
        }
    }
}
