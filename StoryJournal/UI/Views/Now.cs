using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    private string quickNote = string.Empty;
    private string tokenField = string.Empty;

    /// <summary>Duties ahead shown with strict spoilers (Show pressed), until the next one.</summary>
    private readonly HashSet<string> shownDuties = new(StringComparer.Ordinal);

    private List<DueTheory> due = new();
    private DateTime dueAt;

    /// <summary>The theories due, worked out once a second (not each frame).</summary>
    private List<DueTheory> Due()
    {
        if (DateTime.UtcNow - dueAt < TimeSpan.FromSeconds(1)) return due;
        dueAt = DateTime.UtcNow;
        return due = journal.DueTheories();
    }

    private void DrawNow()
    {
        if (journal.Sync.Link is not LinkState.Linked and not LinkState.Linking) DrawLinkBox();
        if (journal.Player == null)
        {
            Empty("##now-out", Icon.BookOpen, "Log in to read your story", "The journal opens on where you are in the main story, once you're in the world.");
            return;
        }
        if (journal.Story.Broken)
        {
            Empty("##now-broken", Icon.AlertTriangle, "The main story couldn't be read", "The game's data didn't read as expected (see /xllog). Recording still works; the window fills in once it can be read.");
            return;
        }
        if (journal.Story.Now is not { } story)
        {
            Empty("##now-wait", Icon.Clock, "Reading the main story…", "A moment: the plugin reads the game's own list of main-story quests once each time it loads.");
            return;
        }
        var progress = story.Progress;
        var player = journal.Player;

        // Where: the expansion and chapter, the job and level.
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        var right = $"Lv {player.Level} {player.Job}";
        var rw = Txt.Measure(right, Face.Mono, 12).X;
        var where = progress.Current is { } cq
            ? cq.ExpansionName + (cq.Chapter != null ? " › " + cq.Chapter : string.Empty)
            : progress.ExpansionNow?.Name ?? string.Empty;
        var lw = Txt.Line(dl, at, "Chapter", Face.Sans, 12, Palette.Muted, 20);
        Txt.Line(dl, new Vector2(at.X + lw + S(8), at.Y), Txt.Fit(where, Face.SansMedium, 13, w - lw - rw - S(20)), Face.SansMedium, 13, Palette.Ink, 20);
        Txt.Line(dl, new Vector2(at.X + w - rw, at.Y), right, Face.Mono, 12, Palette.Muted, 20);
        Kit.Reserve(at, w, S(20));
        Kit.Gap(8);
        if (progress.ExpansionNow is { } ex)
            Parts.Bar(ex.Total > 0 ? (float)ex.Done / ex.Total : 0, $"{Ui.Count(ex.Done)} / {Ui.Count(ex.Total)} quests · {Progress.Percent(ex.Done, ex.Total)}%");
        DrawPaceLock(story);
        Parts.Rule(14);

        if (progress.Complete || story.Current == null)
        {
            Empty("##now-done", Icon.Sparkles, "The main story is done", "Every main-story quest on your road is finished. The journal keeps recording what you do; the site has the whole story.");
            return;
        }
        var q = story.Current;

        // The quest, from the game.
        var head = Kit.At;
        var tag = "· main scenario";
        var used = Txt.Line(Kit.Dl, head, Txt.Fit(q.Name, Face.SansSemibold, 16, w - S(110)), Face.SansSemibold, 16, Palette.Ink, 22);
        Txt.Line(Kit.Dl, new Vector2(head.X + used + S(8), head.Y), tag, Face.Sans, 12, Palette.Muted, 22);
        Kit.Reserve(head, w, S(22));
        Kit.Gap(4);
        if (!story.CurrentTaken) Parts.Story($"Next on the road: take it to carry on{(q.Place.Length > 0 ? $", in {q.Place}" : string.Empty)}.");
        else if (story.Objectives.Count > 0) Parts.Story(string.Join(" ", story.Objectives));
        else Parts.Quiet("This step has no to-do in the game's journal.");
        if (story.CurrentTaken)
        {
            Kit.Gap(8);
            var a2 = Kit.At;
            if (Kit.Button("##now-flag", a2, "Flag on map", Btn.Glass, BtnSize.Sm, Icon.Flag)) Later(journal.FlagNext);
            Kit.Reserve(a2, w, Kit.ButtonHeight(BtnSize.Sm));
        }

        // Why you're here: the last words of the quest before, then this one's journal so far.
        var why = new List<string>();
        if (story.PreviousLine is { Length: > 0 } prev) why.Add(prev);
        why.AddRange(story.Journal.Skip(Math.Max(0, story.Journal.Count - 3)));
        if (why.Count > 0)
        {
            Parts.Heading("WHY YOU'RE HERE", 18);
            foreach (var line in why)
            {
                Parts.Story(line);
                Kit.Gap(6);
            }
        }

        // People in this.
        if (story.People.Count > 0 && journal.Memory is { } memory)
        {
            Parts.Heading("PEOPLE IN THIS", 12);
            var chips = story.People.Select(p => memory.People.FirstOrDefault(m => m.Name == p) is { } met ? $"{p} · met {Ui.Ago(met.At)}" : p).ToList();
            var pressed = Parts.Chips("##now-people", chips);
            if (pressed >= 0)
            {
                var name = story.People[pressed];
                Later(() => ShowCodex(name));
            }
        }

        // Ahead, safely.
        if (progress.Ahead.Count > 0 || progress.NextStop != null)
        {
            Parts.Heading("AHEAD", 16);
            var spoilers = journal.Prefs.Spoilers;
            var i = 0;
            foreach (var a in progress.Ahead)
            {
                var row = Kit.At;
                var when = a.QuestsAway <= 1 ? "Next duty after this quest" : $"Next duty in ~{a.QuestsAway} quests";
                if (i > 0) when = a.QuestsAway <= 1 ? "Then after this quest" : $"Then in ~{a.QuestsAway} quests";
                var x = row.X + Txt.Line(Kit.Dl, row, when + " · ", Face.Sans, 13, Palette.Muted, 30);
                var hidden = spoilers == Spoilers.Strict && !shownDuties.Contains(a.Duty);
                var meta = $"Lv {a.Level} · {a.Type}";
                var mw = Txt.Measure(meta, Face.Sans, 12).X;
                if (hidden)
                {
                    x += Txt.Line(Kit.Dl, new Vector2(x, row.Y), "a duty", Face.SansMedium, 13, Palette.Faint, 30) + S(8);
                    var bw = Kit.ButtonWidth("Show", BtnSize.Tiny, null, Btn.Glass);
                    if (Kit.Button(Ids.Of("##now-show", i), new Vector2(row.X + w - bw, row.Y), "Show", Btn.Glass, BtnSize.Tiny))
                    {
                        var duty = a.Duty;
                        Later(() => shownDuties.Add(duty));
                    }
                }
                else
                {
                    var nameW = w - (x - row.X) - mw - S(12);
                    x += Txt.Line(Kit.Dl, new Vector2(x, row.Y), Txt.Fit(a.Duty, Face.SansMedium, 13, nameW), Face.SansMedium, 13, Palette.Ink, 30) + S(8);
                    Txt.Line(Kit.Dl, new Vector2(row.X + w - mw, row.Y), meta, Face.Sans, 12, Palette.Muted, 30);
                }
                Kit.Reserve(row, w, S(30));
                i++;
                if (spoilers == Spoilers.Strict) break;
            }
            if (progress.NextStop is { } stop)
            {
                Kit.Gap(4);
                var reason = stop.Reason == "chapter" ? "the chapter's end" : "the next duty";
                Lines.Para(stop.QuestsAway <= 1
                    ? $"Good place to stop: after this quest ({reason})."
                    : $"Good place to stop: after the next {stop.QuestsAway} quests ({reason}).", Palette.Soft, 13, 19);
            }
        }

        // A quick note.
        Parts.Rule(16);
        Parts.Heading("QUICK NOTE", 0);
        Parts.Field("##now-note", ref quickNote, "The camp's cook deserves a medal…", 2000);
        Kit.Gap(8);
        var empty = quickNote.Trim().Length == 0;
        var b = Parts.Buttons("##now-note-b", new (string, Btn, Icon?)[]
        {
            ("Save note", empty ? Btn.Glass : Btn.Primary, null),
            ("Seal as theory", Btn.Glass, Icon.Stamp),
            ("→Open on the website", Btn.Quiet, Icon.ExternalLink),
        });
        if (b == 0 && !empty)
        {
            var body = quickNote;
            Later(() =>
            {
                if (journal.AddNote(body)) quickNote = string.Empty;
            });
        }
        else if (b == 1 && !empty)
        {
            var body = quickNote;
            Later(() =>
            {
                if (journal.SealTheory(body, theoryAfter)) quickNote = string.Empty;
            });
        }
        else if (b == 2) Dalamud.Utility.Util.OpenLink(SiteLink.SiteUrl);
    }

    /// <summary>The pace lock's warnings, under the progress bar.</summary>
    private void DrawPaceLock(StoryState story)
    {
        if (journal.Sync.Hello is not { Partners.Count: > 0 } hello) return;
        foreach (var warning in PaceLock.Check(hello.Partners, story.Progress.Index, story.NextIndex))
        {
            Kit.Gap(10);
            Kit.BeginPanel(Ids.Of("##pace", warning.Partner.GetHashCode()), 10, 14, 10, Palette.AccentWash, Palette.AccentEdge);
            var at = Kit.At;
            Icons.Draw(Kit.Dl, Icon.Lock, at + new Vector2(0, S(2)), S(14), Palette.Accent);
            var h = Lines.Draw(Kit.Dl, new Vector2(at.X + S(22), at.Y), Kit.Width - S(22), PaceLock.Words(warning, story.Progress.Index)
                + (warning.Past ? "." : ": you asked to keep pace."), Face.Sans, 13, Palette.AccentSoft, 19);
            Kit.Reserve(at, Kit.Width, h);
            Kit.EndPanel();
        }
    }

    /// <summary>The link, for the first time: a token from the site.</summary>
    private void DrawLinkBox()
    {
        Kit.BeginPanel("##now-link", 16, 18, 16, Palette.AccentWash, Palette.AccentEdge);
        var at = Kit.At;
        var w = Kit.Width;
        Kit.IconWell(at, S(36), Icon.Link);
        var x = at.X + S(36 + 14);
        Txt.Line(Kit.Dl, new Vector2(x, at.Y), "Link your journal", Face.SansSemibold, 15, Palette.Ink, 20);
        var refused = journal.Sync.Link is LinkState.Refused r ? r.Why : null;
        var words = refused ?? "Make a token on kokos-tools.com (Settings → Story Journal) and paste it here. Until then everything is kept on this PC.";
        var h = S(22) + Lines.Draw(Kit.Dl, new Vector2(x, at.Y + S(22)), w - (x - at.X), words, Face.Sans, 13, refused != null ? Palette.Danger : Palette.Muted, 19);
        Kit.Reserve(at, w, MathF.Max(S(36), h));
        Kit.Gap(12);
        DrawTokenField("##now-token");
        Kit.EndPanel();
        Kit.Gap(18);
    }

    private void DrawTokenField(string id)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var linkW = Kit.ButtonWidth("Link", BtnSize.Md, null, Btn.Primary);
        var getW = Kit.ButtonWidth("Get a token", BtnSize.Md, Icon.ExternalLink);
        Kit.Field(id, at, w - linkW - getW - S(16), ref tokenField, "sjp_…", Icon.Link, 80, Dalamud.Bindings.ImGui.ImGuiInputTextFlags.Password);
        var ok = SiteLink.LooksRight(SiteLink.Clean(tokenField));
        if (Kit.Button(id + "-get", new Vector2(at.X + w - linkW - getW - S(8), at.Y), "Get a token", Btn.Glass, BtnSize.Md, Icon.ExternalLink))
            Dalamud.Utility.Util.OpenLink(SiteLink.TokensUrl);
        if (Kit.Button(id + "-go", new Vector2(at.X + w - linkW, at.Y), "Link", ok ? Btn.Primary : Btn.Glass, BtnSize.Md, null, 0, !ok,
                ok ? null : "A token is sjp_ and 43 letters"))
        {
            var t = tokenField;
            Later(() =>
            {
                journal.Link(t);
                tokenField = string.Empty;
                Frame.Toast("Linking…");
            });
        }
        Kit.Reserve(at, w, S(36));
    }
}
