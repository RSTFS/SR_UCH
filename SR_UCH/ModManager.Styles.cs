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
public partial class ModManager {

// ==== 分区：Styles（深色主题 GUIStyle / 字体 / 纹理 / 滑块素材）====

        //--- styles (dark, Config Manager-ish) ---
        private static Texture2D Solid(Color c) {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            UnityEngine.Object.DontDestroyOnLoad(t);
            return t;
        }

        //styles are copied from GUI.skin (the game's custom skin may carry large borders /
        //margins / offsets that squeeze the text area and clip glyphs) - reset everything
        //except the text color so layout and rendering stay predictable
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
            //bottom padding absorbs the CJK glyph sink so text is never clipped
            s.padding = new RectOffset(8, 6, 3, 5);
            _styleList.Add(s);
            return s;
        }

        //font size follows the UI scale so layout height matches rendered text height
        private static void EnsureFont() {
            int fs = Mathf.RoundToInt(14f * Mathf.Clamp(_uiScaleEntry.Value, 1f, 1.8f));
            if (_font == null || _font.fontSize != fs) {
                try {
                    //Microsoft YaHei: CJK glyphs sink below the font metrics, so all text
                    //styles carry extra vertical padding (see EnsureStyles) to avoid clipping
                    Font nf = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", fs);
                    UnityEngine.Object.DontDestroyOnLoad(nf);
                    _font = nf;
                } catch { }
            }
        }

        private static void EnsureStyles() {
            //if the styles survived but their textures were unloaded, rebuild
            if (_stylesReady && _win != null && _win.normal.background == null) _stylesReady = false;
            if (_stylesReady) return;
            _stylesReady = true;
            //cleanup old style textures
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
            _titleMid.alignment = TextAnchor.MiddleCenter;
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

            _item = StyleBtn(item, itemHover, itemHover, text);
            _item.alignment = TextAnchor.MiddleLeft;
            _selItem = StyleBtn(selItem, selItem, selItem, Color.white);
            _selItem.alignment = TextAnchor.MiddleLeft;

            _btn = StyleBtn(btnBg, btnHover, btnActive, text);
            _btn.alignment = TextAnchor.MiddleCenter;
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
            //Dear ImGui 风格复选框：方形边框（1px）+ 深色底，勾选时填充淡蓝
            //未勾选：深色底 + 可见浅灰边框（否则和背景融为一体看不清）
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
            CleanStyle(_searchBox, new RectOffset(0, 0, 0, 0));            //every state gets a solid background - otherwise the game skin's hover
            //texture (an oval blob) shows through and looks like a black hole
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
            //generate the grip (three diagonal bars) then rotate it 270° clockwise (90° + 180°)
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
