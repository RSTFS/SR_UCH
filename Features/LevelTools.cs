// 关卡工具。重载关卡 / 广播方块快照 / 重载模式 / 重载保分；
// 派对盒子炸弹、放弃道具、关卡背景也都在本文件里。
// 命名提醒：本命名空间里自定义一个 Level 会把游戏自带的 global::Level 遮住，所以类名叫 LevelTools。
// 槽位：对外给 "Level.ClearPlacedBlocks"。
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
// 重载关卡 / 广播方块快照 / 重载模式（Reload Mode）。
// 类名用 LevelTools：本命名空间里定义 Level 会遮蔽游戏自带的 global::Level，易出隐蔽错误。
public partial class LevelTools : ITweak {

    private static ConfigEntry<ReloadMode> _reloadMode;
    private static readonly int[] BombTypeVals = new int[] { 0, 1, 2 };   // **R412**：派对盒炸弹种类下拉的 vals（0小/1大/2超级）

    //本文件界面文案（自包含）
    private static void SelfReg() {
//本文件反射的游戏私有成员（游戏更新改名时：启动自检会在日志里告警；改名需等新版模组适配）
        SR.RefOverride("QuickSaver.levelPortalXml", "重载关卡 / 广播方块快照");
        SR.RefOverride("ScoreKeeper.historyPointBlocks", "重载保分（历史分块）");
        SR.RefOverride("ScoreKeeper.newPointBlocks", "重载保分 / 加分（待结算分块）");
        // **补登记**：快照加载后"哪些方块是关卡自带的"靠它算（InitialLevelPieces），
        //  原来裸反射没登记 -> 改名后静默失效（构建归属会把关卡自带方块也算成玩家放置）。
        //  注：同一处的 GameControl."currentPhase" 是不存在的成员（功能因此从未运行），
        //  已改为直接用 public 的 GameControl.Phase，不再是反射，故无需登记。
        SR.RefOverride("QuickSaver.initialLevelPlaceables", "快照加载后的初始方块集合（关卡自带件）");
        SR.LocSec("Level", "关卡", null);
        SR.Nav("Level", 40); //侧栏栏目顺序 40
        SR.RegisterPage("Level", Render);              //本功能页自绘（SR.Window 不再硬编码 if-else 分派）
        SR.LocKey("Level", "Reload Mode", "重载模式", null);
        SR.LocDesc("Level", "Reload Mode", "保留方块和分数: 重载后方块留着, 房主按重载前的分数类型给全员补一次, 下一回合结算时分数和图标都和重载前一样, 没装这个的房客也算上. 只保留方块: 方块留着但分数清零, 相当于重新开一局.", "Reload level mode:\nKeep blocks & score (allow fill) = blocks are kept; the host broadcasts a fill using the pre-reload per-type blocks (win/coin/trap etc. as-is), so everyone (incl. clients without the mod) sees the same score and types as before at the next round's tally (fill does not trigger an immediate tally; icons appear with the normal round end).\nKeep blocks only (skip fill) = blocks are kept, score resets (fresh match, no fill).");
        // 预登记本页的自动快捷键（不用等页面首次渲染）：否则重启后这些键在打开关卡页之前
        //  根本没登记过, SR.CheckHotkeys 轮询不到，表现为"绑了键、重启后按了没反应"。
        //  渲染时的 HotkeyButton 仍会再登记一次（同一 id 幂等，只覆盖 label/action）。
        SR.HotkeyAction("level.reload", "重载关卡", () => LevelTools.ReloadLevel());
        SR.HotkeyAction("level.broadcast", "广播方块快照", () => LevelTools.BroadcastSnapshot());
    }

    public static void Render() {
        // **R392**：关卡背景已移到实验 -> 场景页（用户反馈在关卡页看不到它）。

        // 放弃道具（放在派对盒分区**上面**）
        SR.RenderSectionEntries("Level", delegate (ConfigEntryBase e) {
            return e.Definition.Key == "Drop Held Piece";
        });

        // **R412**：EX 派对盒块（重抽/开箱/关闭）挂在这里：放弃道具之下、派对盒炸弹之上
        SR.RenderPageBlocks("Level", "above-partybomb");

        //派对盒子炸弹：对局内所有成员都发过炸弹！快捷短语才生成
        GUILayout.Label(SR.T("- 派对盒子炸弹 -", "- Party-box bomb -"), SR.Ctl.SecHeader);
        //选择框 + 炸弹种类同一行（种类紧挨在选择框右边）
        GUILayout.BeginHorizontal();
        ConfigEntryBase pbe = SR.Ctl.FindEntry("Level", "Party Bomb"); //PartyBomb 的 Bind 在 Level 段，查 [EX] 段取不到（勾选框写不回去）
        SR.Ctl.RestoreLabel(new GUIContent(SR.T("派对盒子炸弹", "Party-box bomb"), SR.T("房主开启后，对局内所有成员都发过炸弹！快捷短语时，往当前派对盒子塞入炸弹。", "Host on: when every member sends the Bomb quick-phrase, spawn a bomb in the current party box.")), pbe, SR.Ctl.Sc(140), SR.Ctl.Sc(40));
        if (pbe != null) SR.Ctl.RenderControl(pbe);
        GUILayout.Space(SR.Ctl.Sc(10));
        GUILayout.Label(SR.T("炸弹种类", "Bomb type"), SR.Ctl.Label, GUILayout.Height(SR.Ctl.Sc(26)));   // #2 去掉写死宽度（64px 装不下 4 个中文字）-> 自适应
        ConfigEntryBase pbtype = SR.Ctl.FindEntry("Level", "Party Bomb Type"); //同上：取不到则整行下拉框不显示
        if (pbtype != null) {
            //三档：0小 / 1大 / 2超级（对应游戏内 99abombmini / 99bomb / 99bbombmega）
            string[] opts = new[] { SR.T("小炸弹", "Small"), SR.T("大炸弹", "Big"), SR.T("超级炸弹", "Mega") };
            int curIdx = Mathf.Clamp((int)pbtype.BoxedValue, 0, 2);
            bool open; if (!SR.Ctl.EditOpen.TryGetValue(pbtype, out open)) open = false;
            int pbSel = SR.Ctl.ComboBox(pbtype, opts[curIdx], BombTypeVals, opts, ref open, SR.Ctl.Sc(150));
            if (pbSel >= 0) { SR.Ctl.SetValue(pbtype, BombTypeVals[pbSel]); SR.Ctl.EditOpen[pbtype] = false; }
            else SR.Ctl.EditOpen[pbtype] = open;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(2));

        GUILayout.Label(SR.T("- 关卡工具 -", "- Level tools -"), SR.Ctl.SecHeader);
        GUILayout.BeginHorizontal();
        if (SR.Ctl.HotkeyButton("level.reload", SR.T("重载关卡", "Reload level"),
            SR.T("房主把当前看到的方块都写进关卡快照, 然后真的重载一次当前关卡, 全员一起重新加载.\n重载完按快照把方块重建出来并广播, 玩家放的方块和能动的道具都不会丢.\n要不要顺手把分数补回来, 看下面那个重载模式.\n 只在派对和创意模式的对局里生效, 而且只有房主能用.", "Host writes the current blocks into the snapshot and really reloads the level scene, so everyone reloads.\nBlocks are rebuilt from the snapshot and nothing is lost.\nWhether the score is filled back depends on the Reload mode below.\n Party/Creative matches only, host only."),
            () => LevelTools.ReloadLevel(), SR.Ctl.Sc(170), SR.Ctl.Sc(30))) {
            LevelTools.ReloadLevel();
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
            () => LevelTools.BroadcastSnapshot(), SR.Ctl.Sc(170), SR.Ctl.Sc(30))) {
            LevelTools.BroadcastSnapshot();
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(SR.Ctl.Sc(4));
    }

    public void Initialize(IFeatureHost plugin) {
        SelfReg();
        //对外槽位：别的功能/外部模块经 SR.Require 取用清场能力，不引用 LevelTools 类型。
        SR.Provide(SlotClearPlacedBlocks, (Action<List<Placeable>, string>)ClearPlacedBlocks);
			_reloadMode = ((BaseUnityPlugin)plugin).Config.Bind<ReloadMode>("Level", "Reload Mode", ReloadMode.KeepScore, "重载关卡模式：\n保留方块和分数（允许补分）= 重载后当前方块保留；房主按重载前的分类型分块（获胜/金币/陷阱等原样）给全员广播补分，下一回合结算时全员得分板显示与重载前一致的分数和类型（含未装 mod 的房客；补分不立即结算，图标随正常结算显示）。\n仅保留方块（跳过补分）= 重载后当前方块保留，分数重置（重新对局，不补分）。");
			//注册本类的 Harmony 补丁：CreateAndPatchAll 不会自动发现带 [HarmonyPatch] 的类型，
			//独立类必须自己注册，否则本文件里的 9 个补丁写了也不生效（重载保分 / 快照广播全部失效）。
			//同类写法见 Experiments 的 Reeval / ExModule.Other.cs 的 InitMapGrid / QuickAdjust 的初始化。
			//带 try/catch：单个补丁目标失效（游戏更新改方法名）只丢本类功能，不影响其它功能,
			//参考 BuildingTools.cs（它是全项目唯一原本就分开注册 + 容错的地方）。
        try { Harmony.CreateAndPatchAll(typeof(LevelTools), (string)null); }
        catch (Exception e) { SR.LogError("LevelTools 补丁注册失败: " + e.Message); }
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
					System.Reflection.FieldInfo lpx = SR.RefField(typeof(QuickSaver), "levelPortalXml", "重载关卡 / 广播方块快照");
					if (lpx != null) lpx.SetValue(null, doc.OuterXml);
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
				catch (Exception __ex) { SR.Guard.Log("LevelTools.SendToAll", __ex); }
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
				System.Reflection.FieldInfo lpx = SR.RefField(typeof(QuickSaver), "levelPortalXml", "重载关卡 / 广播方块快照");
				if (lpx != null) lpx.SetValue(null, null);
			}
			catch (Exception __ex) { SR.Guard.Log("LevelTools.Field", __ex); }
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

		//对外槽位：别的功能/外部模块经 SR.Require 取用，不引用 LevelTools 类型
		//（这样本功能独立成 DLL 或整段删除时，调用方只是"取不到 -> 降级"，不会编译不过）。
		private const string SlotClearPlacedBlocks = "Level.ClearPlacedBlocks"; // Action<List<Placeable>,string>
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
                    DestroyBlocks.AttributeToHost(built);
                    SR.LogInfo("[快照] 已把 " + built.Count + " 个快照方块归属房主(#1)");
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
        //重载关卡后广播补分（模式一"保留分数"：按原类型分块补分，下一回合结算显示）
        FillScoresIfPending();
        //重载关卡后恢复分数折扣（handicap）：无论模式一/二，房主写回 SyncVar -> 房客折扣保留
        RestoreHandicapsIfAny();
        //重载场景加载完成：通知 Experiments 记录时刻，用于决定何时清 levelPortalXml（替代固定延时）
        LevelTools.OnReloadSceneLoaded();
    }

    // 场景重载期间暂停"坏客户端踢出"检测
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
                FieldInfo hf = SR.RefField(typeof(ScoreKeeper), "historyPointBlocks", "重载保分（历史分块）");
                List<PointBlock> hist = hf != null ? hf.GetValue(__instance) as List<PointBlock> : null;
                if (hist != null) blocks.AddRange(hist);
            } catch (Exception ex) {
                SR.LogWarn("[保分] 备份 historyPointBlocks 失败: " + ex.Message);
            }
            try {
                FieldInfo nf = SR.RefField(typeof(ScoreKeeper), "newPointBlocks", "重载保分 / 加分（待结算分块）");
                List<PointBlock> npb = nf != null ? nf.GetValue(__instance) as List<PointBlock> : null;
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
// 以下由 PartyBomb.cs 并入（UI 在关卡页 -> 文件跟着 UI 走）
namespace SR_UCH.Tweaks {
    //派对盒子炸弹：房主端统计对局内所有在线成员的炸弹！快捷短语
    //（EmoteMeanings.EMOTE_Bomb，按枚举码识别，与多语言/文本无关），全员都发过才往当前
    //派对盒子塞入一个可选取的炸弹；型号可配置（PartyBox.BombPrefab 的第几个）。
    // **实验功能**：动态改 PartyBox 私有 pieces + 网络同步，需游戏内验证。
    public class PartyBomb : ITweak {
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _bombSize;
        public static bool Enabled = false;

        //已发送过炸弹！快捷消息的成员（networkNumber 去重）
        private static readonly HashSet<int> _bombSent = new HashSet<int>();


        public void Initialize(IFeatureHost plugin) {
            SR.RegisterDismissHook(delegate { _bombSent.Clear(); });
//本文件反射的游戏私有成员（游戏更新改名时：启动自检会在日志里告警；改名需等新版模组适配）
            SR.RefOverride("PartyBox.pieces", "派对盒炸弹（生成/清理）");
            _enabled = plugin.Config.Bind("Level", "Party Bomb", false,
                "派对盒子炸弹: 房主开启后, 对局里所有人都发过炸弹这个快捷短语, 才会往派对盒子里塞炸弹. 只有房主生效.");
            _bombSize = plugin.Config.Bind("Level", "Party Bomb Type", 0,
                "塞进去的是哪种炸弹, 填 PartyBox.BombPrefab 里的第几个, 从 0 开始. 不同下标对应盒子里不同大小的炸弹. 第一次生成的时候日志里会列出有哪些可选.");
            Enabled = _enabled.Value;
            _enabled.SettingChanged += (s, e) => Enabled = _enabled.Value;
            //带 try/catch：单个补丁目标失效（游戏更新改方法名/签名）只丢本类功能，不影响其它功能。
            try { Harmony.CreateAndPatchAll(typeof(PartyBomb)); }
            catch (Exception e) { SR.LogError("派对盒炸弹 补丁注册失败: " + e.Message); }
            //换场景/换对局清空已发过炸弹记录：networkNumber 会跨对局复用，
            //残留会让新局里同号玩家被当成"已发"-> 不必全员发过就提前塞炸弹。
            // R396 S8：_bombSent 的清理时机。原来**只有** activeSceneChanged（换场景），
            //  但注释自己说 networkNumber 会跨对局复用, 同一场景内连续两局（派对常在同一场景重开）
            //  不触发该事件，残留 id 会让新局同号玩家被误判"已发过炸弹" -> 炸弹提前生成
            //  （AddBombToPartyBox 无次数上限，可叠加）。
            //  补一个对局开始的清理点：游戏自己发 StartPhaseEvent.START（反编译 125576），
            //  用 SR 的场景钩子接（自带按名去重，不会有重复注册问题）。
            SR.RegisterSceneHook(delegate { _bombSent.Clear(); });
        }

        //快捷短语都走 ChatDisplay.DisplayNewMessage，EmoteType 保留原枚举码；房主端收集每位成员的 EMOTE_Bomb。
        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage", new Type[] { typeof(ChatMessageDetails) })]
        [HarmonyPrefix]
        static void OnBombEmote(ChatMessageDetails chatMessageDetails) {
            if (!SR.GateMaster || !Enabled) return;
            try {
                if (chatMessageDetails.EmoteType != EmoteMeanings.EMOTE_Bomb) return;
                //仅服务器（房主）端统计/生成：炸弹需 NetworkServer.Spawn 广播全员；统一走 SR.HasServer（= NetworkServer.active）
                if (!SR.HasServer) return;
                List<int> online = OnlinePlayerNumbers();
                if (online.Count == 0) return;
                _bombSent.Add(chatMessageDetails.NetworkNumber);
                bool allSent = true;
                foreach (int n in online) {
                    if (!_bombSent.Contains(n)) { allSent = false; break; }
                }
                if (!allSent) {
                    SR.LogInfo("[PartyBomb] 已收到 " + _bombSent.Count + "/" + online.Count + " 名成员的炸弹！快捷消息");
                    return;
                }
                _bombSent.Clear(); //触发一次后清空，等待下一轮全员再发
                SR.LogInfo("[PartyBomb] 全员已发炸弹！，生成炸弹（型号 #" + BombSize + "）");
                AddBombToPartyBox();
            } catch (Exception e) {
                SR.LogWarn("[PartyBomb] 检测失败: " + e.Message);
            }
        }

        //对局在线成员 networkNumber
        static List<int> OnlinePlayerNumbers() {
            List<int> list = new List<int>();
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                        NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                        list.Add(info.NetworkNumber);
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("统计在线玩家(PlayerTracker)", __ex); }
            if (list.Count == 0) {
                try {
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.PlayerCharacter == null) continue;
                        int n = p.PlayerCharacter.networkNumber;
                        if (!list.Contains(n)) list.Add(n);
                    }
                } catch (Exception __ex) { SR.Guard.Log("统计在线玩家(PlayerManager)", __ex); }
            }
            return list;
        }

        static int BombSize { get { return _bombSize != null ? Mathf.Clamp(_bombSize.Value, 0, 2) : 0; } }

        //往当前派对盒子塞入一个炸弹（参考 PartyBox.ChoosePieces 里 Spawn 单个 piece 的做法）
        static void AddBombToPartyBox() {
            try {
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { SR.LogWarn("[PartyBomb] 未找到派对盒子"); return; }
                if (pb.BombPrefab == null || pb.BombPrefab.Length == 0) { SR.LogWarn("[PartyBomb] 派对盒子无炸弹预制体"); return; }
                Placeable bombPlaceable = PickBombPlaceable(pb, BombSize);
                if (bombPlaceable == null || bombPlaceable.PickableBlock == null) { SR.LogWarn("[PartyBomb] 炸弹档 " + BombSize + " 无 PickableBlock"); return; }

                //pieces 是 private：反射获取后加入
                var f = SR.RefField(typeof(PartyBox), "pieces", "派对盒炸弹（生成/清理）");
                if (f == null) return;
                IList pieces = f.GetValue(pb) as IList;
                if (pieces == null) return;

                PickableBlock piece = UnityEngine.Object.Instantiate(bombPlaceable.PickableBlock);
                pieces.Add(piece);

                //设 parent / 美术层 / Layer，并在圆周半径内随机取位置
                piece.transform.SetParent(pb.transform, false);
                try { piece.ChangeArtLayer("Background 2"); } catch (Exception __ex) { SR.Guard.Log("设置炸弹美术层", __ex); }
                foreach (Transform t in piece.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
                float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float rad = Mathf.Max(0.1f, pb.PlacementRadius * 0.8f);
                piece.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, Mathf.Sin(ang) * rad, 0f);
                piece.NetworkUseStartPosition = true;
                piece.NetworkStartPosition = piece.transform.localPosition; //Start() 会用 StartPosition 覆盖 localPosition
                piece.FindPartyBox = true; //让 piece 找到并跟随派对盒子（同游戏自身 spawn）
                try { piece.setInitialScale(GameSettings.GetInstance().partyBoxItemScale); } catch (Exception __ex) { SR.Guard.Log("设置炸弹初始缩放", __ex); }
                piece.InPartybox = true;
                piece.Enable();
                NetworkServer.Spawn(piece.gameObject);

                SR.LogInfo("[PartyBomb] 已向派对盒子塞入炸弹 (" + BombName(BombSize) + ", " + piece.name + ")");
            } catch (Exception e) {
                SR.LogWarn("[PartyBomb] 加炸弹失败: " + e.Message);
            }
        }

        //炸弹三档名称：0 小 / 1 大 / 2 超级
        static string BombName(int size) {
            switch (size) {
                case 0: return "小炸弹";
                case 2: return "超级炸弹";
                default: return "大炸弹";
            }
        }

        //按档从 PartyBox.BombPrefab 里挑对应炸弹预制体（按名字：mini / 普通 bomb / mega）
        static Placeable PickBombPlaceable(PartyBox pb, int size) {
            Placeable[] arr = pb.BombPrefab;
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == null) continue;
                string n = arr[i].name.ToLowerInvariant();
                if (size == 0 && n.Contains("mini")) return arr[i];
                if (size == 2 && n.Contains("mega")) return arr[i];
            }
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == null) continue;
                string n = arr[i].name.ToLowerInvariant();
                if (size == 1 && n.Contains("bomb") && !n.Contains("mini") && !n.Contains("mega")) return arr[i];
            }
            // **R396 S23**：按名字三个循环都没匹配上（换预制体命名/改版本），
            //  原来静默按索引兜底, size=0 而没有名字含 "mini" 的预制体时会返回 arr[0]（普通炸弹）
            //  -> 玩家选"小炸弹"实际拿到大炸弹，且日志无任何提示。补一条告警便于排查。
            SR.LogWarn("[派对盒炸弹] 按名字未匹配到型号（size=" + size + "），已退回按索引取第 " + Mathf.Clamp(size, 0, arr.Length - 1) + " 个");
            int idx = Mathf.Clamp(size, 0, arr.Length - 1);
            return arr[idx];
        }
    }
}

namespace SR_UCH.Tweaks {
    // 放弃道具。需求很简单：派对盒里选好了道具、进了建造阶段，又不想放了。
    // 做法是调游戏自己的 PiecePlacementCursor.ClearCurrentPiece()（private，73021）：
    //   它内部 if (hasAuthority) { CallCmdClearPiece(); 播音效 } -> CmdClearPiece -> **RpcClearPiece**，
    //   由**全员**执行（已放下的先发 DestroyPieceEvent，再 Disable + DestroySelf，最后 SetPiece(null) 清手）。
    // **必须用游戏自己的**：联机同步天然正确 —— RpcClearPiece 全员执行，房客按同样有效、不需要房主权限，
    //   不会出现"只有我丢掉了、别人手上还留着"。自己拼 SetPiece(null)+Destroy 就会漏掉别人那边。
    // 放弃之后手上是空的（就是游戏原生的"取消"语义），要还得从背包重选。
    public class DropHeldPiece : ITweak {
        private static ConfigEntry<KeyCode> _key;

        public void Initialize(IFeatureHost plugin) {
            try {
                SR.LocKey("Level", "Drop Held Piece", "放弃道具", "Drop held piece");
                SR.LocDesc("Level", "Drop Held Piece",
                    "排队模式里手上拿着道具又不想放了, 按这个键丢掉. 走游戏自己的通道, 全员都能看到, 之后正常进下一阶段.",
                    "In the queue phase, press this to discard the piece you are holding. Goes through the game's own channel; everyone sees it.");
                _key = plugin.Config.Bind("Level", "Drop Held Piece", KeyCode.None,
                    "按这个键就把手上拿着的那块丢掉（走游戏原生通道, 全员可见）, 填 None 就是不绑键.");
                SR.RegisterKey("关卡-放弃道具", _key, "press");
                SR.RegisterTick("放弃道具", Tick);
                // **R413-5**：新增的反射成员要登记，否则游戏改版后 SR.RefAudit 不会报出来
                SR.RefOverride("PiecePlacementCursor.CallCmdClearPiece", "关卡：放弃道具（原生 Command 上行，全员可见）");
                SR.RefOverride("PiecePlacementCursor.ClearCurrentPiece", "关卡：放弃道具（原生清件，退路）");
                SR.RefOverride("VersusControl.remainingPartyBoxes", "关卡：放弃道具（清放置计数，防卡放置阶段）");
                SR.RefOverride("VersusControl.PlacedThisRound", "关卡：放弃道具（清本轮已放置集合）");
            } catch (Exception __ex) { SR.Guard.Log("放弃道具 初始化", __ex); }
        }

        private static void Tick() {
            try {
                if (!SR.GateMaster) return;
                if (_key == null || _key.Value == KeyCode.None) return;
                if (!SR.ComboKeyDown(_key)) return;
                PiecePlacementCursor c = FindCursor();
                if (c == null) { SR.Notify(SR.T("未找到放置光标", "Placement cursor not found")); return; }
                if (c.Piece == null) { SR.Notify(SR.T("手上没有方块", "No piece in hand")); return; }
                if (!CallNativeRemoveUnplaced()) {
                    SR.Notify(SR.T("放弃失败", "Failed to drop the piece"));
                    SR.LogWarn("[放弃道具] 原生放弃接口不可用");
                    return;
                }
                // **R418**：只提示"已放弃"，不显示道具名（用户要求）
                SR.Notify(SR.T("已放弃当前方块", "Piece dropped"));
            } catch (Exception __ex) { SR.Guard.Log("放弃道具", __ex); }
        }

        // **本机的放置光标**。CursorInstance 是 LobbyPlayer 的属性（反编译 40959），
        //  不是 Player 的, Player 上只有 AssociatedLobbyPlayer。
        //  所以走 LobbyManager.lobbySlots（NetworkLobbyPlayer[]）筛 IsLocalPlayer。
        private static PiecePlacementCursor FindCursor() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.lobbySlots != null) {
                    NetworkLobbyPlayer[] slots = lm.lobbySlots;
                    for (int i = 0; i < slots.Length; i++) {
                        LobbyPlayer lp = slots[i] as LobbyPlayer;
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        PiecePlacementCursor ppc = lp.CursorInstance as PiecePlacementCursor;
                        if (ppc != null) return ppc;
                    }
                }
                //退路：场上任一 PiecePlacementCursor（单机 / lobbySlots 还没生成时）
                PiecePlacementCursor[] all = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (all != null && all.Length > 0) return all[0];
            } catch (Exception __ex) { SR.Guard.Log("找放置光标", __ex); }
            return null;
        }

        // ================================================================
        // **R416-2（用户问：放弃道具别人能看到吗 / 修房客按了没反应）**
        //
        // 上一轮（R413）我改调 `VersusControl.CallRpcRemoveUnplacedObjects()`，**方向错了**。
        // 反编译 @147543：
        //   public void CallRpcRemoveUnplacedObjects() {
        //       if (!NetworkServer.active) { Debug.LogError("RPC ... called on client."); return; }
        //       ... SendRPCInternal(networkWriter, 0, "RpcRemoveUnplacedObjects");
        //   }
        // 它是**服务器下行广播**（SendRPCInternal，没有 SendCommandInternal、没有 Cmd 前缀），
        // 房客调它在第一行就被拦掉并打 LogError —— **房客按放弃等于什么都没发生，房主也看不到**。
        //
        // 玩家主动"丢弃自己手上的方块"在游戏里本来就有正规通道，就是 `ClearCurrentPiece` @73022：
        //   if (hasAuthority) { CallCmdClearPiece(); AkSound...; }
        // 而 `CallCmdClearPiece()` @73207 是 **public**（NetworkBehaviour 生成的 `[Command]` 包装），
        // 房客可以直接调 -> SendCommandInternal 上行 -> 服务端 `CmdClearPiece` @72991
        //   -> `CallRpcClearPiece()` -> `[ClientRpc] RpcClearPiece` @72997 全员执行：
        //        Piece.Disable() + Piece.DestroySelf(true, false, false)
        //        + DestroyPieceEvent（仅当已放置）+ SetPiece(null)
        // ⇒ **房客按放弃，全场（含房主）都能看到**。这才是"走原生通道"的正确入口。
        //
        // 之前 R392 那版直接反射调 private `ClearCurrentPiece()` 会跳创意模式，
        // 根因是它**绕过了 `OnInventory` @72356 里的 PARTY 门禁**（`if (GameMode == PARTY) return;`）——
        // 但那条门禁本来是"**不要自动清**"，玩家主动点放弃时游戏自己也是调 ClearCurrentPiece 的
        // （走 `CmdClearPiece`），所以现在走 Command 上行链，语义与原生完全一致。
        //
        // 房主/单机：`ClearCurrentPiece` 只在 hasAuthority 时才发命令，所以本地直接调它即可。
        // ================================================================
        private static bool CallNativeRemoveUnplaced() {
            try {
                PiecePlacementCursor c = FindCursor();
                if (c == null) return false;
                if (c.Piece == null) return false;
                int myNum = c.networkNumber;

                //--------------------------------------------------------------
                // **R417-1（用户报：放弃后进入创意模式道具库那种状态）**
                //
                // 上一轮我改调`CallCmdClearPiece`（public @73207，[Command] 上行，全员可见）——
                // 方向对（联机正确、房客也能用），但**只做了一半**。
                // `RpcClearPiece` @72997 只做三件事：DestroyPieceEvent（仅已放置）、
                // `Piece.Disable()`、`DestroySelf(...)`、`SetPiece(null)`。
                // 而原生派对模式的清件通道 `RpcRemoveUnplacedObjects` @146913 还额外做**三件**事：
                //   @146931  `RemainingPlacements[item.networkNumber - 1] = 0;`
                //   @146944  发 `MsgPiecePlaced{ PieceID = 0, ... }`
                //   ↓ 服务端 @146620 收到 -> 发 `PlacementSkippedEvent` @146623 -> `RemainingPlacements=0`
                //              并对**该玩家的光标**调 `Cursor.Hide()` @146634
                //
                // 缺了它们的后果（这就是用户看到的现象）：
                //  · `playersLeftToPlace` @14457 只要 `RemainingPlacements[i] > 0` 就为 true，
                //    而它在排队模式每轮被 `SetupPartyBoxForRound` @144798 重置为 1 ——
                //    不清零 => `DoPlaceMode` @145299 的 `if (!playersLeftToPlace)` 永远进不去
                //    => **永远不开下一个派对盒、永远不ChangeToPlayPhaseDelayed**（整局卡在放置阶段）；
                //  · 光标不会被 `Cursor.Hide()` 关掉：它仍然 Enabled、停在原地可移动，
                //    而 `SetPiece(null)` @71838 又把 `SelectionCollider.enabled` 置回 true
                //    —— **看起来就像"进入了某个放置/选择方块的 UI 状态"**。
                //
                // 顺带纠正我 R413 的错误诊断：`OnInventory` @72356 的 `GameMode == PARTY` 早退
                // 是**阻止**进道具库的（PARTY 下通往 `InventoryBookMenu` 的路对
                // `PiecePlacementCursor` 完全封闭），跳库不可能是绕过它造成的。
                // 用户描述的"创意模式道具库"实际就是上面那个**卡住的光标**。
                //
                // **改法**：走 `CallCmdClearPiece` 之后，照抄 @146939-146946 那段
                // 发 `MsgPiecePlaced{ PlayerNumber = myNum, PieceID = 0, ... }` ——
                // 这一条上行消息会触发完整的服务端收尾链（PlacementSkippedEvent
                // -> RemainingPlacements=0 -> Cursor.Hide()），**完全复刻原生**。
                //--------------------------------------------------------------
                bool sent = false;
                try {
                    System.Reflection.MethodInfo cmd = SR.RefMethod(typeof(PiecePlacementCursor), "CallCmdClearPiece", "关卡：放弃道具（原生 Command 上行，全员可见）");
                    if (cmd != null) {
                        System.Reflection.ParameterInfo[] cps = cmd.GetParameters();
                        cmd.Invoke(c, cps.Length == 0 ? new object[0] : new object[0]);
                        sent = true;
                    }
                } catch (Exception __ce) { SR.Guard.Log("放弃道具(Command 上行)", __ce); }
                if (!sent) {
                    //退路：房主/单机下直接调 private ClearCurrentPiece（等价于本地执行那条链）
                    System.Reflection.MethodInfo clr = SR.RefMethod(typeof(PiecePlacementCursor), "ClearCurrentPiece", "关卡：放弃道具（原生清件，退路）");
                    if (clr == null) return false;
                    System.Reflection.ParameterInfo[] lps = clr.GetParameters();
                    clr.Invoke(c, lps.Length == 0 ? new object[0] : new object[0]);
                }

                //**关键补一步**：上行 `MsgPiecePlaced{PieceID=0}`，让服务端把
                // `RemainingPlacements[myNum-1]` 清零并 `Cursor.Hide()`（照抄 @146939-146946）。
                // 没有它，放弃之后会卡在放置阶段出不来（详见上面的说明）。
                try {
                    MsgPiecePlaced skip = new MsgPiecePlaced();
                    skip.PlayerNumber = myNum;
                    skip.PiecePosition = Vector3.zero;
                    skip.PieceScale = Vector3.zero;
                    skip.PieceRotation = Quaternion.identity;
                    skip.PieceID = 0;   //★ <=0 让客户端 @72892 发 PlacementSkippedEvent、服务端 @146687 忽略放置
                    LobbyManager.instance.client.Send(NetMsgTypes.PiecePlaced, skip);
                } catch (Exception __se) { SR.Guard.Log("放弃道具(上报跳过)", __se); return sent; }
                return true;
            } catch (Exception __ex) { SR.Guard.Log("放弃道具（原生通道）", __ex); return false; }
        }
    }
    // 关卡背景（解除黑域）
    // 它和"关卡边界"是两件事：边界管能不能越过去，背景管**界外有没有画面可看**。
    // 黑域真正的成因在关卡几何（已由 Experiments 的解除关卡边界接管），这里负责让界外有画面，两者互补。
    // 这个类原来没实现 ITweak，而 MainPlugin 靠反射扫 ITweak 发现功能 -> 从未实例化 -> Initialize 没跑过
    //   -> 配置项没绑定 -> 页面只剩一个标题。凡是自带 Initialize(IFeatureHost) 的功能类，必须实现 ITweak。
    public class LevelBackground : ITweak {
        private static ConfigEntry<BackgroundType> _type;
        private static readonly Dictionary<global::Level, BackgroundType> _orig = new Dictionary<global::Level, BackgroundType>();
        internal static string Note;

        //关卡背景自建对象的 tag（用来找回/清理我们造的那一个）
        private const string OwnTag = "SR_Background_Own";

        public void Initialize(IFeatureHost plugin) {
            try {
                SR.RefOverride("Level.currentCustomBackground", "关卡背景（黑域兜底需要写入）");
                SR.LocKey("Level", "Level Background", "关卡背景", "Level background");
                SR.LocDesc("Level", "Level Background",
                    "给关卡换一个背景, 界外就不会一片黑. 自定义关卡和对局里的原版关卡都生效, 树屋大厅不受影响. 没效果的话去看日志里黑域那一行.",
                    "Give the level a background so the void outside is not black. Works in custom levels and in official matches; the treehouse is untouched.");
                _type = plugin.Config.Bind("Level", "Level Background", BackgroundType.None,
                    "给自制关卡装一个背景, 填 None 就是不装. 关卡外面那片黑就是靠这个解决的.");
                SR.RegisterTick("关卡背景", Tick);
                SR.RegisterSceneHook(Reset);
            } catch (Exception __ex) { SR.Guard.Log("关卡背景 初始化", __ex); }
        }

        public static void Reset() { _orig.Clear(); _diagAt = -1; Note = null; _lvCache = null; }

        private static int _diagAt = -1;
        // **P1-09**：Level 实例缓存。本功能挂在 SR.RegisterTick 上 -> Apply 每帧都跑，
        //  而原来每帧都 FindObjectsOfType<Level>()（全场景遍历）。Level 在一局里是稳定的，
        //  只在一开始取一次即可；换场景由 Reset（已注册的 SceneHook）清空，场景切换瞬间
        //  Level 全被销毁时下面也会丢缓存重扫。
        private static global::Level[] _lvCache;
        public static void Tick() {
            try {
                if (_type == null || _type.Value == BackgroundType.None) return;
                if (!SR.GateMaster) return;   //与其它功能一致：受总开关限制
                Apply(_type.Value);
            } catch (Exception __ex) { SR.Guard.Log("关卡背景", __ex); }
        }

        private static void Apply(BackgroundType want) {
            try {
                global::Level[] lvs = _lvCache;
                if (lvs == null) {
                    lvs = UnityEngine.Object.FindObjectsOfType<global::Level>();
                    _lvCache = lvs;
                }
                if (lvs == null || lvs.Length == 0) { Diag("未找到任何 Level 对象"); return; }
                int libCount = -1;
                try {
                    BackgroundLibrary lib = BackgroundLibrary.Instance;
                    libCount = (lib != null && lib.Backgrounds != null) ? lib.Backgrounds.Length : -1;
                } catch (Exception __be) { Diag("读 BackgroundLibrary 失败:" + __be.Message); }
                bool anyBlank = false; int hitBlank = 0; int validLv = 0;
                bool inMatch = false;
                try { inMatch = SR.Gate.InMatch; } catch { }
                for (int i = 0; i < lvs.Length; i++) {
                    if (lvs[i] == null) continue;
                    validLv++;
                    if (lvs[i].thisLevelis == GameState.LevelName.BLANKLEVEL) { anyBlank = true; hitBlank++; }
                }
                // 缓存里的 Level 已全部被销毁（正在换场景）-> 丢掉缓存，下一帧重扫
                if (validLv == 0) { _lvCache = null; return; }
                // **R412**：原版关卡也生效（用户需求）。树屋/大厅不在对局内 -> 不动它们的背景。
                if (!anyBlank && !inMatch) {
                    Diag("当前没有自定义关卡(BLANKLEVEL)且不在对局内（共 " + lvs.Length + " 个）-> 背景不生效是正常的");
                    return;
                }
                for (int i = 0; i < lvs.Length; i++) {
                    global::Level lv = lvs[i];
                    if (lv == null) continue;
                    // **R412**：自定义关卡始终生效；原版关卡仅对局内生效（不动树屋/大厅）
                    if (lv.thisLevelis != GameState.LevelName.BLANKLEVEL && !inMatch) continue;
                    if (!_orig.ContainsKey(lv)) _orig[lv] = lv.DefaultCustombackground;
                    if (lv.currentCustomBackground == null || lv.currentCustomBackground.background != want) {
                        lv.SetBackground(want);   //原生路径
                    }
                    bool got = lv.currentCustomBackground != null && lv.currentCustomBackground.background == want;
                    if (!got) got = MakeOwn(lv, want);   // **兜底**：绕开背景库自己造
                    if (lv.GetInstanceID() != _diagAt) {
                        _diagAt = lv.GetInstanceID();
                        Diag((got ? "背景已装上：" : " 仍然失败：") + want
                            + "；背景库预设数=" + libCount + "；自定义关卡×" + hitBlank
                            + (got ? RenderDiag(lv) : " -> 请把这条日志反馈给 SR"));
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("关卡背景", __ex); }
        }

        private static void Diag(string msg) { SR.LogInfo("[黑域] " + msg); Note = msg; }

        // **渲染层诊断**：背景已装上却仍看到黑时，问题只可能在"没被画出来"这一步。
        //  逐项取证：背景对象是否存在/是否激活/是否在相机视野内/SpriteRenderer 是否被剔除/
        //  相机 cullingMask 与 background 所在 layer 是否匹配。
        private static string RenderDiag(global::Level lv) {
            string r = "";
            try {
                CustomBackground cb = lv.currentCustomBackground;
                if (cb == null) return "；渲染：currentCustomBackground 为 null";
                GameObject go = cb.gameObject;
                r += "；渲染：背景对象" + (go.activeInHierarchy ? "激活" : " 未激活");
                Vector3 lp = go.transform.position;
                Camera cam = Camera.main;
                if (cam != null) {
                    r += "；相机pos(" + ((int)cam.transform.position.x) + "," + ((int)cam.transform.position.y)
                       + ") size=" + (cam.orthographic ? cam.orthographicSize.ToString("0.0") : "perp")
                       + " clear=" + cam.clearFlags;
                    Vector3 sp = cam.WorldToScreenPoint(lp);
                    r += " 背景屏幕pos(" + ((int)sp.x) + "," + ((int)sp.y) + "," + ((int)sp.z) + ")";
                    if (sp.z < 0) r += " 在相机背后";
                    int camMask = cam.cullingMask;
                    int bgMask = 1 << go.layer;
                    r += ((camMask & bgMask) != 0) ? " layer可见✓" : (" layer被剔除(" + LayerMask.LayerToName(go.layer) + ")");
                } else r += "； Camera.main 为 null";
                SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
                if (sr == null) r += "； 无 SpriteRenderer（不渲染）";
                else {
                    r += "；SR:启用=" + sr.enabled + " sprite=" + (sr.sprite != null ? "有" : " 无")
                       + " color=" + sr.color + " 排序层=" + sr.sortingLayerName + " 序=" + sr.sortingOrder
                       + " scale=" + go.transform.localScale;
                }
                //玩家自身也要能看到背景：它挂在 Level 下，Level 若被禁用/移到别处就看不见
                r += "；背景挂于" + go.transform.parent.name;
            } catch (Exception __re) { r += "；渲染诊断失败:" + __re.Message; }
            return r;
        }

        //自己造背景：CustomBackground 只有一个 public BackgroundType 字段，游戏读的就是它
        private static bool MakeOwn(global::Level lv, BackgroundType want) {
            try {
                GameObject go = new GameObject(OwnTag);
                CustomBackground cb = go.AddComponent<CustomBackground>();
                cb.background = want;
                go.transform.SetParent(lv.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, 10f);
                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                Sprite s = WhiteSprite();
                if (s != null) sr.sprite = s;
                sr.color = TintFor(want);
                sr.sortingOrder = -100;   //压在所有方块之下
                // **足够大**：自建背景要覆盖"走到很远也看不到黑"。原来游戏预设比关卡小得多，
                //  这也是"装了背景仍见黑"的常见原因之一, 所以这里直接给一个很大的块。
                go.transform.localScale = new Vector3(4000f, 4000f, 1f);
                lv.currentCustomBackground = cb;   //public 字段，直接赋值（已 Cecil 核实）
                return true;
            } catch (Exception __ex) { SR.Guard.Log("自建背景", __ex); return false; }
        }

        //SpriteRenderer 没有 sprite 就什么都不渲染 -> 又变回透明 = 看着还是黑，必须自己造一张
        private static Sprite _white;
        private static Sprite WhiteSprite() {
            try {
                if (_white != null) return _white;
                Texture2D tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                Color[] px = new Color[16];
                for (int i = 0; i < px.Length; i++) px[i] = Color.white;
                tex.SetPixels(px); tex.Apply();
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                _white = Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
                _white.name = "SR_White";
                return _white;
            } catch (Exception __ex) { SR.Guard.Log("自建背景贴图", __ex); return null; }
        }

        //按背景类型给相近色调（游戏预设的美术资源拿不到，用纯色近似；总比黑域好）
        private static Color TintFor(BackgroundType t) {
            try {
                switch (t) {
                    case BackgroundType.BlueSky: return new Color(0.45f, 0.72f, 0.95f);
                    case BackgroundType.BlueSkyWithClouds: return new Color(0.55f, 0.78f, 0.97f);
                    case BackgroundType.Sunset: return new Color(0.95f, 0.62f, 0.42f);
                    case BackgroundType.NightSky: return new Color(0.10f, 0.12f, 0.26f);
                    case BackgroundType.Forest: return new Color(0.24f, 0.48f, 0.24f);
                    case BackgroundType.CityScape: return new Color(0.30f, 0.32f, 0.38f);
                    case BackgroundType.Farm: return new Color(0.62f, 0.52f, 0.28f);
                    case BackgroundType.Windmill: return new Color(0.58f, 0.62f, 0.44f);
                    case BackgroundType.Paper: return new Color(0.93f, 0.91f, 0.85f);
                    case BackgroundType.PaperPink: return new Color(0.95f, 0.84f, 0.86f);
                    case BackgroundType.PaperOrange: return new Color(0.96f, 0.87f, 0.72f);
                    case BackgroundType.PaperGreen: return new Color(0.85f, 0.91f, 0.82f);
                    case BackgroundType.PaperBlue: return new Color(0.82f, 0.89f, 0.94f);
                    case BackgroundType.BlackWhite: return new Color(0.75f, 0.75f, 0.75f);
                    case BackgroundType.MountainCastle: return new Color(0.42f, 0.44f, 0.50f);
                    case BackgroundType.FinalExplosion: return new Color(0.35f, 0.18f, 0.14f);
                    default: return new Color(0.45f, 0.72f, 0.95f);
                }
            } catch { return new Color(0.45f, 0.72f, 0.95f); }
        }
    }
}
