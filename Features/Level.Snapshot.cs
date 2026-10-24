// Level 的 partial 分片（只做代码组织，无逻辑改动）
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
		// 安全 XML 解析：禁 DTD / 外部实体（快照 XML 可能来自其它玩家分享的关卡）。
		private static XmlDocument ParseXmlSafe(string xml) {
			XmlDocument doc = new XmlDocument();
			XmlReaderSettings s = new XmlReaderSettings();
			s.DtdProcessing = DtdProcessing.Prohibit;
			s.XmlResolver = null;
			using (XmlReader r = XmlReader.Create(new StringReader(xml ?? ""), s)) doc.Load(r);
			return doc;
		}

		// 生成房主当前快照并经 CompressAndSendSnapshotBytes 广播，全员按房主视角重建；不重载场景，分数保留；仅房主可调用。
		// SR 自己发起的快照加载标记，与 AfterLoadSnapshot 的门闩配对。
		// 客户端进程拿不到该静态字段，故房主发送时在 XML 根节点写 srSnapshot="1"，判据为门闩 || XML 标记。
		private static bool _srSnapshotLoad;

		// 给快照 XML 根节点加 srSnapshot="1"（幂等；解析失败原样返回）
		internal static string MarkSnapshotXml(string xml) {
			try {
				if (string.IsNullOrEmpty(xml)) return xml;
				System.Xml.XmlDocument d = new System.Xml.XmlDocument();
				d = ParseXmlSafe(xml);
				if (d.DocumentElement == null) return xml;
				if (d.DocumentElement.GetAttribute("srSnapshot") == "1") return xml;
				d.DocumentElement.SetAttribute("srSnapshot", "1");
				return d.OuterXml;
			} catch (Exception __ex) {
				SRApi.LogWarn("[快照] 打标记失败，按原样发送: " + __ex.Message);
				return xml;
			}
		}

		// 该快照是否由 SR 发起：房主看静态门闩，客户端看 XML 标记。
		private static bool IsSrSnapshot(System.Xml.XmlDocument doc) {
			if (_srSnapshotLoad) return true;
			try {
				if (doc != null && doc.DocumentElement != null
					&& doc.DocumentElement.GetAttribute("srSnapshot") == "1") return true;
			} catch { }
			return false;
		}

		// 把快照 XML 广播给全员：房主压缩 -> RPC -> 各端 LoadSnapshotFromXmlDocument 重建（广播快照与按 XML 构建共用）。
		private static void SendSnapshotXml(GameControl gc, string xml, string feature)
		{
			// 打 srSnapshot 标记：客户端据此只在 SR 的快照上补发阶段事件，不碰游戏自己的开局/重载路径。
			xml = MarkSnapshotXml(xml);
			if (xml.Length > 1500000) SRApi.Notify(SRApi.T("快照较大（" + (xml.Length / 1000) + " KB），应用时可能卡顿", "Large snapshot (" + (xml.Length / 1000) + " KB); may hitch"));
			System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch(); _sw.Start();
			byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
			_sw.Stop();
			SRApi.LogInfo("[快照] 序列化耗时 " + _sw.ElapsedMilliseconds + "ms");
			SRApi.Notify(feature + SRApi.T("：广播快照（", ": broadcasting (") + xmlBytes.Length + SRApi.T(" 字节），全员按房主状态重建", " bytes), everyone rebuilds from the host's state"));
			gc.CompressAndSendSnapshotBytes(xmlBytes, delegate {
				SRApi.Notify(feature + SRApi.T("：方块已重建，分数保留", ": blocks rebuilt, scores kept"));
			});
			// CompressAndSendSnapshotBytes 走 ClientRpc，不在房主本地执行，故这里补一次本地应用。
			try {
				QuickSaver selfQs = gc.GetComponent<QuickSaver>();
				if (selfQs != null) {
					System.Xml.XmlDocument selfDoc = new System.Xml.XmlDocument();
					selfDoc = ParseXmlSafe(xml);
					// 按原生流程先 QuickClear(true)：原生清场并恢复关卡自带件，缺这一步可移动/可变化道具要等下次建造阶段才会动。
					try { selfQs.QuickClear(true); } catch (Exception __qcx) { SRApi.Guard.Log("清场.QuickClear", __qcx); }
					// 门闩必须用 try/finally 复位，否则泄漏后游戏自己的开局也会被补发阶段事件。
					_srSnapshotLoad = true;
					try {
						if (!selfQs.LoadSnapshotFromXmlDocument(selfDoc))
							SRApi.Notify(SRApi.T("本地应用快照失败", "Failed to apply the snapshot locally"));
					} finally { _srSnapshotLoad = false; }
				}
			} catch (Exception __ex) { SRApi.Guard.Log("本地应用快照", __ex); }
		}

		// 广播快照的前置检查（总开关 -> 模式 -> 房主 -> 对局 -> 控制器 -> 快照组件 -> 重载锁）；返回 false 时已提示原因。
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
			// 服务器权威守卫：房客开了无视房主限制也会被放行到这里。
			if (!NetworkServer.active)
			{
				SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
				return false;
			}
			LobbyManager lm = LobbyManager.instance;
			if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
			{
				SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
				return false;
			}
			GameControl g = lm.CurrentGameController as GameControl;
			if ((UnityEngine.Object)(object)g == (UnityEngine.Object)null)
			{
				SRApi.Notify(SRApi.T("未进入有效对局", "No active match"));
				return false;
			}
			QuickSaver q = g.GetComponent<QuickSaver>();
			if ((UnityEngine.Object)(object)q == (UnityEngine.Object)null)
			{
				SRApi.Notify(SRApi.T("当前对局不支持该操作", "Unsupported in the current match"));
				return false;
			}
			if (ReloadBlocked()) return false; // 含锁残留自愈
			gc = g;
			qs = q;
			return true;
		}

		// QuickSaver.initialLevelPlaceables 的反射字段缓存。
		private static System.Reflection.FieldInfo _initialPiecesField;

		// 关卡自带件集合（QuickSaver.initialLevelPlaceables，私有字段 -> 走 SRApi.Ref.Field 反射）。
		// 这些件不能列入清场目标：QuickClear 删后会 RestoreSaveables 恢复，本路径只删不恢复，会连可动方块一起删掉。
		private static System.Collections.Generic.HashSet<Placeable> InitialLevelPieces(QuickSaver qs) {
			System.Collections.Generic.HashSet<Placeable> set = new System.Collections.Generic.HashSet<Placeable>();
			try {
				// 反射游戏成员必须走 SRApi.Ref.Field（自带缓存与失败告警）；外层字段缓存只省一次字典查找。
				if (_initialPiecesField == null) _initialPiecesField = SRApi.Ref.Field(typeof(QuickSaver), "initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
				System.Collections.IEnumerable list = (_initialPiecesField != null && qs != null) ? _initialPiecesField.GetValue(qs) as System.Collections.IEnumerable : null;
				if (list == null) return set;
				foreach (object o in list) {
					if (o == null) continue;
					// QuickSaver.SaveablePiece 是公开嵌套类型、placeable 是 public 字段，直接强类型访问以便编译期发现改版。
					QuickSaver.SaveablePiece sp = o as QuickSaver.SaveablePiece;
					if (sp != null && sp.placeable != null) set.Add(sp.placeable);
				}
			} catch (Exception __ex) { SRApi.Guard.Log("收集关卡初始件", __ex); }
			return set;
		}
		// 清除玩家放置的方块（全员可见，房客也能用）：逐块网络销毁，不走 <destroyed> 快照路径。
		public static void ClearPlacedBlocks(List<Placeable> targets, string feature) {
			try {
				if (targets == null || targets.Count == 0) { SRApi.Notify(SRApi.T("地图上没有玩家放置的道具", "No player-placed props on the map")); return; }
				GameControl gc; QuickSaver qs;
				if (!TryGetSnapshotContext(feature, out gc, out qs)) return;
				FallbackClearPlacedBlocks(targets); // 直接逐块网络销毁（不用快照）
			} catch (Exception __ex) { SRApi.Guard.Log("清场.逐块销毁", __ex); }
		}

		// 对外槽位：调用方经 Slots.Require 按名取用，不引用 Level 类型，取不到即降级。
		private const string SlotClearPlacedBlocks = "Level.ClearPlacedBlocks"; // Action<List<Placeable>,string>
		// 槽位名与 DestroyBlocks 逐字一致，双方各写各的常量，互不引用以免编译期依赖。
		private const string SlotAttributeToHost = "DestroyBlocks.AttributeToHost"; // Action<List<Placeable>>
		// 快照加载后处理（仅 SR 发起的快照）：补发当前阶段事件、补放置计数与着色、把重建出的方块归属房主。
        [HarmonyPatch(typeof(QuickSaver), "LoadSnapshotFromXmlDocument")]
        [HarmonyPostfix]
        private static void AfterLoadSnapshot(QuickSaver __instance, System.Xml.XmlDocument doc, ref bool __result) {
            try {
                // 双重判据：仅 SR 自己发起的快照才补发阶段事件（房主看静态门闩，客户端看 XML 根节点 srSnapshot="1"）。
                // 阶段由服务器经 RpcStartPhase 权威下发（含 EndPhaseEvent 与 nextPhase），无条件补发会让客户端与服务器状态机错位。
                if (!IsSrSnapshot(doc)) return;
                if (!__result) return; // 快照加载失败（关卡名/结构不符）时不按阶段处理
                GameControl gc = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (gc == null) return;
                // GameControl.Phase / GameEvent.StartPhaseEvent / GameEventManager.SendEvent 均为 public，直接调用，无需反射。
                GameControl.GamePhase phv = gc.Phase;
                GameEvent.GameEventManager.SendEvent(new GameEvent.StartPhaseEvent(phv));
                SRApi.LogInfo("[快照] 加载后补发阶段事件: " + phv);
                // 只补发一次当前真实阶段（伪造 PLACE/PLAY 边沿会让客户端与服务器状态机错位）；快照方块无需照抄 WaitForPlacement 的 isKinematic/layer=9：prefab 默认已 Kinematic，而 layer 9(Placed) 与自身及 Fixed 都不碰撞。
                // 快照路径真正缺的只有放置计数 GameState.IncrementPieceCount 与一次 Tint()。
                try {
                    int nFix = 0;
                    System.Collections.Generic.HashSet<Placeable> initFix = InitialLevelPieces(__instance);
                    GameState gsFix = null;
                    try { gsFix = GameState.GetInstance(); } catch { }
                    foreach (Placeable fp in Placeable.AllPlaceables) {
                        if (fp == null || fp.MarkedForDestruction) continue;
                        if (initFix.Contains(fp)) continue; // 关卡自带件不动
                        if (!fp.Placed) continue; // 只处理已放置的
                        try {
                            // 补放置计数（快照路径唯一真正缺的一步）
                            if (gsFix != null) gsFix.IncrementPieceCount(fp.Name);
                            fp.Tint();
                            nFix++;
                        } catch (Exception __pe) { SRApi.Guard.Log("快照方块收尾", __pe); }
                    }
                    if (nFix > 0) SRApi.LogInfo("[快照] 已为 " + nFix + " 个方块补放置计数并着色");
                } catch (Exception __fx) { SRApi.Guard.Log("快照方块收尾", __fx); }
                // 归属登记必须在加载之后：方块是加载时创建的，加载前集合为空。
                try {
                    System.Collections.Generic.HashSet<Placeable> initPcs = InitialLevelPieces(__instance);   // O2：与上方不属同一方法，保持独立调用
                    List<Placeable> built = new List<Placeable>();
                    foreach (Placeable bp in Placeable.AllPlaceables) {
                        // 必须过滤 MarkedForDestruction：上一轮残留的死方块（destroyMarkedPieces 未回收）会被误算进 built。
                        if (bp == null || bp.MarkedForDestruction || initPcs.Contains(bp)) continue;
                        built.Add(bp);
                    }
                    // 经槽位取用以避免跨功能编译期依赖；取不到只丢名牌与归属，不影响重建。
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

		private static string StripDestroyedNodes(string xml) {
		    try {
		        if (string.IsNullOrEmpty(xml)) return xml;
		        System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
		        doc = ParseXmlSafe(xml);
		        System.Xml.XmlElement root = doc.DocumentElement;
		        if (root == null) return xml;
		        List<System.Xml.XmlNode> bad = new List<System.Xml.XmlNode>();
		        foreach (System.Xml.XmlNode n in root.ChildNodes) {
		            if (n.NodeType == System.Xml.XmlNodeType.Element && n.Name == "destroyed") bad.Add(n);
		        }
		        int n2 = bad.Count;
		        for (int i = 0; i < n2; i++) root.RemoveChild(bad[i]);
		        if (n2 > 0) SRApi.LogInfo("[快照] 已剥掉快照内 <destroyed> 节点: " + n2 + " 个");
		        return doc.OuterXml;
		    } catch (Exception __ex) { SRApi.Guard.Log("剥离 destroyed", __ex); return xml; }
		}

		// 兜底清场：快照 <destroyed> 路径不可用时退回逐块 DestroySelf（仍走游戏原生网络销毁信号）。
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

// 分区 Reload：重载关卡保分补分 / 折扣保留 / 重载宽限。

    [HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
    [HarmonyPostfix]
    static void OnLoadEnd() {
        // 补丁内异常会被 Harmony 吞掉并每次记一条日志，统一兜住并经 SRApi.Guard.Log（按调用点去重）记录。
        try {
                // 重载后广播补分：按原类型分块补分，下一回合结算显示。
                FillScoresIfPending();
                // 重载后恢复分数折扣（handicap）：不受重载模式影响，房主写回 SyncVar 广播全员。
                RestoreHandicapsIfAny();
                // 通知 Experiments 记录场景加载完成时刻，用于决定何时清 levelPortalXml。
                Level.OnReloadSceneLoaded();
        } catch (Exception __ex) { SRApi.Guard.Log("重载完成收尾", __ex); }
    }

    // 场景重载期间暂停“坏客户端踢出”检测。
    // LobbyManager.Update 会把连续 3 秒无有效 player controller 的连接踢掉，重载中 controller 暂时无效会被误判。
    }
}
