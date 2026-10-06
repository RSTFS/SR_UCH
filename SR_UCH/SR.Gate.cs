// 门控。每帧给一份"当前场景上下文"快照（SR.Gate），并统一房主判定、内置模式豁免、启动自检。
// Env 那组专门回答"现在在什么场景 / 我是房主还是房客 / 什么模式"这类问题。
using System;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

// 分区：Gate（门控：每帧上下文快照 + 统一房主判定 + 内置模式豁免 + 启动自检）
// 为什么删掉原来的限制声明抽象层（声明式 Requirement + 求值结果/作用域/判定三态那套类型）：
// 那一层是给进度解锁建的，而进度解锁功能已整体删除 -> 该层全项目零调用（grep 核实），属纯死代码。
// 注意：删掉它**不影响豁免机制**, 无视模式限制 / 无视房主限制本来就内建在
// GateModeAllows / GateHost 里（读 GateContext 的 OverrideModes / OverrideHost），不依赖抽象层。

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Env（环境与身份查询：场景 / 房主·房客 / 模式）
// 门控调用之所以乱，是因为"查状态"和"判能否用"混在一起、散落在各个文件。这里把层次定死：
//   1) SR.Env.*                                       , 只读查询：现在是什么情况。**不含任何豁免**。
//   2) SR.GateMaster / SR.GateHost / SR.GateModeAllows, 门控：这个功能现在能不能用（含豁免）。
//   3) SR.Gate（快照）                                 , 同一帧内多处读取要一致时用（避免拉取/推送混用）。
// 举例：想知道"我是不是房主" -> SR.Env.IsHost；想知道"我能不能执行仅房主的操作（可被 EX 豁免）" -> SR.GateHost。
// 功能可以自由调用 Env：不需要注册任何接点、不需要在 SR 里登记自己, 场景/身份/模式是通用信息，
// 不是某个功能的私有数据（以前"是否在树屋"只能由地图功能通过 MapHooks 委托提供，就是这类错配）。
        public static class Env {

            // 场景

            //场景名按帧缓存：一次渲染里会被问很多次（树屋判定/页面显示/门控快照都要用），
            //而 SceneManager.GetActiveScene() 每次都要回到引擎侧取，没必要重复调。
            private static string _sceneName = "";
            private static int _sceneNameFrame = -1;

            public static string SceneName {
                get {
                    if (_sceneNameFrame == Time.frameCount) return _sceneName;
                    _sceneNameFrame = Time.frameCount;
                    try { _sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
                    catch { _sceneName = ""; }
                    return _sceneName;
                }
            }

            // 按帧缓存（同步一次，之后纯字段读取）
            //为什么需要：这些查询挂在每帧 / 每物理步路径上。最典型的是输入门控的
            //FreezeLocalCharacter（Character.Update / Character.FixedUpdate 的 Prefix），
            //**每个角色、每个物理步**都要问一次 Env.InTreehouse：8 人 × 50Hz 物理步 ≈ 每秒 800 次，
            //每次都去访问 LobbyManager.instance / NetworkServer.active。
            //而 Env.Mode 走 GameSettings.GetInstance()，它内部还有 Resources.Load
            //（GameSettings.GetInstance() 取不到时会抛）,  每帧几十次调用不是小开销。
            //同帧内这些值不可能变（场景 / 网络状态 / 模式都只在帧边界切换），所以按帧算一次即可。
            //附带的好处：与 GateContext 快照一样保证"同帧一致"，各处不会读到互相矛盾的值。
            private static int _envFrame = -1;
            private static bool _inTreehouse, _inMatch, _isHost, _hasServer, _online;
            private static GameState.GameMode _mode;
            private static bool _modeKnown;

            private static void EnvSync() {
                if (_envFrame == Time.frameCount) return;
                _envFrame = Time.frameCount;
                try {
                    LobbyManager lm = LobbyManager.instance;
                    _hasServer = NetworkServer.active;
                    _online = lm != null && lm.client != null && lm.client.isConnected;
                    //本机即权威（房主）：有服务器在跑时看 lm.IsHost；没服务器 = 离线/本地对局 = 本机权威。
                    _isHost = _hasServer
                        ? (lm == null || lm.IsHost)
                        : (lm == null || lm.client == null || !lm.client.isConnected);
                    //树屋/大厅：1) 大厅控制器存在（最可靠）；2) 场景名兜底。
                    // 以前这份判定只写在地图功能里，再由它通过 MapHooks 委托喂给核心, 于是：
                    //  · 没装/没加载地图功能时，核心与其它功能根本判不了树屋；
                    //  · 每个需要判场景的功能都得自己再写一遍场景名比较（全项目曾出现 4 份相同逻辑）。
                    //  现在 SR 自带，任何功能直接 SR.Env.InTreehouse 即可。
                    bool hasLsc = lm != null && lm.CurrentLevelSelectController != null;
                    string sc = SceneName;
                    _inTreehouse = hasLsc || (!string.IsNullOrEmpty(sc)
                        && (sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || sc.StartsWith("Lobby_")));
                    _inMatch = !_inTreehouse && lm != null && lm.CurrentGameController != null;
                } catch {
                    //保守：取不到一律按"不是房主 / 不在树屋 / 不在对局"处理（不放大权限）。
                    _hasServer = false; _online = false; _isHost = false;
                    _inTreehouse = false; _inMatch = false;
                }
                try { _mode = GameSettings.GetInstance().GameMode; _modeKnown = true; }
                catch { _mode = GameState.GameMode.FREEPLAY; _modeKnown = false; }
            }

            public static bool InTreehouse { get { EnvSync(); return _inTreehouse; } }

            //对局中：不在树屋、且当前有对局控制器（回合进行中）。
            public static bool InMatch { get { EnvSync(); return _inMatch; } }

            public static bool InMainMenu { get { try { return SceneName == "MainMenu"; } catch { return false; } } }

            // 身份

            //本机即权威（房主）。含离线/单机/本地派对：没有服务器在跑时本机就是权威。
            public static bool IsHost { get { EnvSync(); return _isHost; } }

            //联机房客：有服务器在跑、且本机不是房主。离线/单机/本地派对 = false。
            //（"房客能不能用某功能"通常还要看该功能有没有同步通道，见 DestroyBlocks 的 AllowClientsOn。）
            public static bool IsGuest { get { EnvSync(); return _hasServer && !_isHost; } }

            //是否存在可广播的服务器（NetworkServer.active）。写 SyncVar / 广播前必须判它，
            //否则离线场景会去写同步变量/发包（合并进 IsHost 会误判）。
            public static bool HasServer { get { EnvSync(); return _hasServer; } }

            //客户端已连接（联机中）。
            public static bool Online { get { EnvSync(); return _online; } }

            // 模式

            //一次取出模式与"是否真的取到了"（避免调用方为了拿 ModeKnown 再取一次模式）。
            public static bool TryGetMode(out GameState.GameMode mode) {
                EnvSync();
                mode = _mode;
                return _modeKnown;
            }

            public static GameState.GameMode Mode { get { EnvSync(); return _mode; } }

            //模式是否真的取到了：取不到时门控一律不放行（保守），见 GateModeAllows。
            public static bool ModeKnown { get { EnvSync(); return _modeKnown; } }

        }

        // 门控上下文快照：GateService 每帧刷新一次，所有限制读同一份 -> 同帧一致。
        public readonly struct GateContext
        {
            public readonly bool Master;        // 总开关 AllEnabled
            public readonly bool IsHost;        // 统一房主判定
            public readonly bool InTreehouse;   // 树屋大厅
            public readonly bool InMatch;       // 对局中
            public readonly bool OverrideModes; // 无视模式限制 IgnoreModeLimit
            public readonly bool OverrideHost;  // 无视房主限制 HostOverride（由外部模块提供）
            public readonly GameState.GameMode Mode;
            public readonly bool ModeKnown;     //Mode 是否真的取到了（取不到时 GateModeAllows 一律不放行）
    
            public GateContext(bool master, bool isHost, bool inTreehouse, bool inMatch,
                               bool overrideModes, bool overrideHost, GameState.GameMode mode, bool modeKnown)
            {
                Master = master;
                IsHost = isHost;
                InTreehouse = inTreehouse;
                InMatch = inMatch;
                OverrideModes = overrideModes;
                OverrideHost = overrideHost;
                Mode = mode;
                ModeKnown = modeKnown;
            }
        }

// 分区：GateRequirements（内置限制：总开关 / 模式 / 房主）
// Master 永不豁免；Mode 可被 IgnoreModeLimit 豁免（凡有模式限制的功能都要接入）；Host 可被 HostOverride 豁免。
// 另提供零分配便捷查询 GateMaster / GateHost / GateModeAllows 供每帧路径调用。

        // 模式位掩码：避免每帧用 params 数组（每次调用都会分配）。
        [Flags]
        public enum ModeMask {
            None = 0,
            Freeplay = 1 << 0,
            Party = 1 << 1,
            Creative = 1 << 2,
            Challenge = 1 << 3
        }

        public static ModeMask MaskOf(GameState.GameMode mode) {
            switch (mode) {
                case GameState.GameMode.FREEPLAY: return ModeMask.Freeplay;
                case GameState.GameMode.PARTY: return ModeMask.Party;
                case GameState.GameMode.CREATIVE: return ModeMask.Creative;
                case GameState.GameMode.CHALLENGE: return ModeMask.Challenge;
                default: return ModeMask.None;
            }
        }

        // 零分配便捷查询（每帧路径直接用）

        // 总开关读实时值（AllEnabled）而非每帧快照：补丁前缀可能在同一帧更早执行，读快照有 1 帧延迟，
        // 而总开关是一键停用全部功能的安全阀，必须立刻生效。
        public static bool GateMaster { get { return AllEnabled; } }

        public static bool GateHost { get { return _gateCtx.IsHost || _gateCtx.OverrideHost; } }

        // R396 S2：判"本机能不能做这个**本地**操作"（不需要广播能力）。
        //  CanDo(Cap.HostOnly) 判的是"有没有服务器/广播权限"（Env.HasServer = NetworkServer.active），
        //  **离线/本地对局时恒 false**, 连本机就是权威的情况也判成"不是房主"，
        //  于是立即开始这类纯本地操作在单机树屋里永远不可用，还弹"只有房主能做"这种误导提示。
        //  -> 需要判"我是不是权威"时用这个（Env.IsHost 在离线时为 true，语义正确）。
        //  与 GateHost 的区别：GateHost 还会算上 EX 的无视房主限制豁免，这里是纯判定。
        public static bool CanControlLocally { get { return Env.IsHost; } }

        public static bool GateModeAllows(ModeMask allowed) {
            GateContext ctx = _gateCtx;
            if (ctx.OverrideModes) return true;
            //模式未知（GameSettings 取不到）时一律不放行：否则会按默认值 FREEPLAY 意外放开
            //"仅自由模式"的功能，等于"取不到就当作放行了"，与保守原则相反。
            if (!ctx.ModeKnown) return false;
            return (MaskOf(ctx.Mode) & allowed) != 0;
        }

// 分区：Gate（门控服务：每帧上下文快照 + 统一房主判定）
// 每帧刷新一份 GateContext 快照，所有限制读同一份 -> 同帧一致（避免拉取/推送混用）。
// 房主判定拆成两个语义不同的概念：IsHost（本机即权威，含离线/单机）与 HasServer（NetworkServer.active，
// 即"有服务器可广播"）；合并会让离线场景误去写 SyncVar/广播。

        // 房主判定

        // 是否具备本机即权威的房主权限（含离线/无连接；单机也算）。
        // 实现已收进 SR.Env.IsHost（查询层）：这里只做转发，避免同一套判定出现两份实现。
        public static bool IsHost { get { return Env.IsHost; } }

        // 是否存在可广播的服务器（NetworkServer.active）。
        public static bool HasServer { get { return Env.HasServer; } }

        // 联机房客（有服务器且本机不是房主）。查询层：SR.Env.IsGuest。
        public static bool IsGuest { get { return Env.IsGuest; } }
        // 能力矩阵（房主/房客权限的统一门面）
        // 联机里"谁能做什么"来自三类机制：
        //   · [Command] / CallCmdXxx    客户端 -> 服务器，只能对**自己有权限**的对象发 => "只对自己生效"
        //   · [ClientRpc] / CallRpcXxx  服务器 -> 全体，只有服务器能发        => "房主能操作所有人"
        //   · client.Send(msgType, msg) **任何客户端**都能发，服务端无条件中转（反编译 39123）
        //       => "房客也能生效"（本质是伪造原生消息，跟游戏自身设计一致，不算漏洞利用）
        // 功能只声明自己要哪一等，判定和提示文案统一由这里给 —— 免得各处措辞不一，
        // 也免得"房客点了没反应，却什么都不提示"。
        public enum Cap {
            Self,           // 只影响本机自己：任何人都能做，不需要权限
            HostOnly,       // 必须本机是权威（房主/服务器）才能广播
            AnyViaMessage,  // 靠 client.Send 广播：任何人都能做，但必须连着服务器
        }

        // 判权 + 失败时给一句给玩家看的原因。返回 true = 可执行。
        public static bool CanDo(Cap cap, out string why) {
            why = null;
            try {
                switch (cap) {
                    case Cap.Self:
                        return true;                       //只影响自己，不需要任何权限
                    case Cap.HostOnly:
                        if (Env.HasServer) return true;    //本机在跑服务器 -> 可以广播
                        why = T("仅房主可执行此操作",
                                "Host only");
                        return false;
                    case Cap.AnyViaMessage:
                        //任何人都能发，但**必须连着服务器**，否则消息根本出不去
                        if (Env.HasServer || Env.Online) return true;
                        why = T("此操作需要联机对局",
                                "This action requires an online session");
                        return false;
                }
            } catch (Exception __ex) { Guard.Log("能力判定", __ex); }
            why = T("当前状态不满足执行条件", "Preconditions not met");
            return false;
        }

        // 目标玩家版：targetIsSelf = 要操作的就是本机自己。
        // 语义：目标是自己 -> 对自己的操作本来就不需要权限（除非明确要求 HostOnly）；
        //       目标是别人 -> 只有服务器能广播给别人。
        public static bool CanDo(Cap cap, bool targetIsSelf, out string why) {
            if (targetIsSelf && cap == Cap.AnyViaMessage) return CanDo(Cap.Self, out why);
            return CanDo(cap, out why);
        }

        // 便捷包装：需要"只有房主能做"时用（等价于 CanDo(Cap.HostOnly, out why)）。
        // 失败时自动 Notify 一句，调用方直接 if (!CanDoHost()) return; 即可。
        public static bool CanDoHost() {
            string why;
            bool ok = CanDo(Cap.HostOnly, out why);
            if (!ok && !string.IsNullOrEmpty(why)) { try { Notify(why); } catch { } }
            return ok;
        }

        // 每帧上下文快照

        private static GateContext _gateCtx;

        // 当前帧的门控快照（未刷新时是默认值，等价于全关，保守）。
        public static GateContext Gate { get { return _gateCtx; } }

        // 每帧刷新一次（由 SR.Tick 调用）。任何限制都应读这份快照，而不是各自重新求值。
        public static void RefreshGate() {
            try {
                bool master = AllEnabled;
                //场景/身份/模式一律走查询层 Env：门控只负责"叠加豁免"，不再自己重新问一遍游戏。
                bool isHost = Env.IsHost;
                bool treehouse = Env.InTreehouse;
                bool inMatch = Env.InMatch;
                //（原有一行 bool uiCapturing = UiOpen && BlockInput; 声明后从未使用、也没塞进 GateContext，
                //  属死代码，已删。注意它**不会**产生编译警告, CS0219 只在赋常量时报，
                //  赋属性表达式（可能有副作用）编译器不报，所以这行只能靠人看。）
                bool overrideModes = IgnoreModeLimit;
                bool overrideHost = HostOverride; //由任意模块经 RegisterHostOverride 登记；无人登记 = false
                GameState.GameMode mode;
                bool modeKnown = Env.TryGetMode(out mode);

                _gateCtx = new GateContext(master, isHost, treehouse, inMatch,
                    overrideModes, overrideHost, mode, modeKnown);
            } catch (Exception __ex) {
                //不改成"保守全关"：运行中因某个 getter 抛异常而突然全关，会让正在飞行的玩家
                //瞬间掉下来，也是一种不安全；且这些 getter 几乎不抛异常，下一帧即恢复。
                //这里只让它可观测（Guard 有 5 秒去重，不会刷屏）。
                Guard.Log("刷新门控上下文", __ex);
            }
        }

        // 求值入口

// 补丁自检基线：经 Harmony 运行时补丁登记表（GetAllPatchedMethods / GetPatchInfo）
// 列出本程序集实际打上的补丁目标，并标出挂在每帧方法（Update/FixedUpdate/LateUpdate/OnPreCull 等）上的补丁。

        public static class GateAudit {
            private static readonly HashSet<string> PerFrameMethods = new HashSet<string> {
                "Update", "FixedUpdate", "LateUpdate", "OnPreCull", "OnPreRender", "OnPostRender", "OnGUI"
            };

            private static bool _ran;

            public static void Run() {
                if (_ran) return; //只跑一次
                _ran = true;
                try {
                    Assembly asm = typeof(SR).Assembly;
                    int total = 0;
                    List<string> perFrame = new List<string>();

                    foreach (MethodBase mb in Harmony.GetAllPatchedMethods()) {
                        Patches info;
                        try { info = Harmony.GetPatchInfo(mb); } catch { continue; }
                        if (info == null) continue;
                        if (!HasOurs(info.Prefixes, asm) && !HasOurs(info.Postfixes, asm) && !HasOurs(info.Transpilers, asm))
                            continue; //不是本模组打的补丁

                        total++;
                        if (mb != null && PerFrameMethods.Contains(mb.Name)) {
                            string owner = mb.DeclaringType != null ? mb.DeclaringType.Name : "?";
                            perFrame.Add(owner + "." + mb.Name);
                        }
                    }

                    SR.LogInfo("[GateAudit] SR_UCH 补丁目标方法 " + total + " 个；其中挂在每帧方法上的 " + perFrame.Count + " 个：");
                    for (int i = 0; i < perFrame.Count; i++) {
                        SR.LogInfo("[GateAudit]   " + perFrame[i]);
                    }
                } catch (Exception ex) {
                    try { SR.LogWarn("[GateAudit] 扫描失败: " + ex.Message); } catch (Exception __ex) { Guard.Log("Run", __ex); }
                }
            }

            private static bool HasOurs(IEnumerable<Patch> patches, Assembly asm) {
                if (patches == null) return false;
                foreach (Patch p in patches) {
                    try {
                        if (p == null || p.PatchMethod == null || p.PatchMethod.DeclaringType == null) continue;
                        if (p.PatchMethod.DeclaringType.Assembly == asm) return true;
                    } catch (Exception __ex) { Guard.Log("HasOurs", __ex); }
                }
                return false;
            }
        }

	}
}

