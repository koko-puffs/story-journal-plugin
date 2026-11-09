using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using StoryJournal.Core;
using StoryJournal.Session;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

public sealed partial class MainWindow
{
    private string quickNote = string.Empty;
    private string tokenField = string.Empty;

    /// <summary>Duties ahead shown with strict spoilers (the redaction pressed), until the next one.</summary>
    private readonly HashSet<string> shownDuties = new(StringComparer.Ordinal);

    private List<DueTheory> due = new();
    private DateTime dueAt;

    /// <summary>A redaction pressed: its ink lifting (650ms, the ink curve).</summary>
    private readonly Anim redactionLift = new(Durations.Ink, Ease.Ink);

    /// <summary>The bookmark's ribbon drops once for each quest (its weight, 650ms).</summary>
    private readonly Anim bookmarkDrop = new(Durations.Ink, Ease.Weight);
    private uint bookmarkFor;

    /// <summary>The two cards side by side (Previously, Ahead): each one's height last frame, so they match.</summary>
    private readonly Dictionary<string, float> pairHeights = new();

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

        // Where: the chapter, the place, the level.
        var where = new List<string>();
        if (ChapterNumber() is { } n) where.Add($"Chapter {Roman.Of(n)}");
        else if (progress.ExpansionNow is { } exNow) where.Add(exNow.Name);
        if (progress.Current is { Place.Length: > 0 } cq) where.Add(cq.Place);
        where.Add($"Lv {player.Level} {player.Job}");
        Ink.KickerRow(string.Join(" · ", where));
        DrawPaceLock(story);
        Kit.Gap(12);

        if (progress.Complete || story.Current == null)
        {
            Empty("##now-done", Icon.Sparkles, "The main story is done", "Every main-story quest on your road is finished. The journal keeps recording what you do; the site has the whole story.");
            return;
        }
        DrawBookmark(story);
        Kit.Gap(14);
        DrawPair("##now-prev", "##now-ahead", DrawPreviously, () => DrawAhead(story), 0.555f);
    }

    /// <summary>The bookmark: the quest, its step with Flag on map, why you're here, who's in it. One card, top to bottom.</summary>
    private void DrawBookmark(StoryState story)
    {
        var q = story.Current!;
        if (bookmarkFor != q.Id)
        {
            bookmarkFor = q.Id;
            bookmarkDrop.Start(Ink.Now, 0.15f);
        }
        // A ribboned leaf: its edge in the ribbon's line, its fill a little warmer at the top (#24161a to the leaf, 70% down).
        Kit.BeginPanel("##now-bookmark", 16, 18, 16, Palette.Leaf, Palette.RibbonLine, Palette.RadiusMd, Palette.Hex(0x24161a));
        var (cardAt, cardW, _) = Kit.Panel;
        Ink.KickerRow("Your bookmark", Palette.Ribbon);
        Kit.Gap(4);
        var at = Kit.At;
        var w = Kit.Width;
        foreach (var line in Lines.Of(q.Name, w - S(48), Face.Display, 22))
        {
            var a = Kit.At;
            Txt.Line(Kit.Dl, a, line, Face.Display, 22, Palette.Ink, 30);
            Kit.Reserve(a, w, S(30));
        }
        Kit.Gap(10);

        // The step, with its place and the flag.
        var stepTop = Kit.At;
        var stepH = S(62);
        var dl = Kit.Dl;
        dl.AddRectFilled(stepTop, stepTop + new Vector2(w, stepH), Palette.U32(Palette.Ground2), S(10));
        dl.AddRect(stepTop + new Vector2(0.5f), stepTop + new Vector2(w, stepH) - new Vector2(0.5f), Palette.U32(Palette.Line), S(10));
        var tile = S(34);
        var tileAt = stepTop + new Vector2(S(14), (stepH - tile) / 2);
        dl.AddCircleFilled(tileAt + new Vector2(tile / 2), tile / 2, Palette.U32(Palette.RibbonSoft), 24);
        dl.AddCircle(tileAt + new Vector2(tile / 2), tile / 2 - 0.5f, Palette.U32(Palette.RibbonLine), 24, 1f);
        Icons.Draw(dl, Icon.MapPin, tileAt + new Vector2((tile - S(16)) / 2), S(16), Palette.Ribbon);
        var flagW = story.CurrentTaken ? Kit.ButtonWidth("Flag on map", BtnSize.Sm, Icon.Flag, Btn.Primary) : 0;
        var tx = tileAt.X + tile + S(14);
        var textW = w - (tx - stepTop.X) - flagW - S(28);
        var step = !story.CurrentTaken
            ? "Take it to carry on"
            : story.Objectives.Count > 0 ? story.Objectives[0] : "This step has no to-do in the game's journal";
        var place = new List<string>();
        if (q.Place.Length > 0) place.Add(q.Place);
        if (story.Progress.Current?.Spot is { } spot) place.Add(spot.ToString());
        Txt.Line(dl, new Vector2(tx, stepTop.Y + S(11)), Txt.Fit(step, Face.SansSemibold, 15, textW), Face.SansSemibold, 15, Palette.Ink, 21);
        Txt.Line(dl, new Vector2(tx, stepTop.Y + S(33)), Txt.Fit(string.Join(" · ", place), Face.Sans, 12, textW), Face.Sans, 12, Palette.Muted, 17);
        if (story.CurrentTaken && Kit.Button("##now-flag", new Vector2(stepTop.X + w - S(14) - flagW, stepTop.Y + (stepH - Kit.ButtonHeight(BtnSize.Sm)) / 2),
                "Flag on map", Btn.Primary, BtnSize.Sm, Icon.Flag))
            Later(journal.FlagNext);
        Kit.Reserve(stepTop, w, stepH);

        // Why you're here: the last words of the quest before, then this one's journal so far.
        var why = new List<string>();
        if (story.PreviousLine is { Length: > 0 } prev) why.Add(prev);
        why.AddRange(story.Journal.Skip(Math.Max(0, story.Journal.Count - 2)));
        if (why.Count > 0)
        {
            Kit.Gap(12);
            Ruled(() =>
            {
                Ink.KickerRow("Why you're here");
                Kit.Gap(4);
                Lines.Para(string.Join(" ", why), Palette.Body, 14, 22.4f, Face.Read);
            });
        }

        // Who's in it.
        if (story.People.Count > 0)
        {
            Kit.Gap(12);
            var row = Kit.At;
            var x = row.X + Ink.Kicker(Kit.Dl, row + new Vector2(0, S(7)), "In this") + S(10);
            var y = row.Y;
            for (var i = 0; i < story.People.Count; i++)
            {
                var name = story.People[i];
                var cw = PersonChipWidth(name);
                if (x + cw > row.X + w && x > row.X + S(80))
                {
                    x = row.X + S(64);
                    y += S(30);
                }
                if (PersonChip(Ids.Of("##now-who", i), new Vector2(x, y), name)) Later(() => ShowCodex(name));
                x += cw + S(6);
            }
            Kit.Reserve(row, w, y - row.Y + S(26));
        }
        Kit.EndPanel();
        // The ribbon (26×52) hangs from the card's top edge, 22px in from its right corner, dropped once for each quest.
        var drop = bookmarkDrop.Value(Ink.Now, Ink.Still);
        Ink.Ribbon(Kit.Dl, new Vector2(cardAt.X + cardW - S(22 + 26), cardAt.Y - S(1)), S(26), S(52), drop);
    }

    /// <summary>Lays what <paramref name="body"/> draws with a margin rule down its left (the journal's own words).</summary>
    private static void Ruled(Action body, bool quill = false)
    {
        var at = Kit.At;
        Kit.PushBox(at.X + S(22), Kit.Width - S(22));
        body();
        Kit.PopBox();
        var end = ImGui.GetCursorScreenPos().Y;
        Ink.Rule(Kit.Dl, at.X, at.Y + S(3), end - S(3), quill);
    }

    private static float PersonChipWidth(string name) => S(3 + 20 + 6) + Txt.Measure(name, Face.SansSemibold, 12).X + S(10);

    /// <summary>Someone in the quest: a pill with their initial in an italic circle; pressed, their Codex card.</summary>
    private static bool PersonChip(string id, Vector2 pos, string name)
    {
        var w = PersonChipWidth(name);
        var h = S(26);
        var (clicked, hovered, _) = Kit.Hit(id, pos, new Vector2(w, h));
        var dl = Kit.Dl;
        dl.AddRectFilled(pos, pos + new Vector2(w, h), Palette.U32(hovered ? Palette.Hover : Palette.LeafRaised), h / 2);
        dl.AddRect(pos + new Vector2(0.5f), pos + new Vector2(w, h) - new Vector2(0.5f), Palette.U32(hovered ? Palette.LineStrong : Palette.Line), h / 2);
        var c = pos + new Vector2(S(3 + 10), h / 2);
        dl.AddCircleFilled(c, S(10), Palette.U32(Palette.Hex(0x3a2a26)), 20);
        var initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        var isz = Txt.Measure(initial, Face.DisplayItalic, 15);
        Txt.Draw(dl, c - isz / 2, initial, Face.DisplayItalic, 15, Palette.Ink);
        Txt.Line(dl, pos + new Vector2(S(3 + 20 + 6), 0), name, Face.SansSemibold, 12, Palette.Ink, h / S(1));
        if (hovered) Kit.Tooltip("In the Codex");
        return clicked;
    }

    /// <summary>Two cards side by side (narrow: one over the other), as tall as each other.</summary>
    private void DrawPair(string idL, string idR, Action left, Action right, float split)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var gap = S(12);
        var stacked = w < S(520);
        var lw = stacked ? w : MathF.Floor((w - gap) * split);
        var rw = stacked ? w : w - gap - lw;
        var lastL = pairHeights.TryGetValue(idL, out var hl) ? hl : 0;
        var lastR = pairHeights.TryGetValue(idR, out var hr) ? hr : 0;
        var padY = S(14);
        var pad = S(16);
        var dl = Kit.Dl;
        float Card(string id, Action body, Vector2 pos, float width, float lastH, float minH)
        {
            var h = MathF.Max(lastH, minH);
            if (h > 0)
            {
                dl.AddRectFilled(pos, pos + new Vector2(width, h), Palette.U32(Palette.Leaf), S(Palette.RadiusMd));
                dl.AddRect(pos + new Vector2(0.5f), pos + new Vector2(width, h) - new Vector2(0.5f), Palette.U32(Palette.Line), S(Palette.RadiusMd));
            }
            ImGui.SetCursorScreenPos(pos + new Vector2(0, padY));
            Kit.PushBox(pos.X + pad, width - pad * 2);
            body();
            Kit.PopBox();
            var used = ImGui.GetCursorScreenPos().Y + padY - pos.Y;
            pairHeights[id] = used;
            return used;
        }
        if (stacked)
        {
            var h1 = Card(idL, left, at, w, lastL, 0);
            var h2 = Card(idR, right, at + new Vector2(0, h1 + gap), w, lastR, 0);
            Kit.Reserve(at, w, h1 + gap + h2);
            return;
        }
        var tall = MathF.Max(lastL, lastR);
        var a = Card(idL, left, at, lw, lastL, tall);
        var b = Card(idR, right, at + new Vector2(lw + gap, 0), rw, lastR, tall);
        Kit.Reserve(at, w, MathF.Max(a, b));
    }

    /// <summary>Previously: three lines from the last evenings, ruled, and the way to the whole recap.</summary>
    private void DrawPreviously()
    {
        Ink.KickerRow("Previously");
        Kit.Gap(8);
        var hello = journal.Sync.Hello;
        var lines = hello != null ? Welcome.Glance(hello.Recent, 3, journal.Memory?.JournalSince) : new List<string>();
        if (lines.Count == 0)
        {
            Parts.Quiet(hello == null ? "Your last evenings show here once the site has answered." : "Nothing written yet: your next evenings fill this.");
            return;
        }
        Ruled(() => Lines.Para(string.Join(" ", lines), Palette.Body, 14, 22.4f, Face.Read));
        Kit.Gap(10);
        var a = Kit.At;
        const string recap = "Read it all on the site";
        if (Kit.Link("##now-recap", a, recap, 12)) Dalamud.Utility.Util.OpenLink(SiteLink.RecapUrl);
        Icons.Draw(Kit.Dl, Icon.ArrowRight, a + new Vector2(Txt.Measure(recap, Face.SansSemibold, 12).X + S(4), S(2)), S(12), Palette.Ribbon);
        Kit.Reserve(a, Kit.Width, S(18));
    }

    /// <summary>Ahead: how far to the next duty, its kind inked out on strict spoilers, and a good place to stop.</summary>
    private void DrawAhead(StoryState story)
    {
        var progress = story.Progress;
        Ink.KickerRow("Ahead");
        Kit.Gap(6);
        var spoilers = journal.Prefs.Spoilers;
        if (progress.Ahead.Count > 0)
        {
            var a = progress.Ahead[0];
            var big = a.QuestsAway <= 1 ? "Next quest" : $"~{a.QuestsAway} quests";
            var at = Kit.At;
            Txt.Line(Kit.Dl, at, big, Face.Display, 22, Palette.Ink, 28);
            Kit.Reserve(at, Kit.Width, S(28));
            Parts.Quiet(a.QuestsAway <= 1 ? "then your next duty" : "to your next duty");
            Kit.Gap(6);
            var hidden = spoilers == Spoilers.Strict && !shownDuties.Contains(a.Duty);
            var row = Kit.At;
            var x = row.X + Txt.Line(Kit.Dl, row, "It's ", Face.Sans, 12, Palette.Muted, 22);
            var label = "a " + Kind(a.Type);
            if (hidden)
            {
                if (Ink.Redaction("##now-ahead-show", new Vector2(x, row.Y + S(1)), label))
                {
                    var duty = a.Duty;
                    Later(() =>
                    {
                        shownDuties.Add(duty);
                        // The ink lifts left to right (650ms, the ink curve): the word is covered or whole, never faded.
                        redactionLift.Start(Ink.Now);
                    });
                }
                x += Ink.RedactionWidth(label);
                Txt.Line(Kit.Dl, new Vector2(x, row.Y), ".", Face.Sans, 12, Palette.Muted, 22);
                Kit.Reserve(row, Kit.Width, S(22));
            }
            else
            {
                var h = Lines.Draw(Kit.Dl, new Vector2(x, row.Y + S(2)), Kit.Width - (x - row.X), $"{a.Duty}, Lv {a.Level} ({a.Type.ToLowerInvariant()}).",
                    Face.SansMedium, 12, Palette.Ink, 18);
                if (spoilers == Spoilers.Strict && redactionLift.Started && !redactionLift.Done(Ink.Now, Ink.Still))
                    Ink.Redaction("##now-ahead-show", new Vector2(x, row.Y + S(1)), label, 0, redactionLift.Value(Ink.Now, Ink.Still));
                Kit.Reserve(row, Kit.Width, MathF.Max(S(22), h + S(4)));
            }
            Parts.Quiet("Bring a party, or the Duty Finder.");
        }
        else Parts.Quiet("No duty on the road ahead that the journal can see.");
        if (progress.NextStop is { } stop)
        {
            Kit.Gap(10);
            var line = Kit.At;
            Kit.Dl.AddLine(new Vector2(line.X, line.Y + 0.5f), new Vector2(line.X + Kit.Width, line.Y + 0.5f), Palette.U32(Palette.Line), 1f);
            Kit.Reserve(line, Kit.Width, 1);
            Kit.Gap(10);
            var at = Kit.At;
            Icons.Draw(Kit.Dl, Icon.Check, at + new Vector2(0, S(2)), S(13), Palette.Good);
            var when = stop.QuestsAway <= 1 ? "after this quest" : $"after {stop.QuestsAway} more quests";
            var why = stop.Reason == "chapter" ? ", as the chapter closes" : ", before the next duty";
            var h = Lines.Draw(Kit.Dl, new Vector2(at.X + S(20), at.Y), Kit.Width - S(20), $"A good place to stop: {when}{why}.", Face.Sans, 12, Palette.Body, 18);
            Kit.Reserve(at, Kit.Width, h);
        }
    }

    /// <summary>A duty's kind in a word ("trial", "dungeon"), for the redaction: never its name.</summary>
    private static string Kind(string type)
    {
        var t = type.ToLowerInvariant();
        if (t.Contains("dungeon")) return "dungeon";
        if (t.Contains("trial")) return "trial";
        if (t.Contains("raid")) return "raid";
        if (t.Contains("guildhest")) return "guildhest";
        return "duty";
    }

    /// <summary>The quick note at the foot of Now: a line for tonight's entry in candle gold, its key, Seal as theory, Save.</summary>
    private void DrawQuickBar()
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(52);
        var dl = Kit.Dl;
        dl.AddRectFilled(at, at + new Vector2(w, h), Palette.U32(Palette.Over(Palette.QuillSoft, Palette.Leaf)), S(Palette.RadiusLg));
        Kit.Dashed(at, at + new Vector2(w, h), S(Palette.RadiusLg), Palette.QuillLine, 1f);
        var tile = S(30);
        var ty = at.Y + (h - tile) / 2;
        dl.AddRectFilled(new Vector2(at.X + S(11), ty), new Vector2(at.X + S(11) + tile, ty + tile), Palette.U32(Palette.QuillSoft), S(9));
        dl.AddRect(new Vector2(at.X + S(11), ty), new Vector2(at.X + S(11) + tile, ty + tile), Palette.U32(Palette.QuillLine), S(9));
        Icons.Draw(dl, Icon.Feather, new Vector2(at.X + S(11) + (tile - S(15)) / 2, ty + (tile - S(15)) / 2), S(15), Palette.Quill);
        var saveW = Kit.ButtonWidth("Save", BtnSize.Sm);
        var sealW = Kit.ButtonWidth("Seal as theory", BtnSize.Sm, null, Btn.Quill);
        var key = config.NoteKey.Length > 0 ? config.NoteKey.Replace("+", " ") : null;
        var keyW = key != null ? Txt.Measure(key, Face.Mono, 11).X + S(12) : 0;
        var right = at.X + w - S(11);
        var by = at.Y + (h - Kit.ButtonHeight(BtnSize.Sm)) / 2;
        var empty = quickNote.Trim().Length == 0;
        var save = Kit.Button("##now-note-save", new Vector2(right - saveW, by), "Save", Btn.Glass, BtnSize.Sm, null, 0, empty);
        var seal = Kit.Button("##now-note-seal", new Vector2(right - saveW - S(8) - sealW, by), "Seal as theory", Btn.Quill, BtnSize.Sm, null, 0, empty);
        var fieldX = at.X + S(11) + tile + S(12);
        var fieldR = right - saveW - sealW - S(16) - (key != null ? keyW + S(10) : 0);
        if (key != null)
        {
            var kp = new Vector2(fieldR + S(4), at.Y + (h - S(20)) / 2);
            dl.AddRect(kp, kp + new Vector2(keyW, S(20)), Palette.U32(Palette.LineStrong), S(5));
            Txt.Line(dl, kp + new Vector2(S(6), 0), key, Face.Mono, 11, Palette.Muted, 20);
        }
        var entered = Kit.Field("##now-note", new Vector2(fieldX, at.Y + (h - S(36)) / 2), MathF.Max(S(60), fieldR - fieldX), ref quickNote,
            "A line for tonight's entry…", null, 2000, ImGuiInputTextFlags.EnterReturnsTrue, Face.ReadItalic, 14, Palette.QuillInk, true, 36, null,
            Palette.Rgba(243, 227, 194, 0.45f));
        Kit.Reserve(at, w, h);
        if ((save || entered) && !empty)
        {
            var body = quickNote;
            Later(() =>
            {
                if (journal.AddNote(body)) quickNote = string.Empty;
            });
        }
        else if (seal && !empty)
        {
            var body = quickNote;
            Later(() =>
            {
                if (journal.SealTheory(body, theoryAfter)) quickNote = string.Empty;
            });
        }
    }

    /// <summary>The pace lock's warnings, under the place line.</summary>
    private void DrawPaceLock(StoryState story)
    {
        if (journal.Sync.Hello is not { Partners.Count: > 0 } hello) return;
        foreach (var warning in PaceLock.Check(hello.Partners, story.Progress.Index, story.NextIndex))
        {
            Kit.Gap(10);
            Kit.BeginPanel(Ids.Of("##pace", warning.Partner.GetHashCode()), 10, 14, 10, Palette.AccentWash, Palette.AccentEdge);
            var at = Kit.At;
            Icons.Draw(Kit.Dl, Icon.Hourglass, at + new Vector2(0, S(2)), S(14), Palette.Ribbon);
            var h = Lines.Draw(Kit.Dl, new Vector2(at.X + S(22), at.Y), Kit.Width - S(22), PaceLock.Words(warning, story.Progress.Index)
                + (warning.Past ? "." : ": you asked to keep pace."), Face.Sans, 13, Palette.AccentSoft, 19);
            Kit.Reserve(at, Kit.Width, h);
            Kit.EndPanel();
        }
    }

    /// <summary>The link, for the first time: a token from the site.</summary>
    private void DrawLinkBox()
    {
        Kit.BeginPanel("##now-link", 18, 20, 18, Palette.AccentWash, Palette.RibbonLine, Palette.RadiusMd);
        var at = Kit.At;
        var w = Kit.Width;
        Kit.IconWell(at, S(36), Icon.Link);
        var x = at.X + S(36 + 14);
        Txt.Line(Kit.Dl, new Vector2(x, at.Y), "Link your journal", Face.Display, 17, Palette.Ink, 22);
        var refused = journal.Sync.Link is LinkState.Refused r ? r.Why : null;
        var words = refused ?? "Make a token on kokos-tools.com (Settings → Story Journal → In-game plugin) and paste it here. Until then everything is kept on this PC.";
        var h = S(24) + Lines.Draw(Kit.Dl, new Vector2(x, at.Y + S(24)), w - (x - at.X), words, Face.Sans, 13, refused != null ? Palette.Danger : Palette.Muted, 19);
        Kit.Reserve(at, w, MathF.Max(S(36), h));
        Kit.Gap(12);
        DrawTokenField("##now-token");
        Kit.EndPanel();
        Kit.Gap(16);
    }

    private void DrawTokenField(string id)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var linkW = Kit.ButtonWidth("Link", BtnSize.Md, null, Btn.Primary);
        var getW = Kit.ButtonWidth("Get a token", BtnSize.Md, Icon.ExternalLink);
        Kit.Field(id, at, w - linkW - getW - S(16), ref tokenField, "sjp_…", Icon.Link, 80, ImGuiInputTextFlags.Password);
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
