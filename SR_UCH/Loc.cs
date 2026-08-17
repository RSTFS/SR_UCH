using UnityEngine;

namespace SR_UCH.Tweaks {
    //============================================================
    // Loc = 双语本地化统一入口（长期维护约定）
    // -----------------------------------------------------------
    // · 新增功能 / 新分区代码里的用户可见文本，一律通过 Loc.T(zh, en)
    //   提供中英文，不要在业务代码里自己拼中英文或硬编码。
    // · 界面语言由 设置页 → Language 切换（ModManager 驱动），运行时立即生效；
    //   EX 附加页始终中文是 ModManager 内部行为，Loc 无需感知。
    // · 枚举 / 键位 / 关卡名等翻译入口也集中在此，方便统一扩展。
    // · 翻译表（分区/条目/描述/枚举）集中在 ModManager.Localization.cs。
    //============================================================
    public static class Loc {
        //界面当前是否显示英文（跟随设置页 Language；EX 附加页强制中文时返回 false）
        public static bool IsEnglish { get { return ModManager.IsEnglishUi(); } }

        //双语文本：中文界面返回 zh，英文界面返回 en
        public static string T(string zh, string en) { return ModManager.T(zh, en); }

        //枚举显示名：中文界面查翻译表（如 win→获胜），英文界面返回原名
        public static string EnumName(string name) { return ModManager.EnumNameZh(name); }

        //键位显示名：中文界面返回“左Ctrl”“回车”等，英文界面返回 KeyCode 枚举名
        public static string KeyName(KeyCode key) { return ModManager.KeyDisplayName(key); }

        //关卡场景名 → 中文关卡名（中文界面；英文界面返回场景原名）
        public static string MapName(string scene) { return ModManager.MapNameZh(scene); }
    }
}
