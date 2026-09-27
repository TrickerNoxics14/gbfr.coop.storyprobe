# GBFR Story Mode While Online

Lets **Chapter Select** and **Fate Episode** be entered while an online session
is active, so story content can be played co-op.

Built and tested against **Endless Ragnarok 2.0.5 and 2.0.6**. On 2.0.6 every piece of game code the mod uses was checked byte for byte against 2.0.5: it is the same code, only moved.

---

## Install

1. Drag the zip into Reloaded-II (or copy this whole folder into
   `Reloaded-II\Mods\`).
2. Enable **GBFR Story Mode While Online**.
3. Launch through Reloaded-II.

The unlock is **on by default** — no config editing needed.

### Both machines need it

| Must match | Why |
|---|---|
| The mod, installed on both | Without it the other player's Chapter Select stays locked |
| Same **mod** version | Behaviour must be identical on both clients |
| Same **game** version (2.0.6 today) | Signatures resolve to build-specific addresses |

Each player also needs their **own Steam account owning Relink** — one account
cannot run the game on two machines at once.

---

## The one rule that matters

**Both players' saves must be at roughly the same point in the story.**

This is not optional. Each client resolves story content from its *own* save
position, and nothing in the game shares a story position between players. With
mismatched saves you get:

- infinite loading screens that never resolve
- one player dropped into completely different chapters
- being flung between areas when talking to NPCs

With matched saves, the same mission loaded on both clients within **3 seconds**
of each other and played normally.

---

## Known limitations

These are real and currently unsolved:

- **AI companions that join mid-quest never appear online.** In the Prologue
  and the Chapter 1 opening you start alone with Lyria and the crew is scripted
  to arrive partway through. Online, the scripted scene plays — the dialogue
  triggers on cue — but the characters are invisible during it and absent
  afterwards. If that fight needs a Link Attack, it cannot be finished. See
  below.
- **Story world state is not shared.** A bridge one player activates collapses
  only on their screen; scripted triggers and destructible geometry stay local.
  In Chapter 7 the hole the volcano boss opens appeared on one screen only.
  This is the big one, and it causes most of what follows.
- **A player can be left frozen**, controls locked as if a scripted scene were
  still playing. Reported at the start of Chapter 7's chase. **Press F8** (with
  the game window in front) to unlock your controls; see below. If the HUD
  stays hidden or the scene still doesn't move on, the other player has to
  finish the part, or quit and restart it.
- **Chapter 4 cannot be finished online.** The parts load together, but the
  chapter never completes. Move on by picking the next chapter from Chapter
  Select.
- **Only the party leader can interact** with quest NPCs: Rackam, "Speak
  with…", Depart. This is the game's own rule, so let the lobby host do all
  the talking. Objectives that need the other player to interact can
  soft-lock.
- **Two ship Departs lead into a town section: Chapter 6 and Chapter 8.**
  Online, only the host would go there. Since 0.24 the mod skips those town
  sections while you're in a session: in Chapter 8 the Depart goes straight to
  the next mission, in Chapter 6 it takes you back to town (pick the next part
  from Chapter Select). Every other Depart already takes both players. New and
  not yet tested in play; turn it off with `SkipTownPartsOnline` (below).
- **"Return to town" from the menu during a story part leaves the online
  session.** The game does this on purpose, with only the warning "Any
  unsaved progress will be lost". Finish the part instead when you can. If you
  do leave, the other player's session keeps going and you can rejoin it.
- **Cutscenes do not lock both players.** During scripted scenes the other
  player can usually still move around.
- **Chapter staging is slow** — expect a couple of minutes on the loading screen
  before a chapter starts, versus ~36 seconds solo.
- Missions that drop straight into gameplay work far better than ones that stage
  through the Grandcypher.

If you soft-lock, quit to town and re-enter; nothing is corrupted.

---

## Where co-op actually breaks

Quests where the crew is **already with you** play fine online — a full 4/4
party spawns and behaves. Confirmed on Chapter 1 parts 1-2 and Chapter 3.

Quests where the crew is **scripted to join you partway through** do not work.
The Prologue and the Chapter 1 opening put you alone with Lyria and bring the
others in mid-fight. Online, that arrival scene plays and the dialogue fires,
but nobody is there — invisible during the scene, absent after it. The Quakadile
fight in Chapter 1 then cannot be finished, because it wants a Link Attack and
you have no one to link with.

There is no workaround. Quit the chapter and play something else; nothing is
corrupted.

### What this is not

An earlier version of this README blamed `isPartyChange` — a flag on quest
sections that reassign the party — and listed seven quests as "hanging". Both
claims were wrong and have been withdrawn:

- Those quests do **not** hang. Chapter 4 was played normally online for over
  three minutes while the log looked identical to a "hang".
- A companion mod that cleared every one of those flags was tested with all
  seven applied. **The crew still did not appear.** The flags mark where the
  game changes the party; they are not why the change fails.

The failure is in **spawning the companion entities in an online session**, a
layer no data edit reaches.

---

## F8: unstuck key

If your character is frozen in place during a story part, press **F8** while
the game window is in front. The mod asks the game to run its own "unlock
controls" routine for your characters, the same one the story scripts use.

- It only affects **your** PC. Nothing is sent to the other player.
- It does **not** move the story on. If the script is still waiting for
  something that only happened on the other PC, you can move again but the
  other player has to finish that part.
- It does nothing while an area is still loading (the log says so), and it
  won't fire from F8 pressed in another program.
- Pressing it during a normal cutscene lets you move during that scene. That
  is harmless; the next scene locks you again as usual.

---

## Configuration

`probe_config.json`, re-read every 3 seconds while the game runs:

```json
{ "UnlockStoryWhileOnline": true, "SkipTownPartsOnline": true }
```

- `UnlockStoryWhileOnline`: set to `false` to run the game completely stock
  without uninstalling. Safest to set it **before** launching.
- `SkipTownPartsOnline`: set to `false` to let the Chapter 6 and Chapter 8
  Departs go into their town sections as normal. It is on if the line is
  missing, so older config files get it too.

---

## Two lines worth knowing about

**Where your save resumes**, logged once at startup from the first thing the
game loads:

```
>>> THIS SAVE RESUMES IN: Chapter 4 (102000) — compare with the other player's line; the two saves should be near the same story point <<<
```

Compare this line in both players' logs. If the two are far apart, expect
hangs — this is the matched-progress rule made checkable in one glance. If your
save resumes in town, the line says so and there is nothing to compare.

It is the quest the save *resumes in*, not how far the story has got. On a
save that has finished the story it is just the last chapter played and means
nothing; two finished saves always count as a match. (Before 0.23 this line
was called STORY POSITION and could wrongly report the first chapter you
picked from Chapter Select.)

**Party-change notice.** Entering a quest that reassigns the party mid-way logs
a note, because those are where the crew may fail to arrive:

```
!!! NOTE: Chapter 1 (100001) reassigns the party in 14/14 of its sections.
!!! Party composition does not replicate between clients, so scripted party changes
!!! (companions joining mid-quest) may not happen.
```

It is a heads-up, not a prediction — several of these quests play through fine.

There is no automatic hang detection, deliberately. Two attempts were made and
both were deleted for false alarms. A hang and normal play look **identical** in
this log: same quest id, same phase, same party, near-identical call rates.
Telling them apart needs a signal that only moves while the player can act, and
that means finding the player position or HP globals first.

Quitting to town and re-entering is safe; nothing is corrupted.

## probe.log

The mod logs what the session is doing — this is how the gate was found and is
the fastest way to diagnose a problem.

```
QUEST CHANGE Town (500A00) -> Chapter 1 (101001)  inLobby=1  (held 42s)
PHASE 3072 (0xC00) -> 256 (0x100)   quest=Chapter 1 (101001) inLobby=1
PARTY XX-- (2/4) -> XXXX (4/4)      quest=Chapter 1 (101001) inLobby=1
[hb] quest=Chapter 1 (101001) phase=256 (0x100) party=XX-- (2/4) inLobby=1 unlock=True
```

- **quest** — chapter name, with the raw hex id in parentheses. The chapter for
  each id is read from the game's own `system/table/chapter_select.tbl`, so it
  matches the Chapter Select screen. Ids that are not chapter-select missions
  are shown as `story quest <id>` with no chapter claimed.
- **phase** — despite the name, the map you are on: `0xC00` is the lobby town,
  `0xE20`/`0xE60`/`0xE80` the Grandcypher, low values like `0x500` a mission
  area. The values match the town column of the game's Chapter Select table.
- **party** — occupied party slots; `XX--` means 2 of 4 filled
- **inLobby** — 1 while an online session is active
- **SIGNAL seen / used** — story-script signals this PC received, one line
  each per quest. `obj` is the map object that raised it (0 means the script
  itself) and `hash` says which signal. Compare the SIGNAL lines from two PCs
  in the same chapter and you can see exactly which story triggers happened on
  only one of them. This is how the Chapter 7 hole is being tracked down.
- **INPUT LIMIT lock / unlock** — every time a story script locks or unlocks
  a control. `control` is which control, `who` is `party` (everyone),
  `p1`–`p4` (one party slot) or `x1`–`x8` (extra characters in a scene), and
  `held` lists the locks still on. A lock that stays in `held` on one PC but
  not the other is what a freeze looks like.
- **UNSTUCK** — you pressed F8, and what was locked at that moment.
- **CHAIN / CHAIN START / QUEST END** — a story part ended: which part the
  game looked for next, which one it started, or which map it sent you to
  when there was none. `(a story town, not the lobby town)` marks the case
  that separates players.
- **TOWN SKIP** — the mod kept a Depart out of a town section (see above).
- **UI: SetQuestExitLeaveLobbyFlag / LeaveLobby / EndOnlineAndMulti /
  JumpPhaseTown** — the game's menu steps that leave the session and go to
  town. If `LeaveLobby` appears, that is the moment the session was dropped.

Note the log **travels with the mod folder**. If you copy this folder to another
machine it carries the first machine's history. Tell sessions apart by the
`main module base` line.

---

## Privacy & safety

What this mod does and does not do, so you don't have to take it on trust:

- **It has no network code at all.** The DLL references nothing that can open
  a connection — no sockets, no web requests, no downloads or uploads. It
  cannot send anything anywhere.
- **It never reads your personal information.** No Steam ID, email, IP
  address or account data. It does not touch the registry or start other
  programs.
- **It only uses files in its own folder**: `probe.log` (the session log),
  `probe_config.json` (the on/off switch) and `probe.salt` (random bytes for
  the anonymous PC tag). It also reads `cached_files.txt` in the mod manager's
  folder, only to note which quest files are loaded.
- **The log stays on your PC** unless you send it to someone. It records quest
  numbers, party slot counts and times. Your computer's name appears only as
  an anonymous tag like `pc-3f9a2c`, which cannot be turned back into the name.
- **It adds no network activity of its own.** It changes one menu check, so
  Chapter Select can be opened while you're in a lobby, and (online only) which
  part comes after the Chapter 6 and 8 ship Departs. Co-op then runs through
  the game's normal online system, the same one used for multiplayer quests.
- **It watches exactly one key: F8**, and only acts when the game window is in
  front. It does not record, log or look at any other key.
- **Only download it from the official release.** The real danger with any
  mod is someone re-uploading a tampered copy under the same name. Check that
  the SHA-256 of what you downloaded matches the one published with the release.

---

## What it does to the game

One four-byte patch, applied in memory at runtime, never to files on disk:

```
sete dil   ->   mov dil,1 ; nop      (RVA 0x3D08713)
```

That is a single instruction inside `ui::component::MenuQuestCounter`'s
"is this menu row selectable?" check, which normally reports Chapter Select and
Fate Episode as unavailable whenever an online session exists.

The mod refuses to patch unless it finds exactly the expected instruction, so on
an unrecognised game build it logs a miss and leaves the game untouched. The
patch is reverted on unload.

The F8 key calls the game's own routine that clears every character's locked
controls (RVA 0x32068D0). It writes nothing itself, and it runs only when you
press the key.

The town-section skip changes one answer the game asks itself after a story
part ends: "does this part come next?" (RVA 0x62DB10). Only while that search
is running, only in an online session, only for the Chapter 6 and 8 town
sections and the mission straight after Chapter 8's. Everything else it is
asked is passed through untouched.

Everything else the mod does is read-only logging. Pointers are validated with
`VirtualQuery` before being read, so a stale pointer cannot crash the game.
