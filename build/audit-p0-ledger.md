# P0 审计移植台账（59 条 / 来源 D:\CC\123\audit）

状态取值：`todo` 待移植 / `ported` 已移植（附提交） / `already-fixed` 当前实现已等价修复 / `n-a` 不适用（附证据）

| # | 文档 | 编号 | 标题 | 状态 | 依据 |
|---|---|---|---|---|---|
| 1 | 01_SR.Core | P0-1 | PatchTargetExists 在重载歧义时仍返回 true，失效补丁不会被报告 | `ported` | 返回 hits==1；歧义按失效计 |
| 2 | 02_SR.Contracts | P0-1 | 114 个门面通道未注入时全部静默丢弃，启动期与 SetLogSinks 之前的日志与用户提示彻底消失 | `ported` | 注入前 32 条环形缓冲 + Debug 兜底，SetLogSinks 补发 |
| 3 | 02_SR.Contracts | P0-2 | Slots.Require 类型不符与新名字超过 64 条后静默丢弃，自检界面呈现"假干净" | `ported` | 超 64 条聚合计数 + LogWarn；MissingSinks 注入前 Debug.LogError |
| 4 | 03_门控与扩展点 | P0-01 | `UserMessage(show, false)` 命中错误的二参重载，所有屏幕提示被绑定到当前场景 | `ported` | 改调四参 UserMessage(show,2f,lo,false) |
| 5 | 03_门控与扩展点 | P0-02 | `CanDo(Cap.HostOnly)` 判断 `HasServer`，与“仅房主可执行”的文案和名称不一致 | `ported` | Cap.HostOnly 改判 Env.IsHost |
| 6 | 04_主窗口框架 | P0-1 | `_dirtyConfig` 单槽覆盖会让先改的 ConfigFile 永不落盘 | `ported` | _dirtyConfig 单槽改 HashSet，逐个保存成功才移除 |
| 7 | 04_主窗口框架 | P0-2 | 录制超时与 CommitRecording 在同一帧并存，可能提交错误序列 | `already-fixed` | CancelCapture 原子清 _capturing/_recSeq/_recHeld；CommitRecording 已判 _recSeq.Count==0，仅补注释说明 |
| 8 | 04_主窗口框架 | P0-3 | DrawGUI 的 finally 不能保证 BeginArea / 滚动组对称闭合 | `todo` |  |
| 9 | 04_主窗口框架 | P0-4 | 共享 skin 选择色/字体的还原依赖“SR 样式仍在内存” | `already-fixed` | EnsureFont 已不再写 GUI.skin（早前修复）；每帧 _prevFont 在任何写之前快照，还原走 OrigSkinFont() ?? _prevFont，不依赖样式对象存活 |
| 10 | 05_键位与调色板 | P0-1 | 序列半完成状态跨帧残留，且可能在非 Layout/Repaint 事件上重入 | `ported` | 序列进度加 1.2 秒时效 + 成功后清空进度；字典超 256 条整体清空 |
| 11 | 05_键位与调色板 | P0-2 | `_watchKeys` 无限增长且无失效覆盖，直接改 BoxedValue 时可能漏失效 | `todo` |  |
| 12 | 05_键位与调色板 | P0-3 | `_pendingComboDefault` 迁移可能覆盖已持久化的“无修饰键”配置 | `ported` | 延迟默认判定改为看持久化字符串是否为空 |
| 13 | 05_键位与调色板 | P0-4 | `legacy.Value=""` 会在读回时把用户旧配置清空，且迁移判断忽略 extras | `todo` |  |
| 14 | 05_键位与调色板 | P0-5 | 颜色选择器拖动中丢失 MouseUp 时 Dirty 永久存在，Tick 兜底不覆盖全部路径 | `ported` | Tick 里在 8 帧节流之前补判：Dirty 且鼠标全松 -> 立即 Commit |
| 15 | 05_键位与调色板 | P0-6 | `Tick` 兜底跳过条件与“窗口不再绘制”互斥，关闭/切页可能永远不落盘 | `ported` | 收起/换页时 RegisterDismissHook 显式落盘；Tick 异常不再静默；注册失败立即提交一次 |
| 16 | 06_控件门面Ctl | P0-1 | 操作列 MouseDown 置位但 MouseUp 未命中时不复位 BlockWindowDrag | `ported` | 每帧鼠标全松时兜底清 BlockWindowDrag |
| 17 | 06_控件门面Ctl | P0-2 | 表格高度拖拽中途 MouseUp 外泄，同样可能让窗口拖拽永久阻塞 | `ported` | 同上兜底覆盖表格高度拖拽外泄 |
| 18 | 07_表格与样式 | P0-1 | 控制台表格未接入滚轮输入，玩家列表无法滚动 | `already-fixed` | EX 控制台表格在 ExModule.Console.cs:261-276 自己处理 ScrollWheel（含 maxY=0 转横向），审计针对旧快照 |
| 19 | 07_表格与样式 | P0-2 | 选中行高亮在非 Repaint 事件上逐行调用 DrawTexture | `ported` | 行选中高亮加 Repaint 门控，非 Repaint 不调 DrawTexture |
| 20 | 07_表格与样式 | P0-3 | 字体回退路径会销毁当前正在使用的字体 | `ported` | 旧字体延迟销毁：先记 _retiredFont，下次重建且皮肤不再引用时才 Destroy |
| 21 | 07_表格与样式 | P0-4 | 样式重建的纹理泄漏：未登记到 _styleList 的共享纹理被反复 new 并 DontDestroyOnLoad | `ported` | Solid() 纹理登记到 _styleTex，重建时统一 Destroy |
| 22 | 07_表格与样式 | P0-5 | 表头裁剪块缺少 GUI.color 保护，且鼠标坐标语义可能跨裁剪原点错位 | `ported` | 表头与钉住列表头两个裁剪块都保存/还原 GUI.color |
| 23 | 08_本地化与设置页 | P0-01 | SetValue 在编辑框自身提交帧跳过 `_editText` 清理，会回滚掉"1."之外的后续编辑 | `todo` |  |
| 24 | 08_本地化与设置页 | P0-02 | ApplyCmOrder 用反射写 `<Description>k__BackingField`，BepInEx 5 实际字段名是 `description`（无编译器生成名） | `ported` | 改为按类型+名字含 escription 探测后备字段，探测失败 LogWarn（带 BepInEx 版本） |
| 25 | 08_本地化与设置页 | P0-03 | ResetAllDefaults 只复位 `Section == "Settings"` 的条目，Sidebar/Internal 段与外部插件段被跳过且窗口几何被一并复位 | `ported` | 恢复默认跳过 WinWidth/WinHeight/WinX/WinY/SidebarWidth，Notify 说明几何未动 |
| 26 | 08_本地化与设置页 | P0-04 | ModeLabel 缓存键用 `GameSettings.GetInstance().GameMode`，但 `gmKey` 声明在使用之后、且 catch 块吞掉异常后 `gmKey` 保持 `CREATIVE` 默认值 | `ported` | GameMode 读取失败时不写缓存键，直接 ModeLabelRaw（避免页脚模式文本闪烁+全量重算） |
| 27 | 09_配置迁移 | P0-01 | 删除判定是"手工死键名单"，名单与活键脱节时会在 Bind 之前永久抹掉用户设置 | `ported` | 删除键登记 + 启动日志一次性汇总（迁移时尚未 Bind，无法判定活键；深度修法=post-bind 孤儿清理，记为 P1） |
| 28 | 09_配置迁移 | P0-02 | `_xPending` 是无版本号布尔，"写盘失败仍复位"会让规则永久失效 | `already-fixed` | RunPending 仅在 WriteCfgAtomic 成功后清 _xPending，失败保留待重试 |
| 29 | 09_配置迁移 | P0-03 | 写盘后若调用方未执行 `ConfigFile.Reload()`，迁移结果会与内存中的 ConfigEntry 值分叉 | `already-fixed` | RunPending 返回 true 后调用方 pe.config.Reload() + ClearEntryCache（SR.Core.cs:1154-1157） |
| 30 | 10_布局预设核心 | P0-1 | Provide 只暴露只读锁状态，未见与 Level 功能的双向 Require | `todo` |  |
| 31 | 10_布局预设核心 | P0-2 | 加载异常或失败路径可能永久持有本功能重载锁 | `todo` |  |
| 32 | 11_预设IO与修改器 | P0-1 | ResolveImportPath 的目录穿越防护可被 `..` 与路径分隔符绕过 | `ported` | IsUnderRoot + 绝对路径根校验（提交 5e153fb） |
| 33 | 11_预设IO与修改器 | P0-2 | SafeFileName 净化结果与用户意图脱节，造成"找不到文件"且越界读风险并存 | `ported` | SafeFileName 拒绝 .. 与两种分隔符（提交 5e153fb） |
| 34 | 12_预设加载与工具 | P0-1 | 场景切换掐断挂载在 LobbyManager 上的加载协程，25 秒 XML 清理与放锁逻辑永久失效，仅能靠 45 秒自愈 | `todo` |  |
| 35 | 12_预设加载与工具 | P0-2 | `levelPortalXml` 是 static 字段，协程被掐后脏 XML 残留，下一次原生开局会误加载旧快照 | `todo` |  |
| 36 | 12_预设加载与工具 | P0-3 | 联机直接替换关卡时房客 `SelectedLevel` 不同步，且失败后无任何回退机制 | `todo` |  |
| 37 | 12_预设加载与工具 | P0-4 | `TryReadPresetMeta` 遇自闭合 `<scene .../>` 直接返回 false，该预设元数据全丢失 | `todo` |  |
| 38 | 13_联机主流程 | P0-01 | `DisbandOrLeave` 二次点击捷径会在"刚进主菜单又点一次"时误触发 | `todo` |  |
| 39 | 13_联机主流程 | P0-02 | `ForceMainMenu` 直接 `Destroy(LobbyManager.gameObject)` 可能打断游戏自己的收尾 | `todo` |  |
| 40 | 13_联机主流程 | P0-03 | `ForceMainMenu` 先 `LoadScene("MainMenu")` 后设 `_forceRawDeadline`，在场景切换失败时会永久不硬切 | `todo` |  |
| 41 | 13_联机主流程 | P0-04 | `GoMainMenu` 与 `ForceMainMenu` 的 `ClearPendingJoin` 时机不一致，可能让"退出后立刻加入"重复触发 | `todo` |  |
| 42 | 13_联机主流程 | P0-05 | `HostEndedListener` 幂等哨兵依赖"不重复 Initialize"，热重载会叠成 N 份回调（未验证） | `todo` |  |
| 43 | 14_联机加入与列表 | P0-1 | 加入协程挂在 Matchmaker 上，切场景/离房即被销毁，加入请求永不发出 | `todo` |  |
| 44 | 14_联机加入与列表 | P0-2 | DoJoin 两条"已发出"清空路径互相覆盖，待加入队列实际由 TickPendingJoin 驱动，InSession() 判据可能永久为真 | `todo` |  |
| 45 | 14_联机加入与列表 | P0-3 | IsStale（心跳 10 秒）只改显示字符串，不参与 IsJoinable，用户会点进死房间 | `todo` |  |
| 46 | 15_关卡重载与快照 | P0-1 | FillScoresCoroutine 的 finally 无法覆盖协程被销毁场景，导致分数翻倍 | `todo` |  |
| 47 | 15_关卡重载与快照 | P0-2 | levelPortalXml 清理绑定协程生命周期，协程销毁时旧快照污染下一局 | `todo` |  |
| 48 | 15_关卡重载与快照 | P0-3 | _clearedExternBlocks 按对象引用去重会合并错分块，造成补回错误类型 | `todo` |  |
| 49 | 15_关卡重载与快照 | P0-4 | NetworkServer.active 在协程内可能翻转，补分阶段未再校验 | `todo` |  |
| 50 | 16_回合HUD主实现 | P0-1 | 默认 `Show Mode = Hold` 使"开局即有 HUD"的修复完全失效，打开总开关后 HUD 不可见 | `ported` | Show Mode 默认从 Hold 改为 Toggle，开局即显示 HUD，按键只切显隐 |
| 51 | 17_会话聊天 | P0-1 | 取不到本地玩家号时按 -1 发送，消息被服务端与所有客户端静默丢弃 | `ported` | 取不到本地玩家号时不再按 -1 发送（会被全体丢弃），改为 Notify 提示并返回 |
| 52 | 19_方块破坏 | P0-1 | `_useSmoke` 配置项从未被读取，用户关闭后烟雾效果仍播放 | `ported` | 游戏 DestroySelf 就有 useSmoke 参数：开关传进去真正生效（上一版误删已回滚） |
| 53 | 19_方块破坏 | P0-2 | `DestroySelf` 后紧接 `OnDestroy` 调用，可能触发重复销毁或状态不一致 | `ported` | 去掉手工 target.OnDestroy()，DestroySelf 自己已标记并收尾，避免二次销毁 |
| 54 | 20_实验模块与派对盒 | P0-1 | `CrossPlay.TryPersist` 按方法名子串猜测并 Invoke 保存入口，可能触发破坏性方法 | `ported` | 保存入口改为白名单精确名 + 必须声明在目标类型自身，不再子串匹配继承链 |
| 55 | 21_HUD状态与轨迹 | P0-1 | `DiagTick` 是未关闭的调试残留，会长期污染日志 | `ported` | 临时诊断 DiagTick 已删除（提交 97172cf） |
| 56 | 21_HUD状态与轨迹 | P0-2 | `OnShutdown` 只销毁根对象，未注销 `LevelResetEvent` 监听器，重载模组会重复订阅 | `todo` |  |
| 57 | 21_HUD状态与轨迹 | P0-3 | `networkNumber` 与数组槽位下标耦合，索引不连续时会创建大量空 `LineRenderer` | `todo` |  |
| 58 | 21_HUD状态与轨迹 | P0-4 | 挑战模式分支绕过"结算证据"门槛，回合提示会在一局内反复重播 | `todo` |  |
| 59 | 21_HUD状态与轨迹 | P0-5 | `UpdateChallengeTimer` 中 `runTime`/`runStarted` 字段名未经游戏侧核实，反射取不到会静默退化 | `todo` |  |
