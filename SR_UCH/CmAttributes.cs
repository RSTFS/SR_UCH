using System;

namespace SR_UCH.Tweaks {
    //给 BepInEx Configuration Manager（F1 那个）用的排序/显示标签。
    //它按**类型名**"ConfigurationManagerAttributes"反射读取这些公开字段 —— 所以：
    //  · 不需要引用 ConfigurationManager.dll（本地定义一份同名类即可被识别）；
    //  · 类名不能改，字段名也要和它的约定一致。
    //Order：同一个 section 内按 Order 升序显示；section 内只要有一个条目带 Order，
    //Configuration Manager 就按 Order 排（都没有则退回按 key 字母序）。
    public class ConfigurationManagerAttributes {
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public string Category;
        public bool? ShowRangeAsPercent;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public bool? ReadOnly;
        public string DispName;
        public string Description;
    }
}
