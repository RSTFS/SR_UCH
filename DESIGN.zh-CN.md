# SR_UCH 设计说明（当前实现）

> 面向"要在这个仓库里继续加东西/改东西"的人。描述的是**现状与约定**，不是历史。
> 截图、实机行为以游戏内为准；本文只保证与源码一致（仓库 HEAD）。

---

## 1. 目标与边界

| 目标 | 做法 |
|---|---|
| 一个 BepInEx 插件提供所有增强 | 合并模式只出一个 `SR_UCH.dll`；拆分模式按功能出 DLL，两种模式产物互斥 |
| 功能可独立开发/裁剪 | 功能只依赖**契约层**（`SR.Contracts.cs`），不引用核心实现 |
| 改游戏但尽量不破坏联机 | 所有改动走游戏自己的事件/消息/Command，不自己造同步协议 |
| 出问题能定位 | 统一 `Guard` 日志（去重）、启动自检（补丁/反射/门控/孤儿键）、帧钩子剖析 |

---

## 2. 分层

```
MainPlugin.cs                 入口：BepInEx 插件；配置外置到 SR_UCH_data.cfg；扫描并初始化 ITweak
SR_UCH/SR.Contracts.cs        契约层（SRApi）：功能能用的一切 API；可独立编译
SR_UCH/SR.*.cs                实现层（partial class SR）：窗口 / 控件 / 表格 / 键位 / 门控 / 迁移
Features/*.cs                 功能层：每个文件一组 [SrFeature] 类，实现 ITweak
build/                        构建脚本 + rsp 清单 + 守卫 + 单元测试
```

**方向约束（有守卫检查）**

- 功能层只允许出现 `SRApi.*`（白名单 110 个符号），不得出现 `SR.*` 实现层标识
- 契约层不得引用实现层类型（`sr_uch_contracts.rsp` 独立编译验证）
- 功能之间**不得编译期互相引用**：需要协作时用 `SRApi.Slots`（能力槽）或事件

---

## 3. 一个功能长什么样

```csharp
[SrFeature("sr.demo", "Demo", "1.0")]          // id / 显示名 / 版本
public class Demo : ITweak {
    private const string Sec = "Demo";          // 配置段名 = 栏目名
    private static ConfigEntry<bool> _enabled;

    private static void SelfReg() {             // 只做注册，不碰游戏对象
        SRApi.LocSec(Sec, "示例", null);         // 段名本地化
        SRApi.Nav(Sec, 60);                      // 侧栏位置（越小越靠前，避免序号撞车）
        SRApi.RegisterPage(Sec, Render);         // 单页；多页用 6 参重载
        SRApi.LocKey(Sec, "Enabled", "总开关", "Master switch");
        SRApi.LocDesc(Sec, "Enabled",
            "总开关\n关掉后本功能不做任何事\n只影响本功能，不动其它设置",
            "Master switch\nwhen off this feature does nothing\naffects this feature only");
    }

    public void Initialize(IFeatureHost plugin) {
        try {
            SelfReg();
            _enabled = plugin.Config.Bind(Sec, "Enabled", false, "总开关");
            SRApi.RegisterTick("示例-每帧", Tick);
            SRApi.RegisterSceneHook(delegate { /* 换场景复位 */ });
            // 需要读游戏私有成员：登记 + 用 SRApi.Ref
            SRApi.Ref.Override("SomeClass.privateField", "示例（读取私有字段）");
        } catch (Exception __ex) { SRApi.Guard.Log("示例/初始化", __ex); }
    }

    private static void Tick() { /* 每帧；贵的活自己按时间限流 */ }

    private static void Render() { /* 页面 */ }
}
```

**初始化必须整段 try**：中途抛异常会让功能"静默消失"，而且可能留下 DontDestroyOnLoad 对象。

---

## 4. UI 页面的写法

### 4.1 页面注册

```csharp
SRApi.RegisterPage(Sec, Render);                                   // 单页
SRApi.RegisterPage(Sec, "auto", "自动选择", "Auto pick", 10, RenderAuto);  // 多页：id/中/英/顺序
```

- 多页注册后核心自动画页签，切页时**收起下拉框、页面回到顶部**
- 选中的子页跨会话记住（`[UI] Page <段名>`）；页面被移除时自动回退第一页
- 页面里**不要**自己 `PageScroll = Vector2.zero`：换页归零由核心负责

### 4.2 控件

| 需求 | 用法 |
|---|---|
| 画一个配置条目（标签+控件，按类型自动选控件） | `SRApi.RenderEntryRow(entry, true, col)` 或 `SRApi.Ctl.DrawEntryRow(entry, col)` |
| **统一行入口（推荐）**：标签 + 自动控件 | `SRApi.Ctl.Row(label, tip, entry, labelWidth)`（= `LabeledRow`，按 bool/float/枚举/按键/颜色自动选控件） |
| 标签 + 滑块 + 数值 | `SRApi.Ctl.RowSlider(label, tip, entry, min, max, "0.0", labelWidth)` |
| 标签 + 下拉（entry 作稳定 ID，选中即收起） | `SRApi.Ctl.RowCombo(label, tip, entry, vals, labels, width, labelWidth)` |
| 下拉框 | `SRApi.Ctl.ComboBox(entry, current, vals, labels, ref open, width)` —— **entry 必须传真实配置条目**，否则展开状态无法被核心管理 |
| 颜色 | `SRApi.Ctl.ColorEdit(content, entry, w, h)`（值格式 `#RRGGBB`） |
| 表格 | 构造 `SRApi.TableArgs` 后 `SRApi.TableDraw(args)`；高度用 `SRApi.ListTableHeight` / `Ctl.TableAutoHeight` |
| 页签/分节标题 | `SRApi.Ctl.SectionTitle(title, tip)` |
| 状态行 | `SRApi.Ctl.LabelAuto(text)` / `SRApi.Ctl.LabelWrap` |

### 4.3 三条硬约束（都踩过坑）

0. **GUI 状态用作用域**：禁用/着色一律 `using (SRApi.Ctl.Disabled(cond))`、`using (SRApi.Ctl.Tint(c))`，
   不要手写 `GUI.enabled = old` 再还原（漏一次就整页变灰）。对应 ImGui 的 `BeginDisabled/PushStyleColor`。
   数值行支持 **Ctrl+点击滑块 → 直接输入数值**；下拉框展开后支持 **Up/Down 换项、Enter/Esc 收起**。
0. **标签列宽由核心按页收敛**：统一行的标签宽度取"本页实际出现过的标签"的最大值
   （两帧收敛，换页重置），不要自己传固定宽度、也不要用整段最长条目名——那会让标签比文字宽一大截。
   需要自定义行时用 `SRApi.Ctl.LabelColumnWidth(label)` 取同一列宽。
0b. **只用核心控件**：页面里不要自己写按钮/下拉框/滑块/列表。统一走
   `SRApi.Ctl.Row`（自动按类型选控件）、`RowSlider`、`RowCombo`、`Ctl.Button`、`Ctl.TableDraw`。
   自绘控件会绕过滚轮仲裁、悬浮提示、标签对齐和缩放，属于缺陷来源（动物下拉曾自绘，导致"列表不全"）。
   核心控件不够用时，先给核心加能力，再在页面调用——不要复制一份到功能层。
：`Row` / `RowSlider` / `RowCombo`。标签宽度实测、悬浮说明、控件类型分派只有一份实现，页面不再手写 `BeginHorizontal + RestoreLabel + RenderControl`；只有确实需要自定义排版的行（如带草稿输入的评分百分比）才手写。
1. **Layout 趟与 Repaint 趟的控件数量必须一致**。任何"按某个值决定画不画"的行，如果那个值可能在同一帧被下拉框改写，就会 `Mismatched LayoutGroup.repaint`。做法：结构固定，值不满足时**置灰**（`GUI.enabled=false`）而不是不画。
2. **下拉框/滑块的展开状态必须交给核心**（用配置条目做 key）。自己维护 `ref open` 会出现"关不掉、并持续吃掉滚轮"，表现为整页滚不动。
3. **Begin/End 必须成对**，中间可能抛异常的段落用 `try/finally { EndHorizontal }`。

### 4.3a 两种页面布局：滚动页 / 填充页

| 布局 | 适用 | 做法 |
|---|---|---|
| **滚动页**（默认） | 配置型页面 | 内容随页面滚动；控件在页面内正常排版 |
| **填充页** | 列表为主的页面（联机、布局预设） | 每帧 `NoPageScrollBars = true`；页头（保存/筛选/工具行）固定，列表高度 = `PageViewportHeight − 头部已用高度 − 拖拽条`；**整页不滚，只有列表滚** |

填充页的结构固定为 `固定头部 → 列表 → 底边拖拽条` 三段，新增列表页时照抄联机页/预设页的
`RenderXxxHeader() + ComputeFillTableHeight() + RenderXxxTable()` 三段式，不要再让页面滚动与列表滚动并存。

### 4.3c 搜索命中高亮

预设页搜索框命中时，表格单元格里命中的**片段**会先画一层半透明底色（琥珀 22%），再画文字，
对应 ImGui demo 的 filtering 视觉反馈。实现：

- 页面每帧 `SRApi.Ctl.SetTableHighlight(查询串)`（空串 = 关闭）
- 核心 `DrawTableCells` 用**同一字体**测量前缀宽度来定位高亮矩形（左对齐列从左侧 4px 起，其余列按居中偏移）
- 换页时核心自动清空查询串，避免旧查询串染到别的列表

### 4.3b 表格列策略

对齐 ImGui 的 `ImGuiTableColumnFlags_WidthFixed/WidthStretch`：每列有自己的宽度策略。

| 策略 | 列 | 行为 |
|---|---|---|
| **固定** | 其余数据列 | 宽度来自持久化配置，可拖动调整 |
| **拉伸** | 预设页「名称」、联机页「房主」 | 每帧重算：扣掉其余可见固定列后占满剩余宽度，下限 80（联机 70）设计单位；窗口宽时不留空，窄时其余列溢出并出现横向滚动 |
| **钉住** | 操作/加入列 | 固定在右侧、不参与横向滚动 |

注意：

- 拉伸列在**绘制副本**里计算（预设页 `_drawW`），不会把拉伸值写进配置；拖动固定列的宽度照常回写持久化值
- 新增列表时给"最长文本"的那一列标为拉伸列，不要把宽度写死
- 冻结**左侧多列**（ImGui 的 `TableSetupScrollFreeze`）尚未实现：目前只有右侧钉住列

### 4.4 滚轮（"待处理量"模型，对齐 Dear ImGui 的输入路由）

ImGui 的 examples/ 目录里放的都是**后端接入**（把平台事件喂给 `io.AddMouseWheelEvent`）与
`ShowDemoWindow` 演示；真正的滚轮路由在核心：`imgui.cpp` 的 `UpdateMouseWheel()` 把
`io.MouseWheel` 一次性应用到悬停窗口的滚动，`imgui_widgets.cpp` 的 `DragBehaviorT` 等控件只在
`g.HoveredId == id` 时消费 `io.MouseWheel` 改值。要点是**同一帧先累加、再按控件优先消费、剩下的给窗口滚动**。
我们照这个模型实现（`SR_UCH/SR.Wheel.cs`）：

```
事件趟 (ScrollWheel)
  └─ Wheel.FrameStart: 记录一格滚轮量（上滚=+1）+ 鼠标位置，并立即 e.Use()
                        → 任何 Unity 滚动视图都不会再处理这一次滚轮
绘制趟（同一趟内，页面/控件依次绘制）
  ├─ 控件/表格：SRApi.Ctl.WheelTake(自己的矩形)
  │     鼠标在矩形内 → 取走这一格并改值（返回 0 表示不归我）
  └─ 页面：用剩下的量滚动（Shift = 横向）
帧尾
  └─ Wheel.TakeRemaining: 仍没人取走 → 页面滚动；页面声明 NoPageScrollBars → 丢弃
```

关键点：

- **不再有"区域表 + 坐标换算"**：控件用的是自己绘制期的矩形，与鼠标同源，换多少层容器都不会错位
- **不再有"哪一层先命中"**：取用是显式的、一次性的（`WheelTake` 取走即清零）
- 同一帧内多次滚轮事件**累加**（与 `io.MouseWheel += wheel` 一致），不会后一次覆盖前一次
- 一次滚轮 = **±1**，页面步长固定 **64px**（不乘设备 delta，避免"一次滚很远、一次只走一点"）
- 侧栏、表格、下拉框、滑块都按同一规则取用；页面只拿剩下的

## 5. 配置与本地化

- 段名 = 栏目名；键名英文、稳定，不随文案改
- 每个可见条目都要 `LocKey` + `LocDesc`。悬浮框按**语义段**组装，不是"第 N 行固定是什么"：

  | 段 | 来源 | 出现条件 |
  |---|---|---|
  | 是什么 | `LocDesc` 第 1 行 | 必有 |
  | 做什么 | `LocDesc` 第 2 行 | 必有 |
  | 注意 | `LocDesc` 第 3 行起（可多行） | **可选**：没有注意内容就整段省略，也不染色 |
  | 默认 | 核心按**参数类型**生成 | 开关→开/关；数值→值+范围；枚举→选项名；文本→内容/空；键位→键名/未绑定 |
  | 按键 | 核心生成 | 只有键位类条目（`KeyCode` / `KeyboardShortcut`）才出现 |

  所有"注意"行统一染**琥珀色**（不加前缀文字）。守卫检查：2–4 行、无行首标签、不写默认值行。
  新增条目时按"是什么 / 做什么 /（可选）注意"写说明，默认值与按键**不要**手写。
- 所有功能默认关闭：`bool` 条目默认 `false`；数值条目也必须有明确默认值（写进 `Bind`）。
- **点标签恢复默认值**：所有条目的标签左键 = 恢复默认（复选框/滑块/下拉/键位框恢复默认值或默认键；
  字符串编辑框 = **清空**）。统一行（`LabeledRow`/`RowSlider`/`RowCombo`）与原生条目行共用同一规则。
- **行距统一 2px**：条目之间、统一行之间、分区标题上下、以及分区最后一项到下一个分区标题，都是 2px。
- 删除/改名键用 `ConfigMigration`：`Section/Key/Move/Remove/Retire`，启动时幂等执行；删功能必须同时登记，避免孤儿键
- 设置页自检卡里有「清理孤儿键」按钮（读 BepInEx `OrphanedEntries`，原子改写并留 `.bak`）
- 枚举型取值优先用 `AcceptableValueList` / `AcceptableValueRange`，非法值写不进来

### 语言

`SRApi.T(zh, en)` 是唯一的文案入口，两个参数都会被求值——常量前缀只拼一次更省。界面语言在设置页切换，立即生效。

---

## 6. 钩子（Tick / 按键 / 场景）

| 类型 | 注册 | 说明 |
|---|---|---|
| 每帧 | `SRApi.RegisterTick(name, action)` | 默认**每帧**执行 |
| 帧尾 | `SRApi.RegisterFrameEnd(name, action)` | 光标形状 / 滚轮收口；**永不被时间片跳过** |
| 场景切换 | `SRApi.RegisterSceneHook(action)` | 换场景复位状态 |
| 关机 | `SRApi.OnShutdown(name, action)` | 注销事件监听、槽位、订阅 |
| 相机视图 | `SRApi.RegisterCameraView(Action<Camera>)` | 在 `Camera.onPreCull` 生效（渲染前、最后生效） |

### 6.1 预算调度（重要）

核心每帧给所有钩子 `1ms` 软预算，并按平滑后的压力换档：

| 压力 | 非输入钩子 | 输入钩子 |
|---|---|---|
| < 1.5× | 每帧 | 每帧 |
| > 1.5× | 30Hz | 每帧 |
| > 2.0× | 10Hz | 每帧 |

- **输入类钩子永不被降频**：命名以 `KEY` 开头、包含"键"或 `Hotkey`，或名字是 `HUD` / `聊天窗口`
- 单帧已用掉 2× 预算时，剩余非输入钩子让到下一帧（时间片）
- 换档有 20 帧防抖，压力低于 0.8× 才恢复
- 自检页「帧钩子」卡显示 `预算 / 压力 / 档位`，被降频的行频率列带 `↓`

**写钩子时仍然要自己限流**：全场景扫描、全列表遍历这类工作按时间自限（如 0.1s/0.5s 一次），不要每帧全量。

### 6.2 按键

```csharp
SRApi.RegisterKey("示例-开关", keyEntry, "toggle");   // 只登记组合键信息
SRApi.ComboKeyDown(keyEntry) / SRApi.ComboKeyHeld(keyEntry)
```

- 键位轮询放在**每帧钩子**或游戏的 `Update` 补丁里；放进被降频的钩子会手感发钝
- `Typing`（正在输入聊天/文本）为真时 `ComboKeyDown` 一律返回 false
- 判定是"修饰键按住的组合 + 主键按下"，`Input.GetKeyDown` 未命中时还有 IMGUI KeyDown 兜底（消费式，绝不会双触发）

---

## 7. 与游戏交互的规则

1. **优先走游戏自己的链路**：事件（`PiecePlacedEvent` 等）、网络消息（`Msg*`）、`Command`/`Rpc`。
   例：自动道具发 `MsgPiecePlaced`/`MsgPiecePicked`、时装用 `CallCmdSetOutfitsFromArray`、颜色用 `SetpieceColorChangeEvent`。
2. **`DestroySelf` 自带网络同步**，不要额外广播 `MsgPieceDestroyed`。
3. **只有房主改世界**：`SRApi.GateHost`；客户端只发请求。
4. **反射**：私有成员必须 `SRApi.Ref.Override("类型.成员", "用途")` 登记（改名后自检会报）；能用公开 API 就不要反射。
5. **不要每帧 `FindObjectOfType`**：缓存实例 + 换场景重置；必要时用 `SRApi.FrameObjects<T>()`（按帧缓存）。

---

## 8. 构建 / 守卫 / 部署

```powershell
cd D:\RS_TFS\CC\SR_UCH\build
.\build.ps1                  # 合并模式（默认发布形态）
.\build.ps1 -All             # 合并 + EX + Others
.\build.ps1 -Split           # 拆分模式：核心 + 每个功能一个 DLL
.\build.ps1 -Check           # 守卫：契约独立编译 / rsp 精确覆盖 / csproj 可编
.\build.ps1 -All -Deploy     # 构建并部署到游戏（游戏关着再跑）
```

```powershell
cd D:\RS_TFS\CC\SR_UCH\build
.\guards-text.ps1        # 文本守卫：编码 / 悬浮条目 3 行 / 裸 catch / 静默 catch 棘轮 / 单文件行数 / 分区字段
.\run-tests.ps1          # 单元测试（桩编译 + 断言）
.\update-file-size.ps1   # 大文件行数基线（新增 >900 行文件要登记）
.\update-silent-catch.ps1
```

**新增功能文件时**（三条都要做，漏一条就"装了等于没装"）：

1. `build/rsp/sr_uch.rsp` 加 `Features\X.cs`
2. 若支持拆分：`build/rsp/feat_X.rsp`（照抄现有 rsp 改输出名与末行）+ 两个构建脚本的 `$rspFeatures` 都加
3. `build/rsp/sr_uch.rsp` 加绝对路径（工具链用）

---

## 9. 目录与现有栏目

| 栏目 | 段名 | 页面 |
|---|---|---|
| 首页 | Home | 单页 |
| 建造 | build | 建造上限 |
| 关卡 | Level | 派对盒 / 关卡工具 |
| 联机 | Online | 单页（房间列表） |
| 玩家轨迹 | Player Tracker | 单页 |
| 方块破坏 | Destroy Blocks | 单页 |
| 会话 | Chat | 对话窗口 / 对话内容 |
| 快速调整 | Quick Adjust | 快速调整 / 挑战 |
| 布局预设 | Layout Presets | 单页（列表 + 保存/导入） |
| HUD | HUD | HUD / 显示内容 |
| 实验 | Experiments | 场景 / 关卡边界 / 其它 |
| 角色 | Character | 自动选择 / 自动时装 |
| 设置 | Settings | 通用 / 自检 |

---

## 10. 反模式（都出过真实问题）

| 反模式 | 后果 | 正确做法 |
|---|---|---|
| 下拉框传 `null` 条目 | 展开状态错位、关不掉、吃掉整页滚动 | 传真实 `ConfigEntry` |
| 按值动态增删行 | `Mismatched LayoutGroup.repaint`，整页渲染中断 | 结构固定 + 置灰 |
| 页面每帧 `PageScroll = 0` | "列表滚到底交给页面"永远不生效 | 归零交给核心换页逻辑 |
| 核心统一 10Hz 限流 | 所有按键发钝 | 每帧 + 预算降档（输入类豁免） |
| 在 `ZoomCamera.Update` 后加相机偏移 | 被 `FixedUpdate` 的 `center - position` 反推抵消 | 渲染前应用（`onPreCull`）或改 `frame.center` |
| `internal` 成员跨文件用 | 拆分构建下跨程序集不可见 | 同文件用 `private`，跨功能用 `SRApi` |
| 转发 `Guard.Try(string, Action)` 当"能知道成功" | 保存失败仍清脏标记 | 自己 `try/catch`，成功才清 |
| 功能间直接互相调用 | 拆分构建缺引用直接编译失败 | `Slots` 能力槽 |

---

## 11. 提交前检查清单

- [ ] `-All` 0 error 0 warning
- [ ] `-Split` 通过（若动了功能文件/rsp）
- [ ] `-Check` 10/10
- [ ] `guards-text.ps1` 13/13
- [ ] `run-tests.ps1` 通过
- [ ] 新增可见配置：`LocKey` + 3 行 `LocDesc`
- [ ] 新增私有反射：`Ref.Override` 登记
- [ ] 新增每帧钩子：自己限流；贵的活不要每帧全量
- [ ] 删除/改名配置键：`ConfigMigration` 登记
- [ ] README 两份同步（功能增删）
