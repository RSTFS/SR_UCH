// 实验栏目：注册侧栏栏目和三页、生命周期。
// 页面拆分：p1 关卡边界 -> Experiments.Bounds.cs；p2 其它 -> Experiments.Misc.cs；p3 场景 -> Experiments.Scene.cs。

using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
	{
		// 栏目注册：本地化文案、侧栏顺序、三页页签。
		private static void SelfReg() {
			SR.LocSec("Experiments", "实验", null);
            SR.Nav("Experiments", 100); //侧栏栏目顺序 100
            // 本栏目自绘，共三页。页签顺序由 Order 决定：p3 场景(10) / p1 关卡边界(20) / p2 其它(30)，id 与文件对应关系固定。
			SR.RegisterPage("Experiments", "p1", "关卡边界", "Bounds", 20, Render);
			SR.RegisterPage("Experiments", "p2", "其它", "Misc", 30, RenderPage2);
			SR.RegisterPage("Experiments", "p3", "场景", "Scene", 10, RenderPage3);
		}

		// 状态行：只读、可复制。
		// 高度必须取 SR.Ctl.TextHeight(text, w)：中文有字形下沉，固定高度会裁字；该函数内部已含下沉余量并带缓存。
		private static void DrawStatus(string text) {
			try {
				if (string.IsNullOrEmpty(text)) return;
				float w = Mathf.Max(SR.Ctl.Sc(160), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(40));
				float h = SR.Ctl.TextHeight(text, w);
				GUILayout.Label(text, SR.Ctl.LabelWrap, GUILayout.Width(w), GUILayout.Height(h));
			} catch (Exception __ex) { SRApi.Guard.Log("状态行", __ex); }
		}

		public void Initialize(IFeatureHost plugin)
		{
			SelfReg();
			InitCleanup(plugin);
		}
}
}
