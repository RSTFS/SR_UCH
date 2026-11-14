> **Current state (2026-11)**: Time Control, Death Report, Match Brief, Level Recorder, Match Profile,
> Host Console, Match Cast, Readability and the notification history page were removed.
> Settings is a single page; Self-check and Notifications moved to the **footer bar**.

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

The **Settings** page is a single page; **Self-check** and **Notifications** moved to the footer bar.
(There is no longer a "Reset defaults" button, and no tabs to switch.)

The **Self-check** footer entry opens a card-style health report: one verdict card (with **Rescan**, **Copy
report**, **Write to log**), a *Targets & config* card that turns amber and lists the broken entries
when a name-bound patch target or reflection member disappears, an *Other plugins* card listing
plugins loaded from `BepInEx\plugins` with known conflict/note verdicts, a *Hotkey center* card (all auto
hotkeys and manual keys, clearable, duplicates flagged), an *Active self-test* card (runs API/patch/slot/
notify/config/key checks and exports a single-file **support bundle** with the diagnostic report and log
tail, path copied to clipboard), a *Recent errors* card (last 16 Guard records), and a *Frame hooks* card
for the per-hook cost. If a game update breaks part of the features, this is the first place to look. Hook
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
- **17 cards** -- low/high gravity, weak jump, slow sprint, slow platforms, no wall jumps, mirror
  controls, dance invincibility, player collisions and frictionless. Each can be switched off in
  the card pool, which also has a search box (name / effect) and splits the list into numeric and
  boolean rule groups. Rate of fire, game speed, character size, camera flip and doomsday are excluded
  on purpose: the first two rewrite `Time.timeScale` (conflicting with Time Control), the rest
  touch the camera or require rebuilding renderers.
- **Controls** -- draw interval, max simultaneous cards, draw announcement, reroll hotkey, and the
  page renders each active card as a card (name + effect + green buff / red debuff / blue neutral border).
- **Always reversible** -- rule values are snapshotted at match start. Turning the deck off or
  ending the match re-broadcasts each rule's original treehouse value (so guests without the mod
  revert too), restores the host's local copy, and tells modded guests to clear their card display.
- **Room mod roster** -- a periodic handshake (30 s) on the same rule message shows which players
  have the mod installed. All messages arrive over the server connection on a client, so the
  handshake identifies senders by local player number rather than `conn.connectionId`.
  Only the host (or the local machine offline) draws; guests receive the result on the `None` +
  magic channel and just apply it. There is no approval/proposal flow: offline you are the host,
  just use the reroll key.
- Uses only rules and message types the game already has: **no new Harmony patches, no new msgType,
  no new reflection registrations, no camera changes, no spawned objects, no threads**.

### Rarity Glow

A quality halo around every item in the party box, driven by its **current probability**: lower
chance = rarer = brighter (reversible on the page).

- Uses the party box's own draw weight (`BlockGroup.GetItemFilterWeight`, which handles
  `FilterOverride`), with `GameSettings.GetBlockFrequency` + `TabletBlockList.GetWeightFromStepValue`
  as fallback. It is **not** the prefab's base rarity.
- Six tiers: Flood / Common / Fine / Rare / Epic / Legendary. Items with weight 0 (never picked
  this match) stay dark. Strength, size and breathing pulse are all configurable, plus a per-tier
  color picker.
- The halo is a **child of each pickable** (one procedurally generated radial-gradient sprite per
  session), sorted behind the item on the same layer. It is found/updated via `Transform.Find`
  and is completely stateless -- when the item goes away, the halo goes with it. No strong-reference
  dictionaries.
- Purely local visuals: probabilities are already synced, so every client computes and draws its
  own. No network traffic, no edits to the original material / SpriteRenderer, no effect on draw
  odds, no new patches / reflection / threads.
- Halo spread grows with the tier (1.6–2.2× the item's bounds), so it reads as light spilling from
  the item itself.

### Visual Aid

Fade / hide **other players** for clearer recording or screenshots. Purely local: no gameplay,
network or sync changes.

- Adjustable opacity (0 = fully hidden, 100 = untouched), optional "fade only when close" with a
  1-30 unit distance, and an in-game toggle key (default F6, rebindable).
- Only other players are touched: your character, unseated characters and menu/treehouse phases are
  left alone. Implemented as two postfixes (`Character.Update` and
  `CharacterOpacityController.Update`) that write alpha / currentOpacity every frame.
- If **CamHog** (an existing mod with the same idea) is loaded, this feature yields and says so on
  the page -- two mods writing opacity every frame would fight each other.
- Uses only public game members (`Character.SpriteRenderer` / `HasJetpack` / `NameTag.maxOpacity` /
  `Character.AllCharacters` / `Util_SpriteRenderer.SetAlpha`); no reflection, no network.

### Notification Hub

One place for every screen toast. All notice sources (core, `Notify`, `NotifyT`, `NotifyTag`)
converge on `SR.Notify.cs`'s `NotifyImpl`, so a single optional hook in the core collects them
without touching any call site or changing behaviour when the feature is absent.

- Priority tiers: things you must act on (failures, refusals, blocks) > normal feedback >
  plain confirmations. Same-source duplicates within 3 s merge into one `xN` row.
- **Nothing gets swallowed**: if either **Use Native Toast** or **Show On Screen** is off, the hub
  hands the screen back to the game's own toasts instead of taking over and showing nothing.
  **Quiet in match** is the only deliberate suppression (low/normal during a match); high priority still pops.
- **No drops under bursts**: Max Visible only decides how many cards are drawn at once; extra
  messages queue (up to 16) and slide in as earlier cards expire. A card's dwell time only starts
  once it is actually on screen, so a long queue never makes later messages expire unseen.
- A self-owned `MonoBehaviour.OnGUI` strip draws the cards: a priority colour bar (with a
  length-coded bar), a shrinking lifetime bar, and a same-colour outline for high priority.
- **Hover pauses / click dismisses**: hovering freezes the countdown (the lifetime bar stops too,
  so long notices cannot be swallowed while reading); left-clicking a card starts its fade-out.
- **Transitions**: cards fade + slide in from the docked side, the whole column eases to its new
  positions when cards are inserted or removed, and cards fade + slide out before being removed.
- Appearance: card width (100-300, default 200), opacity (20-100%), layer scale (80-160%), corner
  (bottom-right/top-right/bottom-left/top-left), max visible (1-6), dwell seconds (1-5), animation
  toggle and duration (80-400 ms).

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
./build/run-tests.ps1 -NoGame    # contract layer must compile + 73 assertions (dotnet, net8.0): ColorHex, table scroll math, Slots bus, feature manifest/version/deps, timeouts and sink accounting
./build/run-tests.ps1            # same tests through csc against the real BepInEx when a game is present
./build/guards-text.ps1          # text guards (API allow-list, layer boundaries, rsp coverage +
                                 # generated-list consistency, encoding, tooltips, ratchets)
```

CI runs both jobs (`.github/workflows/guards.yml`): `text-guards` and `unit-tests`, so a pull request
that breaks the contract layer or a ratchet goes red before review.

Trimmed builds (only the listed features end up in the DLL; the repo manifests are left untouched):

```powershell
./build/build.ps1 -Features "HUD,Chat"            # merged Lite
./build/build.ps1 -Split -Features "HUD,Chat"    # split Lite
```

The heavier checks (full build through the rsp chain, split-mode build, csproj build, runtime
self-test comparison) need the game's reference assemblies and stay local:

```powershell
./build/build.ps1 -All -Check            # mechanical guards incl. build + self-test baseline
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
