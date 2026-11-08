using System;
using System.Linq;
using System.Numerics;
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

    private static readonly string[] AfterWords = { "Don't ask", "In 5 quests", "In 10", "In 25" };
    private static readonly int[] AfterValues = { 0, 5, 10, 25 };

    private void DrawNotes()
    {
        Parts.Heading("A NEW NOTE", 0);
        Parts.Field("##notes-text", ref noteText, "A thought, a question, something to remember…", 2000);
        Kit.Gap(8);
        Parts.Field("##notes-subject", ref noteSubject, "About someone or something (optional): Haurchefant", 60, Dalamud.Bindings.ImGui.ImGuiInputTextFlags.None, Icon.Users);
        Kit.Gap(8);
        Parts.Quiet("A note ending in “?” stays a loose thread on the site until you mark it resolved there.");
        Kit.Gap(10);
        var empty = noteText.Trim().Length == 0;
        if (Parts.Buttons("##notes-b", new (string, Btn, Icon?)[] { ("Save note", empty ? Btn.Glass : Btn.Primary, null) }) == 0 && !empty)
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

        var waiting = journal.Config.PendingNotes.Count;
        if (waiting > 0)
        {
            Kit.Gap(10);
            Parts.Quiet($"{Ui.Plural(waiting, "note", "notes")} waiting to reach the site (kept until it can).", Palette.Warn);
        }

        var notes = journal.Sync.Hello?.Notes ?? Array.Empty<SiteNote>();
        Parts.Heading("YOUR UNRESOLVED NOTES", 20);
        if (notes.Count == 0) Parts.Quiet(journal.Sync.Hello == null ? "They show here once the site has answered." : "None: every question answered.");
        foreach (var n in notes)
        {
            Parts.Quote(n.Body, (n.Subject.Length > 0 ? $"about {n.Subject} · " : string.Empty) + (n.At is { } at ? Ui.Ago(at) : string.Empty));
            Kit.Gap(10);
        }
        Kit.Gap(6);
        var a = Kit.At;
        if (Kit.Link("##notes-site", a, "Resolve and edit them on the site")) Dalamud.Utility.Util.OpenLink(SiteLink.SiteUrl);
        Kit.Reserve(a, Kit.Width, S(18));
    }

    private void DrawTheories()
    {
        Parts.Heading("SEAL A THEORY", 0);
        Parts.Field("##th-text", ref theoryText, "What do you think will happen?", 400);
        Kit.Gap(8);
        var at = Kit.At;
        var picked = Array.IndexOf(AfterValues, theoryAfter);
        var next = Kit.Segmented("##th-after", at, AfterWords, picked < 0 ? 2 : picked, 0, Kit.Width);
        if (next >= 0 && next < AfterValues.Length) theoryAfter = AfterValues[next];
        Kit.Reserve(at, Kit.Width, S(36));
        Kit.Gap(8);
        Parts.Quiet("Sealed with where you are in the story. The plugin (and the site) asks how it went once you've played that many main-story quests.");
        Kit.Gap(10);
        var empty = theoryText.Trim().Length == 0;
        if (Parts.Buttons("##th-b", new (string, Btn, Icon?)[] { ("Seal it", empty ? Btn.Glass : Btn.Primary, Icon.Stamp) }) == 0 && !empty)
        {
            var body = theoryText;
            var after = theoryAfter;
            Later(() =>
            {
                if (journal.SealTheory(body, after)) theoryText = string.Empty;
            });
        }

        var due = Due();
        Parts.Heading("TIME TO SETTLE", 22);
        if (due.Count == 0) Parts.Quiet("None due. A theory's question comes when you've played as far as you said.");
        var i = 0;
        foreach (var t in due)
        {
            Parts.Quote($"“{t.Body}”", (t.SealedQuest.Length > 0 ? $"sealed during {t.SealedQuest}" : "sealed") + (t.SealedAt is { } sat ? $", {Ui.Ago(sat)}" : string.Empty));
            Kit.Gap(6);
            var pressed = Parts.Buttons(Ids.Of("##th-due", i++), new (string, Btn, Icon?)[]
            {
                ("Called it", Btn.Glass, Icon.Check), ("Missed it", Btn.Glass, null), ("Changed my mind", Btn.Quiet, null),
            }, BtnSize.Tiny);
            if (pressed >= 0)
            {
                var id = t.Id;
                var status = pressed switch { 0 => "called", 1 => "missed", _ => "changed" };
                Later(() =>
                {
                    journal.AnswerTheory(id, status);
                    dueAt = DateTime.MinValue;
                });
            }
            Kit.Gap(12);
        }

        var mine = journal.Memory?.Theories.OrderByDescending(t => t.SealedAt).ToList();
        if (mine is { Count: > 0 })
        {
            Parts.Heading("SEALED HERE", 18);
            foreach (var t in mine.Take(30))
            {
                var state = t.Answered ? "settled" : !t.Sent ? "waiting to reach the site" : t.DueIndex > 0 ? "sealed · you'll be asked" : "sealed";
                Parts.Quote($"“{t.Body}”", $"{Ui.Ago(t.SealedAt)} · {state}");
                Kit.Gap(10);
            }
        }
        if (journal.Sync.Hello is { } hello)
        {
            Kit.Gap(6);
            Parts.Quiet($"{Ui.Plural(hello.Sealed, "theory", "theories")} sealed in all for this character.");
        }
    }
}
