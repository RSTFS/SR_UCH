// 色觉与可读性适配：把「只靠颜色区分」和「一闪而过」两处自己造出来的门槛拆掉。
//
// 为什么要独立成一个功能（而不是散在各页里各加一个开关）：
//   品质光芒六档全靠颜色、局内简报与通知是小字、房客提案 20 秒 / 提示 2 秒 ——
//   这些门槛分散在四个功能里，各自加开关会让用户开一次房要点七八下，还会互相不一致。
//   收敛成一个「可读性」页 + 四个 BCL 委托槽位，消费方软依赖读取，删掉本 DLL 只是回到默认值。
//
// 四条适配：
//   1) 形状/纹理：品质光晕改用 6 种不同图案（虚线环/单环/双环/斜纹/棋盘/放射），
//      色觉辨识困难的人靠形状也能分档；图例同时给文字等级名。
//   2) 文字等级标签：不依赖字体里的特殊符号，纯中英文本。
//   3) 字号与对比：局内简报 / 通知浮层的字号倍率、高对比配色。
//   4) 倒计时可延长：提案等待、通知停留时间统一加秒，反应慢的人不再永远点不到。
//
// 刻意不碰：Harmony 补丁、反射登记、msgType、相机、线程、网络、游戏字体资源。
//   跨 DLL 通信只走契约层 Slots（名字 + BCL 委托），本 DLL 不引用任何其它功能的类型。

using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.a11y", "可读性", "1.0", Provides = new string[] {
        "sr.a11y.fontScale", "sr.a11y.highContrast", "sr.a11y.shapeLabels", "sr.a11y.extraSeconds" })]
    public class A11y : ITweak {
        private const string Sec = "可读性";

        // 槽位名：形状全部是 BCL 委托，跨 DLL 不需要共享类型。
        public const string SFont = "sr.a11y.fontScale";        // Func<float> 字号倍率，默认 1
        public const string SContrast = "sr.a11y.highContrast";  // Func<bool> 高对比
        public const string SShape = "sr.a11y.shapeLabels";      // Func<bool> 用形状代替纯色
        public const string SExtra = "sr.a11y.extraSeconds";     // Func<float> 一次性倒计时加秒

        private static readonly string[] TierZh = { "泛滥", "普通", "精良", "稀有", "史诗", "传说" };
        private static readonly string[] TierEn = { "Flood", "Common", "Fine", "Rare", "Epic", "Legendary" };
        private static readonly Color[] TierColor = {
            new Color(0.60f, 0.64f, 0.70f),
            new Color(0.91f, 0.94f, 0.98f),
            new Color(0.42f, 0.88f, 0.54f),
            new Color(0.36f, 0.72f, 1.00f),
            new Color(0.71f, 0.48f, 1.00f),
            new Color(1.00f, 0.83f, 0.36f),
        };

        private static ConfigEntry<bool> _enabled, _highContrast, _shapeLabels;
        private static ConfigEntry<int> _fontScale, _extraSeconds;

        private static bool _uiReg;

        // 配置行统一走"标签 + 控件"：RenderControl 只画控件、没有名称列，
        // 那样 LocKey / LocDesc 登记不会被消费，界面上也没有任何配置项名称。
        // 列宽与核心 Sc() 同坐标系；契约层未暴露 EntryNameWidth，故用固定 180 设计像素。
        private static void CfgRow(ConfigEntryBase e) {
            SRApi.Ctl.DrawEntryRow(e, SRApi.Ctl.Sc(180f));
        }

        // 本页配置行的显示名与悬浮说明：缺 LocKey 时中文界面回退英文键名，
        // 缺 LocDesc 时英文界面完全没有提示（内部条目不会回退 Bind 的中文描述）。
        private static void RegUi() {
            SRApi.LocKey(Sec, "Enabled", "启用可读性适配", "Enable readability");
            SRApi.LocDesc(Sec, "Enabled",
                "启用可读性适配\n统一控制字号、对比、品质形状与倒计时加长；关掉后所有功能回到各自默认值。",
                "Enable readability\nMaster switch for font scale, contrast, tier shapes and countdown extension.");
            SRApi.LocKey(Sec, "FontScale", "字号百分比", "Font scale");
            SRApi.LocDesc(Sec, "FontScale",
                "字号百分比\n局内简报与通知浮层的字号百分比（80-160）。",
                "Font scale\nFont size of the match brief and notification overlays in percent. Range 80-160.");
            SRApi.LocKey(Sec, "HighContrast", "高对比", "High contrast");
            SRApi.LocDesc(Sec, "HighContrast",
                "高对比\n高对比：加深浮层底色、提亮文字、给图例色块加描边。",
                "High contrast\nDarker overlay background, brighter text, outlined legend swatches.");
            SRApi.LocKey(Sec, "ShapeLabels", "品质形状与等级名", "Tier shapes & labels");
            SRApi.LocDesc(Sec, "ShapeLabels",
                "品质形状与等级名\n品质用 6 种形状图案 + 文字等级名，而不是只靠颜色区分。",
                "Tier shapes & labels\nUse six shape patterns plus tier names instead of colour alone.");
            SRApi.LocKey(Sec, "ExtraSeconds", "倒计时加秒", "Extra countdown seconds");
            SRApi.LocDesc(Sec, "ExtraSeconds",
                "倒计时加秒\n给一次性倒计时额外加秒（0-20）：房客提案、通知停留都变长。",
                "Extra countdown seconds\nExtend one-shot countdowns (guest proposals, notifications). Range 0-20.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "FontScale": min = 80.0f; max = 160.0f; return true;
                    case "ExtraSeconds": min = 0.0f; max = 20.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", true, "统一控制字号、对比、品质形状与倒计时加长；关掉后所有功能回到各自默认值。");
            _fontScale = plugin.Config.Bind(Sec, "FontScale", 100,
                new ConfigDescription("局内简报与通知浮层的字号百分比（80-160）。", new AcceptableValueRange<int>(80, 160)));
            _highContrast = plugin.Config.Bind(Sec, "HighContrast", false,
                "高对比：加深浮层底色、提亮文字、给图例色块加描边。");
            _shapeLabels = plugin.Config.Bind(Sec, "ShapeLabels", true,
                "品质用 6 种形状图案 + 文字等级名，而不是只靠颜色区分。");
            _extraSeconds = plugin.Config.Bind(Sec, "ExtraSeconds", 0,
                new ConfigDescription("给一次性倒计时额外加秒（0-20）：房客提案、通知停留都变长。", new AcceptableValueRange<int>(0, 20)));

            SRApi.LocSec(Sec, "可读性", "Readability");

            RegUi();
            SRApi.Nav(Sec, 112);
            SRApi.RegisterPage(Sec, "Readability", "可读性", "Readability", 10, RenderPage);

            Slots.Provide(SFont, (Func<float>)(() => FontScale()));
            Slots.Provide(SContrast, (Func<bool>)(() => HighContrast()));
            Slots.Provide(SShape, (Func<bool>)(() => ShapeLabels()));
            Slots.Provide(SExtra, (Func<float>)(() => ExtraSeconds()));
        }

        // 四个 getter 都判空：Initialize 之前的极早期调用（例如别的 DLL 抢跑）不会抛 NRE。
        public static float FontScale() {
            if (_enabled == null || !_enabled.Value || _fontScale == null) return 1f;
            return Mathf.Clamp(_fontScale.Value / 100f, 0.8f, 1.6f);
        }
        public static bool HighContrast() {
            return _enabled != null && _enabled.Value && _highContrast != null && _highContrast.Value;
        }
        public static bool ShapeLabels() {
            return _enabled != null && _enabled.Value && _shapeLabels != null && _shapeLabels.Value;
        }
        public static float ExtraSeconds() {
            if (_enabled == null || !_enabled.Value || _extraSeconds == null) return 0f;
            return Mathf.Clamp(_extraSeconds.Value, 0f, 20f);
        }

        // ============ 页面 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            CfgRow(_enabled);
            CfgRow(_fontScale);
            CfgRow(_highContrast);
            CfgRow(_shapeLabels);
            CfgRow(_extraSeconds);

            Line("四项都是软适配：本功能删掉后，其它功能只是回到原来的默认值，不会报错也不会少一行。",
                 "All four are soft adapters: removing this feature just restores defaults elsewhere; nothing breaks.");
            Line("形状图案由程序生成的贴图绘制，不读取游戏字体资源，也不改道具的原材质。",
                 "Shape patterns are generated textures; no game font assets and no edits to the original materials.");

            GUILayout.Space(SRApi.Ctl.Sc(8f));
            Line("品质图例预览（每行：等级名 + 颜色 + 形状）：",
                 "Rarity legend preview (per row: tier name + color + shape):");
            for (int t = 5; t >= 0; t--) {
                string name = SRApi.T(TierZh[t], TierEn[t]);
                // 文字等级标签：即使完全分不清颜色，等级名本身也可读。
                GUILayout.Label(SRApi.Ctl.Content(name + "  [" + (t + 1) + "/6]", name + "  [" + (t + 1) + "/6]"),
                                SRApi.Ctl.Label);
                if (Event.current == null || Event.current.type != EventType.Repaint) continue;
                Rect last = GUILayoutUtility.GetLastRect();
                float s = SRApi.Ctl.Sc(1f);
                float bx = last.xMax + 8f * s, by = last.y + (last.height - 14f * s) * 0.5f;
                Color c = TierColor[t];
                // 色块 + 高对比描边
                if (HighContrast()) {
                    SRApi.DrawFill(new Rect(bx - 2f * s, by - 2f * s, 22f * s + 4f * s, 14f * s + 4f * s),
                                   new Color(0.02f, 0.02f, 0.02f, 1f));
                }
                SRApi.DrawFill(new Rect(bx, by, 22f * s, 14f * s), new Color(c.r, c.g, c.b, 0.95f));
                DrawShapeMark(new Rect(bx + 30f * s, by, 30f * s, 14f * s), t, c);
            }
            Line("形状依次是：虚点环 / 单环 / 双环 / 斜纹 / 棋盘 / 放射。关闭「形状图案」时六档仍用同一圈柔光，只靠颜色与文字区分。",
                 "Shapes: dotted ring / single ring / double ring / diagonal stripes / checker / rays. With shapes off, all tiers share one soft glow.");
            Line("倒计时加长同时作用于房客提案的等待时间与通知浮层的停留时间，不改变任何游戏规则。",
                 "Extra seconds apply to both the proposal window and toast duration; no game rule is touched.");
        }

        /// <summary>图例里的形状小标记：只用 DrawFill 的小矩形拼，不依赖字体符号。</summary>
        private static void DrawShapeMark(Rect r, int tier, Color c) {
            float u = r.width / 6f;
            Color on = new Color(c.r, c.g, c.b, 1f);
            for (int i = 0; i < 6; i++) {
                bool show;
                switch (tier) {
                    case 0: show = (i % 2 == 0); break;                       // 虚点
                    case 1: show = true; break;                               // 单环=整条
                    case 2: show = (i == 0 || i == 1 || i == 4 || i == 5); break; // 双环=两端
                    case 3: show = (i % 2 == 1); break;                       // 斜纹
                    case 4: show = (i % 4 < 2); break;                        // 棋盘
                    default: show = (i == 0 || i == 2 || i == 3 || i == 5); break; // 放射
                }
                if (!show) continue;
                float h = (tier == 5 && (i == 2 || i == 3)) ? r.height : r.height * 0.55f;
                SRApi.DrawFill(new Rect(r.x + i * u + u * 0.12f, r.y + (r.height - h) * 0.5f, u * 0.76f, h), on);
            }
        }
    }
}
