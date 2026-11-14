// 混乱牌组的纯逻辑：无 Unity / BepInEx 依赖，便于在单元测试里直接覆盖（tests 项目会编译本文件）。
namespace SR_UCH.Tweaks {
    internal static class ChaosLogic {
        // 牌面分类：0 = 增益（绿边）/ 1 = 减益（红边）/ 2 = 中性（蓝边）。
        // 数值字段 id 与 Chaos.CardDef 的 F* 常量一致；改动时两边要同步（测试会锁行为）。
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
                case 4: return 1;                   // 慢射速 debuff
                default: return 2;
            }
        }
    }
}
