// Configuration Manager（F1）反射读取的容器：本地定义同名类即可被识别，类名/字段名不可改。
using System;

namespace SR_UCH.Tweaks {
    // Order 大者显示在前（管理器按 OrderByDescending 排序）。
    public class ConfigurationManagerAttributes {
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public string Category;
        public bool? ShowRangeAsPercent;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public bool? ReadOnly;
        // Browsable=false 或 HideSetting=true 时不显示该条目。
        public bool? HideSetting;
        public string DispName;
        public string Description;
    }
}
