**English** | [简体中文](README.zh-CN.md)

# SR_UCH

A quality-of-life mod suite for **Ultimate Chicken Horse** (BepInEx 5 / Harmony).
One in-game settings panel for levels, building, chat, online play and experiments —
no game files to edit, no config file digging.

## Install

1. Install **BepInEx 5.4 x64** and run the game once.
2. Drop `SR_UCH.dll` into `Ultimate Chicken Horse\BepInEx\plugins\`.
3. Start the game, press **Insert** to open the panel.

The config file is created at `BepInEx\config\SR_UCH_data.cfg`.

## Quick start

| | |
|---|---|
| Open panel | **Insert** (rebindable) |
| Master switch | **All Enabled** (off by default) |
| Language | English by default; Chinese switchable in settings |

## Features

- **Building** — collision override, free placement, grid overlay.
- **Levels** — party box refresh/close, level tools, clear placed blocks.
- **Online** — sortable lobby list with filters, room mod / version check.
- **Chaos Deck** — one random rule card per round (host rolls; vanilla clients still change).
- **Rarity Glow** — quality halo on party-box items by real draw probability.
- **Visual Aid** — fade other players for screenshots/recording.
- **Notification Hub** — unified toasts with priority, queue, position and size options.
- **Layout Presets** — save/load/import/export building layouts, searchable list.
- **HUD / Player Tracker / Destroy Blocks / Chat / Quick Adjust / Character / Experiments** —
  optional extras, all toggleable and rebindable in the panel.
- **Self-check** — patch/reflection/slot/notify/config audit, support bundle export, hook timings.

Most features are **client-side only**. Host-authoritative features (chaos deck, party box,
block destruction, level tools) only take effect for the host or in offline play; online behavior
should be verified with a second player.

## Build & test

```powershell
./build/build.ps1 -All              # build (game installed)
./build/build.ps1 -Check            # guards: API surface, layers, schema, self-test
./build/run-tests.ps1               # unit tests (no game required)
```

`build/build.ps1 -All -Deploy` copies the DLL into `BepInEx\plugins` (close the game first).

## Development

- Features depend only on the contract layer `SR_UCH/Contracts/*.cs`.
- Build scripts, guards and tests live in `build/` and `tests/`.
- `build/gen-release-notes.ps1` / `build/gen-contract-doc.ps1` regenerate developer docs.

## Disclaimer

Unofficial fan project, not affiliated with Clever Endeavour Games. Use at your own risk;
back up saves before experimenting with level/building tools.
