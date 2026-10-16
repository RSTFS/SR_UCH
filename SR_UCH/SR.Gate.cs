// 门控：每帧场景上下文快照（SR.Gate）+ 统一房主判定 + 内置模式豁免 + 启动自检；Env 提供场景/身份/模式查询。
using System;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

// 分区：Gate（门控：每帧上下文快照 + 统一房主判定 + 内置模式豁免 + 启动自检）

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Env（环境与身份查询：场景 / 房主·房客 / 模式）
// 只读查询、不含豁免；“能不能用”由 SR.GateMaster / SR.GateHost / SR.GateModeAllows 判定（含豁免）。
        public static class Env {


            // 场景名按帧缓存，避免同一帧重复查询引擎侧。
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

            // 场景/网络/模式按帧同步一次后读字段：这些查询在每帧与每物理步路径上被大量调用，同帧内取值不变。
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
                    // 有服务器时看 lm.IsHost；无服务器 = 离线/本地对局 = 本机权威。
                    _isHost = _hasServer
                        ? (lm == null || lm.IsHost)
                        : (lm == null || lm.client == null || !lm.client.isConnected);
                    // 树屋/大厅：大厅控制器存在（最可靠），场景名兜底。
                    bool hasLsc = lm != null && lm.CurrentLevelSelectController != null;
                    string sc = SceneName;
                    _inTreehouse = hasLsc || (!string.IsNullOrEmpty(sc)
                        && (sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || sc.StartsWith("Lobby_")));
                    _inMatch = !_inTreehouse && lm != null && lm.CurrentGameController != null;
                } catch {
                    // 取不到一律按“非房主 / 不在树屋 / 不在对局”处理（不放大权限）。
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


            // 本机即权威（房主）；无服务器在跑（离线/单机/本地派对）时本机即权威。
            public static bool IsHost { get { EnvSync(); return _isHost; } }

            // 联机房客：有服务器且本机非房主；离线/单机/本地派对为 false。
            public static bool IsGuest { get { EnvSync(); return _hasServer && !_isHost; } }

            // 是否存在可广播的服务器（NetworkServer.active）；写 SyncVar / 广播前必须判它。
            public static bool HasServer { get { EnvSync(); return _hasServer; } }

            //客户端已连接（联机中）。
            public static bool Online { get { EnvSync(); return _online; } }


            // 一次取出模式与“是否取到”（ModeKnown）。
            public static bool TryGetMode(out GameState.GameMode mode) {
                EnvSync();
                mode = _mode;
                return _modeKnown;
            }

            public static GameState.GameMode Mode { get { EnvSync(); return _mode; } }

            // 模式是否真的取到；取不到时 GateModeAllows 一律不放行。
            public static bool ModeKnown { get { EnvSync(); return _modeKnown; } }

        }

        // 门控上下文快照：GateService 每帧刷新一次，所有限制读同一份 -> 同帧一致。
        public readonly struct GateContext
        {
            public readonly bool IsHost;        // 统一房主判定
            public readonly bool InMatch;       // 对局中
            public readonly bool OverrideModes; // 无视模式限制 IgnoreModeLimit
            public readonly bool OverrideHost;  // 无视房主限制 HostOverride（由外部模块提供）
            public readonly GameState.GameMode Mode;
            public readonly bool ModeKnown; // 模式是否真的取到
    
            public GateContext(bool isHost, bool inMatch,
                               bool overrideModes, bool overrideHost, GameState.GameMode mode, bool modeKnown)
            {
                IsHost = isHost;
                InMatch = inMatch;
                OverrideModes = overrideModes;
                OverrideHost = overrideHost;
                Mode = mode;
                ModeKnown = modeKnown;
            }
        }

// 分区：GateRequirements（内置限制：总开关 / 模式 / 房主）
// Master 永不豁免；Mode 可被 IgnoreModeLimit 豁免（凡有模式限制的功能都要接入）；Host 可被 HostOverride 豁免。

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

        // 总开关读实时值（AllEnabled）而非每帧快照：它是一键停用全部功能的安全阀，必须立刻生效。
        public static bool GateMaster { get { return AllEnabled; } }

        public static bool GateHost { get { return _gateCtx.IsHost || _gateCtx.OverrideHost; } }

        // 判本机能否执行纯本地操作（不需要广播能力）；不含 EX 的无视房主豁免，离线时 Env.IsHost 为 true。
        public static bool CanControlLocally { get { return Env.IsHost; } }

        public static bool GateModeAllows(ModeMask allowed) {
            GateContext ctx = _gateCtx;
            if (ctx.OverrideModes) return true;
            // 模式未知（取不到 GameSettings）时一律不放行（保守）。
            if (!ctx.ModeKnown) return false;
            return (MaskOf(ctx.Mode) & allowed) != 0;
        }

// 分区：Gate（门控服务：每帧上下文快照 + 统一房主判定）
// IsHost = 本机即权威（含离线/单机）；HasServer = 存在可广播服务器（NetworkServer.active），两者不可合并。


        // 本机即权威的房主权限（含离线/单机）；转发 SR.Env.IsHost。
        public static bool IsHost { get { return Env.IsHost; } }

        // 是否存在可广播的服务器（NetworkServer.active）。
        public static bool HasServer { get { return Env.HasServer; } }

        // 联机房客（有服务器且本机不是房主）。查询层：SR.Env.IsGuest。
        public static bool IsGuest { get { return Env.IsGuest; } }
        // 能力矩阵（房主/房客权限的统一门面）：[Command] 只对自己、[ClientRpc] 仅服务器广播、client.Send 任何客户端可发。
        // 功能只声明需要哪一等，判定与提示文案统一由 CanDo 给出。
        public enum Cap {
            Self,           // 只影响本机自己：任何人都能做，不需要权限
            HostOnly,       // 必须本机是权威（房主/服务器）才能广播
            AnyViaMessage,  // 靠 client.Send 广播：任何人都能做，但必须连着服务器
        }

        // 判权 + 失败时返回给玩家看的原因；true = 可执行。
        public static bool CanDo(Cap cap, out string why) {
            why = null;
            try {
                // 总开关是不可豁免的先决条件，且必须在所有能力判定之前。
                if (!GateMaster) {
                    why = T("总开关已关闭（设置页 → All Enabled）", "Master switch is off (Settings -> All Enabled)");
                    return false;
                }
                switch (cap) {
                    case Cap.Self:
                        return true;                       //只影响自己，不需要任何权限
                    case Cap.HostOnly:
                        if (Env.HasServer) return true;    //本机在跑服务器 -> 可以广播
                        why = T("仅房主可执行此操作",
                                "Host only");
                        return false;
                    case Cap.AnyViaMessage:
                        // 任何人都能发，但必须连着服务器，否则消息出不去。
                        if (Env.HasServer || Env.Online) return true;
                        why = T("此操作需要联机对局",
                                "This action requires an online session");
                        return false;
                }
            } catch (Exception __ex) { Guard.Log("能力判定", __ex); }
            why = T("当前状态不满足执行条件", "Preconditions not met");
            return false;
        }

        // 目标玩家版：targetIsSelf = 操作对象即本机自己。
        // 操作自己无需权限；操作别人只有服务器能广播。
        public static bool CanDo(Cap cap, bool targetIsSelf, out string why) {
            if (targetIsSelf && cap == Cap.AnyViaMessage) return CanDo(Cap.Self, out why);
            return CanDo(cap, out why);
        }

        // CanDo(Cap.HostOnly) 的包装；失败时自动 Notify 原因。
        public static bool CanDoHost() {
            string why;
            bool ok = CanDo(Cap.HostOnly, out why);
            if (!ok && !string.IsNullOrEmpty(why)) { try { Notify(why); } catch { } }
            return ok;
        }


        private static GateContext _gateCtx;

        // 当前帧的门控快照（未刷新时是默认值，等价于全关，保守）。
        public static GateContext Gate { get { return _gateCtx; } }

        // 每帧刷新一次（由 SR.Tick 调用）。任何限制都应读这份快照，而不是各自重新求值。
        public static void RefreshGate() {
            try {
                //场景/身份/模式一律走查询层 Env：门控只负责"叠加豁免"，不再自己重新问一遍游戏。
                bool isHost = Env.IsHost;
                bool inMatch = Env.InMatch;
                bool overrideModes = IgnoreModeLimit;
                bool overrideHost = HostOverride; //由任意模块经 RegisterHostOverride 登记；无人登记 = false
                GameState.GameMode mode;
                bool modeKnown = Env.TryGetMode(out mode);

                _gateCtx = new GateContext(isHost, inMatch,
                    overrideModes, overrideHost, mode, modeKnown);
            } catch (Exception __ex) {
                // 不做“保守全关”：运行时 getter 抛异常导致突然全关会打断正在进行的操作；仅记录日志（Guard 去重）。
                Guard.Log("刷新门控上下文", __ex);
            }
        }


// 补丁自检：经 Harmony 登记表列出本程序集实际打上的补丁目标，并标出挂在每帧方法上的补丁。

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

