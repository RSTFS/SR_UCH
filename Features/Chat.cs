// 会话栏目：注册栏目与两个页面、生命周期、Harmony 补丁。
// 页面拆分：对话窗口 -> Chat.Window.cs；对话内容 -> Chat.Log.cs。

using System;
using BepInEx;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Chat : ITweak {

        public void Initialize(IFeatureHost plugin) {
            SRApi.LocSec("Chat", "会话", null);
            SRApi.Nav("Chat", 80); //侧栏栏目顺序 80
            InitWindow(plugin);
            InitLog(plugin);
            // CreateAndPatchAll 按程序集扫描：一次调用即覆盖本类两个 partial 文件的全部补丁，不可分两处重复注册。
            try { HarmonyLib.Harmony.CreateAndPatchAll(typeof(Chat), (string)null); }
            catch (Exception e) { SRApi.LogError("会话(聊天窗口/聊天记录) 补丁注册失败: " + e.Message); }
        }
}
}
