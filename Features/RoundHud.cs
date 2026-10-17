// 回合计时 / 阶段提醒：本地只读 HUD，显示阶段倒计时、回合进度与玩家面板。
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
		// HUD 布局：四角模式按 Edge Margin 距屏幕边缘定位；Custom 用运行期拖动位置（会话内有效）。
		public enum HudLayout {
			TopLeft,
			TopRight,
			BottomLeft,
			BottomRight,
			Custom
		}

		private static ConfigEntry<bool> _enabled;
		private static ConfigEntry<HudLayout> _layout;
		private static ConfigEntry<int> _edgeMargin;
		private static ConfigEntry<int> _fontSize;
		private static ConfigEntry<float> _opacity;
		private static ConfigEntry<int> _warnSeconds;
		private static ConfigEntry<bool> _blink;
		private static ConfigEntry<bool> _phaseNotice;
		private static ConfigEntry<bool> _showScores;
		private static ConfigEntry<bool> _showRound;
		private static ConfigEntry<bool> _beep;
		private static ConfigEntry<float> _beepVolume;
		private static ConfigEntry<bool> _showPhase;
		private static ConfigEntry<bool> _showCountdown;
		private static ConfigEntry<bool> _showPlayers;
		private static ConfigEntry<bool> _showEliminations;
		private static ConfigEntry<bool> _showLevel;
		private static ConfigEntry<bool> _showPanel;
		private static ConfigEntry<string> _extraLine1;
		private static ConfigEntry<string> _extraLine2;

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
		// 内容块缓存（Tick 里按开关更新，OnGUI 只取字符串）。
		private static string _levelText;
		private static string _playerCountText;
		private static string _extra1;
		private static string _extra2;

		// 反射缓存：VersusControl 的三个计时字段（protected float）。
		private static FieldInfo _fPlacementTimer;
		private static FieldInfo _fLevelTimer;
		private static FieldInfo _fCountdownTimer;

		// 旧的"得分列表"（Show Scores）：Tick 里排序并缓存成最多 4 行文本。
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

		// 新的"玩家面板"：所有玩家的名字/角色/分数/达标/房主/投票。
		private struct PlayerRow {
			public string name;
			public string animal;
			public int score;
			public bool hasScore;
			public bool goalReached;
			public bool eliminated;
			public bool host;
			public string vote;
		}
		private static readonly PlayerRow[] _playerRows = new PlayerRow[8];
		private static int _playerRowCount;
		private static readonly string[] _panelLines = new string[10];
		private static int _panelLineCount;
		private static int _panelSig;

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
		// 行类型：0 = 阶段（粗体），1 = 倒计时（大字），2 = 普通小字，3 = 阶段提示，4 = 阶段+倒计时同行。
		private const int LinePhase = 0;
		private const int LineTimer = 1;
		private const int LineSmall = 2;
		private const int LineNotice = 3;
		private const int LinePhaseTimer = 4;
		// 本帧要画的行（复用数组，避免每帧分配）；_hudDim = 该行是否压暗（淘汰玩家）。
		private static readonly string[] _hudLines = new string[24];
		private static readonly int[] _hudKinds = new int[24];
		private static readonly bool[] _hudDim = new bool[24];
		private static readonly float[] _hudHeights = new float[24];
		// HUD 尺寸缓存：首帧用估算值，之后每帧在绘制前按实际内容校正（四角定位据此换算）。
		private static float _hudW = 260f;
		private static float _hudH = 150f;
		// 自定义布局位置：只存运行期内存，会话内保持，不写配置（Pos X/Pos Y 已删除）。
		private static float _customX = 16f;
		private static float _customY = 120f;
		// 拖动状态。
		private static bool _dragging;
		private static float _dragOffX;
		private static float _dragOffY;

		// 设置页分组用的键集合（第一页：内容/外观/行为；第二页：玩家面板）。
		private static readonly HashSet<string> _keysContent = new HashSet<string> {
			"Show Phase", "Show Countdown", "Show Round", "Show Level", "Extra Line 1", "Extra Line 2"
		};
		private static readonly HashSet<string> _keysAppearance = new HashSet<string> {
			"Layout", "Edge Margin", "Font Size", "Opacity"
		};
		private static readonly HashSet<string> _keysBehavior = new HashSet<string> {
			"Enabled", "Warning Seconds", "Blink", "Phase Notice", "Beep", "Beep Volume"
		};
		private static readonly HashSet<string> _keysPlayerPanel = new HashSet<string> {
			"Show Player Panel", "Show Scores", "Show Players", "Show Eliminations"
		};

		// ---- 配置取值（全部 Clamp：手改 cfg 可能拿到超范围值）----
		private static bool IsEnabled() {
			try { return _enabled != null && _enabled.Value; } catch { return false; }
		}

		private static int EdgeMargin() {
			try { return _edgeMargin != null ? Mathf.Clamp(_edgeMargin.Value, 0, 200) : 16; } catch { return 16; }
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

		private static HudLayout LayoutValue() {
			try { return _layout != null ? _layout.Value : HudLayout.TopLeft; } catch { return HudLayout.TopLeft; }
		}

		private static bool ShowPhaseOn() {
			try { return _showPhase != null && _showPhase.Value; } catch { return true; }
		}

		private static bool ShowCountdownOn() {
			try { return _showCountdown != null && _showCountdown.Value; } catch { return true; }
		}

		private static bool ShowPlayersOn() {
			try { return _showPlayers != null && _showPlayers.Value; } catch { return false; }
		}

		private static bool ShowEliminationsOn() {
			try { return _showEliminations != null && _showEliminations.Value; } catch { return true; }
		}

		private static bool ShowLevelOn() {
			try { return _showLevel != null && _showLevel.Value; } catch { return false; }
		}

		private static bool ShowPanelOn() {
			try { return _showPanel != null && _showPanel.Value; } catch { return false; }
		}

		private static void SelfReg() {
			// 本文件反射的游戏私有成员，改名时启动自检会在日志里告警。
			SRApi.Ref.Override("VersusControl.placementTimer", "回合计时（建造阶段剩余时间）");
			SRApi.Ref.Override("VersusControl.levelTimer", "回合计时（对局已用时间）");
			SRApi.Ref.Override("VersusControl.countdownTimer", "回合计时（决胜倒计时）");
			// 枚举下拉框的中文显示名（英文模式仍显示枚举原名）。
			SRApi.LocEnum("TopLeft", "左上");
			SRApi.LocEnum("TopRight", "右上");
			SRApi.LocEnum("BottomLeft", "左下");
			SRApi.LocEnum("BottomRight", "右下");
			SRApi.LocEnum("Custom", "自定义");
			// 侧栏显示名改成 "HUD"；配置段名保持 "Round HUD" 避免迁移。
			SRApi.LocSec("Round HUD", "HUD", "HUD");
			SRApi.Nav("Round HUD", 25); // 侧栏顺序 25：移动轨迹(20) 之后、快速调整(30) 之前
			// 两页：第一页 HUD（内容/外观/行为），第二页玩家面板。
			SRApi.RegisterPage("Round HUD", "hud", "HUD", "HUD", 10, RenderPage1);
			SRApi.RegisterPage("Round HUD", "players", "玩家面板", "Players", 20, RenderPage2);
			SRApi.LocKey("Round HUD", "Enabled", "回合计时 HUD", null);
			SRApi.LocDesc("Round HUD", "Enabled", "回合计时 HUD 开关\n在画面上显示当前阶段、阶段倒计时和回合信息\n纯本地只读，不发送任何网络消息，不影响游戏判定", "round HUD switch\nshows the current phase, phase countdown and round info on screen\nlocal read-only; sends no network messages and does not affect gameplay");
			SRApi.LocKey("Round HUD", "Layout", "布局", null);
			SRApi.LocDesc("Round HUD", "Layout", "HUD 布局\n左上/右上/左下/右下按 Edge Margin 自动贴边，自定义用运行期拖动位置\n自定义位置只在本次会话内保持，不写入配置", "HUD layout\nthe four corner modes stick to the screen edges according to Edge Margin; custom uses the runtime drag position\ncustom position lasts for this session only and is not written to config");
			SRApi.LocKey("Round HUD", "Edge Margin", "四角边距", null);
			SRApi.LocDesc("Round HUD", "Edge Margin", "四角布局边距\n左上/右上/左下/右下布局下 HUD 距屏幕边缘的像素数\n范围 0 到 200", "corner layout margin\ndistance in pixels between the HUD and the screen edge in the four corner layouts\nrange 0-200");
			SRApi.LocKey("Round HUD", "Font Size", "字体大小", null);
			SRApi.LocDesc("Round HUD", "Font Size", "HUD 字体大小\nHUD 文字的基础字号，会随界面缩放一起放大\n范围 10 到 48", "HUD font size\nbase font size of the HUD text, scaled with the UI scale\nrange 10-48");
			SRApi.LocKey("Round HUD", "Opacity", "不透明度", null);
			SRApi.LocDesc("Round HUD", "Opacity", "HUD 不透明度\n同时控制背景和文字的透明程度\n范围 0.2 到 1，1 为完全不透明", "HUD opacity\ncontrols the transparency of both the background and the text\nrange 0.2-1, 1 = fully opaque");
			SRApi.LocKey("Round HUD", "Warning Seconds", "警告秒数", null);
			SRApi.LocDesc("Round HUD", "Warning Seconds", "倒计时警告秒数\n剩余时间低于该秒数时倒计时文字变黄\n最后 5 秒固定变红，可配合闪烁开关；范围 5 到 60", "countdown warning seconds\nthe countdown turns yellow when less time than this remains\nit always turns red in the last 5 seconds; can blink; range 5-60");
			SRApi.LocKey("Round HUD", "Blink", "闪烁提醒", null);
			SRApi.LocDesc("Round HUD", "Blink", "最后 5 秒闪烁\n倒计时进入最后 5 秒时文字闪烁提醒\n关闭则只在最后 5 秒显示红色", "blink in the last 5 seconds\nmakes the countdown blink during the final 5 seconds\nOFF shows a steady red countdown instead");
			SRApi.LocKey("Round HUD", "Phase Notice", "阶段提示", null);
			SRApi.LocDesc("Round HUD", "Phase Notice", "阶段切换提示\n阶段切换时在 HUD 里短暂显示新阶段名称\n显示约 2 秒后自动消失", "phase change notice\nbriefly shows the new phase name in the HUD when the phase changes\nfades out automatically after about 2 seconds");
			SRApi.LocKey("Round HUD", "Show Phase", "显示阶段名", null);
			SRApi.LocDesc("Round HUD", "Show Phase", "显示阶段名\n阶段名与倒计时显示在同一行（如「游玩 0:42」）\n关闭后只保留其他已启用内容", "show phase name\nshows the phase name on the same line as the countdown (e.g. Play 0:42)\nwhen off only the other enabled lines are shown");
			SRApi.LocKey("Round HUD", "Show Countdown", "显示倒计时", null);
			SRApi.LocDesc("Round HUD", "Show Countdown", "显示倒计时\n显示阶段倒计时（读不到时长时显示已用时间）\n关闭后仍会计算最后 5 秒提示音", "show countdown\nshows the phase countdown (elapsed time when no duration is available)\nthe last-5-seconds beep is still evaluated when off");
			SRApi.LocKey("Round HUD", "Show Round", "显示回合进度", null);
			SRApi.LocDesc("Round HUD", "Show Round", "显示回合进度\n在 HUD 中显示第 N 回合与目标回合/胜场数\n取不到回合信息时自动隐藏该行", "show round progress\nshows the current round and the target round/win count in the HUD\nhidden automatically when round info is unavailable");
			SRApi.LocKey("Round HUD", "Show Level", "显示关卡/模式", null);
			SRApi.LocDesc("Round HUD", "Show Level", "显示关卡/模式\n在 HUD 中显示当前关卡名与对局模式\n关卡或模式取不到时只显示能读到的部分，都取不到则隐藏", "show level/mode\nshows the current level name and game mode in the HUD\nshows whichever part is available; hidden when neither is available");
			SRApi.LocKey("Round HUD", "Extra Line 1", "额外信息行 1", null);
			SRApi.LocDesc("Round HUD", "Extra Line 1", "额外信息行 1\n自定义文本，按原文显示在 HUD 里（不做翻译）\n留空则不显示；过长会截断到 120 字符", "extra line 1\ncustom text shown in the HUD as typed (not translated)\nempty = hidden; truncated to 120 characters");
			SRApi.LocKey("Round HUD", "Extra Line 2", "额外信息行 2", null);
			SRApi.LocDesc("Round HUD", "Extra Line 2", "额外信息行 2\n自定义文本，按原文显示在 HUD 里（不做翻译）\n留空则不显示；过长会截断到 120 字符", "extra line 2\ncustom text shown in the HUD as typed (not translated)\nempty = hidden; truncated to 120 characters");
			SRApi.LocKey("Round HUD", "Beep", "最后 5 秒提示音", null);
			SRApi.LocDesc("Round HUD", "Beep", "最后 5 秒提示音\n倒计时进入最后 5 秒时响一声，每个阶段只响一次\n使用运行时生成的简单提示音，不依赖游戏音频资源", "last-5-seconds beep\nplays one beep when the countdown enters its final 5 seconds, once per phase\nuses a beep generated at runtime; no game audio assets are used");
			SRApi.LocKey("Round HUD", "Beep Volume", "提示音音量", null);
			SRApi.LocDesc("Round HUD", "Beep Volume", "提示音音量\n最后 5 秒提示音的音量\n范围 0 到 1，0 为静音", "beep volume\nvolume of the last-5-seconds beep\nrange 0-1, 0 = silent");
			SRApi.LocKey("Round HUD", "Show Player Panel", "显示玩家面板", null);
			SRApi.LocDesc("Round HUD", "Show Player Panel", "玩家面板\n显示所有玩家的名字、角色、分数与是否达到制胜目标\n树屋/大厅额外显示房主标识与投票关卡；取不到玩家数据时整段隐藏", "player panel\nshows every player's name, character, score and whether the win target is reached\nin the treehouse/lobby it also shows the host mark and voted level; hidden when player data is unavailable");
			SRApi.LocKey("Round HUD", "Show Scores", "显示得分列表", null);
			SRApi.LocDesc("Round HUD", "Show Scores", "显示得分列表\nHUD 内增加最多 4 行的玩家得分列表，按分数从高到低排序\n只读取本局得分与淘汰状态，取不到时整段隐藏", "show score list\nadds a list of up to 4 players to the HUD, sorted from highest to lowest score\nreads this match's scores and eliminated state only; hidden when unavailable");
			SRApi.LocKey("Round HUD", "Show Players", "显示玩家数", null);
			SRApi.LocDesc("Round HUD", "Show Players", "显示玩家数\n在 HUD 中显示当前玩家数量\n取不到玩家列表时隐藏该行", "show player count\nshows the number of players in the HUD\nhidden when the player list is unavailable");
			SRApi.LocKey("Round HUD", "Show Eliminations", "显示淘汰标记", null);
			SRApi.LocDesc("Round HUD", "Show Eliminations", "显示淘汰标记\n在玩家面板与得分列表里给已淘汰玩家追加标记\n仅影响文字标记，不影响排序与压暗", "show eliminated marks\nappends an eliminated marker to dead players in the player panel and score list\naffects the text marker only; sorting and dimming still apply");
		}

		// ---- 设置页渲染：两页（第一页再按内容/外观/行为分组）----
		private static bool IsHudEntry(ConfigEntryBase e, HashSet<string> keys) {
			try {
				return e != null && keys != null && e.Definition.Section == "Round HUD" && keys.Contains(e.Definition.Key);
			} catch { return false; }
		}

		private static bool RowContent(ConfigEntryBase e) { return IsHudEntry(e, _keysContent); }
		private static bool RowAppearance(ConfigEntryBase e) { return IsHudEntry(e, _keysAppearance); }
		private static bool RowBehavior(ConfigEntryBase e) { return IsHudEntry(e, _keysBehavior); }
		private static bool RowPlayerPanel(ConfigEntryBase e) { return IsHudEntry(e, _keysPlayerPanel); }

		private static bool RowOther(ConfigEntryBase e) {
			try {
				if (e == null || e.Definition.Section != "Round HUD") return false;
				string k = e.Definition.Key;
				return !_keysContent.Contains(k) && !_keysAppearance.Contains(k) && !_keysBehavior.Contains(k) && !_keysPlayerPanel.Contains(k);
			} catch { return false; }
		}

		private static readonly Func<ConfigEntryBase, bool> _rowContent = RowContent;
		private static readonly Func<ConfigEntryBase, bool> _rowAppearance = RowAppearance;
		private static readonly Func<ConfigEntryBase, bool> _rowBehavior = RowBehavior;
		private static readonly Func<ConfigEntryBase, bool> _rowPlayerPanel = RowPlayerPanel;
		private static readonly Func<ConfigEntryBase, bool> _rowOther = RowOther;

		private static void RenderPage1() {
			try {
				List<ConfigEntryBase> list = SRApi.SectionEntries("Round HUD");
				if (list == null || list.Count == 0) { GUILayout.Label(SRApi.T("（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label); return; }
				SRApi.Ctl.SectionTitle(SRApi.T("- 显示内容 -", "- Content -"));
				SRApi.RenderSectionEntries("Round HUD", _rowContent);
				SRApi.Ctl.SectionTitle(SRApi.T("- 外观 -", "- Appearance -"));
				SRApi.RenderSectionEntries("Round HUD", _rowAppearance);
				SRApi.Ctl.SectionTitle(SRApi.T("- 行为 -", "- Behavior -"));
				SRApi.RenderSectionEntries("Round HUD", _rowBehavior);
				SRApi.RenderSectionEntries("Round HUD", _rowOther);
			} catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 页", __ex); }
		}

		private static void RenderPage2() {
			try {
				List<ConfigEntryBase> list = SRApi.SectionEntries("Round HUD");
				if (list == null || list.Count == 0) { GUILayout.Label(SRApi.T("（无匹配条目）", "(no matching entries)"), SRApi.Ctl.Label); return; }
				SRApi.Ctl.SectionTitle(SRApi.T("- 玩家面板 -", "- Player panel -"));
				SRApi.RenderSectionEntries("Round HUD", _rowPlayerPanel);
			} catch (Exception __ex) { SRApi.Guard.Log("渲染 HUD 玩家面板页", __ex); }
		}

		// 字体/透明度/四角边距用滑块条渲染（注册到核心的条目行滑块范围扩展点）。
		private static bool IsHudKey(ConfigEntryBase e, string key) {
			try { return e != null && e.Definition.Section == "Round HUD" && e.Definition.Key == key; } catch { return false; }
		}

		private static bool FontSizeSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
			min = 10f; max = 48f; fmt = "0";
			return IsHudKey(e, "Font Size");
		}

		private static bool OpacitySlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
			min = 0.2f; max = 1f; fmt = "0.00";
			return IsHudKey(e, "Opacity");
		}

		private static bool EdgeMarginSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
			min = 0f; max = 200f; fmt = "0";
			return IsHudKey(e, "Edge Margin");
		}

		// 初始化需整段 try：中途抛异常会留下常驻对象且功能静默消失。
		public void Initialize(IFeatureHost plugin) {
			try {
				SelfReg();
				ConfigFile cfg = plugin.Config;
				_enabled = cfg.Bind("Round HUD", "Enabled", true, "总开关：在画面上显示当前阶段、阶段倒计时和回合信息（纯本地只读）。");
				_layout = cfg.Bind("Round HUD", "Layout", HudLayout.TopLeft, "HUD 布局：左上/右上/左下/右下按边距贴边；自定义用运行期拖动位置。");
				_edgeMargin = cfg.Bind("Round HUD", "Edge Margin", 16,
					new ConfigDescription("四角布局边距（0-200 像素）：HUD 距屏幕边缘的距离。", new AcceptableValueRange<int>(0, 200)));
				_fontSize = cfg.Bind("Round HUD", "Font Size", 18,
					new ConfigDescription("HUD 字体大小：基础字号（10-48），会随界面缩放放大。", new AcceptableValueRange<int>(10, 48)));
				_opacity = cfg.Bind("Round HUD", "Opacity", 0.85f,
					new ConfigDescription("HUD 不透明度（0.2-1）：同时控制背景和文字，1 为完全不透明。", new AcceptableValueRange<float>(0.2f, 1f)));
				_showPhase = cfg.Bind("Round HUD", "Show Phase", true, "显示阶段名：阶段名与倒计时显示在同一行。");
				_showCountdown = cfg.Bind("Round HUD", "Show Countdown", true, "显示倒计时：显示阶段倒计时（读不到时长时显示已用时间）。");
				_showRound = cfg.Bind("Round HUD", "Show Round", true, "显示回合进度：在 HUD 中显示第 N 回合与目标回合/胜场数（取不到时隐藏）。");
				_showLevel = cfg.Bind("Round HUD", "Show Level", false, "显示关卡/模式：在 HUD 中显示当前关卡名与对局模式（取不到时隐藏）。");
				_extraLine1 = cfg.Bind("Round HUD", "Extra Line 1", "", "额外信息行 1：自定义文本，按原文显示（不做翻译），留空则不显示。");
				_extraLine2 = cfg.Bind("Round HUD", "Extra Line 2", "", "额外信息行 2：自定义文本，按原文显示（不做翻译），留空则不显示。");
				_warnSeconds = cfg.Bind("Round HUD", "Warning Seconds", 10,
					new ConfigDescription("倒计时警告秒数（5-60）：剩余时间低于该值时文字变黄；最后 5 秒固定变红。", new AcceptableValueRange<int>(5, 60)));
				_blink = cfg.Bind("Round HUD", "Blink", false, "最后 5 秒闪烁：倒计时进入最后 5 秒时文字闪烁提醒。");
				_phaseNotice = cfg.Bind("Round HUD", "Phase Notice", true, "阶段切换提示：阶段切换时在 HUD 里短暂显示新阶段名称（约 2 秒）。");
				_beep = cfg.Bind("Round HUD", "Beep", true, "最后 5 秒提示音：倒计时进入最后 5 秒时响一声，每个阶段只响一次。");
				_beepVolume = cfg.Bind("Round HUD", "Beep Volume", 0.5f,
					new ConfigDescription("提示音音量（0-1）：最后 5 秒提示音的音量，0 为静音。", new AcceptableValueRange<float>(0f, 1f)));
				_showPanel = cfg.Bind("Round HUD", "Show Player Panel", false, "显示玩家面板：显示所有玩家的名字、角色、分数与达标状态；树屋/大厅显示房主与投票关卡。");
				_showScores = cfg.Bind("Round HUD", "Show Scores", false, "显示得分列表：在 HUD 内增加最多 4 行玩家得分/淘汰列表（只读本局数据，取不到时隐藏）。");
				_showPlayers = cfg.Bind("Round HUD", "Show Players", false, "显示玩家数：在 HUD 中显示当前玩家数量（取不到时隐藏）。");
				_showEliminations = cfg.Bind("Round HUD", "Show Eliminations", true, "显示淘汰标记：在玩家面板与得分列表里给已淘汰玩家追加标记。");

				// 条目行滑块范围扩展点：让设置页把这三项渲染成滑块条而不是编辑框。
				try {
					SRApi.RowSliders.Add(FontSizeSlider);
					SRApi.RowSliders.Add(OpacitySlider);
					SRApi.RowSliders.Add(EdgeMarginSlider);
				} catch (Exception __ex) { SRApi.Guard.Log("回合计时 滑块注册", __ex); }

				// 已删除的配置键：自动从旧 cfg 清理（幂等；原文件由核心备份）。
				try { SRApi.Migration.Remove("Round HUD", "Pos X", "Pos Y"); }
				catch (Exception __ex) { SRApi.Guard.Log("回合计时 清理旧键", __ex); }

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
					// 不在对局：清掉对局内容，但玩家面板仍可在树屋/大厅显示。
					if (_gc != null || _hasGame) ResetGameState();
					bool needLobbyPanel = ShowPanelOn() || ShowPlayersOn();
					GameSettings gsLobby = needLobbyPanel ? ReadSettings() : null;
					UpdatePlayerPanel(null, gsLobby);
					UpdateExtraText();
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
				UpdateLevelBlock(gs);
				UpdatePlayerPanel(gc, gs);
				UpdateExtraText();
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

		// 旧的得分列表：只读 ScoreKeeper（玩家总分）与 GamePlayer（名字、角色死亡状态），最多 4 行。
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
						_scoreRows[n].name = PlayerDisplayName(gp);
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
				sig = sig * 31 + (ShowEliminationsOn() ? 1 : 0);
				for (int i = 0; i < shown; i++) {
					sig = sig * 31 + _scoreRows[i].score;
					sig = sig * 31 + (_scoreRows[i].outFlag ? 1 : 0);
					sig = sig * 31 + (_scoreRows[i].name != null ? _scoreRows[i].name.GetHashCode() : 0);
				}
				if (sig != _scoreSig) {
					_scoreSig = sig;
					for (int i = 0; i < shown; i++) {
						string line = _scoreRows[i].name + "  " + _scoreRows[i].score + SRApi.T(" 分", " pts");
						if (_scoreRows[i].outFlag && ShowEliminationsOn()) line += SRApi.T(" · 淘汰", " · out");
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

		private static string PlayerDisplayName(GamePlayer gp) {
			string name = null;
			try { name = gp.playerName; } catch { }
			if (string.IsNullOrEmpty(name)) {
				try { name = gp.ValidatedDisplayName; } catch { }
			}
			if (!string.IsNullOrEmpty(name)) name = name.Trim();
			int num = 0;
			try { num = gp.networkNumber; } catch { num = 0; }
			if (string.IsNullOrEmpty(name)) name = "P" + num;
			return Truncate(name, 12);
		}

		// ---- 玩家面板：所有玩家的状态（名字/角色/分数/达标；树屋/大厅加房主/投票）----
		private static void UpdatePlayerPanel(GameControl gc, GameSettings gs) {
			try {
				bool needRows = ShowPanelOn() || ShowPlayersOn();
				if (!needRows) {
					ClearPanel();
					_playerCountText = null;
					return;
				}
				int target;
				bool roundsMode;
				CalcTarget(gs, out target, out roundsMode);
				int n = 0;
				if (gc != null) CollectGamePlayers(gc, target, roundsMode, ref n);
				else CollectLobbyPlayers(ref n);
				_playerRowCount = n;
				_playerCountText = (ShowPlayersOn() && n > 0) ? (SRApi.T("玩家：", "Players: ") + n) : null;
				if (!ShowPanelOn() || n == 0) { ClearPanelLines(); return; }
				int sig = SRApi.LangVer;
				sig = sig * 31 + (ShowEliminationsOn() ? 1 : 0);
				sig = sig * 31 + target;
				sig = sig * 31 + (roundsMode ? 1 : 0);
				sig = sig * 31 + n;
				for (int i = 0; i < n; i++) {
					PlayerRow r = _playerRows[i];
					sig = sig * 31 + r.score;
					sig = sig * 31 + (r.hasScore ? 1 : 0);
					sig = sig * 31 + (r.goalReached ? 1 : 0);
					sig = sig * 31 + (r.eliminated ? 1 : 0);
					sig = sig * 31 + (r.host ? 1 : 0);
					sig = sig * 31 + (r.name != null ? r.name.GetHashCode() : 0);
					sig = sig * 31 + (r.animal != null ? r.animal.GetHashCode() : 0);
					sig = sig * 31 + (r.vote != null ? r.vote.GetHashCode() : 0);
				}
				if (sig == _panelSig) { return; }
				_panelSig = sig;
				int line = 0;
				string head = SRApi.T("玩家 ", "Players ") + "(" + n + ")";
				if (target > 0) {
					head += SRApi.T(" · 制胜目标 ", " · Win target ") + target
						+ (roundsMode ? SRApi.T(" 回合", " rounds") : SRApi.T(" 胜", " wins"));
				}
				_panelLines[line++] = head;
				for (int i = 0; i < n && line < _panelLines.Length; i++) {
					PlayerRow r = _playerRows[i];
					string text = r.name;
					if (!string.IsNullOrEmpty(r.animal)) text += " · " + r.animal;
					if (r.hasScore) text += "  " + r.score + SRApi.T(" 分", " pts");
					if (r.goalReached) text += SRApi.T(" 达标", " GOAL");
					if (r.eliminated && ShowEliminationsOn()) text += SRApi.T(" 淘汰", " OUT");
					if (r.host) text += SRApi.T(" 房主", " HOST");
					if (!string.IsNullOrEmpty(r.vote)) text += SRApi.T(" 投票：", " vote: ") + r.vote;
					_panelLines[line++] = text;
				}
				_panelLineCount = line;
			} catch (Exception __ex) {
				ClearPanel();
				SRApi.Guard.Log("回合计时 玩家面板", __ex);
			}
		}

		// 制胜积分/回合目标：ROUNDS 规则取 MaxRounds，其他规则取 MaxScore。
		private static void CalcTarget(GameSettings gs, out int target, out bool roundsMode) {
			target = 0;
			roundsMode = false;
			try {
				if (gs == null) return;
				if (gs.GameLimitType == GameLimitType.ROUNDS) {
					roundsMode = true;
					target = Mathf.Max(0, gs.MaxRounds);
				} else {
					target = Mathf.Max(0, gs.MaxScore);
				}
			} catch { target = 0; roundsMode = false; }
		}

		private static void CollectGamePlayers(GameControl gc, int target, bool roundsMode, ref int n) {
			try {
				bool versus = gc is VersusControl;
				ScoreKeeper sk = null;
				if (versus) {
					try { sk = ScoreKeeper.Instance; } catch { sk = null; }
				}
				Queue<GamePlayer> queue = gc.CurrentPlayerQueue;
				if (queue == null) return;
				foreach (GamePlayer gp in queue) {
					if (gp == null) continue;
					if (n >= _playerRows.Length) break;
					PlayerRow r = default(PlayerRow);
					r.name = PlayerDisplayName(gp);
					try { r.animal = AnimalName(gp.PickedAnimal, gp.IsWearingSkin); } catch { r.animal = null; }
					if (sk != null) {
						try { r.score = sk.GetPlayerTotal(gp); r.hasScore = true; } catch { r.hasScore = false; }
					}
					r.goalReached = r.hasScore && !roundsMode && target > 0 && r.score >= target;
					try {
						Character c = gp.CharacterInstance;
						if (c != null && (c.Dead || c.Dying)) r.eliminated = true;
					} catch { r.eliminated = false; }
					_playerRows[n] = r;
					n++;
				}
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 收集玩家", __ex); }
		}

		private static void CollectLobbyPlayers(ref int n) {
			try {
				LobbyManager lm = LobbyManager.instance;
				if (lm == null) return;
				foreach (LobbyPlayer lp in lm.GetLobbyPlayers()) {
					if (lp == null) continue;
					if (n >= _playerRows.Length) break;
					PlayerRow r = default(PlayerRow);
					r.name = LobbyDisplayName(lp);
					try { r.animal = AnimalName(lp.PickedAnimal, lp.IsWearingSkin); } catch { r.animal = null; }
					try { r.host = lp.IsHost; } catch { r.host = false; }
					r.vote = FindVote(lp);
					_playerRows[n] = r;
					n++;
				}
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 收集大厅玩家", __ex); }
		}

		private static string LobbyDisplayName(LobbyPlayer lp) {
			string name = null;
			try { name = lp.playerName; } catch { }
			if (string.IsNullOrEmpty(name)) {
				try { name = lp.ValidatedDisplayName; } catch { }
			}
			if (!string.IsNullOrEmpty(name)) name = name.Trim();
			int num = 0;
			try { num = lp.networkNumber; } catch { num = 0; }
			if (string.IsNullOrEmpty(name)) name = "P" + num;
			return Truncate(name, 12);
		}

		// 角色名：游戏本地化的动物名（如 鸡 / Chicken）。
		private static string AnimalName(Character.Animals animal, bool altSkin) {
			try {
				string n = Character.GetLocalizedAnimal(animal, altSkin);
				return string.IsNullOrEmpty(n) ? null : Truncate(n, 10);
			} catch { return null; }
		}

		// 树屋/大厅：从 LevelSelectController 的关卡传送门投票表里找该玩家投的关卡。
		private static string FindVote(LobbyPlayer lp) {
			try {
				if (lp == null) return null;
				Character ch = lp.CharacterInstance;
				if (ch == null) return null;
				LobbyManager lm = LobbyManager.instance;
				if (lm == null) return null;
				LevelSelectController lsc = lm.CurrentLevelSelectController;
				if (lsc == null) return null;
				if (lsc.portals != null) {
					for (int i = 0; i < lsc.portals.Length; i++) {
						LevelPortal p = lsc.portals[i];
						if (p == null || p.Votes == null) continue;
						if (p.Votes.ContainsKey(ch)) return PortalName(p);
					}
				}
				if (lsc.snapshotPortals != null) {
					for (int i = 0; i < lsc.snapshotPortals.Length; i++) {
						LevelPortal p = lsc.snapshotPortals[i];
						if (p == null || p.Votes == null) continue;
						if (p.Votes.ContainsKey(ch)) return PortalName(p);
					}
				}
			} catch (Exception __ex) { SRApi.Guard.Log("回合计时 读取投票", __ex); }
			return null;
		}

		private static string PortalName(LevelPortal p) {
			try {
				CustomLevelPortal cp = p as CustomLevelPortal;
				if (cp != null && cp.snapshotInfo != null && !string.IsNullOrEmpty(cp.snapshotInfo.snapshotName)) {
					return Truncate(cp.snapshotInfo.snapshotName, 14);
				}
				if (p.TargetLevel == GameState.LevelName.RANDOM) return SRApi.T("随机", "Random");
				string lv = GameState.GetLevelLocalizedName(p.TargetLevel);
				if (string.IsNullOrEmpty(lv) || lv.IndexOf("default Level name error", StringComparison.Ordinal) >= 0) {
					lv = p.TargetLevel.ToString();
				}
				return Truncate(lv, 14);
			} catch { return null; }
		}

		private static void ClearPanelLines() {
			_panelLineCount = 0;
			_panelSig = 0;
			for (int i = 0; i < _panelLines.Length; i++) _panelLines[i] = null;
		}

		private static void ClearPanel() {
			ClearPanelLines();
			_playerRowCount = 0;
		}

		private static string Truncate(string s, int max) {
			if (string.IsNullOrEmpty(s)) return s;
			if (max <= 3 || s.Length <= max) return s;
			return s.Substring(0, max - 2) + "..";
		}

		// 关卡/模式行：只读 GameState.SelectedLevel 与 GameSettings.GameMode，取不到就隐藏或只显示能读到的部分。
		private static void UpdateLevelBlock(GameSettings gs) {
			_levelText = null;
			if (!ShowLevelOn()) return;
			try {
				string level = null;
				try {
					GameState st = GameState.GetInstance();
					if (st != null) level = GameState.GetLevelLocalizedName(st.SelectedLevel);
				} catch { level = null; }
				if (!string.IsNullOrEmpty(level) && level.IndexOf("default Level name error", StringComparison.Ordinal) >= 0) level = null;
				string mode = (gs != null) ? ModeName(gs.GameMode) : null;
				string body = level;
				if (!string.IsNullOrEmpty(mode)) body = string.IsNullOrEmpty(body) ? mode : body + " · " + mode;
				if (!string.IsNullOrEmpty(body)) _levelText = SRApi.T("关卡：", "Level: ") + body;
			} catch { _levelText = null; }
		}

		private static string ModeName(GameState.GameMode mode) {
			switch (mode) {
			case GameState.GameMode.FREEPLAY: return SRApi.T("自由", "Freeplay");
			case GameState.GameMode.CREATIVE: return SRApi.T("创造", "Creative");
			case GameState.GameMode.PARTY: return SRApi.T("派对", "Party");
			case GameState.GameMode.CHALLENGE: return SRApi.T("挑战", "Challenge");
			default: return null;
			}
		}

		// 额外信息行：原文显示、不翻译；换行压成空格、最长 120 字符，防超宽/多行撑破 HUD。
		private static void UpdateExtraText() {
			_extra1 = ReadExtraLine(_extraLine1);
			_extra2 = ReadExtraLine(_extraLine2);
		}

		private static string ReadExtraLine(ConfigEntry<string> entry) {
			try {
				string s = entry != null ? entry.Value : null;
				if (string.IsNullOrEmpty(s)) return null;
				s = s.Replace("\r", " ").Replace("\n", " ").Trim();
				if (s.Length > 120) s = s.Substring(0, 120);
				return s.Length > 0 ? s : null;
			} catch { return null; }
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

		// 只清对局相关状态；自定义布局位置（_customX/_customY）与玩家面板/额外文本按会话保留。
		private static void ResetGameState() {
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
			_levelText = null;
			_playerCountText = null;
			ClearScores();
			ClearPanel();
			_dragging = false;
		}

		private static void ResetState() {
			ResetGameState();
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
			bool inGame = _hasGame && _phase != GameControl.GamePhase.NONE;
			bool boardOn = ShowPanelOn() && _panelLineCount > 0;
			bool countOn = ShowPlayersOn() && _playerCountText != null;
			if (!inGame && !boardOn && !countOn) return;
			EnsureStyles();
			float op = Opacity();

			// 1) 按内容开关拼行（状态字符串由 Tick 算好，这里只取）。
			int n = 0;
			if (inGame) {
				bool ph = ShowPhaseOn();
				bool cd = ShowCountdownOn();
				// 阶段名与倒计时同一行：「游玩 0:42」。
				if (ph && cd) AddLine(ref n, PhaseName(_phase) + "  " + _timerText, LinePhaseTimer, false);
				else if (cd) AddLine(ref n, _timerText, LineTimer, false);
				else if (ph) AddLine(ref n, PhaseName(_phase), LinePhase, false);
				if (ShowRoundOn() && _roundText != null) AddLine(ref n, _roundText, LineSmall, false);
				if (ShowLevelOn() && _levelText != null) AddLine(ref n, _levelText, LineSmall, false);
			}
			if (ShowPlayersOn() && _playerCountText != null) AddLine(ref n, _playerCountText, LineSmall, false);
			if (boardOn) {
				for (int i = 0; i < _panelLineCount; i++) {
					string line = _panelLines[i];
					if (line != null) AddLine(ref n, line, LineSmall, i > 0 && PlayerRowEliminated(i - 1));
				}
			}
			if (inGame && ShowScoresOn()) {
				for (int i = 0; i < _scoreLineCount; i++) {
					string line = _scoreLines[i];
					if (line == null) continue;
					bool dim = i < _scoreRowCount && _scoreRows[i].outFlag;
					AddLine(ref n, line, LineSmall, dim);
				}
			}
			if (_extra1 != null) AddLine(ref n, _extra1, LineSmall, false);
			if (_extra2 != null) AddLine(ref n, _extra2, LineSmall, false);
			if (inGame && PhaseNoticeOn() && Time.unscaledTime < _noticeUntil && !string.IsNullOrEmpty(_noticeText)) AddLine(ref n, _noticeText, LineNotice, false);
			if (n > _hudLines.Length) n = _hudLines.Length;
			if (n == 0) return; // 全部内容关闭时不画空框

			// 2) 量尺寸：宽度用 CalcSize，行高用核心 TextHeight（实测 CJK 字形下沉，避免裁字）。
			float pad = SRApi.Ctl.Sc(9f);
			float gap = SRApi.Ctl.Sc(3f);
			float contentW = 0f;
			for (int i = 0; i < n; i++) contentW = Mathf.Max(contentW, Measure(_hudLines[i], LineStyle(_hudKinds[i])));
			float panelW = Mathf.Max(contentW + pad * 2f + SRApi.Ctl.Sc(4f), SRApi.Ctl.Sc(120f));
			float textW = Mathf.Max(1f, panelW - pad * 2f);
			float contentH = 0f;
			for (int i = 0; i < n; i++) {
				GUIStyle st = LineStyle(_hudKinds[i]);
				float th = 0f;
				try { th = SRApi.Ctl.TextHeight(_hudLines[i], textW); } catch { th = 0f; }
				if (th <= 0f) th = (st != null ? st.fontSize : 14);
				_hudHeights[i] = th + gap;
				contentH += _hudHeights[i];
			}
			float panelH = contentH + pad * 2f;
			_hudW = panelW;
			_hudH = panelH;

			// 3) 布局（四角按 Edge Margin；Custom 用运行期位置）与拖动命中。
			float x, y;
			LayoutPosition(_hudW, _hudH, out x, out y);
			x = Mathf.Clamp(x, 0f, Mathf.Max(0f, (float)Screen.width - panelW));
			y = Mathf.Clamp(y, 0f, Mathf.Max(0f, (float)Screen.height - panelH));
			Rect panel = new Rect(x, y, panelW, panelH);
			HandleDrag(panel);

			Event ev = Event.current;
			if (ev == null || ev.type != EventType.Repaint) return;
			GUI.depth = 1; // 低于核心窗口（0）的层级，设置页打开时 HUD 不会盖住窗口

			// 4) 半透明底 + 警告色边框（黄/红；正常时淡白边）。
			Color bg = new Color(0f, 0f, 0f, 0.55f * op);
			GUI.color = bg;
			GUI.DrawTexture(panel, _white);
			GUI.color = Color.white;
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

			// 5) 逐行绘制。
			float blink = 1f;
			if (_warnLevel >= 2 && BlinkOn()) blink = (Mathf.Sin(Time.unscaledTime * 12f) > 0f) ? 1f : 0.25f;
			Color phaseCol = new Color(1f, 1f, 1f, op);
			Color timerCol = (_warnLevel >= 2)
				? new Color(1f, 0.3f, 0.25f, op * blink)
				: (_warnLevel == 1 ? new Color(1f, 0.85f, 0.2f, op) : new Color(1f, 1f, 1f, op));
			Color smallCol = new Color(1f, 1f, 1f, 0.75f * op);
			Color dimCol = new Color(1f, 0.62f, 0.62f, 0.45f * op);
			Color noticeCol = new Color(1f, 0.9f, 0.4f, op);

			float tw = panel.width - pad * 2f;
			float ty = panel.y + pad;
			for (int i = 0; i < n; i++) {
				GUIStyle st = LineStyle(_hudKinds[i]);
				if (st == null) continue;
				float lh = _hudHeights[i];
				int kind = _hudKinds[i];
				Color col;
				if (kind == LineTimer || kind == LinePhaseTimer) col = timerCol;
				else if (kind == LinePhase) col = phaseCol;
				else if (kind == LineNotice) col = noticeCol;
				else col = _hudDim[i] ? dimCol : smallCol;
				st.normal.textColor = col;
				GUI.Label(new Rect(panel.x + pad, ty, tw, lh), _hudLines[i], st);
				ty += lh;
			}
		}

		private static bool PlayerRowEliminated(int index) {
			try { return index >= 0 && index < _playerRowCount && _playerRows[index].eliminated; } catch { return false; }
		}

		private static void AddLine(ref int n, string text, int kind, bool dim) {
			if (string.IsNullOrEmpty(text)) return;
			if (n < _hudLines.Length) {
				_hudLines[n] = text;
				_hudKinds[n] = kind;
				_hudDim[n] = dim;
			}
			n++;
		}

		private static GUIStyle LineStyle(int kind) {
			switch (kind) {
			case LinePhase: return _phaseStyle;
			case LineTimer: return _timerStyle;
			case LinePhaseTimer: return _phaseStyle;
			case LineNotice: return _noticeStyle;
			default: return _smallStyle;
			}
		}

		// 四角布局：Edge Margin 为距对应屏幕边缘的距离（一一对应四个枚举值）；Custom 用运行期坐标。
		private static void LayoutPosition(float w, float h, out float x, out float y) {
			float sw = (float)Screen.width;
			float sh = (float)Screen.height;
			float m = Mathf.Clamp((float)EdgeMargin(), 0f, 200f);
			switch (LayoutValue()) {
			case HudLayout.TopRight:      // 右上：右边、上边各留 m
				x = sw - w - m; y = m; break;
			case HudLayout.BottomLeft:    // 左下：左边、下边各留 m
				x = m; y = sh - h - m; break;
			case HudLayout.BottomRight:   // 右下：右边、下边各留 m
				x = sw - w - m; y = sh - h - m; break;
			case HudLayout.Custom:        // 自定义：运行期拖动后的绝对坐标
				x = _customX; y = _customY; break;
			case HudLayout.TopLeft:       // 左上：左边、上边各留 m
			default:
				x = m; y = m; break;
			}
			x = Mathf.Clamp(x, 0f, Mathf.Max(0f, sw - w));
			y = Mathf.Clamp(y, 0f, Mathf.Max(0f, sh - h));
		}

		private static float Measure(string text, GUIStyle style) {
			if (string.IsNullOrEmpty(text) || style == null) return 0f;
			try { return style.CalcSize(new GUIContent(text)).x; } catch { return 0f; }
		}

		// 拖动：仅在"设置窗口打开 + Layout=自定义"时允许；位置只更新运行期字段，不写配置。
		private static void HandleDrag(Rect panel) {
			Event e = Event.current;
			if (e == null) return;
			bool canDrag = SRApi.UiOpen && LayoutValue() == HudLayout.Custom;
			if (!canDrag) { _dragging = false; return; }
			switch (e.type) {
			case EventType.MouseDown:
				if (e.button == 0 && panel.Contains(e.mousePosition)) {
					_dragging = true;
					_dragOffX = e.mousePosition.x - panel.x;
					_dragOffY = e.mousePosition.y - panel.y;
					e.Use();
				}
				break;
			case EventType.MouseDrag:
				if (_dragging) {
					_customX = Mathf.Clamp(e.mousePosition.x - _dragOffX, 0f, Mathf.Max(0f, (float)Screen.width - panel.width));
					_customY = Mathf.Clamp(e.mousePosition.y - _dragOffY, 0f, Mathf.Max(0f, (float)Screen.height - panel.height));
					e.Use();
				}
				break;
			case EventType.MouseUp:
				if (_dragging && e.button == 0) {
					_dragging = false;
					e.Use();
				}
				break;
			}
		}
	}
}
