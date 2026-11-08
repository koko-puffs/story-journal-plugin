# Story Journal (Dalamud plugin): the details

How the plugin works, where things are, and why. The site's side (the database functions, the
recaps, the Codex's pages) is in kokos-tools' DETAILS.md, under Story Journal; the contract between
the two halves is kokos-tools' `supabase/migrations/20261108150000_sj_story_journal.sql` (its
comments) and the build brief, `mockups/story-journal/BRIEF.md`, with the mock-up beside it
(`index.html`, the "In-game plugin" screens).

## The idea

A journal of your journey through Final Fantasy XIV's main story, written as you play and read on
kokos-tools.com. The plugin is the half that's in the game: it notices what happens (quests taken
and finished, with the game's own journal for them; cutscenes; places; duties; levels; people met)
and keeps it in a queue on this PC until the site has it; it reads where you are in the main story
(the next step, why you're doing it, what's ahead) and tells the site that too; and it makes coming
back easy in the game itself: a Welcome back card, the journal window, a small tracker, cards about
places and people, an end of session card, a card when a chapter closes.

**Spoilers are the one rule above all.** Nothing the plugin shows goes past the player's own last
finished quest, unless they turn spoilers off on the site:

- Story text is only ever the game's journal of quests the player has **finished**, or the current
  quest's journal **up to its current step** (and its objectives for that step).
- Chapter names ahead aren't sent to the site or shown (`progress.chapters[].name` is null until a
  chapter's reached).
- Duties ahead: on **strict** the tracker says only "a duty in N quests", and the journal's Now tab
  shows the next one behind **Show**; on **gentle** their names show; **off** shows everything.
- The Codex shows an entry only once one of its names has appeared in what the player has seen, and
  a layer only when its needs are seen and its expansion reached (all of it with spoilers off).
- The next main-story quest's name (and where it's given) shows before it's taken: the game's own
  scenario guide shows it too.

## Things to know

- **It's new and untested in the game.** It builds against Dalamud 15 (API 15) and its game-free
  code is tested (160 tests), but the readings of the game below marked *unverified* have not been
  seen working in a real client. Expect rough edges, and say what's wrong.
- **What leaves the game**: the events below, the main story's reading, and your own words (notes,
  theories, the end of session card). Only the line of dialogue you save leaves, never the chat.
  Photos only when you take them. Other players' names are never written down.
- It doesn't act in the game: no clicks, no movement. The only things it changes are the map's flag
  (when you press Flag on map) and the HUD, hidden for a moment while a photo is taken.

## The site (`Core/SiteLink.cs`, `Core/SiteRpc.cs`, `Session/SiteSync.cs`)

Linked as Market Ledger's plugin is: a token made on the site (Settings → Story Journal → The
plugin), `sjp_` and 43 letters, pasted into the plugin's Settings (shown masked: `sjp_••••••••abcd`).
Only its SHA-256 is kept on the site. The plugin calls the site's database functions with the site's
publishable key (as anon), the token in the body, never in an address or a header, and never logged.
Every call has a wait that runs out and stops when the plugin unloads; none runs on the framework
thread (answers come back to it through a queue drained each frame).

A **character's key** is the plugin's own id for a character: the SHA-256 of
`story-journal:<content id>:<salt>`, 32 hex letters, with a salt made once per install
(`Configuration.Salt`). The site never sees a content id.

The calls, as the migration has them (arguments `p_token`, `p_key`, `p_session`, `p_events`,
`p_progress`, `p_character`, `p_note`, `p_theory`, `p_id`, `p_status`; JSON fields in camelCase):

| Call | When | What |
| --- | --- | --- |
| `sj_plugin_hello(p_token, p_character)` | at login, after linking, every 15 min | `p_character` `{ key, name, world, progress? }`. Its answer `{ account, prefs, partners, theories, sealed, recent, notes }` is kept (`HelloAnswer`) for the windows: the spoiler and whisper settings, the welcome card's hours, partners for the pace lock, theories due, the last three sessions for the glance, unresolved notes. |
| `sj_plugin_sync(p_token, p_key, p_session, p_events, p_progress)` | every 30 s, and within 10 s of something happening | One session's events at a time from the queue (≤ 200, ≤ 500 KB: the site takes 600,000 bytes), the session `{ id, startedAt, endedAt?, lastAt, title?, moods?, note? }`, and `progress` when it changed since the site last took it (null otherwise). Ids are made here, so a resend adds nothing. With nothing queued, the session under way goes alone when the reading changed. |
| `sj_plugin_note(p_token, p_key, p_note)` | as soon as it can | `{ id, session?, body, subject? }`; `session` only once the site has that session. |
| `sj_plugin_theory(p_token, p_key, p_theory)` | as soon as it can | `{ id, body, subject?, checkAfter? }`: `checkAfter` main-story quests from now (1–5000) the site asks about it. |
| `sj_plugin_theory_answer(p_token, p_id, p_status)` | when you settle one | `called`, `missed`, `changed` (or `sealed` again). |
| `POST /functions/v1/sj-plugin-photo` | once the site has the photo's session | multipart: `token, character, session, id, at, caption, image` (a JPEG, ≤ 6 MB, ≤ 8192 px a side), header `apikey`. Answers `{ id, data }` or `{ message, hint }`. |

Each kind of call is paced well under the site's hourly limits for a person (hello 240, sync 600,
note 240, theory 120, photo 120): hello about 4 an hour, sync at most one each 10 s (360) and usually
two a minute, notes and theories one each 4 s while there are some, photos one each 8 s.

**Refusals** (PostgREST's `{ message, hint, code }`; `Core/SyncPolicy.cs`, tested):

- `token`: the token's no good (revoked, never made, an account that can't use it). Nothing more is
  sent; the window says "Link refused" and Settings asks for a new token. The queue is kept.
- `off`: Story Journal (or cloud saving) is switched off on the site. Everything is kept; tried again
  in 10 minutes. The header says "Switched off on the site".
- `rate`: too often. That kind of call waits 5 minutes (photos 15), then carries on.
- `size` and `data` on a sync: the batch is halved, and again, down to the one event at fault, which
  is set aside (`queue/aside.jsonl`: kept, never sent again, never deleted). A single event refused
  as `data` is checked first by sending its session alone: if the session itself is refused (its time
  off by more than the site takes, or it belongs to someone else), the session and all its events
  are set aside; if not, only the event is.
- `gone`: the thing isn't there (yet). A note or theory for a character the site hasn't heard of
  waits for the next sync; a photo whose session hasn't reached the site waits for it; an answer for a
  theory that's gone is let go.
- No answer, a 5xx, a 429 without a hint: tried again after a backoff (5 s, doubling to 5 minutes).

Photo function hints: `token`, `off`, `rate`, `gone`, `upload`, `server` as above (kept and retried,
or relink); `image`, `data`, `form` set the photo aside (`queue/aside-photos/`).

## The queue (`Core/EventQueue.cs`)

Everything waiting for the site, in the plugin's config folder (`queue/`), so nothing is lost when
the site can't be reached, the game closes or the plugin is reloaded:

- `events.jsonl`: one event a line (`{ id, kind, at, data, session, character }`), appended (and
  flushed) the moment it happens. An event leaves only when the site has said it has it; the file is
  then rewritten through a temporary file, so a crash mid-write leaves the old one. A line read twice
  (a crash between an append and a rewrite) is kept once; a line that can't be read is moved to
  `aside.jsonl`, not dropped.
- `sessions.json`: the sessions those events belong to, with whether the site has each (`known`),
  whether it's still to be told something (`dirty`: its end, its last time, the card's words), and
  whether the card's words were sent (`wordsSent`: after that they aren't sent with the session's
  later events, so what you change on the site stays). The newest 40 ended sessions with nothing
  left to send are kept, the rest forgotten. Events whose session went missing still go, with a
  session made from their own times.
- `photos/`: each photo a JPEG and its details, until the site has it.
- `aside.jsonl`, `aside-photos/`: what the site refused outright, kept for you.

## Sessions (`Core/Sessions.cs`, `Session/Journal.cs`)

A session is one sitting with one character. It starts with the first thing that happens after login
(moving counts), and ends at logout, at `/sj end`, or after **30 minutes** (Settings: 10–180) with
nothing happening: it then ends when it was last heard of, and the next thing starts a new one.
Another character's first event ends the last one's session. A reload picks the session under way up
again; one left open long ago (a crash) is ended at its last time. Its id is made here.

The **end of session card** shows at logout and `/sj end` (not after an idle: you're away), for a
session with anything in it or at least 10 minutes long.

## Recording (`Session/Recorder.cs`)

Cheap polling on the framework thread, twice a second, never during a loading screen. At login (or
when the plugin loads) a **baseline** is read first: the quests in the log, the zone you're in, your
job and level. So what's already so isn't written as new. While paused (`/sj pause`) nothing is
written but the baseline keeps up, so resuming doesn't write what happened meanwhile.

| Kind | How it's noticed | Data |
| --- | --- | --- |
| `accept` | a new id in QuestManager's quest log (`NormalQuests`) | `{ id, name, msq }` |
| `quest` | an id gone from the log that `IsQuestComplete` says is done (checked for 10 s: the game may mark it a moment after; gone and not done is abandoned, not written) | `{ id, name, level, msq, msqIndex?, expansion, chapter?, chapterDone?, expansionDone?, place, journal, people }`: the journal is the quest's whole text sheet (made on a worker), cut to 12 entries of 1,200 letters (the first and the last eleven, so the ending the recaps read is kept); `people` the people met named in it |
| `cutscene` | `OccupiedInCutSceneEvent`, `WatchingCutscene78` or `WatchingCutscene`, from rise to fall (3 s at least) | `{ questId?, place, seconds }`: the current main-story quest when it's taken |
| `zone` | a new territory once the loading screen's gone (not inside a duty) | `{ id, name, region, first }`: `first` from the character's visited set (`CharacterMemory.Visited`, in the plugin's config) |
| `duty` | IDutyState's start (or being in a duty without it: from then) and completion; written when you leave | `{ id, name, type, cleared, seconds }` (`id` the ContentFinderCondition row, `type` "dungeon", "trial", "raid"…) |
| `level` | a higher level on the same job | `{ job, level }` |
| `person` | the speaker of the `Talk` window during a quest's talk or a cutscene (`OccupiedInQuestEvent`, `OccupiedInCutSceneEvent`), the first time this character met them; not "???" or yourself | `{ name, place, questId? }` |
| `line` | `/sj line` or its key: the last `Talk` line | `{ speaker, text, questId?, place }` |
| photo | `/sj photo` or its key | through the photo function, never a sync |

Side quests and duties the main story doesn't unlock are written only with **Side content too** on
(off by default, as the mock-up's "pause while I'm not on the main story"). People met can be
switched off. Turning **Quests, cutscenes, places, duties and levels** off leaves only what you save.

The `Talk` window is read through Dalamud's addon lifecycle (set up, refresh, requested update):
the speaker from its name node (`AddonTalk.AtkTextNode220`), the words from its text node
(`AtkTextNode228`), falling back to its first two values. *Unverified*: the nodes' meaning (taken
from other plugins that read it).

## Photos (`Session/Photos.cs`)

As Glamour Studio's shots: the HUD is hidden (as Scroll Lock does) for a few frames, the game's next
frame is grabbed before ImGui draws (no plugin window in it either), the HUD shown again; on a
worker the picture is made at most 2560 on its long side (1920 if that's still too big) and written
as a JPEG (WIC through Dalamud's texture readback) at the first quality under 5 MB, then kept in the
queue. It goes to the site once the site has its session. *Unverified in the game for this plugin*
(the same code works in Glamour Studio's).

## The main story (`Core/Msq.cs`, `Game/Sheets.cs`, `Session/Story.cs`)

**The catalog** is read once each time the plugin loads, on a worker, from the game's sheets:

- The quests: the `ScenarioTree` rows that are quests (a row's id is its quest's). Should that give
  fewer than 300 (it isn't what was expected), the quests whose journal genre is in the journal's
  **Main Scenario** section (section 0, or one so named) instead. The log says which was used.
- Their order: each quest after the quests it needs (`PreviousQuest`, only those in the catalog),
  then by expansion, level and id. Where `PreviousQuestJoin` is 2 any one of the prerequisites is
  enough (read as "at least one"; 1 as "all"). A loop (there shouldn't be one) is broken by the same
  order. This order is the same for every player, so a quest's **index** (how many come before it)
  is the number every spoiler check on the site compares.
- Chapters: the scenario tree's `QuestChapter` row (or the quest's own `QuestChapter` row), its
  `Redo` row's `QuestRedoChapterUI.ChapterName`. If that names fewer than half the quests, chapters
  instead start at each `QuestRedoChapterUI.Quest` and run on to the next.
- The duty each quest unlocks: `InstanceContentUnlock` → `InstanceContent.ContentFinderCondition`:
  its name, its `ContentType` in a word, its `ClassJobLevelRequired`.
- Expansions: `ExVersion`'s names.

*Unverified*: that every `ScenarioTree` row is a main-story quest's id; the meaning of
`PreviousQuestJoin`'s values; that `QuestChapter.Redo` and `QuestRedoChapterUI` share ids (the
fallback is there because of this); that `QuestRedoChapterUI.Quest` is a chapter's first quest.
Check the log line after loading and the Now tab's chapter against the game's New Game+ list.

**The player's road** (`MsqCatalog.Path`, tested): quests not done that the player can't be on are
left out of the counts:

- a quest not done with a done quest after it on its chain (another starting city's road, a Grand
  Company not chosen: the player went past without it);
- a quest shut out by one taken or done (`QuestLock`: the Grand Companies' choice);
- where roads meet and any one will do, the roads not taken once one of them is begun (a quest in it
  done or in the log).

Before the first quest of a road is begun every road counts (a few minutes for a new character).

**Where the player is**: QuestManager's quest log (quests and their steps) and `IsQuestComplete` for
each main-story quest (read on the framework thread when the log changes, and once a minute), and
the scenario tree's quest (`AgentScenarioTree.Data->MainScenarioQuestIds[MSQPathIndex]`; its fourth
slot is the last finished when none is taken). The current quest is the scenario tree's when it's
on the road and not done, else the first on the road not done. The game's ushort quest ids are the
sheet's row less 65536; both are handled (`QuestIds`). From these, on a worker (`Story`):

- **Progress** (the brief's shape, `Core/Progress.cs`): level and job; `msq { done, total, index }`;
  every expansion with its counts; every chapter in order (names only once reached); the current
  quest with its step, chapter, expansion, objectives for the step, journal so far, the last journal
  entry of the quest before, and the people met named in it; the next three duties
  (`questsAway` counts the current quest as 1); and a good place to stop, the nearer of the chapter's
  end and the quest before the next duty (the chapter when they're the same). Kept under the site's
  32 KB: the current journal's earliest entries go first, then later expansions' chapters.
- `chapterDone` and `expansionDone` on a quest event: every quest of its chapter (expansion) on the
  road is done with it.
- The Codex's corpus: the whole journal of every main-story quest finished, the current quest's so
  far, the people met and the places been.

**A quest's text** (`Game/Sheets.cs`, `Core/JournalText.cs`): the sheet `quest/` + the first three
digits of the number ending its `Id` + `/` + its `Id` (e.g. `quest/006/ManFst001_00655`), read as
`RawRow` columns 0 (the key) and 1 (the text). `…_SEQ_nn` rows are the journal's entries, in number
order; `…_TODO_nn` the objectives, the nth belonging to the step in the Quest sheet's
`TodoParams[n].ToDoCompleteSeq`. The journal "up to a step" is the entries numbered below the
quest's sequence (the first as soon as it's taken; all of them at the last step, 255). Text is
tidied: runs of spaces as one, no space before punctuation (where the game fills in a name, which
`ExtractText` leaves out). *Unverified*: that SEQ numbering lines up with the quest's sequence this
way, and the empty spots where the game names the player.

**Flag on map** (`Game/Quests.cs`): the to-do for the current step names a `Level` (where to go):
`AgentMap.SetFlagMapMarker(territory, map, position)` and the map opened there. A step without a
place says so. *Unverified*.

## The Codex (`Core/Codex.cs`, `Session/CodexStore.cs`)

`https://www.kokos-tools.com/story-journal/codex.json` (`{ version: 1, entries: [{ key, kind, name,
aliases, zone, layers: [{ text, needs, expansion? }], whisper }] }`), fetched on a worker, kept in the
plugin's config folder (`codex.json`) and fetched again once a day. Until then the copy kept is
used, or none: the Codex is empty, and only the people and places the plugin saw itself show.

An entry is **seen** once any of its names (name or aliases) appears as a whole word, whatever its
case, in the corpus above (no letter or digit just before or after; curly and straight apostrophes
are the same). A layer shows when its entry is seen, every name in its `needs` is seen too, and the
player's expansion (the current quest's) is at least its `expansion`; with spoilers off, every layer.
The arrival card's sentence is the `whisper` of the `place` entry whose `zone` is the game's zone
name. "Ask the journal" (the Codex tab's field) finds seen entries whose name or alias is the word,
starts with it, or holds it. The Codex's words are Koko's, proofread by Koko; the plugin only shows
them.

## The windows (`UI/`)

All in the look of Koko's plugins (the kit in `UI/Kit`: Geist, a black ground, hairline edges, pill
buttons) with Story Journal's accent, crystal blue `#6cb6ff`, and the mark's tile blue `#2a6fd6`.
Every card waits for a calm moment (`InGame.Calm`: not in combat, a duty, a cutscene, a loading
screen or Group Pose); Dalamud hides plugin windows during cutscenes and Group Pose anyway.

- **Welcome back** (`WelcomeWindow`): after login, when the last session (as hello says, not the one
  under way) ended at least the site's `welcomeHours` ago (0: every login), once the story's read.
  "Last played Saturday, 2 days ago"; three lines of story at a glance (the last journal lines of the
  last main-story quests finished, from hello's `recent`; else what the sessions did instead); the
  next step (the current quest and its objectives); your thought from the last end of session card
  (else your newest unresolved note); **Flag on map**, **Longer recap** (the site's recap page),
  **Who's who** (the Codex tab), **Got it**; and the tick for showing it (the plugin's own switch;
  the hours are the site's). It closes itself once you've walked 8 yalms. `/sj welcome` opens it.
- **The journal** (`MainWindow`, `/sj`):
  - *Now*: the expansion and chapter, job and level, the expansion's bar; the pace lock's warnings;
    the current quest and its objectives (Flag on map); **Why you're here** (the last entry of the
    quest before, then this quest's journal so far, its last three); **People in this** (people met
    named in it; each opens their Codex entry); **Ahead** (the next duty, behind Show on strict; the
    next three on gentle and off) and a good place to stop; a quick note (Save note, Seal as theory,
    Open on the website). Linking, when not linked.
  - *This session*: playing for, quests, cutscenes, photos; Save the line, Take a photo, End
    session; everything written in it, newest first; what's waiting for the site.
  - *Codex*: Ask the journal; the seen entries by kind (people, places, lore, words), each opening to
    its layers so far, when you met them, your notes about them, a note about them, its page on the
    site; people met and places been that the Codex has no entry for.
  - *Notes*: a note (and who or what it's about); notes waiting to send; your unresolved notes (from
    hello).
  - *Theories*: seal one (asked about in 5, 10 or 25 main-story quests, or not); those due, to
    settle (called it, missed it, changed my mind); those sealed here; how many are sealed in all.
  - *Settings*: below.
- **The tracker** (`TrackerWindow`): the expansion and its percentage, a bar, the next duty ("4
  quests to the next duty"; its name too unless spoilers are strict), the pace lock's warning, and
  "recording paused". Movable; its title opens the journal. Off in Settings.
- **Arrival cards** (`ArrivalWindow`): a place's whisper, the first time you reach it (whispers
  *some*) or every time (*chatty*); never with *quiet*. Waits up to 2 minutes for a calm moment, shows
  for 14 s. More (its Codex entry), Fewer of these (whispers one step quieter, here).
- **Who's that?** (`WhoWindow`): target someone you've met (not another player) with whispers *some*
  or *chatty*: their Codex name and first layer so far, when and where you met them, lines saved and
  notes about them.
- **Capture toasts** (`ToastWindow`): line saved, photo taken, note saved, theory sealed or answered,
  the link made, problems. Top right, 6 s each, click-through.
- **Quick note** (`QuickNoteWindow`, its key or `/sj note`): save it, or seal it as a theory (asked
  about in 10 quests).
- **End of session** (`WrapUpWindow`): played, quests, cutscenes, photos; the expansion's percentage
  then and now; a title suggested from the session (a chapter that closed, else a duty cleared, else
  a first place, else the last main-story quest finished: as the site's own suggestion); moods (up
  to 6, sent lower case); a thought for next time. Save sends them with the session; Open the entry
  opens it on the site.
- **Milestones** (`MilestoneWindow`): a main-story quest finished that closed its chapter: the
  chapter's name, its expansion, the quest's last journal line, how many quests the chapter held; the
  theories due, to settle there; a photo for the chapter's page (captioned with its name), or later.

The **pace lock**: hello's partners with your lock on (`lock`); you're warned when you're past one
(their `msqIndex` below yours) or the next main-story quest on your road would take you past them
(its index above theirs). Partners and locks are made on the site.

## Settings (`Configuration.cs`, the Settings tab)

- **Account**: linked to whom, with which character; the token masked; Unlink. Not linked: the token
  field (masked as you type), Get a token (the site's Settings → Story Journal), Link.
- **Recording**: quests, cutscenes, places, duties and levels (on); side content too (off); people
  met (on); a new session after N minutes (30); Pause recording.
- **Spoilers and whispers**: the site's spoiler setting, shown (one setting, shared: change it on the
  site; the plugin has no call to change preferences); how chatty: the site's, or quieter here.
- **Windows**: the tracker, its pace lock, Welcome back, end of session, milestones, lines in chat,
  and Welcome back now.
- **Keys**: save the line (F11), photo (F12), quick note (Ctrl+J): any key or combination, empty for
  none. A key fires once when it goes down with exactly its modifiers, and the game doesn't see it
  then; not while typing in the game's chat or a plugin's field. Held down it fires once.
- **Offline**: what's waiting, what was set aside, the Codex's state; Send now, Fetch the Codex again.

Per character (`CharacterMemory`, by key): the zones visited, the people met (name, place, when, the
quest), the theories sealed here (with the index at which to ask), theories answered, when the
welcome card last showed. Notes and answers wait in `PendingNotes` and `PendingAnswers` until sent.

## Commands

`/sj` (or `/storyjournal`) opens the journal; `/sj line` saves the line on screen; `/sj photo
[caption]` takes a photo; `/sj note <text>` keeps a note (`/sj note` alone opens the quick note);
`/sj theory <text>` seals a theory, asked about in 10 main-story quests (`/sj theory` alone opens the
Theories tab); `/sj end` ends the session (its card); `/sj pause` and `/sj resume`; `/sj settings`;
`/sj welcome` opens the Welcome back card.

## Threads

Nothing blocks the framework thread: the catalog, quests' text, the story's reading and the Codex's
matching run on workers (one reading at a time, started when what it depends on changes); every call
to the site and every photo's making and sending too. The framework thread reads the game (cheap
reads, twice a second), appends a line to the queue's file per event, and draws. The Codex's pattern
cache is thread-safe; Lumina's sheets are read from both (they're read-only).

Every game read is guarded (null pointers, not logged in, a loading screen, a patch that moved
something): it answers "nothing" rather than throwing. Every handler (framework update, login and
logout, the duty's events, the addon lifecycle's listeners, commands, the UI builder's) is let go of
in `Dispose`, which also keeps the session under way open for a reload to pick up.

## Layout

```
StoryJournal/
  Core/        game-free and tested: the site link and calls, events, sessions, the queue, the main
               story's catalog and progress, journal text, hello and the welcome card, the pace
               lock, the Codex, keys, photos' upload, the sync's policy
  Game/        reading the game: the player, quests, the scenario tree, sheets, places and duties,
               the Talk window, the map flag, keys
  Session/     the recorder, the story, the sync, photos, the Codex's copy, the journal (the heart)
  UI/          the windows; UI/Kit the shared kit; UI/Model the icons' paths (tested)
  Resources/   the Geist fonts (OFL)
StoryJournal.Tests/   xUnit, compiling Core and UI/Model directly
tools/         icon.svg and make-icon.mjs (renders StoryJournal/images/icon.png with Playwright)
```

## Developing

```sh
dotnet test StoryJournal.Tests/StoryJournal.Tests.csproj -c Release
dotnet build StoryJournal.sln -c Release   # needs Dalamud (XIVLauncher's dev folder, or DALAMUD_HOME)
# → StoryJournal/bin/x64/Release/StoryJournal/latest.zip
node tools/make-icon.mjs                   # the icon, after changing tools/icon.svg
```

For a dev build in the game: Dalamud Settings → Experimental → Dev Plugin Locations, the full path to
`StoryJournal/bin/x64/Debug/StoryJournal.dll`.

## Releasing

As the other plugins: push a tag `v0.1.0` (or run the Release workflow with a version). The workflow
tests, builds with DalamudPackager, makes the GitHub release with `StoryJournal.zip` and points
`repo.json` at it. Needs Settings → Actions → General → Workflow permissions → Read and write. Koko's
repository (`https://www.kokos-tools.com/dalamud/repo.json`, kokos-tools' `api/dalamud-repo.js`)
already has this repository in its `PLUGIN_REPOS`, so players see it there as soon as `repo.json`'s
links work. `repo.json` already describes 0.1.0, with links to a v0.1.0 release that doesn't exist
until it's made: until then the download fails, so release before telling anyone.

## What to check first in the game

1. The log line after loading: "read the main story from the scenario tree: N quests, M chapters".
   N should be in the high hundreds; M in the dozens.
2. The Now tab's quest, step, objectives and chapter against the game's journal and the New Game+
   list.
3. Take a quest, finish one (This session); watch a cutscene; enter and clear a duty.
4. `/sj line` during a dialogue; `/sj photo`; F11 and F12.
5. Flag on map.
6. Link, and see the session on the site.

## Release notes

### 0.1.0

The first version: sessions, quests taken and finished (with the game's journal), cutscenes, places,
duties, levels, people met, lines saved and photos, kept on this PC until the site has them; the main
story's reading (progress, chapters, the duties ahead); Welcome back, the journal window, the
tracker, arrival cards and Who's that?, capture toasts, the end of session card and milestones;
notes and theories; the pace lock.
