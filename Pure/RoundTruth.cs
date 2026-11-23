// 真回合判定：游戏 RoundNumber 在重载关卡 / 刷新派对盒时会额外 +1（ToPlaceMode 被直接调用），
// 但那一帧并没有结算记分板。故只有观察到 ScoreboardEvent(Showing / AfterTally) 才接受回合增量。
// 纯函数层：不引用 Unity / BepInEx / HarmonyLib / 游戏类型；HUD 与混乱牌组共用同一份判定。
using System;

namespace SR_UCH.Pure {
    internal struct RoundTruthState {
        internal int TrueRound;     // 已完成 + 正在进行中最大的那个回合序号
        internal int Pending;       // 未确认的候选值（只升不降）
        internal bool TallySeen;    // 是否观察到结算证据
        internal bool InTally;      // 正处在"结算展示期"：本回合已打完、下一回合还没开始
    }

    internal static class RoundTruth {
        // 结算证据：由 ScoreboardEvent 监听器喂入，幂等。
        internal static void NoteTally(ref RoundTruthState s) { s.TallySeen = true; s.InTally = true; }

        // 结算板收起（下一回合真正开始）：退出展示期。
        internal static void EndTally(ref RoundTruthState s) { s.InTally = false; }

        // HUD 展示用：结算展示期显示"刚打完的那一轮"，否则显示当前回合。
        // 结算期 RoundNumber 已提前指向下一轮，直接显示会让玩家看到"第 2/N"但第 1 轮结算板还在。
        // maxRound>0 时不再回退：最后一轮打完仍显示满轮（显示 max-1 会更像 bug）。
        internal static int DisplayRound(RoundTruthState s, int maxRound) {
            if (!s.InTally || s.TrueRound <= 1) return s.TrueRound;
            // TrueRound 已到上限 = 刚打完的就是最后一轮，再回退会显示 max-1，像少算一轮。
            if (maxRound > 0 && s.TrueRound >= maxRound) return Math.Min(s.TrueRound, maxRound);
            return s.TrueRound - 1;
        }

        // 换对局 / 换 GameControl / 功能复位：全部归零。
        internal static void Reset(ref RoundTruthState s) { s = new RoundTruthState(); }

        // 只清证据、不动回合数（HUD 换场景时保留显示值，避免提示闪烁）。
        internal static void ClearTally(ref RoundTruthState s) { s.TallySeen = false; s.InTally = false; }

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
                    // 结算在前、+1 在后：接受增量，但**仍留在展示期** ——
                    // 此刻结算板还挂在屏幕上，HUD 该显示刚打完的那一轮，而不是提前显示下一轮。
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
                s.InTally = false;
            }

            return s.TrueRound != before;
        }
    }
}
