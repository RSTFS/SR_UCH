using System;
using BepInEx.Configuration;
using SR_UCH.Gating;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：Progression（进度解锁：单一 Requirement + 四个执行点同源）====
//
// 重构要点（提示词 T3，缺陷 1）：
//  「进度锁定」这一个概念原来存在 5 份独立实现（UI 灰显 / tooltip / 写入拦截 /
//  定时复位 / 业务判断，行号见提示词 4.1）。现在统一为**一个** ProgressionRequirement，
//  由 AppliesTo 自动投影到 能力门 / 可见门 / 可变门；配置条目 → 进度组的映射也只剩一处
//  （ProgressionGroupOf），不再有散落的硬编码清单。
//
// 行为变更（已经用户确认）：**取消“每分钟旁路强制复位”**。
//  原来 ForceLockedConfigs 每 60 秒直接把未解锁项的 e.BoxedValue 写回 false，
//  绕过了 SetValue 的可变门拦截，造成前后不一致（提示词缺陷 2：手动改 cfg 开启后
//  UI 显示已开、补丁立即生效，最多 60 秒后被悄悄关掉）。现在只做**运行时门控**：
//   · 能力门：未解锁功能补丁直接不执行；
//   · 可见门：UI 灰显 + 原因 tooltip；
//   · 可变门：SetValue 拒绝改开。
//  不再静默改写玩家配置。

        // 进度解锁要求：一个组一个实例。进度组永不接受豁免
        //（IgnoreModeLimit / IgnoreHostLimit 不影响它）——这是有意设计（提示词 5.4）。
        internal sealed class ProgressionRequirement : IRequirement {
            private readonly ProgressGroup _group;
            private readonly float _timeThreshold;
            private readonly float _distThreshold;

            public ProgressionRequirement(ProgressGroup group, float timeThreshold, float distThreshold) {
                _group = group;
                _timeThreshold = timeThreshold;
                _distThreshold = distThreshold;
            }

            public GateScope AppliesTo {
                get { return GateScope.Capability | GateScope.Visibility | GateScope.Mutability; }
            }

            // 进度解锁永不豁免（有意设计）。
            public bool Overridable { get { return false; } }

            public GateResult Evaluate(in GateContext ctx) {
                if (ctx.Has(_group)) return GateResult.Ok;
                return GateResult.Deny(ReasonKey, _timeThreshold, _distThreshold);
            }

            public string ReasonKey {
                get { return _group == ProgressGroup.B ? "gate.progress.B" : "gate.progress.A"; }
            }

            public ProgressGroup Group { get { return _group; } }
        }

        private static readonly ProgressionRequirement _progA =
            new ProgressionRequirement(ProgressGroup.A, Experiments.UnlockTimeSeconds, Experiments.UnlockDistanceMeters);
        private static readonly ProgressionRequirement _progB =
            new ProgressionRequirement(ProgressGroup.B, Experiments.UnlockBTimeSeconds, Experiments.UnlockBDistanceMeters);

        private static ProgressionRequirement ProgressionReq(ProgressGroup group) {
            return group == ProgressGroup.B ? _progB : _progA;
        }

        // 唯一的「配置条目 → 进度组」映射。新增受进度限制的功能时**只改这里**。
        private static ProgressGroup ProgressionGroupOf(ConfigEntryBase entry) {
            try {
                if (entry == null || entry.Definition == null) return ProgressGroup.None;
                string sec = entry.Definition.Section;
                string key = entry.Definition.Key;
                //A 组：建造增强（无视碰撞 / 自由放置）、树屋问号
                if (sec == "Builder Enhancements" && (key == "Collision Override" || key == "Grid Override"))
                    return ProgressGroup.A;
                if (sec == "实验" && key == "Question Mark")
                    return ProgressGroup.A;
                //B 组：方块破坏
                if (sec == "Destroy Blocks" && key == "Enabled")
                    return ProgressGroup.B;
            } catch { }
            return ProgressGroup.None;
        }

        // 某个配置条目当前是否被进度锁定（供 可见门 / 可变门 使用）。
        public static bool ProgressionLocked(ConfigEntryBase entry) {
            ProgressGroup g = ProgressionGroupOf(entry);
            if (g == ProgressGroup.None) return false;
            return !EvaluateGate(ProgressionReq(g), GateScope.Visibility).Allowed;
        }

        // 业务侧（能力门）查询：某进度组此刻是否允许执行。
        // 取代原来散落的 Experiments.IsProgressionUnlocked()/IsProgressionUnlockedB() 直判，
        // 并顺带修掉「GameControl.Update 前缀里每帧读存档」的性能缺陷（这里读每帧快照缓存）。
        public static bool ProgressionAllows(ProgressGroup group) {
            return EvaluateGate(ProgressionReq(group), GateScope.Capability).Allowed;
        }

        // 拒绝原因文案（单一来源，供条目 tooltip 使用）。
        public static string ProgressionReasonText(ConfigEntryBase entry) {
            ProgressGroup g = ProgressionGroupOf(entry);
            if (g == ProgressGroup.None) return null;
            GateResult r = EvaluateGate(ProgressionReq(g), GateScope.Visibility);
            if (r.Allowed) return null;
            if (g == ProgressGroup.B)
                return T("B组未解锁：需游戏时长 > 52时 或 奔跑长度 > 100000米（实验页查看进度）",
                         "Group B locked: need >52h playtime or >100000m run distance (see Experiments page)");
            return T("A组未解锁（建造的无视碰撞 / 自由放置）：需游戏时长 > 17时16分18秒 或 奔跑长度 > 52000米（实验页查看进度）",
                     "Group A locked (Collision Override / Free Placement): need >17h16m18s playtime or >52000m run distance (see Experiments page)");
        }

	}
}
