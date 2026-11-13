// 布局预设 · 关卡记录器联动（BuildingPresets 分部）
//
// 只做一层「薄适配」：把本功能已有的能力以 BCL 委托登记到契约层槽位，
// 让关卡记录器在拆分构建下也能取用，而不需要引用 BuildingPresets 的类型。
// 刻意不改任何既有加载/保存逻辑，只新增入口。
//
// 跨分部约束：本文件只调用同类的【方法】，不直接读写其它分部声明的 private static
// 字段（region-access 棘轮会拦）。枚举预设走 PresetsRoot() + FindPreset()，
// 不碰 _presets。
//
// 记录器侧四个槽位（全部 BCL 形状）：
//   sr.rec.snapshot    Func<string[]>  -> { xml, blocks, label }，取不到返回 null
//   sr.rec.savePreset  Func<string,string,bool> (name, xml) -> 是否落盘成功
//   sr.rec.listPresets Func<string[]>  -> 每项 "key|显示名（N 块）"
//   sr.rec.loadPreset  Action<string>  (key) -> 走既有 LoadPreset 流程
// 反向：sr.rec.note 由记录器 Provide，本功能在发起载入时通知它记一条。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class BuildingPresets {
        private const int RecListCap = 80;

        internal static void ProvideRecordSlots() {
            try {
                Slots.Provide("sr.rec.snapshot", (Func<string[]>)(() => SnapshotForRecorder()));
                Slots.Provide("sr.rec.savePreset", (Func<string, string, bool>)((n, x) => SaveForRecorder(n, x)));
                Slots.Provide("sr.rec.listPresets", (Func<string[]>)(() => ListForRecorder()));
                Slots.Provide("sr.rec.loadPreset", (Action<string>)(k => LoadForRecorder(k)));
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/登记记录器槽位", __ex);
            }
        }

        // LoadPreset 入口调用。措辞是「发起载入」而不是「载入成功」：
        // 该函数在二次确认、跨关卡替换、树屋流程里都可能提前返回，不能冒充成功。
        internal static void NotePresetLoad(string name) {
            try {
                Action<string> note = Slots.Require<Action<string>>("sr.rec.note");
                if (note != null) note(name ?? "");
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/通知记录器", __ex);
            }
        }

        // 静默版快照：TryGetCurrentSnapshot 在失败时会 SRApi.Notify 弹提示，
        // 记录器每回合自动抓一次，弹提示会刷屏，所以这里重写一遍但只返回 null。
        private static string[] SnapshotForRecorder() {
            try {
                if (!SRApi.GateMaster) return null;
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return null;
                GameControl gc = lm.CurrentGameController as GameControl;
                if (gc == null) return null;
                QuickSaver qs = gc.GetComponent<QuickSaver>();
                if (qs == null) return null;
                XmlDocument doc = qs.GetCurrentXmlSnapshot(false);
                if (doc == null || doc.DocumentElement == null) return null;
                StripDestroyed(doc);
                int blocks = CountBlocks(doc);
                string level = LevelFromDoc(doc);
                string label = CurrentLevelDisplayName(level);
                return new string[] { doc.OuterXml, blocks.ToString(CultureInfo.InvariantCulture), label };
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/记录器快照", __ex);
                return null;
            }
        }

        // 把记录器里某一份历史快照存成预设：沿用 SaveXml 的全部校验（大小上限、
        // 同名二次确认、数量上限、srName 写入、索引落盘），不另造一条写入路径。
        private static bool SaveForRecorder(string name, string xml) {
            try {
                if (string.IsNullOrEmpty(xml)) return false;
                string n = (name ?? "").Trim();
                if (n.Length == 0) n = "Rec " + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (n.Length > 40) n = n.Substring(0, 40);
                XmlDocument doc = ParseXmlSafe(xml);
                if (doc == null || doc.DocumentElement == null) return false;
                string mode = "UNKNOWN";
                try {
                    GameSettings st = GameSettings.GetInstance();
                    if (st != null) mode = st.GameMode.ToString();
                } catch (Exception __ms) { SRApi.Guard.Log("布局预设/记录器读模式", __ms); }
                string level = LevelFromDoc(doc);
                SaveXml(xml, n, mode, CountBlocks(doc), level,
                        CurrentLevelDisplayName(level), CurrentLevelCode(level));
                return true;
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/记录器存预设", __ex);
                return false;
            }
        }

        // 从磁盘目录枚举，再用 FindPreset 取显示名；不直接接触 _presets 字段。
        private static string[] ListForRecorder() {
            var outp = new List<string>();
            try {
                EnsureLoaded();
                string root = PresetsRoot();
                if (!Directory.Exists(root)) return outp.ToArray();
                string[] files = Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length && outp.Count < RecListCap; i++) {
                    string rel = files[i].Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string key = rel.Replace('\\', '/');
                    PresetInfo p = FindPreset(key);
                    if (p == null) continue;
                    outp.Add(p.Key + "|" + (string.IsNullOrEmpty(p.Name) ? p.Key : p.Name) +
                             " (" + p.Blocks + " blocks)");
                }
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/记录器列预设", __ex);
            }
            return outp.ToArray();
        }

        private static void LoadForRecorder(string key) {
            try {
                if (string.IsNullOrEmpty(key)) return;
                EnsureLoaded();
                PresetInfo p = FindPreset(key);
                if (p == null) {
                    SRApi.NotifyT("找不到该预设：{0}", "Preset not found: {0}", key);
                    return;
                }
                LoadPreset(p);
            } catch (Exception __ex) {
                SRApi.Guard.Log("布局预设/记录器载入", __ex);
            }
        }
    }
}
