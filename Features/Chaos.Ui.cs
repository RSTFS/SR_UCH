// 混乱牌组（树屋选举版）UI + 房内 mod 清单。
// 网络方案：发真实的 TabletRule + Value，由游戏自己的代码写进 Modifiers，房主安装即可带全场。
// 房主被 !IsHost 挡住不执行那段，故房主须本地双写一次；Value2 高位放牌组魔数供 UI 解析。
// 高位标记不干扰原生规则：游戏的规则分支只读 Value / Valueb。
// 不进池的规则：GameSpeed（改全局速度）、CharacterSize / CameraFlip / Doomsday（碰相机或要重建绘制）。
// 房内 mod 清单复用 MsgGameRuleSet 以 TabletRule.None 承载；Value 放本机 networkNumber（conn 无法区分发件人）。
// 不新增 Harmony 补丁 / msgType / 反射登记；状态只在主线程回调里读写。

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using GameEvent;   // GameEventManager / IGameEventListener 都在 namespace GameEvent 内
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    public partial class Chaos {
        // ============ 树屋选牌页 ============

        private static void Line(string zh, string en) {
            GUILayout.Label(SRApi.T(zh, en), SRApi.Ctl.LabelWrap);
        }

        // ============ 当前牌面：一张牌画成一张卡片（名称 + 效果 + 增益/减益色边） ============

        private static GUIStyle _cardTitle, _cardEffect;
        private static readonly Color CardBuff = new Color(0.42f, 0.88f, 0.54f, 1f);   // 增益：绿
        private static readonly Color CardDebuff = new Color(0.96f, 0.48f, 0.40f, 1f); // 减益：红橙
        private static readonly Color CardNeutral = new Color(0.56f, 0.72f, 1f, 1f);   // 中性/混合：蓝

        private static void EnsureCardStyles() {
            if (_cardTitle != null) return;
            _cardTitle = new GUIStyle(GUI.skin.label);
            _cardTitle.fontStyle = FontStyle.Bold;
            _cardTitle.wordWrap = false;
            _cardTitle.clipping = TextClipping.Clip;
            _cardTitle.alignment = TextAnchor.MiddleLeft;
            _cardTitle.padding = new RectOffset(0, 0, 0, 0);
            _cardTitle.margin = new RectOffset(0, 0, 0, 0);
            _cardTitle.normal.textColor = new Color(0.96f, 0.97f, 1f, 1f);
            _cardEffect = new GUIStyle(GUI.skin.label);
            _cardEffect.wordWrap = true;
            _cardEffect.clipping = TextClipping.Clip;
            _cardEffect.alignment = TextAnchor.UpperLeft;
            _cardEffect.padding = new RectOffset(0, 0, 0, 0);
            _cardEffect.margin = new RectOffset(0, 0, 0, 0);
            _cardEffect.normal.textColor = new Color(0.78f, 0.81f, 0.87f, 1f);
        }

        // 牌面边框色：变强 = 增益（绿），变弱 = 减益（红），两边都占/纯整活 = 中性（蓝）。
        private static Color CardTint(CardDef c) {
            // 分类规则在 ChaosLogic（无 Unity 依赖，有单元测试）；这里只做颜色映射。
            int kind = ChaosLogic.TintKind(c.IsBool, c.Field, c.Min);
            return kind == 0 ? CardBuff : kind == 1 ? CardDebuff : CardNeutral;
        }

        private void RenderActiveCards() {
            int n = EffectiveIndexes();
            if (n <= 0) {
                if (_enabled != null && !_enabled.Value) Line("（本机未启用）", "(off on this machine)");
                else Line("（未生效）", "(none)");
                return;
            }
            EnsureCardStyles();
            float h = SRApi.Ctl.Sc(52f);
            float gap = SRApi.Ctl.Sc(6f);
            // 先占住整行高度：抽到 0/1/多张时页面不会跳，也不会因为卡片内容变化抖高度。
            Rect area = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
            float cw = Mathf.Max(SRApi.Ctl.Sc(72f), Mathf.Floor((area.width - gap * (n - 1)) / Mathf.Max(1, n)));
            for (int k = 0; k < n; k++) {
                int i = _effIdx[k];
                DrawCard(new Rect(area.x + k * (cw + gap), area.y, cw, h), Cards[_active[i].Id], _active[i].Param);
            }
            DrawCardLegend();
        }

        // 三色小图例：色边含义画出来比写在卡上直观（详细说明仍在分区提示里）。
        private static void DrawCardLegend() {
            float h = SRApi.Ctl.Sc(14f);
            float chip = SRApi.Ctl.Sc(10f);
            Rect area = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
            _cardEffect.fontSize = Mathf.RoundToInt(SRApi.Ctl.Sc(10f));
            float x = area.x;
            float y = area.y + (h - chip) * 0.5f;
            CardLegendItem(ref x, y, chip, CardBuff, "增益", "buff");
            CardLegendItem(ref x, y, chip, CardDebuff, "减益", "debuff");
            CardLegendItem(ref x, y, chip, CardNeutral, "中性", "neutral");
        }

        private static void CardLegendItem(ref float x, float y, float chip, Color tint, string zh, string en) {
            SRApi.DrawFill(new Rect(x, y, chip, chip), tint);
            string text = SRApi.T(zh, en);
            float w;
            try { w = _cardEffect.CalcSize(new GUIContent(text)).x; } catch { w = SRApi.Ctl.Sc(36f); }
            GUI.Label(new Rect(x + chip + SRApi.Ctl.Sc(3f), y - SRApi.Ctl.Sc(2f), w + SRApi.Ctl.Sc(2f), chip + SRApi.Ctl.Sc(4f)), text, _cardEffect);
            x += chip + SRApi.Ctl.Sc(3f) + w + SRApi.Ctl.Sc(12f);
        }

        private static void DrawCard(Rect r, CardDef c, int param) {
            Color tint = CardTint(c);
            float bw = Mathf.Max(1f, SRApi.Ctl.Sc(2f));
            // 底色 + 四边色条（不用圆角面板：卡片每帧重画，纯矩形最省）。
            SRApi.DrawFill(r, new Color(0.10f, 0.115f, 0.15f, 0.94f));
            SRApi.DrawFill(new Rect(r.x, r.y, r.width, bw), tint);
            SRApi.DrawFill(new Rect(r.x, r.yMax - bw, r.width, bw), tint);
            SRApi.DrawFill(new Rect(r.x, r.y, bw, r.height), tint);
            SRApi.DrawFill(new Rect(r.xMax - bw, r.y, bw, r.height), tint);

            float pad = SRApi.Ctl.Sc(6f);
            float titleH = SRApi.Ctl.Sc(17f);
            Rect inner = new Rect(r.x + pad, r.y + SRApi.Ctl.Sc(4f), r.width - pad * 2f, r.height - SRApi.Ctl.Sc(9f));
            _cardTitle.fontSize = Mathf.RoundToInt(SRApi.Ctl.Sc(13f));
            _cardEffect.fontSize = Mathf.RoundToInt(SRApi.Ctl.Sc(10f));
            GUI.Label(new Rect(inner.x, inner.y, inner.width, titleH), SRApi.T(c.Zh, c.En), _cardTitle);
            string effect = SRApi.T(c.TipZh, c.TipEn);
            if (!c.IsBool) effect += " · " + SRApi.T("值 ", "value ") + param;
            GUI.Label(new Rect(inner.x, inner.y + titleH, inner.width, inner.height - titleH), effect, _cardEffect);
        }

        private void RenderPage() {
            // 配置项一律走核心统一渲染；核心层没有裸 Toggle/Slider/Label 控件。
            // 限制模式（非派对/创意）下整页配置置灰：触发即跳过，不弹提示。
            using (SRApi.Ctl.Disabled(!ModeAllowed())) {
                CfgRow(_enabled);
                CfgRow(_everyRounds);
                CfgRow(_maxActive);
                CfgRow(_announce);
            }
            if (!ModeAllowed())
                Line("仅派对 / 创意模式可用。", "Party / Creative only.");

            // 本机开关关掉时牌面只作跟踪（房主规则仍生效），必须明说，不能让页面假称“未生效”。
            if (_enabled != null && !_enabled.Value)
                Line("本机开关已关：这里只显示收到的牌面，房主规则仍会生效。",
                     "Switch is off on this machine: cards are display-only; host rules still apply.");
            SRApi.Ctl.SectionTitle(SRApi.T("当前牌面", "Active cards"),
                SRApi.T("本回合生效的规则牌：绿边 = 增益，红边 = 减益，蓝边 = 中性。",
                        "Rules in effect this round: green = buff, red = debuff, blue = neutral."));
            RenderActiveCards();
            using (SRApi.Ctl.Disabled(!IsAuthority || _enabled == null || !_enabled.Value)) {
                SRApi.Ctl.HotkeyButton("chaos.reroll", SRApi.T("重抽", "Draw"),
                                       SRApi.T("仅房主 / 离线有效", "Host or offline only"), DrawAndBroadcast);
            }
        }

        // 牌池单独一页：按「数值规则 / 布尔规则」分组勾选 + 批量操作（全选/全不选/仅增益/仅减益）。

        private void RenderPoolPage() {
            // 四个批量操作同排一行（紧凑按钮，避免占掉过多列表空间）。
            GUILayout.BeginHorizontal();
            float bh = SRApi.Ctl.Sc(24f), bw = SRApi.Ctl.Sc(64f);
            if (SRApi.Ctl.Button(SRApi.T("全选", "All"), SRApi.T("勾选全部牌", "Enable every card"), bw, bh)) SetAllPools(true);
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("全不选", "None"), SRApi.T("全部取消（会再自动勾回第一张）", "Disable all (the first card is re-enabled)"), bw, bh)) SetAllPools(false);
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("仅增益", "Buffs"), SRApi.T("只保留对自己有利的牌（高跳/快跑/跳舞无敌等）", "Keep only beneficial cards"), bw, bh)) KeepBeneficial();
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("仅减益", "Debuffs"), SRApi.T("只保留让人受限/变弱的牌（低重力/慢速/禁墙跳等）", "Keep only weakening cards"), bw, bh)) KeepHarmful();
            GUILayout.Space(SRApi.Ctl.Sc(4f));
            if (SRApi.Ctl.Button(SRApi.T("反选", "Invert"), SRApi.T("勾选状态全部反转（至少留一张）", "Invert every checkbox (keep at least one)"), bw, bh)) InvertPools();
            GUILayout.EndHorizontal();
            int __on = 0;
            for (int i = 0; i < Cards.Length; i++) if (_pool[i] != null && _pool[i].Value) __on++;
            // Line 的两个参数分别是中文与英文：之前两处都传了同一句中英混合串，英文界面会显示中文。
            Line("已启用 " + __on + " / " + Cards.Length + " 张；勾选后才会进入随机池。",
                 __on + " / " + Cards.Length + " cards enabled; only checked cards can be drawn.");
            GUILayout.Space(SRApi.Ctl.Sc(6f));

            RenderPoolGroup(true);    // 数值规则
            RenderPoolGroup(false);   // 布尔规则
        }

        // 分组渲染：按数值/布尔规则分两块（顺序固定，结构不随勾选变化）。
        private void RenderPoolGroup(bool numeric) {
            SRApi.Ctl.SectionTitle(SRApi.T(numeric ? "数值规则" : "布尔规则", numeric ? "Numeric rules" : "Boolean rules"));
            for (int i = 0; i < Cards.Length; i++) {
                if (Cards[i].IsBool == numeric || _pool[i] == null) continue;   // numeric=true 显示数值牌（IsBool=false）
                CardDef c = Cards[i];
                string ours = SRApi.T(c.Zh, c.En);
                string gm = GameRuleName(c.Rule);
                // 同一规则有多张牌（如低/高重力）时游戏名相同，追加本功能的档位名区分。
                string label = string.IsNullOrEmpty(gm) ? ours : (gm == ours ? gm : gm + " · " + ours);
                SRApi.LocDynamic(_pool[i], label, label);   // 行名跟随游戏语言（核心节流重注册）
                CfgRow(_pool[i]);   // 标准复选框行（不是按钮）
                // 数值牌：直接在行下方给出取值区间（可圈定），勾选状态与区间一起决定抽到什么。
                if (!Cards[i].IsBool) RenderParamRange(i, c);
            }
        }

        // 数值牌的取值区间行：最小值 / 最大值两个数字框。
        private void RenderParamRange(int i, CardDef c) {
            try {
                if (_paramMin == null || _paramMax == null) return;
                if (i >= _paramMin.Length || i >= _paramMax.Length) return;
                if (_paramMin[i] == null || _paramMax[i] == null) return;
                SRApi.Ctl.RowSlider(SRApi.T("数值下限", "Min value"),
                    SRApi.T("这张牌抽到的最小档位\n与该牌的上限一起圈定取值范围",
                            "lowest tier this card can roll\nwith the max, defines the allowed range"),
                    _paramMin[i], 0f, 8f, "0");
                SRApi.Ctl.RowSlider(SRApi.T("数值上限", "Max value"),
                    SRApi.T("这张牌抽到的最大档位\n低于下限时会自动按一下限处理",
                            "highest tier this card can roll\nfalls back to the min when lower"),
                    _paramMax[i], 0f, 8f, "0");
                // 防止用户把上限调到下限以下：读取时已夹紧，这里给一行说明。
                if (_paramMax[i].Value < _paramMin[i].Value) {
                    GUILayout.Label(SRApi.T("上限低于下限，抽取时按上限=下限处理。",
                        "Max is below min; the roll uses max = min."), SRApi.Ctl.Label);
                }
            } catch (Exception __rp) { SRApi.Guard.Log("混乱牌组/数值区间行", __rp); }
        }

        private void SetAllPools(bool on) {
            try {
                for (int i = 0; i < _pool.Length; i++) {
                    if (_pool[i] == null) continue;
                    _pool[i].Value = on;
                }
                if (!on && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;   // 至少留一张
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/批量勾选", ex); }
        }

        // 规则名优先用游戏自己的本地化文本（跟随游戏语言），取不到再回退本功能的中英文名。
        private static readonly Dictionary<TabletRule, string> _ruleNameCache = new Dictionary<TabletRule, string>();
        private static string GameRuleName(TabletRule rule) {
            string cached;
            if (_ruleNameCache.TryGetValue(rule, out cached)) return cached;
            string found = null;
            try {
                string en = rule.ToString();
                // 游戏用 I2 Localization；术语可能是枚举名，也可能带前缀，逐个候选探测。
                string[] cands = new string[] { en, "Modifiers/" + en, "Modifier_" + en, "RULE_" + en, "Rule_" + en };
                for (int i = 0; i < cands.Length; i++) {
                    string t2 = I2.Loc.LocalizationManager.GetTranslation(cands[i], false, 0, true, true, null, null, false);
                    if (!string.IsNullOrEmpty(t2) && t2 != cands[i]) { found = t2; break; }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("混乱牌组/游戏规则名", __ex); }
            _ruleNameCache[rule] = found;
            return found;
        }

        // 牌池批量筛选用的倾向分类（与 Cards 顺序一一对应）：+1 增益 / 0 中性 / -1 减益。
        private static readonly sbyte[] CardMood = new sbyte[] {
            1, -1, -1, -1, -1, -1, -1,    // 低重力(有益) / 高重力 / 弱跳跃 / 慢冲刺 / 慢平台 / 禁墙跳 / 镜像
             1, -1, -1, -1,               // 跳舞无敌 / 玩家互撞 / 无摩擦 / 慢射速
             1,  1,  1,                   // 高跳跃 / 快冲刺 / 快平台
            -1, -1                       // 龟速冲刺 / 停滞平台
        };

        private void ApplyPoolMood(int sign) {
            try {
                for (int i = 0; i < Cards.Length; i++) {
                    if (_pool[i] == null) continue;
                    _pool[i].Value = i < CardMood.Length && CardMood[i] == sign;
                }
                bool any = false;
                for (int i = 0; i < _pool.Length; i++) if (_pool[i] != null && _pool[i].Value) { any = true; break; }
                if (!any && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;   // 至少留一张
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/按倾向筛选", ex); }
        }

        private void InvertPools() {
            try {
                bool any = false;
                for (int i = 0; i < _pool.Length; i++) {
                    if (_pool[i] == null) continue;
                    _pool[i].Value = !_pool[i].Value;
                    if (_pool[i].Value) any = true;
                }
                if (!any && _pool.Length > 0 && _pool[0] != null) _pool[0].Value = true;
            } catch (Exception ex) { SRApi.Guard.Log("混乱牌组/反选", ex); }
        }

        private void KeepBeneficial() { ApplyPoolMood(1); }
        private void KeepHarmful() { ApplyPoolMood(-1); }
    }
}
