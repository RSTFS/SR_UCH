using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SR_UCH.Tweaks {
public partial class SR {

// ==== 分区：GateAudit（T5：启动自检，列出补丁目标并标出每帧方法）====
//
// 目的（提示词 T5）：让“哪些补丁没走门控”不再靠人工记忆。
// 务实落地说明：提示词设想的是“扫描程序集内所有 [HarmonyPatch] 方法，未走 FeatureBuilder
// 注册的记 LogError”——但 FeatureBuilder 属于 T7 的产物，现在还没有注册表可比对。
// 因此这里先实现**可靠且无误报**的基线：启动时通过 Harmony 的运行时补丁登记表
//（Harmony.GetAllPatchedMethods / GetPatchInfo）列出本程序集实际打上的补丁目标，
// 并重点标出挂在每帧方法（Update/FixedUpdate/LateUpdate/OnPreCull 等）上的补丁——
// 这些是性能与门控一致性最敏感的位置（提示词第 3 节：9 处补丁挂在每帧方法）。
// 等 T7 的 FeatureBuilder 落地后，再把“未注册即告警”补上。

        public static class GateAudit {
            private static readonly HashSet<string> PerFrameMethods = new HashSet<string> {
                "Update", "FixedUpdate", "LateUpdate", "OnPreCull", "OnPreRender", "OnPostRender", "OnGUI"
            };

            private static bool _ran;

            public static void Run() {
                if (_ran) return; //只跑一次
                _ran = true;
                try {
                    Assembly asm = typeof(SR).Assembly;
                    int total = 0;
                    List<string> perFrame = new List<string>();

                    foreach (MethodBase mb in Harmony.GetAllPatchedMethods()) {
                        Patches info;
                        try { info = Harmony.GetPatchInfo(mb); } catch { continue; }
                        if (info == null) continue;
                        if (!HasOurs(info.Prefixes, asm) && !HasOurs(info.Postfixes, asm) && !HasOurs(info.Transpilers, asm))
                            continue; //不是本模组打的补丁

                        total++;
                        if (mb != null && PerFrameMethods.Contains(mb.Name)) {
                            string owner = mb.DeclaringType != null ? mb.DeclaringType.Name : "?";
                            perFrame.Add(owner + "." + mb.Name);
                        }
                    }

                    MainPlugin.ModLogger.LogInfo("[GateAudit] SR_UCH 补丁目标方法 " + total + " 个；其中挂在每帧方法上的 " + perFrame.Count + " 个：");
                    for (int i = 0; i < perFrame.Count; i++) {
                        MainPlugin.ModLogger.LogInfo("[GateAudit]   " + perFrame[i]);
                    }
                } catch (Exception ex) {
                    try { MainPlugin.ModLogger.LogWarning("[GateAudit] 扫描失败: " + ex.Message); } catch { }
                }
            }

            private static bool HasOurs(IEnumerable<Patch> patches, Assembly asm) {
                if (patches == null) return false;
                foreach (Patch p in patches) {
                    try {
                        if (p == null || p.PatchMethod == null || p.PatchMethod.DeclaringType == null) continue;
                        if (p.PatchMethod.DeclaringType.Assembly == asm) return true;
                    } catch { }
                }
                return false;
            }
        }

	}
}
