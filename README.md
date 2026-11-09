# Story Journal (Dalamud plugin)

The in-game half of [Story Journal](https://www.kokos-tools.com/story-journal) on kokos-tools.com:
a journal of your journey through the main story, written as you play.

> **Status: new and untested in the game.** It compiles against Dalamud 15 (API 15) and its game-free
> code is unit-tested, but nobody has played with it yet. Expect rough edges, and say what's wrong.

## What it does

- **Writes down your story as you play**: the quests you take and finish (with the game's own
  journal for them), cutscenes, the places you reach, the duties you clear, your levels, the people
  you meet. Kept on your PC until the site has it, so a lost connection loses nothing.
- **Makes coming back easy**: a Welcome back card with three lines of story and the next step,
  flagged on your map in one click (or from the site's Today); a journal window that says why you're
  doing what you're doing; a ribbon tracker hanging from the top of the screen with what's next.
- **Lets the world talk back**: a sentence about a place the first time you reach it, a reminder of
  who someone is when you target them, a Codex that grows with you.
- **Keeps your moments**: save the line on screen (F11), take a photo without the HUD (F12), jot a
  note (Ctrl+J), seal a theory under wax and settle it later, give each evening a title and a mood.

It looks like the site's Story Journal ("Ink & Ribbon"): warm ink, paper words, a cherry-red ribbon
where you are. Calm, in Settings, keeps it all still.

**No spoilers.** Nothing it shows goes past your own last finished quest, unless you turn spoilers
off on the site. Only the line you save leaves the game, never the chat; other players' names are
never written down.

## Installing it

1. In game, `/xlsettings` → **Experimental** → **Custom Plugin Repositories** → add
   `https://www.kokos-tools.com/dalamud/repo.json` (Koko's repository: already there if you use
   another of Koko's plugins) → tick it → **Save and Close**.
2. `/xlplugins` → **All Plugins** → **Story Journal** → **Install**.
3. `/sj` opens it.

## Using it

1. Link it: on the site, Settings → Story Journal → In-game plugin makes a token; paste it in the
   plugin's Settings (or on its first page).
2. Play. `/sj` shows where you are and why; the site shows the rest.
3. `/sj line`, `/sj photo`, `/sj note <text>`, `/sj theory <text>`, `/sj ask <name>`, `/sj end`, `/sj pause`,
   `/sj resume` and `/sj settings` work too (for macros).

## Building it

```sh
dotnet test StoryJournal.Tests/StoryJournal.Tests.csproj
dotnet build StoryJournal.sln -c Release   # needs Dalamud (XIVLauncher's dev folder, or DALAMUD_HOME)
```

How it all works is in [DETAILS.md](DETAILS.md). Licence: AGPL-3.0; the Geist, Fraunces and Literata fonts are under the OFL.
