using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Styles（深色主题 GUIStyle / 字体 / 纹理 / 滑块素材）====

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
        //CreateDynamicFontFromOSFont **返回 null 而不抛异常** → 紧接着 DontDestroyOnLoad(null) 抛异常 →
        //_font 保持 null → 中文退回 Unity 默认字体，显示为豆腐块。故必须显式判空 + 候选回退。
        //注：微软雅黑的 CJK 字形下沉到字体度量下方，故所有文本样式加额外垂直内边距防裁切（见 padding）。
        private static readonly string[] FontCandidates = {
            "Microsoft YaHei", "微软雅黑", "Noto Sans CJK SC", "Source Han Sans SC",
            "WenQuanYi Micro Hei", "PingFang SC", "Heiti SC"
        };

        //font size follows the UI scale so layout height matches rendered text height
        private static void EnsureFont() {
            int fs = Mathf.RoundToInt(14f * Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f));
            if (_font == null || _font.fontSize != fs) {
                //缩放变化时 _font 已存在：原代码的循环条件 `_font == null` 直接让循环体不执行 → 字号永远不变。
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
                if (_font == null) _font = old;
                try { if (_font != null) GUI.skin.font = _font; } catch { }
            }
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
            Color bg = new Color(0.13f, 0.13f, 0.15f, 0.97f);
            Color title = new Color(0.08f, 0.08f, 0.1f, 1f);
            Color item = new Color(0.18f, 0.19f, 0.22f, 1f);
            Color itemHover = new Color(0.24f, 0.26f, 0.32f, 1f);
            Color selItem = new Color(0.26f, 0.37f, 0.56f, 1f);
        //文本选中高亮：统一改成左栏选中项的那种蓝色（IMGUI 默认是橙色）
        GUI.skin.settings.selectionColor = selItem;
            Color btnBg = new Color(0.2f, 0.21f, 0.24f, 1f);
            Color btnHover = new Color(0.3f, 0.32f, 0.38f, 1f);
            Color btnActive = new Color(0.16f, 0.17f, 0.2f, 1f);
            Color frame = new Color(0.24f, 0.25f, 0.28f, 1f);
            Color frameHover = new Color(0.3f, 0.32f, 0.36f, 1f);
            Color capture = new Color(0.5f, 0.3f, 0.12f, 1f);
            Color border = new Color(0.4f, 0.4f, 0.45f, 0.7f);
            Color text = new Color(0.92f, 0.92f, 0.95f, 1f);

            Texture2D winTex = new Texture2D(2, 2);
            winTex.SetPixel(0, 0, bg); winTex.SetPixel(1, 0, bg); winTex.SetPixel(0, 1, bg); winTex.SetPixel(1, 1, bg);
            winTex.Apply();
            UnityEngine.Object.DontDestroyOnLoad(winTex);
            _win = new GUIStyle(GUI.skin.box);
            CleanStyle(_win, new RectOffset(1, 1, 1, 1));
            _win.normal.background = winTex;
            _styleList.Add(_win);

            _title = StyleBtn(title, title, title, text);
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

            _label = new GUIStyle(GUI.skin.label);
            CleanStyle(_label, new RectOffset(0, 0, 0, 0));
            _label.normal.textColor = text;
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

            _item = StyleBtn(item, itemHover, itemHover, text);
            _item.alignment = TextAnchor.MiddleLeft;
            _selItem = StyleBtn(selItem, selItem, selItem, Color.white);
            _selItem.alignment = TextAnchor.MiddleLeft;

            _btn = StyleBtn(btnBg, btnHover, btnActive, text);
            _btn.alignment = TextAnchor.MiddleCenter;
            //窄格子里的按钮（联机列表「操作」列）：内边距收到 2px，宽度够就不该被裁掉
            _btnClip = new GUIStyle(_btn);
            _btnClip.padding = new RectOffset(2, 2, 2, 2);
            _btnClip.clipping = TextClipping.Clip;
            _styleList.Add(_btnClip);
            _frame = StyleBtn(frame, frameHover, frame, text);
            _frame.alignment = TextAnchor.MiddleCenter;
            _capture = StyleBtn(capture, capture, capture, Color.white);
            _capture.alignment = TextAnchor.MiddleCenter;
            _checkOn = StyleBtn(frame, frameHover, frame, new Color(0.6f, 0.85f, 1f, 1f));
            _checkOn.alignment = TextAnchor.MiddleCenter;
            _checkOn.padding = new RectOffset(0, 0, 0, 0);
            _checkOff = StyleBtn(frame, frameHover, frame, new Color(0.35f, 0.35f, 0.4f, 1f));
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
            _searchBox.normal.background = Solid(frame);
            _searchBox.normal.textColor = text;
            _searchBox.hover.background = Solid(frameHover);
            _searchBox.hover.textColor = text;
            _searchBox.active.background = Solid(frameHover);
            _searchBox.active.textColor = text;
            _searchBox.focused.background = Solid(frameHover);
            _searchBox.focused.textColor = text;
            _searchBox.padding = new RectOffset(6, 6, 3, 5);
            _styleList.Add(_searchBox);

            //Dear ImGui 风格滑块：轨道 / 已填充 / 把手（normal、hover、active 三态）
            _sliderTrack = new GUIStyle(GUI.skin.box);
            CleanStyle(_sliderTrack, new RectOffset(1, 1, 1, 1));
            _sliderTrack.normal.background = Solid(new Color(0.09f, 0.09f, 0.11f, 1f));
            _styleList.Add(_sliderTrack);
            _sliderFill = new GUIStyle(GUI.skin.box);
            CleanStyle(_sliderFill, new RectOffset(1, 1, 1, 1));
            _sliderFill.normal.background = Solid(new Color(0.3f, 0.45f, 0.75f, 1f)); //填充蓝
            _styleList.Add(_sliderFill);
            _sliderHandle = StyleBtn(new Color(0.72f, 0.74f, 0.8f, 1f), new Color(0.85f, 0.87f, 0.92f, 1f), new Color(0.6f, 0.62f, 0.68f, 1f), Color.white);
            _sliderHandle.alignment = TextAnchor.MiddleCenter;
            _sliderHandleHover = _sliderHandle;
            _sliderHandleActive = _sliderHandle;

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

            _gripTex = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            Color clear = new Color(0, 0, 0, 0);
            //生成三条斜纹把手，再旋转 270°（90° + 180°）
            bool[,] gripOld = new bool[16, 16];
            for (int y = 0; y < 16; y++) {
                for (int x = 0; x < 16; x++) {
                    float sum = x + y;
                    gripOld[x, y] = (Mathf.Abs(sum - 22f) <= 1.5f && x >= 7 && y >= 7)
                        || (Mathf.Abs(sum - 25f) <= 1.5f && x >= 9 && y >= 9)
                        || (Mathf.Abs(sum - 28f) <= 1.5f && x >= 11 && y >= 11);
                }
            }
            for (int ny = 0; ny < 16; ny++) {
                for (int nx = 0; nx < 16; nx++) {
                    _gripTex.SetPixel(nx, ny, gripOld[15 - ny, nx] ? new Color(0.8f, 0.8f, 0.85f, 1f) : clear);
                }
            }
            _gripTex.Apply();
            _gripTex.filterMode = FilterMode.Point; //16px 贴图按 24px 画：点采样才不会糊
            UnityEngine.Object.DontDestroyOnLoad(_gripTex);

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
        }

	}
}
