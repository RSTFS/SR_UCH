// 样式：深色主题的 GUIStyle / 字体 / 纹理 / 滑块素材；Sc() 为统一缩放系数入口。
using System;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Styles（深色主题 GUIStyle / 字体 / 纹理 / 滑块素材）

        internal static Font OrigSkinFontValue { get { return _origSkinFont; } }
        private static Font _origSkinFont;                 //全局皮肤的原始字体（恢复用）
        private static bool _origSelColorSaved;
        private static Color _origSelColor;               //全局皮肤的原始选择色
        private static Texture2D Solid(Color c) {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }

        //复制自 GUI.skin 后重置边框 / 边距 / 偏移，避免游戏皮肤自带的大边距挤压并裁剪文字。
        private static void CleanStyle(GUIStyle s, RectOffset border) {
            s.border = border;
            s.margin = new RectOffset(0, 0, 0, 0);
            s.contentOffset = Vector2.zero;
            s.richText = false;
        }

        private static GUIStyle StyleBtn(Color bg, Color hover, Color active, Color text) {
            GUIStyle s = new GUIStyle(GUI.skin.button);
            CleanStyle(s, new RectOffset(2, 2, 2, 2));
            s.stretchWidth = false; //绝不随窗口宽度拉伸（下拉框/按钮宽度固定）
            s.stretchHeight = false;
            s.normal.background = Solid(bg);
            s.normal.textColor = text;
            s.hover.background = Solid(hover);
            s.hover.textColor = text;
            s.active.background = Solid(active);
            s.active.textColor = text;
            s.alignment = TextAnchor.MiddleLeft;
            s.padding = new RectOffset(8, 6, 3, 5);
            _styleList.Add(s);
            return s;
        }

        //中文字体候选名，依次尝试：CreateDynamicFontFromOSFont 在字体缺失时返回 null 而不抛异常，故须显式判空回退。
        //微软雅黑的 CJK 字形下沉到度量线下方，所有文本样式须加额外垂直内边距防裁切。
        private static readonly string[] FontCandidates = {
            "Microsoft YaHei", "微软雅黑", "Noto Sans CJK SC", "Source Han Sans SC",
            "WenQuanYi Micro Hei", "PingFang SC", "Heiti SC"
        };

        //字体创建整轮失败后的退避：记录失败字号，3 秒内不再重试（否则每帧重查 7 个候选字体）。
        private static int _fontFailSize = -1;   //上次失败的目标字号（-1 = 无记录）
        private static float _fontNextTryAt;

        //字号随 UI 缩放，保证布局高度与文字实际高度一致。
        private static void EnsureFont() {
            int fs = Mathf.RoundToInt(14f * Mathf.Clamp(_uiScaleEntry.Value, Ui.ScaleMin, Ui.ScaleMax));
            if (_font != null && _font.fontSize == fs) return; //字号未变，直接返回
            //退避期内不重试该字号
            if (_fontFailSize == fs && Time.unscaledTime < _fontNextTryAt) return;
            //先摘下旧字体再重建（重建循环以 _font == null 为条件），失败则回退旧字体。
            Font old = _font;
            _font = null;
            for (int i = 0; i < FontCandidates.Length && _font == null; i++) {
                try {
                    Font nf = Font.CreateDynamicFontFromOSFont(FontCandidates[i], fs);
                    if (nf == null) continue; //字体不存在时返回 null
                    UnityEngine.Object.DontDestroyOnLoad(nf);
                    _font = nf;
                } catch (Exception __ex) { Guard.Log("创建中文字体(" + FontCandidates[i] + ")", __ex); }
            }
            if (_font == null) {
                _font = old;                      //回退旧字体（可能为 null）
                _fontFailSize = fs;               //记录失败字号
                _fontNextTryAt = Time.unscaledTime + 3f;
            } else {
                _fontFailSize = -1;               //清除失败记录
                //GUI.skin 为全局共享皮肤（ConfigurationManager 等同样使用），改动前须记录原始值。
                try {
                    if (_origSkinFont == null) _origSkinFont = GUI.skin.font;
                    GUI.skin.font = _font;
                } catch (Exception __ex) { Guard.Log("EnsureFont", __ex); }
                //销毁旧字体，避免逐档泄漏
                if (old != null && !ReferenceEquals(old, _font)) UnityEngine.Object.Destroy(old);
            }
        }

        private static readonly Color _cBg = new Color(0.13f, 0.13f, 0.15f, 0.97f);
        private static readonly Color _cTitle = new Color(0.08f, 0.08f, 0.1f, 1f);
        private static readonly Color _cItem = new Color(0.18f, 0.19f, 0.22f, 1f);
        private static readonly Color _cItemHover = new Color(0.24f, 0.26f, 0.32f, 1f);
        private static readonly Color _cSelItem = new Color(0.26f, 0.37f, 0.56f, 1f);
        private static readonly Color _cBtnBg = new Color(0.2f, 0.21f, 0.24f, 1f);
        private static readonly Color _cBtnHover = new Color(0.3f, 0.32f, 0.38f, 1f);
        private static readonly Color _cBtnActive = new Color(0.16f, 0.17f, 0.2f, 1f);
        private static readonly Color _cFrame = new Color(0.24f, 0.25f, 0.28f, 1f);
        private static readonly Color _cFrameHover = new Color(0.3f, 0.32f, 0.36f, 1f);
        private static readonly Color _cCapture = new Color(0.5f, 0.3f, 0.12f, 1f);
        private static readonly Color _cText = new Color(0.92f, 0.92f, 0.95f, 1f);

        //保存原始选择色：必须在第一次改写之前调用。
        private static void EnsureOrigSelColor() {
            try {
                if (!_origSelColorSaved) { _origSelColor = GUI.skin.settings.selectionColor; _origSelColorSaved = true; }
            } catch (Exception __ex) { Guard.Log("保存原始选择色", __ex); }
        }
        //绘制结束后还原全局选择色（GUI.skin 为全局共享皮肤）。
        internal static void RestoreSkinSelectionColor() {
            try { if (_origSelColorSaved && GUI.skin != null) GUI.skin.settings.selectionColor = _origSelColor; } catch { }
        }

        private static void EnsureStyles() {
            //样式仍在但纹理被卸载时重建
            if (_stylesReady && _win != null && _win.normal.background == null) _stylesReady = false;
            if (_stylesReady) { SyncScaledStyles(); return; }
            _stylesReady = true;
            foreach (GUIStyle s in _styleList) {
                if (s.normal.background != null) UnityEngine.Object.Destroy(s.normal.background);
                if (s.hover.background != null) UnityEngine.Object.Destroy(s.hover.background);
                if (s.active.background != null) UnityEngine.Object.Destroy(s.active.background);
                if (s.focused.background != null) UnityEngine.Object.Destroy(s.focused.background);
            }
            _styleList.Clear();
            //这些纹理挂在 DontDestroyOnLoad 上，重建前须先销毁旧对象。
            if (_gripTex != null) { UnityEngine.Object.Destroy(_gripTex); _gripTex = null; }
            if (_cursorTex != null) { UnityEngine.Object.Destroy(_cursorTex); _cursorTex = null; }
            if (_cursorResizeTex != null) { UnityEngine.Object.Destroy(_cursorResizeTex); _cursorResizeTex = null; }
            if (_cursorMoveTex != null) { UnityEngine.Object.Destroy(_cursorMoveTex); _cursorMoveTex = null; }
            if (_checkTex != null) { UnityEngine.Object.Destroy(_checkTex); _checkTex = null; }
        //文本选中高亮改为左栏选中项的蓝色（IMGUI 默认为橙色）
        EnsureOrigSelColor();   //必须先存原始值再改写
        GUI.skin.settings.selectionColor = _cSelItem;

            Texture2D winTex = new Texture2D(2, 2);
            winTex.SetPixel(0, 0, _cBg); winTex.SetPixel(1, 0, _cBg); winTex.SetPixel(0, 1, _cBg); winTex.SetPixel(1, 1, _cBg);
            winTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(winTex);
            _win = new GUIStyle(GUI.skin.box);
            CleanStyle(_win, new RectOffset(1, 1, 1, 1));
            _win.normal.background = winTex;
            _styleList.Add(_win);

            _title = StyleBtn(_cTitle, _cTitle, _cTitle, _cText);
            _title.alignment = TextAnchor.MiddleLeft;
            _titleLabel = new GUIStyle(GUI.skin.label);
            CleanStyle(_titleLabel, new RectOffset(0, 0, 0, 0));
            _titleLabel.normal.textColor = Color.white;
            _titleLabel.fontStyle = FontStyle.Bold;
            _titleLabel.alignment = TextAnchor.MiddleLeft;
            _titleLabel.padding = new RectOffset(6, 6, 3, 5); //CJK 字形下沉余量
            _styleList.Add(_titleLabel);

            _titleMid = new GUIStyle(_titleLabel);
            _titleMid.fontStyle = FontStyle.Normal;
            _titleMid.fontSize = 0; //跟随缩放后的字体
            _titleMid.normal.textColor = new Color(0.75f, 0.78f, 0.85f, 1f);
            //左对齐：宽度收窄后居中会两端同时裁切，左对齐仅裁右端，文字开头始终可读。
            _titleMid.alignment = TextAnchor.MiddleLeft;
            _styleList.Add(_titleMid);

        EnsureStylesTexts();
        EnsureStylesSidebarItems();
        EnsureStylesButtons();
        EnsureStylesPopups();
        EnsureStylesSliderChrome();
        EnsureStylesCursors();
        SyncScaledStyles();   //记录当前缩放档并同步随缩放变化的样式
        }

        //部分样式的字号在构建时按缩放写死（见 _labelWrap），缩放变化时不重建，故在此同步。
        //只改样式字号，不重建纹理。
        private static float _styleScale = -1f;
        private static void SyncScaledStyles() {
            try {
                float s = Mathf.Clamp(_scaled, Ui.ScaleMin, Ui.ScaleMax);
                if (Mathf.Abs(_styleScale - s) < 0.0005f) return;
                _styleScale = s;
                if (_labelWrap != null) _labelWrap.fontSize = Mathf.RoundToInt(14f * Mathf.Max(1f, s));
            } catch (Exception __ex) { Guard.Log("同步随缩放样式", __ex); }
        }

        private static void EnsureStylesTexts() {
            _label = new GUIStyle(GUI.skin.label);
            CleanStyle(_label, new RectOffset(0, 0, 0, 0));
            _label.normal.textColor = _cText;
            _label.wordWrap = false; //仅在名称列 / 聊天框等显式处换行
            _label.padding = new RectOffset(6, 6, 3, 5); //CJK 字形下沉余量
            _styleList.Add(_label);

            _nameLabel = new GUIStyle(_label);
            _nameLabel.wordWrap = true; //条目名称列：换行而非裁切
            _styleList.Add(_nameLabel);

            //固定宽度单元格用：不折行，超出裁切。
            _labelClip = new GUIStyle(_label);
            _labelClip.wordWrap = false;
            _labelClip.clipping = TextClipping.Clip;
            _styleList.Add(_labelClip);

            //联机列表的表头与单元格一律居中
            _labelClipCenter = new GUIStyle(_labelClip);
            _labelClipCenter.alignment = TextAnchor.MiddleCenter;
            _styleList.Add(_labelClipCenter);

            _secHeaderCenter = new GUIStyle(_labelClip);
            _secHeaderCenter.fontStyle = FontStyle.Bold;
        //表头在格内居中，左右不需要内边距，故把继承的 6px 压到 2px。
        _secHeaderCenter.padding = new RectOffset(2, 2, 3, 5);
            _secHeaderCenter.alignment = TextAnchor.MiddleCenter;
            _secHeaderCenter.normal.textColor = new Color(0.65f, 0.78f, 1f, 1f);
            _styleList.Add(_secHeaderCenter);

            _secHeader = new GUIStyle(_label);
            _secHeader.fontStyle = FontStyle.Bold;
            _secHeader.normal.textColor = new Color(0.65f, 0.78f, 1f, 1f);
            _styleList.Add(_secHeader);

            _labelWrap = new GUIStyle(_label);
            _labelWrap.wordWrap = true; //长说明在窄窗口下换行
            _labelWrap.fontSize = Mathf.RoundToInt(14f * Mathf.Max(1f, _scaled));
            _labelWrap.padding = new RectOffset(6, 6, 6, 14); //底部留白，避免最后一行被裁
            _styleList.Add(_labelWrap);

            _chatLabel = new GUIStyle(_label);
            _chatLabel.wordWrap = true; //聊天条目换行，留足纵向空间避免 CJK 行被裁
            _chatLabel.padding = new RectOffset(6, 6, 6, 16);
            _styleList.Add(_chatLabel);

            //会话内容页显示聊天记录（可选中复制），沿用聊天标签样式且无输入框底色
            _chatArea = new GUIStyle(_chatLabel);
            _chatArea.wordWrap = true;
            _chatArea.richText = false;
            _styleList.Add(_chatArea);

        }
        private static void EnsureStylesSidebarItems() {
            _item = StyleBtn(_cItem, _cItemHover, _cItemHover, _cText);
            _item.alignment = TextAnchor.MiddleLeft;
            //外边距归零，消除相邻栏目之间的缝隙。
            _item.margin = new RectOffset(0, 0, 0, 0);
            _item.border = new RectOffset(0, 0, 0, 0); //9-slice 边框同样会留缝
            _selItem = StyleBtn(_cSelItem, _cSelItem, _cSelItem, Color.white);
            _selItem.alignment = TextAnchor.MiddleLeft;
            //几何参数与普通项一致（仅配色不同），否则选中项与普通项之间会出现细缝。
            _selItem.margin = _item.margin;
            _selItem.border = _item.border;
            _selItem.padding = _item.padding;
            _selItem.overflow = _item.overflow;
            _selItem.margin = new RectOffset(0, 0, 0, 0);
            _selItem.border = new RectOffset(0, 0, 0, 0);

        }
        private static void EnsureStylesButtons() {
            _btn = StyleBtn(_cBtnBg, _cBtnHover, _cBtnActive, _cText);
            _btn.alignment = TextAnchor.MiddleCenter;
            //窄格按钮（联机列表操作列）：内边距 2px
            _btnClip = new GUIStyle(_btn);
            _btnClip.padding = new RectOffset(2, 2, 2, 2);
            _btnClip.clipping = TextClipping.Clip;
            _styleList.Add(_btnClip);
            _frame = StyleBtn(_cFrame, _cFrameHover, _cFrame, _cText);
            _frame.alignment = TextAnchor.MiddleCenter;
            _capture = StyleBtn(_cCapture, _cCapture, _cCapture, Color.white);
            _capture.alignment = TextAnchor.MiddleCenter;
            _checkOn = StyleBtn(_cFrame, _cFrameHover, _cFrame, new Color(0.6f, 0.85f, 1f, 1f));
            _checkOn.alignment = TextAnchor.MiddleCenter;
            _checkOn.padding = new RectOffset(0, 0, 0, 0);
            _checkOff = StyleBtn(_cFrame, _cFrameHover, _cFrame, new Color(0.35f, 0.35f, 0.4f, 1f));
            _checkOff.alignment = TextAnchor.MiddleCenter;
            _checkOff.padding = new RectOffset(0, 0, 0, 0);
            //Dear ImGui 风格复选框：1px 方框 + 深色底，勾选时淡蓝填充；未勾选用浅灰边框保证可辨。
            Texture2D chkOffTex = new Texture2D(3, 3);
            Color chkOffBg = new Color(0.16f, 0.17f, 0.2f, 1f);
            Color chkOffBorder = new Color(0.45f, 0.48f, 0.55f, 0.9f);
            for (int y = 0; y < 3; y++) {
                for (int xx = 0; xx < 3; xx++) {
                    bool edge = y == 0 || y == 2 || xx == 0 || xx == 2;
                    chkOffTex.SetPixel(xx, y, edge ? chkOffBorder : chkOffBg);
                }
            }
            chkOffTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(chkOffTex);
            _checkOff.normal.background = chkOffTex;
            _checkOff.border = new RectOffset(1, 1, 1, 1);
            Texture2D chkOffHovTex = new Texture2D(3, 3);
            for (int y = 0; y < 3; y++) {
                for (int xx = 0; xx < 3; xx++) {
                    bool edge = y == 0 || y == 2 || xx == 0 || xx == 2;
                    chkOffHovTex.SetPixel(xx, y, edge ? new Color(0.6f, 0.63f, 0.7f, 0.95f) : new Color(0.24f, 0.26f, 0.3f, 1f));
                }
            }
            chkOffHovTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(chkOffHovTex);
            _checkOff.hover.background = chkOffHovTex;
            _checkOn.normal.background = Solid(new Color(0.2f, 0.32f, 0.52f, 1f)); //勾选态：淡蓝底
            _checkOn.hover.background = Solid(new Color(0.26f, 0.4f, 0.62f, 1f));

            //勾用运行时生成的贴图：字体回退到 Unity 默认字体时 U+2713 会显示为豆腐块。
            _checkTex = MakeCheckTex(24, 2.6f);

        }
        private static void EnsureStylesPopups() {
            _popup = new GUIStyle(GUI.skin.box);
            CleanStyle(_popup, new RectOffset(1, 1, 1, 1));
            //Dear ImGui popup：深色底 + 1px 边框（9-slice：边=边框色，中心=背景色）
            Texture2D popTex = new Texture2D(3, 3);
            Color popBorder = new Color(0.42f, 0.45f, 0.52f, 0.9f);
            Color popBg = new Color(0.1f, 0.1f, 0.12f, 0.98f);
            for (int y = 0; y < 3; y++) {
                for (int xx = 0; xx < 3; xx++) {
                    bool edge = y == 0 || y == 2 || xx == 0 || xx == 2;
                    popTex.SetPixel(xx, y, edge ? popBorder : popBg);
                }
            }
            popTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(popTex);
            _popup.border = new RectOffset(1, 1, 1, 1);
            _popup.normal.background = popTex;
            _styleList.Add(_popup);

            _searchBox = new GUIStyle(GUI.skin.textField);
            CleanStyle(_searchBox, new RectOffset(0, 0, 0, 0));            //各状态都须有实底，否则会透出游戏皮肤的悬停贴图
            _searchBox.normal.background = Solid(_cFrame);
            _searchBox.normal.textColor = _cText;
            _searchBox.hover.background = Solid(_cFrameHover);
            _searchBox.hover.textColor = _cText;
            _searchBox.active.background = Solid(_cFrameHover);
            _searchBox.active.textColor = _cText;
            _searchBox.focused.background = Solid(_cFrameHover);
            _searchBox.focused.textColor = _cText;
            _searchBox.padding = new RectOffset(6, 6, 3, 5);
            _styleList.Add(_searchBox);

            //Dear ImGui 风格滑块：轨道 / 已填充 / 把手（normal、hover、active 三态）
        }
        private static void EnsureStylesSliderChrome() {
            _sliderTrack = new GUIStyle(GUI.skin.box);
            CleanStyle(_sliderTrack, new RectOffset(1, 1, 1, 1));
            _sliderTrack.normal.background = Solid(new Color(0.09f, 0.09f, 0.11f, 1f));
            _styleList.Add(_sliderTrack);
            _sliderFill = new GUIStyle(GUI.skin.box);
            CleanStyle(_sliderFill, new RectOffset(1, 1, 1, 1));
            _sliderFill.normal.background = Solid(new Color(0.3f, 0.45f, 0.75f, 1f)); //填充蓝
            _styleList.Add(_sliderFill);
            _sliderHandle = StyleBtn(new Color(0.72f, 0.74f, 0.8f, 1f), new Color(0.72f, 0.74f, 0.8f, 1f), new Color(0.72f, 0.74f, 0.8f, 1f), Color.white);
            _sliderHandle.alignment = TextAnchor.MiddleCenter;
            //把手配色对齐 ImGui 的 Grab / GrabHovered / GrabActive（悬停变亮，按下略暗偏蓝）。
            _sliderHandleHover = StyleBtn(new Color(0.85f, 0.87f, 0.92f, 1f), new Color(0.85f, 0.87f, 0.92f, 1f), new Color(0.85f, 0.87f, 0.92f, 1f), Color.white);
            _sliderHandleHover.alignment = TextAnchor.MiddleCenter;
            _sliderHandleActive = StyleBtn(new Color(0.58f, 0.64f, 0.78f, 1f), new Color(0.58f, 0.64f, 0.78f, 1f), new Color(0.58f, 0.64f, 0.78f, 1f), Color.white);
            _sliderHandleActive.alignment = TextAnchor.MiddleCenter;

            _footer = new GUIStyle(GUI.skin.box);
            CleanStyle(_footer, new RectOffset(1, 1, 1, 1));
            Texture2D footTex = Solid(new Color(0.1f, 0.1f, 0.12f, 1f));
            _footer.normal.background = footTex;
            _styleList.Add(_footer);

            _tooltip = new GUIStyle(GUI.skin.box);
            CleanStyle(_tooltip, new RectOffset(2, 2, 2, 2));
            Texture2D tipTex = Solid(new Color(0.08f, 0.08f, 0.1f, 0.96f));
            _tooltip.normal.background = tipTex;
            _tooltip.normal.textColor = new Color(0.95f, 0.95f, 0.98f, 1f);
            _tooltip.padding = new RectOffset(6, 6, 4, 4);
            _tooltip.wordWrap = true;
            _styleList.Add(_tooltip);

        //右下角缩放三角：实心三角，贴图边长等于绘制边长（点采样无锯齿），纯白贴图由 GUI.color 着色。
        int gs = Mathf.Max(8, Mathf.RoundToInt(Sc(21)));   //贴图边长随 UI 缩放
        _gripTex = new Texture2D(gs, gs, TextureFormat.RGBA32, false);
        Color wclear = new Color(1f, 1f, 1f, 0f);
        Color wsolid = new Color(1f, 1f, 1f, 1f);
        for (int gy = 0; gy < gs; gy++) {
            for (int gx = 0; gx < gs; gx++) {
                //右下角实心三角：贴图坐标 y=0 在下，故填充条件为 gx >= gy。
                bool on = gx >= gy;
                _gripTex.SetPixel(gx, gy, on ? wsolid : wclear);
            }
        }
        _gripTex.Apply();
        _gripTex.filterMode = FilterMode.Point;   //贴图与绘制 1:1，点采样即锐利
        _gripTex.wrapMode = TextureWrapMode.Clamp;
        UnityEngine.Object.DontDestroyOnLoad(_gripTex);

        }
        private static void EnsureStylesCursors() {
            _cursorTex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            Color cclear = new Color(0, 0, 0, 0);
            for (int y = 0; y < 16; y++) {
                for (int x = 0; x < 16; x++) {
                    float d = Mathf.Sqrt((x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f));
                    if (d <= 5.5f) _cursorTex.SetPixel(x, y, Color.white);
                    else if (d <= 6.5f) _cursorTex.SetPixel(x, y, new Color(0.1f, 0.1f, 0.1f, 1f));
                    else _cursorTex.SetPixel(x, y, cclear);
                }
            }
            _cursorTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(_cursorTex);

            //左右调整光标（<->）：悬停栏目分隔线时替代圆点光标
            _cursorResizeTex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            string[] rs = {
                "................",
                "................",
                "................",
                "...#.......#....",
                "..##.......##...",
                ".###.......###..",
                "####.......####.",
                "###############.",
                "###############.",
                "####.......####.",
                ".###.......###..",
                "..##.......##...",
                "...#.......#....",
                "................",
                "................",
                "................"
            };
            for (int ry = 0; ry < 16; ry++) {
                string row = rs[ry];
                for (int rx = 0; rx < 16; rx++) {
                    bool on = rx < row.Length && row[rx] == '#';
                    _cursorResizeTex.SetPixel(rx, 15 - ry, on ? Color.white : cclear);
                }
            }
            _cursorResizeTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(_cursorResizeTex);

            //四向移动光标（+）：悬停可拖动单元格时使用（与联机列表共用）
            _cursorMoveTex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            string[] ms = {
                "................",
                ".......#........",
                "......###.......",
                ".....#####......",
                ".......#........",
                ".......#........",
                "...#...#...#....",
                "..###.###.###...",
                ".#############..",
                "..###.###.###...",
                "...#...#...#....",
                ".......#........",
                ".......#........",
                ".....#####......",
                "......###.......",
                ".......#........"
            };
            for (int my = 0; my < 16; my++) {
                string row = ms[my];
                for (int mx = 0; mx < 16; mx++) {
                    bool on = mx < row.Length && row[mx] == '#';
                    _cursorMoveTex.SetPixel(mx, 15 - my, on ? Color.white : cclear);
                }
            }
            _cursorMoveTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(_cursorMoveTex);
        }

        //复选框的"勾"贴图（透明底 + 白色勾）：对两段线段逐像素求距离做软边抗锯齿。
        private static Texture2D _checkTex;
        private static Texture2D MakeCheckTex(int size, float thickness) {
            Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Vector2 p0 = new Vector2(size * 0.18f, size * 0.50f);   //起点（左中）
            Vector2 p1 = new Vector2(size * 0.40f, size * 0.28f);   //底点（y=0 在下）
            Vector2 p2 = new Vector2(size * 0.82f, size * 0.72f);   //终点（右上）
            float half = thickness * 0.5f;
            Color clear = new Color(1f, 1f, 1f, 0f);
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float d = Mathf.Min(SegDist(p, p0, p1), SegDist(p, p1, p2));
                    float a = Mathf.Clamp01(half + 0.75f - d);
                    t.SetPixel(x, y, a > 0f ? new Color(1f, 1f, 1f, a) : clear);
                }
            }
            t.Apply();
            t.wrapMode = TextureWrapMode.Clamp;
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }
        //点到线段的最短距离
        private static float SegDist(Vector2 p, Vector2 a, Vector2 b) {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

	}
}
