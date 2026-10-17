// BepInEx Configuration Manager（F1 呼出）使用的排序与隐藏标签容器。
// 该管理器按类型名反射读取本类公开字段，类名与字段名不可更改。
using System;

namespace SR_UCH.Tweaks {
    //本地定义一份同名类即可被 Configuration Manager 识别，无需引用其 DLL。
    //Order 大的条目显示在前（该管理器按 OrderByDescending 排序），故 SR.Settings.ApplyCmOrder 倒序编号。
    public class ConfigurationManagerAttributes {
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public string Category;
        public bool? ShowRangeAsPercent;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public bool? ReadOnly;
        //Browsable=false 或 HideSetting=true 时 Configuration Manager 不显示该条目。
        public bool? HideSetting;
        public string DispName;
        public string Description;
    }
}
