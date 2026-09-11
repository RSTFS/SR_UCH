# SR_UCH

> Ultimate Chicken Horse 模组整合增强包（免费开源）· A quality-of-life mod suite for **Ultimate Chicken Horse**（BepInEx / Harmony）

**中文 / English**：游戏内可切换界面语言，即时生效 / switch in-game language anytime.

---

## 安装 Install
装好 **BepInEx 5.4** 后，把 `SR_UCH.dll` 放进 `Ultimate Chicken Horse\BepInEx\plugins\`。
Put `SR_UCH.dll` into `Ultimate Chicken Horse\BepInEx\plugins\`. 首次启动生成 `BepInEx\config\com.gamingbeast.SR_UCH.cfg`（可改）。

---

## 快速开始 Quick Start
- 打开管理器：**`Insert`**（可改）· open the manager: `Insert`
- 总开关 **All Enabled** 默认**关**，打开后各功能才生效 · master switch default **OFF**
- 默认英文界面，可在设置页切中文 · English by default, switch to 中文 in Settings

---

## 功能 Features

**移动轨迹 Player Tracker**
- 给每位玩家画**移动轨迹线**，可调长度/跳帧/粗细 · trailing line per player (length / skip frames / width)

**快速调整 Quick Adjust**
- **评分折扣**（平衡板 handicap）/ **快速切换** 行动↔建造（长按 B）/ **快速重试**（挑战模式）/ **快速自杀** · score discount / quick switch / quick retry (challenge) / quick suicide

**建造 Builder**
- **无视碰撞**：方块可放任意位置（重叠/空中/交叉）(`F1`) · ignore collision rules (F1)
- **自由放置**：方块不再吸附 1 单位网格，可微调摆放 (`F2`) · free placement / fine snap (F2)
- **解除建造上限**：树屋保存/发布的满度上限 500 → 自定义（默认 1000000）· lift build-fullness cap

**方块破坏 Destroy Blocks**
- `Alt` 进入删除模式、滚轮切换、`Backspace` 删除；显示放置者；可允许客户端删 · delete blocks, show owner, allow clients

**关卡 Level**（仅派对/创意局内、房主）
- **重载关卡**：真重载当前场景，保留已放方块；按模式保留或重置分数 · reload level, keep blocks (keep/reset score)
- **广播方块快照**：把房主视角方块重发，全员重建（修不同步，不重载）· broadcast snapshot to resync blocks
- **派对盒炸弹**：全员发「炸弹！」快捷消息即在派对盒生成炸弹（不需要 EX）· party-box bomb when all send "Bomb!" (no EX needed)

**自由模式 / 地图 Freeplay & Map**
- **地图** `M`（俯视，T 传送）；**树屋地图**；**地图总开关**；**地图网格**（行动阶段也显示网格）· map (M), treehouse map, map grid
- **视野**：自由相机滚轮缩放 (`F3`) · free camera FOV (F3)
- **重生**：重生无敌 / 重生延迟 / 自定义重生点 `O`/`P`/`K` · spawn invincibility / delay / custom points

**会话内容 Chat**
- 会话聊天记录面板（记录/发送；过滤快捷消息 / 隐藏游戏内聊天窗口）· in-session chat log panel

**实验 Experiments**
- **加载后清理**（进关卡 GC 减卡顿）· GC after level load
- **树屋问号**：给指定关卡的门加问号（解锁盒）· question marks on treehouse portals
- **声音静音**（自己/他人）/ **读取统计** / **作弊标识** / **功能解锁进度** · mute sounds, stats, cheat flag, unlock progress

---

## 全部按键 Keybinds

| 功能 Function | 默认键 Default |
|---|---|
| 管理器 Manager | `Insert` |
| 地图 Map / 传送 Teleport | `M` / `T` |
| 自由相机 Free camera | `F3` |
| 无视碰撞 Ignore collision | `F1` |
| 自由放置 Free placement | `F2` |
| 方块破坏（切换/删除）Destroy | `Alt` / `Backspace` |
| 快速自杀 Quick suicide | `Shift+0` |
| 重生点 设置/传送/恢复 Spawn | `O` / `P` / `K` |

> 在管理器内点击按键框即可改绑：`Esc` 清空，`Shift+Esc` 取消。

---

## 进度解锁 Progression
部分功能需先达标，否则灰显/禁用：

- **A 组**：游戏时长 > 17h16m18s 或 奔跑 > 52000m → 解锁 **无视碰撞 / 自由放置 / 树屋问号**
- **B 组**：游戏时长 > 52h 或 奔跑 > 100000m → 解锁 **方块破坏**
- Group A (>17h / >52000m): ignore collision, free placement, question marks · Group B (>52h / >100000m): destroy blocks. 进度在实验页查看 / live progress on the Experiments page.

---

## 致谢 Credits
BetterFreeplay · BetterNight · BuildingPlus · BuildUnlimiter · Even More Players · UCH Freeplay Spawn Setter · UCH Tweaks · UCH-PlayerTracker-Mod · UltimateBuilder

## License
MIT — 自由使用/修改/分发。 Free to use, modify, distribute.
