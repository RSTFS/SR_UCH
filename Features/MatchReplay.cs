// 对局回放记录：本地记录对局时间线（阶段切换、方块布局摘要、在线玩家得分），可查看 / 导出 / 删除。
// v1 只做记录与查看：不向游戏世界注入任何东西，也不发任何网络消息（只读游戏状态 + 写本地文件）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
// 记录点：对局开始（GameControl 就绪）→ StartSession；每次阶段切换（Patch GameControl.handleEvent Postfix
// 监听 StartPhaseEvent / EndPhaseEvent）→ 追加时间线条目；对局结束（END 相位 / 控制器消失 / 换场）→ 收尾。
[SrFeature("sr.matchreplay", "MatchReplay", "1.0")]
public class MatchReplay : ITweak {
	private const string Sec = "Match Replay";
	// 单会话条目上限：超长对局时丢最旧的阶段条目（保留首条 match_start），防止内存与文件无限膨胀。
	private const int MaxEntries = 2000;
	private const int MaxPlayers = 16;
	private const string StampFormat = "yyyy-MM-dd HH:mm:ss";
	private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

	private static ConfigEntry<bool> _enabledEntry;
	private static ConfigEntry<int> _maxSessionsEntry;
	private static ConfigEntry<bool> _scoresEntry;
	private static ConfigEntry<bool> _blocksEntry;
	private static bool _enabled = true;
	private static bool _recordScores = true;
	private static bool _recordBlocks = true;

	// 时间线条目里的一名玩家得分快照（只读 ScoreKeeper / GamePlayer）。
	private class ScoreRow {
		public int number;
		public string name;
		public int score;
		public bool eliminated;
	}

	// 一条时间线条目。
	private class ReplayEntry {
		public string time;
		public float elapsed;
		public int round;
		public string kind;      // match_start / phase_start / phase_end / match_end
		public string phase;     // GamePhase 名
		public int blocks = -1;  // < 0 表示未记录方块信息（Record Blocks 关闭）
		public int layoutLength;
		public string layoutHash;
		public List<ScoreRow> scores; // null 表示未记录得分（Record Scores 关闭）
	}

	// 当前正在记录的会话（内存态 + 落盘路径）。
	private class Session {
		public string path, id, mode, modeLabel, level;
		public string startedLocal, startedUtc;
		public string endedLocal, endedUtc, endReason;
		public float startedRealtime;
		public GameControl gc;
		public Guid matchGuid;
		public bool ended;
		public bool endSeen;
		public float endSeenAt = -1f;
		public float missingSince = -1f;
		public float sceneLeftAt = -1f;
		public readonly List<ReplayEntry> entries = new List<ReplayEntry>();
	}

	// 会话列表项（扫描 replays 目录得到）。
	private class SessionInfo {
		public string path;
		public string name;
		public string mode;
		public DateTime started;
		public int entries;
		public long size;
	}

	private static Session _session;
	// 已收尾的控制器/对局：防止收尾后同一局又被 Tick 当成新对局重复开一个会话。
	private static GameControl _closedGc;
	private static Guid _closedGuid;
	private static List<SessionInfo> _listCache;
	private static float _listCacheAt = -1000f;
	private static bool _listDirty = true;
	// 查看面板：当前打开的会话路径 + 文本缓存（文件名/修改时间变化即重读）。
	private static string _viewPath;
	private static string _viewText;
	private static DateTime _viewStamp = DateTime.MinValue;
	// 两段式删除确认：第一次点按只记下路径，再点一次才真删。
	private static string _pendingDelete;

	private static void SelfReg() {
		SRApi.LocSec(Sec, "对局回放", null);
		SRApi.Nav(Sec, 50); // 侧栏栏目顺序 50（移动轨迹 20 / 快速调整 30 / 关卡 40 之后）
		SRApi.RegisterPage(Sec, Render); // 本功能页自绘：设置行 + 会话列表 + 只读时间线
		SRApi.LocKey(Sec, "Enabled", "记录总开关", "Recording");
		SRApi.LocDesc(Sec, "Enabled",
			"记录总开关\n开启后对局开始与每次阶段切换都会自动记录一条时间线\n记录只写本地配置文件目录，不发送任何网络消息\n关闭后不再开始新的记录",
			"recording master switch\nwhen on, a timeline entry is recorded at match start and on every phase change\nrecords are written to the local config folder only; nothing is sent over the network\nturning it off stops new recordings");
		SRApi.LocKey(Sec, "Max Sessions", "保留会话数", "Max sessions");
		SRApi.LocDesc(Sec, "Max Sessions",
			"回放会话保留上限\n本地最多保留多少个 .json 回放会话\n超出后自动删除最旧的会话文件\n只清理记录，不动已导出的 .txt",
			"replay session limit\nhow many .json replay sessions are kept locally\nolder sessions are deleted automatically when the limit is exceeded\nonly records are pruned; exported .txt files are left alone");
		SRApi.LocKey(Sec, "Record Scores", "记录玩家得分", "Record scores");
		SRApi.LocDesc(Sec, "Record Scores",
			"记录玩家得分\n每个条目保存一份在线玩家得分快照\n只读取计分板与玩家对象，不修改任何分数\n关闭后时间线不含玩家得分",
			"record player scores\nsaves an online-player score snapshot with every entry\nonly reads the scoreboard and player objects; no score is modified\nwhen off, timelines carry no player scores");
		SRApi.LocKey(Sec, "Record Blocks", "记录方块布局", "Record blocks");
		SRApi.LocDesc(Sec, "Record Blocks",
			"记录方块布局\n每个条目保存方块数量与布局指纹\n指纹为当前快照 XML 的字符数加一个简单哈希，用于对比布局变化\n关闭后时间线不含方块信息",
			"record block layout\nsaves the block count and a layout fingerprint with every entry\nthe fingerprint is the snapshot XML character count plus a simple hash, for comparing layouts\nwhen off, timelines carry no block information");
	}

	public void Initialize(IFeatureHost plugin) {
		// 初始化整段兜住：单个功能初始化失败不能影响其它功能装载。
		try {
			SelfReg();
			ConfigFile cfg = plugin.Config;
			_enabledEntry = cfg.Bind(Sec, "Enabled", true, "对局回放记录总开关：开启后自动记录对局时间线到本地配置目录。");
			_maxSessionsEntry = cfg.Bind(Sec, "Max Sessions", 20,
				new ConfigDescription("本地保留的回放会话数量上限（1-200）。超出后自动删除最旧的会话文件。默认 20。",
					new AcceptableValueRange<int>(1, 200)));
			_scoresEntry = cfg.Bind(Sec, "Record Scores", true, "时间线条目是否记录在线玩家得分快照（只读，不修改分数）。");
			_blocksEntry = cfg.Bind(Sec, "Record Blocks", true, "时间线条目是否记录方块数量与布局指纹（生成快照 XML 摘要，不修改方块）。");
			_enabled = _enabledEntry.Value;
			_recordScores = _scoresEntry.Value;
			_recordBlocks = _blocksEntry.Value;
			_enabledEntry.SettingChanged += delegate { _enabled = _enabledEntry.Value; };
			_scoresEntry.SettingChanged += delegate { _recordScores = _scoresEntry.Value; };
			_blocksEntry.SettingChanged += delegate { _recordBlocks = _blocksEntry.Value; };
			// 生命周期：新对局开始、结束判定、收尾都靠每帧 Tick（只在状态变化时做重活）。
			SRApi.RegisterTick("对局回放-生命周期", Tick);
			SRApi.RegisterSceneHook(OnSceneChanged);
			// 本类自带 [HarmonyPatch]，ITweak 是反射发现，须手动 CreateAndPatchAll；失败只丢本功能。
			try { Harmony.CreateAndPatchAll(typeof(MatchReplay), (string)null); }
			catch (Exception __hx) { SRApi.LogError("对局回放 补丁注册失败: " + __hx.Message); }
			PruneOldSessions();
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 初始化", __ex); }
	}

	// ---------------------------------------------------------------- 记录

	// 阶段事件钩子：只监听阶段开始/结束两种事件，其余事件直接返回（此方法在游戏事件链上，必须轻）。
	[HarmonyPatch(typeof(GameControl), "handleEvent")]
	[HarmonyPostfix]
	private static void AfterHandleEvent(GameControl __instance, GameEvent.GameEvent e) {
		try {
			if (__instance == null || e == null) return;
			bool isStart = e is GameEvent.StartPhaseEvent;
			bool isEnd = e is GameEvent.EndPhaseEvent;
			if (!isStart && !isEnd) return;
			if (!_enabled) return;
			EnsureSession(__instance);
			if (_session == null) return;
			GameControl.GamePhase phase;
			if (isStart) phase = ((GameEvent.StartPhaseEvent)e).Phase;
			else phase = ((GameEvent.EndPhaseEvent)e).Phase;
			string kind = isStart ? "phase_start" : "phase_end";
			AppendEntry(_session, kind, phase.ToString(), __instance);
			if (isEnd && phase == GameControl.GamePhase.END) {
				_session.endSeen = true;
				_session.endSeenAt = Time.realtimeSinceStartup;
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 阶段事件", __ex); }
	}

	// 每帧生命周期检查：开始新会话 / 换控制器 / 判定结束。开销只有几次空引用与浮点比较。
	private static void Tick() {
		try {
			if (_session == null) {
				if (!_enabled) return;
				GameControl gc = CurrentGc();
				if (gc == null) return;
				GameControl.GamePhase phase = SafePhase(gc);
				// END/NONE 都不自动开新会话：END 是已结束的那局，NONE 是还没就绪。
				if (phase == GameControl.GamePhase.NONE || phase == GameControl.GamePhase.END) return;
				if (IsClosedGc(gc)) return;
				StartSession(gc);
				return;
			}
			GameControl cur = CurrentGc();
			if (cur != null && cur != _session.gc) {
				if (SameMatch(cur, _session)) {
					// 换场/重载后重建了 GameControl，但仍是同一场对局：接着记同一个会话。
					_session.gc = cur;
					_session.missingSince = -1f;
					_session.sceneLeftAt = -1f;
				} else if (!_session.ended) {
					FinalizeSession("replaced");
					if (_enabled && cur != null) {
						GameControl.GamePhase phase = SafePhase(cur);
						if (phase != GameControl.GamePhase.NONE && phase != GameControl.GamePhase.END) StartSession(cur);
					}
					return;
				}
			}
			float now = Time.realtimeSinceStartup;
			if (cur == null) {
				if (_session.missingSince < 0f) _session.missingSince = now;
				float since = _session.missingSince;
				if (_session.sceneLeftAt > since) since = _session.sceneLeftAt;
				// 控制器消失（回菜单/断线）后 15 秒仍没等到同一场对局，就当作结束收尾。
				if (now - since > 15f) { FinalizeSession("left"); return; }
			} else {
				_session.missingSince = -1f;
				_session.sceneLeftAt = -1f;
				// END 相位结束后给结算画面 3 秒，再收尾（文件此前已逐条落盘）。
				if (_session.endSeen && _session.endSeenAt >= 0f && now - _session.endSeenAt > 3f) {
					FinalizeSession("end");
					return;
				}
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 生命周期", __ex); }
	}

	private static void OnSceneChanged() {
		try {
			if (_session != null) _session.sceneLeftAt = Time.realtimeSinceStartup;
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 换场", __ex); }
	}

	private static void EnsureSession(GameControl gc) {
		if (gc == null) return;
		if (_session != null) {
			if (_session.gc == gc || SameMatch(gc, _session)) { _session.gc = gc; return; }
			FinalizeSession("replaced");
		}
		if (!_enabled) return;
		if (IsClosedGc(gc)) return;
		GameControl.GamePhase phase = SafePhase(gc);
		if (phase == GameControl.GamePhase.NONE || phase == GameControl.GamePhase.END) return;
		StartSession(gc);
	}

	// 该控制器是否已经收尾过（同一局不再重复开记录）。
	private static bool IsClosedGc(GameControl gc) {
		if (gc == null) return false;
		if (_closedGc != null && gc == _closedGc) return true;
		if (_closedGuid == Guid.Empty) return false;
		try { return gc.MatchGuid == _closedGuid; } catch { return false; }
	}

	private static GameControl CurrentGc() {
		try {
			LobbyManager lm = LobbyManager.instance;
			if (lm == null) return null;
			return lm.CurrentGameController;
		} catch { return null; }
	}

	private static GameControl.GamePhase SafePhase(GameControl gc) {
		try { if (gc != null) return gc.Phase; } catch { }
		return GameControl.GamePhase.NONE;
	}

	// 同一场对局的判据：优先 MatchGuid（换场重建控制器时 guid 不变），guid 为空时退回实例引用相等。
	private static bool SameMatch(GameControl gc, Session s) {
		if (gc == null || s == null) return false;
		try {
			if (gc == s.gc) return true;
			Guid g = gc.MatchGuid;
			return g != Guid.Empty && g == s.matchGuid;
		} catch { return false; }
	}

	private static void StartSession(GameControl gc) {
		try {
			if (!_enabled || gc == null) return;
			string dir = ReplayDir();
			Directory.CreateDirectory(dir);
			GameState.GameMode mode = GameState.GameMode.FREEPLAY;
			try {
				GameSettings gs = GameSettings.GetInstance();
				if (gs != null) mode = gs.GameMode;
			} catch { }
			string modeTag = ModeTag(mode);
			string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
			string path = Path.Combine(dir, stamp + "-" + modeTag + ".json");
			int n = 2;
			while (File.Exists(path)) {
				path = Path.Combine(dir, stamp + "-" + modeTag + "-" + n + ".json");
				n++;
			}
			Session s = new Session();
			s.path = path;
			s.id = Path.GetFileNameWithoutExtension(path);
			s.mode = modeTag;
			s.modeLabel = ModeLabel(mode);
			try {
				GameState st = GameState.GetInstance();
				if (st != null) s.level = st.SelectedLevel.ToString();
			} catch { }
			s.startedLocal = DateTime.Now.ToString(StampFormat);
			s.startedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'");
			s.startedRealtime = Time.realtimeSinceStartup;
			s.gc = gc;
			try { s.matchGuid = gc.MatchGuid; } catch { s.matchGuid = Guid.Empty; }
			_session = s;
			// 对局开始条目：让时间线第一行就能看出模式/关卡/首相位。
			AppendEntry(s, "match_start", SafePhase(gc).ToString(), gc);
			PruneOldSessions();
			SRApi.LogInfo("[回放] 开始记录会话: " + s.id);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 开始会话", __ex); }
	}

	private static void FinalizeSession(string reason) {
		Session s = _session;
		if (s == null) return;
		// 先摘掉当前会话，避免写文件/发通知时重入再补一条 match_end。
		_session = null;
		_closedGc = s.gc;
		_closedGuid = s.matchGuid;
		try {
			if (!s.ended) {
				GameControl gc = s.gc;
				// 控制器已销毁（回菜单/断线）时也补一条结束条目，保证时间线有明确收尾。
				AppendEntry(s, "match_end", gc != null ? SafePhase(gc).ToString() : "UNKNOWN", gc);
				s.ended = true;
				s.endReason = reason;
				s.endedLocal = DateTime.Now.ToString(StampFormat);
				s.endedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'");
				// 结束标记需要再写一次（AppendEntry 已经写过正文，这里补 ended 字段）。
				WriteSession(s);
				PruneOldSessions();
				SRApi.LogInfo("[回放] 会话结束: " + s.id + "（" + s.entries.Count + " 条，" + reason + "）");
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 结束会话", __ex); }
	}

	private static void AppendEntry(Session s, string kind, string phase, GameControl gc) {
		if (s == null) return;
		try {
			ReplayEntry en = new ReplayEntry();
			en.time = DateTime.Now.ToString(StampFormat);
			en.elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - s.startedRealtime);
			en.round = SafeRound(gc);
			en.kind = kind;
			en.phase = string.IsNullOrEmpty(phase) ? "UNKNOWN" : phase;
			if (_recordBlocks) FillBlocks(en, gc);
			if (_recordScores) en.scores = ReadScores(gc);
			s.entries.Add(en);
			// 超上限：保留第 0 条 match_start，丢最旧的阶段条目。
			while (s.entries.Count > MaxEntries && s.entries.Count > 1) s.entries.RemoveAt(1);
			WriteSession(s);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 追加条目", __ex); }
	}

	private static int SafeRound(GameControl gc) {
		try { if (gc != null) return gc.RoundNumber; } catch { }
		return 0;
	}

	// 方块数 + 布局指纹：方块数走 Placeable.AllPlaceables 只读统计；指纹取 QuickSaver 快照 XML 的长度 + FNV-1a。
	private static void FillBlocks(ReplayEntry en, GameControl gc) {
		try {
			int count = 0;
			List<Placeable> all = Placeable.AllPlaceables;
			if (all != null) {
				for (int i = 0; i < all.Count; i++) {
					Placeable p = all[i];
					if (p == null || p.MarkedForDestruction || !p.Placed) continue;
					count++;
				}
			}
			en.blocks = count;
			if (gc == null) return;
			QuickSaver qs = gc.GetComponent<QuickSaver>();
			if (qs == null) return;
			System.Xml.XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
			if (doc == null) return;
			string xml = doc.OuterXml;
			en.layoutLength = xml == null ? 0 : xml.Length;
			en.layoutHash = HashXml(xml);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 记录方块布局", __ex); }
	}

	// 简单布局哈希：FNV-1a 32 位，输出 8 位十六进制（不追求密码学强度，只用于同会话内对比）。
	private static string HashXml(string xml) {
		if (string.IsNullOrEmpty(xml)) return "00000000";
		uint h = 2166136261u;
		for (int i = 0; i < xml.Length; i++) {
			h ^= xml[i];
			h *= 16777619u;
		}
		return h.ToString("x8", CultureInfo.InvariantCulture);
	}

	// 在线玩家得分快照：只读 CurrentPlayerQueue + ScoreKeeper.GetPlayerTotal + Character 状态。
	private static List<ScoreRow> ReadScores(GameControl gc) {
		List<ScoreRow> rows = new List<ScoreRow>();
		try {
			if (gc == null) return rows;
			ScoreKeeper sk = null;
			try { sk = ScoreKeeper.Instance; } catch { }
			foreach (GamePlayer gp in gc.CurrentPlayerQueue) {
				if (gp == null) continue;
				if (rows.Count >= MaxPlayers) break;
				ScoreRow row = new ScoreRow();
				try { row.number = gp.networkNumber; } catch { row.number = 0; }
				try { row.name = string.IsNullOrEmpty(gp.playerName) ? gp.ValidatedDisplayName : gp.playerName; } catch { row.name = ""; }
				if (string.IsNullOrEmpty(row.name)) row.name = "?";
				try { if (sk != null) row.score = sk.GetPlayerTotal(gp); } catch { row.score = 0; }
				try {
					Character c = gp.CharacterInstance;
					if (c != null) row.eliminated = c.Dead || c.Dying || c.LocallyDead;
				} catch { }
				rows.Add(row);
			}
			rows.Sort(delegate (ScoreRow a, ScoreRow b) { return a.number.CompareTo(b.number); });
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 读取玩家得分", __ex); }
		return rows;
	}

	// ---------------------------------------------------------------- 存储

	private static string ReplayDir() {
		return Path.Combine(BepInEx.Paths.ConfigPath, "SR_UCH", "replays");
	}

	private static string ModeTag(GameState.GameMode mode) {
		switch (mode) {
			case GameState.GameMode.FREEPLAY: return "freeplay";
			case GameState.GameMode.PARTY: return "party";
			case GameState.GameMode.CREATIVE: return "creative";
			case GameState.GameMode.CHALLENGE: return "challenge";
			default: return "match";
		}
	}

	private static string ModeLabel(GameState.GameMode mode) {
		switch (mode) {
			case GameState.GameMode.FREEPLAY: return SRApi.T("自由", "Freeplay");
			case GameState.GameMode.PARTY: return SRApi.T("派对", "Party");
			case GameState.GameMode.CREATIVE: return SRApi.T("创意", "Creative");
			case GameState.GameMode.CHALLENGE: return SRApi.T("挑战", "Challenge");
			default: return SRApi.T("对局", "Match");
		}
	}

	private static int MaxSessions() {
		try { if (_maxSessionsEntry != null) return Mathf.Clamp(_maxSessionsEntry.Value, 1, 200); } catch { }
		return 20;
	}

	private static void WriteSession(Session s) {
		if (s == null || string.IsNullOrEmpty(s.path)) return;
		try {
			string dir = Path.GetDirectoryName(s.path);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			string json = BuildSessionJson(s);
			// 先写临时文件再替换，避免查看端读到写了一半的文件。
			string tmp = s.path + ".tmp";
			File.WriteAllText(tmp, json, Utf8);
			if (File.Exists(s.path)) File.Delete(s.path);
			File.Move(tmp, s.path);
			_listDirty = true;
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 写入会话文件", __ex); }
	}

	private static string BuildSessionJson(Session s) {
		StringBuilder sb = new StringBuilder(4096);
		sb.Append("{\n");
		sb.Append("  \"format\": \"sr.matchreplay\",\n");
		sb.Append("  \"version\": 1,\n");
		sb.Append("  \"id\": ").Append(Q(s.id)).Append(",\n");
		sb.Append("  \"mode\": ").Append(Q(s.mode)).Append(",\n");
		sb.Append("  \"modeLabel\": ").Append(Q(s.modeLabel)).Append(",\n");
		sb.Append("  \"level\": ").Append(Q(s.level)).Append(",\n");
		sb.Append("  \"startedLocal\": ").Append(Q(s.startedLocal)).Append(",\n");
		sb.Append("  \"startedUtc\": ").Append(Q(s.startedUtc)).Append(",\n");
		sb.Append("  \"endedLocal\": ").Append(s.ended ? Q(s.endedLocal) : "null").Append(",\n");
		sb.Append("  \"endedUtc\": ").Append(s.ended ? Q(s.endedUtc) : "null").Append(",\n");
		sb.Append("  \"endReason\": ").Append(string.IsNullOrEmpty(s.endReason) ? "null" : Q(s.endReason)).Append(",\n");
		sb.Append("  \"entryCount\": ").Append(s.entries.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
		sb.Append("  \"entries\": [\n");
		for (int i = 0; i < s.entries.Count; i++) {
			sb.Append("    ").Append(EntryJson(s.entries[i]));
			if (i < s.entries.Count - 1) sb.Append(',');
			sb.Append('\n');
		}
		sb.Append("  ]\n");
		sb.Append("}\n");
		return sb.ToString();
	}

	// 单条目保持单行：查看/导出时按行解析，玩家名里的换行已在 Esc 中转义，不会破坏行结构。
	private static string EntryJson(ReplayEntry en) {
		StringBuilder b = new StringBuilder(256);
		b.Append("{ \"time\": ").Append(Q(en.time));
		b.Append(", \"elapsed\": ").Append(en.elapsed.ToString("0.00", CultureInfo.InvariantCulture));
		b.Append(", \"round\": ").Append(en.round.ToString(CultureInfo.InvariantCulture));
		b.Append(", \"event\": ").Append(Q(en.kind));
		b.Append(", \"phase\": ").Append(Q(en.phase));
		if (en.blocks >= 0) {
			b.Append(", \"blocks\": ").Append(en.blocks.ToString(CultureInfo.InvariantCulture));
			b.Append(", \"layoutLength\": ").Append(en.layoutLength.ToString(CultureInfo.InvariantCulture));
			b.Append(", \"layoutHash\": ").Append(Q(en.layoutHash));
		}
		if (en.scores != null) {
			b.Append(", \"scores\": [");
			for (int i = 0; i < en.scores.Count; i++) {
				ScoreRow r = en.scores[i];
				if (i > 0) b.Append(", ");
				b.Append("{ \"number\": ").Append(r.number.ToString(CultureInfo.InvariantCulture));
				b.Append(", \"name\": ").Append(Q(r.name));
				b.Append(", \"score\": ").Append(r.score.ToString(CultureInfo.InvariantCulture));
				b.Append(", \"eliminated\": ").Append(r.eliminated ? "true" : "false").Append(" }");
			}
			b.Append(']');
		}
		b.Append(" }");
		return b.ToString();
	}

	private static string Q(string s) {
		return "\"" + Esc(s) + "\"";
	}

	// 自己拼 JSON 的手写转义（不依赖 Newtonsoft）：引号/反斜杠/控制字符转义，中文等非 ASCII 原样保留（UTF-8 落盘）。
	private static string Esc(string s) {
		if (string.IsNullOrEmpty(s)) return "";
		StringBuilder b = new StringBuilder(s.Length + 8);
		for (int i = 0; i < s.Length; i++) {
			char c = s[i];
			switch (c) {
				case '"': b.Append("\\\""); break;
				case '\\': b.Append("\\\\"); break;
				case '\b': b.Append("\\b"); break;
				case '\f': b.Append("\\f"); break;
				case '\n': b.Append("\\n"); break;
				case '\r': b.Append("\\r"); break;
				case '\t': b.Append("\\t"); break;
				default:
					if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
					else b.Append(c);
					break;
			}
		}
		return b.ToString();
	}

	private static string Unesc(string s) {
		if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
		StringBuilder b = new StringBuilder(s.Length);
		for (int i = 0; i < s.Length; i++) {
			char c = s[i];
			if (c != '\\' || i + 1 >= s.Length) { b.Append(c); continue; }
			i++;
			char n = s[i];
			switch (n) {
				case 'n': b.Append('\n'); break;
				case 'r': b.Append('\r'); break;
				case 't': b.Append('\t'); break;
				case 'b': b.Append('\b'); break;
				case 'f': b.Append('\f'); break;
				case '"': b.Append('"'); break;
				case '\\': b.Append('\\'); break;
				case '/': b.Append('/'); break;
				case 'u':
					if (i + 4 < s.Length) {
						int code;
						if (int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) {
							b.Append((char)code);
							i += 4;
						}
					}
					break;
				default: b.Append(n); break;
			}
		}
		return b.ToString();
	}

	// 保留上限：只清理 *.json（按文件名时间前缀排序，最旧的先删），不动已导出的 txt。
	private static void PruneOldSessions() {
		try {
			string dir = ReplayDir();
			if (!Directory.Exists(dir)) return;
			int max = MaxSessions();
			string[] files = Directory.GetFiles(dir, "*.json");
			if (files.Length <= max) return;
			Array.Sort(files, StringComparer.Ordinal);
			int remove = files.Length - max;
			int done = 0;
			for (int i = 0; i < remove; i++) {
				try { File.Delete(files[i]); done++; } catch (Exception __ex) { SRApi.Guard.Log("对局回放 清理旧会话", __ex); }
			}
			if (done > 0) {
				_listDirty = true;
				SRApi.LogInfo("[回放] 保留上限 " + max + "，已删除最旧 " + done + " 个会话");
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 清理会话", __ex); }
	}

	private static List<SessionInfo> GetSessions() {
		float now = Time.realtimeSinceStartup;
		if (_listCache == null || _listDirty || now - _listCacheAt > 3f) {
			_listCache = ScanSessions();
			_listDirty = false;
			_listCacheAt = now;
		}
		return _listCache;
	}

	private static List<SessionInfo> ScanSessions() {
		List<SessionInfo> list = new List<SessionInfo>();
		try {
			string dir = ReplayDir();
			if (!Directory.Exists(dir)) return list;
			string[] files = Directory.GetFiles(dir, "*.json");
			for (int i = 0; i < files.Length; i++) {
				try {
					string f = files[i];
					FileInfo fi = new FileInfo(f);
					SessionInfo si = new SessionInfo();
					si.path = f;
					si.name = Path.GetFileNameWithoutExtension(f);
					si.size = fi.Length;
					string text = null;
					try { text = File.ReadAllText(f, Encoding.UTF8); } catch { }
					si.entries = ParseIntField(text, "entryCount");
					si.mode = ParseStrField(text, "mode");
					string started = ParseStrField(text, "startedLocal");
					DateTime dt;
					if (!string.IsNullOrEmpty(started)
						&& DateTime.TryParseExact(started, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) {
						si.started = dt;
					} else {
						si.started = fi.LastWriteTime;
					}
					list.Add(si);
				} catch (Exception __ex) { SRApi.Guard.Log("对局回放 扫描会话", __ex); }
			}
			list.Sort(delegate (SessionInfo a, SessionInfo b) {
				int c = b.started.CompareTo(a.started);
				return c != 0 ? c : string.CompareOrdinal(b.name, a.name);
			});
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 读取会话列表", __ex); }
		return list;
	}

	// ---------------------------------------------------------------- 简单 JSON 字段读取（本功能自己写出的行式 JSON）

	private static int ParseIntField(string text, string key) {
		try {
			if (string.IsNullOrEmpty(text)) return 0;
			string pat = "\"" + key + "\": ";
			int i = text.IndexOf(pat, StringComparison.Ordinal);
			if (i < 0) return 0;
			i += pat.Length;
			int e = i;
			while (e < text.Length && (text[e] == '-' || (text[e] >= '0' && text[e] <= '9'))) e++;
			if (e <= i) return 0;
			int v;
			if (int.TryParse(text.Substring(i, e - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
		} catch { }
		return 0;
	}

	private static float ParseFloatField(string text, string key) {
		try {
			if (string.IsNullOrEmpty(text)) return 0f;
			string pat = "\"" + key + "\": ";
			int i = text.IndexOf(pat, StringComparison.Ordinal);
			if (i < 0) return 0f;
			i += pat.Length;
			int e = i;
			while (e < text.Length && (text[e] == '-' || text[e] == '+' || text[e] == '.' || (text[e] >= '0' && text[e] <= '9'))) e++;
			if (e <= i) return 0f;
			float v;
			if (float.TryParse(text.Substring(i, e - i), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
		} catch { }
		return 0f;
	}

	private static string ParseStrField(string text, string key) {
		try {
			if (string.IsNullOrEmpty(text)) return null;
			string pat = "\"" + key + "\": \"";
			int i = text.IndexOf(pat, StringComparison.Ordinal);
			if (i < 0) return null;
			i += pat.Length;
			StringBuilder sb = new StringBuilder();
			for (; i < text.Length; i++) {
				char c = text[i];
				if (c == '\\' && i + 1 < text.Length) { sb.Append(c).Append(text[i + 1]); i++; continue; }
				if (c == '"') break;
				sb.Append(c);
			}
			return Unesc(sb.ToString());
		} catch { return null; }
	}

	private static List<ScoreRow> ParseScores(string line) {
		List<ScoreRow> rows = new List<ScoreRow>();
		try {
			const string pat = "\"scores\": [";
			int i = line.IndexOf(pat, StringComparison.Ordinal);
			if (i < 0) return rows;
			i += pat.Length;
			int end = line.LastIndexOf(']');
			if (end <= i) return rows;
			string body = line.Substring(i, end - i);
			string[] chunks = body.Split(new string[] { "}, " }, StringSplitOptions.None);
			for (int k = 0; k < chunks.Length; k++) {
				string chunk = chunks[k];
				ScoreRow r = new ScoreRow();
				r.number = ParseIntField(chunk, "number");
				string nm = ParseStrField(chunk, "name");
				r.name = string.IsNullOrEmpty(nm) ? "?" : nm;
				r.score = ParseIntField(chunk, "score");
				r.eliminated = chunk.IndexOf("\"eliminated\": true", StringComparison.Ordinal) >= 0;
				rows.Add(r);
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 解析得分", __ex); }
		return rows;
	}

	// ---------------------------------------------------------------- 查看 / 导出

	private static string ReadSessionText(string path) {
		try {
			if (string.IsNullOrEmpty(path)) return null;
			return File.ReadAllText(path, Encoding.UTF8);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 读取会话文本", __ex); return null; }
	}

	// 把行式 JSON 整理成人类可读的时间线文本（查看面板与导出共用）。
	private static string BuildTimelineText(string json, string name) {
		StringBuilder sb = new StringBuilder(4096);
		try {
			sb.Append(SRApi.T("对局回放: ", "Match replay: ")).Append(name).Append('\n');
			string mode = ParseStrField(json, "modeLabel");
			if (string.IsNullOrEmpty(mode)) mode = ParseStrField(json, "mode");
			string level = ParseStrField(json, "level");
			string started = ParseStrField(json, "startedLocal");
			string ended = ParseStrField(json, "endedLocal");
			string reason = ParseStrField(json, "endReason");
			sb.Append(SRApi.T("模式: ", "Mode: ")).Append(string.IsNullOrEmpty(mode) ? "?" : mode);
			sb.Append("    ").Append(SRApi.T("关卡: ", "Level: ")).Append(string.IsNullOrEmpty(level) ? "?" : level).Append('\n');
			sb.Append(SRApi.T("开始: ", "Started: ")).Append(string.IsNullOrEmpty(started) ? "?" : started);
			sb.Append("    ").Append(SRApi.T("结束: ", "Ended: ")).Append(string.IsNullOrEmpty(ended) ? SRApi.T("（未结束）", "(not ended)") : ended);
			if (!string.IsNullOrEmpty(reason)) sb.Append("    ").Append(SRApi.T("原因: ", "Reason: ")).Append(reason);
			sb.Append('\n');
			sb.Append(SRApi.T("条目: ", "Entries: ")).Append(ParseIntField(json, "entryCount").ToString(CultureInfo.InvariantCulture)).Append('\n');
			sb.Append(new string('-', 56)).Append('\n');
			string[] lines = json.Split('\n');
			int shown = 0;
			for (int i = 0; i < lines.Length; i++) {
				string t = lines[i].TrimStart();
				if (!t.StartsWith("{ \"time\":", StringComparison.Ordinal) && !t.StartsWith("{\"time\":", StringComparison.Ordinal)) continue;
				string time = ParseStrField(t, "time");
				float elapsed = ParseFloatField(t, "elapsed");
				int round = ParseIntField(t, "round");
				string ev = ParseStrField(t, "event");
				string phase = ParseStrField(t, "phase");
				int blocks = ParseIntField(t, "blocks");
				bool hasBlocks = t.IndexOf("\"blocks\":", StringComparison.Ordinal) >= 0;
				int len = ParseIntField(t, "layoutLength");
				string hash = ParseStrField(t, "layoutHash");
				sb.Append('[').Append(FormatElapsed(elapsed)).Append("] ").Append(string.IsNullOrEmpty(time) ? "" : time).Append('\n');
				sb.Append("    ").Append(SRApi.T("回合 ", "Round ")).Append(round.ToString(CultureInfo.InvariantCulture));
				sb.Append(" · ").Append(ev).Append(" · ").Append(phase).Append('\n');
				if (hasBlocks) {
					sb.Append("    ").Append(SRApi.T("方块 ", "Blocks ")).Append(blocks.ToString(CultureInfo.InvariantCulture));
					sb.Append(SRApi.T(" · 布局 ", " · layout ")).Append(len.ToString(CultureInfo.InvariantCulture));
					sb.Append(SRApi.T(" 字符 · 指纹 ", " chars · hash ")).Append(string.IsNullOrEmpty(hash) ? "-" : hash).Append('\n');
				}
				if (t.IndexOf("\"scores\":", StringComparison.Ordinal) >= 0) {
					List<ScoreRow> rows = ParseScores(t);
					if (rows.Count > 0) {
						sb.Append("    ").Append(SRApi.T("得分: ", "Scores: "));
						for (int k = 0; k < rows.Count; k++) {
							if (k > 0) sb.Append(", ");
							sb.Append(rows[k].name).Append('=').Append(rows[k].score.ToString(CultureInfo.InvariantCulture));
							if (rows[k].eliminated) sb.Append(SRApi.T("（已淘汰）", "(out)"));
						}
						sb.Append('\n');
					}
				}
				shown++;
			}
			if (shown == 0) sb.Append(SRApi.T("（没有可显示的条目）", "(no entries to show)")).Append('\n');
		} catch (Exception __ex) {
			SRApi.Guard.Log("对局回放 构建时间线文本", __ex);
			sb.Append(SRApi.T("（时间线读取失败）", "(failed to read the timeline)")).Append('\n');
		}
		return sb.ToString();
	}

	private static string FormatElapsed(float sec) {
		if (sec < 0f) sec = 0f;
		int total = (int)sec;
		int h = total / 3600;
		int m = (total % 3600) / 60;
		int s = total % 60;
		if (h > 0) return "+" + h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture);
		return "+" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture);
	}

	private static void ExportSession(SessionInfo si) {
		string text = null;
		try {
			string json = ReadSessionText(si.path);
			if (json != null) text = BuildTimelineText(json, si.name);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 导出读取", __ex); }
		if (string.IsNullOrEmpty(text)) {
			SRApi.Notify(SRApi.T("回放导出失败：读取会话失败", "Export failed: could not read the session"));
			return;
		}
		try {
			string dir = Path.GetDirectoryName(si.path);
			string outPath = Path.Combine(dir, si.name + ".txt");
			File.WriteAllText(outPath, text, Utf8);
			SRApi.Notify(SRApi.T("回放已导出: ", "Replay exported: ") + outPath);
			SRApi.LogInfo("[回放] 已导出: " + outPath);
		} catch (Exception __ex) {
			SRApi.Guard.Log("对局回放 导出写文件", __ex);
			// 写文件失败时退回剪贴板，至少让玩家能拿到内容。
			try {
				GUIUtility.systemCopyBuffer = text;
				SRApi.Notify(SRApi.T("导出失败，已复制到剪贴板", "Export failed; copied to the clipboard"));
			} catch { SRApi.Notify(SRApi.T("回放导出失败", "Replay export failed")); }
		}
	}

	private static void DeleteSession(SessionInfo si) {
		try {
			File.Delete(si.path);
			if (_viewPath == si.path) { _viewPath = null; _viewText = null; }
			_listDirty = true;
			SRApi.Notify(SRApi.T("已删除回放: ", "Replay deleted: ") + si.name);
			SRApi.LogInfo("[回放] 已删除: " + si.path);
		} catch (Exception __ex) {
			SRApi.Guard.Log("对局回放 删除会话", __ex);
			SRApi.Notify(SRApi.T("删除失败: ", "Delete failed: ") + si.name);
		}
	}

	// ---------------------------------------------------------------- UI

	private static void Render() {
		// 整段兜住：任一控件抛异常都会让 IMGUI 布局栈失衡，本页宁可少画也不能把异常带进核心渲染。
		try {
			SRApi.Ctl.SectionTitle(SRApi.T("- 对局回放 -", "- Match replay -"),
				SRApi.T("本地记录对局时间线：阶段切换、方块布局摘要、玩家得分。只记录，不改游戏、不发网络消息。",
					"Records match timelines locally: phase changes, block layout summaries and player scores. Recording only; nothing is injected or sent over the network."));
			// 配置行（Enabled / Max Sessions / Record Scores / Record Blocks）由核心统一绘制。
			SRApi.RenderSectionEntries(Sec);
			GUILayout.Space(SRApi.Ctl.Sc(6));

			// 当前记录状态。
			GUILayout.BeginHorizontal();
			if (_session != null) {
				GUILayout.Label(SRApi.T("正在记录: ", "Recording: ") + _session.id
					+ "  (" + _session.entries.Count.ToString(CultureInfo.InvariantCulture) + SRApi.T(" 条", " entries") + ")",
					SRApi.Ctl.Label);
			} else {
				GUILayout.Label(SRApi.T("（当前未在记录）", "(not recording)"), SRApi.Ctl.Label);
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SRApi.Ctl.Sc(4));

			SRApi.Ctl.SectionTitle(SRApi.T("- 回放会话 -", "- Replay sessions -"));
			List<SessionInfo> list = GetSessions();
			if (list == null || list.Count == 0) {
				GUILayout.Label(SRApi.T("（暂无回放，进入对局后会自动记录）",
					"(no replays yet; matches are recorded automatically)"), SRApi.Ctl.Label);
			} else {
				for (int i = 0; i < list.Count; i++) {
					SessionInfo si = list[i];
					GUILayout.BeginHorizontal();
					GUILayout.Label(si.name, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(230)));
					GUILayout.Label(si.started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
						SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(140)));
					GUILayout.Label(si.entries.ToString(CultureInfo.InvariantCulture) + SRApi.T(" 条", " entries"),
						SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(74)));
					GUILayout.Label(SizeText(si.size), SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(66)));
					if (GUILayout.Button(SRApi.T("查看", "View"), SRApi.Ctl.Btn,
						GUILayout.Width(SRApi.Ctl.Sc(56)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
						_viewPath = si.path;
						_viewText = null;
						_viewStamp = DateTime.MinValue;
						_pendingDelete = null;
					}
					if (GUILayout.Button(SRApi.T("导出", "Export"), SRApi.Ctl.Btn,
						GUILayout.Width(SRApi.Ctl.Sc(56)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
						ExportSession(si);
						_pendingDelete = null;
					}
					bool pending = _pendingDelete == si.path;
					if (GUILayout.Button(pending ? SRApi.T("确认删除", "Confirm") : SRApi.T("删除", "Delete"), SRApi.Ctl.Btn,
						GUILayout.Width(SRApi.Ctl.Sc(72)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
						if (pending) {
							DeleteSession(si);
							_pendingDelete = null;
						} else {
							_pendingDelete = si.path;
						}
					}
					GUILayout.FlexibleSpace();
					GUILayout.EndHorizontal();
				}
			}

			// 只读时间线面板（查看）：打开后固定在列表下方。
			if (!string.IsNullOrEmpty(_viewPath)) {
				GUILayout.Space(SRApi.Ctl.Sc(6));
				SRApi.Ctl.SectionTitle(SRApi.T("- 时间线（只读） -", "- Timeline (read-only) -"));
				GUILayout.BeginHorizontal();
				if (GUILayout.Button(SRApi.T("关闭查看", "Close"), SRApi.Ctl.Btn,
					GUILayout.Width(SRApi.Ctl.Sc(80)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
					_viewPath = null;
					_viewText = null;
				}
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
				string text = ViewText();
				if (!string.IsNullOrEmpty(text)) {
					float w = Mathf.Max(SRApi.Ctl.Sc(200), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(48));
					float innerW = Mathf.Max(SRApi.Ctl.Sc(160), w - SRApi.Ctl.Sc(20));
					float h = SRApi.Ctl.ChatLabel.CalcHeight(new GUIContent(text), innerW) + SRApi.Ctl.Sc(12);
					h = Mathf.Clamp(h, SRApi.Ctl.Sc(160), SRApi.Ctl.Sc(460));
					// 只读编辑框：忽略返回值，仅用于文本选择/复制（游戏本身没有文本选择）。
					GUILayout.TextArea(text, SRApi.Ctl.ChatArea, GUILayout.Width(innerW), GUILayout.Height(h));
				}
			}
		} catch (Exception __ex) { SRApi.Guard.Log("渲染对局回放页", __ex); }
	}

	private static string ViewText() {
		try {
			if (string.IsNullOrEmpty(_viewPath)) return null;
			FileInfo fi = new FileInfo(_viewPath);
			if (_viewText != null && _viewStamp == fi.LastWriteTimeUtc) return _viewText;
			string json = File.ReadAllText(_viewPath, Encoding.UTF8);
			_viewText = BuildTimelineText(json, Path.GetFileNameWithoutExtension(_viewPath));
			_viewStamp = fi.LastWriteTimeUtc;
			return _viewText;
		} catch (Exception __ex) {
			SRApi.Guard.Log("对局回放 查看会话", __ex);
			return SRApi.T("（读取失败）", "(failed to read)");
		}
	}

	private static string SizeText(long bytes) {
		if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
		if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
		return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
	}
}
}
