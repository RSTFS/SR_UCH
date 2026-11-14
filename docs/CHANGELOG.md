# 变更记录（自动生成）

> 由 `build/gen-release-notes.ps1` 从 git 历史生成，**不要手工编辑**。
> 当前 HEAD `8a0a9ce`；契约 ApiLevel `4`。

## 新功能 / 优化

- feat(contract): TableArgs.Create + Validate（必填校验）+ 核心按问题签名记日志（ApiLevel 3）
- feat(contract): 网络魔数注册表 + 自检唯一性检查（ApiLevel 2）
- feat(contract): 注入状态通道登记（Sinks.State）+ 自检检查
- feat(core): Storage 绝对路径 API + 单测修复 + 布局预设原子写迁移（契约 v8）
- feat(core): 统一存储 API（SRApi.Storage，契约 v7）
- feat(core): NotifyEx 元数据通道 + 通知动作按钮（契约 v4）
- feat(presets): 表头点击排序（SortSpecs 式交互）+ Esc 取消二次确认
- feat(ui): 网上设计落地——通知符号/关闭按钮、标签列 token、下拉 Tab 导航、空状态、确认倒计时
- feat(net): 联机版本/功能握手（双方 SR 功能表不一致时提示）
- feat(selfcheck): 主动自检 + 支持包导出 + 最近异常 + 启动耗时（诊断批次）
- feat(notify,selfcheck): 通知悬停暂停/点击关闭 + 自检「第三方插件」兼容性卡
- feat(visualaid): 视觉辅助——淡化/隐藏其他玩家（CamHog 等价，自动让位）
- feat(qa): 三处「运行时验收辅助」入口（不新增配置，只触发真实路径）
- feat(notify): 重写浮层调度——不漏消息 + 排队 + 平滑过渡
- feat(chaos): 当前牌面卡片化（牌名 + 效果 + 增益/减益色边）
- feat(chaos): 整条删除房客提案（死代码清理）
- feat(core): 可折叠分区 SRApi.Ctl.SectionBegin/SectionEnd
- feat(core): 按帧缓存 SRApi.Cache.FrameSig；Chaos.Desc 用它

## 修复

- fix(build): 版本文件写入块此前锚点不匹配未生效，修正后随 -Deploy 落盘 SR_UCH.version.txt
- fix: 按审查报告修 P0 联机越权 + P1 逻辑/还原 + P2 收尾 + 文档对齐实现
- fix(ui): 预设列表滚到底、重生默认 0.5、下拉滚轮/页面联动、调色板左侧方块对齐、通知排序、牌池增强
- fix(presets): 「只剩一条」根因是默认类型筛选只显示自存预设；补齐填充页滚轮
- fix: 通知设置精简/自定字号、混乱牌组会话不存盘、重生延迟标签恢复默认、预设列表对齐联机列表
- fix(ui): 通知浮层重设、长按连发移除、预设列表照联机列表重做、牌池与调色板调整
- fix: 删除权限提醒日志、修复分区通道未注入、快捷键同行、预设列表高度、重抽无效、牌池游戏名
- fix(ui): 按 17 条反馈重做通知/视觉辅助/混乱牌组/自检/模式限制等界面
- fix: 启动自检的两条误报 + 清理遗留死键
- fix(notify): 浮层正文底部缓冲 1px → 2px

## 性能

- perf(core): 快捷键无输入短路 + Guard 去重表容量上限
- perf: Online 表格委托缓存 + 通知卡片文本高度缓存
- perf(core+presets): 表格委托缓存、标签宽度缓存、自检性能计数器
- perf(core): 去掉每帧闭包分配与动态条目名字符串拼接

## 重构

- refactor(features): DestroyBlocks 模式门控改用 FeatureAllowed 元数据
- refactor(features): 快捷键轮询迁移第二批（7 -> 3 处）
- refactor(features): 快捷键轮询迁移第一批（14 -> 7 处）
- refactor(contract): SRApi.cs 按域拆分（Storage / Notify 独立分部文件）
- refactor(contract): 槽位常量全量迁移（reloadBusy）+ 结构守卫扫描契约层
- refactor(contract): 版本语义拆分 + 槽位常量 + API 白名单自动生成 + Storage.Ready
- refactor(core): 配置行默认宽度助手 + 动态条目名核心化（契约 v6）
- refactor(core): 功能级模式门控核心化（[SrFeature].Modes + SRApi.FeatureAllowed，契约 v5）
- refactor(core): 键位动作核心化 + 功能模式元数据（契约 v3）
- refactor(presets): 列表核心按「联机」列表重写（滚轮/滚动条/光标/填充页）
- refactor(contract): A 方案——契约层拆目录成 5 个文件，仍单程序集发布
- refactor(core): SR.Core.cs 拆出每帧钩子/剖析（SR.Hooks.cs，1173 → 878 行）
- refactor(core): SR.Ctl.cs 拆出悬浮提示与绘制原语（1052 → 852 行）
- refactor(core): SR.Settings.cs 拆出自检页（SR.SelfCheck.cs，882 行）
- refactor(core): 跨分部私有字段转访问器，region-access 基线 74 → 51

## 杂项

- chore: 预设删除清理/残留 .tmp 清扫 + 部署版本文件（git hash/ApiLevel）
- chore(build): 清理 BuildingTools 旧键名残留；自检新增「按键中心」卡片

## 文档

- docs: DESIGN 更新到当前实现；脚手架模板改用仓库的 SelfReg 约定

## 测试

- test: 契约层单测 20 → 73 条（Slots / 功能清单 / 几何 / 通道）

## 其它

- perf+robustness: 预设行文本帧缓存、保存前 .bak 备份、契约面自动文档
- build: -Features 裁剪构建（只把指定功能编进 DLL 的 Lite 版）
- build: 新功能脚手架 + 死代码编译守卫 + 文档脚本引用守卫
- build: 仓库内构建链补全为唯一真源 + rsp 由磁盘源码生成（-Check/CI 校验）
- opt: 牌池页搜索/分组；文本守卫收敛成一份实现
- opt: 房客关开关后状态说真话 / 品质光芒光晕登记表 / API 白名单去假条目
- opt: 同规则双牌显示收口 / 卡面图例 / 握手降频 / 品质光芒权重缓存 / 孤儿槽位守卫
- cleanup: 删除死代码/旧内容并做一轮优化（净删 625 行）
- harden(notify): 注销槽位时传自己的委托，避免误删后来注册的实现

