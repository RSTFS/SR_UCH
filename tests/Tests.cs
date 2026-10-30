// SR_UCH 最小单元测试（item 5）：只测"纯逻辑"，不碰 Unity / 游戏程序集。
// 依赖：SR_UCH/SR.Contracts.cs（契约层可独立编译：只引用 BCL + BepInEx）里的 ColorHex。
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

            Console.WriteLine("  通过 " + _pass + " / 失败 " + _fail);
            if (_fail > 0) { Console.WriteLine("== 有失败 ❌ =="); return 1; }
            Console.WriteLine("== 全部通过 ✅ ==");
            return 0;
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

        // 契约版本单调递增：外部模块按它握手，倒退会让 DLL 静默失效。
        private static void TestContractVersion() {
            Check("SRContract.Version >= 2", SRContract.Version >= 2, "v" + SRContract.Version);
        }
    }
}
