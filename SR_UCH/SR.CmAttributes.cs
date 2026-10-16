// BepInEx Configuration Manager（按 F1 出来的那个）要的排序和隐藏标签。
// 注意它是**按类型名反射**读本类公开字段的，所以类名和字段名一个都不能改，改了这个兼容层就断了。
using System;

namespace SR_UCH.Tweaks {
    //给 BepInEx Configuration Manager（F1 那个）用的排序/显示标签。
    //它按**类型名**"ConfigurationManagerAttributes"反射读取这些公开字段, 所以：
    //  · 不需要引用 ConfigurationManager.dll（本地定义一份同名类即可被识别）；
    //  · 类名不能改，字段名也要和它的约定一致。
    //Order：Configuration Manager 是 OrderByDescending(Order), **Order 大的显示在前面**
    //（这是反查它的 IL 确认过的，别改成"升序"）。所以 SR.Settings.ApplyCmOrder 里是倒着编号：
    //同一 section 内先绑定的条目拿最大的 Order，从而显示在 SR 侧栏一致的顺序上。
    //section 内只要有一个条目带 Order，Configuration Manager 就按 Order 排（都没有则退回按 key 字母序）。
    public class ConfigurationManagerAttributes {
        public int? Order;
        public bool? IsAdvanced;
        public bool? Browsable;
        public string Category;
        public bool? ShowRangeAsPercent;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public bool? ReadOnly;
        //Browsable=false / HideSetting=true：让 Configuration Manager 不显示该条目。
        //CM v18 按字段名反射读取这两个名字（dll 里有 Browsable/HideSetting 字符串）。
        public bool? HideSetting;
        public string DispName;
        public string Description;
    }
}
