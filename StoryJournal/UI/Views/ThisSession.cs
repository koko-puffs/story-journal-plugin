using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    /// <summary>
    /// This evening: its numbers (played, quests, how far it moved you, photos), then what happened on the red
    /// thread, newest on top (the newest knot filled), and whether the site has it. Saving a line and taking a
    /// photo are in its head.
    /// </summary>
    private void DrawSession()
    {
        var session = journal.Session;
        if (session == null)
        {
            PlaceHead("This evening", "No evening under way");
            Kit.Gap(12);
            Empty("##ses-none", Icon.Clock, "It starts when you play", journal.Player == null
                ? "An evening starts when you log in and play; it ends at logout, /sj end, or after a while with nothing happening."
                : "One starts with the next thing that happens (move, take a quest). It ends at logout, /sj end, or after a while with nothing happening.");
            Kit.Gap(14);
            DrawQueueLine();
            return;
        }
        var events = journal.SessionEvents.Where(e => e.Session == session.Id).OrderByDescending(e => e.At).ToList();
        var numbers = session.Tally.Numbers;

        // The head: the day and since when, the title, the line and the photo.
        var head = Kit.At;
        var w = Kit.Width;
        var local = session.StartedAt.ToLocalTime();
        Ink.Kicker(Kit.Dl, head, $"{local.ToString("dddd d MMM", CultureInfo.InvariantCulture)} · since {local.ToString("HH:mm", CultureInfo.InvariantCulture)}");
        Txt.Line(Kit.Dl, head + new Vector2(0, S(18)), "The evening so far", Face.Display, 19, Palette.Ink, 26);
        var ib = S(32);
        if (Kit.IconButton("##ses-line", new Vector2(head.X + w - ib * 2 - S(6), head.Y + S(6)), Icon.Quote, "Save the line on screen", 32)) Later(journal.SaveLine);
        if (Kit.IconButton("##ses-photo", new Vector2(head.X + w - ib, head.Y + S(6)), Icon.Camera, "Take a photo (the HUD hidden)", 32)) Later(() => journal.TakePhoto());
        Kit.Reserve(head, w, S(44));
        Kit.Gap(12);

        // The numbers, four tiles.
        var moved = journal.Story.Now?.Progress.ExpansionNow is { } ex && session.Tally.PercentAtStart is { } start
            ? Progress.Percent(ex.Done, ex.Total) - start
            : (int?)null;
        Tiles("##ses-tiles", new (string, string)[]
        {
            (Ui.Duration(DateTime.UtcNow - session.StartedAt), "played"),
            (Ui.Count(numbers.Quests), numbers.Msq > 0 ? $"quests · {numbers.Msq} main" : "quests"),
            (moved is { } m ? $"+{Math.Max(0, m)}%" : "—", "of the story"),
            (Ui.Count(numbers.Photos), numbers.Photos == 1 ? "photo" : "photos"),
        });
        if (journal.Config.Paused)
        {
            Kit.Gap(10);
            Lines.Para("Recording is paused: nothing new is written until /sj resume (or Settings).", Palette.Warn, 13, 19);
        }

        // The red thread.
        Kit.Gap(16);
        if (events.Count == 0) Parts.Quiet("Nothing yet. Quests, cutscenes, places, duties, levels and people met are written here as they happen.");
        else DrawThread(events);
        if (events.Count > 60) Parts.Quiet($"…and {events.Count - 60} more, on the site.");

        Kit.Gap(14);
        var line = Kit.At;
        Kit.Dl.AddLine(new Vector2(line.X, line.Y + 0.5f), new Vector2(line.X + w, line.Y + 0.5f), Palette.U32(Palette.Line), 1f);
        Kit.Reserve(line, w, 1);
        Kit.Gap(10);
        var foot = Kit.At;
        var endW = Kit.ButtonWidth("End the evening", BtnSize.Sm);
        Kit.PushBox(foot.X, w - endW - S(12));
        DrawQueueLine();
        Kit.PopBox();
        var after = Kit.At.Y;
        if (Kit.Button("##ses-end", new Vector2(foot.X + w - endW, foot.Y), "End the evening", Btn.Glass, BtnSize.Sm)) Later(journal.EndSession);
        Kit.Reserve(foot, w, MathF.Max(after - foot.Y, Kit.ButtonHeight(BtnSize.Sm)));
    }

    /// <summary>A row of number tiles: a figure in Fraunces over its word.</summary>
    private static void Tiles(string id, (string Figure, string Word)[] tiles)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var gap = S(8);
        var cols = w < S(360) ? 2 : tiles.Length;
        var rows = (tiles.Length + cols - 1) / cols;
        var tw = (w - gap * (cols - 1)) / cols;
        var th = S(62);
        var dl = Kit.Dl;
        for (var i = 0; i < tiles.Length; i++)
        {
            var p = at + new Vector2((i % cols) * (tw + gap), (i / cols) * (th + gap));
            dl.AddRectFilled(p, p + new Vector2(tw, th), Palette.U32(Palette.Ground2), S(10));
            dl.AddRect(p + new Vector2(0.5f), p + new Vector2(tw, th) - new Vector2(0.5f), Palette.U32(Palette.Line), S(10));
            Txt.Line(dl, p + new Vector2(S(12), S(10)), Txt.Fit(tiles[i].Figure, Face.Display, 19, tw - S(24)), Face.Display, 19, Palette.Ink, 24);
            Txt.Line(dl, p + new Vector2(S(12), S(36)), Txt.Fit(tiles[i].Word, Face.Sans, 11, tw - S(24)), Face.Sans, 11, Palette.Muted, 16);
        }
        Kit.Reserve(at, w, rows * th + (rows - 1) * gap);
    }

    /// <summary>
    /// What happened, newest first, on the red thread (the site's: the string 8px in, each item 28px in): a knot on the
    /// string, the time, the words (names in Geist 600 and ink), a saved line in Literata's italic and candle gold, a
    /// stamp on someone met, a photo's place.
    /// </summary>
    private void DrawThread(System.Collections.Generic.List<JournalEvent> events)
    {
        var top = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var stringX = top.X + S(9);
        var timeX = top.X + S(28);
        var textX = timeX + S(44 + 8);
        var textW = w - (textX - top.X);
        var knots = new System.Collections.Generic.List<float>();
        foreach (var e in events.Take(60))
        {
            var at = Kit.At;
            var (before, name, after, quote, stamp) = Story(e);
            var runs = new System.Collections.Generic.List<Run> { new(before, Face.Sans, 13, Palette.Body) };
            if (name is { Length: > 0 }) runs.Add(new Run(name, Face.SansSemibold, 13, Palette.Ink));
            if (after.Length > 0) runs.Add(new Run(after, Face.Sans, 13, Palette.Body));
            Txt.Line(dl, at, Ui.Clock(e.At), Face.Mono, 11, Palette.Faint, 20);
            var room = textW - (stamp != null ? Ink.StampWidth(stamp) + S(8) : 0);
            var h = Txt.Flow(new Vector2(textX, at.Y), room, runs, 20, out _);
            if (stamp != null)
            {
                var used = Txt.Measure(before, Face.Sans, 13).X + (name is { Length: > 0 } ? Txt.Measure(name, Face.SansSemibold, 13).X : 0) + Txt.Measure(after, Face.Sans, 13).X;
                Ink.Stamp(dl, new Vector2(textX + MathF.Min(used + S(6), textW - Ink.StampWidth(stamp)), at.Y), stamp);
            }
            if (quote != null)
            {
                h += S(3);
                h += Lines.Draw(dl, new Vector2(textX, at.Y + h), textW, $"“{quote}”", Face.ReadItalic, 13, Palette.QuillInk, 19);
            }
            if (e.Kind == EventKind.Photo)
            {
                // The photo's place in the thread (the picture itself is on the site once it's sent): a snowy stand-in.
                var p = new Vector2(textX, at.Y + h + S(6));
                var size = new Vector2(S(120), S(64));
                dl.AddRectFilledMultiColor(p, p + size, Palette.U32(Palette.Hex(0xb8c4d8)), Palette.U32(Palette.Hex(0x9fb0c8)), Palette.U32(Palette.Hex(0x2b3346)), Palette.U32(Palette.Hex(0x3a4258)));
                dl.AddCircleFilled(p + new Vector2(size.X * 0.66f, size.Y * 0.32f), S(16), Palette.U32(Palette.Rgba(240, 243, 250, 0.45f)), 24);
                dl.AddRect(p, p + size, Palette.U32(Palette.Line), S(8));
                h += size.Y + S(6);
            }
            knots.Add(at.Y + S(10));
            Kit.Reserve(at, w, MathF.Max(S(20), h));
            Kit.Gap(12);
        }
        // The string (from 0.4rem to 0.4rem from the ends, fading), then the knots on it: the newest filled and ringed.
        if (knots.Count == 0) return;
        Ink.Thread(dl, new Vector2(stringX, top.Y + S(6)), Kit.At.Y - S(12) - S(6));
        for (var i = 0; i < knots.Count; i++) Ink.Knot(dl, new Vector2(stringX, knots[i]), i == 0, 1f);
    }

    /// <summary>An event as the thread tells it: its words around a name (set in ink), a saved line's words, a stamp (someone met: the Codex grew).</summary>
    private static (string Before, string? Name, string After, string? Quote, string? Stamp) Story(JournalEvent e)
    {
        var d = e.Data;
        string T(string k) => Json.Str(d[k]) ?? string.Empty;
        switch (e.Kind)
        {
            case EventKind.Quest:
                return ("Finished ", T("name"), Json.Bool(d["chapterDone"]) ? $" · the chapter {T("chapter")} closes" : string.Empty, null, null);
            case EventKind.Accept:
                return ("Took ", T("name"), string.Empty, null, null);
            case EventKind.Cutscene:
                return (T("place").Length > 0 ? "A cutscene in " : "A cutscene", T("place").Length > 0 ? T("place") : null,
                    $" · {Ui.Duration(TimeSpan.FromSeconds(Json.Int(d["seconds"])))}", null, null);
            case EventKind.Zone:
                return ("Reached ", T("name"), Json.Bool(d["first"]) ? ", for the first time" : string.Empty, null, null);
            case EventKind.Duty:
                return (Json.Bool(d["cleared"]) ? "Cleared " : "Left ", T("name"), $" · {Ui.Duration(TimeSpan.FromSeconds(Json.Int(d["seconds"])))}", null, null);
            case EventKind.Level:
                return ("Level ", $"{Json.Int(d["level"])} {T("job")}", string.Empty, null, null);
            case EventKind.Person:
                return ("Met ", T("name"), string.Empty, null, "Codex +1");
            case EventKind.Line:
                return ($"Saved a line of {(T("speaker").Length > 0 ? T("speaker") : "someone")}'s", null, string.Empty, T("text"), null);
            case EventKind.Photo:
                return T("caption").Length > 0 ? ("A photo: ", T("caption"), string.Empty, null, null) : ("A photo", null, string.Empty, null, null);
            default:
                return (e.Kind, null, string.Empty, null, null);
        }
    }

    /// <summary>What's waiting for the site, in a line.</summary>
    private void DrawQueueLine()
    {
        var q = journal.Queue;
        var waiting = q.Count;
        var photos = q.PhotoCount;
        var at = Kit.At;
        string words;
        var ok = waiting == 0 && photos == 0;
        if (ok)
            words = journal.Sync.SyncedAt is { } synced ? $"Everything's on the site (sent {Ui.Ago(synced)})." : "Nothing waiting for the site.";
        else
        {
            var parts = new System.Collections.Generic.List<string>();
            if (waiting > 0) parts.Add(Ui.Plural(waiting, "event", "events"));
            if (photos > 0) parts.Add(Ui.Plural(photos, "photo", "photos"));
            words = $"{string.Join(" and ", parts)} waiting to upload · kept on this PC until the site has them.";
        }
        Icons.Draw(Kit.Dl, ok ? Icon.Check : Icon.Clock, at + new Vector2(0, S(3)), S(13), ok ? Palette.Good : Palette.Muted);
        var h = Lines.Draw(Kit.Dl, new Vector2(at.X + S(20), at.Y), Kit.Width - S(20), words, Face.Sans, 12, Palette.Muted, 18);
        Kit.Reserve(at, Kit.Width, h);
        if (journal.Sync.Problem is { } problem) Parts.Quiet(problem, Palette.Warn);
    }
}
