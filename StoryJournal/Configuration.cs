using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace StoryJournal;

/// <summary>Someone a character met (the speaker of a quest's dialogue, the first time): when and where.</summary>
[Serializable]
public sealed class MetPerson
{
    public string Name { get; set; } = string.Empty;

    public string Place { get; set; } = string.Empty;

    public DateTime At { get; set; }

    public uint QuestId { get; set; }
}

/// <summary>A theory sealed in the game: kept so it's asked about when its time comes.</summary>
[Serializable]
public sealed class SealedTheory
{
    public Guid Id { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>The main-story index at which to ask (0: not asked here; the site may still say it's due).</summary>
    public int DueIndex { get; set; }

    public DateTime SealedAt { get; set; }

    public string SealedQuest { get; set; } = string.Empty;

    public bool Answered { get; set; }

    /// <summary>How it was settled here (TheoryAnswers: called, partly, missed, changed), empty while sealed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The site has it.</summary>
    public bool Sent { get; set; }

    public string Subject { get; set; } = string.Empty;

    /// <summary>How many main-story quests from sealing to ask (sent to the site as <c>checkAfter</c>).</summary>
    public int CheckAfter { get; set; }

    /// <summary>
    /// Sealed before the story was read (just after login): its ask-at point (<see cref="DueIndex"/>) is
    /// worked out once it is, and it isn't sent until then.
    /// </summary>
    public bool AwaitingIndex { get; set; }

    /// <summary>The site no longer has its character: kept here (and in the queue's aside.jsonl), never sent.</summary>
    public bool Aside { get; set; }

    /// <summary>How many times the site answered "gone" (its character not there).</summary>
    public int GoneTries { get; set; }
}

/// <summary>A note written in the game, kept until the site has it.</summary>
[Serializable]
public sealed class PendingNote
{
    public Guid Id { get; set; }

    public string Character { get; set; } = string.Empty;

    public Guid? Session { get; set; }

    public string Body { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public DateTime At { get; set; }

    /// <summary>How many times the site answered "gone" (its character not there).</summary>
    public int GoneTries { get; set; }
}

/// <summary>A theory's answer given in the game, kept until the site has it.</summary>
[Serializable]
public sealed class PendingAnswer
{
    public Guid Id { get; set; }

    public string Status { get; set; } = string.Empty;
}

/// <summary>What the plugin remembers of one character (by its key): where it's been, who it's met, its theories.</summary>
[Serializable]
public sealed class CharacterMemory
{
    public string Name { get; set; } = string.Empty;

    public string World { get; set; } = string.Empty;

    /// <summary>Zones visited since the journal began (a zone event's <c>first</c>: first since the journal began, not ever).</summary>
    public HashSet<uint> Visited { get; set; } = new();

    /// <summary>People met, in the order they were met.</summary>
    public List<MetPerson> People { get; set; } = new();

    public List<SealedTheory> Theories { get; set; } = new();

    /// <summary>Theories answered here (the site's due ones too), so they aren't asked again before the site hears.</summary>
    public HashSet<Guid> Answered { get; set; } = new();

    /// <summary>Theories sealed again (not settled yet): not asked again until the next chapter closes.</summary>
    public HashSet<Guid> Snoozed { get; set; } = new();

    /// <summary>When the journal's first session with this character began (a place is "for the first time" only after it).</summary>
    public DateTime? JournalSince { get; set; }

    /// <summary>When the Welcome back card last showed (once a login).</summary>
    public DateTime? WelcomedAt { get; set; }

    /// <summary>The site's last "Flag it in game" this character acted on (FlagPoll), so it isn't done twice.</summary>
    public DateTime? FlaggedFromSite { get; set; }

    /// <summary>The tracker's ribbon folded to its tab (its chevron), kept across logins.</summary>
    public bool TrackerFolded { get; set; }
}

/// <summary>The plugin's settings, kept by Dalamud with the plugin's config.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>The token from the site (Settings → Story Journal → The plugin), "sjp_" and 43 letters. Never logged.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>This install's own salt for characters' keys (<see cref="Core.SiteLink.CharacterKey"/>): made once, so the site never sees a content id.</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>Nothing is recorded while paused (/sj pause); what's queued still goes.</summary>
    public bool Paused { get; set; }

    /// <summary>Quests, cutscenes, places, duties and levels are recorded (off: only what you save yourself).</summary>
    public bool RecordStory { get; set; } = true;

    /// <summary>Side quests, and duties the main story doesn't unlock (roulettes and the like), are recorded too.</summary>
    public bool RecordSideContent { get; set; }

    /// <summary>People met in quests' dialogue are recorded (the speaker's name, the first time).</summary>
    public bool RecordPeople { get; set; } = true;

    /// <summary>Minutes of nothing happening before a session ends (the next thing starts a new one): 10 to 180 (<see cref="Core.SessionClock.IdleFor"/>).</summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>The key that saves the line on screen ("F11"; empty: none).</summary>
    public string LineKey { get; set; } = "F11";

    /// <summary>The key that takes a photo without the HUD.</summary>
    public string PhotoKey { get; set; } = "F12";

    /// <summary>The key that opens the quick note.</summary>
    public string NoteKey { get; set; } = "Ctrl+J";

    /// <summary>The small tracker over the game.</summary>
    public bool ShowTracker { get; set; } = true;

    /// <summary>The tracker shows how far each partner is (and the pace lock).</summary>
    public bool TrackerPartners { get; set; } = true;

    /// <summary>The Welcome back card (when you've been away as long as the site's preference says).</summary>
    public bool ShowWelcome { get; set; } = true;

    /// <summary>The end of session card at logout and /sj end.</summary>
    public bool ShowWrapUp { get; set; } = true;

    /// <summary>A card when a chapter closes.</summary>
    public bool ShowMilestones { get; set; } = true;

    /// <summary>A short line in chat when something's saved, a session starts or the link changes.</summary>
    public bool ChatMessages { get; set; } = true;

    /// <summary>Quiet times: while you're in a party, everything that would come to you waits until you're on your own.</summary>
    public bool HoldInParty { get; set; }

    /// <summary>Where the tracker's ribbon hangs along the top of the screen (0 left, 1 right; dragged by its head).</summary>
    public float TrackerAt { get; set; } = 0.5f;

    /// <summary>Calm: nothing moves (windows and cards simply appear), as the system's reduced motion does.</summary>
    public bool Calm { get; set; }

    /// <summary>Arrival cards and Who's that? are quieter than the site's setting says (never louder): -1 the site's, else a Whispers value.</summary>
    public int WhispersHere { get; set; } = -1;

    /// <summary>What the plugin remembers of each character, by key.</summary>
    public Dictionary<string, CharacterMemory> Characters { get; set; } = new();

    /// <summary>Notes written in the game and answers given, until the site has them.</summary>
    public List<PendingNote> PendingNotes { get; set; } = new();

    public List<PendingAnswer> PendingAnswers { get; set; } = new();

    public CharacterMemory Memory(string key)
    {
        if (!Characters.TryGetValue(key, out var m)) Characters[key] = m = new CharacterMemory();
        return m;
    }

    public void Save() => Services.PluginInterface.SavePluginConfig(this);
}
