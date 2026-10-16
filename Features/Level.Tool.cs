// 关卡 · 关卡工具页（侧栏「关卡」栏目第 2 页）。重载关卡 / 广播方块快照 / 重载模式 / 重载保分补分，
//   以及清场槽位与快照后处理补丁。原则：UI 在哪功能在哪 —— 这一页的全部实现都在本文件。
// **命名提醒**见 Level.cs 顶部：本命名空间里的 Level 会遮住游戏自带的 global::Level。
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
        GUILayout.Label(SR.T("- 关卡工具 -", "- Level tools -"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        if (SR.Ctl.HotkeyButton("level.reload", SR.T("重载关卡", "Reload level"),
            SR.T("房主把当前看到的方块都写进关卡快照, 然后真的重载一次当前关卡, 全员一起重新加载.\n重载完按快照把方块重建出来并广播, 玩家放的方块和能动的道具都不会丢.\n要不要顺手把分数补回来, 看下面那个重载模式.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host writes the current blocks into the snapshot and really reloads the level scene, so everyone reloads.\nBlocks are rebuilt from the snapshot and nothing is lost.\nWhether the score is filled back depends on the Reload mode below.\n Party/Creative matches only, host only."),
            () => Level.ReloadLevel(), SR.Ctl.Sc(170), SR.Ctl.Sc(30))) {
            Level.ReloadLevel();
        }
        //重载模式下拉框放在重载关卡按钮右边（同一行）
        ConfigEntryBase rlm = SR.Ctl.FindEntry("Level", "Reload Mode");
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("重载模式", "Reload mode"),
            SR.T("重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按原类型分块给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置。", "Reload level mode:\nKeep blocks & score (allow fill) = blocks kept; the host fills scores so everyone sees the same score/types next tally.\nKeep blocks only (skip fill) = blocks kept, score resets.")),
            rlm, SR.Ctl.Sc(140), SR.Ctl.Sc(52));
        if (rlm != null) SR.Ctl.RenderControl(rlm);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));
        GUILayout.BeginHorizontal();
        if (SR.Ctl.HotkeyButton("level.broadcast", SR.T("广播方块快照", "Broadcast snapshot"),
            SR.T("房主把当前看到的方块连位置, 旋转和属性一起打包广播出去, 全员按房主那边的样子重建方块.\n局内偶尔出现的方块自己这边看不见或者不同步, 就用这个修.\n不会重载场景: 对局进度和分数都留着, 全员会卡一下, 然后方块就回来了.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host packages the blocks they see and broadcasts them, so everyone rebuilds from the host snapshot.\nUse this when blocks occasionally disappear or desync on your side mid-match.\nNo scene reload: progress and scores stay.\n Party/Creative matches only, host only."),
            () => Level.BroadcastSnapshot(), SR.Ctl.Sc(170), SR.Ctl.Sc(30))) {
            Level.BroadcastSnapshot();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));
    }

		//重载关卡模式：KeepScore（保留方块和分数） / KeepBlocksOnly（仅保留方块，分数重置）
		public enum ReloadMode {
			KeepScore,
			KeepBlocksOnly
		}

		private static bool _reloadBusy; //重载关卡进行中（防重复触发）

		private static float _reloadBusyAt = -1f; //加锁时刻（用于识别"协程被销毁 -> 锁残留"）

		//加锁/解锁统一走这里：加锁时记时刻，解锁清掉。
		private static void SetReloadBusy(bool busy)
		{
			_reloadBusy = busy;
			_reloadBusyAt = busy ? Time.unscaledTime : -1f;
		}

		//锁自愈：正常协程最长约 27 秒（25 秒硬上限 + 2 秒等待），若协程被销毁（退出到菜单、
		//对象生命周期结束等）就没人来复位 -> 重载关卡与广播快照会永远提示"正在重载"。
		//超过 45 秒还锁着必然是残留，直接放锁。
		private static bool ReloadLockStuck()
		{
			return _reloadBusy && _reloadBusyAt >= 0f && Time.unscaledTime - _reloadBusyAt > 45f;
		}

		//两个入口共用的防重入判定：真在重载就提示并拦下；锁已残留则放锁放行本次操作。
		private static bool ReloadBlocked()
		{
			if (!_reloadBusy) return false;
			if (ReloadLockStuck()) {
				SetReloadBusy(false);
				SR.LogWarn("[重载] 检测到残留的忙碌锁（超过 45 秒未复位），已自动放锁");
				return false;
			}
			SR.Notify(SR.T("重载进行中，请稍候", "Reload in progress"));
			return true;
		}

		private static float _lastSnapshotAt = -999f; //广播快照冷却

		private static float _reloadLoadedAt = -1f; //重载场景加载完成(FadeOut)时刻，用于确定何时可清 levelPortalXml

		private static float _reloadStartedAt = -1f; //发起 ReloadScene 时刻（超时兜底）

		//重载是否保留分数（KeepScore = 保留，KeepBlocksOnly = 不保留）
		public static bool ReloadKeepsScore => _reloadMode == null || _reloadMode.Value == ReloadMode.KeepScore;

		//仅派对(PARTY)/创意(CREATIVE)/自由建造(FREEPLAY)局内生效。
		//  **V8**：原名 IsPartyOrCreative 与实现不符（ModeMask 里还有 Freeplay），
		//    注释/README 也跟着写错 -> 改名让"名字 = 行为"。
		private static bool IsPartyCreativeOrFreeplay()
		{
			//统一模式门控（IgnoreModeLimit 豁免已内置）
			return SR.GateModeAllows(SR.ModeMask.Party | SR.ModeMask.Creative | SR.ModeMask.Freeplay);
		}

		//真正重载当前关卡场景（ReloadScene -> 全员重新加载）：重载前把房主快照写入 QuickSaver.levelPortalXml（static），
		//重载后房主 OnSetupStartLevel 读取并原生广播，全员重建方块；分数按 SR 保分 patch 恢复。
		// 仅派对(PARTY)/创意(CREATIVE)局内生效；仅房主有效（ReloadScene 是服务器操作）。
		public static void ReloadLevel()
		{
			ReloadLevelImpl(SR.T("重载关卡", "Reload Level"));
		}

		//共享核心（与重载关卡一致的模式限制 + 快照写入 levelPortalXml）
		private static void ReloadLevelImpl(string feature)
		{
			//协程是否已启动：只有没启动成功时才需要在这里手动复位 _reloadBusy；
			//启动成功后复位交给协程末尾（ReloadSceneRoutine），此处提前放锁会让连点并发重载。
			bool coroutineStarted = false;
			try
			{
				if (!SR.GateMaster)
				{
					SR.Notify(SR.Msg.MasterOff());
					return;
				}
				if (!IsPartyCreativeOrFreeplay())
				{
					SR.Notify(feature + SR.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
					return;
				}
				//房主判定走统一门控 GateHost（= IsHost || OverrideHost）：
				//装了 EX 且开无视房主限制时房客也能执行；否则与 SR.IsHost 完全等价。
				if (!SR.GateHost)
				{
					SR.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				//**V1**：GateHost 会放行"开了无视房主限制"的房客，但这两个功能**必须服务器权威**：
				//  · 重载：NetworkServer.SendToAll 在客户端抛 "called on client"（被 catch 吞掉），
				//    且 LobbyManager.ReloadScene 第一句就是 `if (IsHost)` -> 同样空转；
				//  · 广播快照：CompressAndSendSnapshotBytes 第一句是 `if (!hasAuthority) return;`。
				//  两头都不执行，玩家却看到成功提示 = 假成功。这里提前按真实能力拦下。
				if (!NetworkServer.active)
				{
					SR.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					SR.Notify(SR.T("当前不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					SR.Notify(SR.T("未进入有效对局", "No active match"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					SR.Notify(SR.T("当前对局不支持该操作", "Unsupported in the current match"));
					return;
				}
				GameState gs = GameState.GetInstance();
				if (gs == null)
				{
					SR.Notify(SR.T("游戏状态尚未就绪", "Game state not ready"));
					return;
				}
				//防重入：在生成快照/写 levelPortalXml 之前就锁住，连点直接挡掉。
				if (ReloadBlocked()) return; //防重入（含锁残留自愈）
				//刚广播过快照（3 秒内）先别重载，等方块重建落定
				if (Time.unscaledTime - _lastSnapshotAt < 3f) {
					SR.Notify(SR.T("快照刚广播，请稍后重载", "Snapshot just broadcast; retry later"));
					return;
				}
				SetReloadBusy(true);
				// 锁的复位只在协程 ReloadSceneRoutine() 末尾（:265）；本方法里所有提前 return
				//都在 StartCoroutine 之前，不走协程 -> 必须在每条路径手动复位，否则 _reloadBusy
				//残留 true，之后重载关卡与广播快照（同一把锁）会永远提示"正在重载"，
				//两个功能彻底锁死、只能重启游戏。刚开局还没放方块时点重载即可触发。
				//1) 生成房主当前快照 -> 写 QuickSaver.levelPortalXml，重载后 OnSetupStartLevel 读取并原生广播全员
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null || string.IsNullOrEmpty(doc.OuterXml))
				{
					SetReloadBusy(false); //失败路径复位（见上方说明）
					SR.Notify(SR.T("当前场景没有可同步的方块", "No blocks to sync in the current scene"));
					return;
				}
				try
				{
					QuickSaver.levelPortalXml = doc.OuterXml;   // **Opt-50（T1）**：public static 字段（反编译 140364）-> 直接写
				}
				catch
				{
					SetReloadBusy(false); //失败路径复位（见上方说明）
					SR.Notify(SR.T("快照写入失败", "Failed to write the snapshot"));
					return;
				}
				//2) 发送 PrepareToReloadScene：KeepScore 先置保分标志（重载后按原类型广播补分，下回合结算显示）；
				//   KeepBlocksOnly 不置标志 -> 重载后分数重置。
				if (ReloadKeepsScore) MarkPreserveScores();
				// R396 S21：GameSettings 取不到时**必须中止重载**，不能继续往下走。
				//  原来它在 try 里 -> NRE 被 catch 吞掉、只记一条日志，然后**流程继续重载场景**
				//  -> 客户端没收到 PrepareToReloadScene（其 _refillPending 未清、handicap 未备份）
				//  -> 房客状态与房主脱同步。失败就明确退出并解锁。
				GameSettings gsForMode = GameSettings.GetInstance();
				if (gsForMode == null)
				{
					SR.Notify(SR.T("游戏加载中，请稍后再试",
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
				catch (Exception __ex) { SR.Guard.Log("Level.SendToAll", __ex); }
				if (LoadingInterstitialSplash.Instance != null)
				{
					LoadingInterstitialSplash.Instance.showLevelInfoNextLoad = true;
					LoadingInterstitialSplash.Instance.FadeIn();
				}
				//3) 真正重载场景（全员重新加载；房主 OnSetupStartLevel 自动加载+广播当前方块）
				//  **V2**：成败提示挪进协程（真正执行完之后才报）—— 原来写在 StartCoroutine 之后，
				//    等于"还没干活就先报成功"，与已修的加分假成功（P1-20）同类。
				//  **V3**：模式随参数传入，协程内不再二次 GameSettings.GetInstance()（那次没判空 -> NRE）。
				lm.StartCoroutine(ReloadSceneRoutine(feature, gsForMode.GameMode));
				coroutineStarted = true; //复位交给协程末尾（提前放锁会让连点并发重载）
			}
			catch (Exception ex)
			{
				//协程没起来（StartCoroutine 之前的异常）才手动复位，否则锁残留 -> 两个功能锁死。
				if (!coroutineStarted) SetReloadBusy(false);
				SR.LogWarn((object)(feature + "失败: " + ex.Message));
			}
		}

		private static IEnumerator ReloadSceneRoutine(string feature, GameState.GameMode toMode)
		{
			yield return new WaitForEndOfFrame();
			yield return new WaitForEndOfFrame(); //等加载画面 FadeIn 生效
			bool ok = false;
			try
			{
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm != (UnityEngine.Object)null)
				{
					//与游戏原生重载一致：先重置方块 ID 序列，否则重载后新 ID 从旧计数继续，
					//与 XML 快照里的 parentID/mainID 更容易错位（胶水/父子引用）。
					try { Placeable.SetInitialSequenceID(0); } catch (Exception __ex) { SR.Guard.Log("重载前重置方块 ID 序列", __ex); }
					//模式由调用方传入（V3）：原来在这里二次 GameSettings.GetInstance() 且没判空，
					//  取不到就 NRE -> ok 仍为 false -> 后面立刻 ClearPortalXml()，快照被提前清掉。
					lm.ReloadScene(toMode);
					ok = true;
				}
			}
			catch (Exception ex)
			{
				SR.LogWarn((object)("重载场景失败: " + ex.Message));
			}
			//**V2**：到这里才算"真的执行过"，成败由它决定提示
			if (ok)
			{
				SR.Notify(feature + SR.T("：重载中，方块保留，", ": reloading, blocks kept, ") +
					(ReloadKeepsScore ? SR.T("分数保留", "scores kept") : SR.T("分数重置", "scores reset")));
			}
			else
			{
				SR.Notify(feature + SR.T("：重载失败", ": reload failed"));
			}
			_reloadStartedAt = Time.unscaledTime;
			_reloadLoadedAt = -1f; //等待重载场景真正加载完成（LoadingInterstitialSplash.FadeOut）来置位
			//等新场景加载完成(FadeOut)再多等 2 秒才清 levelPortalXml 并复位 busy（慢房主不会丢方块）；
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
			SetReloadBusy(false);
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
				QuickSaver.levelPortalXml = null;   // **Opt-50（T1）**：同上，静态字段直接写
			}
			catch (Exception __ex) { SR.Guard.Log("Level.Field", __ex); }
		}

		//广播方块快照：房主重发当前关卡快照 -> 全员按房主视角重建方块（修复方块消失/不同步）。
		public static void BroadcastSnapshot()
		{
			RebuildBlocksFromHost(SR.T("广播方块快照", "Broadcast Snapshot"));
		}

		//共享核心：生成房主当前快照 -> CompressAndSendSnapshotBytes 广播 -> 全员按房主视角重建（含属性/胶水/玩家放置的）。
		//不重载场景 -> 分数保留； 仅派对(PARTY)/创意(CREATIVE)局内生效。
		private static void RebuildBlocksFromHost(string feature)
		{
			try
			{
				if (!SR.GateMaster)
				{
					SR.Notify(SR.Msg.MasterOff());
					return;
				}
				if (!IsPartyCreativeOrFreeplay())
				{
					SR.Notify(feature + SR.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
					return;
				}
				//房主判定走统一门控 GateHost（= IsHost || OverrideHost）：
				//装了 EX 且开无视房主限制时房客也能执行；否则与 SR.IsHost 完全等价。
				if (!SR.GateHost)
				{
					SR.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				//**V1**：GateHost 会放行"开了无视房主限制"的房客，但这两个功能**必须服务器权威**：
				//  · 重载：NetworkServer.SendToAll 在客户端抛 "called on client"（被 catch 吞掉），
				//    且 LobbyManager.ReloadScene 第一句就是 `if (IsHost)` -> 同样空转；
				//  · 广播快照：CompressAndSendSnapshotBytes 第一句是 `if (!hasAuthority) return;`。
				//  两头都不执行，玩家却看到成功提示 = 假成功。这里提前按真实能力拦下。
				if (!NetworkServer.active)
				{
					SR.NotifyT("仅房主可执行：", "Host only: ", feature);
					return;
				}
				LobbyManager lm = LobbyManager.instance;
				if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
				{
					SR.Notify(SR.T("当前不在对局中", "Not in a match"));
					return;
				}
				GameControl gc = lm.CurrentGameController as GameControl;
				if ((UnityEngine.Object)(object)gc == (UnityEngine.Object)null)
				{
					SR.Notify(SR.T("未进入有效对局", "No active match"));
					return;
				}
				QuickSaver qs = gc.GetComponent<QuickSaver>();
				if ((UnityEngine.Object)(object)qs == (UnityEngine.Object)null)
				{
					SR.Notify(SR.T("当前对局不支持该操作", "Unsupported in the current match"));
					return;
				}
				//重载进行中不放广播快照（防与场景重载打架）
				if (ReloadBlocked()) return; //防重入（含锁残留自愈）
				//冷却判断放在最前：冷却期内不再白做一次全量序列化
				float _now = Time.unscaledTime;
				if (_now - _lastSnapshotAt < 3f) { SR.Notify(SR.T("快照广播冷却中，请稍后", "Snapshot broadcast on cooldown")); return; }
				_lastSnapshotAt = _now;
				//生成房主当前视角的完整快照 XML（包含所有方块及属性：位置/旋转/缩放/胶水 parentID/mainID 等）
				System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
				if (doc == null)
				{
					SR.Notify(SR.T("快照生成失败", "Failed to generate the snapshot"));
					return;
				}
				string xml = doc.OuterXml;
				if (string.IsNullOrEmpty(xml))
				{
					SR.Notify(SR.T("快照内容为空", "The snapshot is empty"));
					return;
				}
				//用游戏原生机制广播：房主压缩 -> RPC 发给全员 -> LoadSnapshotFromXmlDocument 重建；不重载场景故分数保留。
				xml = StripDestroyedNodes(xml); // 清场已由 FallbackClearPlacedBlocks 直接做，快照里的 <destroyed> 只会引发解析中断
				SendSnapshotXml(gc, xml, feature);
			}
			catch (Exception ex)
			{
				SR.LogWarn((object)(feature + "失败: " + ex.Message));
			}
		}

		// **R396 A1**：SR 自己发起的快照加载标记（与 AfterLoadSnapshot 的门闩配对）。
		//  客户端进程拿不到这个静态字段，所以房主发送时在 XML 根节点写 srSnapshot="1"，
		//  AfterLoadSnapshot 用门闩 || XML 标记双重判据。
		private static bool _srSnapshotLoad;

		// **R396 A1**：给快照 XML 根节点加 srSnapshot="1"（幂等；解析失败原样返回，不影响广播）
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
				SR.LogWarn("[快照] 打标记失败，按原样发送: " + __ex.Message);
				return xml;
			}
		}

		// **R396 A1**：这次快照是不是 SR 发的？（房主看静态门闩，客户端看 XML 标记）
		private static bool IsSrSnapshot(System.Xml.XmlDocument doc) {
			if (_srSnapshotLoad) return true;
			try {
				if (doc != null && doc.DocumentElement != null
					&& doc.DocumentElement.GetAttribute("srSnapshot") == "1") return true;
			} catch { }
			return false;
		}

		//把任意快照 XML 广播给全员：房主压缩 -> RPC -> 各端 LoadSnapshotFromXmlDocument 重建。
		//广播方块快照与按 XML 构建共用这一段（两者只是 XML 来源不同）。
		private static void SendSnapshotXml(GameControl gc, string xml, string feature)
		{
			// **R396 A1**：给 XML 根节点打 srSnapshot 标记, 客户端靠它识别"这次是 SR 发的"，
			//  从而只在 SR 的快照上补发阶段事件，不碰游戏自己的开局/重载路径。
			xml = MarkSnapshotXml(xml);
			//大快照提示 + 序列化耗时。
			if (xml.Length > 1500000) SR.Notify(SR.T("快照较大（" + (xml.Length / 1000) + " KB），应用时可能卡顿", "Large snapshot (" + (xml.Length / 1000) + " KB); may hitch"));
			System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch(); _sw.Start();
			byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
			_sw.Stop();
			SR.LogInfo("[快照] 序列化耗时 " + _sw.ElapsedMilliseconds + "ms");
			SR.Notify(feature + SR.T("：广播快照（", ": broadcasting (") + xmlBytes.Length + SR.T(" 字节），全员按房主状态重建", " bytes), everyone rebuilds from the host's state"));
			gc.CompressAndSendSnapshotBytes(xmlBytes, delegate {
				SR.Notify(feature + SR.T("：方块已重建，分数保留", ": blocks rebuilt, scores kept"));
			});
			//CompressAndSendSnapshotBytes 只用 ClientRpc 发给其它客户端（UNET 的 ClientRpc 不在服务器/房主本地执行）
			//-> 房主自己不应用就看不到任何变化。这里补一次本地应用（构建/广播都受益）。
			try {
				QuickSaver selfQs = gc.GetComponent<QuickSaver>();
				if (selfQs != null) {
					System.Xml.XmlDocument selfDoc = new System.Xml.XmlDocument();
					selfDoc.LoadXml(xml);
					// 按游戏原生加载流程：先 QuickClear(true), 原生清场**并恢复关卡自带件**（RestoreSaveables 会重新
					//  登记/激活可移动、可变化道具的驱动）。缺这一步时，构建后这些道具要等下一次建造阶段被游戏自己
					//  重新注册才会动（表现为"进一次建造再到行动才会动"）, #10。
					try { selfQs.QuickClear(true); } catch (Exception __qcx) { SR.Guard.Log("清场.QuickClear", __qcx); }
					// **R396 A1**：置门闩 -> AfterLoadSnapshot 只为"SR 发起的这次加载"补发阶段事件。
					//  try/finally 保证异常也会复位，否则门闩泄漏后**后续游戏自己的开局也会被补发**。
					_srSnapshotLoad = true;
					try {
						if (!selfQs.LoadSnapshotFromXmlDocument(selfDoc))
							SR.Notify(SR.T("本地应用快照失败", "Failed to apply the snapshot locally"));
					} finally { _srSnapshotLoad = false; }
				}
			} catch (Exception __ex) { SR.Guard.Log("本地应用快照", __ex); }
		}

		//广播快照的前置检查（总开关 -> 模式 -> 房主 -> 对局 -> 控制器 -> 快照组件 -> 重载锁）。
		//返回 false = 已经把原因提示给用户了，调用方直接 return。
		private static bool TryGetSnapshotContext(string feature, out GameControl gc, out QuickSaver qs)
		{
			gc = null;
			qs = null;
			if (!SR.GateMaster)
			{
				SR.Notify(SR.Msg.MasterOff());
				return false;
			}
			if (!IsPartyCreativeOrFreeplay())
			{
				SR.Notify(feature + SR.T("：仅派对、创意或自由关卡内可用", ": available in Party, Creative and Freeplay levels only"));
				return false;
			}
			//房主判定走统一门控 GateHost（= IsHost || OverrideHost）：装了 EX 且开无视房主限制时房客也能执行。
			if (!SR.GateHost)
			{
				SR.NotifyT("仅房主可执行：", "Host only: ", feature);
				return false;
			}
			//**V1**：同上 —— 广播快照链路的服务器权威守卫（房客开了无视房主限制也会被放行到这里）。
			if (!NetworkServer.active)
			{
				SR.NotifyT("仅房主可执行：", "Host only: ", feature);
				return false;
			}
			LobbyManager lm = LobbyManager.instance;
			if ((UnityEngine.Object)(object)lm == (UnityEngine.Object)null)
			{
				SR.Notify(SR.T("当前不在对局中", "Not in a match"));
				return false;
			}
			GameControl g = lm.CurrentGameController as GameControl;
			if ((UnityEngine.Object)(object)g == (UnityEngine.Object)null)
			{
				SR.Notify(SR.T("未进入有效对局", "No active match"));
				return false;
			}
			QuickSaver q = g.GetComponent<QuickSaver>();
			if ((UnityEngine.Object)(object)q == (UnityEngine.Object)null)
			{
				SR.Notify(SR.T("当前对局不支持该操作", "Unsupported in the current match"));
				return false;
			}
			if (ReloadBlocked()) return false; //防重入（含锁残留自愈）
			gc = g;
			qs = q;
			return true;
		}

		//替换模式清场：把要清掉的方块写成快照的 <destroyed path="..."/> 节点。
		//游戏加载快照时由 ClearInitialDestroyedObjects() 在**每一端**按层级路径自行销毁 -> 房主房客天然一致。
		//判定沿用 QuickClear：Placed、非子件、且 (IsSaveable || !isLevelGeometry)（保住关卡几何体）。
		private static System.Reflection.FieldInfo _initialPiecesField;

		//关卡自带件集合（QuickSaver.initialLevelPlaceables，私有字段 -> 反射）。
		// 这些件（含可移动/可变化方块）**不能**列入清场目标：QuickClear 敢删是因为它随后会 RestoreSaveables
		//  把它们恢复，我们只删不恢复 -> 会把可动方块一起删掉（表现为"构建后方块不动/不变化"）。
		private static System.Collections.Generic.HashSet<Placeable> InitialLevelPieces(QuickSaver qs) {
			System.Collections.Generic.HashSet<Placeable> set = new System.Collections.Generic.HashSet<Placeable>();
			try {
				//走 SR.RefField：自带缓存（含"查过且没有"）+ 失败告警，与外部模块的硬约定一致。
				//外层 _initialPiecesField 保留只是为了少一次字典查找。
				if (_initialPiecesField == null) _initialPiecesField = SR.RefField(typeof(QuickSaver), "initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
				System.Collections.IEnumerable list = (_initialPiecesField != null && qs != null) ? _initialPiecesField.GetValue(qs) as System.Collections.IEnumerable : null;
				if (list == null) return set;
				foreach (object o in list) {
					if (o == null) continue;
					// **V5**：`QuickSaver.SaveablePiece` 是**公开嵌套类型**、`placeable` 是 **public 字段**
					//  （反编译 140294 / 140332），走反射纯属多余 —— 而且字段改名后这里静默拿不到，
					//  表现是"关卡自带件被误判成玩家放置 -> 被清场一起删掉"。改强类型，编译期就能发现改版。
					QuickSaver.SaveablePiece sp = o as QuickSaver.SaveablePiece;
					if (sp != null && sp.placeable != null) set.Add(sp.placeable);
				}
			} catch (Exception __ex) { SR.Guard.Log("收集关卡初始件", __ex); }
			return set;
		}
		//清除"玩家放置的"方块（全员可见，房客也能用）。
		// **改名说明**：以前叫 BroadcastSnapshotWithDestroyed（"当前快照 + 注入 <destroyed> 广播清场"），
		//  但那条路实测不稳（路径解析失败会中断整批），现在**只用逐块网络销毁** FallbackClearPlacedBlocks，
		//  快照里根本不再带 <destroyed>, 旧名字会让人以为还在走快照，故改为现状相符的名字。
		public static void ClearPlacedBlocks(List<Placeable> targets, string feature) {
			try {
				if (targets == null || targets.Count == 0) { SR.Notify(SR.T("地图上没有玩家放置的道具", "No player-placed props on the map")); return; }
				GameControl gc; QuickSaver qs;
				if (!TryGetSnapshotContext(feature, out gc, out qs)) return;
				FallbackClearPlacedBlocks(targets); //直接逐块网络销毁（不用快照）
			} catch (Exception __ex) { SR.Guard.Log("清场.逐块销毁", __ex); }
		}

		//对外槽位：别的功能/外部模块经 SR.Require 取用，不引用 Level 类型
		//（这样本功能独立成 DLL 或整段删除时，调用方只是"取不到 -> 降级"，不会编译不过）。
		private const string SlotClearPlacedBlocks = "Level.ClearPlacedBlocks"; // Action<List<Placeable>,string>
		// **R426**：需要 DestroyBlocks 把快照重建出来的方块登记成"房主放置"。
		//  名字与对方逐字一致（双方各写各的常量，互不引用 -> 不产生编译期依赖）。
		private const string SlotAttributeToHost = "DestroyBlocks.AttributeToHost"; // Action<List<Placeable>>
		// 替换模式：把目标快照里所有 <block> 的 placeableID 重置成高位不撞号的值。
		// 原因和叠加一样：RestoreSaveables 会拿 placeableID 去 initialLevelPlaceables 里找"可复用的现有对象"
		// （反编译 141311），存档 ID 一旦跟当前关卡自带件撞号，新方块就会被"吸收"进旧对象 ——
		// 表现为"替换没效果 / 换出来的方块不动"。只动 block、不动 moved（moved 要靠原 ID 匹配现有件）。
		// **另外要剥掉快照里所有 <destroyed> 节点**：清场现在由 FallbackClearPlacedBlocks（逐块网络销毁）负责，
		//   而残留的 <destroyed> 只要有一条在当前地图解析不了，游戏 ClearInitialDestroyedObjects() 就会
		//   抛异常中断整批（表现为"点一次只清一个"）。
        [HarmonyPatch(typeof(QuickSaver), "LoadSnapshotFromXmlDocument")]
        [HarmonyPostfix]
        private static void AfterLoadSnapshot(QuickSaver __instance, System.Xml.XmlDocument doc, ref bool __result) {
            try {
                // R396 A1（阻断级）双重判据：只有 SR 自己发起的快照才补发阶段事件 ——
                // 房主本地看静态门闩 _srSnapshotLoad，客户端看房主写在 XML 根节点上的 srSnapshot="1"。
                // 两者都不成立就是游戏自己的开局/重载路径，一概不碰。原实现无条件补发 StartPhaseEvent(PLACE/PLAY)，
                // 等于每次正常开局都在客户端伪造一次阶段事件；而阶段是服务器经 RpcStartPhase 权威下发的（还会同步
                // EndPhaseEvent 与 nextPhase），本 Postfix 只做三件事里的一件 -> 客户端提前切行动态、与服务器状态机错位。
                if (!IsSrSnapshot(doc)) return;
                if (!__result) return;   // **快照加载失败（关卡名/结构不符）时不要按阶段**
                GameControl gc = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (gc == null) return;
                // 原实现反射 GameControl."currentPhase", 该成员根本不存在（Cecil 读元数据已核：
                //  GameControl 有 nextPhase 字段和 Phase 属性，没有任何叫 currentPhase 的字段）。
                //  于是 phf == null 让整个 Postfix 直接 return -> 本方法从未真正执行过
                //  （修好之前，日志里永远见不到 "[快照] ..."）。
                //  现改为直接用 public 成员：Phase 属性 / StartPhaseEvent / GameEventManager.SendEvent
                //  （三者都是 public；EX 的 ExModule.Round 就是直接 new GameEvent.StartPhaseEvent(...)）。
                //  既修好了功能，也顺手消掉 3 处没登记的裸反射。
                GameControl.GamePhase phv = gc.Phase;
                GameEvent.GameEventManager.SendEvent(new GameEvent.StartPhaseEvent(phv));
                SR.LogInfo("[快照] 加载后补发阶段事件: " + phv);
                // **R413-6（用户报：广播方块后卡在建造模式与行动模式之间，角色能走但方块不动）**：
                // 原实现在这里**无条件再补发 PLACE + PLAY 两条 StartPhaseEvent**（"方案 B"），
                // 那等于伪造一次"进建造再进行动"的阶段边沿。可是：
                //   · 阶段在联机里是**服务器权威**的（RpcStartPhase 统一下发，还同步 EndPhaseEvent 与 nextPhase）；
                //   · VersusControl 推进阶段要看两个条件：RemainingPlacements[net-1] 归零（只由
                //     MsgPiecePlaced 驱动，而快照重建**根本不发这个消息**）与 AreAllPiecesReady()（遍历
                //     PlacedThisRound 要求每个都 Placed）；
                //   · 快照重建出来的方块不经过那条消息链，RemainingPlacements 仍停在 1。
                // 于是客户端按伪造的事件切到了行动态、服务器还卡在放置态 —— 表现正是
                // "角色能行动（客户端状态）但方块不动（服务器阶段没推进、放置光标/阶段事件没对齐）"。
                // **改法**：只补发一次**当前真实阶段**（上面那一条），不再伪造阶段边沿。
                // 阶段推进交回服务器自己的状态机：后续正常放置/超时都会走 MsgPiecePlaced 把计数补上。
                // 如果确实需要把玩家放置计数对齐，用 SR 自己的"放弃/清场"通道，别在这里伪造事件。
                    // **R415-4（用户报：广播方块快照依旧导致方块不动）**
                //
                // 上一轮（R414）我在这里补了 `rb.isKinematic = true` + `gameObject.layer = 9`，
                // 照抄的是 `WaitForPlacement` @72631-72636。但**方向补反了**，UnityPy 实测：
                //   · 方块 prefab 的 Rigidbody2D **默认就是 Kinematic**（`m_BodyType=1`、`gravityScale=0`）
                //     -> `isKinematic = true` 是**幂等的**，补了等于没补。
                //   · `layer = 9` 是 `Placed` 层，而 Physics2D 的 layer 碰撞矩阵实测：
                //         Placed(9) <-> Placed(9) = **0（自己和自己不碰）**
                //         Placed(9) <-> Fixed(8) = **0（不碰固定物）**
                //     prefab 根对象原本是 `layer 0 (Default)`，Default 与 Placed/Fixed **都碰**。
                //     所以把这句抄过来，等于**亲手把方块之间的碰撞关掉** —— 那才是"方块不动"的真正来源。
                //   · `WaitForPlacement` 那两句的前提是：那块方块**刚从光标手上放下来**，
                //     之前 `PickCursor.SetPiece` @71759 刚设过 `isKinematic = false`（激活物理），
                //     所以紧接着要设回 true。快照路径的方块**从没被拾取过**，不需要这一步。
                //
                // 快照方块真正缺的东西只有一件：`GameState.IncrementPieceCount(piece.Name)`
                // （`WaitForPlacement` @72601 的第一行）。少了它，游戏的"已放置方块计数"不涨，
                // 满度/数量相关的判定与 UI 会停在旧值。另外补一次 `Tint()`（@72645），
                // 让方块在加载后走一遍游戏自己的着色初始化。
                try {
                    int nFix = 0;
                    System.Collections.Generic.HashSet<Placeable> initFix = InitialLevelPieces(__instance);
                    GameState gsFix = null;
                    try { gsFix = GameState.GetInstance(); } catch { }
                    foreach (Placeable fp in Placeable.AllPlaceables) {
                        if (fp == null || fp.MarkedForDestruction) continue;
                        if (initFix.Contains(fp)) continue;      // 关卡自带件不动
                        if (!fp.Placed) continue;                 // 只处理已放置的
                        try {
                            // 与原生 @72601 一致：补上放置计数（这是快照路径唯一真正缺的一步）
                            if (gsFix != null) gsFix.IncrementPieceCount(fp.Name);
                            fp.Tint();                           // 与原生 @72645 一致
                            nFix++;
                        } catch (Exception __pe) { SR.Guard.Log("快照方块收尾", __pe); }
                    }
                    if (nFix > 0) SR.LogInfo("[快照] 已为 " + nFix + " 个方块补放置计数并着色");
                } catch (Exception __fx) { SR.Guard.Log("快照方块收尾", __fx); }
                // **#4 归属必须在"加载之后"**：方块是加载时创建出来的，加载前集合是空的
                try {
                    System.Collections.Generic.HashSet<Placeable> initPcs = InitialLevelPieces(__instance);
                    List<Placeable> built = new List<Placeable>();
                    foreach (Placeable bp in Placeable.AllPlaceables) {
                        // **同时过滤 MarkedForDestruction**：本行时机是"快照加载之后"，理论上无已破坏项，
                        //  但**上一轮残留的死方块**（destroyMarkedPieces 尚未回收）会被算进 built ->
                        //  日志里的数量虚高，且被 AttributeToHost 白白登记了归属。
                        if (bp == null || bp.MarkedForDestruction || initPcs.Contains(bp)) continue;
                        built.Add(bp);
                    }
                    // **R426**：走槽位取（原来直接 DestroyBlocks.AttributeToHost，是跨功能编译期依赖）。
                    //  取不到 = 方块破坏功能没装/被单独删掉 -> 只丢"名牌与归属"，不影响重建，降级即可。
                    Action<List<Placeable>> attributeToHost = SR.Require<Action<List<Placeable>>>(SlotAttributeToHost);
                    if (attributeToHost != null) {
                        attributeToHost(built);
                        SR.LogInfo("[快照] 已把 " + built.Count + " 个快照方块归属房主(#1)");
                    } else {
                        SR.LogWarn("[快照] 方块破坏功能未加载，跳过 " + built.Count + " 个方块的归属登记");
                    }
                } catch (Exception __ahx) { SR.Guard.Log("构建归属房主", __ahx); }
            } catch (Exception __ex) { SR.Guard.Log("快照后补发阶段事件", __ex); }
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
		        if (n2 > 0) SR.LogInfo("[快照] 已剥掉快照内 <destroyed> 节点: " + n2 + " 个");
		        return doc.OuterXml;
		    } catch (Exception __ex) { SR.Guard.Log("剥离 destroyed", __ex); return xml; }
		}

		//兜底清场：快照 <destroyed> 路径不可用（注入 0 条）时，退回逐块 DestroySelf（仍走游戏原生网络销毁信号）。
		public static void FallbackClearPlacedBlocks(List<Placeable> targets) {
			if (targets == null) return;
			int n = 0;
			for (int i = 0; i < targets.Count; i++) {
				Placeable p = targets[i];
				if (p == null || p.MarkedForDestruction) continue;
				try { p.DestroySelf(destroyChildren: false, useSmoke: false); n++; } catch (Exception __ex) { SR.Guard.Log("清场.兜底单项", __ex); }
			}
			SR.LogInfo("[清场] 快照路径不可用，已退回逐块网络销毁: " + n + " 个");
		}

    // 以下为重载关卡期间的保分补分（原 SR.Reload.cs，第 8 条：并入本功能）

// 分区：Reload（重载关卡保分补分 / 折扣保留 / 重载宽限）
//（加载后清理的 UI 在实验页 -> 它的配置与实现都在 Experiments.cs，本文件不再涉及。）

    [HarmonyPatch(typeof(LoadingInterstitialSplash), "FadeOut")]
    [HarmonyPostfix]
    static void OnLoadEnd() {
        // **Opt-52**：补丁里抛异常会被 Harmony 吞掉并**每次都记一条日志**（每帧补丁就是每帧刷屏），
        //  而且功能会静默失效。统一在这里兜住，失败经 SR.Guard.Log（内部按调用点去重）留一条痕。
        try {
                //重载关卡后广播补分（模式一"保留分数"：按原类型分块补分，下一回合结算显示）
                FillScoresIfPending();
                //重载关卡后恢复分数折扣（handicap）：无论模式一/二，房主写回 SyncVar -> 房客折扣保留
                RestoreHandicapsIfAny();
                //重载场景加载完成：通知 Experiments 记录时刻，用于决定何时清 levelPortalXml（替代固定延时）
                Level.OnReloadSceneLoaded();
        } catch (Exception __ex) { SR.Guard.Log("重载完成收尾", __ex); }
    }

    // 场景重载期间暂停"坏客户端踢出"检测
    //LobbyManager.Update 每秒把连续 3 秒无有效 player controller 的连接踢掉；重载时客户端正在加载新场景、
    //controller 暂时无效，稍慢就会被误判断连（Steam 断连弹窗），故 ReloadScene 后给 20 秒宽限期。
    private static float _reloadGraceUntil = 0f;
    [HarmonyPatch(typeof(LobbyManager), "ReloadScene")]
    [HarmonyPrefix]
    static void OnReloadSceneStart() {
        // **Opt-52**：补丁里抛异常会被 Harmony 吞掉并**每次都记一条日志**（每帧补丁就是每帧刷屏），
        //  而且功能会静默失效。统一在这里兜住，失败经 SR.Guard.Log（内部按调用点去重）留一条痕。
        try {
                _reloadGraceUntil = Time.unscaledTime + 20f;
        } catch (Exception __ex) { SR.Guard.Log("重载宽限起点", __ex); }
    }
    [HarmonyPatch(typeof(LobbyManager), "DisconnectBrokenClients")]
    [HarmonyPrefix]
    static bool SkipDisconnectDuringReload() {
        // **Opt-52**：补丁里抛异常会被 Harmony 吞掉并**每次都记一条日志**（每帧补丁就是每帧刷屏），
        //  而且功能会静默失效。统一在这里兜住，失败经 SR.Guard.Log（内部按调用点去重）留一条痕。
        try {
                return Time.unscaledTime >= _reloadGraceUntil;
        } catch (Exception __ex) { SR.Guard.Log("重载期跳过断连检查", __ex); return true; }
    }

    // 重载后保留分数（分类型补分）
    //重载时 ScoreKeeper.Setup() 会清空 playerTotal/分块（"重开本关"的固有行为）-> 分数丢失。
    //重载前备份各玩家分块列表，重载后房主按原类型逐个重放 PointAwarded -> 全员下回合 tally 恢复；不恢复 playerTotal。
    private static bool _preserveScoresOnReload = false;
    //备份：networkNumber -> List<PointBlock>（含类型的完整分块）
    private static Dictionary<int, List<PointBlock>> _scoreBackupBlocks = null;
    //补分重放后置位：下次结算的 ClearNewPointBlocks 跳过清除，保证重放分块能正常显示+tally。
    private static bool _refillPending = false;
    //外部加分（外部模块加分 / 补分重放，MsgPointAwarded.AlwaysAward=true）的分块标记：
    //结算会删掉非 AlwaysAward 分块，但玩家见过图标的要保留 -> 被清时缓存，重载备份补回。
    private static HashSet<int> _externAwardedKeys = new HashSet<int>();
    //结算时被 Clear 的外部模块加分分块（重载备份时合并进 _scoreBackupBlocks）
    private static List<PointBlock> _clearedExternBlocks = new List<PointBlock>();
    //备份：networkNumber -> handicap（SyncVar 本应跨场景保留，重载后仍由房主显式写回更保险）
    private static Dictionary<int, int> _handicapBackup = null;

    // **P2-29**：保分标志的"有效期"。MarkPreserveScores 是用户点重载时置的，正常十几秒内就会被
    //  ScoreKeeper.Setup 消费掉；如果中途重载失败（关卡加载出错 / 用户取消 / 掉线），
    //  标志会一直挂着 —— 之后任何一次**普通开局**都会被当成"重载保分"，把上一次留下的备份分块
    //  再补一遍，表现为"分数莫名其妙翻倍"。所以给标志带上时间戳，超时即视为失效。
    private static float _preserveMarkedAt = -1f;
    private const float PreserveStaleAfter = 90f;

    //重载关卡前直接置标志（模式一：允许补分）
    public static void MarkPreserveScores() {
        _preserveScoresOnReload = true;
        _preserveMarkedAt = Time.unscaledTime;
    }

    [HarmonyPatch(typeof(GameControl), "handleEvent")]
    [HarmonyPrefix]
    static void OnPrepareReloadMessage(GameEvent.GameEvent e) {
        try {
            //收到 PrepareToReloadScene 只重置补分保护，不自动置补分标志, 否则外部工具发起的重载会被再补一份 -> 分数翻倍。
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
                SR.LogInfo("[折扣] 重载前备份 " + _handicapBackup.Count + " 名玩家 handicap");
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
            SR.LogInfo("[折扣] 重载后恢复 " + backup.Count + " 名玩家 handicap");
        } catch (Exception __ex) { SR.Guard.Log("恢复 handicap", __ex); }
    }

    //结算时 ClearNewPointBlocks 会删掉非 AlwaysAward 分块。补分重放后(_refillPending)跳过清除；
    //正常结算只缓存"外部加分"分块, 玩家见过图标要保留，原生发的从未 tally 则不缓存（避免多计分）。
    [HarmonyPatch(typeof(ScoreKeeper), "ClearNewPointBlocks")]
    [HarmonyPrefix]
    static bool ClearNewPointBlocksPrefix(ScoreKeeper __instance) {
        try {
            if (_refillPending) {
                // **R396 S9**：原来只在"正常清除"路径把 newPointBlocks 快照进 _clearedExternBlocks，
                //  而**跳过清除**这条分支没做, 于是同一批 AlwaysAward 分块在
                //  跳过清除 -> 下一次重载备份后可能被补回两次（KeepScore 重载后个别玩家分数翻倍）。
                //  -> 这里同样缓存（去重后合并），两条路径对同一批分块的处理就一致了。
                try {
                    if (__instance != null && __instance.newPointBlocks != null) {
                        // _clearedExternBlocks 是 List<PointBlock>，用 Key 做线性去重
                        //（条数本来就受 FillTotalCap 限制，线性扫足够）
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
                } catch (Exception __cex) { SR.Guard.Log("缓存补分分块", __cex); }
                //补分重放后的第一次结算：跳过清除，保留全部分块
                return false;
            }
            if (__instance == null || __instance.newPointBlocks == null) return true;
            for (int i = 0; i < __instance.newPointBlocks.Count; i++) {
                PointBlock pb = __instance.newPointBlocks[i];
                if (pb == null) continue;
                if (!pb.AlwaysAward && _externAwardedKeys.Contains(Key(pb))) {
                    //外部加分（外部模块加分/补分）的非 AlwaysAward 分块：缓存，重载备份时补回
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

    //ScoreKeeper 收到 PointAwarded：标记"外部加分"分块（外部模块加分与补分重放都发 AlwaysAward=true，
    //原生名次分等为 false），以区分"玩家看到的加分"（保留）与"原生无效分"（不补）。
    [HarmonyPatch(typeof(ScoreKeeper), "handleEvent")]
    [HarmonyPrefix]
    static void OnPointAwardedMessage(ScoreKeeper __instance, global::GameEvent.GameEvent e) {
        try {
            if (e == null || e.GetType() != typeof(GameEvent.NetworkMessageReceivedEvent)) return;
            GameEvent.NetworkMessageReceivedEvent nm = e as GameEvent.NetworkMessageReceivedEvent;
            if (nm == null || nm.Message == null || nm.Message.msgType != NetMsgTypes.PointAwarded) return;
            //reader 在消息派发前已被游戏消费（GameState.distributeMessage 调过 readMessage）：
            //再 ReadMessage 会越界/读到垃圾 -> 用事件里已反序列化好的字段。
            MsgPointAwarded msg = nm.ReadMessage as MsgPointAwarded;
            if (msg != null && msg.AlwaysAward) {
                _externAwardedKeys.Add(Key(msg.PlayerNumber, msg.PointType));
            }
        } catch (Exception __ex) { SR.Guard.Log("标记外部加分分块", __ex); }
    }

    //补分重放的分块 tally 后即完成使命：清除 _refillPending 与外部加分标记，恢复正常清除。
    [HarmonyPatch(typeof(ScoreKeeper), "TallyPointBlockAllPlayers")]
    [HarmonyPostfix]
    static void TallyPostfix() {
        // **Opt-52**：补丁里抛异常会被 Harmony 吞掉并**每次都记一条日志**（每帧补丁就是每帧刷屏），
        //  而且功能会静默失效。统一在这里兜住，失败经 SR.Guard.Log（内部按调用点去重）留一条痕。
        try {
                _refillPending = false;
                _externAwardedKeys.Clear();
                _clearedExternBlocks.Clear();
        } catch (Exception __ex) { SR.Guard.Log("结算收尾", __ex); }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPrefix]
    static void ScoreSetupPrefix(ScoreKeeper __instance) {
        if (!_preserveScoresOnReload) return;
        try {
            //备份 historyPointBlocks（已 tally）+ newPointBlocks（本回合未结算）：两者才是重载前真正拥有的分。
            List<PointBlock> blocks = new List<PointBlock>();
            try {
                FieldInfo hf = SR.RefField(typeof(ScoreKeeper), "historyPointBlocks", "重载保分（历史分块）");
                List<PointBlock> hist = hf != null ? hf.GetValue(__instance) as List<PointBlock> : null;
                if (hist != null) blocks.AddRange(hist);
            } catch (Exception ex) {
                SR.LogWarn("[保分] 备份 historyPointBlocks 失败: " + ex.Message);
            }
            try {
                List<PointBlock> npb = __instance.newPointBlocks;   // **Opt-50（T1）**：public 字段（反编译 142921）-> 直接读
                if (npb != null) blocks.AddRange(npb);
            } catch (Exception ex) {
                SR.LogWarn("[保分] 备份 newPointBlocks 失败: " + ex.Message);
            }
            if (_clearedExternBlocks != null && _clearedExternBlocks.Count > 0) {
                //结算时被 Clear 的"外部加分"分块（玩家见过、期望保留）, 合并进备份，重载后补回
                blocks.AddRange(_clearedExternBlocks);
                SR.LogInfo("[保分] 合并被清除的外部加分分块 " + _clearedExternBlocks.Count + " 条");
                _clearedExternBlocks.Clear();
            }
            if (blocks.Count == 0) {
                SR.LogWarn("[保分] 重载时无可备份的分块（history+new+外部加分均为空）");
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
            SR.LogInfo("[保分] 重载备份分块 " + blocks.Count + " 条，涉及 " + _scoreBackupBlocks.Count + " 名玩家");
        } catch (Exception ex) {
            SR.LogWarn("[保分] 备份分块失败: " + ex.Message);
        }
    }

    [HarmonyPatch(typeof(ScoreKeeper), "Setup")]
    [HarmonyPostfix]
    static void ScoreSetupPostfix(ScoreKeeper __instance) {
        // **Opt-52**：补丁里抛异常会被 Harmony 吞掉并**每次都记一条日志**（每帧补丁就是每帧刷屏），
        //  而且功能会静默失效。统一在这里兜住，失败经 SR.Guard.Log（内部按调用点去重）留一条痕。
        try {
                //重载保分模式：下次结算跳过 ClearNewPointBlocks（保护重放分块）；正常开局时同步清掉
                //_refillPending，避免上次重载残留影响本次。
                // **P2-29**：先判过期。标志挂着但已经过了 PreserveStaleAfter 秒，说明那次重载并没有走完
                //  （失败/取消），本次应当按**普通开局**处理，否则会把陈旧的备份分块再补一遍。
                bool fresh = _preserveScoresOnReload && _preserveMarkedAt >= 0f
                    && (Time.unscaledTime - _preserveMarkedAt) <= PreserveStaleAfter;
                if (_preserveScoresOnReload && !fresh) {
                    SR.LogWarn("[保分] 保分标志已过期（" + ((int)(Time.unscaledTime - _preserveMarkedAt))
                        + " 秒没被结算），本次按普通开局处理");
                }
                _refillPending = fresh;
                //只清标志，不恢复分块, 分块图标由补分广播恢复（见 FillScoresIfPending）
                _preserveScoresOnReload = false;
                _preserveMarkedAt = -1f;
        } catch (Exception __ex) { SR.Guard.Log("保分标志复位", __ex); }
    }

    //重载完成后广播补分：房主按备份的原类型分块逐个发 PointAwarded（AlwaysAward=true），下回合 tally 进 playerTotal。
    //防网络风暴：上限 300 条，分帧发送（每帧 30 条）。
    private const int FillPerFrame = 30;
    private const int FillTotalCap = 300;
    private static void FillScoresIfPending() {
        if (_scoreBackupBlocks == null || _scoreBackupBlocks.Count == 0) return;
        // **R396 A4**：原来这里是 if (!SR.GateMaster) return;, 那是 UI 总开关，
        //  语义上不该阻断网络补分（局内把总开关关掉，不该让分数补不回来）。
        //  现在只用 HasServer 把关：必须服务器权威才能广播。
        if (!SR.HasServer) return;
        Dictionary<int, List<PointBlock>> backup = _scoreBackupBlocks;
        _scoreBackupBlocks = null;
        try {
            // R396 A4：只房主广播，且**必须走 NetworkServer.SendToAll**（服务器权威 -> 全员）。
            //  原来的 lm.client.Send 是 client->server 方向（游戏自己 142995 用它把加分上报给
            //  权威端结算），房客的 ScoreKeeper 永远收不到 -> 保留分数对房客必然失效。
            LobbyManager lm = LobbyManager.instance;
            if (lm == null || lm.client == null || !lm.client.isConnected) return;
            List<KeyValuePair<int, PointBlock.pointBlockType>> queue = new List<KeyValuePair<int, PointBlock.pointBlockType>>();
            //先统计总数：备份含 historyPointBlocks（已结算，跨回合累积）-> 长局很容易超过上限，
            //截断时必须告知丢了多少，否则玩家只看到"分数莫名其妙少了"却无从排查。
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
                    if (queue.Count >= FillTotalCap) { capped = true; break; } //全局上限
                    queue.Add(new KeyValuePair<int, PointBlock.pointBlockType>(kv.Key, pb.type));
                }
                if (capped) break; //已达上限：外层也停，避免剩余玩家逐一空转
            }
            if (capped) {
                SR.LogWarn("[补分] 备份分块 " + total + " 条超过上限 " + FillTotalCap
                    + "，仅补回前 " + queue.Count + " 条（" + (total - queue.Count) + " 条被丢弃，重载后分数会偏少）");
            }
            if (queue.Count == 0) return;
            lm.StartCoroutine(FillScoresCoroutine(queue));
            SR.LogInfo("[补分] 队列 " + queue.Count + " 条，分帧发送（每帧 " + FillPerFrame + " 条）");
        } catch (Exception ex) {
            SR.LogWarn("[补分] 初始化失败: " + ex.Message);
        }
    }

    private static System.Collections.IEnumerator FillScoresCoroutine(List<KeyValuePair<int, PointBlock.pointBlockType>> queue) {
        int sent = 0;
        while (sent < queue.Count) {
            int batch = Mathf.Min(FillPerFrame, queue.Count - sent);
            for (int i = 0; i < batch; i++) {
                try {
                    // **R396 A4**：服务器权威广播（与同文件 PrepareToReloadScene 同一套路）。
                    //  房客的 ScoreKeeper 收到后自己进 newPointBlocks，全员分数一致。
                    //  非权威端不需要自己补，等服务器广播即可。
                    if (!NetworkServer.active) { SR.LogWarn("[补分] 已非服务器权威，中止补分"); yield break; }
                    NetworkServer.SendToAll(NetMsgTypes.PointAwarded, new MsgPointAwarded {
                        PlayerNumber = queue[sent].Key, PointType = queue[sent].Value, AlwaysAward = true
                    });
                } catch (Exception ex) {
                    SR.LogWarn("[补分] 发送失败: " + ex.Message);
                }
                sent++;
            }
            yield return null; //每帧发一批，避免网络风暴
        }
    }
}
}