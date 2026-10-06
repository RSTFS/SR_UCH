// 样式。深色主题的 GUIStyle / 字体 / 纹理 / 滑块素材。
// Sc() 是统一的缩放系数与样式缓存入口。
using System;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// 分区：Styles（深色主题 GUIStyle / 字体 / 纹理 / 滑块素材）

        internal static Font OrigSkinFontValue { get { return _origSkinFont; } }
        private static Font _origSkinFont;                 // **全局皮肤原始字体（用于恢复，防泄漏到 CM 等界面）**
        private static bool _origSelColorSaved;
        private static Color _origSelColor;               // **全局皮肤原始选择色**
        private static Texture2D Solid(Color c) {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }

        //样式复制自 GUI.skin 后重置边框/边距/偏移（游戏皮肤自带大边距会挤压文字并裁剪字形），只保留文字颜色。
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

        //中文字体候选名（依次尝试，取第一个真正创建成功的）：
        //Windows 用微软雅黑；Linux/Proton（含 Steam Deck）通常只带开源 CJK 字体，硬编码单个名字时
        //CreateDynamicFontFromOSFont **返回 null 而不抛异常** -> 紧接着 DontDestroyOnLoad(null) 抛异常 ->
        //_font 保持 null -> 中文退回 Unity 默认字体，显示为豆腐块。故必须显式判空 + 候选回退。
        //注：微软雅黑的 CJK 字形下沉到字体度量下方，故所有文本样式加额外垂直内边距防裁切（见 padding）。
        private static readonly string[] FontCandidates = {
            "Microsoft YaHei", "微软雅黑", "Noto Sans CJK SC", "Source Han Sans SC",
            "WenQuanYi Micro Hei", "PingFang SC", "Heiti SC"
        };

        //整轮字体创建失败时的退避状态（见 EnsureFont）：候选字体全都不存在时，原来会**每帧**把 7 个候选
        //重新 FindOSFont 一遍（SR.Window 每帧调用）；系统字体运行期不会凭空出现，故失败后退避 3 秒再试。
        private static int _fontFailSize = -1;   //上次整轮失败的目标字号（-1 = 没有失败记录）
        private static float _fontNextTryAt;

        //font size follows the UI scale so layout height matches rendered text height
        private static void EnsureFont() {
            int fs = Mathf.RoundToInt(14f * Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f));
            if (_font != null && _font.fontSize == fs) return; //正常路径：字号没变，直接返回
            //这一档字号刚刚整轮失败过：退避期内不再重试（否则每帧 7 次系统字体查找）
            if (_fontFailSize == fs && Time.unscaledTime < _fontNextTryAt) return;
            //缩放变化时 _font 已存在：原代码的循环条件 _font == null 直接让循环体不执行 -> 字号永远不变。
            //这里先把旧字体摘下来再重建，失败则回退旧字体（避免中文变豆腐块）。
            Font old = _font;
            _font = null;
            for (int i = 0; i < FontCandidates.Length && _font == null; i++) {
                try {
                    Font nf = Font.CreateDynamicFontFromOSFont(FontCandidates[i], fs);
                    if (nf == null) continue; //字体不存在：返回 null 而非抛异常，必须显式判空
                    UnityEngine.Object.DontDestroyOnLoad(nf);
                    _font = nf;
                } catch (Exception __ex) { Guard.Log("创建中文字体(" + FontCandidates[i] + ")", __ex); }
            }
            if (_font == null) {
                _font = old;                      //回退旧字体（可能也是 null：启动时就没有可用字体）
                _fontFailSize = fs;               //记下失败的字号档，3 秒内不再尝试
                _fontNextTryAt = Time.unscaledTime + 3f;
            } else {
                _fontFailSize = -1;               //成功：清掉失败记录，后续缩放变化照常即时生效
                // 全局泄漏修复：GUI.skin 是**全局共享**皮肤（ConfigurationManager 等也在用），
                //  必须在第一次改动前记下"原始字体/选择色"，之后一律恢复到原始值，否则别的界面字体会被放大、颜色被改。
                try {
                    if (_origSkinFont == null) _origSkinFont = GUI.skin.font;
                    // G7：selectionColor 的原始值保存已挪到 EnsureStyles, 本方法比 EnsureStyles **后**执行，
                    //  在这里保存只会抓到"已经被改写的蓝色"。详见 EnsureOrigSelColor 的说明。
                    GUI.skin.font = _font;
                } catch (Exception __ex) { Guard.Log("EnsureFont", __ex); }
                //成功换新字体后销毁旧字体（旧对象曾 DontDestroyOnLoad，不销毁会逐档泄漏）
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

        // G7：保存"原始选择色"必须发生在**第一次改写之前**。
        //  原来这句写在 EnsureFont 里，而 EnsureStyles 比它**先**执行（SR.Window.cs:241 -> 242）
        //  -> 抓到的已经是改后的蓝色，"全局泄漏修复"等于没做（_origSelColor 也只写不读）。
        private static void EnsureOrigSelColor() {
            try {
                if (!_origSelColorSaved) { _origSelColor = GUI.skin.settings.selectionColor; _origSelColorSaved = true; }
            } catch (Exception __ex) { Guard.Log("保存原始选择色", __ex); }
        }
        //本帧绘制结束后把全局选择色还原：GUI.skin 是**全局共享**皮肤，Configuration Manager 等也在用
        internal static void RestoreSkinSelectionColor() {
            try { if (_origSelColorSaved && GUI.skin != null) GUI.skin.settings.selectionColor = _origSelColor; } catch { }
        }

        private static void EnsureStyles() {
            //if the styles survived but their textures were unloaded, rebuild
            if (_stylesReady && _win != null && _win.normal.background == null) _stylesReady = false;
            if (_stylesReady) return;
            _stylesReady = true;
            foreach (GUIStyle s in _styleList) {
                if (s.normal.background != null) UnityEngine.Object.Destroy(s.normal.background);
                if (s.hover.background != null) UnityEngine.Object.Destroy(s.hover.background);
                if (s.active.background != null) UnityEngine.Object.Destroy(s.active.background);
                if (s.focused.background != null) UnityEngine.Object.Destroy(s.focused.background);
            }
            _styleList.Clear();
            //_gripTex/_cursorTex 挂在 DontDestroyOnLoad 上，重建前必须先销毁旧对象，否则每次重建泄漏
            if (_gripTex != null) { UnityEngine.Object.Destroy(_gripTex); _gripTex = null; }
            if (_cursorTex != null) { UnityEngine.Object.Destroy(_cursorTex); _cursorTex = null; }
            if (_cursorResizeTex != null) { UnityEngine.Object.Destroy(_cursorResizeTex); _cursorResizeTex = null; }
            if (_cursorMoveTex != null) { UnityEngine.Object.Destroy(_cursorMoveTex); _cursorMoveTex = null; }
            if (_checkTex != null) { UnityEngine.Object.Destroy(_checkTex); _checkTex = null; }   // **R411**
        //文本选中高亮：统一改成左栏选中项的那种蓝色（IMGUI 默认是橙色）
        EnsureOrigSelColor();   // G7：必须**先存原始值再改写**（保存点原来写在 EnsureFont 里，那时已经晚了）
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
            _titleLabel.padding = new RectOffset(6, 6, 3, 5); //CJK glyph sink room
            _styleList.Add(_titleLabel);

            _titleMid = new GUIStyle(_titleLabel);
            _titleMid.fontStyle = FontStyle.Normal;
            _titleMid.fontSize = 0; //follows the scaled font
            _titleMid.normal.textColor = new Color(0.75f, 0.78f, 0.85f, 1f);
            //左对齐（原来是居中）：宽度已经收窄到总开关左边，居中时窗口一窄两端一起裁，
            //左对齐只会从右边裁掉，文字开头始终可读。
            _titleMid.alignment = TextAnchor.MiddleLeft;
            _styleList.Add(_titleMid);

        EnsureStylesTexts();
        EnsureStylesSidebarItems();
        EnsureStylesButtons();
        EnsureStylesPopups();
        EnsureStylesSliderChrome();
        EnsureStylesCursors();
        }

        // 拆分自原 EnsureStyles（53 行）：**只做搬移，未改任何逻辑**
        private static void EnsureStylesTexts() {
            _label = new GUIStyle(GUI.skin.label);
            CleanStyle(_label, new RectOffset(0, 0, 0, 0));
            _label.normal.textColor = _cText;
            _label.wordWrap = false; //wrapping only where it is explicit (name column / chat)
            _label.padding = new RectOffset(6, 6, 3, 5); //CJK glyph sink room
            _styleList.Add(_label);

            _nameLabel = new GUIStyle(_label);
            _nameLabel.wordWrap = true; //entry name column: wrap instead of clipping
            _styleList.Add(_nameLabel);

            //固定宽度单元格用：不折行、超出就裁掉（表格里长房主名/长区域名不再挤进下一列）
            _labelClip = new GUIStyle(_label);
            _labelClip.wordWrap = false;
            _labelClip.clipping = TextClipping.Clip;
            _styleList.Add(_labelClip);

            //联机列表：表头与单元格一律居中
            _labelClipCenter = new GUIStyle(_labelClip);
            _labelClipCenter.alignment = TextAnchor.MiddleCenter;
            _styleList.Add(_labelClipCenter);

            _secHeaderCenter = new GUIStyle(_labelClip);
            _secHeaderCenter.fontStyle = FontStyle.Bold;
        // **#2 表头文字"没到边线就显示不全"的真因**：本样式继承 _label 的左右各 6px padding，
        //  列宽够也被吃掉 12px 文字区。表头是"居中在格子里"，左右不需要内边距 -> 压到 2px。
        _secHeaderCenter.padding = new RectOffset(2, 2, 3, 5);
            _secHeaderCenter.alignment = TextAnchor.MiddleCenter;
            _secHeaderCenter.normal.textColor = new Color(0.65f, 0.78f, 1f, 1f);
            _styleList.Add(_secHeaderCenter);

            _secHeader = new GUIStyle(_label);
            _secHeader.fontStyle = FontStyle.Bold;
            _secHeader.normal.textColor = new Color(0.65f, 0.78f, 1f, 1f);
            _styleList.Add(_secHeader);

            _labelWrap = new GUIStyle(_label);
            _labelWrap.wordWrap = true; //long descriptions wrap when the window is narrow
            _labelWrap.fontSize = Mathf.RoundToInt(14f * Mathf.Max(1f, _scaled));
            _labelWrap.padding = new RectOffset(6, 6, 6, 14); //extra bottom room so the last wrapped line never clips
            _styleList.Add(_labelWrap);

            _chatLabel = new GUIStyle(_label);
            _chatLabel.wordWrap = true; //chat entries wrap; extra vertical room so wrapped CJK lines never clip
            _chatLabel.padding = new RectOffset(6, 6, 6, 16);
            _styleList.Add(_chatLabel);

            //会话内容页用只读编辑框显示聊天记录（可选中/Ctrl+C 复制；样式沿用聊天标签，不要输入框底色）
            _chatArea = new GUIStyle(_chatLabel);
            _chatArea.wordWrap = true;
            _chatArea.richText = false;
            _styleList.Add(_chatArea);

        }
        // 拆分自原 EnsureStyles（16 行）：**只做搬移，未改任何逻辑**
        private static void EnsureStylesSidebarItems() {
            _item = StyleBtn(_cItem, _cItemHover, _cItemHover, _cText);
            _item.alignment = TextAnchor.MiddleLeft;
            // **#7 侧栏相邻栏目之间的小缝隙**：把外边距归零，让相邻按钮严丝合缝
            _item.margin = new RectOffset(0, 0, 0, 0);
            _item.border = new RectOffset(0, 0, 0, 0); // **#1 9-slice 边框内缩也会在相邻栏目之间留缝**
            _selItem = StyleBtn(_cSelItem, _cSelItem, _cSelItem, Color.white);
            _selItem.alignment = TextAnchor.MiddleLeft;
            // 几何参数与普通项完全一致（只差配色）：否则选中项的 9-slice 边框/边距与普通项不同，
            //  相邻栏目之间会出现一道细缝（关卡<->自由模式、联机<->实验 就是选中项紧挨普通项时露出来的）。
            _selItem.margin = _item.margin;
            _selItem.border = _item.border;
            _selItem.padding = _item.padding;
            _selItem.overflow = _item.overflow;
            _selItem.margin = new RectOffset(0, 0, 0, 0); // **#7 同上**
            _selItem.border = new RectOffset(0, 0, 0, 0); // **#1 同上**

        }
        // 拆分自原 EnsureStyles（44 行）：**只做搬移，未改任何逻辑**
        private static void EnsureStylesButtons() {
            _btn = StyleBtn(_cBtnBg, _cBtnHover, _cBtnActive, _cText);
            _btn.alignment = TextAnchor.MiddleCenter;
            //窄格子里的按钮（联机列表操作列）：内边距收到 2px，宽度够就不该被裁掉
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
            Color chkOffBorder = new Color(0.45f, 0.48f, 0.55f, 0.9f); //浅灰边框，未勾选也能看清
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
            _checkOn.normal.background = Solid(new Color(0.2f, 0.32f, 0.52f, 1f)); //勾选：淡蓝底
            _checkOn.hover.background = Solid(new Color(0.26f, 0.4f, 0.62f, 1f));

            // **R411**：勾改用运行时生成的贴图，不再依赖"当前字体恰好有 U+2713"。
            //  字体回退到 Unity 默认字体时 ✓ 会变豆腐块（见文件头字体候选那段），贴图与字体度量无关。
            _checkTex = MakeCheckTex(24, 2.6f);

        }
        // 拆分自原 EnsureStyles（32 行）：**只做搬移，未改任何逻辑**
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
            CleanStyle(_searchBox, new RectOffset(0, 0, 0, 0));            //各状态都必须有实底，否则游戏皮肤的悬停贴图（椭圆斑）会透出像黑洞
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
        // 拆分自原 EnsureStyles（54 行）：**只做搬移，未改任何逻辑**
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
            // **R411**：把手补上真正的三态（原来 hover/active 两个字段指向**同一个**样式，
            //  DrawSlider 按 mine/hover 选样式是空转，注释里的"三态反馈"名存实亡）。
            //  配色对齐 ImGui 的 Grab / GrabHovered / GrabActive（悬停变亮、按下略暗偏蓝）。
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

        // #4 右下角缩放三角：按**实际绘制尺寸**生成贴图，从根上消除锯齿。
        //  原来固定 16×16 贴图 + FilterMode.Point 画到 24px -> 1px 斜线被非整数拉伸成台阶。
        //  现在贴图边长 = 绘制边长（Sc(24)），1px 斜线就是 1px，点采样也完全对齐。
        // #1 缩放三角按 ImGui 的画法重做：**实心三角**（ImGui 的 ResizeGrip 就是一个填充三角，
        //  不是三条斜线），且贴图边长 = 绘制边长、位置整数对齐 -> 点采样下不可能出现锯齿。
        //  贴图用纯白，颜色/透明度在绘制时用 GUI.color 决定（对应 ImGui 的 ResizeGrip/Hovered/Active）。
        int gs = Mathf.Max(8, Mathf.RoundToInt(Sc(21)));   //尺寸：原 16 -> 扩大三分之一（21）
        _gripTex = new Texture2D(gs, gs, TextureFormat.RGBA32, false);
        Color wclear = new Color(1f, 1f, 1f, 0f);
        Color wsolid = new Color(1f, 1f, 1f, 1f);
        for (int gy = 0; gy < gs; gy++) {
            for (int gx = 0; gx < gs; gx++) {
                //右下角实心三角：在"反对角线"以下（含）填充
                // **向右旋转 90°**：
                //  贴图 SetPixel 的 y=0 是**下**边，而 DrawTexture 把它画成"y=0 在下"-> 屏幕 sy = gs-1-gy；
                //  原判定在屏幕坐标里等价于 sy<=sx（左下三角）；绕中心顺时针转 90° 后变为 sx+sy>=gs-1（右下三角），
                //  换回贴图坐标即 gx>=gy。
                bool on = gx >= gy;
                _gripTex.SetPixel(gx, gy, on ? wsolid : wclear);
            }
        }
        _gripTex.Apply();
        _gripTex.filterMode = FilterMode.Point;   //贴图与绘制 1:1，点采样即锐利
        _gripTex.wrapMode = TextureWrapMode.Clamp;
        UnityEngine.Object.DontDestroyOnLoad(_gripTex);

        }
        // 拆分自原 EnsureStyles（72 行）：**只做搬移，未改任何逻辑**
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

        // **R411**：复选框的"勾"贴图（透明底 + 白色勾）。对两段线段逐像素求距离，1px 软边抗锯齿；
        //  双线性过滤 + 绘制时缩放到复选框内圈，边缘平滑。只在 EnsureStyles 建一次（重建前先销毁）。
        private static Texture2D _checkTex;
        private static Texture2D MakeCheckTex(int size, float thickness) {
            Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Vector2 p0 = new Vector2(size * 0.18f, size * 0.50f);   //起点（左中）
            Vector2 p1 = new Vector2(size * 0.40f, size * 0.28f);   //底点（SetPixel 的 y=0 在下）
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
