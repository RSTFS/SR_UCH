# SR_UCH

> Ultimate Chicken Horse 模组整合增强包
> A quality-of-life mod suite for **Ultimate Chicken Horse** (BepInEx / Harmony)

[![BepInEx](https://img.shields.io/badge/BepInEx-5.4-blue)](https://github.com/BepInEx/BepInEx)
[![Unity](https://img.shields.io/badge/Unity-2021.3.45f1-lightgrey)]()
[![License](https://img.shields.io/badge/License-MIT-green)]()
[![GitHub](https://img.shields.io/badge/GitHub-RSTFS%2FSR__UCH-orange)](https://github.com/RSTFS/SR_UCH)

**中文** / **English**（游戏内设置页可切换语言，运行时立即生效 / Switch language in-game from the Settings page, applies immediately）

---

## 📦 安装 Installation

**中文**：
1. 确保已安装 **BepInEx 5.4**（`Ultimate Chicken Horse\BepInEx\`）
2. 下载最新 Release
3. 把 **`SR_UCH.dll`** 放进 `Ultimate Chicken Horse\BepInEx\plugins\`

**English**：
1. Make sure **BepInEx 5.4** is installed (`Ultimate Chicken Horse\BepInEx\`)
2. Download the latest Release
3. Put **`SR_UCH.dll`** into `Ultimate Chicken Horse\BepInEx\plugins\`

```
BepInEx/
└── plugins/
    └── SR_UCH.dll        # 主模块 Main module
```

> **中文**：首次启动会生成 `BepInEx\config\com.gamingbeast.SR_UCH.cfg`，所有配置均可改。
> **English**：A config file is generated on first launch at `BepInEx\config\com.gamingbeast.SR_UCH.cfg`.

---

## 🎮 快速开始 Quick Start

**中文**：
- **默认打开管理器的按键：`Insert`**（可改）
- 界面语言：设置页 → `Language` → 中文 / English（即时生效）
- **总开关**：右上角 `总开关：关/开`（默认**关**，需手动打开所有功能才生效；改动自动保存）
- 打开管理器时默认**冻结游戏输入**（防误操作），可关

**English**：
- **Default key to open the manager: `Insert`** (rebindable)
- Language: Settings page → `Language` → 中文 / English (applies immediately)
- **Master switch**: top-right `总开关：关/开` (default **OFF**; turn it on for features to work; auto-saves)
- Game input is **frozen** while the manager is open by default (prevent misclicks), toggleable

---

## ✨ 功能总览 Features

### 🏠 首页 Home
- **中文**：Mod 简介、开源地址、使用提示、参考致谢列表
- **English**：Intro, source link, tips and credits

### 🐾 移动轨迹 Player Tracker
- **中文**：为每个玩家绘制**移动轨迹线**（跟随角色）；可调轨迹长度 / 跳帧数 / 起点宽度 / 终点宽度
- **English**：Draws a trailing line behind each player; adjust length / skip frames / start-end widths

### 🔨 建造增强 Builder Enhancements
- **中文**：
  - **无视碰撞**：方块无视放置规则，可放任意位置（重叠/空中/交叉）（`F1` 切换）
  - **建造上限**：解除树屋保存/发布的关卡满度限制（原版 500 → 自定义，默认 1000000）
  - ⚠ 无视碰撞需**进度解锁**（见下方）
- **English**：
  - **Ignore collision**: pieces ignore placement rules, go anywhere (overlap/air/cross) (`F1`)
  - **Build cap**: lift the treehouse save/publish fullness cap (vanilla 500 → custom, default 1000000)
  - ⚠ Ignore collision needs **progression unlock** (below)

### 💥 方块破坏 Destroy Blocks
- **中文**：
  - 按住 `Alt`（可改）进入删除模式，高亮最近放置的方块；滚轮切换，`Backspace`（可改）删除
  - 显示选中方块是谁放置的（颜色名签）
  - `允许客户端删除`：非房主玩家也能删除（房主同步）
  - ⚠ 总开关需**进度解锁**
- **English**：
  - Hold `Alt` (rebindable) to enter delete mode, highlight the newest block; wheel to switch, `Backspace` to delete
  - Shows who placed the selected block (colored name tag)
  - `Allow Clients`: non-host players can also delete (synced through host)
  - ⚠ Master switch needs **progression unlock**

### 🎥 视野 Camera
- **中文**：**自由相机**：滚轮缩放视野（FOV 1-20，`F3` 切换），任何模式/场景可用；挑战模式对局内自动禁用
- **English**：**Free camera**: wheel zooms FOV (1-20, `F3` toggle), works in any mode/scene; auto-disabled in Challenge matches

### ⚡ 快速调整 Quick Adjust
- **中文**：**分数折扣**（平衡板 handicap）/ **快速切换** 行动↔建造（`LeftCtrl`）/ **快速自杀**（默认 `Shift+0`）
- **English**：**Score discount** (balancer handicap) / **Quick switch** Action↔Build (`LeftCtrl`) / **Quick suicide** (default `Shift+0`)

### 🗺️ 地图与关卡 Map
- **中文**：
  - **地图总开关**：关闭后 M 键无法打开地图，已打开的地图立即关闭（不影响地图网格/同步循环等独立功能）
  - **地图**：`M` 键打开俯视图（暂停游戏，拖拽平移，滚轮缩放；T 传送到鼠标位置）；仅自由模式，挑战模式禁用
  - **同步循环**：强制所有发射器初始延迟统一 0.5 秒（不受 ping 影响，全员节奏一致）
  - **树屋地图**：树屋大厅也能打开地图
  - **地图网格**：行动状态下也显示建造网格（游戏默认只在建造阶段显示）
- **English**：
  - **Map master switch**: OFF disables opening the map (M key does nothing); an open map closes immediately (independent features like map grid / sync cycles are unaffected)
  - **Map**: `M` opens the overhead view (pause, drag to pan, wheel to zoom; T teleports to cursor); freeplay only, disabled in Challenge
  - **Sync cycles**: force all launchers to a fixed 0.5s initial delay (ignores ping, consistent for everyone)
  - **Treehouse map**: open the map in the treehouse lobby too
  - **Map grid**: keep the build grid visible during the play phase (vanilla shows it while building only)

### ♻️ 重生 Respawn
- **中文**：**重生无敌时间** / **重生延迟**（最小 0.1 秒）/ **自定义重生点**（`O` 设置 / `P` 传送 / `K` 恢复，仅自由模式）
- **English**：**Respawn invincibility** / **Respawn delay** (min 0.1s) / **Custom spawn points** (`O` set / `P` go / `K` reset, freeplay only)

### 🔢 尝试计数 Attempt Counter
- **中文**：集成个人尝试计数器；统计挑战 + 自由模式的关卡尝试次数，选关面板显示；`F4` 开关；数据存 `AttemptCounter.json`
- **English**：Integrated personal attempt counter (challenge + freeplay), shown in level-select pane; `F4` toggles; data in `AttemptCounter.json`

### 💬 会话内容 Chat
- **中文**：游戏内聊天记录面板（记录本会话所有聊天消息，可手动清空 / 过滤快捷消息 / 显示时间 / 隐藏游戏内聊天窗口）
- **English**：In-session chat log panel (records all chat messages; clear / filter quick msgs / show time / hide the in-game chat window)

### 👥 更多联机 More Online
- **中文**：主菜单第三个按钮「更多联机」：联机房间扩展到 **8-100 人**（本地游戏/网络对战保持原版 4 人）；邀请码 5 位、第一位 **M**；平衡板置顶等
- **English**：Third main-menu button "More Online": online rooms up to **8-100 players** (Local/Online stay vanilla 4); invite codes are 5 chars starting with **M**; score balancer pin-to-top etc.

### 🔗 模组联机 Mod Lobby
- **中文**：主菜单第四个按钮「模组联机」：原生 4 人、只显示装了本 mod 的房间（版本前缀 `usingMods` 过滤）；邀请码 5 位、第一位 **R**，与普通房间码互不相通
- **English**：Fourth main-menu button "Mod Lobby": vanilla 4 players, only rooms whose host also uses this mod are listed (filtered by the `usingMods` version prefix); invite codes are 5 chars starting with **R**, separate from vanilla codes

### 🧪 实验 Experiments
- **中文**：位置同步（10-50 Hz）/ 地图网格 / 树屋地图·树屋问号 / 重载关卡（保留方块·保留或重置分数）/ 广播方块快照 / 自身增益（无敌·飞天·蹲移，仅自由模式）/ 评分折扣 / 快速切换 / 游戏调试模式 / 读取统计 / 作弊标识 / 角色声音静音 / 功能解锁进度
- **English**：Position sync (10-50 Hz) / Map grid / Treehouse map & question marks / Reload level (keep blocks, keep or reset score) / Broadcast snapshot / Self buffs (invincible·fly·crouch-move, freeplay only) / Score discount / Quick switch / Game debug mode / Stats reader / Cheat flag / Character sound mute / Progression unlock

---

## 🔒 进度解锁 Progression Unlock

**中文**：**游戏时长 > 17 小时 或 奔跑长度 > 52000 米** 时，自动解锁以下两项（达标前灰显禁用、快捷键无效、配置强制复位）：

**English**：When **play time > 17h or distance run > 52000m**, these two unlock automatically (disabled/locked before that):

| 功能 Feature | 解锁前 Before | 解锁后 After |
|---|---|---|
| 建造增强 - 无视碰撞 / Ignore collision | 🔒 禁用 | ✅ 可开启 |
| 方块破坏总开关 / Destroy Blocks | 🔒 禁用 | ✅ 可开启 |

**中文**：实验页「— 功能解锁 —」实时显示当前进度。
**English**：The Experiments page shows live progress.

---

## ⌨️ 全部按键 Keybinds

| 功能 Function | 默认键 Default | 说明 Notes |
|---|---|---|
| 打开/关闭管理器 Manager | `Insert` | 可改 Rebindable |
| 地图 Map | `M` | 可改 Rebindable |
| 自由相机 Free camera | `F3` | 可改 Rebindable |
| 无视碰撞 Collision | `F1` | 可改 Rebindable |
| 方块破坏-切换 Destroy toggle | `Alt`（按住 hold） | 可改 Rebindable |
| 方块破坏-删除 Destroy delete | `Backspace` | 可改 Rebindable |
| 尝试计数显示 Attempts | `F4` | 可改 Rebindable |
| 快速切换 Quick switch | `LeftCtrl` | 可改 Rebindable |
| 快速自杀 Quick suicide | `Shift+0` | 可改 Rebindable |
| 重生点-设置/传送/恢复 Spawn points | `O` / `P` / `K` | 可改 Rebindable |

> **中文**：所有按键在管理器内点击按键框即可重新绑定，`Esc` 清空（未设置），`Shift+Esc` 取消。
> **English**：Rebind any key by clicking its box in the manager; `Esc` clears (unset), `Shift+Esc` cancels.

---

## 🛡️ 兼容性 Compatibility

- **中文**：SR_UCH 是整合增强包，致谢列表中的功能均已内置。**建议只安装 SR_UCH**，不要同时安装同名原版 mod（如 Even More Players / BetterFreeplay / BuildUnlimiter 等），否则相同功能的 Harmony 补丁会互相叠加、行为冲突。
  管理器会**默认禁用 plugins 目录下其他外部 mod**（可在「外部」栏手动启用，启用时请关闭 SR_UCH 中的对应功能）。
- **English**：SR_UCH is an all-in-one pack — every credited feature is built in. **Install SR_UCH only**; do not run the original mods (e.g. Even More Players / BetterFreeplay / BuildUnlimiter) alongside it, or their overlapping Harmony patches will conflict.
  The manager **disables other external plugins by default** (enable them in the "External" tab; when doing so, turn off the matching SR_UCH feature).

---

## 🔧 从源码构建 Build from source

**中文**：本项目不使用 `.csproj`/`.sln`，源码直接由 Roslyn `csc` 编译（`sr_uch.rsp` 响应文件包含全部源码清单与程序集引用，路径为仓库根相对路径）。

**English**：No `.csproj`/`.sln` — sources are compiled directly with Roslyn `csc` (the `sr_uch.rsp` response file lists every source file and assembly reference, relative to the repo root).

```
sr_uch.rsp    # 编译清单：源码清单 + 游戏 Managed DLL / BepInEx 引用 + 输出路径
```

**中文**：
1. 确保本机有 .NET SDK（`dotnet` 可用）
2. 打开 `sr_uch.rsp`，把 `/r:` 引用路径改成你本机 UCH 的 `UltimateChickenHorse_Data\Managed\` 与 `BepInEx\core\` 实际路径
3. 在**仓库根目录**执行（相对路径按仓库根解析）：
   `dotnet <roslyn csc.dll 路径> @sr_uch.rsp`
4. 输出：`bin\Release\SR_UCH.dll`
5. 新增源码文件后，把路径追加到 `sr_uch.rsp` 末尾再构建

**English**：
1. Install the .NET SDK (`dotnet` on PATH)
2. Open `sr_uch.rsp` and point the `/r:` references at your local UCH `UltimateChickenHorse_Data\Managed\` and `BepInEx\core\` folders
3. Run **from the repo root** (relative paths resolve against it):
   `dotnet <path-to-roslyn-csc.dll> @sr_uch.rsp`
4. Output: `bin\Release\SR_UCH.dll`
5. After adding a source file, append its path to `sr_uch.rsp` and rebuild

---

## 📁 项目结构 Structure

```
SR_UCH/
├── SR_UCH/                 # 主模块 Main module
│   └── SR_UCH/
│       ├── MainPlugin.cs              # 入口 Entry (BepInPlugin)；自动发现所有 ITweak 实现
│       ├── Loc.cs                     # 双语本地化统一入口（新功能文本用 Loc.T(zh, en)）
│       ├── ModManager.Core.cs         # 管理器：核心状态 / 初始化 / 插件扫描
│       ├── ModManager.Plugins.cs      # 管理器：外部插件管理
│       ├── ModManager.KeyBinds.cs     # 管理器：自定义键位 / 组合键
│       ├── ModManager.Styles.cs       # 管理器：UI 主题样式
│       ├── ModManager.Localization.cs # 管理器：中英文翻译表（分区/条目/描述/枚举）
│       ├── ModManager.Map.cs          # 管理器：俯视地图视图
│       ├── ModManager.Input.cs        # 管理器：输入冻结 / 角色冻结
│       ├── ModManager.Reload.cs       # 管理器：重载关卡保分补分
│       ├── ModManager.Window.cs       # 管理器：主窗口框架 DrawGUI
│       ├── ModManager.Settings.cs     # 管理器：设置页 / 控件渲染
│       ├── ModManager.Progression.cs  # 管理器：进度解锁
│       ├── ModManager.Pages.cs        # 管理器：各栏目页面渲染
│       ├── ModManager.MonoBehaviour.cs# 管理器：ManagerUI 驱动
│       ├── MorePlayers.Core.cs        # 更多联机：配置 / 门控 / 重打补丁
│       ├── MorePlayers.RoomSize.cs    # 更多联机：房间人数扩展
│       ├── MorePlayers.Handicap.cs    # 更多联机：平衡板置顶
│       ├── MorePlayers.LobbyCode.cs   # 更多联机：M 码邀请码
│       ├── MorePlayers.Menu.cs        # 更多联机：主菜单按钮
│       ├── MorePlayers.CharSelect.cs  # 更多联机：角色多选 / 装扮
│       ├── Experiments.cs             # 实验栏目（单文件，内含分区注释）
│       ├── ModMC.Core.cs              # 模组联机：核心
│       ├── ModMC.Menu.cs              # 模组联机：主菜单按钮
│       ├── ModMC.Lobby.cs             # 模组联机：R 码 / 大厅
│       ├── AttemptCounter.cs          # 尝试计数 Attempt counter
│       ├── BuilderEnhancements.cs     # 建造增强 Builder enhancements
│       ├── BuildUnlimiter.cs          # 建造上限 Build cap
│       ├── CharacterMute.cs           # 角色声音 Character sound
│       ├── ChatLog.cs                 # 聊天记录 Chat log
│       ├── DestroyBlocks.cs           # 方块破坏 Destroy blocks
│       ├── FovAdjust.cs               # 自由相机 Free camera
│       ├── NoSpawnImmunity.cs         # 重生无敌 Spawn immunity
│       ├── PlayerTracker.cs           # 移动轨迹 Player tracker
│       ├── RespawnDelay.cs            # 重生延迟 Respawn delay
│       ├── SpawnPoints.cs             # 自定义重生点 Spawn points
│       ├── Suicide.cs                 # 快速自杀 Quick suicide
│       └── SyncedCycles.cs            # 同步循环 Sync cycles
├── MainPlugin.cs              # 入口（仓库根）
└── sr_uch.rsp                 # 编译清单（相对路径；按本机修改 /r: 引用后构建）
```

### 🧭 长期维护约定 Conventions

- **命名**：大文件按「分区」拆成 `ClassName.Partition.cs` 的 partial 文件；每个分区一个源码，职责单一。
- **新增功能**：新建 `XxxFeature.cs` 实现 `ITweak`（`Initialize(MainPlugin)` 里绑定配置），
  把文件路径追加到 `sr_uch.rsp`，按「从源码构建」章节编译即可；
  配置项会自动出现在管理器对应栏目。
- **中英文**：新功能所有用户可见文本用 `Loc.T(中文, English)`；翻译表集中在
  `ModManager.Localization.cs`（新增条目按「分区\t键名」添加 中/英 两行即可）。
- **实验栏目**：功能保持单一 `Experiments.cs` 文件（文件内用分区注释划分，不拆文件）。

---

## 🙏 致谢 Credits

**中文**：本 mod 参考了以下 mod/库，向制作者表示感谢：
**English**：This mod references the following mods/libraries; thanks to their authors:

- BetterFreeplay
- BetterNight
- BuildingPlus
- BuildUnlimiter
- Even More Players
- UCH Freeplay Spawn Setter
- UCH Tweaks
- UCH-PlayerTracker-Mod
- UltimateBuilder
- Bojack（尝试计数 mod / attempt counter mod）

---

## 📄 License

MIT License — 自由使用、修改、分发（请保留原作者署名）。
MIT License — free to use, modify and distribute (keep the original attribution).
