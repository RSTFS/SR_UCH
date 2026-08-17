using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SR_UCH.Tweaks {
public partial class ModManager {

// ==== 分区：Progression（进度解锁：A/B 组锁定判断与强制复位）====

        //进度解锁（实验页）：两种独立解锁进度，达标前对应功能保持禁用（灰显不可操作）：
        //  A 组（游戏时长 > 17时16分18秒 或 奔跑长度 > 52000米）：
        //    - 建造增强-无视碰撞 (Builder Enhancements/Collision Override)
        //  B 组（游戏时长 > 52时 或 奔跑长度 > 100000米）：
        //    - 方块破坏总开关 (Destroy Blocks/Enabled)
        //    - 自身增益-无敌/飞天/蹲移 (实验/Self Invincible|Self Fly|Self Crouch Move)
        private static bool ProgressionLocked(ConfigEntryBase entry) {
            try {
                if (entry == null || entry.Definition == null) return false;
                string sec = entry.Definition.Section;
                string key = entry.Definition.Key;
                if (sec == "Builder Enhancements" && key == "Collision Override")
                    return !Experiments.IsProgressionUnlocked(); //A 组
                if ((sec == "Destroy Blocks" && key == "Enabled")
                    || (sec == "实验" && (key == "Self Invincible" || key == "Self Fly" || key == "Self Crouch Move")))
                    return !Experiments.IsProgressionUnlockedB(); //B 组
                return false;
            } catch { return false; }
        }

        //进度解锁：未达标时，锁定项的配置强制保持 false（防止控制台/快捷键/外部改动开启）。
        //注意：必须按组判断是否已解锁——已解锁的组不再强制复位（否则解锁后开关仍被
        //每分钟悄悄关掉，功能看似“无效”）。
        private static void ForceLockedConfigs() {
            try {
                //进度数据未就绪（存档未加载）时跳过复位，避免误伤已解锁用户
                if (!Experiments.ProgressionDataReady()) return;
                if (!Experiments.IsProgressionUnlocked()) {
                    ForceLocked("Builder Enhancements", "Collision Override");
                }
                if (!Experiments.IsProgressionUnlockedB()) {
                    ForceLocked("Destroy Blocks", "Enabled");
                    ForceLocked("实验", "Self Invincible");
                    ForceLocked("实验", "Self Fly");
                    ForceLocked("实验", "Self Crouch Move");
                }
            } catch { }
        }

        private static void ForceLocked(string sec, string key) {
            try {
                ConfigEntryBase e = FindInternalEntry(sec, key);
                if (e != null && e.BoxedValue is bool b && b) {
                    e.BoxedValue = false; //直接写底层，不走 SetValue（SetValue 已有锁定拦截，但这里确保复位）
                    MainPlugin.ModLogger.LogInfo("[进度解锁] 强制复位: " + sec + "/" + key);
                }
            } catch { }
        }

	}
}
