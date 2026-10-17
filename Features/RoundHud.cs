// 回合计时 / 阶段提醒：本地只读 HUD，显示当前阶段、阶段倒计时和回合信息。
// 阶段切换来自 GameControl.handleEvent 的 Harmony Postfix；倒计时优先读取游戏公开设置与
// VersusControl 的计时字段，读不到时退化为"已用时间"计时，不发送任何网络消息。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameEvent;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
	// 本类自带 [HarmonyPatch]，由 ITweak 反射发现后手动 CreateAndPatchAll。
	[SrFeature("sr.roundhud", "RoundHUD", "1.0")]
	public class RoundHud : ITweak {
		private static IFeatureHost _mp;
		private static ConfigEntry<bool> _enabled;
		private static ConfigEntry<int> _posX;
		private static ConfigEntry<int> _posY;
		private static ConfigEntry<int> _fontSize;
		private static ConfigEntry<float> _opacity;
		private static ConfigEntry<int> _warnSeconds;
		private static ConfigEntry<bool> _blink;
		private static ConfigEntry<bool> _phaseNotice;
		private static ConfigEntry<bool> _showScores;
		private static ConfigEntry<bool> _showRound;
		private static ConfigEntry<bool> _beep;
		private static ConfigEntry<float> _beepVolume;

		// 阶段状态：Harmony 补丁写，RegisterTick 读。
		private static GameControl _gc;
		private static GameControl.GamePhase _phase = GameControl.GamePhase.NONE;
		private static float _phaseStart;
		private static bool _eventSeen;
		private static bool _hasGame;
		private static bool _wasEnabled;        // 关闭后重新打开时重置阶段起点
		// 帧缓存：Tick 算好，OnGUI 只负责画。
		private static string _timerText = "00:00";
		private static int _warnLevel;          // 0 = 正常，1 = 黄，2 = 红
		private static string _roundText;
		private static string _noticeText;
		private static float _noticeUntil;
		private static bool _vsPlaceSeen;       // placementTimer 是否已初始化（0 也在用）

		// 反射缓存：VersusControl 的三个计时字段（protected float）。
		private static FieldInfo _fPlacementTimer;
		private static FieldInfo _fLevelTimer;
		private static FieldInfo _fCountdownTimer;

		// 得分列表：Tick 里排序并缓存成最多 4 行文本，OnGUI 直接画。
		private struct ScoreRow {
			public string name;
			public int score;
			public bool outFlag;
		}
		private static readonly ScoreRow[] _scoreRows = new ScoreRow[8];
		private static readonly string[] _scoreLines = new string[4];
		private static int _scoreLineCount;
		private static int _scoreSig;
		private static int _scoreRowCount;

		// 最后 5 秒提示音：AudioSource + 运行时生成的正弦波，不依赖游戏音频资源。
		private static AudioSource _audio;
		private static AudioClip _beepClip;
		private static bool _beepedPhase;       // 每个阶段只响一次

		// HUD 绘制缓存（首次 OnGUI 时创建）。
		private static GUIStyle _phaseStyle;
		private static GUIStyle _timerStyle;
		private static GUIStyle _smallStyle;
		private static GUIStyle _noticeStyle;
		private static int _styleFont = -1;
		private static Texture2D _white;
		// 拖动状态。
		private static bool _dragging;
		private static float _dragX;
		private static float _dragY;
		private static float _dragOffX;
		private static float _dragOffY;

		// ---- 配置取值（全部 Clamp：手改 cfg 可能拿到超范围值）----
		private static bool IsEnabled() {
			try { return _enabled != null && _enabled.Value; } catch { return false; }
		}

		private static int PosX() {
			try { return _posX != null ? Mathf.Clamp(_posX.Value, 0, 20000) : 16; } catch { return 16; }
		}

		private static int PosY() {
			try { return _posY != null ? Mathf.Clamp(_posY.Value, 0, 20000) : 120; } catch { return 120; }
		}

		private static int FontSize() {
			try { return _fontSize != null ? Mathf.Clamp(_fontSize.Value, 10, 48) : 18; } catch { return 18; }
		}

		private static float Opacity() {
			try { return _opacity != null ? Mathf.Clamp(_opacity.Value, 0.2f, 1f) : 0.85f; } catch { return 0.85f; }
		}

		private static int WarnSeconds() {
			try { return _warnSeconds != null ? Mathf.Clamp(_warnSeconds.Value, 5, 60) : 10; } catch { return 10; }
		}

		private static bool BlinkOn() {
			try { return _blink != null && _blink.Value; } catch { return false; }
		}

		private static bool PhaseNoticeOn() {
			try { return _phaseNotice != null && _phaseNotice.Value; } catch { return true; }
		}

		private static bool ShowScoresOn() {
			try { return _showScores != null && _showScores.Value; } catch { return false; }
		}

		private static bool ShowRoundOn() {
			try { return _showRound != null && _showRound.Value; } catch { return true; }
		}

		private static bool BeepOn() {
			try { return _beep != null && _beep.Value; } catch { return true; }
		}

		private static float BeepVolume() {
			try { return _beepVolume != null ? Mathf.Clamp(_beepVolume.Value, 0f, 1f) : 0.5f; } catch { return 0.5f; }
		}

		private static void SelfReg() {
			// 本文件反射的游戏私有成员，改名时启动自检会在日志里告警。
			SRApi.Ref.Override("VersusControl.placementTimer", "回合计时（建造阶段剩余时间）");
			SRApi.Ref.Override("VersusControl.levelTimer", "回合计时（对局已用时间）");
			SRApi.Ref.Override("VersusControl.countdownTimer", "回合计时（决胜倒计时）");
			SRApi.LocSec("Round HUD", "回合 HUD");
			SRApi.Nav("Round HUD", 25); // 侧栏顺序 25：移动轨迹(20) 之后、快速调整(30) 之前
			SRApi.RegisterPage("Round HUD", Render);
			SRApi.LocKey("Round HUD", "Enabled", "回合计时 HUD", null);
			SRApi.LocDesc("Round HUD", "Enabled", "回合计时 HUD 开关\n在画面上显示当前阶段、阶段倒计时和回合信息\n纯本地只读，不发送任何网络消息，不影响游戏判定", "round HUD switch\nshows the current phase, phase countdown and round info on screen\nlocal read-only; sends no network messages and does not affect gameplay");
			SRApi.LocKey("Round HUD", "Pos X", "水平位置", null);
			SRApi.LocDesc("Round HUD", "Pos X", "HUD 水平位置\nHUD 左边缘距屏幕左侧的像素数\n可直接拖动 HUD 调整，松开鼠标后自动保存", "HUD horizontal position\ndistance in pixels from the left screen edge to the HUD's left edge\ndrag the HUD to move it; the position is saved on mouse release");
			SRApi.LocKey("Round HUD", "Pos Y", "垂直位置", null);
			SRApi.LocDesc("Round HUD", "Pos Y", "HUD 垂直位置\nHUD 上边缘距屏幕顶部的像素数\n可直接拖动 HUD 调整，松开鼠标后自动保存", "HUD vertical position\ndistance in pixels from the top screen edge to the HUD's top edge\ndrag the HUD to move it; the position is saved on mouse release");
			SRApi.LocKey("Round HUD", "Font Size", "字体大小", null);
			SRApi.LocDesc("Round HUD", "Font Size", "HUD 字体大小\nHUD 文字的基础字号，会随界面缩放一起放大\n范围 10 到 48", "HUD font size\nbase font size of the HUD text, scaled with the UI scale\nrange 10-48");
			SRApi.LocKey("Round HUD", "Opacity", "不透明度", null);
			SRApi.LocDesc("Round HUD", "Opacity", "HUD 不透明度\n同时控制背景和文字的透明程度\n范围 0.2 到 1，1 为完全不透明", "HUD opacity\ncontrols the transparency of both the background and the text\nrange 0.2-1, 1 = fully opaque");
			SRApi.LocKey("Round HUD", "Warning Seconds", "警告秒数", null);
			SRApi.LocDesc("Round HUD", "Warning Seconds", "倒计时警告秒数\n剩余时间低于该秒数时倒计时文字变黄\n最后 5 秒固定变红，可配合闪烁开关\n范围 5 到 60", "countdown warning seconds\nthe countdown turns yellow when less time than this remains\nit always turns red in the last 5 seconds; can blink\nrange 5-60");
			SRApi.LocKey("Round HUD", "Blink", "闪烁提醒", null);
			SRApi.LocDesc("Round HUD", "Blink", "最后 5 秒闪烁\n倒计时进入最后 5 秒时文字闪烁提醒\n关闭则只在最后 5 秒显示红色", "blink in the last 5 seconds\nmakes the countdown blink during the final 5 seconds\nOFF shows a steady red countdown instead");
			SRApi.LocKey("Round HUD", "Phase Notice", "阶段提示", null);
			SRApi.LocDesc("Round HUD", "Phase Notice", "阶段切换提示\n阶段切换时在 HUD 里短暂显示新阶段名称\n显示约 2 秒后自动消失", "phase change notice\nbriefly shows the new phase name in the HUD when the phase changes\nfades out automatically after about 2 seconds");
			SRApi.LocKey("Round HUD", "Show Scores", "显示得分", null);
			SRApi.LocDesc("Round HUD", "Show Scores", "显示玩家得分\nHUD 内增加最多 4 行的玩家得分列表，按分数从高到低排序\n只读取本局得分与淘汰状态，取不到时整段隐藏", "show player scores\nadds a list of up to 4 players to the HUD, sorted from highest to lowest score\nreads this match's scores and eliminated state only; hidden when unavailable");
			SRApi.LocKey("Round HUD", "Show Round", "显示回合进度", null);
			SRApi.LocDesc("Round HUD", "Show Round", "显示回合进度\n在 HUD 中显示第 N 回合与目标回合/胜场数\n取不到回合信息时自动隐藏该行", "show round progress\nshows the current round and the target round/win count in the HUD\nhidden automatically when round info is unavailable");
			SRApi.LocKey("Round HUD", "Beep", "最后 5 秒提示音", null);
			SRApi.LocDesc("Round HUD", "Beep", "最后 5 秒提示音\n倒计时进入最后 5 秒时响一声，每个阶段只响一次\n使用运行时生成的简单提示音，不依赖游戏音频资源", "last-5-seconds beep\nplays one beep when the countdown enters its final 5 seconds, once per phase\nuses a beep generated at runtime; no game audio assets are used");
			SRApi.LocKey("Round HUD", "Beep Volume", "提示音音量", null);
			SRApi.LocDesc("Round HUD", "Beep Volume", "提示音音量\n最后 5 秒提示音的音量\n范围 0 到 1，0 为静音", "beep volume\nvolume of the last-5-seconds beep\nrange 0-1, 0 = silent");
		}

		// 本页内容 = 该段的条目列表（与移动轨迹页同款）。
		private static void Render() {
			try {
				List<ConfigEntryBase> list = SRApi.SectionEntries("Round HUD");
				if (list == null || list.Count == 0) { GUILayout.Label(SRApi.T("（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label); return; }
				SRApi.RenderSectionEntries("Round HUD");
			} catch (Exception __ex) { SRApi.Guard.Log("渲染回合计时页", __ex); }
		}

		// 初始化需整段 try：中途抛异常会留下常驻对象且功能静默消失。
		public void Initialize(IFeatureHost plugin) {
			try {
				SelfReg();
				_mp = plugin;
				ConfigFile cfg = plugin.Config;
				_enabled = cfg.Bind("Round HUD", "Enabled", true, "总开关：在画面上显示当前阶段、阶段倒计时和回合信息（纯本地只读）。");
				_posX = cfg.Bind("Round HUD", "Pos X", 16,
					new ConfigDescription("HUD 水平位置（像素）：HUD 左边缘距屏幕左侧的距离；拖动 HUD 会自动更新。", new AcceptableValueRange<int>(0, 20000)));
				_posY = cfg.Bind("Round HUD", "Pos Y", 120,
					new ConfigDescription("HUD 垂直位置（像素）：HUD 上边缘距屏幕顶部的距离；拖动 HUD 会自动更新。", new AcceptableValueRange<int>(0, 20000)));
				_fontSize = cfg.Bind("Round HUD", "Font Size", 18,
					new ConfigDescription("HUD 字体大小：基础字号（10-48），会随界面缩放放大。", new AcceptableValueRange<int>(10, 48)));
				_opacity = cfg.Bind("Round HUD", "Opacity", 0.85f,
					new ConfigDescription("HUD 不透明度（0.2-1）：同时控制背景和文字，1 为完全不透明。", new AcceptableValueRange<float>(0.2f, 1f)));
				_warnSeconds = cfg.Bind("Round HUD", "Warning Seconds", 10,
					new ConfigDescription("倒计时警告秒数（5-60）：剩余时间低于该值时文字变黄；最后 5 秒固定变红。", new AcceptableValueRange<int>(5, 60)));
				_blink = cfg.Bind("Round HUD", "Blink", false, "最后 5 秒闪烁：倒计时进入最后 5 秒时文字闪烁提醒。");
				_phaseNotice = cfg.Bind("Round HUD", "Phase Notice", true, "阶段切换提示：阶段切换时在 HUD 里短暂显示新阶段名称（约 2 秒）。");
				_showScores = cfg.Bind("Round HUD", "Show Scores", false, "显示玩家得分：在 HUD 内增加最多 4 行玩家得分/淘汰列表（只读本局数据，取不到时隐藏）。");
				_showRound = cfg.Bind("Round HUD", "Show Round", true, "显示回合进度：在 HUD 中显示第 N 回合与目标回合/胜场数（取不到时隐藏）。");
				_beep = cfg.Bind("Round HUD", "Beep", true, "最后 5 秒提示音：倒计时进入最后 5 秒时响一声，每个阶段只响一次。");
				_beepVolume = cfg.Bind("Round HUD", "Beep Volume", 0.5f,
					new ConfigDescription("提示音音量（0-1）：最后 5 秒提示音的音量，0 为静音。", new AcceptableValueRange<float>(0f, 1f)));

				SRApi.RegisterTick("sr.roundhud", Tick);
				SRApi.RegisterSceneHook(ResetStateSafe);

				// 常驻对象：承载 OnGUI 绘制与提示音播放。
				GameObject go = new GameObject("SR_UCHRoundHud");
				UnityEngine.Object.DontDestroyOnLoad(go);
				go.AddComponent<HudComponent>();
				// 2D 音源；AudioClip 懒生成（只在第一次需要响提示音时创建）。
				_audio = go.AddComponent<AudioSource>();
				_audio.playOnAwake = false;
				_audio.spatialBlend = 0f;
				_audio.volume = 1f;

				// 本类自带 [HarmonyPatch] 但 ITweak 为反射发现，须手动 CreateAndPatchAll；失败只丢本类功能。
				try { Harmony.CreateAndPatchAll(typeof(RoundHud), (string)null); }
				catch (Exception e) { SRApi.LogError("回合计时 补丁注册失败: " + e.Message); }
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 初始化", __ex); }
		}

		// 监听 GameControl.handleEvent 的所有派发结果：StartPhaseEvent / EndPhaseEvent。
		// 各模式子类（Versus/FreePlay/Challenge）的 override 都会调 base.handleEvent，故只补基类一次。
		[HarmonyPatch(typeof(GameControl), "handleEvent")]
		[HarmonyPostfix]
		private static void OnGameControlEvent(GameControl __instance, global::GameEvent.GameEvent e) {
			// Harmony 会吞掉补丁异常并每次记一条日志，故统一兜住并经 Guard.Log（按调用点去重）留痕。
			try {
				if (__instance == null || e == null) return;
				StartPhaseEvent start = e as StartPhaseEvent;
				if (start != null) { OnPhaseStarted(__instance, start.Phase); return; }
				EndPhaseEvent end = e as EndPhaseEvent;
				if (end != null) { OnPhaseEnded(end.Phase); }
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 阶段事件", __ex); }
		}

		private static void OnPhaseStarted(GameControl gc, GameControl.GamePhase phase) {
			_gc = gc;
			_eventSeen = true;
			_phase = phase;
			_phaseStart = Time.unscaledTime;
			_vsPlaceSeen = false;
			_beepedPhase = false;   // 新阶段重新允许最后 5 秒提示音
			_noticeText = SRApi.T("阶段：", "Phase: ") + PhaseName(phase);
			_noticeUntil = _phaseStart + 2f;
		}

		private static void OnPhaseEnded(GameControl.GamePhase phase) {
			// 结束事件紧随其后的开始事件；这里只把倒计时钳到 0，避免 End 与 Start 之间出现负值。
			if (phase != _phase) return;
			if (Time.unscaledTime < _phaseStart) _phaseStart = Time.unscaledTime;
		}

		private static void Tick() {
			try {
				if (!IsEnabled()) { _hasGame = false; _wasEnabled = false; return; }
				if (!_wasEnabled) {
					// 首次启用或从关闭切回：丢弃旧阶段起点，让下面的兜底按当前 Phase 重新计时。
					_wasEnabled = true;
					_eventSeen = false;
					_phase = GameControl.GamePhase.NONE;
					_phaseStart = Time.unscaledTime;
				}
				LobbyManager lm = LobbyManager.instance;
				GameControl gc = (lm != null) ? lm.CurrentGameController : null;
				if (gc == null) {
					if (_gc != null || _hasGame) ResetState();
					return;
				}
				if (!ReferenceEquals(gc, _gc)) {
					// 换对局/换关卡：先清空，等新的 StartPhaseEvent 或下面的一次性兜底。
					_gc = gc;
					_phase = GameControl.GamePhase.NONE;
					_phaseStart = Time.unscaledTime;
					_eventSeen = false;
					_vsPlaceSeen = false;
					_roundText = null;
					_timerText = "00:00";
					_warnLevel = 0;
				}
				if (!_eventSeen) {
					// 补丁注册晚于对局开始（热重载/中途装 Mod）时的兜底：用公开的 Phase 属性补一次。
					GameControl.GamePhase p = gc.Phase;
					if (p != GameControl.GamePhase.NONE && p != _phase) {
						_phase = p;
						_phaseStart = Time.unscaledTime;
					}
				}
				if (_phase == GameControl.GamePhase.NONE) { _hasGame = false; return; }
				_hasGame = true;
				GameSettings gs = ReadSettings();
				UpdateTimer(gc, gs);
				if (ShowRoundOn()) UpdateRound(gc, gs); else _roundText = null;
				UpdateScores(gc);
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 状态更新", __ex); }
		}

		private static GameSettings ReadSettings() {
			try { return GameSettings.GetInstance(); } catch { return null; }
		}

		// 阶段时长优先走游戏字段；所有失败路径都退化为"已用时间"，不会画错或崩。
		private static void UpdateTimer(GameControl gc, GameSettings gs) {
			float elapsed = Mathf.Max(0f, Time.unscaledTime - _phaseStart);
			float remaining = -1f;
			bool countdown = false;
			VersusControl vs = gc as VersusControl;
			switch (_phase) {
			case GameControl.GamePhase.START: {
				float d = gc.StartDelay;
				if (d > 0.05f && d < 3600f) { countdown = true; remaining = d - elapsed; }
				break;
			}
			case GameControl.GamePhase.PLACE: {
				// 建造时长：设置里开了建造计时才有；能读到 VersusControl.placementTimer 时以它为准
				// （最后几回合会缩短，公共 PlaceTime 并不等于实际剩余）。
				bool useTimer = gs != null && gs.UsePlaceTimer;
				if (useTimer) {
					if (vs != null) {
						float t = ReadFloat(ref _fPlacementTimer, vs, "placementTimer", "回合计时（建造剩余时间）");
						if (t >= 0f) {
							if (t > 0.01f) _vsPlaceSeen = true;
							if (_vsPlaceSeen) { countdown = true; remaining = t; break; }
						}
					}
					float total = gs != null ? gs.PlaceTime : 0f;
					if (total > 0.05f) { countdown = true; remaining = total - elapsed; }
				}
				break;
			}
			case GameControl.GamePhase.PLAY: {
				// 只有"时间上限"规则才有可倒数的对局总时长；按 VersusControl.levelTimer（已用）换算。
				if (gs != null && gs.GameLimitType == GameLimitType.TIME && vs != null) {
					float used = ReadFloat(ref _fLevelTimer, vs, "levelTimer", "回合计时（对局已用时间）");
					if (used >= 0f) { countdown = true; remaining = (float)gs.MaxTime - used; }
				}
				break;
			}
			case GameControl.GamePhase.SUDDENDEATH: {
				if (vs != null) {
					float t = ReadFloat(ref _fCountdownTimer, vs, "countdownTimer", "回合计时（决胜倒计时）");
					if (t > 0.01f) { countdown = true; remaining = t; }
				}
				break;
			}
			case GameControl.GamePhase.END: {
				// 结算阶段按 WinTime 倒数（VersusControl 用同一个字段决定何时回大厅；平局时会改成 4 秒）。
				float d = gc.WinTime;
				if (d > 0.05f && d < 3600f) { countdown = true; remaining = d - elapsed; }
				break;
			}
			default:
				break;
			}
			if (countdown) {
				if (remaining < 0f) remaining = 0f;
				_timerText = FormatClock(Mathf.CeilToInt(remaining));
				float warn = WarnSeconds();
				float red = Mathf.Min(5f, warn);
				_warnLevel = remaining <= red ? 2 : (remaining <= warn ? 1 : 0);
				// 进入最后 5 秒的那一帧响一次（每个阶段一次，_beepedPhase 在 StartPhaseEvent 重置）。
				if (_warnLevel >= 2) MaybeBeep();
			} else {
				_timerText = FormatClock(Mathf.FloorToInt(elapsed));
				_warnLevel = 0;
			}
		}

		// 回合进度：只读 GameControl.RoundNumber；目标按游戏规则取 MaxRounds 或 MaxScore。
		private static void UpdateRound(GameControl gc, GameSettings gs) {
			try {
				int round = gc.RoundNumber;
				if (round <= 0) { _roundText = null; return; }
				string head = SRApi.T("第 ", "Round ") + round + SRApi.T(" 回合", " round");
				if (gs == null) { _roundText = head; return; }
				if (gs.GameLimitType == GameLimitType.ROUNDS) {
					int rounds = gs.MaxRounds;
					_roundText = rounds > 0 ? head + SRApi.T(" / 目标 ", " / target ") + rounds : head;
				} else {
					int wins = gs.MaxScore;
					_roundText = wins > 0 ? head + SRApi.T(" / 目标 ", " / target ") + wins + SRApi.T(" 胜", " wins") : head;
				}
			} catch { _roundText = null; }
		}

		// 得分/淘汰列表：只读 ScoreKeeper（玩家总分）与 GamePlayer（名字、角色死亡状态），最多 4 行。
		private static void UpdateScores(GameControl gc) {
			try {
				if (!ShowScoresOn()) { ClearScores(); return; }
				// ScoreKeeper 只在 Versus 系模式（派对/创造）里 Setup；其他模式整段隐藏。
				if (!(gc is VersusControl)) { ClearScores(); return; }
				ScoreKeeper sk;
				try { sk = ScoreKeeper.Instance; } catch { sk = null; }
				if (sk == null) { ClearScores(); return; }
				int n = 0;
				Queue<GamePlayer> queue = gc.CurrentPlayerQueue;
				if (queue != null) {
					foreach (GamePlayer gp in queue) {
						if (gp == null) continue;
						if (n >= _scoreRows.Length) break;
						int total;
						try { total = sk.GetPlayerTotal(gp); } catch { total = 0; }
						bool outFlag = false;
						try {
							Character c = gp.CharacterInstance;
							if (c != null && (c.Dead || c.Dying)) outFlag = true;
						} catch { outFlag = false; }
						_scoreRows[n].name = DisplayName(gp);
						_scoreRows[n].score = total;
						_scoreRows[n].outFlag = outFlag;
						n++;
					}
				}
				_scoreRowCount = n;
				if (n == 0) { ClearScores(); return; }
				// 插入排序：按分数从高到低；同分保持队列顺序，避免每帧互换导致抖动。
				for (int i = 1; i < n; i++) {
					ScoreRow key = _scoreRows[i];
					int j = i - 1;
					while (j >= 0 && _scoreRows[j].score < key.score) { _scoreRows[j + 1] = _scoreRows[j]; j--; }
					_scoreRows[j + 1] = key;
				}
				int shown = Mathf.Min(n, _scoreLines.Length);
				int sig = SRApi.LangVer;
				sig = sig * 31 + shown;
				for (int i = 0; i < shown; i++) {
					sig = sig * 31 + _scoreRows[i].score;
					sig = sig * 31 + (_scoreRows[i].outFlag ? 1 : 0);
					sig = sig * 31 + (_scoreRows[i].name != null ? _scoreRows[i].name.GetHashCode() : 0);
				}
				if (sig != _scoreSig) {
					_scoreSig = sig;
					for (int i = 0; i < shown; i++) {
						string line = _scoreRows[i].name + "  " + _scoreRows[i].score + SRApi.T(" 分", " pts");
						if (_scoreRows[i].outFlag) line += SRApi.T(" · 淘汰", " · out");
						_scoreLines[i] = line;
					}
					for (int i = shown; i < _scoreLines.Length; i++) _scoreLines[i] = null;
				}
				_scoreLineCount = shown;
			} catch (Exception __ex) {
				ClearScores();
				SRApi.Guard.Log("回合计时 得分列表", __ex);
			}
		}

		private static void ClearScores() {
			_scoreLineCount = 0;
			_scoreRowCount = 0;
			_scoreSig = 0;
			for (int i = 0; i < _scoreLines.Length; i++) _scoreLines[i] = null;
		}

		private static string DisplayName(GamePlayer gp) {
			string name = null;
			try { name = gp.playerName; } catch { }
			if (string.IsNullOrEmpty(name)) {
				try { name = gp.ValidatedDisplayName; } catch { }
			}
			if (!string.IsNullOrEmpty(name)) name = name.Trim();
			if (string.IsNullOrEmpty(name)) name = "P" + gp.networkNumber;
			if (name.Length > 12) name = name.Substring(0, 11) + "..";
			return name;
		}

		// 最后 5 秒提示音：每次阶段只响一次；AudioClip 懒生成（纯正弦，0.18 秒，淡入淡出防爆音）。
		private static void MaybeBeep() {
			if (!BeepOn() || _beepedPhase || !SRApi.GateMaster) return;
			// 只在有实际倒计时的阶段响：准备/结算阶段的最后 5 秒没有必要提示。
			if (_phase != GameControl.GamePhase.PLACE && _phase != GameControl.GamePhase.PLAY && _phase != GameControl.GamePhase.SUDDENDEATH) return;
			_beepedPhase = true;
			try {
				float vol = BeepVolume();
				if (vol <= 0f) return;
				EnsureBeepClip();
				if (_audio == null || _beepClip == null) return;
				_audio.PlayOneShot(_beepClip, vol);
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 提示音", __ex); }
		}

		private static void EnsureBeepClip() {
			if (_beepClip != null) return;
			const int rate = 44100;
			int samples = Mathf.Max(1, Mathf.RoundToInt((float)rate * 0.18f));
			float[] data = new float[samples];
			for (int i = 0; i < samples; i++) {
				float t = (float)i / (float)rate;
				float env = 1f;
				if (t < 0.012f) env = t / 0.012f;                 // 起音淡入
				float tail = (float)(samples - i) / (float)samples;
				if (tail < 0.3f) env *= tail / 0.3f;              // 收尾淡出
				data[i] = Mathf.Sin(2f * Mathf.PI * 880f * t) * 0.6f * env;
			}
			_beepClip = AudioClip.Create("SR_RoundHudBeep", samples, 1, rate, false);
			_beepClip.SetData(data, 0);
		}

		private static float ReadFloat(ref FieldInfo f, object owner, string name, string feature) {
			if (owner == null) return -1f;
			if (f == null) {
				try { f = SRApi.Ref.Field(typeof(VersusControl), name, feature); } catch { f = null; }
			}
			if (f == null) return -1f;
			try {
				object v = f.GetValue(owner);
				return (v is float) ? (float)v : -1f;
			} catch { return -1f; }
		}

		private static string FormatClock(int totalSeconds) {
			if (totalSeconds < 0) totalSeconds = 0;
			int m = totalSeconds / 60;
			int s = totalSeconds % 60;
			return m.ToString("00") + ":" + s.ToString("00");
		}

		private static string PhaseName(GameControl.GamePhase p) {
			switch (p) {
			case GameControl.GamePhase.START: return SRApi.T("准备", "Ready");
			case GameControl.GamePhase.PLAY: return SRApi.T("游玩", "Play");
			case GameControl.GamePhase.PLACE: return SRApi.T("建造", "Build");
			case GameControl.GamePhase.SUDDENDEATH: return SRApi.T("决胜", "Sudden death");
			case GameControl.GamePhase.END: return SRApi.T("结算", "End");
			case GameControl.GamePhase.WAIT: return SRApi.T("等待", "Wait");
			default: return SRApi.T("空闲", "Idle");
			}
		}

		private static void ResetState() {
			_gc = null;
			_phase = GameControl.GamePhase.NONE;
			_phaseStart = 0f;
			_eventSeen = false;
			_hasGame = false;
			_timerText = "00:00";
			_warnLevel = 0;
			_roundText = null;
			_noticeText = null;
			_noticeUntil = 0f;
			_vsPlaceSeen = false;
			_beepedPhase = false;
			ClearScores();
			_dragging = false;
		}

		private static void ResetStateSafe() {
			try { ResetState(); } catch (Exception __ex) { SRApi.Guard.Log("回合计时 重置", __ex); }
		}

		// ---- OnGUI HUD ----
		private sealed class HudComponent : MonoBehaviour {
			private void OnGUI() {
				try { DrawHud(); } catch (Exception __ex) { SRApi.Guard.Log("回合计时 HUD 绘制", __ex); }
			}
		}

		private static void EnsureStyles() {
			if (_white == null) {
				_white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
				_white.SetPixel(0, 0, Color.white);
				_white.Apply();
				_white.hideFlags = HideFlags.HideAndDontSave;
			}
			if (_phaseStyle == null) {
				GUIStyle src = SRApi.Ctl.Label;
				_phaseStyle = (src != null) ? new GUIStyle(src) : new GUIStyle(GUI.skin.label);
				_phaseStyle.wordWrap = false;
				_phaseStyle.richText = false;
				_phaseStyle.fontStyle = FontStyle.Bold;
				_phaseStyle.alignment = TextAnchor.MiddleLeft;
				_phaseStyle.padding = new RectOffset(0, 0, 0, 0);
				_phaseStyle.margin = new RectOffset(0, 0, 0, 0);
				_timerStyle = new GUIStyle(_phaseStyle);
				_smallStyle = new GUIStyle(_phaseStyle);
				_smallStyle.fontStyle = FontStyle.Normal;
				_noticeStyle = new GUIStyle(_smallStyle);
				_noticeStyle.fontStyle = FontStyle.Italic;
			}
			int baseFont = FontSize();
			float scale = SRApi.Ctl.Sc(1f);
			if (scale <= 0f) scale = 1f;
			int f = Mathf.Max(8, Mathf.RoundToInt((float)baseFont * scale));
			if (f != _styleFont) {
				_styleFont = f;
				_phaseStyle.fontSize = f;
				_timerStyle.fontSize = Mathf.Max(f + 4, Mathf.RoundToInt((float)f * 1.7f));
				_smallStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt((float)f * 0.85f));
				_noticeStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt((float)f * 0.9f));
			}
		}

		private static void DrawHud() {
			if (!IsEnabled() || !SRApi.GateMaster) return;
			if (!_hasGame || _phase == GameControl.GamePhase.NONE) return;
			EnsureStyles();
			float op = Opacity();
			string phaseName = PhaseName(_phase);
			string timer = _timerText;
			string round = ShowRoundOn() ? _roundText : null;
			int scoreCount = ShowScoresOn() ? _scoreLineCount : 0;
			string notice = (PhaseNoticeOn() && Time.unscaledTime < _noticeUntil) ? _noticeText : null;

			float pad = SRApi.Ctl.Sc(9f);
			float gap = SRApi.Ctl.Sc(3f);
			float hPhase = (float)_phaseStyle.fontSize + gap;
			float hTimer = (float)_timerStyle.fontSize + gap;
			float hSmall = (float)_smallStyle.fontSize + gap;

			float w = Measure(phaseName, _phaseStyle);
			w = Mathf.Max(w, Measure(timer, _timerStyle));
			if (round != null) w = Mathf.Max(w, Measure(round, _smallStyle));
			for (int i = 0; i < scoreCount; i++) w = Mathf.Max(w, Measure(_scoreLines[i], _smallStyle));
			if (notice != null) w = Mathf.Max(w, Measure(notice, _noticeStyle));
			float panelW = Mathf.Max(w + pad * 2f + SRApi.Ctl.Sc(4f), SRApi.Ctl.Sc(120f));
			float panelH = hPhase + hTimer + pad * 2f;
			if (round != null) panelH += hSmall;
			if (scoreCount > 0) panelH += hSmall * scoreCount;
			if (notice != null) panelH += hSmall;

			float x, y;
			if (_dragging) { x = _dragX; y = _dragY; } else { x = PosX(); y = PosY(); }
			x = Mathf.Clamp(x, 0f, Mathf.Max(0f, (float)Screen.width - panelW));
			y = Mathf.Clamp(y, 0f, Mathf.Max(0f, (float)Screen.height - panelH));
			Rect panel = new Rect(x, y, panelW, panelH);
			HandleDrag(panel);

			Event ev = Event.current;
			if (ev == null || ev.type != EventType.Repaint) return;
			GUI.depth = 1; // 低于核心窗口（0）的层级，设置页打开时 HUD 不会盖住窗口

			// 半透明底
			Color bg = new Color(0f, 0f, 0f, 0.55f * op);
			GUI.color = bg;
			GUI.DrawTexture(panel, _white);
			GUI.color = Color.white;

			// 边框颜色跟随警告级别（黄/红），正常时是一条很淡的白边。
			Color edge = (_warnLevel >= 2)
				? new Color(1f, 0.3f, 0.25f, 0.9f * op)
				: (_warnLevel == 1 ? new Color(1f, 0.85f, 0.2f, 0.8f * op) : new Color(1f, 1f, 1f, 0.18f * op));
			float bw = Mathf.Max(1f, SRApi.Ctl.Sc(2f));
			GUI.color = edge;
			GUI.DrawTexture(new Rect(panel.x, panel.y, panel.width, bw), _white);
			GUI.DrawTexture(new Rect(panel.x, panel.yMax - bw, panel.width, bw), _white);
			GUI.DrawTexture(new Rect(panel.x, panel.y, bw, panel.height), _white);
			GUI.DrawTexture(new Rect(panel.xMax - bw, panel.y, bw, panel.height), _white);
			GUI.color = Color.white;

			float blink = 1f;
			if (_warnLevel >= 2 && BlinkOn()) blink = (Mathf.Sin(Time.unscaledTime * 12f) > 0f) ? 1f : 0.25f;
			Color phaseCol = new Color(1f, 1f, 1f, op);
			Color timerCol = (_warnLevel >= 2)
				? new Color(1f, 0.3f, 0.25f, op * blink)
				: (_warnLevel == 1 ? new Color(1f, 0.85f, 0.2f, op) : new Color(1f, 1f, 1f, op));
			Color smallCol = new Color(1f, 1f, 1f, 0.75f * op);
			Color noticeCol = new Color(1f, 0.9f, 0.4f, op);

			float tw = panel.width - pad * 2f;
			float ty = panel.y + pad;
			_phaseStyle.normal.textColor = phaseCol;
			GUI.Label(new Rect(panel.x + pad, ty, tw, hPhase), phaseName, _phaseStyle);
			ty += hPhase;
			_timerStyle.normal.textColor = timerCol;
			GUI.Label(new Rect(panel.x + pad, ty, tw, hTimer), timer, _timerStyle);
			ty += hTimer;
			if (round != null) {
				_smallStyle.normal.textColor = smallCol;
				GUI.Label(new Rect(panel.x + pad, ty, tw, hSmall), round, _smallStyle);
				ty += hSmall;
			}
			// 得分/淘汰行：淘汰的玩家整行压暗，仍然显示分数。
			for (int i = 0; i < scoreCount; i++) {
				string line = _scoreLines[i];
				if (line == null) continue;
				bool outFlag = i < _scoreRowCount && _scoreRows[i].outFlag;
				_smallStyle.normal.textColor = outFlag ? new Color(1f, 0.62f, 0.62f, 0.45f * op) : smallCol;
				GUI.Label(new Rect(panel.x + pad, ty, tw, hSmall), line, _smallStyle);
				ty += hSmall;
			}
			if (notice != null) {
				_noticeStyle.normal.textColor = noticeCol;
				GUI.Label(new Rect(panel.x + pad, ty, tw, hSmall), notice, _noticeStyle);
			}
		}

		private static float Measure(string text, GUIStyle style) {
			if (string.IsNullOrEmpty(text) || style == null) return 0f;
			try { return style.CalcSize(new GUIContent(text)).x; } catch { return 0f; }
		}

		// 拖动：按住 HUD 左键拖动，松开时写回 Pos X/Pos Y；设置窗口打开时不抢鼠标。
		private static void HandleDrag(Rect panel) {
			Event e = Event.current;
			if (e == null) return;
			if (SRApi.UiOpen || SRApi.BlockInput) {
				if (_dragging) { _dragging = false; SavePos(); }
				return;
			}
			switch (e.type) {
			case EventType.MouseDown:
				if (e.button == 0 && panel.Contains(e.mousePosition)) {
					_dragging = true;
					_dragX = panel.x;
					_dragY = panel.y;
					_dragOffX = e.mousePosition.x - panel.x;
					_dragOffY = e.mousePosition.y - panel.y;
					e.Use();
				}
				break;
			case EventType.MouseDrag:
				if (_dragging) {
					_dragX = Mathf.Clamp(e.mousePosition.x - _dragOffX, 0f, Mathf.Max(0f, (float)Screen.width - panel.width));
					_dragY = Mathf.Clamp(e.mousePosition.y - _dragOffY, 0f, Mathf.Max(0f, (float)Screen.height - panel.height));
					e.Use();
				}
				break;
			case EventType.MouseUp:
				if (_dragging && e.button == 0) {
					_dragging = false;
					SavePos();
					e.Use();
				}
				break;
			}
		}

		private static void SavePos() {
			try {
				int nx = Mathf.Max(0, Mathf.RoundToInt(_dragX));
				int ny = Mathf.Max(0, Mathf.RoundToInt(_dragY));
				if (nx > Screen.width - 8) nx = Mathf.Max(0, Screen.width - 8);
				if (ny > Screen.height - 8) ny = Mathf.Max(0, Screen.height - 8);
				if (_posX != null) _posX.Value = nx;
				if (_posY != null) _posY.Value = ny;
				if (_mp != null && _mp.Config != null) _mp.Config.Save();
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 保存位置", __ex); }
		}
	}
}
