# SR_UCH

> Ultimate Chicken Horse 模组整合增强包（免费开源）· A quality-of-life mod suite for **Ultimate Chicken Horse**（BepInEx / Harmony）

**中文 / English**：游戏内设置页可切换语言，即时生效 / switch language in-game from the Settings page, applies immediately.

---

## 安装 Install

1. 已装 **BepInEx 5.4**；把 `SR_UCH.dll` 放进 `Ultimate Chicken Horse\BepInEx\plugins\`
2. Install **BepInEx 5.4**; put `SR_UCH.dll` into `Ultimate Chicken Horse\BepInEx\plugins\`

首次启动生成 `BepInEx\config\com.gamingbeast.SR_UCH.cfg`（可改）。
A config file is created on first launch at `BepInEx\config\com.gamingbeast.SR_UCH.cfg`.

---

## 快速开始 Quick Start

- 默认打开管理器：**`Insert`**（可改）· open manager: **`Insert`** (rebindable)
- 右上角**总开关**默认**关**，打开后各功能才生效 · master switch top-right, default **OFF**
- 打开管理器时默认**冻结游戏输入**（防误操作，可关）· game input frozen while open (toggleable)

---

## 功能 Features

| 栏目 Tab | 功能 Features |
|---|---|
| 首页 Home | 简介 / 使用提示 / 致谢 Intro, tips, credits |
| 移动轨迹 Player Tracker | 每玩家**移动轨迹线**，可调长度/跳帧/宽细 trailing line per player |
| 建造增强 Builder | **无视碰撞**（`F1`）/ **建造上限**（解除树屋保存满度）ignore collision (F1) / build cap |
| 方块破坏 Destroy | **`Alt`** 进入删除、滚轮切换、**`Backspace`** 删除；显示放置者；可允许客户端删 delete blocks (Alt/Backspace), show owner, allow clients |
| 视野 Camera | **自由相机**：滚轮缩放 FOV（**`F3`** 切换）free camera (F3) |
| 快速调整 Quick | **评分折扣** / **快速切换** 行动↔建造（`LeftCtrl`）/ **快速自杀**（`Shift+0`）score discount / quick switch / suicide |
| 地图 Map | **地图总开关** / 俯视图**`M`** / **树屋地图** / **地图网格** map on/off, M map, treehouse map, map grid |
| 重生 Respawn | 重生无敌 / 重生延迟 / 自定义重生点 **`O`/`P`/`K`** spawn invincibility, delay, points |
| 会话内容 Chat | 本会话聊天记录面板（过滤/清空/时间/隐藏窗口）chat log panel |
| 更多联机 More Online | 房间扩展到 **8-100 人**；`M` 码 invites 8-100 players, M codes |
| 模组联机 Mod Lobby | 原生 4 人、只显示装 mod 的房间；`R` 码 vanilla 4p mod-only rooms, R codes |
| 实验 Experiments | 位置同步 / 地图网格 / 树屋问号 / **重载关卡**（保留方块·保留或重置分）/ **广播方块快照** / 快速切换·重试 / 声音静音 / 作弊标识等 position sync, reload level (keep blocks & score), snapshot broadcast, etc. |

---

## 全部按键 Keybinds

| 功能 Function | 键 Default |
|---|---|
| 管理器 Manager | `Insert` |
| 地图 Map | `M` |
| 自由相机 Free camera | `F3` |
| 无视碰撞 Ignore collision | `F1` |
| 方块破坏（切换/删除）Destroy | `Alt` / `Backspace` |
| 快速切换 Quick switch | `LeftCtrl` |
| 快速自杀 Quick suicide | `Shift+0` |
| 重生点 设置/传送/恢复 Spawn | `O` / `P` / `K` |

> 在管理器点击按键框即可改绑：`Esc` 清空，`Shift+Esc` 取消。

---

## 进度解锁 Progression Unlock

部分功能需先达标，锁定期间灰显/禁用：

- **A 组**：游戏时长 > 17h 或奔跑 > 52000m → 解锁 无视碰撞、树屋问号
- **B 组**：游戏时长 > 52h 或奔跑 > 100000m → 解锁 方块破坏总开关
- Group A (>17h / >52000m): ignore collision, question marks · Group B (>52h / >100000m): destroy blocks. Live progress on the Experiments page.

---

## 兼容性 Compatibility

整合包，已内置所列功能。**建议只装 SR_UCH**，勿与同名原版 mod（Even More Players / BetterFreeplay / BuildUnlimiter 等）同装以免补丁冲突。管理器会默认禁用其他外部插件（可在「外部」栏开启，并关闭 SR_UCH 对应功能）。
Install SR_UCH only; the manager disables other external plugins by default (enable them in the External tab and turn off the matching SR_UCH feature).

---

## 从源码构建 Build

无 `.csproj`，源码经 Roslyn `csc` 用 `sr_uch.rsp`（源码清单 + 引用，仓库根相对路径）直接编译。改 `/r:` 指向本机 UCH 后，在仓库根执行 `dotnet <roslyn-csc.dll> @sr_uch.rsp`，输出 `bin\Release\SR_UCH.dll`。新增源文件后把路径追加到 `sr_uch.rsp` 再构建。
No project file — compile with Roslyn `csc` via `sr_uch.rsp` (source list + references, repo-root relative); add new files to the rsp and rebuild.

---

## 致谢 Credits
BetterFreeplay · BetterNight · BuildingPlus · BuildUnlimiter · Even More Players · UCH Freeplay Spawn Setter · UCH Tweaks · UCH-PlayerTracker-Mod · UltimateBuilder

## License
MIT — 自由使用/修改/分发，请保留原作者署名。 Free to use, modify, distribute (keep attribution).
