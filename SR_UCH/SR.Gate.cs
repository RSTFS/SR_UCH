// 门控：每帧上下文快照 + 统一房主判定 + 内置模式豁免 + 补丁自检；Env 提供场景/身份/模式查询。
using System;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;

// 分区：Gate（门控 / 房主判定 / 模式豁免 / 补丁自检）

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Env（场景 / 身份 / 模式查询；只读、不含豁免）
        public static class Env {


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

            // 每帧同步一次后读字段：每帧与每物理步高频调用，同帧内取值不变。
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
                    // 有服务器看 lm.IsHost；无服务器（离线/本地对局）即本机权威。
                    _isHost = _hasServer
                        ? (lm == null || lm.IsHost)
                        : (lm == null || lm.client == null || !lm.client.isConnected);
                    bool hasLsc = lm != null && lm.CurrentLevelSelectController != null;
                    string sc = SceneName;
                    _inTreehouse = hasLsc || (!string.IsNullOrEmpty(sc)
                        && (sc == "TreeHouseLobby" || sc == "Treehouse" || sc == "Lobby" || sc.StartsWith("Lobby_")));
                    _inMatch = !_inTreehouse && lm != null && lm.CurrentGameController != null;
                } catch {
                    // 取不到一律按非房主 / 非树屋 / 非对局处理，不放大权限。
                    _hasServer = false; _online = false; _isHost = false;
                    _inTreehouse = false; _inMatch = false;
                }
                try { _mode = GameSettings.GetInstance().GameMode; _modeKnown = true; }
                catch { _mode = GameState.GameMode.FREEPLAY; _modeKnown = false; }
            }

            public static bool InTreehouse { get { EnvSync(); return _inTreehouse; } }

            // 对局中：不在树屋且有对局控制器。
            public static bool InMatch { get { EnvSync(); return _inMatch; } }


            // 本机即权威（含离线/单机）。
            public static bool IsHost { get { EnvSync(); return _isHost; } }

            // 联机房客：有服务器且非房主；离线/单机为 false。
            public static bool IsGuest { get { EnvSync(); return _hasServer && !_isHost; } }

            public static bool HasServer { get { EnvSync(); return _hasServer; } }

            public static bool Online { get { EnvSync(); return _online; } }


            public static bool TryGetMode(out GameState.GameMode mode) {
                EnvSync();
                mode = _mode;
                return _modeKnown;
            }

            public static GameState.GameMode Mode { get { EnvSync(); return _mode; } }

            public static bool ModeKnown { get { EnvSync(); return _modeKnown; } }

        }

        // 门控快照：每帧刷新一次，所有限制读同一份，同帧一致。
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

// 分区：GateRequirements（总开关 / 模式 / 房主限制）
// Master 永不豁免；Mode 可被 IgnoreModeLimit、Host 可被 HostOverride 豁免。

        // 模式位掩码：避免每帧 params 数组分配。
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


        // 总开关读实时值而非快照：一键停用的安全阀必须立刻生效。
        public static bool GateMaster { get { return AllEnabled; } }

        public static bool GateHost { get { return _gateCtx.IsHost || _gateCtx.OverrideHost; } }

        public static bool CanControlLocally { get { return Env.IsHost; } }

        // 功能级模式门控：查 [SrFeature].Modes（无记录或未声明 = 不限）。
        // 结果按帧缓存：hold 类键位每帧、每功能都要问一次，不缓存就是每帧多次全表遍历。
        private static readonly Dictionary<Type, int> _featureModes = new Dictionary<Type, int>();   // -1 = 不限
        private static readonly Dictionary<Type, bool> _allowedCache = new Dictionary<Type, bool>();
        private static int _allowedFrame = -1;
        private static int _featureModesVersion = -1;   // 功能表变化后模式缓存必须重建

        private static int ModesOf(Type impl) {
            // 功能表（晚注册 / 热重载后重建）版本变了就整体作废，否则会一直用旧的"-1 = 不限"。
            if (_featureModesVersion != Features.Version) {
                _featureModesVersion = Features.Version;
                _featureModes.Clear();
                _allowedCache.Clear();
            }
            int modes;
            if (_featureModes.TryGetValue(impl, out modes)) return modes;
            modes = -1;
            try {
                IList<FeatureInfo> all = Features.All;
                for (int i = 0; i < all.Count; i++) {
                    FeatureInfo fi = all[i];
                    if (fi != null && fi.Impl == impl) { modes = fi.Modes; break; }
                }
            } catch (Exception __fm) { Guard.Log("功能模式查询", __fm); }
            _featureModes[impl] = modes;
            return modes;
        }

        internal static bool FeatureAllowed(Type impl) {
            if (impl == null) return true;
            try {
                if (_allowedFrame != Time.frameCount) { _allowedFrame = Time.frameCount; _allowedCache.Clear(); }
                bool cached;
                if (_allowedCache.TryGetValue(impl, out cached)) return cached;
                int modes = ModesOf(impl);
                bool ok = modes == 0 || (modes < 0) || GateModeAllows((ModeMask)modes);
                _allowedCache[impl] = ok;
                return ok;
            } catch (Exception __fa) { Guard.Log("功能模式门控", __fa); return true; }
        }

        // 统一门控：总开关 -> 功能开关 -> 模式限制。补丁与热路径都读这一处，避免各写各的。
        internal static bool FeatureActive(Type impl, ConfigEntryBase on) {
            if (!GateMaster) return false;
            if (on != null) {
                try { if (!Equals(on.BoxedValue, true)) return false; } catch (Exception) { return false; }
            }
            return FeatureAllowed(impl);
        }

        // 供按键派发用：受限功能在错误模式下按下键，给一次节流提示（否则"按了没反应"）。
        // 节流按"功能 → 上次提示时刻"记录，用时间戳而非布尔集合，避免集合随功能数无限增长。
        private static readonly Dictionary<Type, float> _modeHintAt = new Dictionary<Type, float>();
        private const float ModeHintInterval = 2f;

        internal static bool GateFeatureForAction(Type impl, string actionName) {
            if (FeatureAllowed(impl)) return true;
            float last;
            if (!_modeHintAt.TryGetValue(impl, out last) || Time.unscaledTime - last > ModeHintInterval) {
                if (_modeHintAt.Count > 64) _modeHintAt.Clear();   // 只做节流，条目数不需要精确
                _modeHintAt[impl] = Time.unscaledTime;
                try {
                    LogWarn("[模式限制] " + (actionName ?? "?") + " 在当前模式下不可用");
                    Notify(T(actionName ?? "?", actionName ?? "?") + " " + T("在当前模式下不可用", "is unavailable in this mode"));
                } catch (Exception __mh) { Guard.Log("模式限制提示", __mh); }
            }
            return false;
        }

        public static bool GateModeAllows(ModeMask allowed) {
            GateContext ctx = _gateCtx;
            if (ctx.OverrideModes) return true;
            if (!ctx.ModeKnown) return false;
            return (MaskOf(ctx.Mode) & allowed) != 0;
        }

// 分区：Gate（门控服务：每帧快照 + 统一房主判定）
// IsHost = 本机即权威（含离线/单机）；HasServer = 有可广播服务器；两者不可合并。


        public static bool IsHost { get { return Env.IsHost; } }

        public static bool HasServer { get { return Env.HasServer; } }

        public static bool IsGuest { get { return Env.IsGuest; } }
        // 能力矩阵，由 CanDo 统一判定并给提示：[Command] 只对自己、[ClientRpc] 仅服务器广播、client.Send 任意客户端可发。
        public enum Cap {
            Self,           // 只影响本机自己：任何人都能做，不需要权限
            HostOnly,       // 必须本机是权威（房主/服务器）才能广播
            AnyViaMessage,  // 靠 client.Send 广播：任何人都能做，但必须连着服务器
        }

        // 判权；失败时经 why 返回给玩家看的原因。
        public static bool CanDo(Cap cap, out string why) {
            why = null;
            try {
                // 总开关不可豁免，须在所有能力判定之前。
                if (!GateMaster) {
                    why = T("总开关已关闭（设置页 → All Enabled）", "Master switch is off (Settings -> All Enabled)");
                    return false;
                }
                switch (cap) {
                    case Cap.Self:
                        return true;                       //只影响自己，不需要任何权限
                    case Cap.HostOnly:
                        // 仅房主按“本机是否权威”而非“是否在跑服务器”：离线单机/本地派对同样成立。
                        if (Env.IsHost) return true;
                        why = T("仅房主可执行此操作",
                                "Host only");
                        return false;
                    case Cap.AnyViaMessage:
                        if (Env.HasServer || Env.Online) return true;
                        why = T("此操作需要联机对局",
                                "This action requires an online session");
                        return false;
                }
            } catch (Exception __ex) { Guard.Log("能力判定", __ex); }
            why = T("当前状态不满足执行条件", "Preconditions not met");
            return false;
        }

        public static bool CanDo(Cap cap, bool targetIsSelf, out string why) {
            if (targetIsSelf && cap == Cap.AnyViaMessage) return CanDo(Cap.Self, out why);
            return CanDo(cap, out why);
        }

        public static bool CanDoHost() {
            string why;
            bool ok = CanDo(Cap.HostOnly, out why);
            if (!ok && !string.IsNullOrEmpty(why)) { try { Notify(why); } catch { /* 静默：非关键路径，失败可直接忽略 */ } }
            return ok;
        }


        private static GateContext _gateCtx;

        // 当前帧快照；未刷新时为默认值，等价全关（保守）。
        public static GateContext Gate { get { return _gateCtx; } }

        // 每帧由 SR.Tick 调用；所有限制读这份快照，不各自重算。
        public static void RefreshGate() {
            try {
                bool isHost = Env.IsHost;
                bool inMatch = Env.InMatch;
                bool overrideModes = IgnoreModeLimit;
                bool overrideHost = HostOverride; //由任意模块经 RegisterHostOverride 登记；无人登记 = false
                GameState.GameMode mode;
                bool modeKnown = Env.TryGetMode(out mode);

                _gateCtx = new GateContext(isHost, inMatch,
                    overrideModes, overrideHost, mode, modeKnown);
            } catch (Exception __ex) {
                // 不保守全关：突然全关会打断正在进行的操作；仅记日志（Guard 去重）。
                Guard.Log("刷新门控上下文", __ex);
            }
        }


// 补丁自检：列出本程序集实际打上的 Harmony 补丁目标，并标出每帧方法上的补丁。

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

                    SR.AuditGateTotal = total;
                    SR.AuditPerFrame = perFrame.Count;
                    SR.LogInfo("[SR.Gate] 补丁目标方法 " + total + " 个（其中每帧方法 " + perFrame.Count + " 个）");
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

