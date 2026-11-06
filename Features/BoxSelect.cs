// 框选：按住键在画面里拉框选中多个方块，高亮显示，Delete 整批删除（仅房主）。
// 这是 BuildingPlus 多选功能的 P1 安全子集：只做"选中 + 删除"，不做移动/复制，避免它的已知缺陷。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.boxselect", "BoxSelect", "1.0")]
    public class BoxSelect : ITweak {
        private const string Sec = "build";
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _selectKey;
        private static ConfigEntry<KeyCode> _deleteKey;
        private static ConfigEntry<KeyCode> _appendKey;
        private static ConfigEntry<bool> _sameType;
        private static float _armedAt = -10f;   // 删除确认：第一次按下记时间，2 秒内再按才真删
        private static readonly List<Placeable> _selected = new List<Placeable>();
        private static bool _dragging;
        private static Vector2 _dragStart;
        private static bool _dragMoved;
        private static GameObject _overlay;
        private static string _hint = "";        // 屏幕提示（选中数量等）

        private static void SelfReg() {
            SRApi.LocKey(Sec, "Box Select", "框选", null);
            SRApi.LocDesc(Sec, "Box Select", "框选\n按住下面的键并左键拖框选中多个方块\n选中后按删除键整批删除（仅房主）", "Box select\nhold the key below and drag with the left mouse to select several pieces\npress the delete key to remove them all (host only)");
            SRApi.LocKey(Sec, "Box Select Key", "框选键", null);
            SRApi.LocDesc(Sec, "Box Select Key", "框选键\n按住它再左键拖框\n松开后保留选中，右键或 Esc 取消", "Box select key\nhold it and drag a rectangle with the left mouse\nreleasing keeps the selection; right click or Esc clears it");
            SRApi.LocKey(Sec, "Box Append Key", "追加选择键", null);
            SRApi.LocDesc(Sec, "Box Append Key", "追加选择键\n按住它再框选＝新选中的并入现有选择\n不按＝每次框选都重新开始", "Append key\nhold it while dragging to add to the current selection\nwithout it each drag starts a fresh selection");
            SRApi.LocKey(Sec, "Box Same Type", "只选同类", null);
            SRApi.LocDesc(Sec, "Box Same Type", "只选同类\n开启后框选只保留与第一个命中方块同名的\n便于批量处理同一种方块", "Same type only\nkeeps only pieces whose name matches the first hit\nhandy for batch work on one kind of piece");
            SRApi.LocKey(Sec, "Box Delete Key", "删除选中键", null);
            SRApi.LocDesc(Sec, "Box Delete Key", "删除选中键\n把当前选中的方块全部删除\n仅房主生效；删除走游戏自己的同步", "Delete selection key\nremoves every selected piece\nhost only; deletion uses the game's own sync");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Box Select", false, "框选：按住框选键拉框选中多个方块，配合删除键批量删除。");
                _selectKey = plugin.Config.Bind(Sec, "Box Select Key", KeyCode.LeftControl, "框选键（按住 + 左键拖框）。");
                _deleteKey = plugin.Config.Bind(Sec, "Box Delete Key", KeyCode.Delete, "删除选中键（连按两次确认，仅房主）。");
                _appendKey = plugin.Config.Bind(Sec, "Box Append Key", KeyCode.LeftShift, "追加选择键（按住框选＝并入现有选择）。");
                _sameType = plugin.Config.Bind(Sec, "Box Same Type", false, "只选同类：框选只保留与第一个命中同名的方块。");
                SRApi.RegisterKey("框选-框选键", _selectKey, "hold");
                SRApi.RegisterKey("框选-删除键", _deleteKey, "press");
                SRApi.RegisterTick("KEY_BOXSELECT", Tick);
                SRApi.RegisterSceneHook(delegate { Clear(); });
                if (_overlay == null) {
                    _overlay = new GameObject("SR_UCHBoxSelect");
                    UnityEngine.Object.DontDestroyOnLoad(_overlay);
                    _overlay.AddComponent<Overlay>();
                }
            } catch (Exception __ex) { SRApi.Guard.Log("框选/初始化", __ex); }
        }

        private static bool Active {
            get {
                try {
                    if (_enabled == null || !_enabled.Value || !SRApi.GateMaster) return false;
                    if (SRApi.UiOpen) return false;                    // 面板打开时不抢鼠标
                    LobbyManager lm = LobbyManager.instance;
                    if (lm == null || lm.CurrentGameController == null) return false;
                    return true;
                } catch (Exception __ex) { SRApi.Guard.Log("框选/状态判定", __ex); return false; }
            }
        }

        private static void Tick() {
            try {
                if (!Active) { if (_dragging) EndDrag(false); return; }
                // 删除选中
                if (_selected.Count > 0 && _deleteKey != null && _deleteKey.Value != KeyCode.None && Input.GetKeyDown(_deleteKey.Value)) {
                    // 二次确认：第一次只提示，2 秒内再按一次才真删，避免误按 Delete 清掉一片
                    if (Time.unscaledTime - _armedAt <= 2f) { _armedAt = -10f; DeleteSelected(); return; }
                    _armedAt = Time.unscaledTime;
                    _hint = SRApi.T("再按一次删除键确认删除 ", "press delete again to remove ") + _selected.Count;
                    SRApi.Notify(SRApi.T("再按一次删除键确认", "Press delete again to confirm"));
                    return;
                }
                // 取消：右键 / Esc
                if (_selected.Count > 0 && (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))) { Clear(); return; }

                bool keyHeld = _selectKey != null && _selectKey.Value != KeyCode.None && Input.GetKey(_selectKey.Value);
                if (keyHeld && Input.GetMouseButtonDown(0)) {
                    _dragging = true; _dragStart = Input.mousePosition; _dragMoved = false;
                }
                if (_dragging) {
                    Vector2 now = Input.mousePosition;
                    if ((now - _dragStart).sqrMagnitude > 16f) _dragMoved = true;
                    if (!Input.GetMouseButton(0)) EndDrag(true);
                }
                ApplyHighlight();
            } catch (Exception __ex) { SRApi.Guard.Log("框选/轮询", __ex); }
        }

        private static void EndDrag(bool commit) {
            try {
                Vector2 end = Input.mousePosition;
                _dragging = false;
                if (!commit || !_dragMoved) return;
                bool append = _appendKey != null && _appendKey.Value != KeyCode.None && Input.GetKey(_appendKey.Value);
                if (!append) _selected.Clear();
                string firstType = null;
                if (_sameType != null && _sameType.Value && _selected.Count > 0) firstType = _selected[0] != null ? _selected[0].Name : null;
                Rect r = ScreenRect(_dragStart, end);
                List<Placeable> hits = new List<Placeable>();
                foreach (Placeable p in Placeable.AllPlaceables) {
                    if (!Eligible(p)) continue;
                    Vector3 sp = Camera.main != null ? Camera.main.WorldToScreenPoint(p.transform.position) : Vector3.zero;
                    if (sp.z < 0f) continue;
                    if (r.Contains(new Vector2(sp.x, sp.y))) hits.Add(p);
                }
                // 只选同类：以第一个命中为准，其余同名才保留
                if (_sameType != null && _sameType.Value && hits.Count > 0) {
                    string type = firstType ?? hits[0].Name;
                    for (int i = 0; i < hits.Count; i++) {
                        if (hits[i] != null && hits[i].Name == type && !_selected.Contains(hits[i])) _selected.Add(hits[i]);
                    }
                } else {
                    for (int i = 0; i < hits.Count; i++) if (!_selected.Contains(hits[i])) _selected.Add(hits[i]);
                }
                _hint = _selected.Count > 0 ? SRApi.T("已选 ", "selected ") + _selected.Count : "";
                if (_selected.Count > 0) SRApi.LogInfo("[框选] 已选中 " + _selected.Count + " 个方块");
            } catch (Exception __ex) { SRApi.Guard.Log("框选/结束拖动", __ex); }
        }

        private static Rect ScreenRect(Vector2 a, Vector2 b) {
            float x = Mathf.Min(a.x, b.x), y = Mathf.Min(a.y, b.y);
            return new Rect(x, y, Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        private static bool Eligible(Placeable p) {
            try {
                if (p == null || p.MarkedForDestruction || !p.gameObject.activeSelf) return false;
                if (p.isSetPiece) return false;
                string n = p.Name ?? "";
                if (n.Contains("SetPiece") || n.Contains("Goal Block") || n.Contains("Start Plank")) return false;
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("框选/过滤方块", __ex); return false; }
        }

        // 高亮：从选中集合每帧重建（幂等），不做增量 add/remove —— 这正是上游"高亮重复"的根源。
        private static void ApplyHighlight() {
            for (int i = 0; i < _selected.Count; i++) {
                Placeable p = _selected[i];
                if (p == null) continue;
                try { p.AddBombTint(new Color(1f, 0.35f, 0.35f, 1f)); } catch (Exception __t) { SRApi.Guard.Log("框选/高亮", __t); }
            }
        }

        private static void Clear() {
            try {
                for (int i = 0; i < _selected.Count; i++) {
                    Placeable p = _selected[i];
                    if (p == null) continue;
                    try { p.RemoveBombTint(); } catch (Exception __t) { SRApi.Guard.Log("框选/清除高亮", __t); }
                }
                _selected.Clear();
                _hint = "";
            } catch (Exception __ex) { SRApi.Guard.Log("框选/清空", __ex); }
        }

        private static void DeleteSelected() {
            try {
                if (!SRApi.GateHost) { SRApi.Notify(SRApi.T("只有房主能批量删除方块", "Host only")); return; }
                int n = 0;
                for (int i = 0; i < _selected.Count; i++) {
                    Placeable p = _selected[i];
                    if (!Eligible(p)) continue;
                    // DestroySelf 自带网络同步（MsgPieceDestroyed），不要再显式广播。
                    p.DestroySelf(false, true, true);
                    n++;
                }
                Clear();
                SRApi.Notify(SRApi.T("已删除 ", "deleted ") + n + SRApi.T(" 个方块", " piece(s)"));
            } catch (Exception __ex) { SRApi.Guard.Log("框选/批量删除", __ex); }
        }

        // 屏幕提示 + 拖动框（自绘，不占面板）
        private sealed class Overlay : MonoBehaviour {
            private void OnGUI() {
                try {
                    if (!Active) return;
                    if (_dragging && _dragMoved) {
                        Rect r = ScreenRect(_dragStart, Input.mousePosition);
                        Rect gui = new Rect(r.x, Screen.height - r.yMax, r.width, r.height);
                        SRApi.DrawFill(gui, new Color(0.2f, 0.8f, 1f, 0.15f));
                        SRApi.DrawFill(new Rect(gui.x, gui.y, gui.width, 1f), new Color(0.2f, 0.8f, 1f, 0.8f));
                        SRApi.DrawFill(new Rect(gui.x, gui.yMax - 1f, gui.width, 1f), new Color(0.2f, 0.8f, 1f, 0.8f));
                        SRApi.DrawFill(new Rect(gui.x, gui.y, 1f, gui.height), new Color(0.2f, 0.8f, 1f, 0.8f));
                        SRApi.DrawFill(new Rect(gui.xMax - 1f, gui.y, 1f, gui.height), new Color(0.2f, 0.8f, 1f, 0.8f));
                    }
                    if (!string.IsNullOrEmpty(_hint)) {
                        var style = new GUIStyle(GUI.skin.label);
                        style.fontSize = 16;
                        GUI.Label(new Rect(12f, Screen.height - 40f, 300f, 24f), _hint, style);
                    }
                } catch (Exception __ex) { SRApi.Guard.Log("框选/绘制", __ex); }
            }
        }
    }
}
