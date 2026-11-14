// SR_UCH 最小单元测试（item 5）：只测"纯逻辑"，不碰 Unity / 游戏程序集。
// 依赖：SR_UCH/Contracts/*.cs（契约层可独立编译：只引用 BCL + BepInEx）里的 ColorHex。
// 运行：build\run-tests.ps1（csc 编译本文件 + 契约层 → 直接跑，失败返回非 0）
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
            TestSlots();
            TestFeatureManifest();
            TestSinksAndFallbacks();
            TestStoragePath();

            Console.WriteLine("  通过 " + _pass + " / 失败 " + _fail);
            if (_fail > 0) { Console.WriteLine("== 有失败 ❌ =="); return 1; }
            Console.WriteLine("== 全部通过 ✅ ==");
            return 0;
        }

        // 表格滚动几何：内容装得下 maxY 必须为 0；装不下必须等于高出的部分。
        private static void TestTableScrollMath() {
            Eq("RowsH(200,30)", 170f, (float)SRApi.Ctl.TableRowsH(200f, 30f));
            Eq("RowsH 不为负", 0f, (float)SRApi.Ctl.TableRowsH(10f, 30f));
            Eq("maxY 内容装得下=0", 0f, (float)SRApi.Ctl.TableMaxScrollY(17, 34f, 600f));
            Eq("maxY 内容超出", 578f - 400f, (float)SRApi.Ctl.TableMaxScrollY(17, 34f, 400f));
            Eq("maxY 空列表=0", 0f, (float)SRApi.Ctl.TableMaxScrollY(0, 34f, 400f));
            // 自绘列表：框伸到屏幕外时必须收敛到可见底（这是"滚不动"的根因）
            var boxOut = new UnityEngine.Rect(0f, 100f, 400f, 900f);   // 顶 210、底 1110
            Eq("行区收敛到可见底", 610f, (float)SRApi.Ctl.ScrollRowsH(boxOut, 20f, 730f, 30f));
            var boxIn = new UnityEngine.Rect(0f, 100f, 400f, 300f);    // 框本身就在屏幕内
            Eq("框内不收敛", 280f, (float)SRApi.Ctl.ScrollRowsH(boxIn, 20f, 730f, 30f));
        }

        // ColorHex：功能层所有颜色文本解析都走它，最容易在重构里被改坏。
        private static void TestColorHex() {
            Color c;

            Eq("ParseHex(#RRGGBB) 返回值", true, SRApi.ColorHex.ParseHex("#FF8000", out c) && Math.Abs(c.r - 1f) < 0.01f && Math.Abs(c.g - 0.502f) < 0.01f && Math.Abs(c.b - 0f) < 0.01f);
            Eq("ParseHex 无 # 前缀", true, SRApi.ColorHex.ParseHex("00FF00", out c) && Math.Abs(c.g - 1f) < 0.01f && Math.Abs(c.r) < 0.01f);
            Eq("ParseHex 小写", true, SRApi.ColorHex.ParseHex("#0a0b0c", out c));

            // 非法输入必须返回 false，且不抛异常（调用方靠返回值判红框）
            Check("ParseHex(空) 返回 false", !SRApi.ColorHex.ParseHex("", out c));
            Check("ParseHex(null) 返回 false", !SRApi.ColorHex.ParseHex(null, out c));
            Check("ParseHex(#XYZ) 返回 false", !SRApi.ColorHex.ParseHex("#XYZXYZ", out c));
            Check("ParseHex(过短) 返回 false", !SRApi.ColorHex.ParseHex("#FFF", out c));

            // 往返：ToHex -> ParseHex -> 同一个颜色
            Color src = new Color(0.2f, 0.6f, 0.9f, 1f);
            string hex = SRApi.ColorHex.ToHex(src);
            bool rt = SRApi.ColorHex.ParseHex(hex, out c);
            Check("ToHex/ParseHex 往返", rt && Math.Abs(c.r - src.r) < 0.01f && Math.Abs(c.g - src.g) < 0.01f && Math.Abs(c.b - src.b) < 0.01f, hex);
            Check("ToHex 输出 7 字符 #RRGGBB", hex != null && hex.Length == 7 && hex[0] == '#', hex);

            // Color32 取整边界：0 / 255 不能溢出成 256 或负数
            Color32 c32 = SRApi.ColorHex.ToColor32(new Color(0f, 1f, 0.5f, 1f));
            Eq("ToColor32 R=0", (byte)0, c32.r);
            Eq("ToColor32 G=255", (byte)255, c32.g);
            Color back = SRApi.ColorHex.FromColor32(c32);
            Check("FromColor32(ToColor32) 往返", Math.Abs(back.g - 1f) < 0.01f);
        }

        // Slots（槽位总线）：覆盖 Provide/Require 覆盖语义、类型不符、Retract 只删自己、miss 统计。
        // 注意：契约里没有 Clear()，测试用唯一槽位名 + 相对计数（Count/MissCount 的 delta）。
        private static void TestSlots() {
            int c0 = Slots.Count;
            object a = new object();
            object b = new object();

            Slots.Provide("ut.slot", a);
            Eq("Provide 后 Count+1", c0 + 1, Slots.Count);
            Check("Require 取回同一引用", ReferenceEquals(a, Slots.Require<object>("ut.slot")));

            // 覆盖语义：后 Provide 的赢（真实用途：EX 覆盖内置实现）
            Slots.Provide("ut.slot", b);
            Check("Provide 覆盖后 Require 返回新实现", ReferenceEquals(b, Slots.Require<object>("ut.slot")));

            // Retract(expected) 只删自己提供的：expected 不匹配时不能删别人的
            Slots.Retract("ut.slot", a);
            Eq("Retract(expected 不匹配) 不删除", c0 + 1, Slots.Count);
            Slots.Retract("ut.slot", b);
            Eq("Retract(expected 匹配) 删除", c0, Slots.Count);
            Eq("Retract 后 Require 返回 null", (object)null, Slots.Require<object>("ut.slot"));

            // 非法输入：null/空槽位名、null 实现都不得改变注册表
            int c1 = Slots.Count;
            Slots.Provide("", a);
            Slots.Provide(null, a);
            Slots.Provide("ut.slot.null", null);
            Eq("非法 Provide 不改变 Count", c1, Slots.Count);
            Slots.Retract("", null);
            Slots.Retract(null, null);
            Slots.Retract("ut.slot.absent", null);
            Eq("非法 Retract 不改变 Count", c1, Slots.Count);
            Eq("空槽位名 Require 返回 null", (object)null, Slots.Require<object>(""));

            // 缺槽位：返回 null 并记入自检统计（MissCount 增加、MissNames 能找到）
            int m0 = Slots.MissCount;
            Eq("缺槽位 Require 返回 null", (object)null, Slots.Require<object>("ut.slot.absent"));
            Check("缺槽位记入 MissCount", Slots.MissCount > m0, "before=" + m0 + " after=" + Slots.MissCount);
            bool named = false;
            foreach (string n in Slots.MissNames) if (n != null && n.StartsWith("ut.slot.absent")) { named = true; break; }
            Check("缺槽位名出现在 MissNames", named);

            // 类型不符：已登记但不是要的类型 -> null + 记 miss（带「类型不符」后缀）
            Slots.Provide("ut.slot.typed", a);
            Eq("类型不符 Require 返回 null", (object)null, Slots.Require<string>("ut.slot.typed"));
            bool typedMiss = false;
            foreach (string n in Slots.MissNames) if (n != null && n.StartsWith("ut.slot.typed")) { typedMiss = true; break; }
            Check("类型不符记入 MissNames", typedMiss);
            Slots.Retract("ut.slot.typed", a);
        }

        // 功能清单：FromType 有/无 [SrFeature]、MinContract 过期、Register 去重、Find、ToString 标记。
        [SrFeature("ut.basic", "Basic", "1.5", Rank = 7)]
        private sealed class BasicTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        [SrFeature("ut.dep", "Dep", "1.0", DependsOn = new string[] { "ut.missing.dep" }, Provides = new string[] { "ut.slot.provided" })]
        private sealed class DepTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        [SrFeature("ut.toonew", "TooNew", "0.1", MinContract = 99)]
        private sealed class TooNewTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        private sealed class NoManifestTweak : ITweak { public void Initialize(IFeatureHost host) { } }

        private static void TestFeatureManifest() {
            // 属性默认值：Rank 默认 120，MinContract 默认 = 当前契约版本
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

            // Register / Find / 去重：同一 id 第二次注册改成 id#类型名，不互相覆盖
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

            // 依赖缺失/槽位冲突的检测逻辑在 Rebuild() 里（需要扫 AppDomain），这里只验证数据结构可用。
            di.MissingDeps.Add("ut.missing.dep");
            di.ConflictSlots.Add("ut.slot.provided");
            Eq("MissingDeps 记录", 1, di.MissingDeps.Count);
            Eq("ConflictSlots 记录", 1, di.ConflictSlots.Count);
            string ds = Features.Describe();
            Check("Describe 返回非空且含契约版本", !string.IsNullOrEmpty(ds) && ds.Contains("契约 v"));
        }

        // 通道/回退：未注入 sink 时 T() 回退中文、Sink 记账单调、时间常量合理。
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
            // 线程检查：主线程上断言不得抛异常（跨线程行为需真游戏环境，这里只保证接口安全）
            MainThread.Enable(true);
            MainThread.Assert("ut.mainthread");
            MainThread.Enable(false);
            Check("主线程 Assert 不抛异常", true);
        }

        // 统一存储（绝对路径版）：原子写、UTF-8 无 BOM、读回、覆盖、删除。
        private static void TestStoragePath() {
            // 注意：%TEMP% 在测试宿主进程里会被拒绝写（UnauthorizedAccessException，沙箱 ACL），
            // 所以用测试程序自己的目录（构建产物目录天然可写）做真实文件回归。
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

        // 契约版本单调递增：外部模块按它握手，倒退会让 DLL 静默失效。
        private static void TestContractVersion() {
            Check("SRContract.Version >= 2", SRContract.Version >= 2, "v" + SRContract.Version);
        }
    }
}
