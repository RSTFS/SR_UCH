// 功能层配置登记单一入口：每个配置项必然成对出现的 LocKey + LocDesc 收敛成一次调用。
// 说明文字仍以字面量传入，由 TXT-006 守卫按 2-4 行格式静态校验，登记口径不变。
using BepInEx.Configuration;

namespace SR_UCH.Tweaks {
    internal static class TweakCfg {
        internal static void Entry(string sec, string key, string zh, string en,
                                   string descZh, string descEn) {
            SRApi.LocKey(sec, key, zh, en);
            SRApi.LocDesc(sec, key, descZh, descEn);
        }

        // 配置行统一渲染入口：原先 4 个功能各写一份同样的转发。
        internal static void Row(ConfigEntryBase e) { SRApi.Ctl.DrawEntryRow(e); }
    }
}
