# 生成构建清单（rsp）：单一真源 = 磁盘上的源码文件。
# 用法：
#   pwsh build\gen-rsp.ps1            # 重写 build\rsp\ 下三组清单（合并 / 拆分核心 / 每个功能）
#   pwsh build\gen-rsp.ps1 -Verify    # 只校验（CI/守卫用）：与磁盘不一致则退出码 1
# 生成范围（其余 rsp 不是生成的）：
#   sr_uch.rsp             = 核心（含 Contracts 契约目录） + 全部功能（合并模式）
#   sr_uch_core.rsp        = 核心（含 Contracts；拆分模式）
#   sr_uch_contracts.rsp   = 仅 SR_UCH\Contracts\*.cs（契约独立编译校验）
#   feat_<功能>.rsp        = 单个功能（拆分模式；<功能> = Features\<功能>*.cs 的前缀）
# 特例清单（手工维护，不在生成范围）：sr_uch_ex.rsp（EX 仓库）、plugins\*.rsp（独立插件）。
param([switch]$Verify, [switch]$NoExit)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot           # <repo>
$rspDir = Join-Path $PSScriptRoot 'rsp'
$enc = New-Object System.Text.UTF8Encoding($true)  # .rsp 统一 UTF-8 + BOM（与仓库约定一致）

# 引用清单：所有主模块/功能清单共用（顺序固定，保证生成结果可复现）。
$refs = @(
    'mscorlib.dll',
    'netstandard.dll',
    'System.dll',
    'System.Core.dll',
    'System.Xml.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.UNETModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.Physics2DModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.AudioModule.dll',
    'InControl.dll',
    'com.unity.multiplayer-hlapi.Runtime.dll',
    'Assembly-CSharp.dll',
    'AK.Wwise.Unity.API.dll'
)
$refLines = New-Object System.Collections.Generic.List[string]
foreach ($r in $refs) { $refLines.Add('/r:"%GAME%\UltimateChickenHorse_Data\Managed\' + $r + '"') }
$refLines.Add('/r:"%GAME%\BepInEx\core\BepInEx.dll"')
$refLines.Add('/r:"%GAME%\BepInEx\core\0Harmony.dll"')
$refLines.Add('/r:"%GAME%\UltimateChickenHorse_Data\Managed\UnityEngine.AnimationModule.dll"')

function Get-Rel([string]$p) { return $p.Replace('/', '\') }

# 核心源码：MainPlugin.cs + SR_UCH\ 下所有 .cs（递归，含 Contracts 契约目录）
$core = New-Object System.Collections.Generic.List[string]
$core.Add('MainPlugin.cs')
$coreDir = Join-Path $Root 'SR_UCH'
Get-ChildItem -LiteralPath $coreDir -Recurse -Filter '*.cs' -File |
    Sort-Object FullName | ForEach-Object {
        $core.Add((Get-Rel ($_.FullName.Substring($Root.Length).TrimStart('\', '/'))))
    }

# 契约源码：SR_UCH\Contracts\*.cs（独立编译成 SR.Contracts.dll 用）
$contractFiles = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath (Join-Path $coreDir 'Contracts') -Filter '*.cs' -File -ErrorAction SilentlyContinue |
    Sort-Object Name | ForEach-Object { $contractFiles.Add((Get-Rel ('SR_UCH\Contracts\' + $_.Name))) }

# 功能源码：Features\*.cs，按文件名第一个 '.' 前的前缀分组
$features = @{}
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $Root 'Features') -Filter '*.cs' -File) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
    $i = $base.IndexOf('.')
    $grp = if ($i -gt 0) { $base.Substring(0, $i) } else { $base }
    if (-not $features.ContainsKey($grp)) { $features[$grp] = New-Object System.Collections.Generic.List[string] }
    $features[$grp].Add((Get-Rel ('Features\' + $f.Name)))
}
$allFeatures = New-Object System.Collections.Generic.List[string]
foreach ($g in ($features.Keys | Sort-Object)) {
    $features[$g].Sort()
    foreach ($s in $features[$g]) { $allFeatures.Add($s) }
}

# 契约独立编译用的引用清单（比主清单小：契约不允许依赖游戏业务类型）
$contractRefLines = New-Object System.Collections.Generic.List[string]
foreach ($r in @('mscorlib.dll', 'netstandard.dll', 'System.dll', 'System.Core.dll')) {
    $contractRefLines.Add('/r:"%GAME%\UltimateChickenHorse_Data\Managed\' + $r + '"')
}
$contractRefLines.Add('/r:"%GAME%\BepInEx\core\BepInEx.dll"')
foreach ($r in @('UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll',
                 'UnityEngine.InputLegacyModule.dll', 'UnityEngine.TextRenderingModule.dll')) {
    $contractRefLines.Add('/r:"%GAME%\UltimateChickenHorse_Data\Managed\' + $r + '"')
}

function New-ContractText([string[]]$sources) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('/nologo')
    $lines.Add('/target:library')
    $lines.Add('/out:bin\Release\SR.Contracts.dll')
    foreach ($l in $contractRefLines) { $lines.Add($l) }
    foreach ($s in $sources) { $lines.Add($s) }
    return (($lines -join "`n") + "`n")
}

function New-Text([string]$out, [bool]$needCoreRef, [string[]]$sources) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('/nologo')
    $lines.Add('/target:library')
    $lines.Add($out)
    foreach ($l in $refLines) { $lines.Add($l) }
    if ($needCoreRef) { $lines.Add('/r:"bin\Release\SR_UCH.dll"') }
    foreach ($s in $sources) { $lines.Add($s) }
    return (($lines -join "`n") + "`n")
}

# 目标文件内容（有序）：name -> text
$want = @{}
$want['sr_uch_contracts.rsp'] = New-ContractText $contractFiles.ToArray()
$want['sr_uch_core.rsp'] = New-Text '/out:bin\Release\SR_UCH.dll' $false $core.ToArray()
$want['sr_uch.rsp'] = New-Text '/out:bin\Release\SR_UCH.dll' $false ($core + $allFeatures | ForEach-Object { $_ })
foreach ($g in ($features.Keys | Sort-Object)) {
    $want['feat_' + $g + '.rsp'] = New-Text ('/out:bin\Release\SR_UCH_' + $g + '.dll') $true $features[$g].ToArray()
}

$bad = 0
foreach ($name in ($want.Keys | Sort-Object)) {
    $path = Join-Path $rspDir $name
    $cur = if (Test-Path -LiteralPath $path) { [System.IO.File]::ReadAllText($path) -replace "`r`n", "`n" } else { $null }
    if ($cur -ne $want[$name]) {
        if ($Verify) { Write-Host ('  [FAIL] 清单与磁盘源码不一致: ' + $name); $bad++ }
        else {
            [System.IO.File]::WriteAllText($path, $want[$name], $enc)
            Write-Host ('  已生成: ' + $name)
        }
    }
}
# 删掉已不存在的功能分组清单（功能文件删光后不留空清单）
foreach ($f in Get-ChildItem -LiteralPath $rspDir -Filter 'feat_*.rsp' -File) {
    if (-not $want.ContainsKey($f.Name)) {
        if ($Verify) { Write-Host ('  [FAIL] 多余的功能清单（对应源码已不存在）: ' + $f.Name); $bad++ }
        else { Remove-Item -LiteralPath $f.FullName -Force; Write-Host ('  已删除: ' + $f.Name) }
    }
}
if ($Verify) {
    $script:RspVerifyBad = $bad
    if ($bad -gt 0) {
        Write-Host ('  清单校验失败：' + $bad + ' 处。运行 build\gen-rsp.ps1 重新生成。')
        if (-not $NoExit) { exit 1 }
        return
    }
    Write-Host ('  [ OK ] 构建清单与磁盘源码一致（' + $want.Count + ' 份）')
}
if (-not $NoExit) { exit 0 }
