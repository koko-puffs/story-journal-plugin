using System;
using System.Collections.Generic;
using System.Numerics;
using StoryJournal.Core;

namespace StoryJournal.Session;

/// <summary>A capture toast: what happened, in a few words, for a few seconds.</summary>
public sealed record Toast(string Title, string? Body, string? Note, ToastKind Kind, DateTime At);

public enum ToastKind
{
    Plain,

    /// <summary>A line saved: its words in the quill's colour.</summary>
    Quill,

    /// <summary>Done (a photo kept).</summary>
    Good,

    /// <summary>Something went wrong.</summary>
    Bad,
}

/// <summary>The Welcome back card's contents, made once when it opens.</summary>
/// <param name="Lines">Three lines of story at a glance (oldest first).</param>
/// <param name="Next">The next step: the current quest's objectives (or its name).</param>
/// <param name="At">Where the character stood when it opened (it closes as they walk away).</param>
public sealed record WelcomeCard(DateTime LastPlayed, int Sessions, IReadOnlyList<string> Lines, string? Quest, IReadOnlyList<string> Next, SiteNote? Note, Vector3? At);

/// <summary>An arrival card: a place reached, with the Codex's sentence for it.</summary>
public sealed record ArrivalCard(string Place, string Region, bool First, string Whisper, DateTime At);

/// <summary>Who's that?: someone the character met, targeted.</summary>
public sealed record WhoCard(string Name, MetPerson Met, CodexEntry? Entry, IReadOnlyList<CodexLayer> Layers, IReadOnlyList<SiteNote> Notes, int Lines, DateTime At);

/// <summary>A chapter closed: its name, its expansion, its numbers, and theories to settle.</summary>
public sealed record MilestoneCard(string Chapter, string Expansion, string Quest, string? LastLine, int Quests, bool ExpansionDone, IReadOnlyList<DueTheory> Theories, DateTime At);

/// <summary>The end of session card: the evening in numbers, a suggested title.</summary>
public sealed record WrapUpCard(SessionRecord Session, SessionNumbers Numbers, string? Title, string ExpansionName, int PercentBefore, int PercentNow, DateTime At);
