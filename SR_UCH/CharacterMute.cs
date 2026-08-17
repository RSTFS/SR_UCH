using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //角色声音静音（功能探索）：
    //  A = 关闭自己的角色声音；B = 关闭其它玩家角色的声音（该声音是由角色发出的声音）。
    //所有角色声音（走路/跳跃/落地/掉落等）最终都经 AkSoundEngine.PostEvent(事件名, 角色 GameObject)
    //播放——包括本地播放（Character.audioEvent / AudioEventExact）、远端播放
    //（RpcAudioEvent / RpcAudioEventExact）与掉落音（CharacterFallingSound）。
    //因此在 PostEvent(string, GameObject) 汇聚点按 hasAuthority 分流：
    //  自己的角色在本端有权威（hasAuthority=true），其它玩家的角色为 false。
    public class CharacterMute : ITweak {
        private static ConfigEntry<bool> _muteOwnEntry;
        private static ConfigEntry<bool> _muteOthersEntry;
        public static bool MuteOwn;
        public static bool MuteOthers;

        public void Initialize(MainPlugin plugin) {
            _muteOwnEntry = plugin.Config.Bind(
                "实验",
                "Mute Own",
                false,
                "静音：关闭自己角色的声音（走路/跳跃/落地/掉落等角色音效）");
            MuteOwn = _muteOwnEntry.Value;
            _muteOwnEntry.SettingChanged += (s, e) => MuteOwn = _muteOwnEntry.Value;

            _muteOthersEntry = plugin.Config.Bind(
                "实验",
                "Mute Others",
                false,
                "静音：关闭其它玩家角色的声音（仅自己听不到，不影响对方）");
            MuteOthers = _muteOthersEntry.Value;
            _muteOthersEntry.SettingChanged += (s, e) => MuteOthers = _muteOthersEntry.Value;

            Harmony.CreateAndPatchAll(typeof(CharacterMute));
        }

        //所有角色声音的播放汇聚点（Wwise）。非角色声音（UI/环境等，GameObject 上无 Character）
        //不受影响。
        [HarmonyPatch(typeof(AkSoundEngine), "PostEvent", new Type[] { typeof(string), typeof(GameObject) })]
        [HarmonyPrefix]
        static bool MuteCharacterSound(string in_pszEventName, GameObject in_gameObjectID) {
            if (!ModManager.AllEnabled) return true;
            if (!MuteOwn && !MuteOthers) return true;
            if (in_gameObjectID == null) return true;
            try {
                Character c = in_gameObjectID.GetComponentInParent<Character>();
                if (c == null) return true; //非角色声音，不拦截
                if (MuteOwn && c.hasAuthority) return false; //A：自己的角色声音
                if (MuteOthers && !c.hasAuthority) return false; //B：其它玩家的角色声音
            } catch { }
            return true;
        }
    }
}
