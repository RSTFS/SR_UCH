// 自检页（底栏「自检」）：卡片式体检报告 + 可粘贴诊断报告 + 帧钩子表格。
// 只依赖 SR 的诊断数据，与设置项渲染无关。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：SelfCheck（自检卡片 / 诊断报告 / 帧钩子表格）

        // 自检块：启动自检结果（补丁目标 + 反射成员 + 扩展点 / 孤儿键 / 运行时异常）。
        // 换行标签：wordWrap 样式不会自动撑高，须按可用宽度实测高度，否则长行会被裁掉。
        private static void AuditLabel(string text) {
            if (string.IsNullOrEmpty(text)) return;
            float w = Mathf.Max(Sc(180f), SR.ContentArea.width - Sc(28f));
            float h = _labelWrap.CalcHeight(new GUIContent(text), w);
            GUILayout.Label(text, _labelWrap, GUILayout.Height(Mathf.Max(Sc(18f), h)));
        }

        // 诊断报告：自检、功能清单、帧钩子耗时、运行时异常拼成可粘贴文本，无需查日志文件。
        internal static string BuildDiagnosticReport() {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try {
                sb.AppendLine("=== SR_UCH 诊断报告 ===");
                sb.Append("游戏版本: ").AppendLine(Application.version);
                sb.Append("契约版本: v").AppendLine(SRContract.Version.ToString());
                sb.Append("补丁: ").Append(SR.AuditPatchOk).Append("/").Append(SR.AuditPatchTotal)
                  .Append("  反射: ").Append(SR.AuditRefOk).Append("/").Append(SR.AuditRefTotal).AppendLine();
                sb.Append("功能: ").Append(Features.Count).Append("  问题: ").Append(Features.ProblemCount).AppendLine();
                sb.Append("第三方插件: ").AppendLine(CompatSummary());
                sb.Append("启动耗时: ").AppendLine(SR.BootTimings ?? "(未记录)");
                sb.Append("API 注入: ").Append(SRApi.SinkCount()).Append("/").Append(SRApi.SinkTotal()).AppendLine();
                string[] miss = SRApi.MissingSinks();
                if (miss != null && miss.Length > 0) sb.Append("缺失注入: ").AppendLine(string.Join(",", miss));
                sb.Append("槽位: ").Append(Slots.Count).Append("  未提供: ").Append(Slots.MissCount).AppendLine();
                if (Slots.MissNames != null && Slots.MissNames.Count > 0) sb.Append("未提供的槽位: ").AppendLine(string.Join(", ", new List<string>(Slots.MissNames).ToArray()));
                sb.Append("运行时异常: ").AppendLine(SR.Guard.WarnCount.ToString());
                if (!string.IsNullOrEmpty(SR.Guard.LastWhat)) sb.Append("最近一次: ").Append(SR.Guard.LastWhat).Append(" - ").AppendLine(SR.Guard.LastError ?? "");
                IList<SR.AuditIssue> issues = SR.AuditIssues;
                if (issues != null) {
                    for (int i = 0; i < issues.Count; i++) {
                        sb.Append("  ✗ ").Append(issues[i].Kind).Append(" ").Append(issues[i].Target).Append(" <- ").AppendLine(issues[i].Scope);
                    }
                }
                sb.AppendLine(SR.TickProfileReport());
                sb.AppendLine(SR.AttributionReport());
            } catch (Exception __ex) { Guard.Log("生成诊断报告", __ex); }
            return sb.ToString();
        }

        private static SRApi.TableArgs _hookArgs;   // 帧钩子表格
        private static List<string[]> _hookRows = new List<string[]>();   // 每帧刷新

        internal static void RenderAuditBlock() {
            try {
                // 卡片式布局：每张卡 = 状态点 + 标题 + 一行结论 + 展开体。
                System.Collections.Generic.IList<SR.AuditIssue> issues = SR.AuditIssues;
                int bad = issues != null ? issues.Count : 0;
                bool allClean = bad == 0;

                Card(allClean, T("总体", "Overall"),
                    allClean ? T("一切正常", "Everything is fine")
                             : T(bad + " 项失效：开关还在，行为没了", bad + " broken target(s): the switches remain, the behaviour is gone"),
                    delegate {
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button(T("重新扫描", "Rescan"), _btn, GUILayout.Width(Sc(96)), GUILayout.Height(Sc(24)))) {
                            // 真正重新采集：重扫 Harmony 补丁表并重建归因快照。
                            try {
                                SR.ScanPatches();
                                SR.Notify(T("已重新扫描补丁与归因", "Patches and attribution rescanned"));
                            } catch (Exception __rs) { Guard.Log("自检/重新扫描", __rs); }
                        }
                        if (GUILayout.Button(T("复制报告", "Copy report"), _btn, GUILayout.Width(Sc(96)), GUILayout.Height(Sc(24)))) {
                            string rep = BuildDiagnosticReport();
                            bool ok = false;
                            try { GUIUtility.systemCopyBuffer = rep; ok = true; } catch (Exception __cb) { Guard.Log("复制诊断报告", __cb); }
                            SR.Notify(ok ? T("诊断报告已复制", "Report copied") : T("复制失败，已写入日志", "Copy failed; written to log"));
                            if (!ok) { try { SR.LogInfo(rep); } catch (Exception __lg) { Guard.Log("诊断报告写日志", __lg); } }
                        }
                        if (GUILayout.Button(T("写入日志", "Write to log"), _btn, GUILayout.Width(Sc(96)), GUILayout.Height(Sc(24)))) {
                            try { SR.LogInfo(BuildDiagnosticReport()); SR.Notify(T("已写入日志", "Written to log")); }
                            catch (Exception __lg) { Guard.Log("写诊断日志", __lg); }
                        }
                        GUILayout.FlexibleSpace();
                        GUILayout.EndHorizontal();
                    });

                bool __cfgOk = SR.OrphanKeyCount == 0 && SR.FallbackSectionCount == 0;
                Card(bad == 0 && __cfgOk, T("目标与配置", "Targets & config"),
                    bad == 0 ? T(SR.AuditPatchOk + "/" + SR.AuditPatchTotal + " 补丁 · " + SR.AuditRefOk + "/" + SR.AuditRefTotal + " 反射 · 孤儿键 " + SR.OrphanKeyCount + " · 异常 " + SR.Guard.WarnCount,
                                 SR.AuditPatchOk + "/" + SR.AuditPatchTotal + " patches · " + SR.AuditRefOk + "/" + SR.AuditRefTotal + " members · orphan keys " + SR.OrphanKeyCount + " · errors " + SR.Guard.WarnCount)
                             : T(bad + " 项失效", bad + " broken"),
                    delegate {
                        if (bad > 0) {
                        // 不用内层滚动区：页面本身可滚，嵌套 BeginScrollView 会让排版错位。
                        for (int i = 0; i < issues.Count; i++) {
                            string kindRaw = issues[i].Kind;
                            string kindTxt = kindRaw == "补丁" ? T("补丁", "Patch") : kindRaw == "反射" ? T("反射", "Reflection") : kindRaw;
                            string tgt = issues[i].Target ?? "";
                            float h = Mathf.Max(_labelWrap.CalcHeight(new GUIContent(tgt), Sc(320)), Ctl.TableRowH());
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("✗ " + kindTxt, _label, GUILayout.Width(Sc(64)), GUILayout.Height(h));
                            GUILayout.Label(tgt, _labelWrap, GUILayout.Width(Sc(320)), GUILayout.Height(h));
                            GUILayout.Label(issues[i].Scope ?? "", _labelWrap, GUILayout.Width(Sc(220)), GUILayout.Height(h));
                            GUILayout.EndHorizontal();
                        }
                        }
                        // 孤儿键清理：原子改写 cfg（留 .bak）后重载配置。
                        GUILayout.Space(Sc(4));
                        GUILayout.BeginHorizontal();
                        try {
                            bool had = SR.OrphanKeyCount > 0;
                            bool oldEn2 = GUI.enabled;
                            try {
                                GUI.enabled = oldEn2 && had;
                                if (GUILayout.Button(T("清理孤儿键（", "Clean orphan keys (") + SR.OrphanKeyCount + ")", _btn,
                                        GUILayout.Width(Sc(170)), GUILayout.Height(Sc(24)))) {
                                    int __n = SR.CleanOrphanKeys();
                                    SR.Notify(__n > 0
                                        ? T("已清理 ", "Cleaned ") + __n + T(" 个孤儿键（原文件已备份）", " orphan keys (backup kept)")
                                        : T("没有可清理的孤儿键", "Nothing to clean"));
                                    try { SR.ScanPatches(); } catch (Exception __rs) { Guard.Log("自检/清理后重扫", __rs); }
                                }
                            } finally { GUI.enabled = oldEn2; }
                            GUILayout.Label(had
                                    ? T("配置里有已不再使用的键；清理会先备份原文件", "The config still has keys nothing reads any more; the original file is backed up first")
                                    : T("没有孤儿键", "No orphan keys"),
                                _label, GUILayout.Height(Sc(24)));
                            GUILayout.FlexibleSpace();
                        } finally { GUILayout.EndHorizontal(); }

                        // 契约版本：宿主 API 版本与功能最低要求，升级/降级时一眼可见。
                        AuditLabel(T("契约版本", "Contract") + "：API v" + SRContract.Version
                            + " · " + T("最低要求", "min required") + " ≤ v" + SRContract.Version);
                        AuditLabel(T("配置：孤儿键", "Config: orphan keys") + "：" + SR.OrphanKeyCount
                            + " · " + T("无主段", "unowned sections") + "：" + SR.FallbackSectionCount
                            + " · " + T("运行时异常", "runtime errors") + "：" + SR.Guard.WarnCount);
                        if (!string.IsNullOrEmpty(SR.Guard.LastWhat))
                            AuditLabel(T("最近一次", "Last") + "：" + SR.Guard.LastWhat + " - " + (SR.Guard.LastError ?? ""));
                        try { AuditLabel(SR.AttributionReport()); } catch (Exception __at) { Guard.Log("自检/归因", __at); }
                    });

                RenderActiveSelfTestCard();
                RenderGuardRecentCard();

                List<SR.KeyConflict> conflicts = SR.FindKeyConflicts();
                Card(conflicts.Count == 0, T("键位冲突", "Key conflicts"),
                    conflicts.Count == 0 ? T("没有重复的快捷键", "no duplicate hotkeys")
                                         : T(conflicts.Count + " 组快捷键重复", conflicts.Count + " duplicate combo(s)"),
                    delegate {
                        for (int i = 0; i < conflicts.Count; i++) {
                            SR.KeyConflict kc = conflicts[i];
                            if (kc == null) continue;
                            GUILayout.BeginHorizontal();
                            GUILayout.Label("✗ " + (kc.Combo ?? ""), _label, GUILayout.Width(Sc(140)), GUILayout.Height(Ctl.TableRowH()));
                            GUILayout.Label(kc.A + "  ↔  " + kc.B, _labelWrap, GUILayout.ExpandWidth(true), GUILayout.Height(Ctl.TableRowH()));
                            GUILayout.EndHorizontal();
                        }
                        if (conflicts.Count == 0) AuditLabel(T("（自动快捷键与右键绑定都在检测范围内；重复时两条动作会同时触发）", "(covers auto hotkeys and right-click bindings; a duplicate fires both actions)"));
                    });

                RenderKeyCenterCard();

                // 剖析关闭时不采样、不重算，卡片只显示一行提示（开关在卡片内）。
                bool __profOn = false;
                try { __profOn = SR.TickProfilingEnabled; } catch (Exception __tp) { Guard.Log("自检/读性能开关", __tp); }
                string tickRep = __profOn ? SR.TickProfileReport() : null;
                Card(true, T("帧钩子", "Frame hooks"),
                    T("按名字排序，超阈值才需要关注", "sorted by name; only over-threshold rows matter"),
                    delegate {
                        // 剖析开关（默认关）：关掉后既不写日志也不刷新列表。
                        SRApi.RenderSectionEntries(Keys.Settings, delegate (ConfigEntryBase e) {
                            return e.Definition.Key == "Tick Profiling";
                        });
                        // 预算状态：压力与当前档位（超预算时非输入钩子自动降频）。
                        try { AuditLabel(SR.TickBudgetText()); } catch (Exception __tb) { Guard.Log("自检/预算状态", __tb); }
                        if (!__profOn) {
                            AuditLabel(T("（剖析已关闭：勾选上面的开关后开始采样）", "(profiling is off: tick the switch above to start sampling)"));
                            return;
                        }
                        // 用核心 TableDraw：按内容高，不套内层滚动区。
                        _hookRows = SR.TickProfileRows();   // 静态字段：避免闭包钉死首帧数据
                        List<string[]> hrows = _hookRows;
                        if (hrows.Count == 0) { AuditLabel(T("（还没有采样）", "(no samples yet)")); return; }
                        if (_hookArgs == null) {
                            _hookArgs = new SRApi.TableArgs();
                            _hookArgs.colW = new float[] { 200f, 90f, 90f, 70f, 60f };
                            _hookArgs.order = new int[] { 0, 1, 2, 3, 4 };
                            _hookArgs.hidden = new HashSet<int>();
                            _hookArgs.vis = new List<int>();   // 由核心推导可见列
                            _hookArgs.resizable = true;
                            _hookArgs.title = delegate (int c) { return c == 0 ? T("钩子", "Hook") : c == 1 ? T("平均", "Avg") : c == 2 ? T("峰值", "Max") : c == 3 ? T("频率", "Rate") : T("超限", "Over"); };
                            _hookArgs.rowCells = delegate (int row) { return _hookRows[row]; };
                        }
                        _hookArgs.rowCount = hrows.Count;
                        float __hh = SRApi.Ctl.TableHeaderH(), __rh = SRApi.Ctl.TableRowH();
                        float __h = __hh + hrows.Count * __rh;
                        _hookArgs.headerH = __hh; _hookArgs.rowH = __rh; _hookArgs.rowsH = hrows.Count * __rh;
                        _hookArgs.mp = Event.current != null ? Event.current.mousePosition : Vector2.zero;
                        Rect __hbox = GUILayoutUtility.GetRect(0f, __h, GUILayout.ExpandWidth(true));
                        _hookArgs.box = __hbox;
                        _hookArgs.rowsClip = new Rect(__hbox.x, __hbox.y + __hh, __hbox.width, hrows.Count * __rh);
                        _hookArgs.stripW = __hbox.width;
                        _hookArgs.pinCol = -1; _hookArgs.alignLeftCol = 0; _hookArgs.headerLeft = true;
                        SR.TableDraw(_hookArgs);
                    });

            } catch (Exception __ex) { Guard.Log("渲染自检", __ex); }
        }

        // 一张卡片：状态点 + 标题 + 一行结论 + 可选展开体。
        // 展开状态按标题记在 _cardOpen 字段：传 ref 给局部变量只会改副本，永远打不开也收不上。
        private static readonly HashSet<string> _cardOpen = new HashSet<string>();

        private static GUIStyle _cdDot, _cdTitle, _cdSum;
        private static void EnsureCardStyles() {
            if (_cdSum != null) return;
            _cdDot = new GUIStyle(_label); _cdDot.alignment = TextAnchor.MiddleCenter;   // 与标题同一基线
            _cdTitle = new GUIStyle(_labelWrap); _cdTitle.alignment = TextAnchor.MiddleLeft;
            _cdSum = new GUIStyle(_label); _cdSum.alignment = TextAnchor.MiddleLeft;
        }

        private static void Card(bool ok, string title, string summary, Action body) {
            try {
                bool canExpand = body != null;
                bool open = canExpand && _cardOpen.Contains(title);
                GUILayout.BeginVertical(GUI.skin.box);
                EnsureCardStyles();
                // 行高 = 标题与结论较高者 + 2px，三者同高并垂直居中，标题不会被裁。
                float __titleH = Mathf.Max(Sc(20), _cdTitle.CalcHeight(new GUIContent(title), Sc(120)));
                float __textW0 = Mathf.Max(Sc(120), SRApi.ContentArea.width - Sc(18) - Sc(120) - Sc(30));
                float __sumH = Mathf.Max(Sc(20), _cdSum.CalcHeight(new GUIContent(summary), __textW0));
                float __rowH = Mathf.Max(__titleH, __sumH) + Sc(2);
                // 结论的 CalcHeight 必须用与 Label 相同的可用宽度，否则多行明细按宽度算错会被裁。
                float __textW = Mathf.Max(Sc(120), SRApi.ContentArea.width - Sc(18) - Sc(120) - Sc(30));
                GUILayout.BeginHorizontal(GUILayout.Height(__rowH));
                Color prev = GUI.color;
                try {
                    GUI.color = ok ? new Color(0.55f, 0.92f, 0.55f) : new Color(1f, 0.78f, 0.35f);
                    GUILayout.Label(ok ? "●" : "▲", _cdDot, GUILayout.Width(Sc(18)), GUILayout.Height(__rowH));
                } finally { GUI.color = prev; }
                GUILayout.Label(title, _cdTitle, GUILayout.Width(Sc(120)), GUILayout.Height(__rowH));
                GUILayout.Label(summary, _cdSum, GUILayout.MinHeight(__rowH));
                GUILayout.EndHorizontal();
                // 整行可点：点任意位置展开/收起。
                if (canExpand) {
                    Rect __row = GUILayoutUtility.GetLastRect();
                    Event __e = Event.current;
                    if (__e != null && __e.type == EventType.MouseUp && __e.button == 0 && __row.Contains(__e.mousePosition)) {
                        if (open) _cardOpen.Remove(title); else _cardOpen.Add(title);
                        __e.Use();
                    }
                }
                if (open) {
                    GUILayout.Space(Sc(4));
                    // body 抛异常也必须先收掉布局组，否则后续控件/按钮全部失效。
                    try { body(); } catch (Exception __b) { Guard.Log("自检卡片内容 " + title, __b); }
                }
                GUILayout.EndVertical();
                GUILayout.Space(Sc(4));
            } catch (Exception __ex) { Guard.Log("自检卡片 " + title, __ex); }
        }

}
}
