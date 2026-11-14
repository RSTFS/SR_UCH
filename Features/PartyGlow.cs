// 派对盒道具「品质光芒」。
//
// 依据（全部逐行核对过游戏反编译源码，均为 public 编译期可见 API，零反射登记）：
//   Placeable.Rarity                 :11982   NeverPick=0 / Rare=200 / Normal=1000 / Common=2000
//   TabletBlockList.GetWeightFromStepValue :104578  概率档 0..9 → 权重 0/100/200/500/700/1000/1500/2000/3000/5000
//   BlockGroup.GetItemFilterWeight  :6954   按派对盒实际抽取口径取权重（自动处理 FilterOverride）
//   GameSettings.GetBlockFrequency  :129627 退化路径：按 blockSerializeIndex 读频率档
//   PartyBox.InitializeWeightedBlockList :87385 派对盒抽取用的正是 GetItemFilterWeight
//   PickableBlock.InPartybox        :49752  [SyncVar]，客户端已同步
//   PickableBlock.ArtSprites        :49736  / ArtHolderTransform :49759
//   PartyBox 布置道具               :87357  设为 layer 5、ChangeArtLayer("Background 2")、NetworkServer.Spawn
//
// 设计要点（刻意避开这个仓库历史上的三个坑）：
//   1) 不建全局强引用字典 —— 光晕是「道具自己的子对象」，道具销毁时自动带走，
//      用 Transform.Find(名字) 判重与更新，完全无状态（Bounds.cs 的 alive/dead 范式都不需要）。
//   2) 不改原 SpriteRenderer / 材质 —— 只加一层半透明径向渐变，排序放在最底，绝不遮住道具本体。
//   3) 不碰网络、不碰相机、不新增 Harmony 补丁 / msgType / 反射登记、不加线程。
//      概率本来就是 GameSettings 同步的，各端各算各画，天然一致。
//
// 品质方向：概率越低 = 越稀有 = 品质越高（RPG 直觉）。可用「反向着色」开关翻转。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.partyglow", "品质光芒", "1.0", Provides = new string[] { "sr.brief.rarest" })]
    public class PartyGlow : ITweak {
        private const string Sec = "品质光芒";
        private const string GlowName = "SR_UCH_RarityGlow";
        private const float ScanInterval = 0.05f;   // 道具出现后光晕尽快跟上（原来 0.2s 有明显延迟）

        // 6 档：0 泛滥 / 1 普通 / 2 精良 / 3 稀有 / 4 史诗 / 5 传说；-1 = 禁用（不出现）
        private static readonly string[] TierZh = { "泛滥", "普通", "精良", "稀有", "史诗", "传说" };
        private static readonly string[] TierEn = { "Flood", "Common", "Fine", "Rare", "Epic", "Legendary" };
        private static readonly Color[] TierColorDefault = {
            new Color(0.60f, 0.64f, 0.70f),   // 泛滥 · 灰
            new Color(0.91f, 0.94f, 0.98f),   // 普通 · 白
            new Color(0.42f, 0.88f, 0.54f),   // 精良 · 绿
            new Color(0.36f, 0.72f, 1.00f),   // 稀有 · 蓝
            new Color(0.71f, 0.48f, 1.00f),   // 史诗 · 紫
            new Color(1.00f, 0.83f, 0.36f),   // 传说 · 金
        };
        // 实际生效的颜色（配置优先，缺省回退默认表）。
        private static readonly Color[] TierColor = new Color[6];
        private static readonly float[] TierAlpha = { 0.22f, 0.36f, 0.52f, 0.68f, 0.84f, 1.00f };
        private static readonly int[] TierMaxWeight = { int.MaxValue, 2000, 1000, 700, 500, 200 };

        private static IFeatureHost _mp;
        private static ConfigEntry<bool> _enabled, _breathe;
        private static ConfigEntry<string>[] _tierColors;   // 六档自定义颜色（十六进制）
        private static ConfigEntry<int> _strength, _size;
        private static Sprite _glowSprite;          // 柔光（六档共用一张）
        private static Material[] _addMats;         // 六档各一份加色材质（共享同一份时颜色会被最后一件覆盖）
        private static bool _addShaderTried;
        private static Shader _addShader;
        private static Sprite[] _glowShapes;        // 6 档形状贴图（可读性页开启时）
        private static float _nextScan;
        // 可读性适配（软依赖）：只查槽位名，缺 DLL 时零 miss、零副作用。
        private static Func<bool> _aShape, _aContrast;
        private static int _aTries;
        private static float _aNext;
        private static readonly int[] _tierCount = new int[7];   // 索引 6 = 禁用件数
        private static int _totalSeen;
        private static int _wMin = int.MaxValue, _wMax = 0;   // 本盒出现过的权重范围（相对分档用）

        // ============ 生命周期 ============

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
            SRApi.LocKey(Sec, "Enabled", "启用品质光芒", "Enable rarity glow");
            SRApi.LocDesc(Sec, "Enabled",
                "启用品质光芒\n按当前概率给派对盒里的道具加一圈品质光晕；纯本地视觉，联机各端各画各的。",
                "Enable rarity glow\nAdds a soft radial glow behind party-box items based on their current draw probability. Local only.");
            SRApi.LocKey(Sec, "Strength", "光芒强度", "Glow strength");
            SRApi.LocDesc(Sec, "Strength",
                "光芒强度\n光晕不透明度百分比（20-150）。",
                "Glow strength\nGlow opacity in percent. Range 20-150.");
            SRApi.LocKey(Sec, "Size", "光芒尺寸", "Glow size");
            SRApi.LocDesc(Sec, "Size",
                "光芒尺寸\n光晕相对道具包围盒的尺寸百分比（100-300）。",
                "Glow size\nGlow size relative to the item bounds in percent. Range 100-300.");
            SRApi.LocKey(Sec, "Breathe", "呼吸脉动", "Breathe");
            SRApi.LocDesc(Sec, "Breathe",
                "呼吸脉动\n轻微呼吸脉动。用缩放时间驱动：梦境定格时它会跟着静止。",
                "Breathe\nGentle pulse driven by scaled time; stops while Dream freezes the clock.");
            if (!_uiReg) { _uiReg = true; SRApi.RowSliders.Add(UiSlider); }
        }

        // 数值条目改由滑块渲染：范围与 Bind 的 AcceptableValueRange 逐项一致。
        private static bool UiSlider(ConfigEntryBase e, out float min, out float max, out string fmt) {
            min = 0f; max = 0f; fmt = "0";
            try {
                if (e == null || e.Definition.Section != Sec) return false;
                switch (e.Definition.Key) {
                    case "Strength": min = 20.0f; max = 150.0f; return true;
                    case "Size": min = 100.0f; max = 300.0f; return true;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("数值滑块范围", __ex); }
            return false;
        }

        public void Initialize(IFeatureHost plugin) {
            _mp = plugin;
            _enabled = _mp.Config.Bind(Sec, "Enabled", true, "按当前概率给派对盒里的道具加一圈品质光晕；纯本地视觉，联机各端各画各的。");
            _strength = _mp.Config.Bind(Sec, "Strength", 80, new ConfigDescription("光晕不透明度百分比（20-150）。", new AcceptableValueRange<int>(20, 150)));
            _tierColors = new ConfigEntry<string>[6];
            for (int i = 0; i < 6; i++) {
                _tierColors[i] = _mp.Config.Bind(Sec, "Tier Color " + i, "#" + ColorUtility.ToHtmlStringRGB(TierColorDefault[i]),
                    "第 " + (i + 1) + " 档（" + TierZh[i] + "）的光晕颜色，十六进制 #RRGGBB。");
                TierColor[i] = ParseColor(_tierColors[i].Value, TierColorDefault[i]);
                SRApi.LocKey(Sec, "Tier Color " + i, TierZh[i] + "颜色", TierEn[i] + " color");
            }
            _size = _mp.Config.Bind(Sec, "Size", 180, new ConfigDescription("光晕相对道具包围盒的尺寸百分比（100-300）。", new AcceptableValueRange<int>(100, 300)));
            _breathe = _mp.Config.Bind(Sec, "Breathe", false, "轻微呼吸脉动。用缩放时间驱动：梦境定格时它会跟着静止。");

            SRApi.LocSec(Sec, "品质光芒", "Rarity Glow");

            RegUi();
            SRApi.Nav(Sec, 120);                 // 实验(100) 之后
            SRApi.RegisterPage(Sec, "Glow", "品质光芒", "Rarity Glow", 10, RenderPage);
            SRApi.RegisterTick("品质光芒-扫描", Tick);
            Slots.Provide("sr.brief.rarest", (Func<string>)(() => BriefRarest()));
            _enabled.SettingChanged += (s, e) => { if (!_enabled.Value) RemoveAll(); };
        }

        public void Shutdown() {
            RemoveAll();
            if (_glowSprite != null) {
                try { UnityEngine.Object.Destroy(_glowSprite); }
                catch (Exception ex) { SRApi.Guard.Log("品质光芒/释放贴图", ex); }
                _glowSprite = null;
            }
            if (_glowShapes != null) {
                for (int i = 0; i < _glowShapes.Length; i++) {
                    if (_glowShapes[i] == null) continue;
                    try { UnityEngine.Object.Destroy(_glowShapes[i]); }
                    catch (Exception ex) { SRApi.Guard.Log("品质光芒/释放形状贴图", ex); }
                }
                _glowShapes = null;
            }
        }

        // ============ 每帧扫描（按帧对象缓存 + 0.2s 节流） ============

        private static void Tick() {
            BindA11y();
            if (!_enabled.Value || Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanInterval;
            try {
                for (int i = 0; i < _tierCount.Length; i++) _tierCount[i] = 0;
                _totalSeen = 0;
                _wMin = int.MaxValue; _wMax = 0;

                PickableBlock[] all = SRApi.FrameObjects<PickableBlock>();
                if (all == null) return;
                // 第一遍：先把全盒权重范围统计完。边扫边分级会让"第一件只有它自己"落回绝对阈值、
                // 第二件起才走相对分档 —— 同一盒用两套相反标准，六档分布会乱。
                for (int i = 0; i < all.Length; i++) {
                    PickableBlock pb0 = all[i];
                    if (pb0 == null || !pb0.InPartybox) continue;
                    try {
                        Placeable __pl = pb0.placeablePrefab;
                        if (__pl != null) {
                            int __w = BlockGroup.GetItemFilterWeight(__pl, (int)__pl.BaseRarity);
                            if (__w > 0) { if (__w < _wMin) _wMin = __w; if (__w > _wMax) _wMax = __w; }
                        }
                    } catch (Exception __ww) { SRApi.Guard.Log("品质光芒/权重范围", __ww); }
                }
                // 第二遍：按完整范围分级并上光。
                for (int i = 0; i < all.Length; i++) {
                    PickableBlock pb = all[i];
                    if (pb == null || !pb.InPartybox) continue;      // 只处理派对盒里的，场上已放置的不碰
                    _totalSeen++;
                    int tier = TierOf(pb);
                    if (tier < 0) { _tierCount[6]++; HideGlow(pb); continue; }
                    _tierCount[tier]++;
                    ApplyGlow(pb, tier);
                }
            } catch (Exception ex) { SRApi.Guard.Log("品质光芒/扫描", ex); }
        }

        /// <summary>派对盒实际抽取权重 → 品质档。优先用与 PartyBox 完全同源的 GetItemFilterWeight。</summary>
        private static int TierOf(PickableBlock pb) {
            int w = -1;
            try {
                Placeable pl = pb.placeablePrefab;
                if (pl != null) w = BlockGroup.GetItemFilterWeight(pl, (int)pl.BaseRarity);
                if (w < 0 && pb.blockSerializeIndex >= 0) {
                    int step = GameSettings.GetInstance().GetBlockFrequency(pb.blockSerializeIndex);
                    w = TabletBlockList.GetWeightFromStepValue(step);
                }
            } catch (Exception ex) { SRApi.Guard.Log("品质光芒/读概率", ex); }
            if (w <= 0) return -1;                       // 0 = 该道具不会出现在派对盒
            // 相对分档（修复"整盒只有一个颜色"）：权重是"每多少件出一件"的绝对值，
            // 同一盒里常常全部落在同一个绝对阈值里 → 六档只出一档。
            // 改为按本盒出现过的权重做对数插值：最轻（概率最高）→ 泛滥，最重（最稀有）→ 传说。
            // 权重记录在扫描阶段（见 RenderTick：_wMin/_wMax）。
            if (_wMax > _wMin && _wMin > 0) {
                float lo = Mathf.Log(_wMin), hi = Mathf.Log(_wMax);
                // 权重 = "每多少件出一件"：越小越稀有 → 高档位。pos 按稀有度取反。
                float pos = hi > lo ? (Mathf.Log(w) - lo) / (hi - lo) : 1f;
                return Mathf.Clamp(Mathf.RoundToInt((1f - pos) * 5f), 0, 5);
            }
            for (int t = 5; t >= 0; t--) if (w <= TierMaxWeight[t]) return t;
            return 0;
        }

        // ============ 光晕的创建 / 更新 / 移除 ============

        private static Transform Holder(PickableBlock pb) {
            return pb.ArtHolderTransform != null ? pb.ArtHolderTransform : pb.transform;
        }

        private static void HideGlow(PickableBlock pb) {
            Transform g = Holder(pb).Find(GlowName);
            if (g != null) {
                SpriteRenderer sr = g.GetComponent<SpriteRenderer>();
                if (sr != null) sr.enabled = false;
            }
        }

        private static void ApplyGlow(PickableBlock pb, int tier) {
            Transform holder = Holder(pb);
            Transform g = holder.Find(GlowName);
            SpriteRenderer sr;
            if (g == null) {
                GameObject go = new GameObject(GlowName);
                go.layer = 5;                                  // 与 PartyBox:87360 一致
                go.transform.SetParent(holder, false);
                sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = EnsureSprite(tier);
                // 跟道具同一排序层，但排在它后面，绝不遮住本体
                SpriteRenderer refSr = FirstArt(pb);
                sr.sortingLayerName = refSr != null ? refSr.sortingLayerName : "Background 2";
                sr.sortingOrder = (refSr != null ? refSr.sortingOrder : 0) - 100;
                g = go.transform;
            } else {
                sr = g.GetComponent<SpriteRenderer>();
                if (sr == null) return;
            }
            sr.enabled = true;
            sr.sprite = EnsureSprite(tier);
            Material __mat = AddMat(tier);
            if (__mat != null) sr.sharedMaterial = __mat;

            // 包围盒：合并所有 ArtSprites 的世界包围；空则退化成固定尺寸
            Vector3 worldCenter = pb.transform.position;
            float worldSize = 1.6f;
            Bounds b = default(Bounds); bool has = false;
            SpriteRenderer[] arts = pb.ArtSprites;
            if (arts != null) {
                for (int i = 0; i < arts.Length; i++) {
                    if (arts[i] == null || !arts[i].enabled) continue;
                    if (!has) { b = arts[i].bounds; has = true; }
                    else b.Encapsulate(arts[i].bounds);
                }
                if (has) { worldCenter = b.center; worldSize = Mathf.Max(b.size.x, b.size.y); }
            }
            float scaleX = Mathf.Max(0.01f, holder.lossyScale.x);
            // 光晕比包围盒大一圈（×2.6）：光要"散"出去，而不是贴着道具画一圈边。
            float tierSpread = 2.2f + 0.18f * Mathf.Clamp(tier, 0, 5);
            float localSize = worldSize * tierSpread * (_size.Value / 100f) / scaleX;
            g.localPosition = holder.InverseTransformPoint(worldCenter);
            g.localScale = new Vector3(localSize, localSize, 1f);

            float a = TierAlpha[tier] * (_strength.Value / 100f);
            if (_breathe.Value) a *= 0.86f + 0.14f * Mathf.Sin(Time.time * 2.2f + pb.GetInstanceID() * 0.37f);
            Color c = TierColor[tier];
            sr.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(a));
            Material __tm = AddMat(tier);
            if (__tm != null) {
                try { __tm.SetColor("_TintColor", new Color(c.r, c.g, c.b, Mathf.Clamp01(a))); }
                catch (Exception __tc) { SRApi.Guard.Log("品质光芒/tint", __tc); }
            }
        }

        // 每档一份材质：六档共用一份时，同一帧里每件道具都会改写它，
        // 最终所有光晕都变成最后一件的颜色（"只有一个颜色"的另一个来源）。
        private static Material AddMat(int tier) {
            int t = Mathf.Clamp(tier, 0, 5);
            if (_addMats == null) _addMats = new Material[6];
            if (_addMats[t] != null) return _addMats[t];
            if (!_addShaderTried) {
                _addShaderTried = true;
                try { _addShader = Shader.Find("Particles/Additive"); } catch (Exception __sh) { SRApi.Guard.Log("品质光芒/着色器", __sh); }
            }
            if (_addShader == null) return null;                 // 构建裁剪时退回默认 Sprite 材质
            Material m = new Material(_addShader);
            m.hideFlags = HideFlags.HideAndDontSave;
            _addMats[t] = m;
            return m;
        }

        private static Color ParseColor(string hex, Color fallback) {
            Color c;
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out c)) return c;
            return fallback;
        }

        private static SpriteRenderer FirstArt(PickableBlock pb) {
            SpriteRenderer[] a = pb.ArtSprites;
            if (a != null) for (int i = 0; i < a.Length; i++) if (a[i] != null) return a[i];
            return null;
        }

        // ============ 可读性适配：形状贴图 ============

        private static void BindA11y() {
            if (_aTries >= 6 || Time.unscaledTime < _aNext) return;
            _aTries++; _aNext = Time.unscaledTime + 2f;
            foreach (string n in Slots.Names) {
                if (n == "sr.a11y.shapeLabels" && _aShape == null) _aShape = Slots.Require<Func<bool>>(n);
                else if (n == "sr.a11y.highContrast" && _aContrast == null) _aContrast = Slots.Require<Func<bool>>(n);
            }
        }
        private static bool AShape() {
            try { return _aShape != null && _aShape(); }
            catch (Exception ex) { SRApi.Guard.Log("品质光芒/形状", ex); return false; }
        }
        private static bool AContrast() {
            try { return _aContrast != null && _aContrast(); }
            catch (Exception ex) { SRApi.Guard.Log("品质光芒/对比", ex); return false; }
        }

        /// <summary>按档取贴图：可读性页开启「形状图案」时用六档不同纹理，否则退回同一圈柔光。</summary>
        private static Sprite EnsureSoft() {
            if (_glowSprite != null) return _glowSprite;
            const int N = 128;
            Texture2D tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            tex.name = "SR_UCH_RarityGlowTex";
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            Color32[] px = new Color32[N * N];
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++) {
                for (int x = 0; x < N; x++) {
                    float dx = (x - c) / c, dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float t = Mathf.Clamp01(1f - d);
                    t = Mathf.Pow(t, 2.6f);
                    px[y * N + x] = new Color32(255, 255, 255, (byte)(t * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            _glowSprite = Sprite.Create(tex, new Rect(0f, 0f, N, N), new Vector2(0.5f, 0.5f), N / 2f);
            _glowSprite.name = "SR_UCH_RarityGlowSprite";
            _glowSprite.hideFlags = HideFlags.HideAndDontSave;
            return _glowSprite;
        }

        private static Sprite EnsureSprite(int tier) {
            // 六档共用同一张"中心亮、向外平滑衰减"的径向柔光；档位差异靠颜色、尺寸与亮度体现。
            // （以前的环带形状在实机里看起来就是"画圈"，用户明确要求改成道具自身发光。）
            return EnsureSoft();
        }

        /// <summary>图例里的形状小标记：只用 DrawFill 的小矩形拼，不依赖字体符号。</summary>
        private static void DrawShapeMark(Rect r, int tier, Color c) {
            float u = r.width / 6f;
            Color on = new Color(c.r, c.g, c.b, 1f);
            for (int i = 0; i < 6; i++) {
                bool show;
                switch (tier) {
                    case 0: show = (i % 2 == 0); break;
                    case 1: show = true; break;
                    case 2: show = (i == 0 || i == 1 || i == 4 || i == 5); break;
                    case 3: show = (i % 2 == 1); break;
                    case 4: show = (i % 4 < 2); break;
                    default: show = (i == 0 || i == 2 || i == 3 || i == 5); break;
                }
                if (!show) continue;
                float h = (tier == 5 && (i == 2 || i == 3)) ? r.height : r.height * 0.55f;
                SRApi.DrawFill(new Rect(r.x + i * u + u * 0.12f, r.y + (r.height - h) * 0.5f, u * 0.76f, h), on);
            }
        }

        private static void RemoveAll() {
            try {
                PickableBlock[] all = SRApi.FrameObjects<PickableBlock>();
                if (all == null) return;
                for (int i = 0; i < all.Length; i++) {
                    if (all[i] == null) continue;
                    Transform g = Holder(all[i]).Find(GlowName);
                    if (g != null) UnityEngine.Object.Destroy(g.gameObject);
                }
            } catch (Exception ex) { SRApi.Guard.Log("品质光芒/清理", ex); }
            for (int i = 0; i < _tierCount.Length; i++) _tierCount[i] = 0;
            _totalSeen = 0;
        }

        /// <summary>派对盒里出现的最高品质；没有道具时返回 null（扫描每 0.2 秒更新一次）。</summary>
        public static string BriefRarest() {
            if (!_enabled.Value || _totalSeen <= 0) return null;
            for (int t = 5; t >= 0; t--) {
                if (_tierCount[t] <= 0) continue;
                return SRApi.T("本盒最稀有：" + TierZh[t] + " ×" + _tierCount[t] + "（共 " + _totalSeen + " 件）",
                               "Rarest in box: " + TierEn[t] + " x" + _tierCount[t] + " (" + _totalSeen + " items)");
            }
            return null;
        }

        // ============ 页面 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        private void RenderPage() {
            CfgRow(_enabled);
            CfgRow(_strength);
            CfgRow(_size);
            CfgRow(_breathe);
            if (_tierColors != null) for (int i = 0; i < _tierColors.Length; i++) CfgRow(_tierColors[i]);

        }
    }
}
