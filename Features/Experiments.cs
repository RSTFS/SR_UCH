// 实验栏目。注册侧栏栏目和三页、生命周期，以及三页共用的小工具。
// 拆法沿用 R402 的规矩 —— 功能显示在 UI 的哪里，源码就在哪里，一页一个文件：
//   p1 关卡边界 -> Experiments.Bounds.cs
//   p2 其它     -> Experiments.Misc.cs
//   p3 场景     -> Experiments.Scene.cs

using System;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Experiments : ITweak
	{
		//（高频共用文案 Msgs 已上移到核心 SR.Msg：那是全模块共用的提示文本，不该挂在实验模块上）
		//（加载后清理的 UI 在 p2其它页 -> 它的配置与实现在 Experiments.Misc.cs）
		private static void SelfReg() {
			SR.LocSec("Experiments", "实验", null);
            SR.Nav("Experiments", 100); //侧栏栏目顺序 100
            // 本功能页自绘（SR.Window 不再硬编码 if-else 分派）：共三页，见下方 RegisterPage。
			// **R392 重命名 + 排序**：场景 / 关卡边界 / 其它
			// R394 排序（用户要求）：场景 -> 关卡边界 -> 其它。id(p1/p2/p3) 保持不变，只改 Order。
			SR.RegisterPage("Experiments", "p1", "关卡边界", "Bounds", 20, Render);
			SR.RegisterPage("Experiments", "p2", "其它", "Misc", 30, RenderPage2);
			SR.RegisterPage("Experiments", "p3", "场景", "Scene", 10, RenderPage3);
		}

		//状态行（只读、可复制）
		// R394：状态行原来是 **TextArea（编辑框）**, 它长得像可输入框、也真的能选中文本，
		//  但状态行是只读的，用编辑框既误导又多一层框线。改成**标签**。
		// 高度用 SR.Ctl.TextHeight(text, w)：不能写固定高度,
		//  中文有字形下沉（g/y/括号的下半部分会被裁掉），SR.Ctl.TextHeight 内部已经
		//  按CalcHeight + Sc(16) 下沉余量算好并带缓存（SR.Settings.cs:870）。
		private static void DrawStatus(string text) {
			try {
				if (string.IsNullOrEmpty(text)) return;
				float w = Mathf.Max(SR.Ctl.Sc(160), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(40));
				float h = SR.Ctl.TextHeight(text, w);
				GUILayout.Label(text, SR.Ctl.LabelWrap, GUILayout.Width(w), GUILayout.Height(h));
			} catch (Exception __ex) { SR.Guard.Log("状态行", __ex); }
		}

		public void Initialize(IFeatureHost plugin)
		{
			SelfReg();
			//加载后清理的 UI 在 p2其它页 -> 它的配置 / 每帧钩子 / 补丁注册全部在
			//Experiments.Misc.cs（见 InitCleanup）；本文件只负责栏目与三页注册。
			InitCleanup(plugin);
		}
}
}
