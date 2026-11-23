// 注意事项：表驱动不变量；任何失败返回非 0，CI 的 pure-tests job 直接红。
using System;
using System.Collections.Generic;
using SR_UCH.Pure;

namespace SR_UCH.Tests {
    internal static class PureProgram {
        private static int _pass, _fail;
        private static void Eq(string name, object want, object got) {
            if (want == null ? got == null : want.Equals(got)) { _pass++; return; }
            _fail++;
            Console.WriteLine("  [FAIL] " + name + " want=" + want + " got=" + got);
        }
        private static void Ok(string name, bool v) { Eq(name, true, v); }

        private static int Main() {
            Console.WriteLine("== SR_UCH 纯函数表驱动测试 ==");
            // 滚动算术：0..100 上限 × -3..3 滚轮 × 多种步长，全部与运行时同源。
            for (int max = 0; max <= 100; max += 10)
                for (int y = -20; y <= 130; y += 7)
                    Eq("Clamp(" + y + "," + max + ")",
                        (float)Math.Max(0, Math.Min(y, max)), ScrollMath.Clamp((float)y, (float)max));
            for (int n = -3; n <= 3; n++)
                for (int step = 1; step <= 40; step += 7)
                    for (int max = 0; max <= 60; max += 20) {
                        float before = 17f;
                        float got = ScrollMath.Wheel(before, (float)n, (float)max, (float)step);
                        float want = ScrollMath.Clamp(before - n * step, max);
                        Eq("Wheel", want, got);
                        Ok("Wheel 不越界", got >= 0f && got <= max);
                    }
            for (int rows = 0; rows <= 40; rows += 4)
                for (int h = 0; h <= 60; h += 12) {
                    float want = Math.Max(0f, rows * h - 300f);
                    Eq("TableMaxScrollY", want, ScrollMath.TableMaxScrollY(rows, h, 300f));
                }
            Eq("RowsHVisible 收敛", 610f, ScrollMath.RowsHVisible(120f, 880f, 730f, 30f));
            Ok("Changed 阈值", ScrollMath.Changed(1f, 1.02f) && !ScrollMath.Changed(1f, 1.005f));

            // cfg 版本门与孤儿键清理。
            Ok("schema 匹配", CfgRules.SchemaMatches("SchemaVersion=3\n[A]\nk=v"));
            Ok("schema 不匹配", !CfgRules.SchemaMatches("SchemaVersion=2\n[A]\nk=v"));
            Ok("schema 缺失", !CfgRules.SchemaMatches("[A]\nk=v"));
            Ok("WithSchema 幂等", CfgRules.WithSchema("SchemaVersion=3\nx=1") == "SchemaVersion=3\nx=1");
            Ok("WithSchema 注入", CfgRules.WithSchema("x=1").StartsWith("SchemaVersion=3"));
            string cfg = "[A]\nk = 1\nk = 2\nkeep = 3\n[B]\ndrop = 4";
            string cleaned = CfgRules.RemovePairs(cfg, new List<string> { "B\u0001drop" }, '\u0001');
            Ok("RemovePairs 删指定键", !cleaned.Contains("drop = 4"));
            Ok("RemovePairs 去重保留末值", cleaned.Contains("k = 2") && !cleaned.Contains("k = 1"));

            // XML 四层防护。
            for (int i = 0; i < 60; i++) {
                long bytes = i * 90000L;
                Ok("SizeOk 边界", PresetXmlPolicy.SizeOk(bytes) == (bytes <= PresetXmlPolicy.MaxBytes));
            }
            Ok("禁 DTD", !PresetXmlPolicy.ForbidsDtd("<x><!DOCTYPE y></x>"));
            Ok("禁 ENTITY", !PresetXmlPolicy.ForbidsDtd("<x><!ENTITY y \"z\"></x>"));
            Ok("根节点 scene", PresetXmlPolicy.RootOk("<scene levelSceneName=\"A\"><block/></scene>"));
            Ok("根节点带 XML 声明", PresetXmlPolicy.RootOk("<?xml version=\"1.0\"?><scene/>"));
            Ok("根节点带注释", PresetXmlPolicy.RootOk("<!-- x --><scene><a/></scene>"));
            Ok("拒绝旧错根", !PresetXmlPolicy.RootOk("<SR_UCH_Preset><a/></SR_UCH_Preset>"));
            Ok("拒绝近似根", !PresetXmlPolicy.RootOk("<sceneFake><a/></sceneFake>"));
            Ok("拒绝注释伪造根", !PresetXmlPolicy.RootOk("<!-- <scene> --><evil/>"));
            Ok("路径越界", !PresetXmlPolicy.IsUnderRoot("/data/presets", "/data/presets/../secret.xml"));
            Ok("路径在根内", PresetXmlPolicy.IsUnderRoot("/data/presets", "/data/presets/a.xml"));

            // 声明自洽。
            Ok("组覆盖", SpecRules.GroupsCover(new List<string> { "A", "B" },
                new Dictionary<string, string> { { "x", "A" }, { "y", "B" } }));
            Ok("组不覆盖", !SpecRules.GroupsCover(new List<string> { "A" },
                new Dictionary<string, string> { { "x", "C" } }));
            Ok("无重复", SpecRules.NoDuplicate(new List<string> { "a", "b", "c" }));
            Ok("有重复", !SpecRules.NoDuplicate(new List<string> { "a", "a" }));
            Eq("Missing 数量", 2, SpecRules.Missing(
                new List<string> { "a", "b", "c" }, new List<string> { "a" }).Length);

            // 指纹与健康判定。
            Eq("Canonical 排序", "a\nb\nc", FingerprintRules.Canonical(new List<string> { "c", "a", "b" }));
            Ok("AllHit", FingerprintRules.AllHit(3, 3) && !FingerprintRules.AllHit(2, 3));
            Eq("属性别名", "NetworkHandicap", FingerprintRules.MemberAlias("set_NetworkHandicap"));
            Ok("跳过外部", FingerprintRules.ShouldSkip(true));
            for (int m = 0; m < 4; m++)
                Eq("Verdict", m == 0 ? "OK" : "REVIEW",
                    HealthRules.Verdict(true, true, false, m));
            Eq("Verdict FAIL", "FAIL", HealthRules.Verdict(false, true, false, 0));
            Eq("Verdict REVERIFY", "REVERIFY", HealthRules.Verdict(true, true, true, 0));
            Ok("健康全命中", HealthRules.AllHit("39/39") && !HealthRules.AllHit("38/39"));

            // 声明式页面轨迹：纯数据断言，无需渲染后端。
            UiNode __root = new UiNode("settings", "page");
            UiNode __row = new UiNode("row1", "row");
            __row.Add(new UiNode("toggle1", "control"));
            __root.Add(__row);
            Eq("节点数", 3, UiNodeRules.NodeCount(__root));
            Ok("轨迹含层级", UiNodeRules.Trace(__root).Contains("  row:row1"));
            Eq("无重复 id", 0, UiNodeRules.DuplicateIds(__root).Count);
            Ok("滚动策略", UiTrace.ScrollPolicyAllowed("per-page") && !UiTrace.ScrollPolicyAllowed("global"));
            Eq("轨迹判定", "OK", UiTrace.Verdict(UiNodeRules.Trace(__root), "per-page"));

            // 声明式页面模型：节点校验 / 轨迹比对 / 影子记录。
            UiNode good = new UiNode("p", "page");
            good.Add(new UiNode("p.header", "header"));
            good.Add(new UiNode("p.rows", "rows"));
            good.Add(new UiNode("p.footer", "footer"));
            Eq("Validate 通过", 0, UiNodeRules.Validate(good).Count);
            Eq("Validate 节点数", 4, UiNodeRules.NodeCount(good));
            Eq("Validate 滚动数", 0, UiNodeRules.ScrollCount(good));
            UiNode dup = new UiNode("p", "page");
            dup.Add(new UiNode("x", "rows")); dup.Add(new UiNode("x", "rows"));
            Ok("Validate 抓重复 id", UiNodeRules.Validate(dup).Count > 0);
            UiNode badKind = new UiNode("p", "page");
            badKind.Add(new UiNode("p.bad", "wat"));
            Ok("Validate 抓非法 kind", UiNodeRules.Validate(badKind).Count > 0);
            badKind.Children[0].Layer = "nope";
            Ok("Validate 抓非法 layer", UiNodeRules.Validate(badKind).Count > 0);
            UiNode nested = new UiNode("p", "page");
            UiNode s1 = new UiNode("p.s1", "scroll");
            s1.Add(new UiNode("p.s2", "scroll"));
            nested.Add(s1);
            Ok("Validate 抓嵌套滚动", UiNodeRules.Validate(nested).Count > 0);
            Eq("ScrollCount 嵌套", 2, UiNodeRules.ScrollCount(nested));
            string tr = UiNodeRules.Trace(good);
            Eq("Trace 一致", "", UiTrace.Compare(tr, tr));
            Ok("Trace 差异定位", UiTrace.Compare("a\nb", "a\nc").StartsWith("2|"));
            Ok("Verdict OK", UiTrace.Verdict(tr, "per-page") == "OK");
            Ok("Verdict 非法策略", UiTrace.Verdict(tr, "nope") == "FAIL");
            Ok("HasDuplicate 抓重复", UiTrace.HasDuplicate("page:p\nrows:x\nrows:x"));
            Ok("ScrollVerdict 嵌套红", UiTrace.ScrollVerdict(tr, 2) == "FAIL");
            Ok("ScrollVerdict 正常", UiTrace.ScrollVerdict(tr, 0) == "OK");
            UiShadow sh = new UiShadow();
            sh.Begin("Chat/ChatLog"); sh.Node("header", "Chat/ChatLog.header");
            sh.Node("rows", "Chat/ChatLog.rows"); sh.End();
            Eq("UiShadow 节点数", 3, sh.Count);
            Ok("UiShadow 轨迹", sh.Freeze().StartsWith("page:Chat/ChatLog"));
            sh.Node("rows", "late");
            Eq("UiShadow 冻结后不写", 3, sh.Count);
            sh.Reset(); sh.Begin("Home/main"); sh.End();
            Eq("UiShadow 重置", 1, sh.Count);

            // 组合根关机顺序 / 换页状态转移 / 双轨 cutover（与 SrRuntime 同源）。
            Ok("关机顺序 4 步", RuntimeRules.ShutdownOrder.Length == 4);
            Ok("关机顺序可判定", RuntimeRules.OrderIsValid(new string[] { "leases", "ref-cache", "subscriptions", "domains" }));
            Ok("关机顺序错序被抓", !RuntimeRules.OrderIsValid(new string[] { "domains", "leases", "ref-cache", "subscriptions" }));
            Eq("per-page 恢复上次位置", 42f, RuntimeRules.PageTransition(7f, 42f, "per-page"));
            Eq("global 归零", 0f, RuntimeRules.PageTransition(7f, 42f, "global"));
            Eq("非法策略归零", 0f, RuntimeRules.PageTransition(7f, 42f, "nope"));
            Ok("cutover 三道缺一不可", RuntimeRules.CanCutover(true, true, true)
                && !RuntimeRules.CanCutover(true, true, false)
                && !RuntimeRules.CanCutover(true, false, true)
                && !RuntimeRules.CanCutover(false, true, true));
            Ok("关机不变量", RuntimeRules.ShutdownInvariant(3, 4) && !RuntimeRules.ShutdownInvariant(-1, 4));
            Eq("服务键", "ui.ActivePage", RuntimeRules.ServiceKey("ui", "ActivePage"));
            Eq("服务键空值", "", RuntimeRules.ServiceKey("", "x"));

            // 真回合：结算证据 + 挂起候选 + 只进不退（HUD / 混乱牌组同源）。
            RoundTruthState rt = new RoundTruthState();
            Ok("首回合无需结算", RoundTruth.Step(ref rt, 1, false) && rt.TrueRound == 1 && rt.Pending == 0);
            Ok("证据一次性消费", !rt.TallySeen);
            RoundTruth.NoteTally(ref rt);
            Ok("结算后 +1 接受", RoundTruth.Step(ref rt, 2, false) && rt.TrueRound == 2 && rt.Pending == 0 && !rt.TallySeen);
            Ok("无结算虚增只挂起", !RoundTruth.Step(ref rt, 3, false) && rt.TrueRound == 2 && rt.Pending == 3);
            Ok("同帧抖动不丢挂起", !RoundTruth.Step(ref rt, 2, false) && rt.TrueRound == 2 && rt.Pending == 3);
            RoundTruth.NoteTally(ref rt);
            Ok("结算迟到确认挂起", RoundTruth.Step(ref rt, 3, false) && rt.TrueRound == 3 && rt.Pending == 0 && !rt.TallySeen);
            Ok("回退到 1 才算换局", RoundTruth.Step(ref rt, 1, false) && rt.TrueRound == 1);
            RoundTruthState rc = new RoundTruthState();
            Ok("挑战模式重试计数", RoundTruth.Step(ref rc, 5, true) && rc.TrueRound == 5);
            Ok("挑战模式抖动不回退", !RoundTruth.Step(ref rc, 3, true) && rc.TrueRound == 5);
            Ok("挑战模式回到 1", RoundTruth.Step(ref rc, 1, true) && rc.TrueRound == 1);
            RoundTruth.NoteTally(ref rt);
            RoundTruth.Reset(ref rt);
            Ok("Reset 全清", rt.TrueRound == 0 && rt.Pending == 0 && !rt.TallySeen);
            RoundTruthState rp = new RoundTruthState();
            RoundTruth.Step(ref rp, 1, false);
            Ok("多次虚增只记最大", !RoundTruth.Step(ref rp, 4, false) && !RoundTruth.Step(ref rp, 5, false) && rp.Pending == 5);
            RoundTruth.NoteTally(ref rp);
            Ok("一次结算确认最大候选", RoundTruth.Step(ref rp, 5, false) && rp.TrueRound == 5 && rp.Pending == 0);
            RoundTruth.NoteTally(ref rp);
            RoundTruth.ClearTally(ref rp);
            Ok("ClearTally 只清证据", !rp.TallySeen && rp.TrueRound == 5);

            Console.WriteLine("  通过 " + _pass + " / 失败 " + _fail);
            if (_fail > 0) { Console.WriteLine("== 有失败 =="); return 1; }
            Console.WriteLine("== 全部通过 ==");
            return 0;
        }
    }
}
