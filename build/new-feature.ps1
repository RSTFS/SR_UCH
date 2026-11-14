# 新功能脚手架：生成 Features\<Name>.cs 骨架（清单不用手改，构时会自动生成）。
# 用法：
#   pwsh build\new-feature.ps1 -Name MyFeature [-Zh "我的功能"] [-En "My Feature"] [-Rank 130] [-DryRun]
# 生成后：
#   1. 补自己的配置项（Config.Bind + SRApi.LocKey/LocDesc）与页面内容；
#   2. 直接构建（build\build.ps1）即可，rsp 清单由 build\gen-rsp.ps1 自动生成；
#   3. README/DESIGN 的功能表记得补一行（文本守卫会检查文档里引用的脚本是否存在）。
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Zh = '',
    [string]$En = '',
    [int]$Rank = 130,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if ($Name -notmatch '^[A-Za-z][A-Za-z0-9]*$') {
    Write-Host ('功能名只能用字母/数字且以字母开头（清单按文件名前缀分组）：' + $Name)
    exit 1
}
if (-not $Zh) { $Zh = $Name }
if (-not $En) { $En = $Name }
$path = Join-Path (Join-Path $Root 'Features') ($Name + '.cs')
if ((Test-Path -LiteralPath $path) -and -not $DryRun) {
    Write-Host ('已存在，不覆盖：' + $path)
    exit 1
}

$tpl = @'
// <NAME>：<一句话说明>（由 build\new-feature.ps1 生成，按需改名/补实现）。
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.<ID>", "<NAME>", "1.0")]
    public class <NAME> : ITweak {
        private const string Sec = "<NAME>";           // 配置段名 + 栏目内部键；显示名见下面 LocSec
        private static ConfigEntry<bool> _enabled;

        public void Initialize(IFeatureHost plugin) {
            _enabled = plugin.Config.Bind(Sec, "Enabled", false, "总开关：打开后本功能才生效。");

            // 文案自注册：缺 LocKey 时中文界面会回退英文键名，缺 LocDesc 时没有悬浮说明。
            SRApi.LocSec(Sec, "<ZH>", "<EN>");
            SRApi.LocKey(Sec, "Enabled", "启用<ZH>", "Enable <EN>");
            SRApi.LocDesc(Sec, "Enabled", "启用<ZH>\n打开后本功能才生效。", "Enable <EN>\nTurn on to activate this feature.");

            SRApi.Nav(Sec, <RANK>);                                   // 侧栏栏目顺序（内置 10-130，留空档）
            SRApi.RegisterPage(Sec, "Main", "主页", "Main", 10, RenderPage);
            // 需要每帧逻辑时打开；不需要就删掉这行和 Tick：
            // SRApi.RegisterTick("<NAME>-每帧", Tick);
            // 需要清理（槽位/对象/监听）时保留并在 Shutdown 里实现：
            SRApi.OnShutdown("<NAME>", Shutdown);
        }

        public void Shutdown() { }

        private static void Tick() {
            if (_enabled == null || !_enabled.Value) return;
            // 每帧逻辑放这里；注意异常不要外泄（核心会兜底，但别依赖它）。
        }

        private static void RenderPage() {
            if (_enabled != null) SRApi.Ctl.DrawEntryRow(_enabled, SRApi.Ctl.Sc(180f));
            // 其它控件走 SRApi.Ctl（统一外观/命中），不要自己拼 IMGUI 布局组。
        }
    }
}
'@
$tpl = $tpl.Replace('<NAME>', $Name).Replace('<ID>', $Name.ToLowerInvariant())
$tpl = $tpl.Replace('<ZH>', $Zh).Replace('<EN>', $En).Replace('<RANK>', $Rank.ToString())
# 仓库约定：.cs = UTF-8 无 BOM + LF
$bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($tpl.Replace("`r`n", "`n"))

if ($DryRun) {
    Write-Host ('--- 将生成: ' + $path + ' ---')
    Write-Host $tpl
    exit 0
}
[System.IO.File]::WriteAllBytes($path, $bytes)
Write-Host ('已生成: ' + $path)
Write-Host '下一步：'
Write-Host '  1) 补配置项与页面内容（SRApi.Ctl 控件 + SRApi.LocKey/LocDesc）；'
Write-Host '  2) 直接 .\build\build.ps1 构建（rsp 清单自动生成，不需要手改）；'
Write-Host '  3) README / DESIGN 功能表补一行；'
Write-Host '  4) .\build\build.ps1 -Check 验证守卫（API 白名单 / 槽位 / 行数 / 死代码编译错误等）。'
