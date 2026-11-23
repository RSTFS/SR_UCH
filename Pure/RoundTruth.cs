// 真回合判定：游戏 RoundNumber 在重载关卡 / 刷新派对盒时会额外 +1（ToPlaceMode 被直接调用），
// 但那一帧并没有结算记分板。故只有观察到 ScoreboardEvent(Showing / AfterTally) 才接受回合增量。
// 纯函数层：不引用 Unity / BepInEx / HarmonyLib / 游戏类型；HUD 与混乱牌组共用同一份判定。
using System;

namespace SR_UCH.Pure {
    internal struct RoundTruthState {
        internal int TrueRound;     // 对外展示 / 用于抽牌间隔的真实回合
        internal int Pending;       // 未确认的候选值（只升不降）
        internal bool TallySeen;    // 是否观察到结算证据
    }

    internal static class RoundTruth {
        // 结算证据：由 ScoreboardEvent 监听器喂入，幂等。
        internal static void NoteTally(ref RoundTruthState s) { s.TallySeen = true; }

        // 换对局 / 换 GameControl / 功能复位：全部归零。
        internal static void Reset(ref RoundTruthState s) { s = new RoundTruthState(); }

        // 只清证据、不动回合数（HUD 换场景时保留显示值，避免提示闪烁）。
        internal static void ClearTally(ref RoundTruthState s) { s.TallySeen = false; }

        // 每帧一步：candidate = GameControl.RoundNumber；challenge = 挑战模式（无结算事件、无上限）。
        // 返回 TrueRound 是否发生变化（即真的推进了一个回合）。
        internal static bool Step(ref RoundTruthState s, int candidate, bool challenge) {
            int before = s.TrueRound;

            if (challenge) {
                // 挑战模式每死一次即一次重试：只接受「前进」或「回到 1」，防抖动重播。
                if (candidate > 0 && (candidate > s.TrueRound || candidate <= 1)) {
                    s.TrueRound = candidate;
                    s.Pending = 0;
                    s.TallySeen = false;
                }
                return s.TrueRound != before;
            }

            // 回退：只有确实换了新对局（回到 1）才接受，否则保留较大值。
            if (candidate < s.TrueRound && candidate <= 1) {
                s.TrueRound = candidate;
                s.Pending = 0;
                s.TallySeen = false;
            }

            if (s.TrueRound <= 0 && candidate > 0) {
                // 开局第一回合不用等结算；证据一次性消费，避免残留让下一次虚增被误认。
                s.TrueRound = candidate;
                s.Pending = 0;
                s.TallySeen = false;
            } else if (candidate > s.TrueRound) {
                if (candidate == s.TrueRound + 1 && s.TallySeen) {
                    // 结算在前、+1 在后：直接接受。
                    s.TrueRound = candidate;
                    s.Pending = 0;
                    s.TallySeen = false;
                } else if (candidate > s.Pending) {
                    // 没有证据：只挂起（可能是刷新派对盒的额外 +1）。
                    s.Pending = candidate;
                }
            }

            // +1 在前、结算在后：接受此前挂起的候选。
            if (s.TallySeen && s.Pending > s.TrueRound) {
                s.TrueRound = s.Pending;
                s.Pending = 0;
                s.TallySeen = false;
            }

            return s.TrueRound != before;
        }
    }
}
