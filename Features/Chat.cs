// 会话栏目。注册栏目和两个页面、生命周期、Harmony 补丁。
// 拆法沿用 R402 的规矩 —— 功能显示在 UI 的哪里，源码就在哪里，一页一个文件：
//   对话窗口 -> Chat.Window.cs
//   对话内容 -> Chat.Log.cs
// （这两个原来叫 ChatLog / ChatWindow，R402 合并进来时统一改叫 Chat 了。）

using System;
using BepInEx;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Chat : ITweak {

        //会话栏目（两个页面共用）：栏目名 + 侧栏顺序。
        // 两个页面分属两个文件（对话窗口 / 对话内容），但同属这一个栏目。
        public void Initialize(IFeatureHost plugin) {
            SR.LocSec("Chat", "会话", null);
            SR.Nav("Chat", 80); //侧栏栏目顺序 80
            InitWindow(plugin);  //第 1 页：对话窗口
            InitLog(plugin);     //第 2 页：对话内容
            //两个页面的 Harmony 补丁（ChatDisplay.Update / DisplayNewMessage）在本类，统一注册一次。
            // CreateAndPatchAll 只认程序集 -> 一次调用即覆盖本类两个文件里的全部补丁；
            //  分两处调用没有额外收益，反而把"同一个补丁注册两次"的风险留在代码里。
            try { HarmonyLib.Harmony.CreateAndPatchAll(typeof(Chat), (string)null); }
            catch (Exception e) { SR.LogError("会话(聊天窗口/聊天记录) 补丁注册失败: " + e.Message); }
        }
}
}
