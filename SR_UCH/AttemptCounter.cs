using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
    //尝试计数（整合自 Attempt Counter mod + UchTweaks FreeplayAttempts）：
    //统计玩家在每个关卡上的尝试次数（挑战模式失败重置 + 自由模式死亡重置），
    //在选关信息面板（FeaturedQuickInfoPane）里显示"我的尝试次数"，数据存本地 JSON。
    //适配当前游戏版本 API：NotifyChallengeAttempt(bool,int) / resetPlayerCharacter(Character,bool)。
    public class AttemptCounter : ITweak {
        private static MainPlugin _mp;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _toggleKey;
        private static bool _runtimeOn = true; 

        public static bool Enabled { get { return _enabled != null && _enabled.Value; } }
        public static bool RuntimeOn { get { return _runtimeOn; } }

        public static int RecordedLevels { get { return _counts.Count; } }
        public static int TotalAttempts {
            get {
                int sum = 0;
                foreach (KeyValuePair<string, int> kv in _counts) sum += kv.Value;
                return sum;
            }
        }

        public static void ClearAll() {
            _counts.Clear();
            Save();
            HideAllRows();
        }

        //tracker state
        private static Dictionary<string, int> _counts = new Dictionary<string, int>();
        private static bool _initialized;
        private static string FilePath {
            get { return Path.Combine(Paths.ConfigPath, "AttemptCounter.json"); }
        }

        private static Dictionary<FeaturedQuickInfoPane, Text> _rows = new Dictionary<FeaturedQuickInfoPane, Text>();

        public void Initialize(MainPlugin plugin) {
            _mp = plugin;
            _enabled = plugin.Config.Bind("尝试计数", "Enabled", true,
                "尝试计数总开关：统计每个关卡的尝试次数（挑战 + 自由模式）");
            _toggleKey = plugin.Config.Bind("尝试计数", "Toggle Key", KeyCode.F4,
                "游戏内开关尝试计数显示（再次按下恢复；支持组合键：点按钮后在按住 Shift/Ctrl/Alt 的同时按主键设置）");
            ModManager.RegisterKey("尝试计数-开关", _toggleKey, "press");
            _runtimeOn = _enabled.Value;
            InitTracker();
            Harmony.CreateAndPatchAll(typeof(AttemptCounter));
        }

        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPrefix]
        static void Controls() {
            if (!ModManager.AllEnabled || !Enabled) return;
            if (_toggleKey != null && ModManager.ComboKeyDown(_toggleKey)) {
                _runtimeOn = !_runtimeOn;
                if (!_runtimeOn) HideAllRows();
            }
        }

        [HarmonyPatch(typeof(ChallengeControl), "NotifyChallengeAttempt")]
        [HarmonyPostfix]
        static void OnChallengeAttempt() {
            if (!ModManager.AllEnabled || !Enabled || !_runtimeOn) return;
            string code = GameState.GetInstance().currentSnapshotInfo.snapshotCode;
            if (string.IsNullOrEmpty(code)) return;
            Increment(code);
        }

        [HarmonyPatch(typeof(FreePlayControl), "resetPlayerCharacter")]
        [HarmonyPrefix]
        static void OnPlayerReset(Character c) {
            if (!ModManager.AllEnabled || !Enabled || !_runtimeOn) return;
            if (c == null || !c.isClient) return;
            string code = GameState.GetInstance().currentSnapshotInfo.snapshotCode;
            if (string.IsNullOrEmpty(code)) return;
            Increment(code);
        }

        [HarmonyPatch(typeof(FeaturedQuickInfoPane), "SetSnapshotInfo")]
        [HarmonyPostfix]
        static void OnSetSnapshotInfo(FeaturedQuickInfoPane __instance, UndergroundComputer.FeaturedLevelData featuredLevelData) {
            if (!ModManager.AllEnabled || !Enabled || !_runtimeOn) return;
            if (featuredLevelData == null) { SetVisible(__instance, false, null); return; }
            try {
                string code = featuredLevelData.code;
                int featuredAttempts = featuredLevelData.attempts;
                int count = GetCount(code);
                bool visible = featuredAttempts > 0 || count > 0;
                SetVisible(__instance, visible, string.Format(ModManager.T("我的尝试次数: {0}", "My attempts: {0}"), count));
            } catch { }
        }

        [HarmonyPatch(typeof(FeaturedQuickInfoPane), "SetLocalSaveInfo")]
        [HarmonyPostfix]
        static void OnSetLocalSaveInfo(FeaturedQuickInfoPane __instance) {
            if (!ModManager.AllEnabled || !Enabled || !_runtimeOn) return;
            SetVisible(__instance, false, null);
        }

        [HarmonyPatch(typeof(FeaturedQuickInfoPane), "SetArchivedLevelInfo")]
        [HarmonyPostfix]
        static void OnSetArchivedLevelInfo(FeaturedQuickInfoPane __instance) {
            if (!ModManager.AllEnabled || !Enabled || !_runtimeOn) return;
            SetVisible(__instance, false, null);
        }

        //---- tracker ----
        private static void InitTracker() {
            if (_initialized) return;
            _counts = Load();
            _initialized = true;
            MainPlugin.ModLogger.LogInfo("[尝试计数] 已加载 " + _counts.Count + " 个关卡的记录");
        }

        private static void Increment(string code) {
            if (string.IsNullOrEmpty(code)) return;
            string key = BuildKey(code);
            int count;
            _counts.TryGetValue(key, out count);
            _counts[key] = count + 1;
            Save();
        }

        private static int GetCount(string code) {
            if (string.IsNullOrEmpty(code)) return 0;
            string key = BuildKey(code);
            int count;
            _counts.TryGetValue(key, out count);
            return count;
        }

        private static string BuildKey(string code) {
            return "code:" + GameSparksQuery.SanitizeSnapshotCode(code);
        }

        //---- storage: AttemptCounter.json（与原 Attempt Counter mod 兼容） ----
        private static Dictionary<string, int> Load() {
            if (!File.Exists(FilePath)) return new Dictionary<string, int>();
            try {
                return Parse(File.ReadAllText(FilePath));
            } catch (Exception e) {
                BackupCorruptFile(e);
                return new Dictionary<string, int>();
            }
        }

        private static void Save() {
            try {
                File.WriteAllText(FilePath, Serialize());
            } catch (Exception e) {
                MainPlugin.ModLogger.LogError("[尝试计数] 保存失败: " + e.Message);
            }
        }

        private static string Serialize() {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            bool first = true;
            foreach (KeyValuePair<string, int> pair in _counts) {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  ").Append(EscapeString(pair.Key)).Append(": ").Append(pair.Value);
            }
            sb.Append("\n}");
            return sb.ToString();
        }

        private static Dictionary<string, int> Parse(string text) {
            Dictionary<string, int> dict = new Dictionary<string, int>();
            int pos = 0;
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length || text[pos] != '{') throw new FormatException("Expected '{'");
            pos++;
            while (true) {
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;
                if (text[pos] == '}') { pos++; break; }
                string key = ReadString(text, ref pos);
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length || text[pos] != ':') throw new FormatException("Expected ':'");
                pos++;
                SkipWhitespace(text, ref pos);
                int value = ReadInt(text, ref pos);
                dict[key] = value;
                SkipWhitespace(text, ref pos);
                if (pos >= text.Length) break;
                if (text[pos] == ',') { pos++; continue; }
                if (text[pos] == '}') { pos++; break; }
                throw new FormatException("Expected ',' or '}'");
            }
            return dict;
        }

        private static void SkipWhitespace(string text, ref int pos) {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        }

        private static string ReadString(string text, ref int pos) {
            if (pos >= text.Length || text[pos] != '"') throw new FormatException("Expected '\"'");
            pos++;
            StringBuilder sb = new StringBuilder();
            while (pos < text.Length && text[pos] != '"') {
                char c = text[pos];
                if (c == '\\' && pos + 1 < text.Length) {
                    char nxt = text[pos + 1];
                    if (nxt == '"' || nxt == '\\' || nxt == '/') sb.Append(nxt);
                    else if (nxt == 'n') sb.Append('\n');
                    else if (nxt == 't') sb.Append('\t');
                    else if (nxt == 'r') sb.Append('\r');
                    else sb.Append(nxt);
                    pos += 2;
                } else {
                    sb.Append(c);
                    pos++;
                }
            }
            if (pos >= text.Length) throw new FormatException("Unterminated string");
            pos++;
            return sb.ToString();
        }

        private static int ReadInt(string text, ref int pos) {
            int start = pos;
            if (pos < text.Length && text[pos] == '-') pos++;
            while (pos < text.Length && char.IsDigit(text[pos])) pos++;
            if (start == pos) throw new FormatException("Expected number");
            return int.Parse(text.Substring(start, pos - start));
        }

        private static string EscapeString(string s) {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s) {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\r') sb.Append("\\r");
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static void BackupCorruptFile(Exception e) {
            string backup = string.Format("{0}.corrupt.{1:yyyyMMdd-HHmmss}", FilePath, DateTime.Now);
            MainPlugin.ModLogger.LogWarning("[尝试计数] 文件解析失败（" + e.Message + "），备份到 " + backup);
            try { File.Move(FilePath, backup); } catch { }
        }

        //---- ui ----
        private static Text GetOrCreateRow(FeaturedQuickInfoPane pane) {
            Text row;
            if (_rows.TryGetValue(pane, out row) && row != null) return row;
            if (pane.attemptsText == null) return null;
            GameObject original = pane.attemptsText.gameObject;
            GameObject go = UnityEngine.Object.Instantiate(original, original.transform.parent);
            go.name = "PersonalAttemptsText";
            go.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
            Text text = go.GetComponent<Text>();
            text.text = ModManager.T("我的尝试次数: 0", "My attempts: 0");
            text.color = new Color(1f, 0.84f, 0.3f, 1f);
            text.fontStyle = FontStyle.Bold;
            _rows[pane] = text;
            return text;
        }

        private static void SetVisible(FeaturedQuickInfoPane pane, bool visible, string text) {
            Text row = GetOrCreateRow(pane);
            if (row == null) return;
            row.gameObject.SetActive(visible);
            if (text != null) row.text = text;
        }

        private static void HideAllRows() {
            foreach (KeyValuePair<FeaturedQuickInfoPane, Text> pair in _rows) {
                if (pair.Value != null) pair.Value.gameObject.SetActive(false);
            }
        }
    }
}
