using System.Collections.Generic;

namespace StoryJournal.Core;

/// <summary>Where a session stands with the site, for what names it (a note, a photo).</summary>
public enum SessionStanding
{
    /// <summary>Not kept here (forgotten, or never was): it can't be waited for.</summary>
    Missing,

    /// <summary>Still in the queue, not yet taken by the site.</summary>
    Waiting,

    /// <summary>The site has it.</summary>
    Known,

    /// <summary>The site refused it outright: it never will have it.</summary>
    Refused,
}

/// <summary>A note waiting to go: where its session stands (<see cref="SessionStanding.Missing"/> for none), and whether it's resting after a <c>gone</c>.</summary>
public readonly record struct NoteState(SessionStanding Session, bool Resting = false);

/// <summary>The note to send next, and whether it names its session.</summary>
public readonly record struct NoteChoice(int Index, bool WithSession);

/// <summary>A theory sealed here: the site has it, it's ready to go (its ask-at point is worked out), it's resting after a <c>gone</c>.</summary>
public readonly record struct TheoryState(bool Sent, bool Ready = true, bool Resting = false);

/// <summary>Whose theory an answer is about: the site's own (sealed there), or one sealed here, sent or not.</summary>
public enum AnswerFor
{
    Site,
    Sent,
    Unsent,
}

/// <summary>The theories' call to make next: a theory, or an answer.</summary>
public readonly record struct TheoryChoice(bool Answer, int Index);

/// <summary>What to do with a note, theory or photo the site answered <c>gone</c> for.</summary>
public enum GoneStep
{
    /// <summary>Not there yet (the site hasn't heard of its character, or its session): try again later, without holding up the rest.</summary>
    Wait,

    /// <summary>It never will be (the site had its character and dropped it, or it's been tried enough): kept aside here, never sent again.</summary>
    SetAside,
}

/// <summary>
/// The order the notes, theories and answers go in, and what's held back (the sync's decisions, pure so
/// they're tested):
/// <list type="bullet">
/// <item>A note goes once its session has reached the site, so it's linked to it; while its session is
/// still in the queue it waits (and the queue goes first). A session the site refused, or one gone
/// from here, isn't waited for: the note goes without it.</item>
/// <item>Theories sealed here go before answers, and an answer to a theory sealed here waits until the
/// theory has gone (else the site, not knowing the theory, says <c>gone</c> and the answer is lost; and
/// a theory that arrives after its ask-at point is kept unasked there, so its answer must follow it).</item>
/// <item>A theory whose ask-at point isn't worked out yet (sealed before the story was read) waits.</item>
/// <item>Something resting after a <c>gone</c> waits, and doesn't hold up what comes after it.</item>
/// </list>
/// </summary>
public static class Outbox
{
    /// <summary>How many <c>gone</c> answers before something's set aside.</summary>
    public const int GoneTries = 5;

    /// <summary>
    /// The note to send next (the oldest that can go), or null; <paramref name="waitingForSync"/> when one
    /// waits for its session to reach the site (the queue should go).
    /// </summary>
    public static NoteChoice? NextNote(IReadOnlyList<NoteState> notes, out bool waitingForSync)
    {
        waitingForSync = false;
        for (var i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            if (n.Resting) continue;
            if (n.Session == SessionStanding.Waiting)
            {
                waitingForSync = true;
                continue;
            }
            return new NoteChoice(i, n.Session == SessionStanding.Known);
        }
        return null;
    }

    /// <summary>The theories' next call: an unsent theory first, then an answer that can go; null when nothing can.</summary>
    public static TheoryChoice? NextTheoryCall(IReadOnlyList<TheoryState> theories, IReadOnlyList<AnswerFor> answers)
    {
        for (var i = 0; i < theories.Count; i++)
        {
            var t = theories[i];
            if (!t.Sent && t.Ready && !t.Resting) return new TheoryChoice(false, i);
        }
        for (var i = 0; i < answers.Count; i++)
            if (answers[i] != AnswerFor.Unsent) return new TheoryChoice(true, i);
        return null;
    }

    /// <summary>
    /// A note or theory answered <c>gone</c> (the site has no such character): when the site had heard of
    /// the character since the plugin loaded, it's been dropped there, and this is set aside; else it waits
    /// for the character's next sync, <see cref="GoneTries"/> times at most. <paramref name="tries"/>
    /// counts this one.
    /// </summary>
    public static GoneStep Gone(int tries, bool characterHeardOf) =>
        characterHeardOf || tries >= GoneTries ? GoneStep.SetAside : GoneStep.Wait;

    /// <summary>A photo answered <c>gone</c> (its session isn't on the site, though it was taken there): waits, <see cref="GoneTries"/> times at most.</summary>
    public static GoneStep PhotoGone(int tries) => tries >= GoneTries ? GoneStep.SetAside : GoneStep.Wait;
}
