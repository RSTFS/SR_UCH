<!-- 整份由 tools/spec_engine.py 派生；勿手改 -->

# 项目事实（自动生成）

| 项 | 值 |
|---|---|
| groups | 16 |
| features | 28 |
| core_features | 0 |
| ci_jobs | 3 |
| loc_ids | 509 |
| rsp_sources | 123 |
| reflection_unique | 33 |
| fingerprint_reflection_entries | 104 |
| pure_files | 10 |
| scenes | 2 |
| net_magics | 4 |
| health_fields | 20 |
| fingerprint_hash | 88e2695cb4ae9b4da8e548d4c30d60b2c71fd8861bded265f303b90cd96e7c27 |

维护入口：

- 日常：`python3 tools/uch.py verify --strict`（fresh clone 自动自举）
- 游戏更新：`python3 tools/uch.py fingerprint --dump <反编译.cs>`，红哪条改哪个功能；唯一绑定表 `generated/contract-fingerprint.json`
- 加功能：`python3 tools/uch.py add <id>`；删功能：`python3 tools/uch.py remove <id>`
- 改声明：`python3 tools/uch.py gen`（单一引擎重算全部派生产物并 seal）
- 自检期望：`generated/selftest.expected.json`；运行时值写 `health.actual.json`
- 棘轮基线：`generated/ratchets.json`（守卫只读这一份）
