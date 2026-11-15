// 会话栏目：栏目与页面注册、生命周期、Harmony 补丁。

using System;
using BepInEx;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class Chat : ITweak {

        public void Initialize(IFeatureHost plugin) {
            SRApi.LocSec("Chat", "会话", null);
            SRApi.Nav("Chat", 90); // 栏位顺序值（越小越靠前）
            InitWindow(plugin);
            InitLog(plugin);
            // CreateAndPatchAll 按类型扫描，一次调用覆盖两个 partial 文件，不可重复注册。
            try { SRApi.RegisterPatch(typeof(Chat)); }
            catch (Exception e) { SRApi.LogError("会话(聊天窗口/聊天记录) 补丁注册失败: " + e.Message); }
        }
}
}
