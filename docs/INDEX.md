<!-- generated:uch.py docs -->
# SR_UCH 维护手册

本文件是唯一文档入口，由 `python3 tools/uch.py docs` 生成。
数值来自 `build/specs/generated/facts.generated.json` 与 `docs/STATUS.md`。

## 结构：一个入口 / 一个引擎 / 一份规则

- 唯一声明：`build/specs/features.spec.json`（schema 3，旧版直接拒绝）
- 唯一入口：`tools/uch.py`（verify / gen / doctor / smoke / add / remove / docs …）
- 唯一引擎：`tools/spec_engine.py`（从 spec + 磁盘源码派生全部生成物）
- 唯一规则：`tools/uch_rules.py`（33 条机械守卫，`uch.py rules --suite all`）
- 独立守卫：`tools/check_architecture.py`（分层）、`tools/check_uimodel.py`（页面）
- 构建：`build/build.ps1`（rsp 单链）、`build/run-tests.ps1`（真实程序集测试）
- 派生物：`build/rsp/`、`build/specs/generated/`、`*.g.cs` 由 `uch.py gen` 重算，勿手改

## 日常命令

```
python3 tools/uch.py verify --strict   # 统一回归（固定 CHK + 单一基线）
python3 tools/uch.py doctor            # 环境诊断与下一步
python3 tools/uch.py gen               # 改了声明/源码后重算派生物
python3 tools/uch.py analyze          # 装载顺序 / SRApi 门面注入前置离线对账
```

加功能：`uch.py add <Name>`（生成骨架并打印声明片段）；
删功能：`uch.py remove <Name>`（列出残渣清单）→ 手工删除后 `gen` + `verify`。

## 口径（既定，不再改动）

- 单 DLL 构建；split 需求已移除；EX 与独立插件在各自项目构建
- 关闭混乱牌组 → 所有人的修改器清空（无快照回滚）
- 派对盒炸弹不处理；不加搜索框；README 描述文案人工维护
- 不兼容旧版本：Tid 只认 @id，cfg schema 门 == 3

## 诊断与游戏更新

- 游戏更新后跑 `uch.py fingerprint --dump <反编译.cs>`：重比签名，红哪条改哪个功能
- 运行时健康状态由 `[SR.SelfTest]` / 诊断报告 / 结构化事件码给出
- 双轨 UI 仍并跑：`uch.py trace-gate --enforce` 校验声明完整；
  旧 IMGUI 路径的退役需实机 A/B 证据，未达标不请求 cutover

## 验证边界

- CI（3 个 job）：统一回归 / 契约层与纯函数层真实编译 / 构建图与全源码 IL
- 编译输入（rsp / compile.targets / references.props）已入库，fresh clone 直接 `dotnet build` 只需 `UCH_MANAGED_DIR`
- 报告与基线：`build/specs/generated/verify.manifest.json`（固定 CHK ID）
