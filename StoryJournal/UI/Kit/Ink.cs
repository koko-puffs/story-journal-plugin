using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using StoryJournal.Core;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// The redesign's motifs on the draw list ("Ink &amp; Ribbon", design/story-journal-redesign/pages/plugin.html):
/// the ribbon (where you are), the wax seal (a theory, a milestone), the red thread and its knots (an
/// evening's events), the margin rule (the journal's own words), the stamp (when something entered your
/// story), the redaction (what's ahead, inked out, press to read), the kicker (small spaced capitals), and a
/// paragraph with a drop cap. Shapes are fills, lines and polygons only (no blur, no rotation): the ribbon's
/// V is a rectangle and two triangles, the seal an uneven twenty-point outline and three circles.
/// </summary>
public static class Ink
{
    /// <summary>Motion's "now" (seconds) for the windows' <see cref="Anim"/>s.</summary>
    public static double Now => ImGui.GetTime();

    /// <summary>Calm: the plugin's switch or the system's reduced motion; everything is simply there.</summary>
    public static bool Calm { get; set; }

    /// <summary>Skip animations now (calm, or reduced motion asked for).</summary>
    public static bool Still => Calm || ReducedMotion;

    // ---------------------------------------------------------------------------------------------
    // The ribbon.

    /// <summary>
    /// The ribbon hanging from <paramref name="top"/> (its top-left), <paramref name="w"/>×<paramref name="h"/>
    /// screen px, unrolled to <paramref name="unrolled"/> (0..1, a little past 1 as it drops with weight): a
    /// rectangle to the V's notch and a triangle each side of it, a darker band along its top.
    /// </summary>
    public static void Ribbon(ImDrawListPtr dl, Vector2 top, float w, float h, float unrolled = 1f, Vector4? color = null, float notch = 0.22f)
    {
        if (unrolled <= 0 || w <= 0 || h <= 0) return;
        var p = Shapes.Ribbon(top, w, h, unrolled, notch);
        var fill = Palette.U32(color ?? Palette.Ribbon);
        var notchY = p[3].Y;
        if (notchY > top.Y) dl.AddRectFilled(top, new Vector2(top.X + w, notchY), fill);
        if (p[4].Y > notchY)
        {
            dl.AddTriangleFilled(new Vector2(top.X, notchY), p[3], p[4], fill);
            dl.AddTriangleFilled(p[3], new Vector2(top.X + w, notchY), p[2], fill);
        }
        // The deeper red where it's folded over the edge.
        var band = MathF.Min(p[4].Y - top.Y, MathF.Max(S(2), h * 0.12f));
        dl.AddRectFilled(top, new Vector2(top.X + w, top.Y + band), Palette.U32(Palette.RibbonDeep));
    }

    // ---------------------------------------------------------------------------------------------
    // The seal.

    private static readonly Dictionary<(int Seed, int R), Vector2[]> Outlines = new();

    /// <summary>
    /// A wax seal at <paramref name="centre"/>, <paramref name="radius"/> screen px, scaled by
    /// <paramref name="press"/> (the press animation: 1.5 → 0.9 → 1): an uneven outline (fixed per
    /// <paramref name="seed"/>), dark to light wax, a fine ring inside, a highlight; broken, a crack across it in
    /// the card's colour. Then <paramref name="mark"/> (a numeral) or <paramref name="icon"/> on top.
    /// </summary>
    public static void Seal(ImDrawListPtr dl, Vector2 centre, float radius, int seed, bool broken = false, string? mark = null, Icon? icon = null,
        float press = 1f, float alpha = 1f, Vector4? behind = null)
    {
        var r = radius * press;
        if (r <= 0.5f) return;
        var old = Palette.Opacity;
        Palette.Opacity = old * Math.Clamp(alpha, 0, 1);
        // The outline, at this size (made once per size).
        var key = (seed, (int)MathF.Round(r));
        if (!Outlines.TryGetValue(key, out var unit))
        {
            if (Outlines.Count > 512) Outlines.Clear();
            Outlines[key] = unit = Shapes.SealOutline(Vector2.Zero, r, seed);
        }
        var pts = new Vector2[unit.Length];
        for (var i = 0; i < unit.Length; i++) pts[i] = centre + unit[i];
        // A soft ring of deeper red round it (the site's 3px glow), then the wax.
        dl.AddCircleFilled(centre, r * 1.1f, Palette.U32(Palette.WithAlpha(Palette.RibbonDeep, 0.35f)), 32);
        unsafe
        {
            fixed (Vector2* p = pts)
                dl.AddConvexPolyFilled(p, pts.Length, Palette.U32(Palette.WaxDark));
        }
        dl.AddCircleFilled(centre, r * 0.86f, Palette.U32(Palette.WaxMid), 32);
        dl.AddCircleFilled(centre, r * 0.66f, Palette.U32(Palette.WaxLight), 32);
        dl.AddCircle(centre, r * 0.7f, Palette.U32(Palette.Rgba(255, 220, 226, 0.3f)), 32, MathF.Max(1f, r * 0.03f));
        dl.AddCircleFilled(centre + new Vector2(-r * 0.32f, -r * 0.38f), r * 0.2f, Palette.U32(Palette.Rgba(255, 190, 200, 0.35f)), 16);
        if (mark is { Length: > 0 })
        {
            var px = r > S(28) ? 22 : r > S(18) ? 15 : 13;
            var size = Txt.Measure(mark, Face.Display, px);
            Txt.Draw(dl, centre - size / 2 - new Vector2(0, S(1)), mark, Face.Display, px, Palette.Rgba(255, 228, 232, 0.94f));
        }
        else if (icon is { } ic)
        {
            var s = r;
            Icons.Draw(dl, ic, centre - new Vector2(s / 2), s, Palette.Rgba(255, 228, 232, 0.94f), MathF.Max(1.4f, r * 0.08f));
        }
        if (broken)
        {
            var crack = Shapes.Crack(centre, r);
            var line = Palette.U32(behind ?? Palette.Leaf);
            for (var i = 0; i + 1 < crack.Length; i++) dl.AddLine(crack[i], crack[i + 1], line, MathF.Max(2f, r * 0.14f));
        }
        Palette.Opacity = old;
    }

    /// <summary>The press of a seal at time-progress <paramref name="t"/> (0..1): 1.5 → 0.9 → 1.05 → 1, as the site's keyframes.</summary>
    public static float PressScale(float t)
    {
        if (t >= 1) return 1;
        if (t < 0.55f) return 1.5f + (0.9f - 1.5f) * Ease.Settle(t / 0.55f);
        if (t < 0.75f) return 0.9f + (1.05f - 0.9f) * Ease.Settle((t - 0.55f) / 0.2f);
        return 1.05f + (1f - 1.05f) * Ease.Settle((t - 0.75f) / 0.25f);
    }

    // ---------------------------------------------------------------------------------------------
    // The thread.

    /// <summary>The red thread down from <paramref name="top"/> to <paramref name="bottomY"/>, fading as it goes.</summary>
    public static void Thread(ImDrawListPtr dl, Vector2 top, float bottomY)
    {
        if (bottomY <= top.Y) return;
        var w = MathF.Max(1, S(2));
        var mid = top.Y + (bottomY - top.Y) * 0.7f;
        var a = Palette.U32(Palette.Ribbon);
        var b = Palette.U32(Palette.RibbonLine);
        var c = Palette.U32(Palette.WithAlpha(Palette.Ribbon, 0));
        dl.AddRectFilledMultiColor(new Vector2(top.X - w / 2, top.Y), new Vector2(top.X + w / 2, mid), a, a, b, b);
        dl.AddRectFilledMultiColor(new Vector2(top.X - w / 2, mid), new Vector2(top.X + w / 2, bottomY), b, b, c, c);
    }

    /// <summary>A knot on the thread: an outlined circle (an event), filled and ringed for the newest.</summary>
    public static void Knot(ImDrawListPtr dl, Vector2 centre, bool big, float scale = 1f)
    {
        if (scale <= 0) return;
        if (big)
        {
            var r = S(9) * scale;
            dl.AddCircleFilled(centre, r + S(4), Palette.U32(Palette.RibbonSoft), 24);
            dl.AddCircleFilled(centre, r, Palette.U32(Palette.Ribbon), 24);
        }
        else
        {
            var r = S(6) * scale;
            dl.AddCircleFilled(centre, r, Palette.U32(Palette.Leaf), 20);
            dl.AddCircle(centre, r - S(1), Palette.U32(Palette.Ribbon), 20, S(2));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The margin rule, the stamp, the kicker.

    /// <summary>The fine margin rule down the left of the journal's words (gold for your own).</summary>
    public static void Rule(ImDrawListPtr dl, float x, float y0, float y1, bool quill = false) =>
        dl.AddLine(new Vector2(x + 0.5f, y0), new Vector2(x + 0.5f, y1), Palette.U32(quill ? Palette.QuillLine : Palette.RibbonLine), 1f);

    public enum StampTone
    {
        Ribbon,
        Quill,
        Good,
        Muted,
    }

    /// <summary>The stamp's width for its words.</summary>
    public static float StampWidth(string caps) => Txt.SpacedWidth(caps.ToUpperInvariant(), Face.SansSemibold, 10, 0.1f) + S(8) * 2;

    /// <summary>A stamp: its words in spaced capitals in a 1.5px box (the site's is pressed a little askew; ImGui draws it straight).</summary>
    public static float Stamp(ImDrawListPtr dl, Vector2 pos, string caps, StampTone tone = StampTone.Ribbon, float scale = 1f, float alpha = 1f)
    {
        var text = caps.ToUpperInvariant();
        var w = StampWidth(text);
        var h = S(20);
        var color = tone switch
        {
            StampTone.Quill => Palette.Quill,
            StampTone.Good => Palette.Good,
            StampTone.Muted => Palette.Muted,
            _ => Palette.Ribbon,
        };
        var edge = tone switch
        {
            StampTone.Quill => Palette.QuillLine,
            StampTone.Good => Palette.Rgba(134, 217, 168, 0.45f),
            StampTone.Muted => Palette.LineStrong,
            _ => Palette.RibbonLine,
        };
        var old = Palette.Opacity;
        Palette.Opacity = old * alpha;
        var centre = pos + new Vector2(w, h) / 2;
        var half = new Vector2(w, h) / 2 * scale;
        dl.AddRect(centre - half, centre + half, Palette.U32(edge), S(4), ImDrawFlags.None, S(1.5f));
        if (scale > 0.98f && scale < 1.02f)
            Txt.Spaced(dl, pos + new Vector2(S(8), (h - Txt.Natural(Face.SansSemibold, 10)) / 2), text, Face.SansSemibold, 10, color, 0.1f);
        Palette.Opacity = old;
        return w;
    }

    /// <summary>A kicker: small capitals, tracked out (11px Geist 600, +0.14em), as the site's above every heading. Returns its width.</summary>
    public static float Kicker(ImDrawListPtr dl, Vector2 pos, string caps, Vector4? color = null, int px = 11) =>
        Txt.Spaced(dl, pos, caps.ToUpperInvariant(), Face.SansSemibold, px, color ?? Palette.Muted, 0.14f);

    public static float KickerWidth(string caps, int px = 11) => Txt.SpacedWidth(caps.ToUpperInvariant(), Face.SansSemibold, px, 0.14f);

    /// <summary>A kicker on its own line across the box.</summary>
    public static void KickerRow(string caps, Vector4? color = null, float lineCss = 16)
    {
        var at = Kit.At;
        Kicker(Kit.Dl, at + new Vector2(0, (S(lineCss) - Txt.Natural(Face.SansSemibold, 11)) / 2), caps, color);
        Kit.Reserve(at, Kit.Width, S(lineCss));
    }

    // ---------------------------------------------------------------------------------------------
    // The redaction.

    /// <summary>A redaction's width for its label.</summary>
    public static float RedactionWidth(string label) => Txt.Measure(label + " · show", Face.SansMedium, 11).X + S(9) * 2;

    /// <summary>
    /// What's ahead, inked out: a hatched bar with a dashed edge saying what kind of thing it is ("a trial ·
    /// show"), never what. Returns true when pressed (the caller then shows the words in its place).
    /// <paramref name="wipe"/> is how much of the ink has wiped off (0..1, left to right, as it lifts).
    /// </summary>
    public static bool Redaction(string id, Vector2 pos, string label, float height = 0, float wipe = 0)
    {
        var text = label + " · show";
        var w = RedactionWidth(label);
        var h = height > 0 ? height : S(20);
        var (clicked, hovered, _) = Kit.Hit(id, pos, new Vector2(w, h));
        var dl = Kit.Dl;
        var min = pos + new Vector2(w * Math.Clamp(wipe, 0, 1), 0);
        var max = pos + new Vector2(w, h);
        if (max.X - min.X > 0.5f)
        {
            dl.PushClipRect(min, max, true);
            dl.AddRectFilled(pos, max, Palette.U32(Palette.Hover), S(4));
            foreach (var (a, b) in Shapes.Hatch(pos, max, S(8))) dl.AddLine(a, b, Palette.U32(Palette.Rgba(255, 255, 255, 0.035f)), S(3));
            Kit.Dashed(pos, max, S(4), hovered ? Palette.RibbonLine : Palette.LineStrong, 1f);
            Txt.Line(dl, pos + new Vector2(S(9), 0), text, Face.SansMedium, 11, hovered ? Palette.Ink : Palette.Faint, h / S(1));
            dl.PopClipRect();
        }
        Kit.FocusRing(pos, max, S(4));
        if (hovered) Kit.Tooltip("Show it");
        return clicked;
    }

    // ---------------------------------------------------------------------------------------------
    // A paragraph with a drop cap.

    /// <summary>
    /// Story words with a drop cap: the first letter in Fraunces, ribbon red, two lines tall; the first two lines
    /// set beside it, the rest across. Returns the height drawn.
    /// </summary>
    public static float DropCap(ImDrawListPtr dl, Vector2 pos, float width, string text, Face face = Face.Read, int px = 15, Vector4? color = null, float lineCss = 23)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        var cap = text[..1];
        var rest = text[1..];
        var capPx = 28;
        var capSize = Txt.Measure(cap, Face.Display, capPx);
        var line = S(lineCss);
        Txt.Draw(dl, pos + new Vector2(0, S(2)), cap, Face.Display, capPx, Palette.Ribbon);
        var indent = capSize.X + S(6);
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var y = pos.Y;
        var i = 0;
        var lineNo = 0;
        var space = Txt.Measure(" ", face, px).X;
        while (i < words.Length)
        {
            var x0 = lineNo < 2 ? pos.X + indent : pos.X;
            var room = pos.X + width - x0;
            var start = i;
            var used = Txt.Measure(words[i], face, px).X;
            i++;
            while (i < words.Length)
            {
                var add = space + Txt.Measure(words[i], face, px).X;
                if (used + add > room) break;
                used += add;
                i++;
            }
            Txt.Line(dl, new Vector2(x0, y), string.Join(' ', words, start, i - start), face, px, color ?? Palette.Body, lineCss);
            y += line;
            lineNo++;
        }
        return MathF.Max(y - pos.Y, line * 2);
    }
}
