// 混乱牌组纯逻辑：无 Unity / BepInEx 依赖，tests 工程直接编译本文件。
namespace SR_UCH.Tweaks {
    internal static class ChaosLogic {
        // 0 增益（绿边）/ 1 减益（红边）/ 2 中性（蓝边）；field 与 CardDef.F* 同步。
        public static int TintKind(bool isBool, int field, int min) {
            if (isBool) {
                if (field == 9) return 0;                                  // 跳舞无敌
                if (field == 8 || field == 10 || field == 12 || field == 13) return 1; // 禁墙跳 / 镜像 / 互撞 / 无摩擦
                return 2;
            }
            switch (field) {
                case 0: return min <= 1 ? 0 : 1;    // 低重力 buff，高重力/超重 debuff
                case 1: return min >= 2 ? 0 : 1;    // 高跳跃 buff，弱跳跃 debuff
                case 2:                             // 冲刺：快 buff，慢/龟速 debuff
                case 3: return min >= 2 ? 0 : 1;    // 平台：快 buff，慢/停滞 debuff
                default: return 2;
            }
        }
    }
}
