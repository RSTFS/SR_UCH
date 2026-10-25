// 实验栏目：注册侧栏栏目和三页、生命周期。
// 页面拆分：p1 关卡边界 -> Experiments.Bounds.cs；p2 其它 -> Experiments.Misc.cs；p3 场景 -> Experiments.Scene.cs。

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
	{
		// 栏目注册：本地化文案、侧栏顺序、三页页签。
		private static void SelfReg() {
			SRApi.LocSec("Experiments", "实验", null);
            SRApi.Nav("Experiments", 100); //侧栏栏目顺序 100
            // 本栏目自绘，共三页。页签顺序由 Order 决定：p3 场景(10) / p1 关卡边界(20) / p2 其它(30)，id 与文件对应关系固定。
			SRApi.RegisterPage("Experiments", "p1", "关卡边界", "Bounds", 20, Render);
			SRApi.RegisterPage("Experiments", "p2", "其它", "Misc", 30, RenderPage2);
			SRApi.RegisterPage("Experiments", "p3", "场景", "Scene", 10, RenderPage3);
		}

		// 状态行：只读、可复制。
		// 高度必须取 SRApi.Ctl.TextHeight(text, w)：中文有字形下沉，固定高度会裁字；该函数内部已含下沉余量并带缓存。
		private static void DrawStatus(string text) {
			try {
				if (string.IsNullOrEmpty(text)) return;
				float w = Mathf.Max(SRApi.Ctl.Sc(160), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(40));
				float h = SRApi.Ctl.TextHeight(text, w);
				GUILayout.Label(text, SRApi.Ctl.LabelWrap, GUILayout.Width(w), GUILayout.Height(h));
			} catch (Exception __ex) { SRApi.Guard.Log("状态行", __ex); }
		}

		public void Initialize(IFeatureHost plugin)
		{
			SelfReg();
			InitCleanup(plugin);
		}

}

// 触发器：在关卡内画一块矩形区域，玩家进入时执行动作（提示 / 清掉区域内的方块）。
// 设计要点：数据存在配置里（简单分隔符），运行期只在房主端判定，触发状态按"进入沿"去重。
[SrFeature("sr.triggers", "Triggers", "1.0")]
public class Triggers : ITweak {
    private static ConfigEntry<bool> _on;
    private static ConfigEntry<string> _list;
    private static readonly System.Collections.Generic.List<Tg> _items = new System.Collections.Generic.List<Tg>();
    private static bool _loaded;
    private static float _lastTick = -999f;
    private static Vector2 _pointA, _pointB;
    private static bool _hasA, _hasB;
    private static int _pendingAction;
    private static readonly System.Collections.Generic.HashSet<int> _inside = new System.Collections.Generic.HashSet<int>();

    private class Tg {
        public string name = "T1";
        public float x, y, w, h;
        public int action;      // 0 = 提示，1 = 清除区域内方块
        public string text = "";
        public bool once;
        public bool enabled = true;
        public bool fired;
    }

    public void Initialize(IFeatureHost plugin) {
        try {
            SRApi.LocSec("Triggers", "触发器", null);
            SRApi.Nav("Triggers", 130);
            SRApi.RegisterPage("Triggers", "main", "触发器", "Triggers", 10, Render);
            _on = plugin.Config.Bind("Triggers", "Enabled", true, "触发器总开关：关闭后不再判定区域进入。");
            _list = plugin.Config.Bind("Triggers", "List", "", "触发器数据（内部格式，请勿手改）。");
            SRApi.LocKey("Triggers", "Enabled", "启用触发器", null);
            SRApi.LocDesc("Triggers", "Enabled", "触发器总开关\n关闭后不再判定玩家进入区域\n已保存的触发器不会丢失。", "master switch for triggers\nwhen off, region entry is no longer evaluated\nsaved triggers are kept");
            SRApi.LocKey("Triggers", "List", "触发器数据", null);
            SRApi.LocDesc("Triggers", "List", "触发器数据（内部）\n由界面自动写入，不建议手动修改\n格式错误会被忽略。", "trigger data (internal)\nwritten by the UI, do not edit by hand\ninvalid entries are ignored");
            SRApi.RegisterTick("触发器", Tick);
            SRApi.RegisterSceneHook(ResetRuntime);
            SRApi.RegisterDismissHook(ResetRuntime);
        } catch (Exception __ex) { SRApi.Guard.Log("触发器 初始化", __ex); }
    }

    // ---------------- 序列化 ----------------
    private static void Load() {
        if (_loaded) return;
        _loaded = true;
        _items.Clear();
        try {
            string raw = _list != null ? _list.Value : null;
            if (string.IsNullOrEmpty(raw)) return;
            foreach (string row in raw.Split(';')) {
                if (string.IsNullOrEmpty(row)) continue;
                string[] f = row.Split('|');
                if (f.Length < 9) continue;
                Tg g = new Tg();
                g.name = f[0];
                float.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g.x);
                float.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g.y);
                float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g.w);
                float.TryParse(f[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g.h);
                int.TryParse(f[5], out g.action);
                g.text = f[6];
                g.once = f[7] == "1";
                g.enabled = f[8] == "1";
                _items.Add(g);
            }
        } catch (Exception __ex) { SRApi.Guard.Log("触发器 读取", __ex); }
    }

    private static void Save() {
        try {
            if (_list == null) return;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < _items.Count; i++) {
                Tg g = _items[i];
                if (i > 0) sb.Append(';');
                sb.Append(g.name).Append('|')
                  .Append(g.x.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(g.y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(g.w.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(g.h.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(g.action).Append('|').Append(g.text).Append('|')
                  .Append(g.once ? "1" : "0").Append('|').Append(g.enabled ? "1" : "0");
            }
            SRApi.Ctl.SetValue(_list, sb.ToString());
            SRApi.Ctl.MarkConfigDirty(_list.ConfigFile);
        } catch (Exception __ex) { SRApi.Guard.Log("触发器 保存", __ex); }
    }

    // ---------------- 运行时 ----------------
    private static void ResetRuntime() {
        _inside.Clear();
        for (int i = 0; i < _items.Count; i++) _items[i].fired = false;
    }

    private static void Tick() {
        try {
            Load();
            if (_on != null && !_on.Value) { ResetRuntime(); return; }
            if (!SRApi.GateMaster || !SRApi.CanControlLocally) return;
            if (_items.Count == 0) return;
            if (Time.unscaledTime - _lastTick < 0.15f) return;
            _lastTick = Time.unscaledTime;
            Character[] chars = SRApi.FrameObjects<Character>();
            if (chars == null || chars.Length == 0) return;
            for (int i = 0; i < _items.Count; i++) {
                Tg g = _items[i];
                if (!g.enabled || (g.once && g.fired)) continue;
                for (int c = 0; c < chars.Length; c++) {
                    Character ch = chars[c];
                    if (ch == null) continue;
                    Vector3 pos = ch.transform.position;
                    bool inside = pos.x >= g.x && pos.x <= g.x + g.w && pos.y >= g.y && pos.y <= g.y + g.h;
                    int key = i * 100 + (ch.networkNumber & 0x3F);
                    if (inside) {
                        if (_inside.Add(key)) Fire(g);
                    } else {
                        _inside.Remove(key);
                    }
                }
            }
        } catch (Exception __ex) { SRApi.Guard.Log("触发器 Tick", __ex); }
    }

    private static void Fire(Tg g) {
        try {
            if (g.once) { g.fired = true; Save(); }
            if (g.action == 1) {
                // 清掉区域内玩家放置的方块（房主权威）。
                int n = 0;
                Placeable[] all = SRApi.FrameObjects<Placeable>();
                if (all != null) {
                    for (int i = 0; i < all.Length; i++) {
                        Placeable p = all[i];
                        if (p == null || p.MarkedForDestruction) continue;
                        Vector3 v = p.transform.position;
                        if (v.x < g.x || v.x > g.x + g.w || v.y < g.y || v.y > g.y + g.h) continue;
                        try { p.DestroySelf(); p.OnDestroy(); n++; } catch { }
                    }
                }
                SRApi.Notify((string.IsNullOrEmpty(g.text) ? "触发器" : g.text) + "：" + SRApi.T("清理方块 ", "cleared ") + n);
            } else {
                SRApi.Notify(string.IsNullOrEmpty(g.text) ? g.name : g.text);
            }
        } catch (Exception __ex) { SRApi.Guard.Log("触发器 执行", __ex); }
    }

    private static Vector2 CursorWorld() {
        try {
            Camera cam = Camera.main;
            if (cam == null) return Vector2.zero;
            Vector3 m = Input.mousePosition;
            m.z = 0f;
            Vector3 w = cam.ScreenToWorldPoint(m);
            return new Vector2(w.x, w.y);
        } catch { return Vector2.zero; }
    }

    // ---------------- 界面 ----------------
    private static void Render() {
        try {
            Load();
            SRApi.Ctl.SectionTitle(SRApi.T("- 触发器 -", "- Triggers -"));
            if (_on != null) {
                bool old = GUI.enabled;
                GUI.enabled = true;
                SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("启用触发器", "Enable triggers"),
                    SRApi.T("触发器总开关\n关闭后不再判定玩家进入区域\n已保存的触发器不会丢失。", "master switch for triggers\nwhen off, region entry is no longer evaluated\nsaved triggers are kept")), _on, SRApi.Ctl.Sc(130), SRApi.Ctl.Sc(26), null);
                SRApi.Ctl.RenderControl(_on);
                GUI.enabled = old;
            }
            GUILayout.Space(SRApi.Ctl.Sc(4));

            // 取点：用鼠标在世界里的位置
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(SRApi.T("取点 A", "Point A"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(90)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
                _pointA = CursorWorld(); _hasA = true;
            }
            GUILayout.Label(_hasA ? _pointA.x.ToString("0.0") + ", " + _pointA.y.ToString("0.0") : "-", SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(110)));
            if (GUILayout.Button(SRApi.T("取点 B", "Point B"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(90)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
                _pointB = CursorWorld(); _hasB = true;
            }
            GUILayout.Label(_hasB ? _pointB.x.ToString("0.0") + ", " + _pointB.y.ToString("0.0") : "-", SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(110)));
            GUILayout.Space(SRApi.Ctl.Sc(8));
            if (GUILayout.Button(_pendingAction == 0 ? SRApi.T("动作：提示", "Action: Notify") : SRApi.T("动作：清方块", "Action: Clear"),
                    SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(130)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
                _pendingAction = _pendingAction == 0 ? 1 : 0;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(SRApi.T("新建触发器", "Add trigger"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(120)), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
                if (!_hasA || !_hasB) SRApi.Notify(SRApi.T("请先取两个角点", "Pick both corners first"));
                else {
                    Tg g = new Tg();
                    g.name = "T" + (_items.Count + 1);
                    g.x = Mathf.Min(_pointA.x, _pointB.x);
                    g.y = Mathf.Min(_pointA.y, _pointB.y);
                    g.w = Mathf.Abs(_pointA.x - _pointB.x);
                    g.h = Mathf.Abs(_pointA.y - _pointB.y);
                    g.action = _pendingAction;
                    g.text = g.name;
                    _items.Add(g);
                    Save();
                }
            }
            GUILayout.Label(SRApi.T("每个触发器只对「玩家进入」触发一次，离开后再进会重新触发。", "Each trigger fires on entry; re-entering fires again."), SRApi.Ctl.LabelWrap);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(SRApi.Ctl.Sc(6));
            for (int i = 0; i < _items.Count; i++) {
                Tg g = _items[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label("#" + (i + 1) + " " + g.name, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(90)));
                GUILayout.Label("(" + g.x.ToString("0.0") + "," + g.y.ToString("0.0") + ") " + g.w.ToString("0.0") + "×" + g.h.ToString("0.0"),
                    SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(190)));
                GUILayout.Label(g.action == 1 ? SRApi.T("清方块", "Clear") : SRApi.T("提示", "Notify"), SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(70)));
                if (GUILayout.Button(g.enabled ? SRApi.T("已启用", "On") : SRApi.T("已停用", "Off"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(80)), GUILayout.Height(SRApi.Ctl.Sc(22)))) {
                    g.enabled = !g.enabled; Save();
                }
                if (GUILayout.Button(SRApi.T("只触发一次：" + (g.once ? "是" : "否"), "Once: " + (g.once ? "yes" : "no")), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(140)), GUILayout.Height(SRApi.Ctl.Sc(22)))) {
                    g.once = !g.once; Save();
                }
                if (GUILayout.Button(SRApi.T("删除", "Delete"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(70)), GUILayout.Height(SRApi.Ctl.Sc(22)))) {
                    _items.RemoveAt(i); Save(); break;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            if (_items.Count == 0) GUILayout.Label(SRApi.T("（还没有触发器：取两个角点后点「新建触发器」）", "(no triggers yet: pick two corners, then Add trigger)"), SRApi.Ctl.LabelWrap);
        } catch (Exception __ex) { SRApi.Guard.Log("触发器 界面", __ex); }
    }
}

}
