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

At the bottom of the Settings page there is a **Self-check** block: it shows how many name-bound
patch targets and reflection members are available, and lists any that failed, one by one.
If a game update breaks part of the features, this is the first place to look.

---

## Features

The panel has sections on the left and pages on the right. Listed by section below.

### Build

- **Ignore Collision** -- blocks can be placed anywhere: overlapping, floating, intersecting. Toggle with `F1`.
- **Free Placement** -- turns off 1-unit grid snapping so placement can be fine-tuned. Toggle with `F2`.
- **Unlock Build Limit** -- the "fullness" limit for saving / publishing in the treehouse (default 500) becomes configurable.

### Level

- **Reload Level** -- truly reloads the current scene with **all placed blocks kept**; scores can be kept or reset.
- **Broadcast Block Snapshot** -- the host re-sends the blocks it sees to everyone, who rebuild them in place (fixes desync without reloading the level).
- **Drop Piece** -- in queue mode, if you are holding a piece you no longer want to place, press a key to drop it. Uses the game's own channel, so it is **visible to everyone**.
- **Level Background** -- give a level a different background color; **works in matches on vanilla levels too**.
- **Party Box Bomb** -- have everyone send a "Bomb!" quick message and a bomb spawns in the party box.
- **Online Room List** -- filter by region, mode, progress, full / not-full and more; sort, refresh and join.

### Destroy Blocks

- Press `Alt` to enter delete mode, scroll to switch the target, `Backspace` to delete.
- When deleting, it shows **who placed** that block, so you can track a specific player.
- There is also a list mode: it lists every block on the field; refresh and click to pick.
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
- **Quick Suicide** -- `Shift+0`.

### Experiments

**Level Bounds** page

- **Unlock Level Bounds** -- walk outside the original level bounds. How far each side extends is adjustable; "hard unlock" lets the cursor and camera go further, with an adjustable radius; out-of-bounds block placement is enabled along with it.
- **Remove UI3 Overlay** -- removes the black screen that covers the view in a level.

**Scene** page

- Skip the loading animation, gentle loading (delay loading to avoid stutter).
- **Start Immediately** -- start a level directly from the treehouse (requires at least one door to be selected).

**Misc** page

- **Cleanup After Load** -- run a GC once after entering a level to reduce stutter.
- **Free Camera** -- scroll to zoom the view; both the view size and the camera move speed are adjustable.
- **Camera Follow** -- adjustable camera move speed.

---

## Credits

BetterFreeplay · BetterNight · BuildingPlus · BuildUnlimiter · UCH Freeplay Spawn Setter ·
UCH Tweaks · UCH-PlayerTracker-Mod · UltimateBuilder

---

## About the Game

This project is a third-party mod for Ultimate Chicken Horse. It only interacts with the game
process through BepInEx and **contains, modifies and distributes none of the game's code or
assets**. The game itself and all related content are copyright Clever Endeavour Games; please
obtain it through official channels.
