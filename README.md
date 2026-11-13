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

### Updating

From 0.32 the mod updates through **Reloaded-II itself**; the mod has no
network code of its own. Its `ModConfig.json` tells the launcher where to look
(this GitHub repository). Every release is a beta, so one thing has to be
switched on once per PC: in Reloaded-II open the mod's settings and tick
**Allow Beta Versions** (Edit User Config). After that the launcher checks for a
newer release when you open it and offers to download and install it.

- The first time (or from a version older than 0.32) install by hand: download
  `gbfr.coop.storyprobe.zip` from the newest release on the
  [Releases page](https://github.com/TrickerNoxics14/gbfr.coop.storyprobe/releases)
  and drag it into Reloaded-II.
- Both players need the **same version**. Each mod tells the other its version,
  and if they differ both of you see a note on screen ("Mod versions differ:
  player slot 2 has v0.31.0, you have v0.32.0. Update so both match.") and a
  `VERSION` line in the log. Players on a build older than 0.32 are reported
  as "an older version".
- Nothing is updated without you: the launcher asks first, and the mod never
  downloads anything.

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

**Since 0.31 the mod warns you.** With the mod on both PCs, each one reads how
many story quests its own save has cleared and shares that number (and a short
fingerprint of which ones) with the other players. When they differ, the host
sees a note ("Heads up: your story save differs from player slot 2's") and the
Chapter Select start is held once, with the reason on screen. Pick the same
part again to start anyway. Turn it off with `WarnStoryProgressMismatch`.

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
- **Chapter 4's last part did not finish online** (the one two-PC test, in
  0.20, long before most of today's fixes). Its first two parts (the church
  and the ship) finished and carried both players; the mission after them
  ended early with the host and guest 34 seconds apart, and the log could not
  say why. It has not been tried since. Since 0.30 the log records what a
  stalled step was waiting for (`WAIT`, `STALL`, `STORY SCRIPT`), so one more
  try tells the cause. If it stalls, move on by picking the next chapter from
  Chapter Select.
- **Guests talking to story NPCs (new in 0.28, not yet seen in play).**
  Before 0.28 only the lobby host could use Rackam, "Speak with…" or Depart.
  The game already lets a guest's press ask the host to run it, but in story
  areas the guest never got the prompt. With the mod on **both** PCs, the
  guest now gets the prompt, and the press runs on the host's game, so
  everyone moves on together. The host still decides: a press only runs if
  the object is ready on the host's game too. If it does nothing, let the host
  do it. Turn it off with `GuestInteractOnline`. The log line is `INTERACT`.
  **New in 0.29 (not yet seen in play):** a guest WITHOUT the mod (for
  example a console player) can now use story objects too, as long as the
  **host** has the mod. The game tells guests "this object is ready" only once,
  and a guest that had not set the object up yet never hears it. The host's mod
  now says it again (the game's own message, every few seconds for the first
  minute and a half, then rarely) for every story object that is ready on the
  host. The guest's own game then shows the prompt, and its press reaches the
  host as the game's normal request, which the same host check handles.
- **Two ship Departs lead into a town section: Chapter 6 and Chapter 8.**
  Online, only the host would go there. Since 0.24 the mod skips those town
  sections while you're in a session: in Chapter 8 the Depart goes straight to
  the next mission, in Chapter 6 it takes you back to town (pick the next part
  from Chapter Select). Every other Depart already takes both players. New and
  not yet tested in play; turn it off with `SkipTownPartsOnline` (below).
- **"Return to town" from the menu during a story part.** The game normally
  leaves the online session here. Since 0.25 the mod keeps the session up and
  sends you to the lobby town, where the other players end up. New and not yet
  tested in play: what the other player's game does when the host goes to town
  this way is not known yet. Turn it off with `KeepSessionOnReturnToTown`
  (below). If the session does drop, the other player's session keeps going
  and you can rejoin it.
- **Cutscenes do not lock both players.** During scripted scenes the other
  player can usually still move around.
- **The host's cutscene skip skips it for everyone (since 0.26).** Story
  cutscenes are never synced by the game itself. When the host skips one, the
  mod tells the others' games, and each skips the same scene if it is playing
  there. Everyone needs 0.26. Turn it off with `LeaderSkipsForEveryone`.
- **Chapter Select "Ready" check (since 0.26).** When the host picks a
  Chapter Select part online, the start waits until every other player who has
  the mod has picked **the same part** and confirmed ("Start chapter?" → Yes).
  That is each player's Ready. The screen stays dark while it waits. After 60
  seconds without everyone, it is cancelled and the screen fades back in; pick
  the part again to retry. The host picking the same part a second time while
  waiting starts at once. Players without the mod are not waited for. Turn it
  off with `ReadyCheckOnline`.
- **Story signals the host gets, the others get too (since 0.26).** Some story
  steps (the Chapter 7 volcano chase) wait for a signal that only the host's
  game raises. The mod passes those on so the other players' story steps move
  on too. New and not yet seen in play. Turn it off with `RelayStorySignals`.
- **Main Menu with no list online (fixed in 0.27).** Opening the Main Menu in
  a story area online sometimes showed only the background. The game has two
  ways to open the menu, and one of them waits on timers that can stall
  online; the mod makes it take the other way, which builds the whole menu at
  once (with a short fade). Turn it off with `FullMainMenuOnline`.
- **Tutorial popups online (fixed in 0.27).** Every story tutorial is marked
  "offline only" by the game, so none appeared in a lobby. The mod lets them
  show in story areas online. A tutorial window pauses only your own game
  (your input and menus), not the other players'. Turn it off with
  `TutorialsOnline`.
- **Chapter staging is slow** — expect a couple of minutes on the loading screen
  before a chapter starts, versus ~36 seconds solo.
- Missions that drop straight into gameplay work far better than ones that stage
  through the Grandcypher.

If you soft-lock, quit to town and re-enter; nothing is corrupted.

---

## Where co-op actually breaks

Quests where the crew is **already with you** play fine online — a full 4/4
party spawns and behaves. Confirmed on Chapter 1 parts 1-2 and Chapter 3.

Quests where the crew is **scripted to join you partway through** used to
fail. The Prologue and the Chapter 1 opening put you alone with Lyria and bring
the others in mid-fight. Online, that arrival scene played and the dialogue
fired, but nobody was there, so the Quakadile fight in Chapter 1 could not be
finished (it wants a Link Attack).

**Since 0.27 the mod fixes this.** The cause is one check in the game: online,
the routine that spawns AI party members skips slots 2-4 entirely. During a
story section's party step the mod lifts that skip, and fills **only empty
slots**: a slot that already holds a character (the other players) is never
touched, the same way CPU allies fill empty slots in online quests. Each PC
spawns the crew on its own screen, which is also how the game itself handles
CPU allies in online quests. New and not yet seen in play; turn it off with
`SpawnCrewOnline`. The log line is `CREW`.

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
{
  "UnlockStoryWhileOnline": true,
  "SkipTownPartsOnline": true,
  "KeepSessionOnReturnToTown": true,
  "LeaderSkipsForEveryone": true,
  "ReadyCheckOnline": true,
  "RelayStorySignals": true,
  "SpawnCrewOnline": true,
  "FullMainMenuOnline": true,
  "TutorialsOnline": true,
  "GuestInteractOnline": true,
  "WarnStoryProgressMismatch": true,
  "StatusTextOnScreen": true,
  "SkipVoteOnline": false
}
```

- `UnlockStoryWhileOnline`: set to `false` to run the game completely stock
  without uninstalling. Safest to set it **before** launching.
- `SkipTownPartsOnline`: set to `false` to let the Chapter 6 and Chapter 8
  Departs go into their town sections as normal.
- `KeepSessionOnReturnToTown`: set to `false` to let "Return to town" in a
  story part leave the online session as the game normally does.
- `LeaderSkipsForEveryone`, `ReadyCheckOnline`, `RelayStorySignals`: set to
  `false` to turn off the three features that send messages between players
  (see Privacy below). With all three off the mod sends nothing.
- `SpawnCrewOnline`: set to `false` to stop the mod filling empty party
  slots with the crew online (Chapter 1's Quakadile fight then can't be won).
- `FullMainMenuOnline`: set to `false` to let the Main Menu open the game's
  own way online (which sometimes shows no menu list).
- `TutorialsOnline`: set to `false` to hide story tutorials online again, as
  the game does.
- `GuestInteractOnline`: set to `false` to go back to "only the host can talk
  to story NPCs". It also stops the guest's press being sent to the host.
- `WarnStoryProgressMismatch`: set to `false` to stop reading and sharing your
  story progress and to never hold a start because the saves differ.
- `SkipVoteOnline` (**off by default**): set to `true` on every PC to let guests
  vote to skip movie and event cutscenes. A guest's confirmed skip is then sent
  to the host and the guest keeps watching ("You voted to skip. Waiting for
  the others..."). When **more than half** of the players with the mod have
  voted, the host skips for everyone. In a two-player lobby a lone guest is
  not a majority, so the host still decides (it sees "1 of 1 other player(s)
  want to skip"). The host's own confirmed skip still skips for everyone at
  once. A guest who confirms skip a second time within 10 seconds skips only
  on their own PC. In-engine scenes are not voted on: only the host's skip is
  passed on, as before. Needs `LeaderSkipsForEveryone` on.
- `StatusTextOnScreen`: set to `false` to hide the small text box the mod
  shows at the top of the game window ("Waiting for 1 of 1 player to pick the
  same part", "Story saves differ ..."). The box is a separate window: it never
  takes focus, never catches a click or key, and only shows while the game is
  the active window (it does not show in exclusive full screen; use
  borderless or windowed).

All but the first are on if their line is missing, so older config files get
them too.

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
- **KEEP SESSION** — "Return to town" was chosen online in a story part: the
  mod skipped the leave-session step and, if needed, sent you to the lobby
  town instead of the chapter's story town. A `KEEP SESSION diag` line means
  the leave step was not recognised and the session was left as before.
- **CUTSCENE** — a cutscene skip on this PC, and which kind it was.
- **RELAY** — the message channel: on/off, whether this PC is the host or a
  guest, and which other players have the mod. `dropped a damaged message`
  should never appear; if it does, send the log.
- **LEADER SKIP** — the host's skip was passed on, or arrived and skipped
  the same scene here.
- **READY** — the Chapter Select ready check: who is ready, waiting, started
  or cancelled.
- **STORY RELAY** — a story signal from the host reached this PC.
- **CREW** — a story party step online: which empty slots got the crew, and
  which filled slots (the other players) were left alone.
- **MENU** — the Main Menu was opened online in a story area and built the
  full way.
- **TUTORIAL** — story tutorials are being shown online in this part.
- **WAIT** — a story step has waited 45 seconds or more for the same signal
  (the signal's number, once per step, and whether this PC is host or guest).
- **STALL** — when a part ends, the signal steps that were still waiting.
- **STORY SCRIPT** — the part's own clear-quest or game-over action ran on
  this PC, so a defeat can be told apart from a stall.
- **SKIP VOTE** — a guest voted to skip a cutscene, confirmed twice and skipped
  on this PC only, or the host skipped for everyone because enough players
  voted.
- **VERSION** — another player's mod version differs from yours (or is
  older than 0.32); update the older one.
- **PROGRESS** — your story save differs from another player's (both
  numbers), or the host's start was held because of it and what to do.
  The heartbeat also shows `saves=<cleared>/diff=<players that differ>`.
- **INTERACT** — online only, at most 25 a minute. Guest: a story object
  opened to this guest, a press sent to the host, or a press that could not
  be sent (the object has no network id). Host: a guest's press ran here, or
  was ignored (the object was not ready here yet, or the player is not known
  here yet).

Note the log **travels with the mod folder**. If you copy this folder to another
machine it carries the first machine's history. Tell sessions apart by the
`main module base` line.

---

## Privacy & safety

What this mod does and does not do, so you don't have to take it on trust:

- **It opens no connections of its own.** The DLL references nothing that can
  open a connection: no sockets, no web requests, no downloads or uploads.
- **What it sends (since 0.26), and to whom.** Only while you are in an online
  session, and only to the players in that session. The messages travel
  through the game's own "sync flags", the on/off markers the game already
  sends to the other players (for example "this player is ready"), using nine
  of them that the game never uses. Nothing else is sent:
  - "I have the mod" (every 20 seconds), and whether this PC hosts the lobby,
  - "the host skipped this cutscene" (with the quest number),
  - "I'm ready for this Chapter Select part" (with the part's number),
  - a story signal number from the host's game (the game's own internal number
    for a story step),
  - "this object is ready" from the host (since 0.29): the game's own
    message with the object's in-session number, repeated for a while. It goes
    through the game's own sender; the mod opens no connection itself.
  - "I pressed this object" from a guest (since 0.28): the game's own
    in-session numbers for the object and for the guest's character, the same
    two numbers the game itself sends for that press,
  - "I voted to skip this cutscene" (since 0.31, only with `SkipVoteOnline`):
    the quest number, re-sent every second while the vote stands,
  - which version of the mod this is (since 0.32): three small numbers, so
    the other player's mod can warn you when the versions differ,
  - how far your save is (since 0.31): the number of story quests it has
    cleared and a short fingerprint of which ones. Nothing else from the save.

  No names, Steam IDs, email, IP address, account data or file contents are
  ever sent. Turn all of it off with the three options in Configuration, plus
  `GuestInteractOnline`, `WarnStoryProgressMismatch` and `SkipVoteOnline`.
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
- **Co-op itself runs through the game's normal online system,** the same one
  used for multiplayer quests. The mod changes one menu check (so Chapter
  Select opens in a lobby) and, online only, which part follows the Chapter 6
  and 8 ship Departs and what the story menu's "Return to town" does.
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

The kept session on "Return to town" changes two answers, both only online,
in a story part, within 30 seconds of choosing that menu entry: the menu's
leave-session step asks "are we online?" twice (2.0.6 RVA 0x1D2B7D0) and is
told "no" for those two checks only, so it ends without leaving; then a jump
to a chapter's story town is sent to the lobby town instead (RVA 0x1A3430).
Every other "are we online?" in the game is passed through untouched.

The message channel (0.26) uses the game's own sync-flag setter (2.0.6 RVA
0x74A4D0): the mod calls it for its own player's flags only, with flag ids
0x01, 0x02, 0x06-0x0A, 0x12 and 0x1B, and watches it to read the other
players' changes. Every other flag passes through untouched. On top of it:
- the host's cutscene skip is passed on; another player's game then sets its
  own cutscene skip flag (only while a cutscene plays) or, for an in-engine
  scene, sees one skip-button press inside its own skip routine, so the
  game's own skip rules still apply;
- Chapter Select's start (RVA 0x1BE8970) can be held on the host until the
  others are ready, then run; on cancel the game's own fade-in (0x1C08790) is
  run; a guest's own pick becomes its Ready answer;
- a story step waiting for a signal (RVA 0x321A4F0) is counted as reached
  when the host's game reported that same signal, once per step.

Since 0.27, online in story areas only: during a story section's party
step (RVA 0x31F5550) the jump that skips spawning party slots 2-4 online
(0x31F76F2, 6 bytes) is lifted and put back when the step ends, and filled
slots are protected from removal and respawn; the Main Menu's open check
(0x1D5FD60) answers "full way"; a tutorial step (0x320E6E0) is treated as
shown online for the length of its call.

Since 0.28, online in story areas only, when the host runs the mod: on a
guest, an Interact object that waits for the host's announcement (start
RVA 0x96E100, byte +0x235) stops waiting, so its prompt shows; a guest's
press (C::Interact, 0x219D130) still runs the game's own code and is also
sent to the host. On the host, the game's receiver for a guest's press
(0xB9FBC0) is gated: a press from a player the host does not know is dropped
in any area (the game would otherwise run it with no player and crash); in
story areas a press on an object that is not enabled on the host is dropped,
and the same press arriving twice (the game's copy and the mod's) runs once.
A relayed press is handed to that same receiver as the game's own request.

Since 0.29, on the lobby owner, online in story areas only: for every Interact
object whose start has finished on the host (callback field +0x310 empty, state
not 5 or 6) and whose interactable is enabled (found by the game's own handle
check, never through the stored +0x3E8), the mod builds the game's own
announcement byte for byte (network type 10, subtype 1, size 0x68, reliable,
count at +0x10, ids from +0x18, at most ten) and sends it through the game's
network interface exactly as C::Interact does (online state 3, local index not
-1, interface object +0x38, slot 0x80). It first goes out 1 s after the object
is ready, repeats every 3 s for 90 s and then every 30 s. A guest's game
handles it with its own code (receiver 0xAE64F0 sets +0x235 for ids it
knows), so no mod is needed on the guest. The host writes nothing else.

Since 0.30, log only: the game's signal-wait check (RVA 0x321A4F0, already
watched by the signal relay) notes when each step started waiting; the quest
end hook lists steps still waiting; the story script's clear-quest (RVA
0x3200D70) and game-over (0x3204200) actions are logged and run unchanged.

Since 0.31: the quest system's story records (global at RVA 0x7C53D30, record
list +0x108..+0x110, 0xD48 bytes each, cleared count at +8) are read, never
written. The mod's Chapter Select start hold (RVA 0x1BE8970) now also holds
once when the saves differ. The status text box is an ordinary operating
system window created by the mod (not part of the game's drawing); the game
itself is not changed.

Since 0.31, only with `SkipVoteOnline`: on a guest, a confirmed skip of a movie or
event cutscene is taken as a vote and the game's own skip (the SkipCut action,
RVA 0x1BECE50, which only copies a byte to the cutscene HUD) does not run; on
the host, when enough votes are in, the same HUD byte (+0x46) is written, which
is what a guest's relayed skip already did. The cutscene-playing test used by
the relayed skip now matches the game's own (RVA 0x6461D0: mode 1 or 3; it was
1 or 5, which could miss a movie played in mode 3).

Since 0.32: the HELLO message carries the mod's version (three bytes after the
flags byte; older builds read only the first byte and ignore the rest).
ModConfig.json gained the update settings Reloaded-II reads (the GitHub
repository, `UseReleaseTag`, and the release file name
`gbfr.coop.storyprobe.zip`); the mod itself does not use them.

Everything else the mod does is read-only logging. Pointers are validated with
`VirtualQuery` before being read, so a stale pointer cannot crash the game.
