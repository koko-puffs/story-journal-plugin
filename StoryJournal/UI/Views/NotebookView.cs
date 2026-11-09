using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    private string noteText = string.Empty;
    private string noteSubject = string.Empty;
    private string theoryText = string.Empty;
    private int theoryAfter = 10;
    private bool notebookTheories = true;
    private bool composing;

    /// <summary>The tabs' red line: where it was and where it goes (220ms settle).</summary>
    private readonly Anim notebookLine = new(Durations.Step, Ease.Settle);
    private (float X, float W) lineFrom;
    private (float X, float W) lineTo = (float.NaN, 0);

    private static readonly string[] AfterWords = { "Don't ask", "In 5 quests", "In 10", "In 25" };
    private static readonly int[] AfterValues = { 0, 5, 10, 25 };

    /// <summary>The Notebook's count on the rail: theories due to settle, else those still sealed.</summary>
    private int NotebookCount()
    {
        var due = Due().Count;
        if (due > 0) return due;
        return journal.Memory?.Theories.Count(t => !t.Answered) ?? 0;
    }

    /// <summary>The Notebook, as the site calls it: theories under wax (opened ones cracked) and your notes; the site keeps the whole of it.</summary>
    private void DrawNotebook()
    {
        var mine = journal.Memory?.Theories ?? new List<SealedTheory>();
        var due = Due();
        var theories = due.Count + mine.Count(t => !due.Any(d => d.Id == t.Id));
        var notes = (journal.Sync.Hello?.Notes.Count ?? 0) + journal.Config.PendingNotes.Count;

        // The tabs, the red line under the open one sliding over.
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var x = at.X;
        var tabs = new (string Label, int Count, bool On)[] { ("Theories", theories, notebookTheories), ("Notes", notes, !notebookTheories) };
        (float X, float W) on = (0, 0);
        for (var i = 0; i < tabs.Length; i++)
        {
            var (label, count, isOn) = tabs[i];
            // The site's tabs: Geist 600 14px, muted (ink when open), the count after in faint 500.
            var lw = Txt.Measure(label, Face.SansSemibold, 14).X;
            var cw = Txt.Measure(Ui.Count(count), Face.SansMedium, 14).X;
            var tw = lw + S(6) + cw;
            var (clicked, hovered, _) = Kit.Hit(Ids.Of("##nb-tab", i), new Vector2(x, at.Y), new Vector2(tw, S(34)));
            Txt.Line(dl, new Vector2(x, at.Y), label, Face.SansSemibold, 14, isOn || hovered ? Palette.Ink : Palette.Muted, 30);
            Txt.Line(dl, new Vector2(x + lw + S(6), at.Y), Ui.Count(count), Face.SansMedium, 14, Palette.Faint, 30);
            if (isOn) on = (x, tw);
            var theoriesTab = i == 0;
            if (clicked) Later(() => notebookTheories = theoriesTab);
            x += tw + S(22);
        }
        dl.AddLine(new Vector2(at.X, at.Y + S(34) + 0.5f), new Vector2(at.X + w, at.Y + S(34) + 0.5f), Palette.U32(Palette.Line), 1f);
        if (float.IsNaN(lineTo.X)) lineFrom = lineTo = on;
        else if (MathF.Abs(on.X - lineTo.X) > 0.5f)
        {
            lineFrom = Line();
            lineTo = on;
            notebookLine.Start(Ink.Now);
        }
        var (lx, lwid) = Line();
        dl.AddRectFilled(new Vector2(lx, at.Y + S(33)), new Vector2(lx + lwid, at.Y + S(35)), Palette.U32(Palette.Ribbon), S(1));
        Kit.Reserve(at, w, S(36));
        Kit.Gap(16);

        if (notebookTheories) DrawTheories(mine, due);
        else DrawNotes();
    }

    private (float X, float W) Line()
    {
        if (!notebookLine.Started) return lineTo;
        var v = notebookLine.Value(Ink.Now, Ink.Still);
        return (lineFrom.X + (lineTo.X - lineFrom.X) * v, lineFrom.W + (lineTo.W - lineFrom.W) * v);
    }

    private void DrawTheories(List<SealedTheory> mine, List<DueTheory> due)
    {
        var index = journal.Story.Now?.Progress.Index;
        var i = 0;

        // Due: the story's come far enough; settle them.
        foreach (var t in due)
        {
            TheoryCard(Ids.Of("##nb-due", i), Shapes.Seed(t.Id.ToString()), t.Body,
                $"Sealed {Day(t.SealedAt)}{(t.SealedQuest.Length > 0 ? $" · {t.SealedQuest}" : string.Empty)} · time to settle it", null, false, null);
            Kit.Gap(6);
            Kit.PushBox(Kit.Left + S(16), Kit.Width - S(16));
            var pressed = Parts.Buttons(Ids.Of("##nb-settle", i), TheoryAnswers.Settled
                .Select(s => (TheoryAnswers.Button(s), s == TheoryAnswers.Changed ? Btn.Quiet : Btn.Glass, s == TheoryAnswers.Called ? (Icon?)Icon.Check : null)).ToList(), BtnSize.Tiny);
            Kit.PopBox();
            if (TheoryAnswers.At(pressed) is { } status)
            {
                var id = t.Id;
                Later(() =>
                {
                    journal.AnswerTheory(id, status);
                    dueAt = DateTime.MinValue;
                });
            }
            Kit.Gap(14);
            i++;
        }

        // Sealed here, newest first: still under wax, then opened (cracked, with how it went).
        foreach (var t in mine.Where(t => !due.Any(d => d.Id == t.Id)).OrderBy(t => t.Answered).ThenByDescending(t => t.SealedAt).Take(40))
        {
            var meta = $"Sealed {Day(t.SealedAt)}";
            if (t.SealedQuest.Length > 0) meta += $" · {t.SealedQuest}";
            if (!t.Answered)
            {
                if (!t.Sent) meta += " · waiting to reach the site";
                else if (t.DueIndex > 0 && index is { } now && t.DueIndex > now) meta += $" · opens in about {Ui.Plural(t.DueIndex - now, "quest", "quests")}";
                else if (t.DueIndex <= 0) meta += " · “don’t ask me”, settle it yourself";
            }
            // Still (motion.html: a milestone's is the only seal pressed in game).
            TheoryCard(Ids.Of("##nb-th", i++), Shapes.Seed(t.Id.ToString()), t.Body, meta, t.Answered ? t.Status : null, t.Answered, null);
            Kit.Gap(10);
        }
        if (due.Count == 0 && mine.Count == 0) Parts.Quiet("No theories yet. Write what you think will happen: it's sealed with where you are, and the story answers it.");

        // Writing one.
        Kit.Gap(6);
        if (composing)
        {
            Kit.BeginPanel("##nb-compose", 16, 16, 16, Palette.Over(Palette.QuillSoft, Palette.Leaf), Palette.QuillLine, Palette.RadiusMd);
            Ink.KickerRow("A new theory", Palette.Quill);
            Kit.Gap(6);
            var a = Kit.At;
            Kit.Field("##th-text", a, Kit.Width, ref theoryText, "What do you think will happen?", null, 400, ImGuiInputTextFlags.None, Face.ReadItalic, 14, Palette.QuillInk,
                false, 36, Palette.QuillLine);
            Kit.Reserve(a, Kit.Width, S(36));
            Kit.Gap(8);
            var b = Kit.At;
            var picked = Array.IndexOf(AfterValues, theoryAfter);
            var next = Kit.Segmented("##th-after", b, AfterWords, picked < 0 ? 2 : picked, 0, Kit.Width);
            if (next >= 0 && next < AfterValues.Length) theoryAfter = AfterValues[next];
            Kit.Reserve(b, Kit.Width, Kit.SegmentedHeight);
            Kit.Gap(8);
            Parts.Quiet("Sealed with where you are in the story; you can't change it after. The plugin asks how it went once you've played that far.");
            Kit.Gap(10);
            var empty = theoryText.Trim().Length == 0;
            var pressedB = Parts.Buttons("##th-b", new (string, Btn, Icon?)[] { ("Seal it", empty ? Btn.Glass : Btn.Primary, Icon.Stamp), ("→Not now", Btn.Quiet, null) });
            if (pressedB == 0 && !empty)
            {
                var body = theoryText;
                var after = theoryAfter;
                Later(() =>
                {
                    if (!journal.SealTheory(body, after)) return;
                    theoryText = string.Empty;
                    composing = false;
                });
            }
            else if (pressedB == 1) Later(() => composing = false);
            Kit.EndPanel();
        }
        else
        {
            var a = Kit.At;
            var w = Kit.Width;
            if (Kit.Button("##nb-seal", a, "Seal a theory", Btn.Quill, BtnSize.Sm, Icon.Feather)) Later(() => composing = true);
            var link = "The whole Notebook on the site →";
            var lw = Txt.Measure(link, Face.Sans, 12).X;
            if (Kit.Link("##nb-site", new Vector2(a.X + w - lw, a.Y + S(7)), link, 12)) Dalamud.Utility.Util.OpenLink(SiteLink.NotebookUrl(true));
            Kit.Reserve(a, w, Kit.ButtonHeight(BtnSize.Sm));
        }
        if (journal.Sync.Hello is { } hello)
        {
            Kit.Gap(10);
            Parts.Quiet($"{Ui.Plural(hello.Sealed, "theory", "theories")} sealed in all for this character.");
        }
    }

    /// <summary>"Sat 7 Nov".</summary>
    private static string Day(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("ddd d MMM", CultureInfo.InvariantCulture) : "a while ago";

    /// <summary>
    /// A theory under wax, as the mock-up's: the seal (34px; once answered cracked, a tick on it), your words in
    /// Literata's italic and candle gold, when and where it was sealed in faint, and how it went as a chip (Called it
    /// in green with a tick, Partly in gold, Not quite and Changed my mind plain); a called one's edge goes green.
    /// </summary>
    private static void TheoryCard(string id, int seed, string body, string meta, string? status, bool broken, float? press)
    {
        var edge = status == TheoryAnswers.Called ? Palette.Rgba(134, 217, 168, 0.3f) : status == TheoryAnswers.Partly ? Palette.QuillLine : Palette.Line;
        Kit.BeginPanel(id, 12, 12, 12, Palette.Leaf, edge, Palette.RadiusMd);
        var at = Kit.At;
        var w = Kit.Width;
        var r = S(17);
        var tx = at.X + r * 2 + S(12);
        var tw = w - (tx - at.X);
        var h = Lines.Draw(Kit.Dl, new Vector2(tx, at.Y), tw, body, Face.ReadItalic, 14, Palette.QuillInk, 21);
        h += S(4) + Lines.Draw(Kit.Dl, new Vector2(tx, at.Y + h + S(4)), tw, meta, Face.Sans, 11, Palette.Faint, 16);
        if (status is { Length: > 0 })
        {
            var kind = status switch
            {
                TheoryAnswers.Called => ChipKind.Ok,
                TheoryAnswers.Partly => ChipKind.Quill,
                _ => ChipKind.Plain,
            };
            Kit.Chip(new Vector2(tx, at.Y + h + S(8)), TheoryAnswers.Stamp(status), kind, status == TheoryAnswers.Called, 22);
            h += S(8 + 22);
        }
        Ink.Seal(Kit.Dl, at + new Vector2(r, r), r, seed, broken, null, broken ? Icon.Check : Icon.Feather, press ?? 1f);
        Kit.Reserve(at, w, MathF.Max(r * 2, h));
        Kit.EndPanel();
    }

    private void DrawNotes()
    {
        Kit.BeginPanel("##nb-note", 16, 16, 16, Palette.Over(Palette.QuillSoft, Palette.Leaf), Palette.QuillLine, Palette.RadiusMd);
        Ink.KickerRow("A new note", Palette.Quill);
        Kit.Gap(6);
        var a = Kit.At;
        Kit.Field("##notes-text", a, Kit.Width, ref noteText, "A thought, a question, something to remember…", null, 2000, ImGuiInputTextFlags.None, Face.ReadItalic, 14, Palette.QuillInk,
            false, 36, Palette.QuillLine);
        Kit.Reserve(a, Kit.Width, S(36));
        Kit.Gap(8);
        Parts.Field("##notes-subject", ref noteSubject, "About someone or something (optional): Haurchefant", 60, ImGuiInputTextFlags.None, Icon.Users);
        Kit.Gap(8);
        Parts.Quiet("A note ending in “?” stays a loose thread on the site until you mark it resolved there.");
        Kit.Gap(10);
        var empty = noteText.Trim().Length == 0;
        if (Parts.Buttons("##notes-b", new (string, Btn, Icon?)[] { ("Add to tonight's entry", empty ? Btn.Glass : Btn.Primary, null) }) == 0 && !empty)
        {
            var body = noteText;
            var about = noteSubject.Trim();
            Later(() =>
            {
                if (!journal.AddNote(body, about)) return;
                noteText = string.Empty;
                noteSubject = string.Empty;
            });
        }
        Kit.EndPanel();

        var waiting = journal.Config.PendingNotes.Count;
        if (waiting > 0)
        {
            Kit.Gap(10);
            Parts.Quiet($"{Ui.Plural(waiting, "note", "notes")} waiting to reach the site (kept until it can).", Palette.Warn);
        }

        var notes = journal.Sync.Hello?.Notes ?? Array.Empty<SiteNote>();
        Kit.Gap(18);
        Ink.KickerRow("Still open");
        Kit.Gap(8);
        if (notes.Count == 0) Parts.Quiet(journal.Sync.Hello == null ? "They show here once the site has answered." : "None: every question answered.");
        foreach (var n in notes)
        {
            // A note, as the mock-up's: a box on the ground's second ink, your words in candle gold, who and when in faint.
            Kit.BeginPanel(Ids.Of("##nb-open", n.GetHashCode()), 10, 12, 10, Palette.Ground2, Palette.Line, 10);
            Lines.Para(n.Body, Palette.QuillInk, 14, 21, Face.ReadItalic);
            Kit.Gap(4);
            Lines.Para((n.Subject.Length > 0 ? $"about {n.Subject} · " : string.Empty) + (n.At is { } at2 ? Ui.Ago(at2) : string.Empty), Palette.Faint, 11, 16);
            Kit.EndPanel();
            Kit.Gap(8);
        }
        Kit.Gap(4);
        var l = Kit.At;
        if (Kit.Link("##notes-site", l, "Resolve and edit them on the site →", 12)) Dalamud.Utility.Util.OpenLink(SiteLink.NotebookUrl(false));
        Kit.Reserve(l, Kit.Width, S(18));
    }
}
