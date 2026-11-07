// 自由模式 · 其它 → 地图网格：在世界空间画一张一直可见的网格（早期 EX 有、后来被删掉的功能）。
// 游戏本体没有网格类，所以这里自己画：相机后处理阶段用 GL.LINES 画线，按距离淡出。

using System;
using BepInEx.Configuration;
using SR_UCH.Tweaks;
using UnityEngine;

namespace SR_UCH {
    [SrFeature("sr.freeplay.grid", "FreePlayGrid", "1.0")]
    public class FreePlayGrid : ITweak {
        private const string Sec = "自由模式";
        private static ConfigEntry<bool> _alwaysOn;
        private static ConfigEntry<float> _maxAlpha;
        private static ConfigEntry<float> _fadeTime;
        private static ConfigEntry<Color> _color;
        private static SRGridRenderer _renderer;

        public void Initialize(IFeatureHost plugin) {
            try {
                SRApi.LocKey(Sec, "Grid Always On", "常显网格", "Always show grid");
                SRApi.LocDesc(Sec, "Grid Always On",
                    "常显网格\n自由模式下一直显示地面网格（便于对齐摆放）\n仅自由模式生效",
                    "Always show grid\nalways draws a ground grid in freeplay\nfreeplay only");
                SRApi.LocKey(Sec, "Grid Max Alpha", "网格亮度", "Grid max alpha");
                SRApi.LocDesc(Sec, "Grid Max Alpha", "网格亮度\n最近处的格线不透明度\n调低更不挡视线", "Grid max alpha\nopacity of the nearest grid lines\nlower keeps the view clear");
                SRApi.LocKey(Sec, "Grid Fade Time", "网格淡出距离", "Grid fade distance");
                SRApi.LocDesc(Sec, "Grid Fade Time", "网格淡出距离\n离相机越远越淡，到该距离归零\n单位：米", "Grid fade distance\nlines fade out with distance from the camera\nunit: metres");
                SRApi.LocKey(Sec, "Grid Refresh Color", "网格颜色", "Grid colour");
                SRApi.LocDesc(Sec, "Grid Refresh Color", "网格颜色\n格线颜色（含透明度）\n建议用低透明度避免挡视线", "Grid colour\nline colour including alpha\nkeep the alpha low so it does not hide the level");

                _alwaysOn = plugin.Config.Bind(Sec, "Grid Always On", false, "自由模式下一直显示地面网格。");
                _maxAlpha = plugin.Config.Bind(Sec, "Grid Max Alpha", 0.35f, "最近处的格线不透明度（0-1）。");
                _fadeTime = plugin.Config.Bind(Sec, "Grid Fade Time", 30f, "格线淡出距离（米）：离相机越远越淡。");
                _color = plugin.Config.Bind(Sec, "Grid Refresh Color", new Color(1f, 1f, 1f, 0.5f), "格线颜色。");
                SRApi.RegisterTick("自由模式-地图网格", Tick);
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/初始化", __ex); }
        }

        // 每帧：只在"自由模式 + 总开关 + 本开关"都成立时挂渲染器，否则卸掉（零开销）
        private static void Tick() {
            try {
                bool want = SRApi.GateMaster && _alwaysOn != null && _alwaysOn.Value
                    && SRApi.GateModeAllows(SRApi.ModeMask.Freeplay);
                if (want) {
                    if (_renderer == null) _renderer = SRGridRenderer.Attach();
                    if (_renderer != null) {
                        _renderer.enabled = true;
                        _renderer.Alpha = Mathf.Clamp01(_maxAlpha != null ? _maxAlpha.Value : 0.35f);
                        _renderer.Fade = Mathf.Max(2f, _fadeTime != null ? _fadeTime.Value : 30f);
                        _renderer.LineColor = _color != null ? _color.Value : new Color(1f, 1f, 1f, 0.5f);
                    }
                } else if (_renderer != null) {
                    _renderer.enabled = false;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/每帧", __ex); }
        }

        // 自由模式页「其它」子页：只渲染这几行设置。
        internal static void RenderOtherPage() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 地图网格 -", "- Map grid -"));
                SRApi.Ctl.LabeledRow(SRApi.T("常显网格", "Always show grid"),
                    SRApi.T("自由模式下一直显示地面网格\n仅自由模式生效", "always draw a ground grid in freeplay\nfreeplay only"), _alwaysOn);
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/渲染", __ex); }
        }
    }

    // 挂在主相机上：OnPostRender 里用 GL 画世界空间格线（内置渲染管线，UCH 就是内置管线）。
    public class SRGridRenderer : MonoBehaviour {
        public float Alpha = 0.35f;
        public float Fade = 30f;
        public Color LineColor = Color.white;
        private Material _mat;
        private static SRGridRenderer _inst;

        internal static SRGridRenderer Attach() {
            try {
                if (_inst != null) return _inst;
                Camera cam = Camera.main;
                if (cam == null) return null;
                SRGridRenderer r = cam.gameObject.GetComponent<SRGridRenderer>();
                if (r == null) r = cam.gameObject.AddComponent<SRGridRenderer>();
                _inst = r;
                r.enabled = false;
                return r;
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/挂载", __ex); return null; }
        }

        private void OnPostRender() {
            try {
                if (_mat == null) {
                    Shader sh = Shader.Find("Hidden/Internal-Colored");
                    if (sh == null) return;
                    _mat = new Material(sh);
                    _mat.hideFlags = HideFlags.HideAndDontSave;
                    _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                    _mat.SetInt("_ZWrite", 0);
                }
                Camera cam = Camera.main;
                if (cam == null) return;
                Vector3 center = cam.transform.position;
                float radius = Mathf.Max(8f, Fade);
                float step = 1f;
                _mat.SetPass(0);
                GL.PushMatrix();
                GL.Begin(GL.LINES);
                int n = Mathf.CeilToInt(radius / step);
                for (int i = -n; i <= n; i++) {
                    float off = i * step;
                    for (int seg = -n; seg < n; seg++) {
                        float a0 = seg * step, a1 = (seg + 1) * step;
                        // 两条正交方向的线段，按距相机的水平距离淡出
                        AddLine(center, off, a0, off, a1, radius);
                        AddLine(center, a0, off, a1, off, radius);
                    }
                }
                GL.End();
                GL.PopMatrix();
            } catch (Exception __ex) { SRApi.Guard.Log("地图网格/绘制", __ex); }
        }

        private void AddLine(Vector3 c, float x0, float z0, float x1, float z1, float radius) {
            Vector3 p0 = new Vector3(c.x + x0, 0.02f, c.z + z0);
            Vector3 p1 = new Vector3(c.x + x1, 0.02f, c.z + z1);
            float d = Vector3.Distance(new Vector3(c.x, 0f, c.z), new Vector3(p0.x, 0f, p0.z));
            float a = Mathf.Clamp01(1f - d / radius) * Alpha;
            if (a <= 0.004f) return;
            Color col = LineColor;
            col.a = Alpha * a;
            GL.Color(col);
            GL.Vertex3(p0.x, p0.y, p0.z);
            GL.Vertex3(p1.x, p1.y, p1.z);
        }
    }
}
