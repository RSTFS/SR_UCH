// 关卡工具页（侧栏「关卡」第 2 页）：重载关卡 / 广播方块快照 / 重载模式 / 重载保分补分 / 清场槽位 / 快照后处理补丁。
// 命名空间内 Level 会遮蔽游戏自带的 global::Level。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
namespace SR_UCH.Tweaks {
public partial class Level {
    private static ConfigEntry<ReloadMode> _reloadMode;


    public static void RenderTools() {
        GUILayout.Label(SRApi.T("- 关卡工具 -", "- Level tools -"), SRApi.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("level.reload", SRApi.T("重载关卡", "Reload level"),
            SRApi.T("房主把当前看到的方块都写进关卡快照, 然后真的重载一次当前关卡, 全员一起重新加载.\n重载完按快照把方块重建出来并广播, 玩家放的方块和能动的道具都不会丢.\n要不要顺手把分数补回来, 看下面那个重载模式.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host writes the current blocks into the snapshot and really reloads the level scene, so everyone reloads.\nBlocks are rebuilt from the snapshot and nothing is lost.\nWhether the score is filled back depends on the Reload mode below.\n Party/Creative matches only, host only."),
            () => Level.ReloadLevel(), SRApi.Ctl.Sc(170), SRApi.Ctl.Sc(30))) {
            Level.ReloadLevel();
        }
        ConfigEntryBase rlm = SRApi.Ctl.FindEntry("Level", "Reload Mode");
        SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("重载模式", "Reload mode"),
            SRApi.T("重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按原类型分块给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks kept; the host fills scores so everyone sees the same score/types next tally.\nKeep blocks only (skip fill) = blocks kept, score resets.")),
            rlm, SRApi.Ctl.Sc(140), SRApi.Ctl.Sc(52));
        if (rlm != null) SRApi.Ctl.RenderControl(rlm);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SRApi.Ctl.HotkeyButton("level.broadcast", SRApi.T("广播方块快照", "Broadcast snapshot"),
            SRApi.T("房主把当前看到的方块连位置, 旋转和属性一起打包广播出去, 全员按房主那边的样子重建方块.\n局内偶尔出现的方块自己这边看不见或者不同步, 就用这个修.\n不会重载场景: 对局进度和分数都留着, 全员会卡一下, 然后方块就回来了.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host packages the blocks they see and broadcasts them, so everyone rebuilds from the host snapshot.\nUse this when blocks occasionally disappear or desync on your side mid-match.\nNo scene reload: progress and scores stay.\n Party/Creative matches only, host only."),
            () => Level.BroadcastSnapshot(), SRApi.Ctl.Sc(170), SRApi.Ctl.Sc(30))) {
            Level.BroadcastSnapshot();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SRApi.Ctl.Sc(4));
    }

		// 重载关卡模式：保留方块与分数 / 仅保留方块（分数重置）。
		public enum ReloadMode {
			KeepScore,
			KeepBlocksOnly
		}

		private static bool _reloadBusy; // 重载关卡进行中，防重复触发

		private static float _reloadBusyAt = -1f; // 加锁时刻，用于识别协程被销毁导致的锁残留

		private static void SetReloadBusy(bool busy)
		{
			_reloadBusy = busy;
			_reloadBusyAt = busy ? Time.unscaledTime : -1f;
		}

		// 锁残留自愈：协程被销毁时无人复位，超过 45 秒仍锁着即视为残留。
		private static bool ReloadLockStuck()
		{
			return _reloadBusy && _reloadBusyAt >= 0f && Time.unscaledTime - _reloadBusyAt > 45f;
		}

		// 两个入口共用的防重入判定：重载中提示并拦下，锁残留则放锁放行本次操作。
		private static bool ReloadBlocked()
		{
			if (!_reloadBusy) return false;
			if (ReloadLockStuck()) {
				SetReloadBusy(false);
				SRApi.LogWarn("[重载] 检测到残留的忙碌锁（超过 45 秒未复位），已自动放锁");
				return false;
			}
			SRApi.Notify(SRApi.T("重载进行中，请稍候", "Reload in progress"));
			return true;
		}

		private static float _lastSnapshotAt = -999f; // 上次广播快照的时刻（冷却用）

		private static float _reloadLoadedAt = -1f; // 重载场景加载完成（FadeOut）时刻，用于确定何时可清 levelPortalXml

		private static float _reloadStartedAt = -1f; // 发起 ReloadScene 的时刻（超时兜底）

		public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

		// 仅派对 / 创意 / 自由建造关卡内生效（统一走 SR.GateModeAllows）。
		private static bool IsPartyCreativeOrFreeplay()
		{
			// 统一模式门控，IgnoreModeLimit 豁免已内置
			return SR.GateModeAllows(SR.ModeMask.Party | SR.ModeMask.Creative | SR.ModeMask.Freeplay);
		}

		// 真正重载当前关卡场景：重载前把房主快照写入 QuickSaver.levelPortalXml（static），重载后由房主 OnSetupStartLevel 读取并原生广播，全员重建方块。
		// 仅房主可调用（ReloadScene 是服务器操作），仅派对 / 创意 / 自由建造关卡内生效。
		public static void ReloadLevel()
		{
			ReloadLevelImpl(SRApi.T("重载关卡", "Reload Level"));
		}

		// 重载关卡实现：模式限制 + 快照写入 levelPortalXml。
		private static void ReloadLevelImpl(string feature)
		{
			// 启动成功后 _reloadBusy 由协程末尾复位；此处提前放锁会让连点并发重载。
			bool coroutineStarted = false;
			try
			{
				if (!SRApi.GateMaster)
				{
					SRApi.Notify(SR.Msg.MasterOff());
					return;
				}
				if (!IsPartyCreativeOrFreeplay())
				{
					SRApi.Notify(feature + SRApi.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
					return;
				}
				// 房主判定走统一门控 GateHost（= IsHost || OverrideHost）
				if (!SRApi.GateHost)
				{
					SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				// 必须服务器权威：GateHost 会放行开了无视房主限制的房客，但 NetworkServer.SendToAll 与 ReloadScene / CompressAndSendSnapshotBytes 在客户端都不执行，会假成功。
				if (!NetworkServer.active)
				{
					SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					SRApi.Notify(SRApi.T("未进入有效对局", "No active match"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					SRApi.Notify(SRApi.T("当前对局不支持该操作", "Unsupported in the current match"));
					return;
				}
				GameState gs = GameState.GetInstance();
				if (gs == null)
				{
					SRApi.Notify(SRApi.T("游戏状态尚未就绪", "Game state not ready"));
					return;
				}
				if (ReloadBlocked()) return; // 含锁残留自愈
				// 快照刚广播过（3 秒内）先别重载，等方块重建落定
				if (Time.unscaledTime - _lastSnapshotAt < 3f) {
					SRApi.Notify(SRApi.T("快照刚广播，请稍后重载", "Snapshot just broadcast; retry later"));
					return;
				}
				SetReloadBusy(true);
				// 协程只在末尾复位 _reloadBusy；本方法所有提前 return 都在 StartCoroutine 之前，必须逐条手动复位，否则与广播快照共用的这把锁会永久锁死。
				// 生成房主当前快照并写入 QuickSaver.levelPortalXml（static），重载后由 OnSetupStartLevel 读取并原生广播全员。
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
				{
					SetReloadBusy(false); // 失败路径复位
					SRApi.Notify(SRApi.T("当前场景没有可同步的方块", "No blocks to sync in the current scene"));
					return;
				}
				try
				{
					QuickSaver.levelPortalXml = doc.OuterXml; // public static 字段，直接写
				}
				catch
				{
					SetReloadBusy(false); // 失败路径复位
					SRApi.Notify(SRApi.T("快照写入失败", "Failed to write the snapshot"));
					return;
				}
				// KeepScore 先置保分标志：重载后按原类型广播补分，下回合结算显示；KeepBlocksOnly 不置标志，分数重置。
				if (ReloadKeepsScore) MarkPreserveScores();
				// GameSettings 取不到必须中止重载并解锁：客户端收不到 PrepareToReloadScene 会与房主脱同步。
				GameSettings gsForMode = GameSettings.GetInstance();
				if (gsForMode == null)
				{
					SRApi.Notify(SRApi.T("游戏加载中，请稍后再试",
						"Still loading; try again later"));
					SetReloadBusy(false);
					return;
				}
				try
				{
					MsgPrepareToReloadScene msg = new MsgPrepareToReloadScene
					{
						reloadToMode = gsForMode.GameMode,
						snapshotInfo = gs.currentSnapshotInfo
					};
					NetworkServer.SendToAll(NetMsgTypes.PrepareToReloadScene, msg);
				}
				catch (Exception __ex) { SRApi.Guard.Log("Level.SendToAll", __ex); }
				if (LoadingInterstitialSplash.Instance != null)
				{
					LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
					LoadingInterstitialSplash.Instance.FadeIn();
				}
				// 成败提示在协程内真正执行完才发出；模式随参数传入，协程内不再取 GameSettings（未判空会 NRE）。
				lm.StartCoroutine(ReloadSceneRoutine(feature, gsForMode.GameMode));
				coroutineStarted = true; // 复位交给协程末尾
			}
			catch (Exception ex)
			{
				// 仅协程未启动（StartCoroutine 之前的异常）时手动复位，否则锁残留
				if (!coroutineStarted) SetReloadBusy(false);
				SRApi.LogWarn((object)(feature + "失败: " + ex.Message));
			}
		}

		private static IEnumerator ReloadSceneRoutine(string feature, GameState.GameMode toMode)
		{
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame(); // 等加载画面 FadeIn 生效
			bool ok = false;
			try
			{
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm != (UnityEngine.Object)null)
				{
					// 与游戏原生重载一致：先重置方块 ID 序列，否则重载后新 ID 会与快照里的 parentID/mainID 错位。
					try { Placeable.SetInitialSequenceID(0); } catch (Exception __ex) { SRApi.Guard.Log("重载前重置方块 ID 序列", __ex); }
					lm.ReloadScene(toMode);
					ok = true;
				}
			}
			catch (Exception ex)
			{
				SRApi.LogWarn((object)("重载场景失败: " + ex.Message));
			}
			if (ok)
			{
				SRApi.Notify(feature + SRApi.T("：重载中，方块保留，", ": reloading, blocks kept, ") +
					(ReloadKeepsScore ? SRApi.T("分数保留", "scores kept") : SRApi.T("分数重置", "scores reset")));
			}
			else
			{
				SRApi.Notify(feature + SRApi.T("：重载失败", ": reload failed"));
			}
			_reloadStartedAt = Time.unscaledTime;
			_reloadLoadedAt = -1f; // 等待 LoadingInterstitialSplash.FadeOut 置位
			// 新场景加载完成后再等 2 秒才清 levelPortalXml 并复位 busy；FadeOut 不触发时用 25 秒硬上限兜底。
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
			SetReloadBusy(false);
		}

		// 重载场景加载完成（FadeOut，由 SR.Reload.OnLoadEnd 调用）：记录时刻供 ReloadSceneRoutine 决定何时清 levelPortalXml。
		public static void OnReloadSceneLoaded()
		{
			_reloadLoadedAt = Time.unscaledTime;
		}

		// 清掉写入的 QuickSaver.levelPortalXml（static）
		private static void ClearPortalXml()
		{
			try
			{
				QuickSaver.levelPortalXml = null;
			}
			catch (Exception __ex) { SRApi.Guard.Log("Level.Field", __ex); }
		}

		// 广播方块快照：房主重发当前关卡快照，全员按房主视角重建方块。
		public static void BroadcastSnapshot()
		{
			RebuildBlocksFromHost(SRApi.T("广播方块快照", "Broadcast Snapshot"));
		}

		// 生成房主当前快照并经 CompressAndSendSnapshotBytes 广播，全员按房主视角重建；不重载场景，分数保留；仅房主可调用。
		private static void RebuildBlocksFromHost(string feature)
		{
			try
			{
				if (!SRApi.GateMaster)
				{
					SRApi.Notify(SR.Msg.MasterOff());
					return;
				}
				if (!IsPartyCreativeOrFreeplay())
				{
					SRApi.Notify(feature + SRApi.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
					return;
				}
				// 房主判定走统一门控 GateHost（= IsHost || OverrideHost）
				if (!SRApi.GateHost)
				{
					SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				// 必须服务器权威：GateHost 会放行开了无视房主限制的房客，但 SendToAll 与 CompressAndSendSnapshotBytes 在客户端都不执行，会假成功。
				if (!NetworkServer.active)
				{
					SRApi.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					SRApi.Notify(SRApi.T("当前不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					SRApi.Notify(SRApi.T("未进入有效对局", "No active match"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					SRApi.Notify(SRApi.T("当前对局不支持该操作", "Unsupported in the current match"));
					return;
				}
				// 重载进行中不放广播快照（与场景重载共用同一把锁）
				if (ReloadBlocked()) return; // 含锁残留自愈
				float _now = Time.unscaledTime;
				if (_now - _lastSnapshotAt < 3f) { SRApi.Notify(SRApi.T("快照广播冷却中，请稍后", "Snapshot broadcast on cooldown")); return; }
				_lastSnapshotAt = _now;
				// 生成房主当前视角的完整快照 XML（含位置/旋转/缩放/胶水 parentID/mainID 等属性）
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null)
				{
					SRApi.Notify(SRApi.T("快照生成失败", "Failed to generate the snapshot"));
					return;
				}
				string xml = doc.OuterXml;
				if (string.IsNullOrEmpty(xml))
				{
					SRApi.Notify(SRApi.T("快照内容为空", "The snapshot is empty"));
					return;
				}
				// 走游戏原生机制广播：房主压缩 -> RPC 全员 -> LoadSnapshotFromXmlDocument 重建；不重载场景，分数保留。
				xml = StripDestroyedNodes(xml); // <destroyed> 节点会导致整批解析中断，清场由 FallbackClearPlacedBlocks 负责
				SendSnapshotXml(gc, xml, feature);
			}
			catch (Exception ex)
			{
				SRApi.LogWarn((object)(feature + "失败: " + ex.Message));
			}
		}

		// SR 自己发起的快照加载标记，与 AfterLoadSnapshot 的门闩配对。
		// 客户端进程拿不到该静态字段，故房主发送时在 XML 根节点写 srSnapshot="1"，判据为门闩 || XML 标记。
		private static bool _srSnapshotLoad;

		// 给快照 XML 根节点加 srSnapshot="1"（幂等；解析失败原样返回）
		internal static string MarkSnapshotXml(string xml) {
			try {
				if (string.IsNullOrEmpty(xml)) return xml;
				System.Xml.XmlDocument d = new System.Xml.XmlDocument();
				d.LoadXml(xml);
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
					selfDoc.LoadXml(xml);
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
				SRApi.Notify(SR.Msg.MasterOff());
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

		// 对外槽位：调用方经 SR.Require 按名取用，不引用 Level 类型，取不到即降级。
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
                    System.Collections.Generic.HashSet<Placeable> initPcs = InitialLevelPieces(__instance);
                    List<Placeable> built = new List<Placeable>();
                    foreach (Placeable bp in Placeable.AllPlaceables) {
                        // 必须过滤 MarkedForDestruction：上一轮残留的死方块（destroyMarkedPieces 未回收）会被误算进 built。
                        if (bp == null || bp.MarkedForDestruction || initPcs.Contains(bp)) continue;
                        built.Add(bp);
                    }
                    // 经槽位取用以避免跨功能编译期依赖；取不到只丢名牌与归属，不影响重建。
                    Action<List<Placeable>> attributeToHost = SR.Require<Action<List<Placeable>>>(SlotAttributeToHost);
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
		        doc.LoadXml(xml);
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
    private static float _reloadGraceUntil = 0f;
    [HarmonyPatch(typeof(LobbyManager), "ReloadScene")]
    [HarmonyPrefix]
    static void OnReloadSceneStart() {
        try {
                _reloadGraceUntil = Time.unscaledTime + 20f;
        } catch (Exception __ex) { SRApi.Guard.Log("重载宽限起点", __ex); }
    }
    [HarmonyPatch(typeof(LobbyManager), "DisconnectBrokenClients")]
    [HarmonyPrefix]
    static bool SkipDisconnectDuringReload() {
        try {
                return Time.unscaledTime >= _reloadGraceUntil;
        } catch (Exception __ex) { SRApi.Guard.Log("重载期跳过断连检查", __ex); return true; }
    }

    // 重载后保留分数：重载前备份各玩家的分块列表，重载后房主按原类型逐个重放 PointAwarded，全员下回合 tally 恢复。
    // 不恢复 playerTotal（ScoreKeeper.Setup() 会清空 playerTotal 与分块）。
    private static bool _preserveScoresOnReload = false;
    // 备份：networkNumber -> 分块列表（含类型）
    private static Dictionary<int, List<PointBlock>> _scoreBackupBlocks = null;
    // 补分重放后置位：下次结算的 ClearNewPointBlocks 跳过清除，保证重放分块正常显示与 tally。
    private static bool _refillPending = false;
    // 外部加分（MsgPointAwarded.AlwaysAward=true）的分块标记：被清除时缓存，重载备份时补回。
    private static HashSet<int> _externAwardedKeys = new HashSet<int>();
    // 结算时被 Clear 的外部加分分块，重载备份时合并进 _scoreBackupBlocks
    private static List<PointBlock> _clearedExternBlocks = new List<PointBlock>();
    // 备份：networkNumber -> handicap，重载后由房主显式写回 SyncVar
    private static Dictionary<int, int> _handicapBackup = null;

    // 保分标志的有效期：标志正常由 ScoreKeeper.Setup 消费，重载失败时会一直挂着，之后普通开局会被误当重载保分。
    // 超时（PreserveStaleAfter）即视为失效，否则会把陈旧备份分块再补一遍导致分数翻倍。
    private static float _preserveMarkedAt = -1f;
    private const float PreserveStaleAfter = 90f;

    // 重载前置保分标志（模式一：允许补分）
    public static void MarkPreserveScores() {
        _preserveScoresOnReload = true;
        _preserveMarkedAt = Time.unscaledTime;
    }

    [HarmonyPatch(typeof(GameControl), "handleEvent")]
    [HarmonyPrefix]
    static void OnPrepareReloadMessage(GameEvent.GameEvent e) {
        try {
            // 收到 PrepareToReloadScene 只重置补分保护，不置补分标志，否则外部工具发起的重载会再补一份导致分数翻倍。
            if (e is GameEvent.NetworkMessageReceivedEvent nm && nm.Message.msgType == NetMsgTypes.PrepareToReloadScene) {
                _refillPending = false;
                // 折扣备份与补分模式无关；恢复仅房主执行，房客端备份无副作用。
                BackupHandicaps();
            }
        } catch (Exception __ex) { SRApi.Guard.Log("处理 PrepareToReloadScene", __ex); }
    }

    // 备份所有在线玩家的 handicap（从 LobbyPlayer 读服务器权威值）。
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
                } catch (Exception __ex) { SRApi.Guard.Log("读取玩家 handicap", __ex); }
            }
            if (_handicapBackup.Count > 0) {
                SRApi.LogInfo("[折扣] 重载前备份 " + _handicapBackup.Count + " 名玩家 handicap");
            }
        } catch (Exception __ex) { SRApi.Guard.Log("备份 handicap", __ex); }
    }

    // 重载完成后房主写回备份的 handicap（LobbyPlayer + GamePlayer 的 SyncVar，服务器赋值自动广播全员）。
    private static void RestoreHandicapsIfAny() {
        if (_handicapBackup == null || _handicapBackup.Count == 0) return;
        Dictionary<int, int> backup = _handicapBackup;
        _handicapBackup = null;
        try {
            if (!SRApi.HasServer) return; // 仅房主可写 SyncVar
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.PlayerTracker == null) return;
            foreach (KeyValuePair<int, int> kv in backup) {
                try {
                    LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(kv.Key);
                    if (lp != null) lp.Networkhandicap = kv.Value;
                } catch (Exception __ex) { SRApi.Guard.Log("恢复 LobbyPlayer handicap", __ex); }
                try {
                    GamePlayer gp = lm.PlayerTracker.GetGamePlayer(kv.Key);
                    if (gp != null) gp.NetworkHandicap = kv.Value;
                } catch (Exception __ex) { SRApi.Guard.Log("恢复 GamePlayer handicap", __ex); }
            }
            SRApi.LogInfo("[折扣] 重载后恢复 " + backup.Count + " 名玩家 handicap");
        } catch (Exception __ex) { SRApi.Guard.Log("恢复 handicap", __ex); }
    }

    // ClearNewPointBlocks 会删掉非 AlwaysAward 分块：补分重放后（_refillPending）跳过清除；正常结算只缓存外部加分分块，原生未 tally 的不缓存以免多计分。
    [HarmonyPatch(typeof(ScoreKeeper), "ClearNewPointBlocks")]
    [HarmonyPrefix]
    static bool ClearNewPointBlocksPrefix(ScoreKeeper __instance) {
        try {
            if (_refillPending) {
                // 跳过清除分支同样要缓存（去重后合并），否则同一批 AlwaysAward 分块会在下次重载备份时被补回两次。
                try {
                    if (__instance != null && __instance.newPointBlocks != null) {
                        // 按 Key 线性去重（条数受 FillTotalCap 限制）
                        for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                            PointBlock pb = __instance.newPointBlocks[i];
                            if (pb == null) continue;
                            int k = Key(pb);
                            bool dup = false;
                            for (int j = 0; j < _clearedExternBlocks.Count; j++) {
                                if (_clearedExternBlocks[j] != null && Key(_clearedExternBlocks[j]) == k) { dup = true; break; }
                            }
                            if (!dup) _clearedExternBlocks.Add(pb);
                        }
                    }
                } catch (Exception __cex) { SRApi.Guard.Log("缓存补分分块", __cex); }
                // 补分重放后的第一次结算跳过清除，保留全部分块
                return false;
            }
            if (__instance == null || __instance.newPointBlocks == null) return true;
            for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                PointBlock pb = __instance.newPointBlocks[i];
                if (pb == null) continue;
                if (!pb.AlwaysAward && _externAwardedKeys.Contains(Key(pb))) {
                    // 外部加分的非 AlwaysAward 分块：缓存，重载备份时补回
                    _clearedExternBlocks.Add(pb);
                }
            }
            if (_clearedExternBlocks.Count > 500) {
                _clearedExternBlocks.RemoveRange(0, _clearedExternBlocks.Count - 500);
            }
        } catch (Exception __ex) { SRApi.Guard.Log("缓存被清除的外部加分分块", __ex); }
        return true;
    }

    // 分块标记键：(playerNumber << 8) | (int)type
    private static int Key(PointBlock pb) { return (pb.playerNumber << 8) | (int)pb.type; }
    private static int Key(int playerNumber, PointBlock.pointBlockType type) { return (playerNumber << 8) | (int)type; }

    // 标记外部加分分块：外部模块加分与补分重放都发 AlwaysAward=true，原生名次分等为 false。
    [HarmonyPatch(typeof(ScoreKeeper), "handleEvent")]
    [HarmonyPrefix]
    static void OnPointAwardedMessage(ScoreKeeper __instance, global::GameEvent.GameEvent e) {
        try {
            if (e == null || e.GetType() != typeof(GameEvent.NetworkMessageReceivedEvent)) return;
            GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
            if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.PointAwarded) return;
            // reader 已被 GameState.distributeMessage 消费，再 ReadMessage 会越界，必须用事件里已反序列化的字段。
            MsgPointAwarded msg = nm.ReadMessage as MsgPointAwarded;
            if (msg != null && msg.AlwaysAward) {
                _externAwardedKeys.Add(Key(msg.PlayerNumber, msg.PointType));
            }
        } catch (Exception __ex) { SRApi.Guard.Log("标记外部加分分块", __ex); }
    }

    // 分块 tally 后清除 _refillPending 与外部加分标记，恢复正常清除。
    [HarmonyPatch(typeof(ScoreKeeper), "TallyPointBlockAllPlayers")]
    [HarmonyPostfix]
    static void TallyPostfix() {
        try {
                _refillPending = false;
                _externAwardedKeys.Clear();
                _clearedExternBlocks.Clear();
        } catch (Exception __ex) { SRApi.Guard.Log("结算收尾", __ex); }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPrefix]
    static void ScoreSetupPrefix(ScoreKeeper __instance) {
        if (!_preserveScoresOnReload) return;
        try {
            // 备份 historyPointBlocks（已 tally）+ newPointBlocks（本回合未结算）。
            List<PointBlock> blocks = new List<PointBlock>();
            try {
                FieldInfo hf = SRApi.Ref.Field(typeof(ScoreKeeper), "historyPointBlocks", "重载保分（历史分块）");
                List<PointBlock> hist = hf != null ? hf.GetValue(__instance) as List<PointBlock> : null;
                if (hist != null) blocks.AddRange(hist);
            } catch (Exception ex) {
                SRApi.LogWarn("[保分] 备份 historyPointBlocks 失败: " + ex.Message);
            }
            try {
                List<PointBlock> npb = __instance.newPointBlocks; // public 字段，直接读
                if (npb != null) blocks.AddRange(npb);
            } catch (Exception ex) {
                SRApi.LogWarn("[保分] 备份 newPointBlocks 失败: " + ex.Message);
            }
            if (_clearedExternBlocks != null && _clearedExternBlocks.Count > 0) {
                // 结算时被 Clear 的外部加分分块：合并进备份，重载后补回
                blocks.AddRange(_clearedExternBlocks);
                SRApi.LogInfo("[保分] 合并被清除的外部加分分块 " + _clearedExternBlocks.Count + " 条");
                _clearedExternBlocks.Clear();
            }
            if (blocks.Count == 0) {
                SRApi.LogWarn("[保分] 重载时无可备份的分块（history+new+外部加分均为空）");
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
            SRApi.LogInfo("[保分] 重载备份分块 " + blocks.Count + " 条，涉及 " + _scoreBackupBlocks.Count + " 名玩家");
        } catch (Exception ex) {
            SRApi.LogWarn("[保分] 备份分块失败: " + ex.Message);
        }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPostfix]
    static void ScoreSetupPostfix(ScoreKeeper __instance) {
        try {
                // 重载保分模式：下次结算跳过 ClearNewPointBlocks；超时未消费的保分标志按普通开局处理，避免补回陈旧分块。
                bool fresh = _preserveScoresOnReload && _preserveMarkedAt >= 0f
                    && (Time.unscaledTime - _preserveMarkedAt) <= PreserveStaleAfter;
                if (_preserveScoresOnReload && !fresh) {
                    SRApi.LogWarn("[保分] 保分标志已过期（" + ((int)(Time.unscaledTime - _preserveMarkedAt))
                        + " 秒没被结算），本次按普通开局处理");
                }
                _refillPending = fresh;
                // 只清标志，不恢复分块（分块图标由 FillScoresIfPending 的补分广播恢复）
                _preserveScoresOnReload = false;
                _preserveMarkedAt = -1f;
        } catch (Exception __ex) { SRApi.Guard.Log("保分标志复位", __ex); }
    }

    // 重载后广播补分：房主按备份的原类型逐个发 PointAwarded（AlwaysAward=true），下回合 tally 进 playerTotal。
    // 防网络风暴：上限 FillTotalCap 条，每帧 FillPerFrame 条。
    private const int FillPerFrame = 30;
    private const int FillTotalCap = 300;
    private static void FillScoresIfPending() {
        if (_scoreBackupBlocks == null || _scoreBackupBlocks.Count == 0) return;
        // 只以服务器权威把关（SRApi.HasServer），不用 UI 总开关，否则关掉总开关会让分数补不回来。
        if (!SRApi.HasServer) return;
        Dictionary<int, List<PointBlock>> backup = _scoreBackupBlocks;
        _scoreBackupBlocks = null;
        try {
            // 必须走 NetworkServer.SendToAll（服务器 -> 全员）；lm.client.Send 是 client->server 方向，房客的 ScoreKeeper 收不到。
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.client == null || !lm.client.isConnected) return;
            List<KeyValuePair<int, PointBlock.pointBlockType>> queue = new List<KeyValuePair<int, PointBlock.pointBlockType>>();
            // 备份含跨回合累积的 historyPointBlocks，长局易超上限；截断时必须告警丢了多少。
            int total = 0;
            foreach (KeyValuePair<int, List<PointBlock>> kv in backup) {
                List<PointBlock> list = kv.Value;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++) if (list[i] != null) total++;
            }
            bool capped = false;
            foreach (KeyValuePair<int, List<PointBlock>> kv in backup) {
                List<PointBlock> list = kv.Value;
                if (list == null || list.Count == 0) continue;
                foreach (PointBlock pb in list) {
                    if (pb == null) continue;
                    if (queue.Count >= FillTotalCap) { capped = true; break; } // 全局上限
                    queue.Add(new KeyValuePair<int, PointBlock.pointBlockType>(kv.Key, pb.type));
                }
                if (capped) break; // 已达上限，外层同步停止
            }
            if (capped) {
                SRApi.LogWarn("[补分] 备份分块 " + total + " 条超过上限 " + FillTotalCap
                    + "，仅补回前 " + queue.Count + " 条（" + (total - queue.Count) + " 条被丢弃，重载后分数会偏少）");
            }
            if (queue.Count == 0) return;
            lm.StartCoroutine(FillScoresCoroutine(queue));
            SRApi.LogInfo("[补分] 队列 " + queue.Count + " 条，分帧发送（每帧 " + FillPerFrame + " 条）");
        } catch (Exception ex) {
            SRApi.LogWarn("[补分] 初始化失败: " + ex.Message);
        }
    }

    private static System.Collections.IEnumerator FillScoresCoroutine(List<KeyValuePair<int, PointBlock.pointBlockType>> queue) {
        int sent = 0;
        while (sent < queue.Count) {
            int batch = Mathf.Min(FillPerFrame, queue.Count - sent);
            for (int i = 0; i < batch; i++) {
                try {
                    // 服务器权威广播：房客的 ScoreKeeper 收到后自行进入 newPointBlocks，非权威端等广播即可。
                    if (!NetworkServer.active) { SRApi.LogWarn("[补分] 已非服务器权威，中止补分"); yield break; }
                    NetworkServer.SendToAll(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                        PlayerNumber = queue[sent].Key, PointType = queue[sent].Value, AlwaysAward = true
                    });
                } catch (Exception ex) {
                    SRApi.LogWarn("[补分] 发送失败: " + ex.Message);
                }
                sent++;
            }
            yield return null; // 每帧发一批
        }
    }
}
}