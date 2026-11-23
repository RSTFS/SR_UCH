// 真回合判定：直接采用游戏自身的语义。
//
// 依据（Assembly-CSharp 反编译源码）：
//   VersusControl.ToPlaceMode() 里 `roundNumber++`，即“进入新一轮建造阶段”时加 1；
//   AfterScoreBoardCoroutine() 里用 `MaxRounds - roundNumber` 算 lastRoundsToGo；
//   结算流程为 ShowScoreboard → AfterScoreBoard →（nextPhase=PLACE）→ ToPlaceMode，
//   即结算期间 roundNumber **仍停在刚打完的那一轮**，下一轮开始时才加 1。
//
// 故 roundNumber 本身就是“当前回合”，不需要推测、不需要结算期回退。
// 只需两件事：
//   1. 换新对局时归零（roundNumber 会回到 1）；
//   2. 挑战模式每死一次重试，同样按前进处理。
// 纯函数层：不引用 Unity / BepInEx / HarmonyLib / 游戏类型。
using System;

namespace SR_UCH.Pure {
    internal struct RoundTruthState {
        internal int TrueRound;     // 当前回合（与游戏 roundNumber 同义，只进不退）
    }

    internal static class RoundTruth {
        // 换对局 / 换 GameControl / 功能复位：全部归零。
        internal static void Reset(ref RoundTruthState s) { s = new RoundTruthState(); }

        // 兼容旧调用点：回合证据已不需要，保留空实现避免调用方改动。
        internal static void NoteTally(ref RoundTruthState s) { }
        internal static void ClearTally(ref RoundTruthState s) { }
        internal static void EndTally(ref RoundTruthState s) { }

        // 每帧一步：candidate = GameControl.RoundNumber（= 当前回合）。
        // 只接受前进，或回到 1（换局）；其余抖动一律忽略。
        // 返回 TrueRound 是否变化（供混乱牌组计“推进了一回合”）。
        internal static bool Step(ref RoundTruthState s, int candidate, bool challenge) {
            int before = s.TrueRound;
            if (candidate <= 0) return false;
            // 换局：roundNumber 回到 1（挑战模式每次重试也是 1 起）。
            if (candidate <= 1 && s.TrueRound != candidate) { s.TrueRound = candidate; return s.TrueRound != before; }
            // 前进：只认严格更大的值，避免关卡重载导致的瞬时回退把显示抹掉。
            if (candidate > s.TrueRound) s.TrueRound = candidate;
            return s.TrueRound != before;
        }

        // HUD 展示用：直接就是当前回合，上限按 MaxRounds 钳住。
        internal static int DisplayRound(RoundTruthState s, int maxRound) {
            if (maxRound > 0) return Math.Min(s.TrueRound, maxRound);
            return s.TrueRound;
        }
    }
}
