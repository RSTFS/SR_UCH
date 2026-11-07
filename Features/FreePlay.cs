// 自由模式页：只在自由模式可用，内容由 EX 模块通过槽位提供（自身状态等）。
// 归属：SR_UCH 主体；EX 未安装时给出提示而不是空白页。

using System;
using BepInEx.Configuration;
using SR_UCH.Tweaks;
using UnityEngine;

namespace SR_UCH {
    [SrFeature("sr.freeplay", "FreePlay", "1.0")]
    public class FreePlayPage : ITweak {
        private const string Sec = "自由模式";
        private const string SlotRender = "ex.freeplay.render";   // EX 注册的页面渲染回调（Action）

        public void Initialize(IFeatureHost plugin) {
            try {
                SRApi.LocKey(Sec, "FreePlay Page", "自由模式", "Freeplay");
                // 侧栏栏目必须显式 Nav 注册：_navOrder 只由 SR.Nav 填充，
                // 只调 RegisterPage 的栏目不会出现在左栏（这就是"加了但侧栏没有"的原因）。
                SRApi.Nav(Sec, 85);
                SRApi.RegisterPage(Sec, "freeplay", "自由模式", "Freeplay", 10, Render);
                SRApi.RegisterTick("自由模式页", Tick);
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/初始化", __ex); }
        }

        private static Action _render;
        private static bool _probed;

        private static void Tick() {
            if (_probed) return;
            _probed = true;
            try { _render = Slots.Require<Action>(SlotRender); } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/取渲染回调", __ex); }
        }

        private static void Render() {
            try {
                if (!_probed) Tick();
                SRApi.Ctl.SectionTitle(SRApi.T("- 自由模式 -", "- Freeplay -"));
                if (!SRApi.GateModeAllows(SRApi.ModeMask.Freeplay)) {
                    GUILayout.Label(SRApi.T("本页只在自由模式下可用", "This page is available in freeplay only"), SRApi.Ctl.Label);
                    return;
                }
                if (_render == null) {
                    GUILayout.Label(SRApi.T("未检测到 EX 模块（自身状态等功能由它提供）", "EX module not detected (it provides these options)"), SRApi.Ctl.Label);
                    return;
                }
                _render();
            } catch (Exception __ex) { SRApi.Guard.Log("自由模式页/渲染", __ex); }
        }
    }
}
