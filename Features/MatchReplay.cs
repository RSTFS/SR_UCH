// 对局回放记录：本地记录对局时间线（阶段切换、方块布局摘要、在线玩家得分、放置/删除事件），可查看 / 导出 /
// 阶段快照存为布局预设。只读游戏状态 + 写本地文件，不向游戏世界注入，也不发任何网络消息。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
// 记录点：对局开始（GameControl 就绪）→ StartSession；每次阶段切换（Patch GameControl.handleEvent Postfix
// 监听 StartPhaseEvent / EndPhaseEvent）→ 追加时间线条目 + 落盘完整布局快照 XML；对局结束（END 相位 /
// 控制器消失 / 换场）→ 收尾。
[SrFeature("sr.matchreplay", "MatchReplay", "1.1")]
public class MatchReplay : ITweak {
	private const string Sec = "Match Replay";
	// 单会话条目上限：超长对局时丢最旧的阶段条目（保留首条 match_start），防止内存与文件无限膨胀。
	private const int MaxEntries = 2000;
	private const int MaxPlayers = 16;
	// 阶段快照文件上限（UTF-8 字节）：超过就跳过落盘，避免单个 XML 拖垮磁盘 / 内存。
	private const int MaxSnapshotBytes = 2000000;
	// 每个会话最多保留的阶段快照份数（最新优先；第一份 match_start 快照永久保留）。
	private const int MaxSnapshots = 40;
	// 方块差分扫描（放置/删除事件）间隔（秒）；只在阶段切换时消费，用于 v2 回放事件预留。
	private const float OpScanInterval = 0.5f;
	// 单个条目最多携带的放置/删除事件数（防止超长局生成超大 JSON）。
	private const int MaxPendingOps = 1000;
	// 布局预设功能的对外槽位：Func<string xml, string displayName, bool ok>。
	private const string SlotSaveXml = "presets.saveXml";
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

	// 时间线条目里的一个放置/删除事件（v2 回放预留：type/player/time/pos）。
	private class OpRow {
		public string type;    // place / destroy
		public int player;
		public string time;
		public float elapsed;
		public float x;
		public float y;
		public string name;
	}

	// 一条布局快照的落盘记录（entry 用于快照被清理时把 JSON 里的引用一并清掉）。
	private class SnapshotRef {
		public string rel;      // 相对 replays 根目录，用 '/' 分隔：<sessionId>/<seq>-<phase>.xml
		public ReplayEntry entry;
	}

	// 差分扫描用的方块状态快照。
	private class PieceSnap {
		public int player;
		public string name;
		public float x;
		public float y;
	}

	// 一条时间线条目。
	private class ReplayEntry {
		public string time;
		public float elapsed;
		public int round;
		public string kind;      // match_start / phase_start / phase_end / match_end
		public string phase;     // GamePhase 名
		public int blocks = -1;  // < 0 表示未记录方块信息（Record Blocks 关闭或房客端无 QuickSaver）
		public int layoutLength;
		public string layoutHash;
		public string snapshot;  // 阶段快照相对文件名；null 表示没有落盘
		public List<ScoreRow> scores; // null 表示未记录得分（Record Scores 关闭）
		public List<OpRow> ops = new List<OpRow>(); // v2 事件预留：可为空数组
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
		public int snapshotSeq;
		public bool oversizeWarned;
		public bool opsCapped;
		public float opScanAt = -1f;
		public readonly List<ReplayEntry> entries = new List<ReplayEntry>();
		public readonly List<SnapshotRef> snapshots = new List<SnapshotRef>();
		public readonly Dictionary<int, PieceSnap> pieces = new Dictionary<int, PieceSnap>();
		public readonly List<OpRow> pendingOps = new List<OpRow>();
	}

	// 会话列表项（扫描 replays 目录得到）。
	private class SessionInfo {
		public string path;
		public string name;
		public string mode;
		public DateTime started;
		public int entries;
		public int snapshots;
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
	// 查看面板里的阶段快照行缓存（与文本缓存同一刷新时机）。
	private static List<SnapRow> _viewRows;
	// 两段式删除确认：第一次点按只记下路径，再点一次才真删。
	private static string _pendingDelete;

	// 查看面板用：一个可「存为预设」的阶段快照条目。
	private class SnapRow {
		public string snapshot;
		public string time;
		public float elapsed;
		public int round;
		public string kind;
		public string phase;
	}

	private static void SelfReg() {
		SRApi.LocSec(Sec, "对局回放", null);
		SRApi.Nav(Sec, 65); // 侧栏栏目顺序 65：关卡工具(60) 之后、会话(80) 之前
		SRApi.RegisterPage(Sec, Render); // 本功能页自绘：设置行 + 会话列表 + 只读时间线
		SRApi.LocKey(Sec, "Enabled", "记录总开关", "Recording");
		SRApi.LocDesc(Sec, "Enabled",
			"记录总开关\n开启后对局开始与每次阶段切换都会自动记录一条时间线\n记录只写本地配置文件目录，不发送任何网络消息；关闭后不再开始新的记录。",
			"recording master switch\nwhen on, a timeline entry is recorded at match start and on every phase change\nrecords are written to the local config folder only; nothing is sent over the network; turning it off stops new recordings");
		SRApi.LocKey(Sec, "Max Sessions", "保留会话数", "Max sessions");
		SRApi.LocDesc(Sec, "Max Sessions",
			"回放会话保留上限\n本地最多保留多少个 .json 回放会话\n超出后自动删除最旧的会话文件；只清理记录，不动已导出的 .txt。",
			"replay session limit\nhow many .json replay sessions are kept locally\nolder sessions are deleted automatically when the limit is exceeded; only records are pruned; exported .txt files are left alone");
		SRApi.LocKey(Sec, "Record Scores", "记录玩家得分", "Record scores");
		SRApi.LocDesc(Sec, "Record Scores",
			"记录玩家得分\n每个条目保存一份在线玩家得分快照\n只读取计分板与玩家对象，不修改任何分数；关闭后时间线不含玩家得分。",
			"record player scores\nsaves an online-player score snapshot with every entry\nonly reads the scoreboard and player objects; no score is modified; when off, timelines carry no player scores");
		SRApi.LocKey(Sec, "Record Blocks", "记录方块布局", "Record blocks");
		SRApi.LocDesc(Sec, "Record Blocks",
			"记录方块布局\n每个条目保存方块数量与布局指纹\n指纹为当前快照 XML 的字符数加一个简单哈希，用于对比布局变化；关闭后时间线不含方块信息。",
			"record block layout\nsaves the block count and a layout fingerprint with every entry\nthe fingerprint is the snapshot XML character count plus a simple hash, for comparing layouts; when off, timelines carry no block information");
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
			// 放置/删除差分扫描：按间隔累积到 pendingOps，下一次阶段条目消费（v2 回放事件预留）。
			ScanOps(_session, false);
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
			// 先同步一次方块基线（不产生 place 事件），match_start 条目的 ops 保持空数组。
			SyncPiecesBaseline(s);
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
			// 先把差分事件扫到最新，再落快照：条目里的 ops 覆盖到本条为止的所有放置/删除。
			ScanOps(s, true);
			// 阶段快照无条件落盘（task v2 核心）；Record Blocks 只控制条目里的方块数/指纹字段。
			CaptureLayout(s, en, gc);
			if (_recordScores) en.scores = ReadScores(gc);
			en.ops = new List<OpRow>(s.pendingOps);
			s.pendingOps.Clear();
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

	// 方块数 + 布局指纹 + 完整快照落盘：只读 Placeable / QuickSaver，不修改任何方块。
	// 快照始终落盘（受 2MB/40 份上限约束）；Record Blocks 关闭时只跳过条目里的方块数/指纹字段。
	// 房客端可能取不到 QuickSaver：按约定跳过快照并把 blocks 记为 -1（不报错）。
	private static void CaptureLayout(Session s, ReplayEntry en, GameControl gc) {
		try {
			int count = _recordBlocks ? CountPlacedBlocks() : -1;
			QuickSaver qs = null;
			try { if (gc != null) qs = gc.GetComponent<QuickSaver>(); } catch (Exception __qx) { SRApi.Guard.Log("对局回放 取 QuickSaver", __qx); }
			if (qs == null) { en.blocks = -1; return; }
			en.blocks = count;
			XmlDocument doc = null;
			try { doc = qs.GetCurrentXmlSnapshot(false); } catch (Exception __gx) { SRApi.Guard.Log("对局回放 生成布局快照", __gx); }
			if (doc == null || doc.DocumentElement == null) return;
			StripDestroyed(doc);
			string xml = doc.OuterXml;
			if (string.IsNullOrEmpty(xml)) return;
			en.layoutLength = xml.Length;
			en.layoutHash = HashXml(xml);
			SaveSnapshotXml(s, en, en.phase, xml);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 记录方块布局", __ex); }
	}

	private static int CountPlacedBlocks() {
		int count = 0;
		try {
			List<Placeable> all = Placeable.AllPlaceables;
			if (all == null) return 0;
			for (int i = 0; i < all.Count; i++) if (IsTrackedPiece(all[i])) count++;
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 统计方块数", __ex); }
		return count;
	}

	// 阶段快照落盘：replays\<sessionId>\<seq>-<phase>.xml，条目里记相对文件名（<sessionId>/<file>）。
	private static void SaveSnapshotXml(Session s, ReplayEntry en, string phase, string xml) {
		try {
			if (s == null || en == null || string.IsNullOrEmpty(xml)) return;
			if (Encoding.UTF8.GetByteCount(xml) > MaxSnapshotBytes) {
				if (!s.oversizeWarned) {
					s.oversizeWarned = true;
					SRApi.LogWarn("[回放] 阶段快照超过 " + (MaxSnapshotBytes / 1000) + " KB，已跳过落盘: " + s.id);
				}
				return;
			}
			string dir = Path.Combine(ReplayDir(), s.id);
			Directory.CreateDirectory(dir);
			s.snapshotSeq++;
			string fileName = s.snapshotSeq.ToString("000", CultureInfo.InvariantCulture) + "-" + SafeSnapshotTag(phase) + ".xml";
			File.WriteAllText(Path.Combine(dir, fileName), xml, Utf8);
			string rel = s.id + "/" + fileName;
			en.snapshot = rel;
			s.snapshots.Add(new SnapshotRef { rel = rel, entry = en });
			PruneSnapshots(s);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 写入阶段快照", __ex); }
	}

	private static string SafeSnapshotTag(string phase) {
		try {
			if (string.IsNullOrEmpty(phase)) return "UNKNOWN";
			StringBuilder sb = new StringBuilder(phase.Length);
			for (int i = 0; i < phase.Length; i++) {
				char c = phase[i];
				bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_' || c == '-';
				sb.Append(ok ? c : '_');
			}
			return sb.ToString();
		} catch { return "UNKNOWN"; }
	}

	// 每会话最多保留 MaxSnapshots 份：索引 0 的 match_start 快照永久保留，其余从最旧开始删。
	private static void PruneSnapshots(Session s) {
		try {
			while (s.snapshots.Count > MaxSnapshots) {
				SnapshotRef r = s.snapshots[1];
				s.snapshots.RemoveAt(1);
				if (r.entry != null) r.entry.snapshot = null;
				try {
					string path = SnapshotFullPath(r.rel);
					if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
				} catch (Exception __dx) { SRApi.Guard.Log("对局回放 清理旧快照", __dx); }
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 快照保留上限", __ex); }
	}

	private static string SnapshotFullPath(string rel) {
		try {
			if (string.IsNullOrEmpty(rel)) return null;
			return Path.Combine(ReplayDir(), rel.Replace('/', Path.DirectorySeparatorChar));
		} catch { return null; }
	}

	// 剥掉 <destroyed> 节点：与广播快照/布局预设一致，重载后它会让整批解析中断。
	private static void StripDestroyed(XmlDocument doc) {
		try {
			if (doc == null || doc.DocumentElement == null) return;
			List<XmlNode> bad = new List<XmlNode>();
			foreach (XmlNode n in doc.DocumentElement.ChildNodes) {
				if (n != null && n.NodeType == XmlNodeType.Element && n.Name == "destroyed") bad.Add(n);
			}
			for (int i = 0; i < bad.Count; i++) doc.DocumentElement.RemoveChild(bad[i]);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 剥离 destroyed", __ex); }
	}

	// ---------------- v2 事件预留：Placeable 只读差分（放置 / 删除） ----------------

	private static bool IsTrackedPiece(Placeable p) {
		try { return p != null && p.Placed && !p.MarkedForDestruction; } catch { return false; }
	}

	private static int PieceId(Placeable p) {
		try { int id = p.ID; if (id != 0) return id; } catch { }
		try { return p.GetInstanceID(); } catch { return 0; }
	}

	private static PieceSnap SnapshotPiece(Placeable p) {
		PieceSnap snap = new PieceSnap();
		try { snap.player = p.placedByPlayerNumber; } catch { }
		try { snap.name = p.Name; } catch { }
		try {
			Vector3 v = p.transform.position;
			snap.x = v.x;
			snap.y = v.y;
		} catch { }
		return snap;
	}

	// 建立基线：当前已放置方块全部记入 pieces，但不生成 place 事件。
	private static void SyncPiecesBaseline(Session s) {
		try {
			if (s == null) return;
			s.pieces.Clear();
			List<Placeable> all = Placeable.AllPlaceables;
			if (all != null) {
				for (int i = 0; i < all.Count; i++) {
					Placeable p = all[i];
					if (!IsTrackedPiece(p)) continue;
					int id = PieceId(p);
					if (id == 0) continue;
					s.pieces[id] = SnapshotPiece(p);
				}
			}
			s.opScanAt = Time.realtimeSinceStartup;
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 初始化方块基线", __ex); }
	}

	// 差分扫描：按 OpScanInterval 累积放置/删除事件到 pendingOps；force=true 时立即扫描（阶段条目落盘前）。
	private static void ScanOps(Session s, bool force) {
		if (s == null) return;
		try {
			float now = Time.realtimeSinceStartup;
			if (!force && s.opScanAt >= 0f && now - s.opScanAt < OpScanInterval) return;
			s.opScanAt = now;
			List<Placeable> all = Placeable.AllPlaceables;
			HashSet<int> seen = new HashSet<int>();
			if (all != null) {
				for (int i = 0; i < all.Count; i++) {
					Placeable p = all[i];
					if (!IsTrackedPiece(p)) continue;
					int id = PieceId(p);
					if (id == 0 || seen.Contains(id)) continue;
					seen.Add(id);
					PieceSnap prev;
					if (s.pieces.TryGetValue(id, out prev)) continue;
					PieceSnap snap = SnapshotPiece(p);
					s.pieces[id] = snap;
					AddOp(s, "place", snap);
				}
			}
			List<int> gone = null;
			foreach (KeyValuePair<int, PieceSnap> kv in s.pieces) {
				if (seen.Contains(kv.Key)) continue;
				if (gone == null) gone = new List<int>();
				gone.Add(kv.Key);
			}
			if (gone != null) {
				for (int i = 0; i < gone.Count; i++) {
					PieceSnap prev;
					if (!s.pieces.TryGetValue(gone[i], out prev)) continue;
					s.pieces.Remove(gone[i]);
					if (prev != null) AddOp(s, "destroy", prev);
				}
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 方块差分扫描", __ex); }
	}

	private static void AddOp(Session s, string type, PieceSnap piece) {
		try {
			if (s == null || piece == null) return;
			if (s.pendingOps.Count >= MaxPendingOps) {
				if (!s.opsCapped) {
					s.opsCapped = true;
					SRApi.LogWarn("[回放] 方块事件超过单条上限 " + MaxPendingOps + "，后续事件已丢弃: " + s.id);
				}
				return;
			}
			OpRow op = new OpRow();
			op.type = type;
			op.player = piece.player;
			op.time = DateTime.Now.ToString("HH:mm:ss");
			op.elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - s.startedRealtime);
			op.x = piece.x;
			op.y = piece.y;
			op.name = piece.name;
			s.pendingOps.Add(op);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 追加方块事件", __ex); }
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
		sb.Append("  \"version\": 2,\n");
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
		sb.Append("  \"snapshotCount\": ").Append(s.snapshots.Count.ToString(CultureInfo.InvariantCulture)).Append(",\n");
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
		if (!string.IsNullOrEmpty(en.snapshot)) b.Append(", \"snapshot\": ").Append(Q(en.snapshot));
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
		b.Append(", \"ops\": [");
		for (int i = 0; i < en.ops.Count; i++) {
			OpRow op = en.ops[i];
			if (i > 0) b.Append(", ");
			b.Append("{ \"type\": ").Append(Q(op.type));
			b.Append(", \"player\": ").Append(op.player.ToString(CultureInfo.InvariantCulture));
			b.Append(", \"time\": ").Append(Q(op.time));
			b.Append(", \"elapsed\": ").Append(op.elapsed.ToString("0.00", CultureInfo.InvariantCulture));
			b.Append(", \"pos\": { \"x\": ").Append(op.x.ToString("0.00", CultureInfo.InvariantCulture));
			b.Append(", \"y\": ").Append(op.y.ToString("0.00", CultureInfo.InvariantCulture)).Append(" }");
			b.Append(", \"name\": ").Append(Q(op.name)).Append(" }");
		}
		b.Append(']');
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

	// 删除一个会话：JSON + 同名快照目录（<sessionId>/）。
	private static void DeleteSessionFiles(string jsonPath) {
		if (string.IsNullOrEmpty(jsonPath)) return;
		try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch (Exception __ex) { SRApi.Guard.Log("对局回放 删除会话文件", __ex); }
		try {
			string name = Path.GetFileNameWithoutExtension(jsonPath);
			string dir = Path.Combine(ReplayDir(), name);
			if (Directory.Exists(dir)) Directory.Delete(dir, true);
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 删除会话快照目录", __ex); }
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
				DeleteSessionFiles(files[i]);
				done++;
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
					si.snapshots = ParseIntField(text, "snapshotCount");
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
			// scores 数组后面还跟着 ops 数组，必须取 scores 自己的结束括号（首个 ']'）。
			int end = line.IndexOf(']', i);
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
			sb.Append(SRApi.T("快照: ", "Snapshots: ")).Append(ParseIntField(json, "snapshotCount").ToString(CultureInfo.InvariantCulture)).Append('\n');
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
				string snap = ParseStrField(t, "snapshot");
				if (!string.IsNullOrEmpty(snap)) sb.Append("    ").Append(SRApi.T("快照: ", "Snapshot: ")).Append(snap).Append('\n');
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
				int placeOps = CountToken(t, "\"type\": \"place\"");
				int destroyOps = CountToken(t, "\"type\": \"destroy\"");
				if (placeOps + destroyOps > 0) {
					sb.Append("    ").Append(SRApi.T("操作: 放置 ", "Ops: place ")).Append(placeOps.ToString(CultureInfo.InvariantCulture));
					sb.Append(SRApi.T(" · 删除 ", " · destroy ")).Append(destroyOps.ToString(CultureInfo.InvariantCulture)).Append('\n');
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
			DeleteSessionFiles(si.path);
			if (_viewPath == si.path) { _viewPath = null; _viewText = null; _viewRows = null; }
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
					GUILayout.Label(si.snapshots.ToString(CultureInfo.InvariantCulture) + SRApi.T(" 快照", " snaps"),
						SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(70)));
					GUILayout.Label(SizeText(si.size), SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(66)));
					if (GUILayout.Button(SRApi.T("查看", "View"), SRApi.Ctl.Btn,
						GUILayout.Width(SRApi.Ctl.Sc(56)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
						_viewPath = si.path;
						_viewText = null;
						_viewRows = null;
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

			// 查看面板：阶段快照（可存为预设） + 只读时间线，固定在列表下方。
			if (!string.IsNullOrEmpty(_viewPath)) {
				GUILayout.Space(SRApi.Ctl.Sc(6));
				string text = ViewText(); // 顺带刷新 _viewRows
				string sessionId = Path.GetFileNameWithoutExtension(_viewPath);
				SRApi.Ctl.SectionTitle(SRApi.T("- 阶段快照（可存为预设） -", "- Phase snapshots (save as preset) -"));
				List<SnapRow> rows = _viewRows;
				if (rows == null || rows.Count == 0) {
					GUILayout.Label(SRApi.T("（本会话没有阶段快照）", "(no phase snapshots in this session)"), SRApi.Ctl.Label);
				} else {
					for (int i = 0; i < rows.Count; i++) {
						SnapRow r = rows[i];
						if (r == null) continue;
						GUILayout.BeginHorizontal();
						GUILayout.Label("[" + FormatElapsed(r.elapsed) + "] " + SRApi.T("回合 ", "R")
							+ r.round.ToString(CultureInfo.InvariantCulture) + " · " + r.kind + " · " + r.phase,
							SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(300)));
						GUILayout.Label(Path.GetFileName(r.snapshot), SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(170)));
						if (GUILayout.Button(SRApi.T("存为预设", "Save as preset"), SRApi.Ctl.Btn,
							GUILayout.Width(SRApi.Ctl.Sc(96)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
							SaveSnapshotAsPreset(sessionId, r);
							_pendingDelete = null;
						}
						GUILayout.FlexibleSpace();
						GUILayout.EndHorizontal();
					}
				}
				GUILayout.Space(SRApi.Ctl.Sc(6));
				SRApi.Ctl.SectionTitle(SRApi.T("- 时间线（只读） -", "- Timeline (read-only) -"));
				GUILayout.BeginHorizontal();
				if (GUILayout.Button(SRApi.T("关闭查看", "Close"), SRApi.Ctl.Btn,
					GUILayout.Width(SRApi.Ctl.Sc(80)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
					_viewPath = null;
					_viewText = null;
					_viewRows = null;
				}
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
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
			_viewRows = ParseSnapshotRows(json);
			_viewStamp = fi.LastWriteTimeUtc;
			return _viewText;
		} catch (Exception __ex) {
			SRApi.Guard.Log("对局回放 查看会话", __ex);
			return SRApi.T("（读取失败）", "(failed to read)");
		}
	}

	// 从行式 JSON 中收集带 snapshot 字段的条目（查看面板的「存为预设」行）。
	private static List<SnapRow> ParseSnapshotRows(string json) {
		List<SnapRow> rows = new List<SnapRow>();
		try {
			if (string.IsNullOrEmpty(json)) return rows;
			string[] lines = json.Split('\n');
			for (int i = 0; i < lines.Length; i++) {
				string t = lines[i].TrimStart();
				if (t.IndexOf("\"snapshot\": \"", StringComparison.Ordinal) < 0) continue;
				string snap = ParseStrField(t, "snapshot");
				if (string.IsNullOrEmpty(snap)) continue;
				SnapRow r = new SnapRow();
				r.snapshot = snap;
				r.time = ParseStrField(t, "time");
				r.elapsed = ParseFloatField(t, "elapsed");
				r.round = ParseIntField(t, "round");
				r.kind = ParseStrField(t, "event");
				r.phase = ParseStrField(t, "phase");
				rows.Add(r);
			}
		} catch (Exception __ex) { SRApi.Guard.Log("对局回放 解析快照行", __ex); }
		return rows;
	}

	// 「存为预设」：优先走布局预设槽位；槽位不存在（或未提供）时降级导出到 replays/exports/*.xml。
	private static void SaveSnapshotAsPreset(string sessionId, SnapRow row) {
		try {
			if (string.IsNullOrEmpty(sessionId) || row == null || string.IsNullOrEmpty(row.snapshot)) return;
			string path = SnapshotFullPath(row.snapshot);
			if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
				SRApi.Notify(SRApi.T("快照文件不存在: ", "Snapshot file not found: ") + row.snapshot);
				return;
			}
			string xml = File.ReadAllText(path, Encoding.UTF8);
			if (string.IsNullOrEmpty(xml)) {
				SRApi.Notify(SRApi.T("快照文件为空", "The snapshot file is empty"));
				return;
			}
			string displayName = sessionId + "-R" + row.round.ToString(CultureInfo.InvariantCulture) + "-" + (row.phase ?? "?");
			Func<string, string, bool> save = null;
			try { save = Slots.Require<Func<string, string, bool>>(SlotSaveXml); } catch (Exception __sx) { SRApi.Guard.Log("对局回放 取预设槽位", __sx); }
			if (save != null) {
				bool ok = false;
				try { ok = save(xml, displayName); } catch (Exception __cx) { SRApi.Guard.Log("对局回放 调用预设槽位", __cx); }
				if (ok) SRApi.Notify(SRApi.T("已存为布局预设: ", "Saved as layout preset: ") + displayName);
				else SRApi.Notify(SRApi.T("存为布局预设失败: ", "Failed to save the layout preset: ") + displayName);
				return;
			}
			// 槽位不存在：降级写文件（replays/exports/*.xml），并提示绝对路径。
			string dir = Path.Combine(ReplayDir(), "exports");
			Directory.CreateDirectory(dir);
			string fileBase = SafeFileNameForExport(displayName);
			string outPath = Path.Combine(dir, fileBase + ".xml");
			int n = 2;
			while (File.Exists(outPath)) {
				outPath = Path.Combine(dir, fileBase + "-" + n + ".xml");
				n++;
			}
			File.WriteAllText(outPath, xml, Utf8);
			SRApi.Notify(SRApi.T("布局预设功能不可用，快照已导出: ", "Layout presets unavailable; snapshot exported: ") + outPath);
			SRApi.LogInfo("[回放] 快照降级导出: " + outPath);
		} catch (Exception __ex) {
			SRApi.Guard.Log("对局回放 存为预设", __ex);
			SRApi.Notify(SRApi.T("存为预设失败", "Failed to save as preset"));
		}
	}

	// 降级导出用的文件名安全化（与布局预设的规则近似但不跨功能依赖）。
	private static string SafeFileNameForExport(string name) {
		try {
			string src = (name ?? "").Trim();
			if (src.Length == 0) src = "replay-snapshot";
			char[] bad = Path.GetInvalidFileNameChars();
			StringBuilder sb = new StringBuilder(src.Length);
			for (int i = 0; i < src.Length; i++) sb.Append(Array.IndexOf(bad, src[i]) < 0 ? src[i] : '_');
			string s = sb.ToString().Trim().TrimEnd('.');
			if (s.Length > 80) s = s.Substring(0, 80).Trim();
			return s.Length == 0 ? "replay-snapshot" : s;
		} catch { return "replay-snapshot"; }
	}

	private static int CountToken(string line, string token) {
		try {
			if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(token)) return 0;
			int n = 0;
			int i = 0;
			while (true) {
				i = line.IndexOf(token, i, StringComparison.Ordinal);
				if (i < 0) break;
				n++;
				i += token.Length;
			}
			return n;
		} catch { return 0; }
	}

	private static string SizeText(long bytes) {
		if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
		if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
		return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
	}
}
}
