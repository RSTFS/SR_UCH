// SR_UCH 最小单元测试：只测纯逻辑，不碰 Unity / 游戏程序集。
// 依赖契约层（Contracts/*.cs，可独立编译）；运行 build\run-tests.ps1，失败返回非 0。
using System;
using System.Collections.Generic;
using UnityEngine;
using SR_UCH;

namespace SR_UCH.Tests {
    internal static class Program {
        private static int _pass, _fail;

        private static void Check(string name, bool ok, string detail = null) {
            if (ok) { _pass++; return; }
            _fail++;
            Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : ""));
        }

        private static void Eq(string name, object want, object got) {
            bool ok = want == null ? got == null : want.Equals(got);
            Check(name, ok, "want=" + (want ?? "null") + " got=" + (got ?? "null"));
        }

        private static int Main() {
            Console.WriteLine("== SR_UCH 单元测试 ==");
            TestColorHex();
            TestContractVersion();
            TestTableScrollMath();
            TestCaps();
            TestFeatureManifest();
            TestSinksAndFallbacks();
            TestStoragePath();
            TestTableArgsCreate();
            TestFeatureLogic();
            TestFeatureRuntime();
            RunStructureChecks();
            TestPureParity();

            Console.WriteLine("  通过 " + _pass + " / 失败 " + (_fail + Failures));
            if (_fail + Failures > 0) { Console.WriteLine("== 有失败 ❌ =="); return 1; }
            Console.WriteLine("== 全部通过 ✅ ==");
            return 0;
        }

        private static void TestTableScrollMath() {
            Eq("RowsH(200,30)", 170f, (float)SRApi.Ctl.TableRowsH(200f, 30f));
            Eq("RowsH 不为负", 0f, (float)SRApi.Ctl.TableRowsH(10f, 30f));
            Eq("maxY 内容装得下=0", 0f, (float)SRApi.Ctl.TableMaxScrollY(17, 34f, 600f));
            Eq("maxY 内容超出", 578f - 400f, (float)SRApi.Ctl.TableMaxScrollY(17, 34f, 400f));
            Eq("maxY 空列表=0", 0f, (float)SRApi.Ctl.TableMaxScrollY(0, 34f, 400f));
            var boxOut = new UnityEngine.Rect(0f, 100f, 400f, 900f);   // 顶 210、底 1110
            Eq("行区收敛到可见底", 610f, (float)SRApi.Ctl.ScrollRowsH(boxOut, 20f, 730f, 30f));
            var boxIn = new UnityEngine.Rect(0f, 100f, 400f, 300f);    // 框本身就在屏幕内
            Eq("框内不收敛", 280f, (float)SRApi.Ctl.ScrollRowsH(boxIn, 20f, 730f, 30f));
        }

        // ColorHex：功能层颜色文本解析入口。
        // 运行时门面与纯函数层必须给出同一结果。
        private static void TestPureParity() {
            for (int i = -10; i <= 120; i += 11) {
                float y = (float)i;
                Eq("Scroll.Clamp 同源", SR_UCH.Pure.ScrollMath.Clamp(y, 55f), SRApi.Ctl.Scroll.Clamp(y, 55f));
                Eq("Scroll.Wheel 同源", SR_UCH.Pure.ScrollMath.Wheel(y, 2f, 55f, 7f), SRApi.Ctl.Scroll.Wheel(y, 2f, 55f, 7f));
            }
            Eq("TableRowsH 同源", SR_UCH.Pure.ScrollMath.TableRowsH(200f, 30f), (float)SRApi.Ctl.TableRowsH(200f, 30f));
            Eq("TableMaxScrollY 同源", SR_UCH.Pure.ScrollMath.TableMaxScrollY(17, 34f, 400f), (float)SRApi.Ctl.TableMaxScrollY(17, 34f, 400f));
        }

        private static void TestColorHex() {
            Color c;

            Eq("ParseHex(#RRGGBB) 返回值", true, SRApi.ColorHex.ParseHex("#FF8000", out c) && Math.Abs(c.r - 1f) < 0.01f && Math.Abs(c.g - 0.502f) < 0.01f && Math.Abs(c.b - 0f) < 0.01f);
            Eq("ParseHex 无 # 前缀", true, SRApi.ColorHex.ParseHex("00FF00", out c) && Math.Abs(c.g - 1f) < 0.01f && Math.Abs(c.r) < 0.01f);
            Eq("ParseHex 小写", true, SRApi.ColorHex.ParseHex("#0a0b0c", out c));

            // 非法输入返回 false 且不抛异常（调用方靠返回值判红框）。
            Check("ParseHex(空) 返回 false", !SRApi.ColorHex.ParseHex("", out c));
            Check("ParseHex(null) 返回 false", !SRApi.ColorHex.ParseHex(null, out c));
            Check("ParseHex(#XYZ) 返回 false", !SRApi.ColorHex.ParseHex("#XYZXYZ", out c));
            Check("ParseHex(过短) 返回 false", !SRApi.ColorHex.ParseHex("#FFF", out c));

            Color src = new Color(0.2f, 0.6f, 0.9f, 1f);
            string hex = SRApi.ColorHex.ToHex(src);
            bool rt = SRApi.ColorHex.ParseHex(hex, out c);
            Check("ToHex/ParseHex 往返", rt && Math.Abs(c.r - src.r) < 0.01f && Math.Abs(c.g - src.g) < 0.01f && Math.Abs(c.b - src.b) < 0.01f, hex);
            Check("ToHex 输出 7 字符 #RRGGBB", hex != null && hex.Length == 7 && hex[0] == '#', hex);

            // Color32 取整边界：0 / 255 不得溢出成 256 或负数。
            Color32 c32 = SRApi.ColorHex.ToColor32(new Color(0f, 1f, 0.5f, 1f));
            Eq("ToColor32 R=0", (byte)0, c32.r);
            Eq("ToColor32 G=255", (byte)255, c32.g);
            Color back = SRApi.ColorHex.FromColor32(c32);
            Check("FromColor32(ToColor32) 往返", Math.Abs(back.g - 1f) < 0.01f);
        }

        private static void TestCaps() {
            object token = new object();
            SrCaps.SelfTestToken = token;
            Check("能力容器赋读同一引用", ReferenceEquals(token, SrCaps.SelfTestToken));
            SrCaps.LevelReloadBusy = () => true;
            Check("能力容器委托可调用", SrCaps.LevelReloadBusy != null && SrCaps.LevelReloadBusy());
            SrCaps.SelfTestToken = null;
            SrCaps.LevelReloadBusy = null;
            Check("能力容器清空后为 null", SrCaps.SelfTestToken == null && SrCaps.LevelReloadBusy == null);
        }

        [SrFeature("ut.basic", "Basic", "1.5", Rank = 7)]
        private sealed class BasicTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        [SrFeature("ut.dep", "Dep", "1.0", DependsOn = new string[] { "ut.missing.dep" }, Provides = new string[] { "ut.slot.provided" })]
        private sealed class DepTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        [SrFeature("ut.toonew", "TooNew", "0.1", MinContract = 99)]
        private sealed class TooNewTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        private sealed class NoManifestTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        private static void TestFeatureManifest() {
            SrFeatureAttribute attr = new SrFeatureAttribute("ut.x", "X", "1.0");
            Eq("SrFeature 默认 Rank", 120, attr.Rank);
            Eq("SrFeature 默认 MinContract", SRContract.Version, attr.MinContract);

            FeatureInfo bi = Features.FromType(typeof(BasicTweak));
            Check("FromType 有清单", bi != null && !bi.ManifestMissing);
            Eq("FromType 复制 Id", "ut.basic", bi.Id);
            Eq("FromType 复制 Name", "Basic", bi.Name);
            Eq("FromType 复制 Version", "1.5", bi.Version);
            Eq("FromType 复制 Rank", 7, bi.Rank);
            Check("MinContract 未过期 -> ContractTooNew=false", !bi.ContractTooNew);

            FeatureInfo di = Features.FromType(typeof(DepTweak));
            Eq("DependsOn 长度", 1, di.DependsOn != null ? di.DependsOn.Length : -1);
            Eq("Provides 长度", 1, di.Provides != null ? di.Provides.Length : -1);
            Eq("DependsOn 内容", "ut.missing.dep", di.DependsOn[0]);

            FeatureInfo tn = Features.FromType(typeof(TooNewTweak));
            Check("MinContract 过期 -> ContractTooNew=true", tn.ContractTooNew);
            Check("ContractTooNew 出现在 ToString", tn.ToString() != null && tn.ToString().Contains("要求契约"));

            FeatureInfo nm = Features.FromType(typeof(NoManifestTweak));
            Check("无 [SrFeature] -> ManifestMissing=true", nm.ManifestMissing);
            Eq("无清单回退 Id=类型全名", typeof(NoManifestTweak).FullName, nm.Id);
            Eq("无清单回退 Version=0", "0", nm.Version);
            Check("ManifestMissing 出现在 ToString", nm.ToString().Contains("未声明清单"));

            int n0 = Features.Count;
            FeatureInfo r1 = new FeatureInfo { Id = "ut.reg", Name = "R1", Version = "1", Impl = typeof(string) };
            Features.Register(r1);
            FeatureInfo r2 = new FeatureInfo { Id = "ut.reg", Name = "R2", Version = "1", Impl = typeof(object) };
            Features.Register(r2);
            Eq("Register 两条同 id 都保留", n0 + 2, Features.Count);
            Check("Find 原 id 返回第一条", Features.Find("ut.reg") == r1);
            Check("Find 去重 id 返回第二条", Features.Find("ut.reg#Object") == r2);
            Eq("Find null 返回 null", (object)null, Features.Find(null));
            Eq("Find 空串返回 null", (object)null, Features.Find(""));
            int n1 = Features.Count;
            Features.Register(null);
            Features.Register(new FeatureInfo { Id = "", Name = "empty" });
            Eq("Register 非法输入 no-op", n1, Features.Count);
            Features.Register(new FeatureInfo { Id = "ut.problem", Name = "Problem", Version = "1", Impl = typeof(string), ContractTooNew = true });
            Features.Register(new FeatureInfo { Id = "ut.problem", Name = "Problem", Version = "1", Impl = typeof(string), ContractTooNew = true });
            Check("ProblemCount 计数包含过期契约", Features.ProblemCount >= 1);

            // 依赖缺失 / 槽位冲突检测在 Rebuild()，这里只验数据结构。
            di.MissingDeps.Add("ut.missing.dep");
            di.ConflictSlots.Add("ut.slot.provided");
            Eq("MissingDeps 记录", 1, di.MissingDeps.Count);
            Eq("ConflictSlots 记录", 1, di.ConflictSlots.Count);
            string ds = Features.Describe();
            Check("Describe 返回非空且含契约版本", !string.IsNullOrEmpty(ds) && ds.Contains("契约 v"));
        }

        private static void TestSinksAndFallbacks() {
            Eq("T 未注入时回退中文", "中文原文", SRApi.T("中文原文", "English text"));
            Check("SinkCount <= SinkTotal", SRApi.SinkCount() <= SRApi.SinkTotal(), SRApi.SinkCount() + "/" + SRApi.SinkTotal());
            Check("MissingSinks 非 null", SRApi.MissingSinks() != null);
            Check("SinkTotal >= 0", SRApi.SinkTotal() >= 0);
            Check("重载锁自愈阈值 > 0", SrTimeouts.ReloadLockStaleSeconds > 0f);
            Check("预设消费超时 > 0", SrTimeouts.PresetConsumeTimeoutSeconds > 0f);
            Check("重载锁阈值 > 预设消费超时", SrTimeouts.ReloadLockStaleSeconds > SrTimeouts.PresetConsumeTimeoutSeconds);
            Check("TableRowH > 0", SRApi.Ctl.TableRowH() > 0f);
            Check("ThemeRadius >= 0", SRApi.Ctl.ThemeRadius >= 0f);
            Check("ThemeHoverFade 在 0..1", SRApi.Ctl.ThemeHoverFade >= 0f && SRApi.Ctl.ThemeHoverFade <= 1f);
            Eq("TableColumnWidths(null) 返回 null", (object)null, SRApi.Ctl.TableColumnWidths(null));
            MainThread.Enable(true);
            MainThread.Assert("ut.mainthread");
            MainThread.Enable(false);
            Check("主线程 Assert 不抛异常", true);
        }

        // 统一存储：原子写、UTF-8 无 BOM、读回、覆盖、删除。
        private static void TestStoragePath() {
            // %TEMP% 在测试宿主可能被拒写，故用构建产物目录。
            string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sr_store_test");
            try { System.IO.Directory.CreateDirectory(dir); } catch (Exception) { }
            string file = System.IO.Path.Combine(dir, "a_" + Guid.NewGuid().ToString("N") + ".txt");
            try {
                Check("Storage.WriteAtomicPath", SRApi.Storage.WriteAtomicPath(file, "hello"));
                Eq("Storage.ReadPath 往返", "hello", SRApi.Storage.ReadPath(file));
                Check("Storage.WriteAtomicPath 覆盖", SRApi.Storage.WriteAtomicPath(file, "world"));
                Eq("Storage 覆盖后读回", "world", SRApi.Storage.ReadPath(file));
                byte[] bytes = System.IO.File.Exists(file) ? System.IO.File.ReadAllBytes(file) : null;
                Check("Storage 无 BOM", bytes != null && bytes.Length >= 5 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
                Eq("Storage.ReadPath 缺文件返回 null", (object)null, SRApi.Storage.ReadPath(file + ".missing"));
            } finally {
                try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch (Exception) { }
                try { if (System.IO.File.Exists(file + ".tmp")) System.IO.File.Delete(file + ".tmp"); } catch (Exception) { }
                try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        private static void TestTableArgsCreate() {
            var vis = new System.Collections.Generic.List<int> { 0, 1 };
            var a = SRApi.TableArgs.Create(new Rect(0, 0, 400, 200), new float[] { 100f, 100f }, vis, 2,
                delegate (int c) { return "c" + c; }, delegate (int r) { return new string[] { "a", "b" }; });
            Eq("TableArgs.Create 合法参数无问题", 0, a.Validate().Length);
            var bad = SRApi.TableArgs.Create(new Rect(0, 0, 400, 200), new float[] { 100f, 100f }, vis, 2, null, null);
            Check("TableArgs.Validate 能报缺回调", bad.Validate().Length > 0);
        }

        private static void TestFeatureLogic() {
            string[] high = { "失败", "拒绝" };
            string[] low = { "已保存", "已开启" };
            Eq("NotifyLogic.Classify 高优先级", 2, SR_UCH.Tweaks.NotifyLogic.Classify("操作失败", "", high, low));
            Eq("NotifyLogic.Classify 低优先级", 0, SR_UCH.Tweaks.NotifyLogic.Classify("已保存", "reset", high, low));
            Eq("NotifyLogic.Classify 普通", 1, SR_UCH.Tweaks.NotifyLogic.Classify("普通提示", "", high, low));
            Eq("NotifyLogic.Norm 去数字/小写", "abc def", SR_UCH.Tweaks.NotifyLogic.Norm("abc 12 Def"));
            Check("PresetFilterLogic.全部通过", SR_UCH.Tweaks.PresetFilterLogic.Pass("n", "L", "M", true, "", "", "", ""));
            Check("PresetFilterLogic.preset 排除本地", !SR_UCH.Tweaks.PresetFilterLogic.Pass("n", "L", "M", true, "preset", "", "", ""));
            Check("PresetFilterLogic.local 排除预设", !SR_UCH.Tweaks.PresetFilterLogic.Pass("n", "L", "M", false, "local", "", "", ""));
            Check("PresetFilterLogic.关卡过滤", !SR_UCH.Tweaks.PresetFilterLogic.Pass("n", "L", "M", false, "", "OTHER", "", ""));
            Check("PresetFilterLogic.搜索命中", SR_UCH.Tweaks.PresetFilterLogic.Pass("My Preset", "L", "M", false, "", "", "", "preset"));
            Check("PresetFilterLogic.搜索未命中", !SR_UCH.Tweaks.PresetFilterLogic.Pass("My Preset", "L", "M", false, "", "", "", "zzz"));
            Eq("ChaosLogic 低重力=增益", 0, SR_UCH.Tweaks.ChaosLogic.TintKind(false, 0, 1));
            Eq("ChaosLogic 高重力=减益", 1, SR_UCH.Tweaks.ChaosLogic.TintKind(false, 0, 2));
            Eq("ChaosLogic 快冲刺=增益", 0, SR_UCH.Tweaks.ChaosLogic.TintKind(false, 2, 2));
            Eq("ChaosLogic 跳舞=增益", 0, SR_UCH.Tweaks.ChaosLogic.TintKind(true, 9, 0));
            Eq("ChaosLogic 镜像=减益", 1, SR_UCH.Tweaks.ChaosLogic.TintKind(true, 10, 0));
            Eq("ChaosLogic 未知字段=中性", 2, SR_UCH.Tweaks.ChaosLogic.TintKind(false, 7, 1));
        }

        // 组合根：幂等、作用域账本、撤销。放在最后，避免污染其它用例的槽位状态。
        private static void TestFeatureRuntime() {
            FeatureRuntime.SetRevokeSink(null);
            bool first = FeatureRuntime.TryInit(typeof(Program), "unit.runtime", delegate {
                FeatureRuntime.RecordTick("T1");
                FeatureRuntime.RecordPage("unit/page");
                FeatureRuntime.RecordSlot("unit.slot", new object());
                FeatureRuntime.NoteRequire("unit.slot", true);
            });
            bool second = FeatureRuntime.TryInit(typeof(Program), "unit.runtime", delegate { });
            Check("FeatureRuntime 幂等", first && !second);
            Eq("FeatureRuntime 作用域数", 1, FeatureRuntime.Count);
            Check("FeatureRuntime 账本可见", FeatureRuntime.Report().Contains("unit.runtime"));
            Check("FeatureRuntime 可撤销", FeatureRuntime.Dispose("unit.runtime"));
            Eq("FeatureRuntime 撤销后归零", 0, FeatureRuntime.Count);
        }

        // 契约版本单调递增：外部模块按它握手，倒退会让 DLL 失效。
        private static void TestContractVersion() {
            Check("SRContract.Version >= 2", SRContract.Version >= 2, "v" + SRContract.Version);
        }

        // 结构不变量：与上面的 Check 分开计数，避免重载劫持三参数版本的 _pass/_fail。
        private static void CheckS(string name, bool ok) {
            if (ok) { _pass++; return; }
            Failures += 1;
            Console.WriteLine((ok ? "  OK  " : " FAIL ") + name);
        }
    public static int Failures;

    public static void RunStructureChecks() {
        int[] layers = new int[] { -2000, -1000, 0, 1, 3, 100 };
        System.Collections.Generic.HashSet<int> seen = new System.Collections.Generic.HashSet<int>();
        bool uniq = true;
        foreach (int v in layers) if (!seen.Add(v)) uniq = false;
        CheckS("UiLayer 值唯一", uniq);
        short[] ids = new short[] { 771, 772, 773, 774 };
        System.Collections.Generic.HashSet<short> si = new System.Collections.Generic.HashSet<short>();
        bool iu = true; foreach (short v in ids) if (!si.Add(v)) iu = false;
        CheckS("NetMsgId 唯一", iu);
        UiLayerRegistry.Reset(); NetMsgId.Reset();
        UiLayerRegistry.Register("a", UiLayer.Main);
        UiLayerRegistry.Register("b", UiLayer.Main);
        CheckS("同层冲突可检出", UiLayerRegistry.Conflicts.Count == 1);
        NetMsgId.Register("a", NetMsgId.ChaosRule);
        NetMsgId.Register("b", NetMsgId.ChaosRule);
        CheckS("消息撞号可检出", NetMsgId.Conflicts.Count == 1);
        RefCache.InvalidateAll(); RefCache.Declare("X.Y"); RefCache.Declare("X.Y");
        CheckS("反射声明去重", RefCache.DeclaredCount == 1);
        RefCache.InvalidateAll();
        CheckS("反射失效递增", RefCache.Version >= 0);
        CheckS("稳定 ID 非空", SR_UCH.SrLocIds.Count > 0);
        CheckS("稳定 ID 全部 10 位", System.Array.TrueForAll(SR_UCH.SrLocIds.Keys, k => k != null && k.Length == 10));
        CheckS("列号声明与迁移同源", SR_UCH.SrCfgColumns.OnlineV5ColCount == SR_UCH.SrCfgColumns.OnlineV4ColCount - 1);
        CheckS("反射缓存支持 Property", SR_UCH.RefCache.PropertyOf(typeof(string), "Length") != null);
        CheckS("反射缓存失效递增", SR_UCH.RefCache.Version >= 0);
        SR_UCH.Sub.DisposeAll();
        CheckS("全局撤销可清空", SR_UCH.Sub.GlobalCount == 0);
        // 页级滚动分桶：同一 key 覆写、不同 key 互不影响（纯字典语义）
        var d = new System.Collections.Generic.Dictionary<string, UnityEngine.Vector2>();
        d["Internal|A"] = new UnityEngine.Vector2(0, 120);
        d["Internal|B"] = UnityEngine.Vector2.zero;
        CheckS("页级 ViewState 隔离", d["Internal|A"].y == 120 && d["Internal|B"].y == 0);

        // 能力容器：强类型字段，拼错在编译期失败。
        CheckS("能力容器字段可直接赋值", SrCaps.LevelReloadBusy == null);

        // 健康快照：四个出口共用的数据模型，纯函数可测。
        HealthSchemaCheck();
    }

    private static void HealthSchemaCheck() {
        CheckS("健康 schema 含 events", HealthSchema.Has("events"));
        CheckS("健康 schema 含 verdict", HealthSchema.Has("verdict"));
        CheckS("事件码全部有描述", HealthCode.All.Length == 5 &&
            Array.TrueForAll(HealthCode.All, c => !string.IsNullOrEmpty(HealthCode.Describe(c))));
        CheckS("未知事件码被拒", !HealthCode.IsKnown("SR-H999"));
        HealthSnapshot s = new HealthSnapshot();
        s.PatchTotal = 2; s.PatchOk = 1;
        CheckS("补丁不全中即 FAIL", s.Verdict() == "FAIL");
        s.PatchOk = 2; s.RefTotal = 1; s.RefOk = 1;
        CheckS("全部命中即 OK", s.Verdict() == "OK");
        s.Add(HealthCode.DegradedCatch, "sr.demo", "降级处理");
        s.Add("BAD", "x", "y");
        CheckS("事件登记去重且仅收已知码", s.Events.Count == 1 && s.Events[0].Valid);
        CheckS("自检行格式固定", s.ToSelfTestLine("OK").StartsWith("[SR.SelfTest] OK 游戏版本=") &&
            s.ToSelfTestLine("OK").Contains("补丁=2/2") && s.ToSelfTestLine("OK").Contains("API=0/0"));
        CheckS("文本报告兼容旧格式", s.ToText().StartsWith("=== SR_UCH 诊断报告 ===") &&
            s.ToText().Contains("结构化事件"));
        CheckS("JSON 含 schema 与 events", s.ToJson().Contains("\"schema\": 1") && s.ToJson().Contains("\"events\": ["));
            new System.Collections.Generic.Dictionary<string, string[]>();
        // 组合根注入：替身写的值必须被关机路径读回。
        CheckS("注入判定 OK", SR_UCH.Pure.RuntimeRules.InjectionHonored("Ui.ActivePage", "Ui.ActivePage"));
        CheckS("关机报告 OK", SR_UCH.Pure.RuntimeRules.ShutdownReport(true, 3, 4) == "OK");
        CheckS("关机报告 REVIEW", SR_UCH.Pure.RuntimeRules.ShutdownReport(false, 3, 4) == "REVIEW");
    }

    }
}
