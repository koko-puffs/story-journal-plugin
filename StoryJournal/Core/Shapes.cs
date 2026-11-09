using System;
using System.Collections.Generic;
using System.Numerics;

namespace StoryJournal.Core;

/// <summary>
/// The redesign's shapes as points, for the draw list (pure, so they're tested): the ribbon (a bookmark, its
/// foot cut in a V), the wax seal's uneven outline (twenty points jittered once by the seal's id, so it
/// never wobbles), and a stamp's slant.
/// </summary>
public static class Shapes
{
    /// <summary>
    /// The ribbon at <paramref name="topLeft"/>, <paramref name="width"/> wide and <paramref name="height"/>
    /// long, unrolled to <paramref name="unrolled"/> (0..1, more than 1 for a moment as it drops): five points,
    /// clockwise from the top left, the V's point <paramref name="notch"/> (a fraction of its length, 0.22 as
    /// the site's 78%) up from its foot.
    /// </summary>
    public static Vector2[] Ribbon(Vector2 topLeft, float width, float height, float unrolled = 1f, float notch = 0.22f)
    {
        var h = MathF.Max(0, height * unrolled);
        var cut = MathF.Min(h, height * notch);
        return new[]
        {
            topLeft,
            topLeft + new Vector2(width, 0),
            topLeft + new Vector2(width, h),
            topLeft + new Vector2(width / 2, h - cut),
            topLeft + new Vector2(0, h),
        };
    }

    /// <summary>The seal's outline: <paramref name="points"/> points round <paramref name="centre"/>, each a little in or out (by up to <paramref name="jitter"/> of the radius), the same every time for one <paramref name="seed"/>.</summary>
    public static Vector2[] SealOutline(Vector2 centre, float radius, int seed, int points = 20, float jitter = 0.08f)
    {
        var rng = new Random(seed);
        var outline = new Vector2[points];
        for (var i = 0; i < points; i++)
        {
            var a = MathF.PI * 2 * i / points - MathF.PI / 2;
            var r = radius * (1 + ((float)rng.NextDouble() * 2 - 1) * jitter);
            outline[i] = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        }
        return outline;
    }

    /// <summary>A stable seed from a string (a theory's id, a chapter's name): the same for the same words, run to run.</summary>
    public static int Seed(string text)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in text) h = (h ^ c) * 16777619;
            return h;
        }
    }

    /// <summary>The crack across a broken seal: from top to bottom, a little to one side and back, as the site draws it.</summary>
    public static Vector2[] Crack(Vector2 centre, float radius) => new[]
    {
        centre + new Vector2(radius * 0.16f, -radius * 1.02f),
        centre + new Vector2(radius * 0.03f, -radius * 0.45f),
        centre + new Vector2(-radius * 0.10f, -radius * 0.06f),
        centre + new Vector2(radius * 0.03f, radius * 0.30f),
        centre + new Vector2(-radius * 0.03f, radius * 1.02f),
    };

    /// <summary>The 45° hatching of a redaction: lines across a box, <paramref name="gap"/> apart, each clipped to the box.</summary>
    public static List<(Vector2 A, Vector2 B)> Hatch(Vector2 min, Vector2 max, float gap)
    {
        var lines = new List<(Vector2, Vector2)>();
        if (gap <= 0) return lines;
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        if (w <= 0 || h <= 0) return lines;
        // Lines x - y = c, from c = -h to w.
        for (var c = -h + gap / 2; c < w; c += gap)
        {
            var x0 = MathF.Max(0, c);
            var y0 = x0 - c;
            var x1 = MathF.Min(w, c + h);
            var y1 = x1 - c;
            lines.Add((min + new Vector2(x0, h - y0), min + new Vector2(x1, h - y1)));
        }
        return lines;
    }
}
