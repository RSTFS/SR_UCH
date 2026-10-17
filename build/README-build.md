# 构建说明（仓库内构建链）

本目录让"只拿到本仓库源码"的人也能构建出 `SR_UCH.dll`，不再依赖外部 `_tools\`。

## 依赖

- Windows + PowerShell 5.1+
- **.NET SDK**（含 Roslyn 编译器）：`dotnet --list-sdks` 能看到 ≥ 6.0 即可；脚本会自动从 SDK 里找 `csc.dll`
- **游戏本体**（提供 `Assembly-CSharp.dll` / `UnityEngine*.dll` / `BepInEx` 等引用）

## 用法

```powershell
# 最简：自动探测 Steam 常见安装路径
.\build\build.ps1

# 显式指定游戏目录（推荐）
.\build\build.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Ultimate Chicken Horse"

# 其它常用
.\build\build.ps1 -Deploy          # 构建并复制到 <Game>\BepInEx\plugins
.\build\build.ps1 -Check           # 只跑机械守卫（10 项）
.\build\build.ps1 -Split           # 拆分模式：核心 + 每个功能一个 DLL
.\build\build.ps1 -Separate        # 契约程序集分离（SR.Contracts.dll）
```

也可用环境变量代替 `-GameDir`：`setx UCH_DIR "…\Ultimate Chicken Horse"`。

## 产物

- 默认（合并模式）：`SR_UCH\bin\Release\SR_UCH.dll`
- `-Split`：`SR_Manager.dll`（核心）+ `SR_UCH_<功能>.dll` × 8
- `-Separate`：额外产出 `SR.Contracts.dll`（契约程序集）

## rsp 模板

`build\rsp\*.rsp` 是编译清单模板，游戏相关引用用 `%GAME%` 占位；脚本运行时会替换成实际路径并写入 `build\.cache\`（该目录已加入 .gitignore）。

## 机械守卫（-Check，10 项）

契约层可独立编译 · rsp 精确覆盖源码 · 迁移规则与 Config.Bind 无冲突 · IDE 工程可构建 · 功能层 API 棘轮白名单为 0 · 契约层不含实现标识 · 功能层不含实现标识 · 注入点全部已调用 · 拆分/分离模式可编译（按需）。
