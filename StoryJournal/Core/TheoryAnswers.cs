using System;
using System.Collections.Generic;

namespace StoryJournal.Core;

/// <summary>
/// How a theory is settled, as the site's <c>sj_theory_settle</c> takes it (through <c>sj_plugin_theory_answer</c>):
/// called (right), partly (partly right), missed, or changed (your mind); <c>sealed</c> seals it again (not settled,
/// asked again later). The words the windows use for each: the buttons, the stamps, the toast. Pure.
/// </summary>
public static class TheoryAnswers
{
    public const string Called = "called";
    public const string Partly = "partly";
    public const string Missed = "missed";
    public const string Changed = "changed";
    public const string Sealed = "sealed";

    /// <summary>The answers in the order the buttons show them.</summary>
    public static readonly IReadOnlyList<string> Settled = new[] { Called, Partly, Missed, Changed };

    /// <summary>Whether the site takes this as an answer (or a seal again).</summary>
    public static bool Valid(string? status) => status is Called or Partly or Missed or Changed or Sealed;

    /// <summary>The button's words: "Called it", "Partly", "Missed it", "Changed my mind".</summary>
    public static string Button(string status) => status switch
    {
        Called => "Called it",
        Partly => "Partly",
        Missed => "Missed it",
        Changed => "Changed my mind",
        _ => "Seal it again",
    };

    /// <summary>The stamp on a settled theory: "Called it", "Partly", "Not quite", "Changed my mind".</summary>
    public static string Stamp(string status) => status switch
    {
        Called => "Called it",
        Partly => "Partly",
        Missed => "Not quite",
        Changed => "Changed my mind",
        _ => "Sealed",
    };

    /// <summary>The toast's words once it's settled.</summary>
    public static string Toast(string status) => status switch
    {
        Called => "You called it.",
        Partly => "Partly right.",
        Missed => "Not this time.",
        Changed => "You changed your mind.",
        _ => "Sealed again.",
    };

    /// <summary>The button's index for an answer, and back (−1 / null when it isn't one).</summary>
    public static string? At(int index) => index >= 0 && index < Settled.Count ? Settled[index] : null;

    public static int IndexOf(string status)
    {
        for (var i = 0; i < Settled.Count; i++)
            if (string.Equals(Settled[i], status, StringComparison.Ordinal)) return i;
        return -1;
    }
}
