using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
// 重载关卡 / 广播方块快照 / 重载模式（Reload Mode）。
// 类名用 LevelTools：本命名空间里定义 Level 会遮蔽游戏自带的 global::Level，易出隐蔽错误。
public partial class LevelTools : ITweak {

    private static ConfigEntry<ReloadMode> _reloadMode;

    //本文件界面文案（自包含）
    private static void SelfReg() {
        SR.LocSec("Level", "关卡", null);
        SR.Nav("Level", 60); //侧栏栏目（顺序 60）
        SR.LocKey("Level", "Reload Mode", "重载模式", null);
        SR.LocDesc("Level", "Reload Mode", "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks are kept; the host broadcasts a fill using the pre-reload per-type blocks (win/coin/trap etc. as-is), so everyone (incl. clients without the mod) sees the same score and types as before at the next round's tally (fill does not trigger an immediate tally; icons appear with the normal round end).\nKeep blocks only (skip fill) = blocks are kept, score resets (fresh match, no fill).");
    }

    public static void Render() {
        GUILayout.Label(SR.T("— 重载关卡 —", "— Reload level —"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent(SR.T("重载关卡", "Reload level"),
            SR.T("房主把当前视角的所有方块写入关卡快照 → 真正重载当前关卡场景（全员重新加载）。\n重载后按快照重建方块并广播：所有方块（含玩家放置的、可移动的）不丢；\n按下方“重载模式”决定是否补分保留分数。\n⚠ 仅派对/创意局内生效；仅房主有效。", "Host writes the current blocks into the snapshot → truly reloads the current level scene (everyone reloads).\nRebuilds blocks from the snapshot: every block is kept;\nscore fill depends on the Reload mode below.\n⚠ Party/Creative only; host only.")),
            SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(170)), GUILayout.Height(SR.Ctl.Sc(30)))) {
            LevelTools.ReloadLevel();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        ConfigEntryBase rlm = SR.Ctl.FindEntry("Level", "Reload Mode");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("重载模式", "Reload mode"),
            SR.T("重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按原类型分块给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks kept; the host fills scores so everyone sees the same score/types next tally.\nKeep blocks only (skip fill) = blocks kept, score resets.")),
            rlm, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (rlm != null) SR.Ctl.RenderControl(rlm);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));

        GUILayout.Label(SR.T("— 广播方块快照 —", "— Broadcast snapshot —"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(new GUIContent(SR.T("广播方块快照", "Broadcast snapshot"),
            SR.T("房主把当前视角的所有方块（含位置/旋转/属性）打包广播 → 全员按房主视角重建方块。\n用于修复偶发的“局内方块在自己视野消失/不同步”。\n不重载场景：对局进度与分数保留，全员短暂卡顿后方块即重建。\n⚠ 仅派对/创意局内生效；仅房主有效。", "Host packages all blocks in their view and broadcasts → everyone rebuilds blocks from the host snapshot.\nFixes occasional in-match blocks disappearing/desyncing.\nNo scene reload: progress and scores are kept.\n⚠ Party/Creative only; host only.")),
            SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(170)), GUILayout.Height(SR.Ctl.Sc(30)))) {
            LevelTools.BroadcastSnapshot();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));

        //派对盒子炸弹：对局内所有成员都发过「炸弹！」快捷短语才生成
        GUILayout.Label(SR.T("— 派对盒子炸弹 —", "— Party-box bomb —"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        ConfigEntryBase pbe = SR.Ctl.FindEntry("Level", "Party Bomb"); //PartyBomb 的 Bind 在 Level 段，查 EX 段取不到（勾选框写不回去）
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("派对盒子炸弹", "Party-box bomb"), SR.T("房主开启后，对局内所有成员都发过「炸弹！」快捷短语时，往当前派对盒子塞入炸弹。", "Host on: when every member sends the Bomb quick-phrase, spawn a bomb in the current party box.")), pbe, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (GUILayout.Button(PartyBomb.Enabled ? "✓" : "", PartyBomb.Enabled ? SR.Ctl.CheckOn : SR.Ctl.CheckOff, GUILayout.Width(SR.Ctl.Sc(30)), GUILayout.Height(SR.Ctl.Sc(26)))) {
            if (pbe != null) SR.Ctl.SetValue(pbe, !PartyBomb.Enabled); //走统一入口：带 try/catch + 标记待保存
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        GUILayout.Label(SR.T("炸弹种类", "Bomb type"), SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(84)), GUILayout.Height(SR.Ctl.Sc(26)));
        ConfigEntryBase pbtype = SR.Ctl.FindEntry("Level", "Party Bomb Type"); //同上：取不到则整行下拉框不显示
        if (pbtype != null) {
            //三档：0小 / 1大 / 2超级（对应游戏内 99abombmini / 99bomb / 99bbombmega）
            string[] opts = new[] { SR.T("小炸弹", "Small"), SR.T("大炸弹", "Big"), SR.T("超级炸弹", "Mega") };
            int curIdx = Mathf.Clamp((int)pbtype.BoxedValue, 0, 2);
            bool open; if (!SR.Ctl.EditOpen.TryGetValue(pbtype, out open)) open = false;
            int sel = SR.Ctl.ComboBox(pbtype, opts[curIdx], null, opts, ref open, SR.Ctl.Sc(150));
            if (sel >= 0) { pbtype.BoxedValue = sel; SR.Ctl.EditOpen[pbtype] = false; }
            else SR.Ctl.EditOpen[pbtype] = open;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));
    }

    public void Initialize(MainPlugin plugin) {
        SelfReg();
			_reloadMode = ((BaseUnityPlugin)plugin).Config.Bind<ReloadMode>("Level", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
    }

		//重载关卡模式：KeepScore（保留方块和分数） / KeepBlocksOnly（仅保留方块，分数重置）
		public enum ReloadMode {
			KeepScore,
			KeepBlocksOnly
		}

		private static bool _reloadBusy; //重载关卡进行中（防重复触发）

		private static float _lastSnapshotAt = -999f; //广播快照冷却

		private static float _reloadLoadedAt = -1f; //重载场景加载完成(FadeOut)时刻，用于确定何时可清 levelPortalXml

		private static float _reloadStartedAt = -1f; //发起 ReloadScene 时刻（超时兜底）

		//重载是否保留分数（KeepScore = 保留，KeepBlocksOnly = 不保留）
		public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

		//仅派对(PARTY)/创意(CREATIVE)局内生效
		private static bool IsPartyOrCreative()
		{
			//统一模式门控（IgnoreModeLimit 豁免已内置）
			return SR.GateModeAllows(SR.ModeMask.Party | SR.ModeMask.Creative);
		}

		//真正重载当前关卡场景（ReloadScene → 全员重新加载）：重载前把房主快照写入 QuickSaver.levelPortalXml（static），
		//重载后房主 OnSetupStartLevel 读取并原生广播，全员重建方块；分数按 SR 保分 patch 恢复。
		//⚠ 仅派对(PARTY)/创意(CREATIVE)局内生效；仅房主有效（ReloadScene 是服务器操作）。
		public static void ReloadLevel()
		{
			ReloadLevelImpl(SR.T("重载关卡", "Reload Level"));
		}

		//共享核心（与重载关卡一致的模式限制 + 快照写入 levelPortalXml）
		private static void ReloadLevelImpl(string feature)
		{
			try
			{
				if (!SR.GateMaster)
				{
					Experiments.NotifyExp(Experiments.Msgs.MasterOff());
					return;
				}
				if (!IsPartyOrCreative())
				{
					Experiments.NotifyExp(feature + SR.T("仅派对/创意局内生效", " works only in Party/Creative matches"));
					return;
				}
				if (!Experiments.IsHostExp())
				{
					Experiments.NotifyExp(SR.T("仅房主有效：", "Host only: ") + feature + SR.T("需要服务器权限（房主也装本模组后可用；房客可请房主操作）", " requires server authority (usable after the host installs this mod; guests can ask the host)"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					Experiments.NotifyExp(SR.T("不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					Experiments.NotifyExp(SR.T("找不到游戏控制器", "Cannot find the game controller"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					Experiments.NotifyExp(SR.T("找不到快照组件", "Cannot find the snapshot component"));
					return;
				}
				GameState gs = GameState.GetInstance();
				if (gs == null)
				{
					Experiments.NotifyExp(SR.T("游戏状态不可用", "Game state unavailable"));
					return;
				}
				//防重入：在生成快照/写 levelPortalXml 之前就锁住，连点直接挡掉。
				if (_reloadBusy) { Experiments.NotifyExp(SR.T("正在重载，请稍候", "Reload in progress")); return; }
				//刚广播过快照（3 秒内）先别重载，等方块重建落定
				if (Time.unscaledTime - _lastSnapshotAt < 3f) {
					Experiments.NotifyExp(SR.T("快照刚广播，请稍候再重载", "A snapshot was just broadcast; wait a moment before reloading"));
					return;
				}
				_reloadBusy = true;
				//1) 生成房主当前快照 → 写 QuickSaver.levelPortalXml，重载后 OnSetupStartLevel 读取并原生广播全员
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
				{
					Experiments.NotifyExp(SR.T("生成快照失败（当前场景无方块数据）", "Failed to generate snapshot (no block data in the current scene)"));
					return;
				}
				try
				{
					System.Reflection.FieldInfo lpx = HarmonyLib.AccessTools.Field(typeof(QuickSaver), "levelPortalXml");
					if (lpx != null) lpx.SetValue(null, doc.OuterXml);
				}
				catch
				{
					Experiments.NotifyExp(SR.T("写入快照失败", "Failed to write snapshot"));
					return;
				}
				//2) 发送 PrepareToReloadScene：KeepScore 先置保分标志（重载后按原类型广播补分，下回合结算显示）；
				//   KeepBlocksOnly 不置标志 → 重载后分数重置。
				if (ReloadKeepsScore) MarkPreserveScores();
				try
				{
					MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene
					{
						reloadToMode = GameSettings.GetInstance().GameMode,
						snapshotInfo = gs.currentSnapshotInfo
					};
					NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
				}
				catch (Exception __ex) { SR.Guard.Log("LevelTools.SendToAll", __ex); }
				if (LoadingInterstitialSplash.Instance != null)
				{
					LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
					LoadingInterstitialSplash.Instance.FadeIn();
				}
				//3) 真正重载场景（全员重新加载；房主 OnSetupStartLevel 自动加载+广播当前方块）
				lm.StartCoroutine(ReloadSceneRoutine());
				Experiments.NotifyExp(feature + SR.T("：全员重载关卡，方块保留，", ": reloading the level for everyone; blocks kept, ") +
					(ReloadKeepsScore ? SR.T("分数补回", "scores restored") : SR.T("分数重置", "scores reset")));
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)(feature + "失败: " + ex.Message));
			}
		}

		private static IEnumerator ReloadSceneRoutine()
		{
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame(); //等加载画面 FadeIn 生效
			bool ok = false;
			try
			{
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm != (UnityEngine.Object)null)
				{
					lm.ReloadScene(GameSettings.GetInstance().GameMode);
					ok = true;
				}
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)("重载场景失败: " + ex.Message));
			}
			_reloadStartedAt = Time.unscaledTime;
			_reloadLoadedAt = -1f; //等待重载场景真正加载完成（LoadingInterstitialSplash.FadeOut）来置位
			//等「新场景加载完成(FadeOut)」再多等 2 秒才清 levelPortalXml 并复位 busy（慢房主不会丢方块）；
			//FadeOut 迟迟不触发时用 25 秒硬上限兜底，避免一直锁死。
			if (ok)
			{
				while (_reloadBusy)
				{
					if (_reloadLoadedAt >= 0f && Time.unscaledTime - _reloadLoadedAt >= 2f) break;
					if (Time.unscaledTime - _reloadStartedAt >= 25f) break;
					yield return null;
				}
			}
			ClearPortalXml();
			_reloadBusy = false;
		}

		//重载场景加载完成（FadeOut，由 SR.Reload.OnLoadEnd 调用）：记录时刻供 ReloadSceneRoutine 决定何时清 levelPortalXml。
		public static void OnReloadSceneLoaded()
		{
			_reloadLoadedAt = Time.unscaledTime;
		}

		//清掉写入的 QuickSaver.levelPortalXml（static）
		private static void ClearPortalXml()
		{
			try
			{
				System.Reflection.FieldInfo lpx = HarmonyLib.AccessTools.Field(typeof(QuickSaver), "levelPortalXml");
				if (lpx != null) lpx.SetValue(null, null);
			}
			catch (Exception __ex) { SR.Guard.Log("LevelTools.Field", __ex); }
		}

		//广播方块快照：房主重发当前关卡快照 → 全员按房主视角重建方块（修复方块消失/不同步）。
		public static void BroadcastSnapshot()
		{
			RebuildBlocksFromHost(SR.T("广播方块快照", "Broadcast Snapshot"));
		}

		//共享核心：生成房主当前快照 → CompressAndSendSnapshotBytes 广播 → 全员按房主视角重建（含属性/胶水/玩家放置的）。
		//不重载场景 → 分数保留；⚠ 仅派对(PARTY)/创意(CREATIVE)局内生效。
		private static void RebuildBlocksFromHost(string feature)
		{
			try
			{
				if (!SR.GateMaster)
				{
					Experiments.NotifyExp(Experiments.Msgs.MasterOff());
					return;
				}
				if (!IsPartyOrCreative())
				{
					Experiments.NotifyExp(feature + SR.T("仅派对/创意局内生效", " works only in Party/Creative matches"));
					return;
				}
				if (!Experiments.IsHostExp())
				{
					Experiments.NotifyExp(SR.T("仅房主有效：", "Host only: ") + feature + SR.T("需要服务器权限（房主也装本模组后可用；房客可请房主操作）", " requires server authority (usable after the host installs this mod; guests can ask the host)"));
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					Experiments.NotifyExp(SR.T("不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					Experiments.NotifyExp(SR.T("找不到游戏控制器", "Cannot find the game controller"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					Experiments.NotifyExp(SR.T("找不到快照组件", "Cannot find the snapshot component"));
					return;
				}
				//重载进行中不放广播快照（防与场景重载打架）
				if (_reloadBusy) { Experiments.NotifyExp(SR.T("正在重载，请稍候", "Reload in progress")); return; }
				//冷却判断放在最前：冷却期内不再白做一次全量序列化
				float _now = Time.unscaledTime;
				if (_now - _lastSnapshotAt < 3f) { Experiments.NotifyExp(SR.T("快照广播冷却中，请稍候", "Snapshot broadcast on cooldown")); return; }
				_lastSnapshotAt = _now;
				//生成房主当前视角的完整快照 XML（包含所有方块及属性：位置/旋转/缩放/胶水 parentID/mainID 等）
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null)
				{
					Experiments.NotifyExp(SR.T("生成快照失败", "Failed to generate snapshot"));
					return;
				}
				string xml = doc.OuterXml;
				if (string.IsNullOrEmpty(xml))
				{
					Experiments.NotifyExp(SR.T("快照为空", "Snapshot is empty"));
					return;
				}
				//用游戏原生机制广播：房主压缩 → RPC 发给全员 → LoadSnapshotFromXmlDocument 重建；不重载场景故分数保留。
				//大快照提示 + 序列化耗时。
				if (xml.Length > 1500000) Experiments.NotifyExp(SR.T("快照较大（" + (xml.Length / 1000) + " KB），广播可能略卡", "Large snapshot (" + (xml.Length / 1000) + " KB); may hitch"));
				System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch(); _sw.Start();
				byte[] xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
				_sw.Stop();
				MainPlugin.ModLogger.LogInfo("[快照] 序列化耗时 " + _sw.ElapsedMilliseconds + "ms");
				Experiments.NotifyExp(feature + SR.T("：正在广播快照（", ": broadcasting snapshot (") + xmlBytes.Length + SR.T(" 字节）…全员将按房主当前状态重建方块", " bytes)… everyone will rebuild blocks from the host's current state"));
				gc.CompressAndSendSnapshotBytes(xmlBytes, delegate {
					Experiments.NotifyExp(feature + SR.T("完成：全员方块已重建，分数保留", " done: everyone rebuilt the blocks, scores kept"));
				});
			}
			catch (Exception ex)
			{
				MainPlugin.ModLogger.LogWarning((object)(feature + "失败: " + ex.Message));
			}
		}

    // ==== 浠ヤ笅涓洪噸杞藉叧鍗℃湡闂寸殑鐘舵€佹満涓庤ˉ涓侊紙鍘?SR.Reload.cs锛岀 8 鏉★細骞跺叆鏈姛鑳斤級 ====

// ==== 分区：Reload（重载关卡保分补分 / 折扣保留 / 重载宽限 / 加载后 GC）====

    //加载完成：回收加载产生的垃圾，减少进入对局后的卡顿；只在本场景第一次加载完成时清理
    //（同关卡回合切换场景名不变 → 跳过，避免每回合 GC 拖慢结算）。延迟 1 秒执行，避免 GC 的同步阻塞卡住淡出过渡。
    private static string _lastCleanedScene = "";
    private static float _pendingCleanupAt = -1f;
    private static string _pendingCleanupScene = "";

    //加载后清理的实际执行（由 SR.Tick 每帧调用）：FadeOut 后延迟 1 秒再 GC，不阻塞过渡
    internal static void TickCleanup() {
        if (_pendingCleanupAt < 0f || Time.unscaledTime < _pendingCleanupAt) return;
        _pendingCleanupAt = -1f;
        try {
            _lastCleanedScene = _pendingCleanupScene;
            System.GC.Collect();
            Resources.UnloadUnusedAssets();
        } catch (Exception __ex) { SR.Guard.Log("加载后清理(GC)", __ex); }
    }

    [HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
    [HarmonyPostfix]
    static void OnLoadEnd() {
        //重载关卡后广播补分（模式一"保留分数"：按原类型分块补分，下一回合结算显示）
        FillScoresIfPending();
        //重载关卡后恢复分数折扣（handicap）：无论模式一/二，房主写回 SyncVar → 房客折扣保留
        RestoreHandicapsIfAny();
        //重载场景加载完成：通知 Experiments 记录时刻，用于决定何时清 levelPortalXml（替代固定延时）
        LevelTools.OnReloadSceneLoaded();
        if (!SR.GateMaster) return;
        if (Experiments.GcAfterLoadEntry == null || !Experiments.GcAfterLoadEntry.Value) return;
        try {
            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc == _lastCleanedScene) return; //同场景（回合切换重载）不清理
            _pendingCleanupScene = sc;
            _pendingCleanupAt = Time.unscaledTime + 1f;
        } catch (Exception __ex) { SR.Guard.Log("记录加载后清理场景", __ex); }
    }

    //--- 场景重载期间暂停"坏客户端踢出"检测 ---
    //LobbyManager.Update 每秒把连续 3 秒无有效 player controller 的连接踢掉；重载时客户端正在加载新场景、
    //controller 暂时无效，稍慢就会被误判断连（Steam 断连弹窗），故 ReloadScene 后给 20 秒宽限期。
    private static float _reloadGraceUntil = 0f;
    [HarmonyPatch(typeof(LobbyManager), "ReloadScene")]
    [HarmonyPrefix]
    static void OnReloadSceneStart() {
        _reloadGraceUntil = Time.unscaledTime + 20f;
    }
    [HarmonyPatch(typeof(LobbyManager), "DisconnectBrokenClients")]
    [HarmonyPrefix]
    static bool SkipDisconnectDuringReload() {
        return Time.unscaledTime >= _reloadGraceUntil;
    }

    //--- 重载后保留分数（分类型补分） ---
    //重载时 ScoreKeeper.Setup() 会清空 playerTotal/分块（"重开本关"的固有行为）→ 分数丢失。
    //重载前备份各玩家分块列表，重载后房主按原类型逐个重放 PointAwarded → 全员下回合 tally 恢复；不恢复 playerTotal。
    private static bool _preserveScoresOnReload = false;
    //备份：networkNumber → List<PointBlock>（含类型的完整分块）
    private static Dictionary<int, List<PointBlock>> _scoreBackupBlocks = null;
    //补分重放后置位：下次结算的 ClearNewPointBlocks 跳过清除，保证重放分块能正常显示+tally。
    private static bool _refillPending = false;
    //外部加分（EX 加分 / 补分重放，MsgPointAwarded.AlwaysAward=true）的分块标记：
    //结算会删掉非 AlwaysAward 分块，但玩家见过图标的要保留 → 被清时缓存，重载备份补回。
    private static HashSet<int> _externAwardedKeys = new HashSet<int>();
    //结算时被 Clear 的 EX 加分分块（重载备份时合并进 _scoreBackupBlocks）
    private static List<PointBlock> _clearedExternBlocks = new List<PointBlock>();
    //备份：networkNumber → handicap（SyncVar 本应跨场景保留，重载后仍由房主显式写回更保险）
    private static Dictionary<int, int> _handicapBackup = null;

    //重载关卡前直接置标志（模式一：允许补分）
    public static void MarkPreserveScores() {
        _preserveScoresOnReload = true;
    }

    [HarmonyPatch(typeof(GameControl), "handleEvent")]
    [HarmonyPrefix]
    static void OnPrepareReloadMessage(GameEvent.GameEvent e) {
        try {
            //收到 PrepareToReloadScene 只重置补分保护，不自动置补分标志——否则外部工具发起的重载会被再补一份 → 分数翻倍。
            if (e is GameEvent.NetworkMessageReceivedEvent nm && nm.Message.msgType == NetMsgTypes.PrepareToReloadScene) {
                _refillPending = false;
                //折扣备份与补分无关：无论哪种模式重载后都要保留；恢复仅房主执行，房客端备份无妨。
                BackupHandicaps();
            }
        } catch (Exception __ex) { SR.Guard.Log("处理 PrepareToReloadScene", __ex); }
    }

    //备份所有在线玩家的 handicap（从 LobbyPlayer 读服务器权威值）。
    private static void BackupHandicaps() {
        try {
            _handicapBackup = new Dictionary<int, int>();
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.PlayerTracker == null) return;
            for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                try {
                    NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                    LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(info.NetworkNumber);
                    if (lp == null) continue;
                    _handicapBackup[info.NetworkNumber] = lp.Networkhandicap;
                } catch (Exception __ex) { SR.Guard.Log("读取玩家 handicap", __ex); }
            }
            if (_handicapBackup.Count > 0) {
                MainPlugin.ModLogger.LogInfo("[折扣] 重载前备份 " + _handicapBackup.Count + " 名玩家 handicap");
            }
        } catch (Exception __ex) { SR.Guard.Log("备份 handicap", __ex); }
    }

    //重载完成后房主写回备份的 handicap（LobbyPlayer + GamePlayer 的 SyncVar，服务器赋值自动广播全员）。
    private static void RestoreHandicapsIfAny() {
        if (_handicapBackup == null || _handicapBackup.Count == 0) return;
        Dictionary<int, int> backup = _handicapBackup;
        _handicapBackup = null;
        try {
            if (!SR.HasServer) return; //仅房主写 SyncVar（统一走 SR.HasServer）
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.PlayerTracker == null) return;
            foreach (KeyValuePair<int, int> kv in backup) {
                try {
                    LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(kv.Key);
                    if (lp != null) lp.Networkhandicap = kv.Value;
                } catch (Exception __ex) { SR.Guard.Log("恢复 LobbyPlayer handicap", __ex); }
                try {
                    GamePlayer gp = lm.PlayerTracker.GetGamePlayer(kv.Key);
                    if (gp != null) gp.NetworkHandicap = kv.Value;
                } catch (Exception __ex) { SR.Guard.Log("恢复 GamePlayer handicap", __ex); }
            }
            MainPlugin.ModLogger.LogInfo("[折扣] 重载后恢复 " + backup.Count + " 名玩家 handicap");
        } catch (Exception __ex) { SR.Guard.Log("恢复 handicap", __ex); }
    }

    //结算时 ClearNewPointBlocks 会删掉非 AlwaysAward 分块。补分重放后(_refillPending)跳过清除；
    //正常结算只缓存"外部加分"分块——玩家见过图标要保留，原生发的从未 tally 则不缓存（避免多计分）。
    [HarmonyPatch(typeof(ScoreKeeper), "ClearNewPointBlocks")]
    [HarmonyPrefix]
    static bool ClearNewPointBlocksPrefix(ScoreKeeper __instance) {
        try {
            if (_refillPending) {
                //补分重放后的第一次结算：跳过清除，保留全部分块
                return false;
            }
            if (__instance == null || __instance.newPointBlocks == null) return true;
            for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                PointBlock pb = __instance.newPointBlocks[i];
                if (pb == null) continue;
                if (!pb.AlwaysAward && _externAwardedKeys.Contains(Key(pb))) {
                    //外部加分（EX/补分）的非 AlwaysAward 分块：缓存，重载备份时补回
                    _clearedExternBlocks.Add(pb);
                }
            }
            if (_clearedExternBlocks.Count > 500) {
                _clearedExternBlocks.RemoveRange(0, _clearedExternBlocks.Count - 500);
            }
        } catch (Exception __ex) { SR.Guard.Log("缓存被清除的外部加分分块", __ex); }
        return true;
    }

    //分块标记键：(playerNumber << 8) | (int)type
    private static int Key(PointBlock pb) { return (pb.playerNumber << 8) | (int)pb.type; }
    private static int Key(int playerNumber, PointBlock.pointBlockType type) { return (playerNumber << 8) | (int)type; }

    //ScoreKeeper 收到 PointAwarded：标记"外部加分"分块（EX 加分与补分重放都发 AlwaysAward=true，
    //原生名次分等为 false），以区分"玩家看到的加分"（保留）与"原生无效分"（不补）。
    [HarmonyPatch(typeof(ScoreKeeper), "handleEvent")]
    [HarmonyPrefix]
    static void OnPointAwardedMessage(ScoreKeeper __instance, global::GameEvent.GameEvent e) {
        try {
            if (e == null || e.GetType() != typeof(GameEvent.NetworkMessageReceivedEvent)) return;
            GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
            if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.PointAwarded) return;
            MsgPointAwarded msg = nm.Message.ReadMessage<MsgPointAwarded>();
            if (msg != null && msg.AlwaysAward) {
                _externAwardedKeys.Add(Key(msg.PlayerNumber, msg.PointType));
            }
        } catch (Exception __ex) { SR.Guard.Log("标记外部加分分块", __ex); }
    }

    //补分重放的分块 tally 后即完成使命：清除 _refillPending 与外部加分标记，恢复正常清除。
    [HarmonyPatch(typeof(ScoreKeeper), "TallyPointBlockAllPlayers")]
    [HarmonyPostfix]
    static void TallyPostfix() {
        _refillPending = false;
        _externAwardedKeys.Clear();
        _clearedExternBlocks.Clear();
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPrefix]
    static void ScoreSetupPrefix(ScoreKeeper __instance) {
        if (!_preserveScoresOnReload) return;
        try {
            //备份 historyPointBlocks（已 tally）+ newPointBlocks（本回合未结算）：两者才是重载前真正拥有的分。
            List<PointBlock> blocks = new List<PointBlock>();
            try {
                FieldInfo hf = AccessTools.Field(typeof(ScoreKeeper), "historyPointBlocks");
                List<PointBlock> hist = hf != null ? hf.GetValue(__instance) as List<PointBlock> : null;
                if (hist != null) blocks.AddRange(hist);
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("[保分] 备份 historyPointBlocks 失败: " + ex.Message);
            }
            try {
                FieldInfo nf = AccessTools.Field(typeof(ScoreKeeper), "newPointBlocks");
                List<PointBlock> npb = nf != null ? nf.GetValue(__instance) as List<PointBlock> : null;
                if (npb != null) blocks.AddRange(npb);
            } catch (Exception ex) {
                MainPlugin.ModLogger.LogWarning("[保分] 备份 newPointBlocks 失败: " + ex.Message);
            }
            if (_clearedExternBlocks != null && _clearedExternBlocks.Count > 0) {
                //结算时被 Clear 的"外部加分"分块（玩家见过、期望保留）——合并进备份，重载后补回
                blocks.AddRange(_clearedExternBlocks);
                MainPlugin.ModLogger.LogInfo("[保分] 合并被清除的外部加分分块 " + _clearedExternBlocks.Count + " 条");
                _clearedExternBlocks.Clear();
            }
            if (blocks.Count == 0) {
                MainPlugin.ModLogger.LogWarning("[保分] 重载时无可备份的分块（history+new+外部加分均为空）");
                return;
            }
            _scoreBackupBlocks = new Dictionary<int, List<PointBlock>>();
            foreach (PointBlock pb in blocks) {
                if (pb == null) continue;
                List<PointBlock> list;
                if (!_scoreBackupBlocks.TryGetValue(pb.playerNumber, out list)) {
                    list = new List<PointBlock>();
                    _scoreBackupBlocks[pb.playerNumber] = list;
                }
                list.Add(pb);
            }
            MainPlugin.ModLogger.LogInfo("[保分] 重载备份分块 " + blocks.Count + " 条，涉及 " + _scoreBackupBlocks.Count + " 名玩家");
        } catch (Exception ex) {
            MainPlugin.ModLogger.LogWarning("[保分] 备份分块失败: " + ex.Message);
        }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPostfix]
    static void ScoreSetupPostfix(ScoreKeeper __instance) {
        //重载保分模式：下次结算跳过 ClearNewPointBlocks（保护重放分块）；正常开局时同步清掉
        //_refillPending，避免上次重载残留影响本次。
        _refillPending = _preserveScoresOnReload;
        //只清标志，不恢复分块——分块图标由补分广播恢复（见 FillScoresIfPending）
        _preserveScoresOnReload = false;
    }

    //重载完成后广播补分：房主按备份的原类型分块逐个发 PointAwarded（AlwaysAward=true），下回合 tally 进 playerTotal。
    //防网络风暴：上限 300 条，分帧发送（每帧 30 条）。
    private const int FillPerFrame = 30;
    private const int FillTotalCap = 300;
    private static void FillScoresIfPending() {
        if (_scoreBackupBlocks == null || _scoreBackupBlocks.Count == 0) return;
        if (!SR.GateMaster) return;
        Dictionary<int, List<PointBlock>> backup = _scoreBackupBlocks;
        _scoreBackupBlocks = null;
        try {
            if (!SR.HasServer) return; //仅房主广播（统一走 SR.HasServer）
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.client == null || !lm.client.isConnected) return;
            List<KeyValuePair<int, PointBlock.pointBlockType>> queue = new List<KeyValuePair<int, PointBlock.pointBlockType>>();
            foreach (KeyValuePair<int, List<PointBlock>> kv in backup) {
                List<PointBlock> list = kv.Value;
                if (list == null || list.Count == 0) continue;
                foreach (PointBlock pb in list) {
                    if (pb == null) continue;
                    if (queue.Count >= FillTotalCap) break; //全局上限
                    queue.Add(new KeyValuePair<int, PointBlock.pointBlockType>(kv.Key, pb.type));
                }
            }
            if (queue.Count == 0) return;
            lm.StartCoroutine(FillScoresCoroutine(queue));
            MainPlugin.ModLogger.LogInfo("[补分] 队列 " + queue.Count + " 条，分帧发送（每帧 " + FillPerFrame + " 条）");
        } catch (Exception ex) {
            MainPlugin.ModLogger.LogWarning("[补分] 初始化失败: " + ex.Message);
        }
    }

    private static System.Collections.IEnumerator FillScoresCoroutine(List<KeyValuePair<int, PointBlock.pointBlockType>> queue) {
        int sent = 0;
        while (sent < queue.Count) {
            int batch = Mathf.Min(FillPerFrame, queue.Count - sent);
            for (int i = 0; i < batch; i++) {
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm == null || lm.client == null || !lm.client.isConnected) yield break;
                    lm.client.Send(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                        PlayerNumber = queue[sent].Key, PointType = queue[sent].Value, AlwaysAward = true
                    });
                } catch (Exception ex) {
                    MainPlugin.ModLogger.LogWarning("[补分] 发送失败: " + ex.Message);
                }
                sent++;
            }
            yield return null; //每帧发一批，避免网络风暴
        }
    }

}
}
