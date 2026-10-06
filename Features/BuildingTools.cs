// 建造增强。自由放置（网格覆盖 + 吸附到 0.01 单位）和建造上限放宽。
// BuildUnlimiter 也并在本文件里。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace SR_UCH.Tweaks {
    public class BuildingTools : ITweak {
        private static IFeatureHost _mp;   //只用来读 Config（契约类型）
        private static bool _collisionOverride;
        private static ConfigEntry<bool> _collisionEntry;
        private static ConfigEntry<KeyCode> _toggleCollisionKey;
        //自由放置（网格覆盖）：方块不再吸附 1 单位网格，改为吸附 0.01 单位，可自由微调摆放
        private static bool _gridOverride;
        private static ConfigEntry<bool> _gridEntry;
        private static ConfigEntry<KeyCode> _toggleGridKey;
        //runtime toggles (also controlled by the in-game manager)
        public static bool Enabled = true;
        public static bool GridOverride { get { return _gridOverride; } set { _gridOverride = value; } }

        private static void SelfReg() {
//本文件反射的游戏私有成员（游戏更新改名时：启动自检会在日志里告警；改名需等新版模组适配）
            SR.RefOverride("PiecePlacementCursor.SetPiece", "自由放置（改写放置结果）");
            SR.RefOverride("PiecePlacementCursor.FixedUpdate", "自由放置（补丁目标）");
            SR.RefOverride("PiecePlacementCursor.heldPositionOffset", "自由放置（关闭网格吸附）");
            SR.RowCompanions.Add(BuilderCompanion); //通用条目行扩展点：覆盖与它的快捷键同行显示
            SR.RowFilters.Add(BuilderRowVisible);   //两个开关键位不单独成行
            SR.RegisterPage("build", RenderPage); //本功能页自绘（含建造上限小分区）
            SR.LocSec("build", "建造", "Build");
            SR.Nav("build", 50); //侧栏栏目顺序 50
            SR.LocKey("build", "Collision Override", "无视碰撞", null);
            SR.LocDesc("build", "Collision Override", "关掉放置规则, 方块可以互相重叠, 也可以悬空放着, 想放哪放哪.", "Ignore collision: pieces ignore placement rules and can go anywhere (overlap/air/crossing)");
            SR.LocKey("build", "Collision Toggle Key", "无视碰撞开关", null);
            SR.LocDesc("build", "Collision Toggle Key", "游戏里按这个键临时开关无视碰撞, 默认 F1.", "Toggle ignore-collision in-game (default F1)");
            SR.LocKey("build", "Grid Override", "自由放置", null);
            SR.LocDesc("build", "Grid Override", "把吸附精度从 1 格调到 0.01 格, 放歪一点也能摆. 只改放置取整, 不改玩法和物理, 只在建造阶段生效. 关掉立刻回到整格.", "Free placement: pieces no longer snap to the 1-unit grid - they snap to 0.01 units, so you can fine-tune placement.\nOnly changes placement rounding precision, not gameplay/physics; build phase only.\nNo key needed while on; turning it off restores the standard grid snap.");
            SR.LocKey("build", "Grid Toggle Key", "自由放置开关", null);
            SR.LocDesc("build", "Grid Toggle Key", "游戏里按这个键临时开关自由放置, 默认 F2.", "Toggle free placement in-game (default F2)");
        }

        //当前生效的满度上限（只读框显示用）：直接读游戏字段。
        //不借建造上限功能的取值接口（那会造成功能间横向依赖），直接读游戏字段本身
        //（取值语义完全相同：读不到时回退游戏原版上限 500）。
        private static int CurrentCapShown() {
            try { return GameSettings.GetInstance().LevelFullnessScoreLimit; } catch { return 500; }
        }

        //覆盖条目的同行同伴：它的开关键位（通用条目行按注册的 key 再渲染一个控件）
        private static string BuilderCompanion(ConfigEntryBase e) {
            if (e.Definition.Section != "build") return null;
            if (e.Definition.Key == "Collision Override") return "Collision Toggle Key";
            if (e.Definition.Key == "Grid Override") return "Grid Toggle Key";
            return null;
        }

        //两个开关键位并进覆盖行显示，不单独成行
        private static bool BuilderRowVisible(ConfigEntryBase e) {
            if (e.Definition.Section != "build") return true;
            return e.Definition.Key != "Collision Toggle Key" && e.Definition.Key != "Grid Toggle Key";
        }

        //本功能的自绘页：先画建造上限小分区（分隔标题 + 当前生效值），再逐行渲染本段条目
        //分区判据（键名来自 Initialize 里的 Config.Bind；Toggle Key 那两条已被可见性过滤掉）
        private static bool IsEnhanceKey(string k) { return k == "Collision Override" || k == "Grid Override"; }
        private static bool IsLimitKey(string k) { return k == "Lift Build Cap" || k == "Build Cap Value"; }
        private static void RenderPage() {
            float col = Mathf.Max(SR.Ctl.Sc(180), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(240));
            List<ConfigEntryBase> entries = SR.Ctl.SectionEntries("build");
            bool any = false;

            // #1 分区**按条目键显式划分**，不依赖遍历顺序。
            //为什么必须这样：SectionEntries 是按键名排序的，Build Cap Value(B) 排在
            //Collision Override(C) 前面, 原来"循环里遇到第一条上限条目才插标题"会让
            //建造上限正好插在建造增强标题下方，无视碰撞/自由放置反而落到建造上限里面。

            // 分区一：建造增强（无视碰撞 / 自由放置）
            GUILayout.Label(new GUIContent("- " + SR.T("建造", "Build") + " -",
                SR.T("建造增强：让方块无视放置规则、按更细的网格自由微调摆放。",
                     "Build: place pieces ignoring the placement rules and snap on a finer grid.")),
                SR.Ctl.SecHeader);
            GUILayout.Space(SR.Ctl.Sc(2));
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (!IsEnhanceKey(e.Definition.Key)) continue;
                any = true;
                SR.Ctl.DrawEntryRow(e, col);
            }

            // 分区二：建造上限（解除上限 + 上限值 + 当前生效值）
            GUILayout.Space(SR.Ctl.Sc(6));
            GUILayout.Label(new GUIContent("- " + SR.T("建造上限", "Build Limit") + " -",
                SR.T("建造上限（BuildUnlimiter）：解除树屋保存/发布界面的关卡满度限制，超满的关卡也能正常发布/上传。",
                     "Build Limit (BuildUnlimiter): lifts the treehouse save/publish fullness cap so over-full levels can be published.")),
                SR.Ctl.SecHeader);
            GUILayout.Space(SR.Ctl.Sc(2));
            // R384：这一行是**手搓**的（不是通用条目行），原来用 FlexibleSpace 把只读框推到行最右，
            //  与下面解除上限 / 上限值两行的编辑框不在同一列。现在按通用条目行的排版走：
            //  标签占名称列宽度 + 间隔 6 + 主控件列（与下面两行的编辑框左边缘对齐）。
            GUILayout.BeginHorizontal();
            // R385：这里用的是 GUILayout.Label（**不会**像 RestoreLabel 那样自动收缩到文字宽度），
            //  所以必须自己按文字实测，否则当前上限右侧会空一大块、只读框被推离标签。
            float lw = SR.Ctl.Label.CalcSize(new GUIContent(SR.T("当前上限", "Current limit"))).x + SR.Ctl.Sc(12);
            GUILayout.Label(new GUIContent(SR.T("当前上限", "Current limit"),
                SR.T("当前生效的满度上限；游戏原版为 500", "The current effective fullness cap; vanilla is 500")),
                SR.Ctl.Label, GUILayout.Width(Mathf.Max(SR.Ctl.Sc(60), lw)), GUILayout.Height(SR.Ctl.Sc(26)));
            GUILayout.Space(SR.Ctl.Sc(6));
            bool oldEn = GUI.enabled;
            try {
            GUI.enabled = false;
            //SR.T(zh, en) 的两个参数都会被求值：先取到局部变量再拼接，避免同帧算两次。
            //上限值直接读游戏字段（不借别的功能的接口，避免横向依赖）
            int curLimit = CurrentCapShown();
            //前缀是常量、只有末尾数字在变 -> 把数字挪到 T() 外面接，字符串只拼一次
            GUILayout.TextField(SR.T("当前上限：", "Current: ") + curLimit,
                SR.Ctl.SearchBox, GUILayout.Width(SR.Ctl.Sc(170)), GUILayout.Height(SR.Ctl.Sc(26)));
            } finally { GUI.enabled = oldEn; }
            GUILayout.EndHorizontal();
            GUILayout.Space(SR.Ctl.Sc(2));
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (!IsLimitKey(e.Definition.Key)) continue;
                any = true;
                SR.Ctl.DrawEntryRow(e, col);
            }

            // 其余条目（将来新增的，归到建造增强之下，避免"看不见的条目"）
            for (int i = 0; i < entries.Count; i++) {
                ConfigEntryBase e = entries[i];
                if (IsEnhanceKey(e.Definition.Key) || IsLimitKey(e.Definition.Key)) continue;
                any = true;
                SR.Ctl.DrawEntryRow(e, col);
            }
            if (!any) GUILayout.Label(SR.T("（无匹配条目）", "(no matching entries)"), SR.Ctl.Label);
        }

        public void Initialize(IFeatureHost plugin) {
            SelfReg();
            _mp = plugin;
            _collisionEntry = _mp.Config.Bind(
                "build",
                "Collision Override",
                false,
                "Whether pieces ignore placement rules (can be placed anywhere) at startup");
            _collisionOverride = _collisionEntry.Value;
            _collisionEntry.SettingChanged += (s, e) => _collisionOverride = _collisionEntry.Value;
            _toggleCollisionKey = _mp.Config.Bind(
                "build",
                "Collision Toggle Key",
                KeyCode.F1,
                "Key to toggle the collision override in-game");
            SR.RegisterKey("建造增强-碰撞开关", _toggleCollisionKey, "toggle");
            _gridEntry = _mp.Config.Bind(
                "build",
                "Grid Override",
                false,
                "Whether pieces snap to a fine 0.01 grid instead of the 1-unit build grid (free placement)");
            _gridOverride = _gridEntry.Value;
            _gridEntry.SettingChanged += (s, e) => _gridOverride = _gridEntry.Value;
            _toggleGridKey = _mp.Config.Bind(
                "build",
                "Grid Toggle Key",
                KeyCode.F2,
                "Key to toggle free placement (grid override) in-game");
            SR.RegisterKey("建造增强-自由放置开关", _toggleGridKey, "toggle");

            //分开注册：嵌套类的 CreateAndPatchAll 不递归，需显式注册，单个失败不影响其他。
            try { Harmony.CreateAndPatchAll(typeof(BuildingTools)); } catch (Exception e) { SR.LogWarn("BuildingTools 补丁失败: " + e.Message); }
            try { Harmony.CreateAndPatchAll(typeof(FreePlacementPatch)); } catch (Exception e) { SR.LogError("自由放置补丁注册失败: " + e.Message); }
        }

        [HarmonyPatch(typeof(GameState), "Update")]
        [HarmonyPrefix]
        static void ToggleKeys() {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            if (SR.ComboKeyDown(_toggleCollisionKey)) { _collisionEntry.Value = !_collisionEntry.Value; Notify(SR.T("无视碰撞", "Collision Override"), _collisionEntry.Value); }
            if (SR.ComboKeyDown(_toggleGridKey)) { _gridEntry.Value = !_gridEntry.Value; Notify(SR.T("自由放置", "Free Placement"), _gridEntry.Value); }
        }

        private static void Notify(string name, bool state) {
            SR.Notify(name + (state ? SR.T("：开", ": ON") : SR.T("：关", ": OFF")));   // **走核心通道**：原来只弹提示不写日志（快捷键切换查不到）
        }

        [HarmonyPatch(typeof(PiecePlacementCursor), "ReceiveEvent")]
        [HarmonyPostfix]
        static void OnPieceInput(PiecePlacementCursor __instance) {
            if (!SR.GateMaster) return;
            if (!Enabled) return;
            if (SR.UiOpen && SR.BlockInput) return; 
            if (__instance.Piece == null) return;
            __instance.Piece.IgnorePlacementRules = _collisionOverride;
        }

        //自由放置：游戏把 Piece 放到吸附后的 gridPosition 后，用 Postfix 改成未取整的光标位置，
        //实现自由微调摆放（Postfix 比 transpiler 可靠）。
        [HarmonyPatch]
        static class FreePlacementPatch {
            private static FieldInfo _heldOffsetField;
            static IEnumerable<MethodBase> TargetMethods() {
                //不能直接 yield return RefMethod(...)：反射失效时它返回 null，
                //Harmony 遍历到 null 会抛异常 -> 整个自由放置功能一起丢（连另一个目标也用不了）。
                //逐个判空后 yield，单个目标失效只丢对应的那个，与"分开注册、单个失败不影响其他"一致。
                MethodInfo m = SR.RefMethod(typeof(PiecePlacementCursor), "SetPiece", "自由放置（改写放置结果）");
                if (m != null) yield return m;
                m = SR.RefMethod(typeof(PiecePlacementCursor), "FixedUpdate", "自由放置（补丁目标）");
                if (m != null) yield return m;
            }
            static void Postfix(PiecePlacementCursor __instance) {
                try {
                    if (!SR.GateMaster) return; //总开关关闭时自由放置同样失效（与本文件另两个补丁一致）
                    if (!GridOverride) return;
                    if (__instance == null || __instance.Piece == null) return;
                    // **R396 S5**：复刻游戏 PiecePlacementCursor.FixedUpdate（反编译 71952-71958）的守卫。
                    //  这些状态下**游戏自己刻意不动位置**，原来我们照写不误 ->
                    //  等待服务器确认放置期间方块被拽到未取整的原始位置；若这次放置随后被服务器否决，
                    //  方块已经出现在非法位置（联机房客最易撞上，表现为"方块偶尔自己跳一下"）。
                    // disabled 是 protected（编译不可直接访问），走 SafeBool
                    if (SafeBool(__instance, "disabled")) return;
                    if (__instance.WaitingForPlaceMessageResponse) return;
                    if (SafeBool(__instance, "waitingForFixedUpdate")) return;
                    if (SafeBool(__instance, "tryingToCancel")) return;
                    if (SafeBool(__instance, "placementPhysicsLock")) return;
                    //自由位置 = 光标位置 + 手持偏移 + 变体偏移（不取整）
                    Vector2 offset = __instance.Piece.GetTransformedPlacementOffset();
                    Vector3 cp = __instance.transform.position;
                    if (_heldOffsetField == null)
                        _heldOffsetField = SR.RefField(typeof(PiecePlacementCursor), "heldPositionOffset", "自由放置（关闭网格吸附）");
                    Vector3 held = Vector3.zero;
                    if (_heldOffsetField != null) {
                        try { held = (Vector3)_heldOffsetField.GetValue(__instance); } catch (Exception __ex) { SR.Guard.Log("BuildingTools.Field", __ex); }
                    }
                    Vector2 freePos = new Vector2(cp.x + held.x + offset.x, cp.y + held.y + offset.y);
                    Vector3 cur = __instance.Piece.transform.position;
                    //受约束件只能沿一个轴移动：与原版 SetPiece/FixedUpdate 的分支一致，保留另一轴当前值
                    if (__instance.Piece.ConstrainX) freePos = new Vector2(cur.x, freePos.y);
                    else if (__instance.Piece.ConstrainY) freePos = new Vector2(freePos.x, cur.y);
                    __instance.Piece.transform.position = new Vector3(freePos.x, freePos.y, cur.z);
                } catch (Exception __ex) { SR.Guard.Log("BuildingTools.Vector3", __ex); }
            }

            // **R396 S5**：读一个 private/protected bool 字段，取不到就当 false（不抛）。
            //  这些字段游戏改名时只是少一条守卫，不会让功能崩。
            private static bool SafeBool(object target, string field) {
                try {
                    System.Reflection.FieldInfo fi = SR.RefField(target.GetType(), field, "建造增强：自由放置守卫");
                    if (fi == null) return false;
                    object v = fi.GetValue(target);
                    return v is bool && (bool)v;
                } catch { return false; }
            }
        }
    }
}

// 以下由 BuildUnlimiter.cs 并入（UI 在 BuildingTools.cs -> 文件跟着 UI 走）
namespace SR_UCH.Tweaks {
    //建造上限：放宽 GameSettings.LevelFullnessScoreLimit（移植自 Osqat/UCH-BuildUnlimiter）
    public class BuildUnlimiter : ITweak {
        public const int VanillaLimit = 500;
        private const int DefaultLimit = 1000000;
        private const int MinLimit = 500;
        private const int MaxLimit = 10000000;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _limitEntry;

        public static bool Enabled { get { return _enabled != null && _enabled.Value; } }

        public static int LimitValue {
            get { return _limitEntry != null ? Mathf.Clamp(_limitEntry.Value, MinLimit, MaxLimit) : DefaultLimit; }
        }

        private static void SelfReg() {
            SR.LocKey("build", "Lift Build Cap", "解除建造上限", null);
            SR.LocDesc("build", "Lift Build Cap", "把关卡满度上限从原版 500 提到下面那个数, 默认 1000000. 装太满的关卡也能发出去. 不用按键, 关掉就回到 500.", "Lift the level fullness cap: the treehouse save/publish cap rises from 500 to the Limit value (default 1000000), so over-full levels can be published.\nWorks from anywhere; turning it off restores 500.");
            SR.LocKey("build", "Build Cap Value", "上限数值", "Limit Value");
            SR.LocDesc("build", "Build Cap Value", "满度上限具体填多少, 500 到 10000000, 原版是 500. 改完立刻生效, 越大能放的方块越多.", "Fullness cap value (500 - 10000000; vanilla is 500).\nTakes effect immediately; higher allows more blocks before publish is blocked.");
        }

        public void Initialize(IFeatureHost plugin) {
            SelfReg();
            _enabled = plugin.Config.Bind("build", "Lift Build Cap", false,
                "解除关卡满度限制：开启后树屋保存/发布界面的满度上限从原版 500 提高到上限数值（默认 1000000），超满的关卡也能正常发布/上传；关闭立即恢复原版。");
            _limitEntry = plugin.Config.Bind("build", "Build Cap Value", DefaultLimit, new ConfigDescription(
                "满度上限数值（500 - 10000000；游戏原版为 500）", new AcceptableValueRange<int>(MinLimit, MaxLimit)));
            if (_limitEntry.Value < MinLimit || _limitEntry.Value > MaxLimit) {
                _limitEntry.Value = DefaultLimit;
            }
            _enabled.SettingChanged += (s, e) => Apply();
            _limitEntry.SettingChanged += (s, e) => Apply();
            Apply(); //启动时归位：关闭则保持原版 500
            //启动时 GameSettings 可能还没加载完（BepInEx 插件的 Awake 早于游戏首个场景），
            //GetInstance() 返回 null -> Apply 静默失败，而之后没有任何重试时机 ->
            //已勾选的解除建造上限要等用户手动切换一次开关才生效（建造页只读框显示 500，与勾选状态矛盾）。
            //挂场景切换重试：与 DestroyBlocks / Respawn / ExModule 等其它 tweak 的做法一致。
            SceneManager.activeSceneChanged += (a, b) => Apply();
            // R396 S6：Apply() 的求值依赖 SR.GateMaster，但**没有任何东西在总开关变化时调用它**
            //  （SR.AllEnabled 的 SettingChanged 只更新它自己，SR.Core.cs:857，不通知任何功能）
            //  -> 关掉总开关后满度上限仍是 1000000 而非原版 500，要等下次换场景才恢复。
            //  总开关是"一键停用全部功能"的最后手段，这里是它漏掉的唯一一项。
            //  模式照抄 Experiments.LevelBoundsUnlock.Reeval（759-760）。
            SR.RegisterDismissHook(delegate { if (!SR.GateMaster) Apply(); });
            SR.RegisterSceneHook(delegate { if (!SR.GateMaster) Apply(); });
        }

        private static void Apply() {
            try {
                GameSettings gs = GameSettings.GetInstance();
                if (gs == null) return; //场景还没加载完：交给下次场景切换重试（见 Initialize 里的订阅）
                gs.LevelFullnessScoreLimit = (SR.GateMaster && Enabled) ? LimitValue : VanillaLimit;
                //注：这里只写游戏的字段、不改任何配置项，原来还调了一次 Config.Save(), 纯属无谓的磁盘写
                //（挂了场景切换重试后会变成每次换场景都写一次），故去掉；配置落盘统一由 SR.FlushConfig 节流。
            } catch (Exception __ex) {
                SR.Guard.Log("应用建造上限", __ex);
            }
        }
    }
}
