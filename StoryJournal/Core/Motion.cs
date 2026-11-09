using System;

namespace StoryJournal.Core;

/// <summary>
/// The site's four motion curves (design/story-journal-redesign/pages/motion.html), as functions from
/// time (0..1) to progress: <see cref="Settle"/> (paper coming to rest: fast, then easing off),
/// <see cref="Ink"/> (a pen: slow to start, slow to lift), <see cref="Weight"/> (wax and ribbons: one small
/// overshoot) and <see cref="Leave"/> (going away: gathers speed, no fuss). Each is the CSS cubic-bezier of
/// the same name, solved for x. Pure.
/// </summary>
public static class Ease
{
    public static float Settle(float t) => Bezier(0.2f, 0.7f, 0.2f, 1f, t);

    public static float Ink(float t) => Bezier(0.65f, 0f, 0.25f, 1f, t);

    public static float Weight(float t) => Bezier(0.34f, 1.45f, 0.55f, 1f, t);

    public static float Leave(float t) => Bezier(0.4f, 0f, 1f, 1f, t);

    public static float Linear(float t) => Math.Clamp(t, 0f, 1f);

    /// <summary>CSS's cubic-bezier(x1, y1, x2, y2) at time <paramref name="t"/> (0 and 1 at the ends exactly).</summary>
    public static float Bezier(float x1, float y1, float x2, float y2, float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Find s with x(s) = t (Newton, then bisection if it wanders), then y(s).
        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Coord(x1, x2, s) - t;
            if (MathF.Abs(x) < 1e-5f) return Coord(y1, y2, s);
            var d = Slope(x1, x2, s);
            if (MathF.Abs(d) < 1e-6f) break;
            s -= x / d;
        }
        float lo = 0, hi = 1;
        s = t;
        for (var i = 0; i < 30; i++)
        {
            var x = Coord(x1, x2, s);
            if (MathF.Abs(x - t) < 1e-5f) break;
            if (x < t) lo = s;
            else hi = s;
            s = (lo + hi) / 2;
        }
        return Coord(y1, y2, s);
    }

    private static float Coord(float a, float b, float s) => ((1 - 3 * b + 3 * a) * s + (3 * b - 6 * a)) * s * s + 3 * a * s;

    private static float Slope(float a, float b, float s) => 3 * (1 - 3 * b + 3 * a) * s * s + 2 * (3 * b - 6 * a) * s + 3 * a;
}

/// <summary>The site's durations, in seconds: press, step, turn, ink, wax, and leaving.</summary>
public static class Durations
{
    public const float Press = 0.12f;
    public const float Step = 0.22f;
    public const float Turn = 0.42f;
    public const float Ink = 0.65f;
    public const float Wax = 0.9f;
    public const float Leave = 0.2f;
}

/// <summary>
/// One moving thing: started at a moment, lasting a while, eased by one of the curves. <see cref="Value"/>
/// is its progress (0..1, beyond 1 for a moment with Weight) at a time given, so it's pure and testable; a
/// window asks it each frame with the time now. Calm (the plugin's switch, the system's reduced motion) or
/// a busy game (combat, a duty, a cutscene) skip straight to the end.
/// </summary>
public sealed class Anim
{
    private readonly float seconds;
    private readonly Func<float, float> ease;
    private double startedAt = double.NaN;
    private float delay;

    public Anim(float seconds, Func<float, float> ease)
    {
        this.seconds = MathF.Max(0.001f, seconds);
        this.ease = ease;
    }

    /// <summary>Whether it's been started (and not reset).</summary>
    public bool Started => !double.IsNaN(startedAt);

    /// <summary>Starts it now (<paramref name="now"/> in seconds), after a delay.</summary>
    public void Start(double now, float delaySeconds = 0)
    {
        startedAt = now;
        delay = MathF.Max(0, delaySeconds);
    }

    /// <summary>Starts it once: a second call before <see cref="Reset"/> does nothing.</summary>
    public void StartOnce(double now, float delaySeconds = 0)
    {
        if (!Started) Start(now, delaySeconds);
    }

    public void Reset() => startedAt = double.NaN;

    /// <summary>How far along in time (0..1) at <paramref name="now"/>; 1 when skipped or never started.</summary>
    public float Time(double now, bool skip = false)
    {
        if (skip || !Started) return 1;
        return Math.Clamp((float)((now - startedAt - delay) / seconds), 0f, 1f);
    }

    /// <summary>Its eased progress at <paramref name="now"/>; the end at once when <paramref name="skip"/>.</summary>
    public float Value(double now, bool skip = false) => ease(Time(now, skip));

    /// <summary>Whether it's finished at <paramref name="now"/>.</summary>
    public bool Done(double now, bool skip = false) => Time(now, skip) >= 1;

    /// <summary>A number between two, at its progress now.</summary>
    public float Between(float from, float to, double now, bool skip = false) => from + (to - from) * Value(now, skip);
}
