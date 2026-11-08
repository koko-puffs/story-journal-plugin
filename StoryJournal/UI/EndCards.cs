using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// The end of a session (at logout, or <c>/sj end</c>): the evening in numbers and how far it moved you,
/// a title suggested from what happened (type over it, or keep it), how it felt, and a thought for
/// next time (it comes back on the Welcome back card). Save sends them with the session; Skip doesn't.
/// </summary>
public sealed class WrapUpWindow : CardWindow
{
    private Guid shownFor;
    private string title = string.Empty;
    private string note = string.Empty;
    private readonly HashSet<int> moods = new();

    public WrapUpWindow(Journal journal)
        : base("Story Journal · Tonight's chapter###SjWrapUp", journal)
    {
    }

    protected override float CardWidth => 420;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.1f), true);

    public override void PreOpenCheck() => IsOpen = journal.WrapUp != null;

    protected override void DrawCard()
    {
        if (journal.WrapUp is not { } card) return;
        if (card.Session.Id != shownFor)
        {
            shownFor = card.Session.Id;
            title = card.Title ?? string.Empty;
            note = string.Empty;
            moods.Clear();
        }
        if (Parts.Head("##wrap-x", "Tonight's chapter")) Later(() => journal.WrapUp = null);
        Kit.Gap(12);
        var length = (card.Session.EndedAt ?? card.Session.LastAt) - card.Session.StartedAt;
        Kit.Facts("##wrap-facts", new Kit.Fact[]
        {
            new("Played", Ui.Duration(length)),
            new("Quests", Ui.Count(card.Numbers.Quests)),
            new("Cutscenes", Ui.Count(card.Numbers.Cutscenes)),
            new("Photos", Ui.Count(card.Numbers.Photos)),
        });
        if (card.ExpansionName.Length > 0)
        {
            Kit.Gap(10);
            Parts.Bar(card.PercentNow / 100f, card.PercentBefore == card.PercentNow
                ? $"{card.ExpansionName} · {card.PercentNow}%"
                : $"{card.ExpansionName} · {card.PercentBefore}% → {card.PercentNow}%");
        }
        Parts.Heading("TITLE", 14);
        Parts.Field("##wrap-title", ref title, "Give tonight a name", 80);
        Kit.Gap(4);
        Parts.Quiet(card.Title != null ? "Suggested from tonight. Type over it, or keep it." : "The Chronicle and the Storybook use it as the chapter's name.");
        Parts.Heading("HOW WAS IT?", 12);
        var flipped = Parts.PickChips("##wrap-mood", Sessions.MoodChoices, i => moods.Contains(i));
        if (flipped >= 0 && !moods.Remove(flipped) && moods.Count < 6) moods.Add(flipped);
        Parts.Heading("A THOUGHT FOR NEXT TIME", 12);
        Parts.Field("##wrap-note", ref note, "It comes back first thing next time", 2000);
        Kit.Gap(14);
        var pressed = Parts.Buttons("##wrap-b", new (string, Btn, Icon?)[]
        {
            ("Save", Btn.Primary, null),
            ("Open the entry", Btn.Glass, Icon.ExternalLink),
            ("→Skip", Btn.Quiet, null),
        });
        switch (pressed)
        {
            case 0:
            {
                var t = title;
                var n = note;
                var m = moods.OrderBy(i => i).Select(i => Sessions.MoodChoices[i]).ToList();
                Later(() => journal.SaveWrapUp(card, t, m, n));
                break;
            }
            case 1:
                Dalamud.Utility.Util.OpenLink(SiteLink.SessionUrl(card.Session.Id));
                break;
            case 2:
                Later(() => journal.WrapUp = null);
                break;
        }
    }
}

/// <summary>
/// A chapter closed: a card worth keeping, with the chapter's name, the last words of its last quest,
/// how many quests it held, and the theories whose time has come, to settle (called it, missed it,
/// changed my mind). A photo for the chapter's page from here; or later.
/// </summary>
public sealed class MilestoneWindow : CardWindow
{
    public MilestoneWindow(Journal journal)
        : base("Story Journal · Chapter closed###SjMilestone", journal)
    {
    }

    protected override float CardWidth => 440;

    protected override Vector4 Edge => Palette.Accent;

    protected override (Vector2 At, bool Centre) FirstPlace => (new Vector2(0.5f, 0.12f), true);

    public override void PreOpenCheck() => IsOpen = journal.Milestone != null;

    protected override void DrawCard()
    {
        if (journal.Milestone is not { } card) return;
        if (Parts.Head("##mile-x", card.ExpansionDone ? "Expansion finished" : "Chapter closed")) Later(() => journal.Milestone = null);
        Kit.Gap(14);
        var caps = (card.Expansion.Length > 0 ? card.Expansion + " · " : string.Empty).ToUpperInvariant() + "CHAPTER CLOSED";
        Centred(caps, Face.Mono, 11, Palette.Muted);
        Kit.Gap(6);
        foreach (var line in Lines.Of(card.Chapter, Kit.Width, Face.SansSemibold, 22)) Centred(line, Face.SansSemibold, 22, Palette.White);
        if (card.LastLine is { Length: > 0 } last)
        {
            Kit.Gap(8);
            foreach (var line in Lines.Of(last, Kit.Width - S(40), Face.Sans, 14)) Centred(line, Face.Sans, 14, Palette.Soft);
        }
        Kit.Gap(10);
        Centred(card.Quests > 0 ? $"{Ui.Plural(card.Quests, "quest", "quests")} in this chapter · the last, {card.Quest}" : card.Quest, Face.Sans, 12, Palette.Muted);
        if (card.Theories.Count > 0)
        {
            Parts.Rule(12);
            Parts.Heading("THEORIES TO SETTLE", 0);
            var i = 0;
            foreach (var t in card.Theories.Take(3))
            {
                Parts.Quote($"“{t.Body}”", t.SealedQuest.Length > 0 ? $"sealed during {t.SealedQuest}" : null);
                Kit.Gap(6);
                var pressed = Parts.Buttons(Ids.Of("##mile-t", i++), new (string, Btn, Icon?)[]
                {
                    ("Called it", Btn.Glass, Icon.Check), ("Missed it", Btn.Glass, null), ("Changed my mind", Btn.Quiet, null),
                }, BtnSize.Tiny);
                if (pressed >= 0)
                {
                    var status = pressed switch { 0 => "called", 1 => "missed", _ => "changed" };
                    var id = t.Id;
                    Later(() =>
                    {
                        journal.AnswerTheory(id, status);
                        if (journal.Milestone is { } m) journal.Milestone = m with { Theories = m.Theories.Where(x => x.Id != id).ToList() };
                    });
                }
                Kit.Gap(10);
            }
        }
        Kit.Gap(12);
        var b = Parts.Buttons("##mile-b", new (string, Btn, Icon?)[]
        {
            ("Take a photo for the page", Btn.Primary, Icon.Camera),
            ("→Later", Btn.Quiet, null),
        });
        if (b == 0)
        {
            var caption = card.Chapter;
            Later(() =>
            {
                journal.Milestone = null;
                journal.TakePhoto(caption);
            });
        }
        else if (b == 1) Later(() => journal.Milestone = null);
    }

    private static void Centred(string text, Face face, int px, Vector4 color)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var shown = Txt.Fit(text, face, px, w);
        var tw = Txt.Measure(shown, face, px).X;
        var h = Txt.Natural(face, px);
        Txt.Draw(Kit.Dl, new Vector2(at.X + (w - tw) / 2, at.Y), shown, face, px, color);
        Kit.Reserve(at, w, h + S(2));
    }
}
