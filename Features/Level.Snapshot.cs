// Level 分片：广播快照、清场与快照加载后的收尾（阶段事件 / 放置计数 / 归属）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.IO;
using System.Xml;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    public partial class Level {
        // 安全 XML 解析：禁 DTD / 外部实体（快照 XML 可能来自其它玩家）。
        private static XmlDocument ParseXmlSafe(string xml) {
            XmlDocument doc = new XmlDocument();
            XmlReaderSettings s = new XmlReaderSettings();
            s.DtdProcessing = DtdProcessing.Prohibit;
            s.XmlResolver = null;
            using (XmlReader r = XmlReader.Create(new StringReader(xml ?? ""), s)) doc.Load(r);
            return doc;
        }

        // 广播房主快照并全员按房主视角重建；不重载场景，分数保留，仅房主可调。
        // SR 快照在根节点写 srSnapshot="1"，客户端按该标记识别（房主与房客同一判据）。


        private static bool IsSrSnapshot(System.Xml.XmlDocument doc) {
            try {
                if (doc != null && doc.DocumentElement != null
                    && doc.DocumentElement.GetAttribute("srSnapshot") == "1") return true;
            } catch { /* 静默：非关键路径，失败可直接忽略 */ }
            return false;
        }

        // 前置检查（总开关→模式→房主→服务器→对局→控制器→快照组件→重载锁）；返回 false 时已提示原因。
        private static bool TryGetSnapshotContext(string feature, out GameControl gc, out QuickSaver qs)
        {
            gc = null;
            qs = null;
            if (!SRApi.GateMaster)
            {
                SRApi.Notify(SRApi.MsgMasterOff);
                return false;
            }
            if (!IsPartyCreativeOrFreeplay())
            {
                SRApi.Notify(feature + SRApi.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
                return false;
            }
            if (!SRApi.GateHost)
            {
                SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
                return false;
            }
            // 服务器权威守卫：开了无视房主限制的房客也会被放行到这里。
            if (!NetworkServer.active)
            {
                SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
                return false;
            }
            LobbyManager lm = LobbyManager.instance;
            if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
            {
                SRApi.NotifyT("当前不在对局中", "Not in a match");
                return false;
            }
            GameControl g = lm.CurrentGameController as GameControl;
            if ((UnityEngine.Object)(object)g == (UnityEngine.Object)null)
            {
                SRApi.NotifyT("未进入有效对局", "No active match");
                return false;
            }
            QuickSaver q = g.GetComponent<QuickSaver>();
            if ((UnityEngine.Object)(object)q == (UnityEngine.Object)null)
            {
                SRApi.NotifyT("当前对局不支持该操作", "Unsupported in the current match");
                return false;
            }
            if (ReloadBlocked()) return false; // 锁残留自愈
            gc = g;
            qs = q;
            return true;
        }

        private static System.Reflection.FieldInfo _initialPiecesField;

        // 关卡自带件集合（私有字段，走 SRApi.Ref.Field）：不能列入清场目标，本路径只删不恢复。
        private static System.Collections.Generic.HashSet<Placeable> InitialLevelPieces(QuickSaver qs) {
            System.Collections.Generic.HashSet<Placeable> set = new System.Collections.Generic.HashSet<Placeable>();
            try {
                // 反射必须走 SRApi.Ref.Field（自带缓存与失败告警）；外层缓存只省一次字典查找。
                if (_initialPiecesField == null) _initialPiecesField = SRApi.Ref.Field(typeof(QuickSaver), "initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
                System.Collections.IEnumerable list = (_initialPiecesField != null && qs != null) ? _initialPiecesField.GetValue(qs) as System.Collections.IEnumerable : null;
                if (list == null) return set;
                foreach (object o in list) {
                    if (o == null) continue;
                    QuickSaver.SaveablePiece sp = o as QuickSaver.SaveablePiece;
                    if (sp != null && sp.placeable != null) set.Add(sp.placeable);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("收集关卡初始件", __ex); }
            return set;
        }
        // 清除玩家放置的方块（全员可见）：逐块网络销毁，不走 <destroyed> 快照路径。
        public static void ClearPlacedBlocks(List<Placeable> targets, string feature) {
            try {
                if (targets == null || targets.Count == 0) { SRApi.NotifyT("地图上没有玩家放置的道具", "No player-placed props on the map"); return; }
                GameControl gc; QuickSaver qs;
                if (!TryGetSnapshotContext(feature, out gc, out qs)) return;
                FallbackClearPlacedBlocks(targets); // 逐块网络销毁
            } catch (Exception __ex) { SRApi.Guard.Log("清场.逐块销毁", __ex); }
        }

        // 对外槽位：调用方经 Slots.Require 按名取用，取不到即降级。
        private const string SlotClearPlacedBlocks = "Level.ClearPlacedBlocks"; // Action<List<Placeable>,string>
        private const string SlotAttributeToHost = "DestroyBlocks.AttributeToHost"; // Action<List<Placeable>>
        // 快照加载后收尾（仅 SR 发起的快照）：补阶段事件、放置计数与着色、方块归属房主。
        [HarmonyPatch(typeof(QuickSaver), "LoadSnapshotFromXmlDocument")]
        [HarmonyPostfix]
        private static void AfterLoadSnapshot(QuickSaver __instance, System.Xml.XmlDocument doc, ref bool __result) {
            try {
                // 仅 SR 发起的快照才补发阶段事件（房主看门闩，客户端看 srSnapshot 标记）。
                if (!IsSrSnapshot(doc)) return;
                if (!__result) return; // 加载失败时不按阶段处理
                GameControl gc = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (gc == null) return;
                GameControl.GamePhase phv = gc.Phase;
                // 相位由服务器权威下发；客户端再补发会让状态机多收一次阶段开始而错位，故只在服务器补发。
                if (UnityEngine.Networking.NetworkServer.active) {
                    GameEvent.GameEventManager.SendEvent(new GameEvent.StartPhaseEvent(phv));
                    SRApi.LogInfo("[快照] 加载后补发阶段事件: " + phv);
                } else {
                    SRApi.LogInfo("[快照] 客户端：不补发阶段事件（相位由服务器权威下发）: " + phv);
                }
                // 只补发当前真实阶段；快照方块无需照抄 isKinematic/layer，真正缺的只有放置计数与一次 Tint()。
                System.Collections.Generic.HashSet<Placeable> initFix = InitialLevelPieces(__instance);
                try {
                    int nFix = 0;
                    GameState gsFix = null;
                    try { gsFix = GameState.GetInstance(); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
                    foreach (Placeable fp in Placeable.AllPlaceables) {
                        if (fp == null || fp.MarkedForDestruction) continue;
                        if (initFix.Contains(fp)) continue; // 自带件不动
                        if (!fp.Placed) continue; // 只处理已放置
                        try {
                            if (gsFix != null) gsFix.IncrementPieceCount(fp.Name);
                            fp.Tint();
                            nFix++;
                        } catch (Exception __pe) { SRApi.Guard.Log("快照方块收尾", __pe); }
                    }
                    if (nFix > 0) SRApi.LogInfo("[快照] 已为 " + nFix + " 个方块补放置计数并着色");
                } catch (Exception __fx) { SRApi.Guard.Log("快照方块收尾", __fx); }
                // 归属登记必须在加载之后：方块是加载时创建的，之前集合为空。
                try {
                    System.Collections.Generic.HashSet<Placeable> initPcs = initFix;   // 与上方共用
                    List<Placeable> built = new List<Placeable>();
                    foreach (Placeable bp in Placeable.AllPlaceables) {
                        // 必须过滤 MarkedForDestruction：上一轮残留的死方块会被误算进 built。
                        if (bp == null || bp.MarkedForDestruction || initPcs.Contains(bp)) continue;
                        built.Add(bp);
                    }
                    // 经槽位取用以避免跨功能依赖；取不到只丢归属，不影响重建。
                    Action<List<Placeable>> attributeToHost = Slots.Require<Action<List<Placeable>>>(SlotAttributeToHost);
                    if (attributeToHost != null) {
                        attributeToHost(built);
                        SRApi.LogInfo("[快照] 已把 " + built.Count + " 个快照方块归属房主(#1)");
                    } else {
                        SRApi.LogWarn("[快照] 方块破坏功能未加载，跳过 " + built.Count + " 个方块的归属登记");
                    }
                } catch (Exception __ahx) { SRApi.Guard.Log("构建归属房主", __ahx); }
            } catch (Exception __ex) { SRApi.Guard.Log("快照后补发阶段事件", __ex); }
        }

        // 兜底清场：快照路径不可用时逐块 DestroySelf（仍走原生网络销毁）。
        public static void FallbackClearPlacedBlocks(List<Placeable> targets) {
            if (targets == null) return;
            int n = 0;
            for (int i = 0; i < targets.Count; i++) {
                Placeable p = targets[i];
                if (p == null || p.MarkedForDestruction) continue;
                try { p.DestroySelf(destroyChildren: false, useSmoke: false); n++; } catch (Exception __ex) { SRApi.Guard.Log("清场.兜底单项", __ex); }
            }
            SRApi.LogInfo("[清场] 快照路径不可用，已退回逐块网络销毁: " + n + " 个");
        }

// 分区：Reload（重载保分补分 / 折扣保留 / 加载完成收尾）

    [HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
    [HarmonyPostfix]
    static void OnLoadEnd() {
        // 异常会被 Harmony 吞掉并每次记日志，故统一兜住经 Guard.Log 去重记录。
        try {
                // 重载后补分并恢复 handicap（房主写回 SyncVar 广播）。
                FillScoresIfPending();
                RestoreHandicapsIfAny();
                // 记录加载完成时刻，决定何时清 levelPortalXml。
                Level.OnReloadSceneLoaded();
        } catch (Exception __ex) { SRApi.Guard.Log("重载完成收尾", __ex); }
    }

    // 重载期间暂停“坏客户端踢出”：连续 3 秒无有效 controller 会被误判。
    }
}
