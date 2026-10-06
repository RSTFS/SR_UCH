// 契约层（地基）。第三方功能只该依赖这个文件，不该碰 SR 的实现。
// 里面有 ITweak / IFeatureHost / ILanguagePack / Slots。
// 硬约定：本文件**不得**引用 SR 的任何其它文件 —— 它得能单独编成一个程序集，
// 用来证明"契约和实现真的分开了"（_tools\roslyn_toolset\sr_uch_contracts.rsp）。
// 所以槽位里只准传 BCL / 引擎 / 游戏类型；一旦塞进 SR 自定义类型，这份独立性就没了。
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace SR_UCH {
    //功能统一契约：实现它 -> 被 MainPlugin 反射发现并 Initialize。
    //（原来定义在 MainPlugin.cs 里，且参数是具体的 MainPlugin 类型, 那等于逼第三方功能
    //  依赖 SR 的实现。现在参数换成 IFeatureHost，契约就能单独编出去。）
    public interface ITweak {
        void Initialize(IFeatureHost host);
    }

    //功能初始化时真正需要的东西, 只有两样，所以契约很小：
    //  · Config：读/写自己的配置（BepInEx 类型，所有插件共享同一个程序集）
    //  · Logger：往 BepInEx 日志写字（同上）
    //不含 Harmony（功能自己 new Harmony(id) 即可）、不含 SR 的任何类型。
    public interface IFeatureHost {
        ConfigFile Config { get; }
        ManualLogSource Logger { get; }
    }

    // 外部语言包契约（多语言扩展：一个 DLL = 一门语言）
    // 做法：建一个普通类库项目，引用 SR_UCH.dll，实现本接口，把生成的 DLL 丢进
    // BepInEx\plugins\（不需要 [BepInPlugin]，也不用引用 BepInEx）。
    // SR 启动时扫描 plugins 目录，发现实现本接口的类型就注册 -> 设置页界面语言下拉框
    // 立刻多出一个选项，选中即生效（写进 cfg，下次启动保持）。装几门语言就多几个选项。
    public interface ILanguagePack {
        //稳定 id：写进 cfg（如 "fr"）。不要用显示名, 显示名改了不该让老用户的设置失效。
        string Id { get; }
        //下拉框里显示的名字（如 "Français"）
        string DisplayName { get; }
        //未收录的文案回退到哪门内置语言："English"（默认）或 "中文"。
        string FallbackId { get; }
        //词条表：key = SR 源码里的中文原文（也接受英文原名/枚举名/场景名/KeyCode 名）；
        //value = 目标语言译文。没收录的 key 自动回退（见 FallbackId）-> 可以增量补全。
        IDictionary<string, string> Map { get; }
    }
    // 槽位总线：功能之间就靠它通信
    // SR 只认"名字 + 委托签名"，不认识双方类型；取不到就当对方没装，降级，不崩。
    //   · Provide 是**覆盖**语义，同一槽位重复登记以最后一次为准；
    //   · Require 返回 null 就是"没人提供"，调用方必须自己降级；
    //   · 名字写成 "<模块>.<能力>"，委托签名写在调用处注释里。
    //
    // **硬约定**：反射游戏私有成员必须走 SR.RefField / SR.RefMethod，别直接调 AccessTools，也别忘用
    // SR.RefOverride 登记 —— 启动自检只认登记过的成员，没登记的反射在游戏改名后会静默返回 null，
    // 用户只看到"装了没反应"，日志里连告警都没有。两个坑：登记名必须和 "类型名.成员名" 逐字一致；
    // 嵌套类型（如 ScoreKeeper.scoreInfo）不是字段，得走 GetNestedType，但照样要登记。
    public static class Slots {
        private static readonly Dictionary<string, object> _map = new Dictionary<string, object>();
        public static void Provide(string slot, object impl) {
            if (impl != null && !string.IsNullOrEmpty(slot)) _map[slot] = impl;
        }
        public static T Require<T>(string slot) where T : class {
            if (string.IsNullOrEmpty(slot)) return null;
            object o;
            return _map.TryGetValue(slot, out o) ? o as T : null;
        }
        //自检/诊断用：当前登记了多少个槽位
        public static int Count { get { return _map.Count; } }
        public static ICollection<string> Names { get { return _map.Keys; } }
    }
}
