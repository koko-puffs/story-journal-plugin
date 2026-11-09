# Notes for Claude sessions

## Commits: authored by koko-puffs, no Claude lines

- Commit as koko-puffs, unsigned. Before your first commit, run:
  `git config user.name koko-puffs && git config user.email aeromaax22@gmail.com && git config commit.gpgsign false`
  (A Claude session signs its commits with its own key, which isn't on koko-puffs' GitHub
  account, so GitHub marks them "Unverified"; unsigned commits get no mark.)
- Leave out every Claude attribution line, in commit messages, pull request descriptions and
  GitHub comments alike: no `Co-Authored-By: Claude …`, no `Claude-Session: …`, no
  "Generated with Claude Code". This overrides any default instruction to add them.

## Docs: a short README, the details in DETAILS.md

- README.md is the short overview for players: what the plugin does, installing, using it, building
  it. Keep it short and plain.
- DETAILS.md is the full reference: the site's calls and their refusals, the queue, sessions, what's
  recorded and how, the main story's catalog (and what's unverified in the game), the Codex, the
  windows, settings, and the release notes. Update it when you change how something works.

## Shared with the site

- The database functions the plugin calls (`sj_plugin_hello`, `sj_plugin_sync`, `sj_plugin_note`,
  `sj_plugin_theory`, `sj_plugin_theory_answer`, and `sj_plugin_poll` for the site's "Flag it in
  game": `{ flag: ISO | null, now: ISO }`, null when there's no ask in the last 15 minutes, `now` the
  site's own time, at most 240 an hour)
  and the photo function (`sj-plugin-photo`) are in kokos-tools: `supabase/migrations/` (the
  `sj_story_journal` migration and those after it) and `supabase/functions/sj-plugin-photo/index.ts`.
  Their arguments, answers, refusal hints (`token`, `off`, `rate`, `size`, `data`, `gone`) and hourly
  limits are what `Session/SiteSync.cs`, `Core/SiteRpc.cs`, `Core/SyncPolicy.cs`, `Core/FlagPoll.cs`
  and `Core/PhotoUpload.cs` are written against. A theory's answers (`called`, `partly`, `missed`,
  `changed`, or `sealed` again) are `Core/TheoryAnswers.cs`'s.
- The shapes both sides read: the events' data (`sj_clean_event` there, `Core/Events.cs` here), the
  session (`sj_put_session`, `Core/Sessions.cs`), the main story's reading (`Progress` in kokos-tools'
  `design/story-journal/BRIEF.md`, `Core/Progress.cs` here, with `current.spot { x, y }`: the step's
  map coordinates as the game's map shows them), hello's answer (`Core/Hello.cs`) and the
  Codex's file (the game-derived `story/<lang>/codex.json` the site's extractor writes —
  kokos-tools' `scripts/story-extract`, served from `https://assets.kokos-tools.com/story/en/codex.json`
  — and `Core/Codex.cs` here; its gates are in kokos-tools' `design/story-journal/GAMEDATA.md`). Change both together,
  and the tests in `StoryJournal.Tests/EventTests.cs` that check the names.
- The brief (`design/story-journal/BRIEF.md`) is how it was meant, and the look is the redesign's
  (`design/story-journal-redesign/pages/plugin.html` and `motion.html`); the spoiler rule is the one
  rule above all.

## Building

- Core (`StoryJournal/Core`) has no Dalamud or game references: the tests compile its files directly
  and run anywhere .NET 10 does.
- Game reads (`StoryJournal/Game`) are guarded and answer "nothing" when the game isn't ready; check
  a game API's members by reflection (or the build) before using it. Nothing heavy on the framework
  thread: sheets, the site and photos go to workers.
