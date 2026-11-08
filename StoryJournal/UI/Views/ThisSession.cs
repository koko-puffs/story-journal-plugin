using System;
using System.Linq;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    private void DrawSession()
    {
        var session = journal.Session;
        if (session == null)
        {
            Empty("##ses-none", Icon.Clock, "No session under way", journal.Player == null
                ? "A session starts when you log in and play; it ends at logout, /sj end, or after a while with nothing happening."
                : "One starts with the next thing that happens (move, take a quest). It ends at logout, /sj end, or after a while with nothing happening.");
            DrawQueueLine();
            return;
        }
        var events = journal.SessionEvents.Where(e => e.Session == session.Id).OrderByDescending(e => e.At).ToList();
        var numbers = Sessions.Count(events, journal.SessionPhotos);
        Kit.Facts("##ses-facts", new Kit.Fact[]
        {
            new("Playing", Ui.Duration(DateTime.UtcNow - session.StartedAt), $"since {Ui.Clock(session.StartedAt)}"),
            new("Quests", Ui.Count(numbers.Quests), numbers.Msq > 0 ? $"{numbers.Msq} main" : null),
            new("Cutscenes", Ui.Count(numbers.Cutscenes)),
            new("Photos", Ui.Count(numbers.Photos)),
        });
        Kit.Gap(12);
        var b = Parts.Buttons("##ses-b", new (string, Btn, Icon?)[]
        {
            ("Save the line", Btn.Glass, Icon.Quote),
            ("Take a photo", Btn.Glass, Icon.Camera),
            ("→End session…", Btn.Quiet, null),
        });
        if (b == 0) Later(journal.SaveLine);
        else if (b == 1) Later(() => journal.TakePhoto());
        else if (b == 2) Later(journal.EndSession);
        if (journal.Config.Paused)
        {
            Kit.Gap(10);
            Lines.Para("Recording is paused: nothing new is written until /sj resume (or Settings).", Palette.Warn, 13, 19);
        }

        Parts.Heading("WHAT HAPPENED", 18);
        if (events.Count == 0) Parts.Quiet("Nothing yet. Quests, cutscenes, places, duties, levels and people met are written here as they happen.");
        foreach (var e in events.Take(80)) EventRow(e);
        if (events.Count > 80) Parts.Quiet($"…and {events.Count - 80} more, on the site.");
        Kit.Gap(10);
        DrawQueueLine();
    }

    private static void EventRow(JournalEvent e)
    {
        var (icon, words, note) = Describe(e);
        var at = Kit.At;
        var w = Kit.Width;
        var time = Ui.Clock(e.At);
        var tw = Txt.Measure(time, Face.Mono, 11).X;
        Txt.Line(Kit.Dl, at, time, Face.Mono, 11, Palette.Dim, 20);
        var x = at.X + MathF.Max(tw, S(40)) + S(10);
        Icons.Draw(Kit.Dl, icon, new Vector2(x, at.Y + S(3)), S(14), e.Kind == EventKind.Line ? Palette.Quill : Palette.Accent);
        x += S(22);
        var h = Lines.Draw(Kit.Dl, new Vector2(x, at.Y), w - (x - at.X), words, Face.Sans, 13, Palette.Soft, 20);
        if (note != null) h += Lines.Draw(Kit.Dl, new Vector2(x, at.Y + h), w - (x - at.X), note, Face.Sans, 12, e.Kind == EventKind.Line ? Palette.Quill : Palette.Muted, 18);
        Kit.Reserve(at, w, MathF.Max(S(20), h));
        Kit.Gap(6);
    }

    /// <summary>An event in words, as the session's list shows it.</summary>
    private static (Icon Icon, string Words, string? Note) Describe(JournalEvent e)
    {
        var d = e.Data;
        string T(string k) => Json.Str(d[k]) ?? string.Empty;
        switch (e.Kind)
        {
            case EventKind.Quest:
                var chapter = Json.Bool(d["chapterDone"]) ? $"Chapter closed: {T("chapter")}" : null;
                return (Icon.BookOpen, $"Finished {T("name")}", chapter ?? (Json.Bool(d["msq"]) ? "main scenario" : null));
            case EventKind.Accept:
                return (Icon.Bookmark, $"Took {T("name")}", Json.Bool(d["msq"]) ? "main scenario" : null);
            case EventKind.Cutscene:
                return (Icon.Eye, $"A cutscene{(T("place").Length > 0 ? " in " + T("place") : string.Empty)}", Ui.Duration(TimeSpan.FromSeconds(Json.Int(d["seconds"]))));
            case EventKind.Zone:
                return (Icon.MapPin, $"Reached {T("name")}", Json.Bool(d["first"]) ? "first visit" : T("region"));
            case EventKind.Duty:
                var verb = Json.Bool(d["cleared"]) ? "Cleared" : "Left";
                return (Icon.Swords, $"{verb} {T("name")}", $"{T("type")} · {Ui.Duration(TimeSpan.FromSeconds(Json.Int(d["seconds"])))}");
            case EventKind.Level:
                return (Icon.Sparkles, $"Level {Json.Int(d["level"])} {T("job")}", null);
            case EventKind.Person:
                return (Icon.Users, $"Met {T("name")}", T("place").Length > 0 ? T("place") : null);
            case EventKind.Line:
                return (Icon.Quote, $"Saved a line of {(T("speaker").Length > 0 ? T("speaker") : "someone")}", $"“{T("text")}”");
            default:
                return (Icon.Info, e.Kind, null);
        }
    }

    /// <summary>What's waiting for the site, in a line.</summary>
    private void DrawQueueLine()
    {
        var q = journal.Queue;
        var waiting = q.Count;
        var photos = q.PhotoCount;
        string words;
        if (waiting == 0 && photos == 0)
            words = journal.Sync.SyncedAt is { } at ? $"Everything's on the site (last sent {Ui.Ago(at)})." : "Nothing waiting for the site.";
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (waiting > 0) parts.Add(Ui.Plural(waiting, "event", "events"));
            if (photos > 0) parts.Add(Ui.Plural(photos, "photo", "photos"));
            words = $"{string.Join(" and ", parts)} waiting to upload · kept on this PC until the site has them.";
        }
        Parts.Quiet(words);
        if (journal.Sync.Problem is { } problem) Parts.Quiet(problem, Palette.Warn);
    }
}
