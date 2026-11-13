# SR_UCH

**English** | [简体中文](README.zh-CN.md)

> A quality-of-life mod suite for **Ultimate Chicken Horse** (BepInEx 5 / Harmony)

An in-game settings panel you can just install and use: levels, building, chat, online play and
experimental features are all gathered into one sidebar with sortable sections. No game files to edit, no
digging through BepInEx config files for options. The UI language can be switched at any time and
takes effect immediately.

**Chinese / English**: switch any time on the in-game Settings page; changes apply instantly.

---

## Install

1. Install **BepInEx 5.4** (on first setup, run the game once so it generates the loader).
2. Put `SR_UCH.dll` into `Ultimate Chicken Horse\BepInEx\plugins\`.

On first launch a config file `BepInEx\config\SR_UCH_data.cfg` is generated.

---

## Quick Start

| | |
|---|---|
| Open panel | **`Insert`** (rebindable on the Settings page) |
| Master switch | **All Enabled** defaults to **off**; turn it on for the features to take effect |
| UI language | English by default; switch to Chinese on the Settings page |
| Rebind keys | Click the key box in the panel: `Esc` clears, `Shift+Esc` cancels |

The **Settings** page has two tabs pinned at the top (**General** / **Self-check**); `Ctrl` + wheel
switches between them, and **Reset defaults** sits at the right end of that bar (it deliberately
leaves the window size/position and the sidebar width alone).

The **Self-check** tab is a card-style health report: one verdict card (with **Rescan**, **Copy
report**, **Write to log**), a *Targets & config* card that turns amber and lists the broken entries
when a name-bound patch target or reflection member disappears, and a *Frame hooks* card for the
per-hook cost. If a game update breaks part of the features, this is the first place to look. Hook
profiling is off by default (its switch lives in that card) so it does not spam the log.

---

## Features

The panel has sections on the left and pages on the right. Listed by section below.

### Build

- **Ignore Collision** -- blocks can be placed anywhere: overlapping, floating, intersecting. Toggle with `F1`.
- **Free Placement** -- turns off 1-unit grid snapping so placement can be fine-tuned. Toggle with `F2`.
- **Unlock Build Limit** -- the "fullness" limit for saving / publishing in the treehouse (default 500) becomes configurable.

### Level (pages: Party Box | Level Tools)

- **Reload Level** -- truly reloads the current scene with **all placed blocks kept**; scores can be kept or reset.
- **Broadcast Block Snapshot** -- the host re-sends the blocks it sees to everyone, who rebuild them in place (fixes desync without reloading the level).
- **Drop Piece** -- in queue mode, if you are holding a piece you no longer want to place, press a key to drop it. Uses the game's own channel, so it is **visible to everyone**.
- **Party Box Bomb** -- have everyone send a "Bomb!" quick message and a bomb spawns in the party box.

### Online

- **Online Room List** -- filter by region, mode, progress, full / not-full and more; sort, refresh and join.

### Time Control

- **Dream** -- a *local* freeze: `Time.timeScale` goes to 0, so the picture, physics and every hazard
  stop. The panel and your hotkeys keep working (the whole mod runs on unscaled time). Offline /
  local party only.
- No online freeze is provided: the game has no native network message that can set
  `Time.timeScale` to 0 on a client without the mod, so the old "Time Stop" hybrid was removed.
- **Freeze Self** -- freezes only your own character (velocity cleared, animation paused, synced to
  the other players); everyone else keeps moving. Do not enable it together with the EX module's
  switch of the same name.
- Both are **forced off at startup** on purpose: a freeze left in the config must never survive
  a restart. Audio (Wwise) is not driven by `timeScale`, so sound keeps playing while time is stopped.

### Chaos Deck

Turn the rules that normally stay fixed for a whole match into one card drawn per round.
Round 3 suddenly gives everyone low gravity, round 5 mirrors the controls -- the match writes
its own script.

**Since 2.0 only the host needs the mod.** Draws are sent as *real* game rule messages
(`MsgGameRuleSet` with a real `TabletRule` / `Value`), and the game's own `TabletRulesScreen`
writes them into `Modifiers` on non-host clients. Guests without the mod therefore change too,
and can see the current rule in the vanilla rules UI. The host is excluded by the game's
`!IsHost` guard, so it applies the same values locally (double-write).

- **Host-authoritative draw** -- only the host (or the local machine offline) rolls. The result is
  broadcast, not a seed, so every client applies exactly the same card; guests never roll locally.
- **10 cards** -- low/high gravity, weak jump, slow sprint, slow platforms, no wall jumps, mirror
  controls, dance invincibility, player collisions and frictionless. Each can be switched off in
  the card pool. Rate of fire, game speed, character size, camera flip and doomsday are excluded
  on purpose: the first two rewrite `Time.timeScale` (conflicting with Time Control), the rest
  touch the camera or require rebuilding renderers.
- **Controls** -- draw interval, max simultaneous cards, draw announcement, reroll hotkey, and a
  live line showing the active cards.
- **Always reversible** -- rule values are snapshotted at match start. Turning the deck off or
  ending the match re-broadcasts each rule's original treehouse value (so guests without the mod
  revert too), restores the host's local copy, and tells modded guests to clear their card display.
- **Room mod roster** -- a periodic handshake on the same rule message shows which players have
  the mod installed. All messages arrive over the server connection on a client, so the handshake
  identifies senders by local player number rather than `conn.connectionId`.
- **Guest proposals (2.1)** -- a guest presses a hotkey to suggest one card. It rides the same
  `None` + magic channel and only the host acts on it: a pending card appears on the Chaos page
  with Accept / Ignore, expiring after 20 seconds (extendable and reopenable from Notification Hub). Accepting still uses the real rule broadcast,
  so guests without the mod change too. Offline you are the host -- just use the reroll key.
- Uses only rules and message types the game already has: **no new Harmony patches, no new msgType,
  no new reflection registrations, no camera changes, no spawned objects, no threads**.

### Death Report

An end-of-match card that tells you how you died. Fully local: no network, no patches,
no reflection registrations, no game-object references.

- Listens to the game's own `PlayerKilledEvent` (sent from `setupDeath` under
  `base.hasAuthority`, so only your own character's deaths are recorded), plus
  `PiecePlacedEvent`, `NewMatchEvent` and `GameEndEvent`.
- Reports total deaths / rounds, top 3 causes (localised name + count), the round of
  your first death, self-inflicted deaths, the most likely culprit, and a one-line verdict.
- **The "suspected culprit" is an inference, not exact attribution.** The game's
  `PlayerKilledEvent` carries only `Cause` and the victim — `causedByPlayerNumber` is used
  internally in `setupDeath` to award the killer a point, and online it goes out as a
  network message rather than landing in the local list. This feature matches the most
  recent placed block with the same kill type (`Placeable.KillType` / `Name` /
  `placedByPlayerNumber` are all public), so with several identical blocks it can be wrong.
  The UI says "suspected" and the whole item can be switched off.
- Online, `PiecePlaced` network messages supplement placement counts (local
  `PiecePlacedEvent` only fires on the placer's machine).
- The card auto-pops in Match Brief (25 s by default, disableable). The settings page keeps
  the last report with a one-click copy button.
- Only values are stored (string / int / float), with caps (256 deaths, 512 placements),
  cleared on a new match — the game sends no event when a block is destroyed, so holding
  references would read recycled objects.

### Match Brief

A small foldable strip in the **game view** that surfaces the three features above without
opening the settings panel:

- Time-control state (Dream / Freeze Self), the active Chaos card, the rarest item
  in the party box, and the room mod roster (optional). **Nothing is drawn when there is nothing
  to show.**
- A **pending guest proposal force-expands the strip** into a highlighted card with a countdown
  and Accept / Ignore buttons. Hidden inside a settings page it would just expire in 20 seconds;
  expired ones can now be reopened from the Notification Hub history.
- Four-corner docking plus drag-to-reposition, opacity / scale / fold hotkey; position persists.
- It is a self-owned persistent `MonoBehaviour.OnGUI`: the core's `RegisterOverlay` is only
  dispatched while the panel is open (`DrawGUI` returns early when closed), so it cannot host an
  in-match HUD.
- All state travels through contract **Slots** (name + BCL delegate). In split builds every
  feature is its own DLL, so referencing another feature's type directly would not compile.
  Removing a feature simply removes its line.
- Button clicks never send network messages from `OnGUI`; they set a frame-stamped intent that
  Tick executes once, avoiding IMGUI's multiple passes per frame.

### Rarity Glow

A quality halo around every item in the party box, driven by its **current probability**: lower
chance = rarer = brighter (reversible on the page).

- Uses the party box's own draw weight (`BlockGroup.GetItemFilterWeight`, which handles
  `FilterOverride`), with `GameSettings.GetBlockFrequency` + `TabletBlockList.GetWeightFromStepValue`
  as fallback. It is **not** the prefab's base rarity.
- Six tiers: Flood / Common / Fine / Rare / Epic / Legendary. Items with weight 0 (never picked
  this match) stay dark. Strength, size, breathing pulse, inverted coloring and the legend are
  all configurable.
- The halo is a **child of each pickable** (one procedurally generated radial-gradient sprite per
  session), sorted behind the item on the same layer. It is found/updated via `Transform.Find`
  and is completely stateless -- when the item goes away, the halo goes with it. No strong-reference
  dictionaries.
- Purely local visuals: probabilities are already synced, so every client computes and draws its
  own. No network traffic, no edits to the original material / SpriteRenderer, no effect on draw
  odds, no new patches / reflection / threads.
- The page shows live per-tier counts from the party box (0.2s throttle, core per-frame object cache).

### Level Recorder

A full timeline of everything that happened in a match, two-way linked with Layout Presets.
Three pages: Timeline / Layouts / Settings.

- Records match start, phases, placements, removals, points, chat, active Chaos cards,
  preset loads, snapshots, disconnects and match end. The server relays placements / points /
  chat / finishes to every client, so **a host-only install records the whole room**; guests
  without the mod simply see no panel.
- Viewing deliberately avoids a long scrolling list: **filter by kind / round / player / text,
  paginate, tap a row for details**. Copy the filtered view or the whole match, or export to
  `BepInEx\config\SR_UCH\records\`. The last match is archived at game end and stays readable.
- **Layout Presets link**: an optional snapshot each round (via `QuickSaver.GetCurrentXmlSnapshot`)
  can be saved as a preset in one click; saved presets can also be listed and loaded here.
  Loading a preset leaves a row on the timeline.
- Death rows cover the local character only (the game emits `PlayerKilledEvent` only with
  authority) — this limit is stated in the page and in exports. Values only, no Unity object
  references; timing uses `unscaledTime`, so it keeps working during a local time freeze.

### Match Profile

Bundles the game's native ruleset **and** this mod's config into a reusable room setup.

- Rules travel through the game's own `MsgApplyRuleset` broadcast (`:46530` / `:111651`), so
  **guests without the mod still get them**; mod config is host-local. Window size, sidebar width
  and internal-state keys are excluded, matching Reset defaults.
- Profiles are line-based text with Base64-encoded XML/config, so they can be copied to a friend
  or imported from the clipboard. Import rejects `DOCTYPE` and enforces a size cap.
- **Diff before applying**: the first tap on Apply lists every rule field that would change,
  diffed against the XML this game version actually generates. Fields that were renamed or removed
  after a game update are flagged with a warning (warning only, so older profiles still work).
  Chat-command / clipboard applies skip the confirmation but log the same diff.
- **Self-check handshake**: after applying, a full rescan runs (patch targets, reflection members,
  attribution) and the verdict appears in the status line; a manual **Rescan self-check** button
  runs the same path. A broadcast error or failed rescan rolls host-local config back to the
  pre-apply snapshot. "No online room" is a known boundary, not a failure.
- Apply from the **treehouse / before a match**: the handler lives on `TabletRulesScreen`, whose
  presence in-match is unverified. Offline with no network connection only mod config is restored.

### Host Console

One read-only page with room state (connections / local player number / ping / average ping /
scene / mode / round) and the status of all five features; one click copies the report to the
clipboard and writes it to the log.

- **Chat commands**: guests type `/sr help | ping | rules | cards | report | copy | profile <name>`
  in the vanilla chat window; the host executes and replies in chat (visible to guests without the
  mod). This is the only guest-to-host channel under a host-only install.
- Only `profile` writes state, host-only; 1.5 s cooldown. If text chat is disabled in game
  settings, the game rejects the reply but the host still gets a local toast.
- No new patches / msgTypes / reflection entries / threads.

### Notification Hub

One place for every screen toast. All notice sources (core, `Notify`, `NotifyT`, `NotifyTag`)
converge on `SR.Notify.cs`'s `NotifyImpl`, so a single optional hook in the core collects them
without touching any call site or changing behaviour when the feature is absent.

- Priority tiers: things you must act on (guest proposals, failures, refusals) > normal feedback >
  plain confirmations. Same-source duplicates within 3 s merge into one `xN` row.
- **Quiet in match** silences low/normal toasts on screen (they still go to history); high priority
  always shows. A **Use Native Toast** switch restores the game's own toasts as a zero-risk rollback.
- History page: filter by level / text / reopenable, paginate, expand a row for full text, copy or
  export to `BepInEx\config\SR_UCH\notices.txt`, clear all.
- Expired Chaos proposals can be **reopened** from history, so a 20-second window is no longer
  "miss it and it is gone". Medium/high notices are also written into the Level Recorder timeline.
- A self-owned `MonoBehaviour.OnGUI` strip shows the newest notices in the game view with a
  priority colour bar, a shape-coded length marker and a shrinking lifetime bar.

### Readability

Removes two barriers the mod suite introduced itself: pure-colour rarity tiers and one-shot
timers that are too short to react to.

- **Six shape patterns** for the rarity glow (dotted ring / single / double / diagonal stripes /
  checker / rays), plus explicit text tier names with `[n/6]` numbering in the legend. Colour is
  no longer the only channel.
- **Font scale** (80-160%) and **high contrast** apply to Match Brief and the Notification Hub
  strip; contrast also outlines legend swatches.
- **Extra seconds** (0-20) extend both the guest-proposal window and toast duration. All four
  settings are soft adapters exposed through contract Slots: removing this feature simply restores
  defaults elsewhere.
- No game font assets are loaded, no patches / msgTypes / reflection entries / threads.

### Match Cast

Sends the status that only the host's screen used to show into the **vanilla chat window**, so
guests with no mod installed can read it.

- `ChatDisplay.NewChatMessage` posts through `NetMsgTypes.ChatSent`, which the server relays with
  `NetworkServer.SendToAll` (`:84077` / `:39125`). No new msgType, patch, reflection entry or thread.
- Opening post: the enabled Chaos card pool (optionally with the rarest piece). Chaos draws
  randomly **each round**, so there is no fixed deck order to preview; each draw is posted afterwards.
  End of match: a one-line summary of the Death Report.
- Throttling reuses the Notification Hub's 3-second same-kind merge and priority tiers, plus a
  configurable minimum interval, per-round cap and character cap. Removing the hub simply falls back
  to a fixed priority.
- If text chat is disabled in the game options (`EmotesOnly` / `ChatAndEmotesOff`) or you are
  offline, posts degrade to a single local toast on the host. No retries, no spam.
- Text only: no rules, scores or entities are touched.

### Player Tracker

- **Movement trails** -- draws the recent movement path of other players on screen.
- Trail length, sampling interval, start/end line width, teleport-clear distance and opacity are all configurable.
- **OFF by default**; display only, it never affects gameplay.

### Destroy Blocks

- Press `Alt` to enter delete mode, scroll to switch the target, `Backspace` to delete.
- When deleting, it shows **who placed** that block, so you can track a specific player.
- There is also a list mode: it lists every block on the field; refresh and click to pick.
- **Break effect** -- the game's own `DestroySelf(..., useSmoke, ...)` parameter is honoured, so
  turning the effect off really makes the piece disappear without smoke.

### Chat

**Chat Window** page

- Hold `Z` to show the chat window, or tap once to make it always-on (rebindable).
- Resize the window with `X` + scroll; change the font size with `C` + scroll.
- Turn off auto-open, turn off fade, enable sharpness, adjust the window position.
- Turn "Enable Enhancements" off and the game's original font size / scale / position are **restored once**, then handed fully back to vanilla.

**Chat Content** page

- A chat log panel for the session that can record and send.
- Filter quick messages, hide the game's own chat window, show a timestamp on each message.

### Quick Adjust

- **Score Discount** -- sets your handicap to 100 minus the discount; it takes effect in that round immediately and is visible on the balance board; restore the original value with one click.
- **Quick Toggle** -- hold `B` to switch back and forth between action and build.
- **Challenge** (page 2) -- auto retry after death / hold-to-retry plus its wait time; retries automatically in challenge mode.

### Character

- **Auto pick** -- picks an animal once when you enter the treehouse or a room: specific animal / random free / smart (the one you use most according to your save); only unlocked animals are considered and nothing happens if you already picked one.
- **Auto outfit** -- applies a random look as soon as the animal is picked (unlocked outfits only); reroll with the button.

### Respawn (under Quick Adjust -> Respawn)

- **Respawn delay** -- seconds to wait after death before auto-respawning (challenge and freeplay; default 0.5s, the game's own hold threshold).
- **Spawn invincibility** -- seconds of invincibility after each spawn (default 1s, matching the game).
- **Cannon-stuck respawn** -- respawns automatically when you stay dead inside a cannon for over 1.5s.
- **No death scoreboard** -- skips the scoreboard when you die in challenge (only while dead and not yet succeeded).

### Layout Presets

- Save the current block layout as a named preset and load it back later (host only).
- Presets live under `BepInEx/config/SR_UCH/presets/`; import from or export to the clipboard for sharing.
- Loading reloads the level natively: whether scores are kept follows your reload-mode setting.

### HUD

- A small on-screen HUD shows the current phase, a phase countdown and round progress.
- The countdown turns yellow in the last 10 seconds and red in the last 5; optional blinking and phase notices.
- Local only: sends no network messages and never affects gameplay. Drag it to move; the position is saved.
- Visibility defaults to **Toggle** (the HUD is on from the start; the key only hides/shows it).
- The section used to be called *Round HUD*; an existing config is migrated to `[HUD]` automatically,
  so every value is kept.

### Experiments

**Level Bounds** page

- **Unlock Level Bounds** -- walk outside the original level bounds. How far each side extends is adjustable; "hard unlock" lets the cursor and camera go further, with an adjustable radius; out-of-bounds block placement is enabled along with it.
- **Remove UI3 Overlay** -- removes the black screen that covers the view in a level. It has no switch of its own; it follows **Unlock Level Bounds**.

**Scene** page

- Skip the loading animation, gentle loading (delay loading to avoid stutter).
- **Start Immediately** -- start a level directly from the treehouse (requires at least one door to be selected).

**Misc** page

- **Free Camera** -- scroll to zoom the view; both the view size and the camera move speed are adjustable.
- **Camera Follow** -- adjustable camera move speed.
- **Cross-play** -- turns the game's own cross-platform matchmaking switch on or off (the value is
  also written into the save file, so it survives a restart).

---

## Reliability

The **2026-10 static audit** (286 findings) has been triaged: all **59 P0** items are handled and
each one is recorded in [`build/audit-p0-ledger.md`](build/audit-p0-ledger.md) with its status
(`ported` / `already-fixed` / `n-a`), the reason and the commit. Highlights:

- Patches/reflection targets that disappear after a game update now report as **broken** instead of
  silently "healthy"; contract-layer logs written before the log sinks are injected are buffered and
  flushed instead of being dropped.
- Config migration never blanks a legacy key it just copied, and every key it deletes is summarised
  once in the startup log.
- Settings "reset defaults" no longer moves/resizes the window or resets the sidebar width.
- Long-running work that used to hang off scene objects (preset reload coroutines, join flow) now
  runs on persistent hosts, and stale `levelPortalXml` / reload locks are cleared by registered
  frame hooks.

The text guards and unit tests below keep new regressions of the same shape out.

---

## Build

```powershell
# Requires: .NET SDK (provides Roslyn csc) + a local game install (provides reference assemblies)
./build/build.ps1                                        # -> bin/Release/SR_UCH.dll
./build/build.ps1 -Deploy                                # build and copy to <game>/BepInEx/plugins
./build/build.ps1 -GameDir "D:/.../Ultimate Chicken Horse"
```

The game folder is auto-detected from common Steam paths; set the UCH_DIR environment variable to override.

### Tests and guards

Everything below runs **without the game installed** (contract layer is compiled against local
`tests/UnityStubs.cs` + `tests/BepInExStubs.cs`), so it works on a fresh clone and in CI:

```powershell
./build/run-tests.ps1 -NoGame    # contract layer must compile + 13 assertions (dotnet, net8.0)
./build/run-tests.ps1            # same tests through csc against the real BepInEx when a game is present
./build/guards-text.ps1          # 13 text guards (API allow-list, layer boundaries, rsp coverage,
                                 # encoding, tooltip shapes, silent-catch and file-size ratchets)
```

CI runs both jobs (`.github/workflows/guards.yml`): `text-guards` and `unit-tests`, so a pull request
that breaks the contract layer or a ratchet goes red before review.

The heavier checks (full build through the rsp chain, split-mode build, csproj build, runtime
self-test comparison) need the game's reference assemblies and stay local:

```powershell
./_tools/build_sr_uch.ps1 -All -Check     # mechanical guards incl. build + self-test baseline
./build/check-selftest.ps1                # compare the last log's [SR.SelfTest] line with the baseline
```

---

## Credits

BetterFreeplay · BuildingPlus · BuildUnlimiter · UCH Freeplay Spawn Setter ·
UCH Tweaks · UCH-PlayerTracker-Mod · UltimateBuilder

---

## About the Game

This project is a third-party mod for Ultimate Chicken Horse. It only interacts with the game
process through BepInEx and **contains, modifies and distributes none of the game's code or
assets**. The game itself and all related content are copyright Clever Endeavour Games; please
obtain it through official channels.
