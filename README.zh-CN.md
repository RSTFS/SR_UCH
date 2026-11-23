[English](README.md) | **简体中文**

# SR_UCH

《Ultimate Chicken Horse》游戏内增强模组（BepInEx 5 / Harmony）。
把关卡、建造、聊天、联机与实验类功能收进一个游戏内设置面板——不用改游戏文件，也不用翻配置文件。

## 安装

1. 安装 **BepInEx 5.4 x64**，先运行一次游戏。
2. 把 `SR_UCH.dll` 放进 `Ultimate Chicken Horse\BepInEx\plugins\`。
3. 启动游戏，按 **Insert** 打开面板。

配置文件生成在 `BepInEx\config\SR_UCH_data.cfg`。

## 快速开始

| | |
|---|---|
| 打开面板 | **Insert**（可改键） |
| 总开关 | **All Enabled**（默认关，打开后各功能才生效） |
| 界面语言 | 默认英文，设置页可切中文 |

## 功能

- **建造** —— 无视碰撞、自由放置、网格显示。
- **关卡** —— 派对盒刷新/关闭、关卡工具、清空已放置方块。
- **联机** —— 可排序/筛选的房间列表，房间 mod 与版本检查。
- **混乱牌组** —— 每回合抽一张随机规则牌（房主抽；原版房客也会变）。
- **品质光芒** —— 按真实抽取概率给派对盒道具加径向柔光团。
- **视觉辅助** —— 淡化其他玩家，方便录屏/截图。
- **通知中心** —— 统一浮层通知，支持优先级、排队、位置与尺寸设置。
- **布局预设** —— 保存/加载/导入/导出建造布局，可排序、可滚动列表。
- **HUD / 玩家轨迹 / 方块破坏 / 会话 / 快速调整 / 角色 / 实验** ——
  可选增强，均可在面板中开关与改键。
- **自检** —— 补丁/反射/槽位/通知/配置审计、导出支持包、钩子耗时表。

大多数功能**只在本机生效**；房主权威类功能（混乱牌组、派对盒、方块破坏、关卡工具）
只在房主端或离线时生效，联机行为请以实机双人测试为准。

<!-- generated:facts:begin 由 tools/spec_engine.py 派生，勿手改 -->
- 功能分组 16 / 逻辑功能 28 / 核心自描述 0 / 纯函数文件 10
- CI jobs 3 / 本地化 ID 509 / 本区块由单一引擎派生
- 契约指纹 88e2695cb4ae… / 反射唯一 key 33
<!-- generated:facts:end -->

## 构建与测试

```powershell
./build/build.ps1                   # 构建（需要本机装游戏）
./build/build.ps1 -Check            # 统一回归（uch.py verify --strict）
./build/run-tests.ps1               # 单元测试（不需要游戏）
```

`build/build.ps1 -Deploy` 按 `build/specs/features.spec.json` 的产物清单复制 DLL
进 `BepInEx\plugins`：单个 `SR_UCH.dll`。请先退出游戏。

## 开发

- 功能只依赖契约层 `SR_UCH/Contracts/*.cs`。
- 构建脚本位于 `build/`，守卫规则位于 `tools/uch_rules.py`，测试位于 `tests/`。
- `python3 tools/uch.py docs` 生成唯一开发者文档 `docs/INDEX.md`。
- `build/specs/features.spec.json` 是功能分组、功能与产物的单一真值；
  `python3 tools/uch.py verify --strict` 是 CI 里的对账任务。

## 免责

非官方作品，与 Clever Endeavour Games 无关。使用风险自负；改关卡/建造类功能前建议备份存档。
