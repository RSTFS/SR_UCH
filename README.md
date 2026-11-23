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
- **Rarity Glow** — radial soft glow on party-box items by real draw probability.
- **Visual Aid** — fade other players for screenshots/recording.
- **Notification Hub** — unified toasts with priority, queue, position and size options.
- **Layout Presets** — save/load/import/export building layouts, sortable scrollable list.
- **HUD / Player Tracker / Destroy Blocks / Chat / Quick Adjust / Character / Experiments** —
  optional extras, all toggleable and rebindable in the panel.
- **Self-check** — patch/reflection/slot/notify/config audit, support bundle export, hook timings.

Most features are **client-side only**. Host-authoritative features (chaos deck, party box,
block destruction, level tools) only take effect for the host or in offline play; online behavior
should be verified with a second player.

<!-- generated:facts:begin 由 tools/spec_engine.py 派生，勿手改 -->
- 功能分组 16 / 逻辑功能 28 / 核心自描述 0 / 纯函数文件 10
- CI jobs 3 / 本地化 ID 509 / 本区块由单一引擎派生
- 契约指纹 88e2695cb4ae… / 反射唯一 key 33
<!-- generated:facts:end -->

## Build & test

```powershell
./build/build.ps1                   # build (game installed)
./build/build.ps1 -Check            # unified regression (uch.py verify --strict)
./build/run-tests.ps1               # unit tests (no game required)
```

`build/build.ps1 -Deploy` copies the DLL declared in `build/specs/features.spec.json`
into `BepInEx\plugins`: a single `SR_UCH.dll`.
Close the game first.

## Development

- Features depend only on the contract layer `SR_UCH/Contracts/*.cs`.
- Build scripts live in `build/`, guard rules in `tools/uch_rules.py`, tests in `tests/`.
- `python3 tools/uch.py docs` regenerates the single developer document `docs/INDEX.md`.
- `build/specs/features.spec.json` is the single source for feature groups, features and
  artifacts; `python3 tools/uch.py verify --strict` is the CI check.

## Disclaimer

Unofficial fan project, not affiliated with Clever Endeavour Games. Use at your own risk;
back up saves before experimenting with level/building tools.
