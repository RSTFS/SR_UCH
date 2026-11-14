// 回合 HUD · 内容定义表。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    public partial class HUD {
        private sealed class ContentDef {
            public string Key, ColorKey, LabelZh, LabelEn, DescZh, DescEn;
        }

        private static readonly ContentDef[] _content = new ContentDef[] {
            new ContentDef {
                Key = "Show Phase", ColorKey = "Color Phase",
                LabelZh = "阶段名", LabelEn = "Phase name",
                DescZh = "阶段名\n在 HUD 显示当前阶段（建造、游玩、决胜）\n关闭后隐藏该行",
                DescEn = "Phase name\nshows the current phase in the HUD (build, play, sudden death)\nhidden when off"
            },
            new ContentDef {
                Key = "Show Countdown", ColorKey = "Color Countdown",
                LabelZh = "倒计时", LabelEn = "Countdown",
                DescZh = "倒计时\n显示阶段倒计时，读不到时长时显示已用时间\n关闭后隐藏该行",
                DescEn = "Countdown\nphase countdown, or elapsed time when no duration is known\nhidden when off"
            },
            new ContentDef {
                Key = "Show Round", ColorKey = "Color Round",
                LabelZh = "回合进度", LabelEn = "Round progress",
                DescZh = "回合进度\n普通模式显示「回合：当前/最大」，挑战模式显示「重试：N」\n取不到回合信息时隐藏该行",
                DescEn = "Round progress\nround current/max, or retry count in challenge mode\nhidden when round info is unavailable"
            },
            new ContentDef {
                Key = "Show Level", ColorKey = "Color Level",
                LabelZh = "关卡/模式", LabelEn = "Level / mode",
                DescZh = "关卡/模式\n显示「关卡名 · 模式」，不带前缀\n取不到时隐藏该行",
                DescEn = "Level / mode\nshows level name and mode without a prefix\nhidden when unavailable"
            },
            new ContentDef {
                Key = "Show Win Score", ColorKey = "Color Win Score",
                LabelZh = "获胜", LabelEn = "Win",
                DescZh = "获胜目标\n显示游戏设置里的获胜目标分\n读不到时显示 250；关闭后隐藏该行",
                DescEn = "Win target\nwinning score from the game settings\nfalls back to 250; hidden when off"
            },
            new ContentDef {
                Key = "Extra Line 1", ColorKey = "Color Extra 1",
                LabelZh = "额外信息行 1", LabelEn = "Extra line 1",
                DescZh = "额外信息行 1\n自定义文本，按原文显示（不翻译）\n留空则不显示",
                DescEn = "Extra line 1\ncustom text shown as typed (not translated)\nhidden when empty"
            },
            new ContentDef {
                Key = "Extra Line 2", ColorKey = "Color Extra 2",
                LabelZh = "额外信息行 2", LabelEn = "Extra line 2",
                DescZh = "额外信息行 2\n自定义文本，按原文显示（不翻译）\n留空则不显示",
                DescEn = "Extra line 2\ncustom text shown as typed (not translated)\nhidden when empty"
            }
        };

        // ---- 配置项 ----
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _fade;
        private static ConfigEntry<KeyCode> _showKey;
        private static ConfigEntry<HudLayout> _layout;
        private static ConfigEntry<int> _fontSize;
        private static ConfigEntry<float> _opacity;
        private static ConfigEntry<float> _bgOpacity;
        private static ConfigEntry<int> _warnSeconds;
        private static ConfigEntry<bool> _blink;
        private static ConfigEntry<bool> _roundNotice;
        private static ConfigEntry<DisplayMode> _displayMode;
        private static ConfigEntry<bool> _showPhase;
        private static ConfigEntry<bool> _showCountdown;
        private static ConfigEntry<bool> _showRound;
        private static ConfigEntry<bool> _showLevel;
        private static ConfigEntry<bool> _showWin;
    }
}
