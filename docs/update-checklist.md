# 游戏更新后的适配清单

> 目标：游戏更新后 10 分钟内确认"SR_UCH 还能不能用"，并在失效时快速定位到功能。

## 三步回归（按顺序）

```powershell
# 1) 编译 + 10 项机械守卫（需要一个可用游戏目录）
.\build\build.ps1 -Check

# 2) 部署 + 实机冒烟（自动启动游戏、等 [SR.SelfTest] 标记、关游戏）
.\build\build.ps1 -Deploy
.\build\smoke.ps1

# 3) 导出启动审计报告（含补丁/反射/功能表/API 通道/自检结论）
.\build\build.ps1 -ExportAudit
```

判定标准：
- 第 1 步：`== 全部守卫通过 ✅ ==`
- 第 2 步：日志出现 `[SR.SelfTest] OK 游戏版本=… 契约=v2 功能=N API通道缺失=0 功能表问题=0`
- 第 3 步：`docs\audit-<游戏版本>.md` 生成，且"审计行"里没有 `✗`/`失效`

## 失效时怎么定位

| 日志线索 | 含义 | 处理 |
|---|---|---|
| `[SR.Patch] … 失效` | Harmony 补丁目标（游戏方法）改名/移动 | 按补丁清单里的类名去 `SR.RefOverride`/`[HarmonyPatch]` 搜，改目标名 |
| `[SR.Ref] … ✗ 成员名` | 反射成员（字段/方法/属性）找不到 | 按成员名在 `SR.RefField/RefMethod` 调用点定位，更新名字或加降级 |
| `[SR.Api] 未注入的 API 通道` | 启动时漏注入 sink | 补 `MainPlugin` 里的 `Set*Sinks` 调用 |
| `[SR.Feat] … 缺口依赖/契约过新` | 功能清单问题 | 检查 `[SrFeature]` 声明与 `SRContract.Version` |
| `[SR.SelfTest] FAIL` | 上面任一问题的汇总 | 按 FAIL 行里的字段逐项排查 |

## 适配完成的标准

1. 三步回归全部通过；
2. `docs\audit-<新版本>.md` 归档（旧版本报告保留，便于对比哪次更新引入了变化）；
3. 提交信息里写明游戏版本号。
## 例行：配置瘦身（每次发布前）

1. 跑一次游戏（或 `build\smoke.ps1`），日志会输出 `[SR.Cfg] 孤儿键全量: ...`；
2. `build\build.ps1 -ExportAudit` → `docs\audit-<版本>.md` 归档完整清单；
3. 对"已删除功能"的键：先静态校验 `"键名"` 在主模块源码中无引用，再在 `SR.ConfigMigration.cs` legacy 区加 `RemoveKeys(text, "段", new[]{ ... })`；
4. 不要删 `[Hotkeys]` / `[Settings] 组合键*` 这类按需生成的条目（删了会丢用户键位），交给设置页的"清理孤儿键"按钮；
5. 改完必须跑 `-Check`：守卫会核对"清理名单不得撞上活键"。