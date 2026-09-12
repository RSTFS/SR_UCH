using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //角色声音静音：A = 关闭自己角色的声音；B = 关闭其它玩家角色的声音。
    //所有角色声音（走路/跳跃/落地/掉落等）都经 AkSoundEngine.PostEvent(事件名, 角色 GameObject)
    //播放（本地/远端 RPC/掉落音同理），故在该汇聚点按 hasAuthority 分流：自己的角色在本端为 true。
    public class CharacterMute : ITweak {
        private static ConfigEntry<bool> _muteOwnEntry;
        private static ConfigEntry<bool> _muteOthersEntry;
        public static bool MuteOwn;
        public static bool MuteOthers;

        private static void SelfReg() {
            SR.LocKey("Experiments", "Mute Own", "关闭自己的声音", null);
            SR.LocDesc("Experiments", "Mute Own", "关闭自己的声音：静音自己角色的角色音效（走路/跳跃/落地/掉落等，由角色发出的声音）。其他人不受影响。", "Mute your own character's sounds (walk/jump/land/fall etc. emitted by the character). Others are not affected.");
            SR.LocKey("Experiments", "Mute Others", "关闭其它玩家的声音", null);
            SR.LocDesc("Experiments", "Mute Others", "关闭其它玩家的声音：静音其他玩家角色的角色音效（自己听不到，不影响对方）。", "Mute other players' character sounds (you won't hear them; their clients are unaffected).");
        }

        public void Initialize(MainPlugin plugin) {
            SelfReg();
            _muteOwnEntry = plugin.Config.Bind(
                "Experiments",
                "Mute Own",
                false,
                "静音：关闭自己角色的声音（走路/跳跃/落地/掉落等角色音效）");
            MuteOwn = _muteOwnEntry.Value;
            _muteOwnEntry.SettingChanged += (s, e) => MuteOwn = _muteOwnEntry.Value;

            _muteOthersEntry = plugin.Config.Bind(
                "Experiments",
                "Mute Others",
                false,
                "静音：关闭其它玩家角色的声音（仅自己听不到，不影响对方）");
            MuteOthers = _muteOthersEntry.Value;
            _muteOthersEntry.SettingChanged += (s, e) => MuteOthers = _muteOthersEntry.Value;

            Harmony.CreateAndPatchAll(typeof(CharacterMute));
        }

        //所有角色声音的播放汇聚点（Wwise）；GameObject 上没有 Character 的是 UI/环境音，不受影响。
        [HarmonyPatch(typeof(AkSoundEngine), "PostEvent", new Type[] { typeof(string), typeof(GameObject) })]
        [HarmonyPrefix]
        static bool MuteCharacterSound(string in_pszEventName, GameObject in_gameObjectID) {
            if (!SR.GateMaster) return true;
            if (!MuteOwn && !MuteOthers) return true;
            if (in_gameObjectID == null) return true;
            try {
                Character c = in_gameObjectID.GetComponentInParent<Character>();
                if (c == null) return true;
                if (MuteOwn && c.hasAuthority) return false;
                if (MuteOthers && !c.hasAuthority) return false;
            } catch (Exception __ex) { SR.Guard.Log("判定角色声音归属", __ex); }
            return true;
        }
    }
}
