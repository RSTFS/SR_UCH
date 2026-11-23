# ============================================================
# SR_UCH 一键构建：单一产物 SR_UCH.dll
#   .\build\build.ps1                      构建（默认）
#   .\build\build.ps1 -Deploy             构建并复制进 BepInEx\plugins
#   .\build\build.ps1 -Check              统一回归（uch.py verify --strict）
#   .\build\build.ps1 -Features "HUD,Chat"  裁剪构建（Lite）
#   .\build\build.ps1 -ExportAudit        从运行日志导出启动审计报告
# 源码清单、引用清单、部署产物都由 build/specs/features.spec.json 派生。
# 构建链只有 build/rsp/sr_uch.rsp 一条；EX 与独立插件在各自项目构建，本仓库不放其清单。
# ============================================================
param([switch]$Check, [switch]$Deploy,
      [string]$Features = '', [string]$GameDir = '', [switch]$ExportAudit,
      [string]$PluginsDir = '', [string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $Root

# ---- 游戏目录探测：-GameDir > UCH_DIR > 常见 Steam 路径 ----
if (-not $GameDir) { $GameDir = $env:UCH_DIR }
if (-not $GameDir) {
    $cands = @(
        'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Chicken Horse',
        'D:\Steam\steamapps\common\Ultimate Chicken Horse',
        'E:\Steam\steamapps\common\Ultimate Chicken Horse'
    )
    foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { $GameDir = $c; break } }
}
if (-not $GameDir) { Write-Host '找不到游戏目录：设 UCH_DIR 或用 -GameDir'; exit 1 }

# ---- csc：优先 dotnet 自带 Roslyn，回退 net472 csc.exe ----
$csc = ''
$roslyn = Join-Path $Root '_tools\roslyn_toolset\csc.dll'
if (Test-Path -LiteralPath $roslyn) { $csc = $roslyn }
if (-not $csc) {
    $net472 = Join-Path $Root '_tools\net472\csc.exe'
    if (Test-Path -LiteralPath $net472) { $csc = $net472 }
}
$useDotnetBuild = $false
if (-not $csc) {
    # fresh clone 没有 _tools 工具集时，直接回退 MSBuild（编译输入已入库）。
    $dn = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dn) { $useDotnetBuild = $true; Write-Host 'csc 缺失 -> 回退 dotnet build SR_UCH.csproj' }
    else { Write-Host '找不到 csc / dotnet：需要 .NET SDK 或 _tools\net472\csc.exe'; exit 1 }
}
Write-Host "csc: $csc"

$cache = Join-Path $PSScriptRoot '.cache'
$rspDir = Join-Path $PSScriptRoot 'rsp'
$outDir = Join-Path $Root 'bin\Release'
if (-not (Test-Path -LiteralPath $cache)) { New-Item -ItemType Directory -Force -Path $cache | Out-Null }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

# ---- 清单副本：仓库 rsp 是模板，本次构建的裁剪只改 .cache ----
$main = Join-Path $rspDir 'sr_uch.rsp'
$work = Join-Path $cache 'sr_uch.rsp'
if (-not (Test-Path -LiteralPath $main)) {
    Write-Host "缺 $main，自动恢复派生物…"
    $py = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) { Write-Host 'python3 未找到，无法自举'; exit 1 }
    & $py.Source (Join-Path $Root 'tools\uch.py') materialize
    if (-not (Test-Path -LiteralPath $main)) { Write-Host "自举后仍缺 $main"; exit 1 }
}
Copy-Item -LiteralPath $main -Destination $work -Force

if ($Features) {
    $wantSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($w in ($Features.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })) { [void]$wantSet.Add($w) }
    if ($wantSet.Count -eq 0) { Write-Host '裁剪构建：-Features 为空'; exit 1 }
    Write-Host ('裁剪构建：只编功能 ' + (($wantSet | Sort-Object) -join ', '))
    $kept = New-Object System.Collections.Generic.List[string]
    foreach ($ln in [System.IO.File]::ReadAllLines($work)) {
        $m = [regex]::Match($ln, '^Features\\([^.]+)')
        if ($m.Success -and -not $wantSet.Contains($m.Groups[1].Value)) { continue }
        $kept.Add($ln)
    }
    [System.IO.File]::WriteAllLines($work, $kept.ToArray(), (New-Object System.Text.UTF8Encoding($true)))
}

function Invoke-Build([string]$rsp, [string]$name) {
    $missing = @()
    if (Test-Path $rsp) {
        foreach ($ln in [System.IO.File]::ReadAllLines($rsp)) {
            $t = $ln.Trim()
            if ($t.EndsWith('.cs') -and -not $t.StartsWith('/')) {
                $p = Join-Path $Root ($t -replace '"', '')
                if (-not (Test-Path -LiteralPath $p)) { $missing += $t }
            }
        }
    } else { Write-Host "  跳过 $name（缺 $rsp）"; return }
    if ($missing.Count) { Write-Host ("  跳过 {0}：源码缺失 {1}" -f $name, ($missing -join ', ')); return }
    Write-Host "== 构建 $name =="
    if ($csc.EndsWith('.dll')) { & dotnet $csc "@$rsp" 2>&1 | Out-Null }
    else { & $csc "@$rsp" 2>&1 | Out-Null }
    if ($LASTEXITCODE -ne 0) { Write-Host "  [FAIL] $name"; exit 1 }
    Write-Host "  [ OK ] $name"
}

# ---- -Check：守卫只有一份实现，交给统一入口 ----
if ($Check) {
    $py = Get-Command python3 -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) { Write-Host 'python3 未找到'; exit 1 }
    & $py.Source (Join-Path $Root 'tools\uch.py') verify --strict
    exit $LASTEXITCODE
}

if ($useDotnetBuild) {
    if ($Features) { Write-Host '注意：-Features 裁剪只作用于 rsp 链；本次 dotnet 回退为全量编译' }
    & dotnet build (Join-Path $Root 'SR_UCH.csproj') -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { Write-Host '  [FAIL] SR_UCH.dll (dotnet)'; exit 1 }
    Write-Host '  [ OK ] SR_UCH.dll (dotnet)'
} else {
    Invoke-Build $work 'SR_UCH.dll'
}

# ---- -Deploy：只搬一个 DLL ----
function Get-PluginsDir {
    if ($PluginsDir) { return $PluginsDir }
    return (Join-Path $GameDir 'BepInEx\plugins')
}
if ($Deploy) {
    $plugins = Get-PluginsDir
    if (-not (Test-Path $plugins)) { Write-Host "找不到 plugins 目录；用 -PluginsDir 指定"; exit 1 }
    $specFile = Join-Path $PSScriptRoot 'specs\features.spec.json'
    $dll = ((Get-Content -LiteralPath $specFile -Raw -Encoding UTF8) | ConvertFrom-Json).artifacts.dll
    $src = Join-Path $outDir $dll
    if (-not (Test-Path -LiteralPath $src)) { Write-Host "找不到产物 $dll（先不带 -Deploy 构建）"; exit 1 }
    try { Copy-Item -LiteralPath $src -Destination (Join-Path $plugins $dll) -Force }
    catch { Write-Host ('复制失败（游戏是否正在运行？）: ' + $_.Exception.Message); exit 1 }
    Write-Host ("已部署: " + (Join-Path $plugins $dll))
    try {
        $hash = (Get-FileHash $src -Algorithm SHA256).Hash.Substring(0, 16)
        $verLine = 'SR_UCH ' + $hash + '  built ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        [IO.File]::WriteAllText((Join-Path $plugins 'SR_UCH.version.txt'), $verLine, (New-Object System.Text.UTF8Encoding($false)))
    } catch { Write-Host ('版本文件写入失败（忽略）: ' + $_.Exception.Message) }
}

if ($ExportAudit) {
    $log = Join-Path $GameDir 'BepInEx\LogOutput.log'
    if (-not (Test-Path -LiteralPath $log)) { Write-Host "找不到日志 $log"; exit 1 }
    $lines = [System.IO.File]::ReadAllLines($log)
    $ver = 'unknown'
    foreach ($l in $lines) { if ($l -match '游戏版本=([^\s]+)') { $ver = $Matches[1]; break } }
    $docs = Join-Path $Root 'docs'
    if (-not (Test-Path -LiteralPath $docs)) { New-Item -ItemType Directory -Force -Path $docs | Out-Null }
    $out = Join-Path $docs ('audit-' + $ver + '.md')
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('# SR_UCH 启动审计报告')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('- 游戏版本: ' + $ver)
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 审计行')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('```')
    foreach ($l in $lines) { if ($l -match '\[SR\.(SelfTest|Gate|Cfg|Audit|Patch|Ref)\]') { [void]$sb.AppendLine($l.Trim()) } }
    [void]$sb.AppendLine('```')
    [IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ('已导出: ' + $out)
}
