# SR_UCH

**English** | [简体中文](README.zh-CN.md)

> A quality-of-life mod suite for **Ultimate Chicken Horse** (BepInEx 5 / Harmony)

An in-game settings panel you can just install and use: levels, building, chat, online play and
experimental features are all gathered into one searchable sidebar. No game files to edit, no
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
- You can optionally allow clients to delete too (requires the matching extension module).

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
- **Auto Retry After Death** / **Quick Retry** -- automatically retry when you die in challenge mode.

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
