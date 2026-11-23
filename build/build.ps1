# ============================================================
# SR_UCH 一键构建：单一产物 SR_UCH.dll
#   .\build\build.ps1                      构建 SR
#   .\build\build.ps1 -Deploy             构建并复制进 BepInEx\plugins
#   .\build\build.ps1 -Check              统一回归（uch.py verify --strict）
#   .\build\build.ps1 -ExportAudit        从运行日志导出启动审计报告
# 源码清单、引用清单、部署产物都由 build/specs/features.spec.json 派生。
# 构建链只有 build/rsp/sr_uch.rsp 一条。EX 由上层编排脚本（CC\build.ps1）单独构建。
# ============================================================
param([switch]$Check, [switch]$Deploy, [switch]$Quiet,
      [string]$Features = '', [string]$GameDir = '', [switch]$ExportAudit,
      [string]$PluginsDir = '', [string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
# ---- Python 解析：Windows 上 python3 常解析到 WindowsApps 的 App Execution Alias
#      （0 字节占位符，直接执行返回 9009），必须按真实路径过滤。----
function Resolve-Python {
    foreach ($name in @('python', 'python3')) {
        foreach ($c in (Get-Command $name -All -ErrorAction SilentlyContinue)) {
            $src = $c.Source
            if (-not $src) { continue }
            if ($src -like '*\WindowsApps\*') { continue }
            if (-not (Test-Path -LiteralPath $src)) { continue }
            $len = 0
            try { $len = (Get-Item -LiteralPath $src).Length } catch { $len = 0 }
            if ($len -lt 65536) { continue }
            return $src
        }
    }
    return ''
}

# 构建结果账本：末尾统一打印总结（每个产物一行）。
$summary = New-Object System.Collections.Generic.List[string]

# ---- 游戏目录探测：-GameDir > UCH_DIR > 固定候选 > 注册表 ----
if (-not $GameDir) { $GameDir = $env:UCH_DIR }
if (-not $GameDir) {
    # 已知安装位置（含本机实际路径）。
    $cands = @(
        'D:\EF\Steam\steamapps\common\Ultimate Chicken Horse',
        'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Chicken Horse',
        'D:\Steam\steamapps\common\Ultimate Chicken Horse',
        'E:\Steam\steamapps\common\Ultimate Chicken Horse'
    )
    foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { $GameDir = $c; break } }
}
if (-not $GameDir) {
    # 注册表：SteamPath（安装目录）+ libraryfolders.vdf（该账号的所有库）。
    $probe = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($steam in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
            $sp = (Get-ItemProperty -Path $steam -ErrorAction SilentlyContinue).SteamPath
            if (-not $sp) { continue }
            $probe.Add($sp)
            $vdf = Join-Path $sp 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s*"([^"]+)"')) {
                    $probe.Add($m.Groups[1].Value.Replace('\\', '\'))
                }
            }
        }
    } catch { }
    foreach ($lib in $probe) {
        $cand = Join-Path (Join-Path $lib 'steamapps\common') 'Ultimate Chicken Horse'
        if (Test-Path -LiteralPath (Join-Path $cand 'UltimateChickenHorse_Data\Managed\Assembly-CSharp.dll')) { $GameDir = $cand; break }
    }
}
if (-not $GameDir) {
    Write-Host '找不到游戏目录：设环境变量 UCH_DIR，或加 -GameDir "<游戏根目录>"'
    Write-Host '  例：$env:UCH_DIR = "D:\...\Ultimate Chicken Horse"'
    exit 1
}
$sw = [System.Diagnostics.Stopwatch]::StartNew()
if (-not $Quiet -and -not $Env:CC_ORCHESTRATED) { Write-Host ("  game      " + $GameDir) }

# rsp 里是相对路径（/out:bin\Release\...、源码清单），必须以仓库根为当前目录执行。
# 兼容入口从 _tools 转发过来时 cwd 不是仓库根，不切会写错位置并报编译失败。
Push-Location -LiteralPath $Root
try {

# ---- csc：优先 dotnet 自带 Roslyn，回退 net472 csc.exe ----
$csc = ''
$roslyn = Join-Path $Root '_tools\roslyn_toolset\csc.dll'
if (Test-Path -LiteralPath $roslyn) { $csc = $roslyn }
if (-not $csc) {
    $net472 = Join-Path $Root '_tools\net472\csc.exe'
    if (Test-Path -LiteralPath $net472) { $csc = $net472 }
}
if (-not $csc) {
    # 再试 dotnet SDK 自带的 Roslyn（无需仓库内置工具集）。
    try {
        $dnRoot = Split-Path (Get-Command dotnet -ErrorAction SilentlyContinue).Source -Parent
        $sdkDir = Join-Path $dnRoot 'sdk'
        if (Test-Path -LiteralPath $sdkDir) {
            $latestSdk = Get-ChildItem -LiteralPath $sdkDir -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending | Select-Object -First 1
            if ($latestSdk) {
                $sdkCsc = Join-Path $latestSdk.FullName 'Roslyn\bincore\csc.dll'
                if (Test-Path -LiteralPath $sdkCsc) { $csc = $sdkCsc }
            }
        }
    } catch { }
}
$useDotnetBuild = $false
if (-not $csc) {
    # 没有仓库内置工具集、SDK 里也没找到 Roslyn：回退 MSBuild（编译输入已入库，结果等价）。
    $dn = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dn) { $useDotnetBuild = $true; if (-not $Quiet) { Write-Host '  compiler  msbuild' } }
    else {
        Write-Host '找不到任何 C# 编译器：需要 .NET SDK，或 _tools\roslyn_toolset\csc.dll / _tools\net472\csc.exe'
        exit 1
    }
} else {
    if (-not $Quiet) { Write-Host '  compiler  csc' }
}

$cache = Join-Path $PSScriptRoot '.cache'
$rspDir = Join-Path $PSScriptRoot 'rsp'
$outDir = Join-Path $Root 'bin\Release'
# 产物名从 spec 读一次，供 -Deploy 与 EX 构建共用（原先只在 -Deploy 分支里赋值，
# 不带 -Deploy 时为 $null，EX 会拿到空的 SrDll 路径）。
$dll = 'SR_UCH.dll'
try {
    $specFileTop = Join-Path $PSScriptRoot 'specs\features.spec.json'
    if (Test-Path -LiteralPath $specFileTop) {
        $specDll = ((Get-Content -LiteralPath $specFileTop -Raw -Encoding UTF8) | ConvertFrom-Json).artifacts.dll
        if ($specDll) { $dll = $specDll }
    }
} catch { }
if (-not (Test-Path -LiteralPath $cache)) { New-Item -ItemType Directory -Force -Path $cache | Out-Null }
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

# ---- 清单副本：仓库 rsp 是模板，本次构建的裁剪只改 .cache ----
$main = Join-Path $rspDir 'sr_uch.rsp'
$work = Join-Path $cache 'sr_uch.rsp'
if (-not (Test-Path -LiteralPath $main)) {
    Write-Host "缺 $main，自动恢复派生物…"
    $pyPath = Resolve-Python
    if (-not $pyPath) { Write-Host 'Python 未找到（需要真实安装的 python，不能是 WindowsApps 占位符），无法自举'; exit 1 }
    $env:PYTHONIOENCODING = 'utf-8'
    try { & $pyPath (Join-Path $Root 'tools\uch.py') materialize }
    finally { Remove-Item Env:\PYTHONIOENCODING -ErrorAction SilentlyContinue }
    if (-not (Test-Path -LiteralPath $main)) { Write-Host "自举后仍缺 $main"; exit 1 }
}
Copy-Item -LiteralPath $main -Destination $work -Force
# rsp 里的 %GAME% 是占位符，必须替换成实际游戏根目录后才能交给 csc。
try {
    $rspText = [System.IO.File]::ReadAllText($work)
    if ($rspText.Contains('%GAME%')) {
        $rspText = $rspText.Replace('%GAME%', $GameDir)
        [System.IO.File]::WriteAllText($work, $rspText, (New-Object System.Text.UTF8Encoding($true)))
    }
} catch { Write-Host ('rsp 占位符替换失败: ' + $_.Exception.Message); exit 1 }

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
    # 捕获编译器输出：成功只报一行，失败才展开（否则 csc 的噪音淹没真正的错误）。
    if ($csc.EndsWith('.dll')) { $out = & dotnet $csc "@$rsp" 2>&1 }
    else { $out = & $csc "@$rsp" 2>&1 }
    if ($LASTEXITCODE -ne 0) {
        $out | ForEach-Object { Write-Host $_ }
        Write-Host "[FAIL] $name"
        exit 1
    }
    if (-not $Quiet) {
        Write-Host ("  build     " + $name + "   " + $sw.ElapsedMilliseconds + " ms")
    }
    $summary.Add("SR  build  $name")
}

# ---- -Check：守卫只有一份实现，交给统一入口 ----
if ($Check) {
    $pyPath = Resolve-Python
    if (-not $pyPath) { Write-Host 'Python 未找到（需要真实安装的 python，不能是 WindowsApps 占位符）'; exit 1 }
    # Windows 控制台默认 GBK，uch.py 输出含中文会 UnicodeEncodeError；显式指定 UTF-8。
    $env:PYTHONIOENCODING = 'utf-8'
    try {
        & $pyPath (Join-Path $Root 'tools\uch.py') verify --strict
        $verifyRc = $LASTEXITCODE
    } finally { Remove-Item Env:\PYTHONIOENCODING -ErrorAction SilentlyContinue }
    exit $verifyRc
}

if ($useDotnetBuild) {
    if ($Features) { Write-Host '注意：-Features 裁剪只作用于 rsp 链；本次 dotnet 回退为全量编译' }
    $out = & dotnet build (Join-Path $Root 'SR_UCH.csproj') -c Release --nologo -warnaserror -v:q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $out | ForEach-Object { Write-Host $_ }
        Write-Host '[FAIL] SR_UCH.dll'
        exit 1
    }
    # csproj 的 OutputPath 是 bin\MSBuild；-Deploy 只认 $outDir（bin\Release），
    # 这里把产物同步一份过去，两种构建路线对部署表现为一致。
    $builtDll = Join-Path $Root 'bin\MSBuild\SR_UCH.dll'
    if (Test-Path -LiteralPath $builtDll) {
        Copy-Item -LiteralPath $builtDll -Destination (Join-Path $outDir 'SR_UCH.dll') -Force
    }
    if (-not $Quiet) {
        Write-Host ('  build     SR_UCH.dll   ' + $sw.ElapsedMilliseconds + ' ms')
    }
    $summary.Add('SR  build  SR_UCH.dll')
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
    # $dll 已在上面统一解析。
    $src = Join-Path $outDir $dll
    if (-not (Test-Path -LiteralPath $src)) {
        # 兜底：dotnet 路线的默认输出目录。
        $alt = Join-Path (Join-Path $Root 'bin\MSBuild') $dll
        if (Test-Path -LiteralPath $alt) { $src = $alt }
    }
    if (-not (Test-Path -LiteralPath $src)) {
        Write-Host "找不到产物 $dll（先不带 -Deploy 构建）"
        Write-Host ("  已查找: " + (Join-Path $outDir $dll))
        exit 1
    }
    try { Copy-Item -LiteralPath $src -Destination (Join-Path $plugins $dll) -Force }
    catch { Write-Host ('复制失败（游戏是否正在运行？）: ' + $_.Exception.Message); exit 1 }
    if (-not $Quiet) {
        Write-Host ("  deploy    " + $dll + "   " + $sw.ElapsedMilliseconds + " ms")
    }
    $srHash = ''
    try { $srHash = (Get-FileHash -LiteralPath (Join-Path $plugins $dll) -Algorithm SHA256).Hash.Substring(0, 12) } catch { }
    $summary.Add("SR  deploy  $dll  " + $srHash)
    try {
        $hash = (Get-FileHash $src -Algorithm SHA256).Hash.Substring(0, 16)
        $verLine = 'SR_UCH ' + $hash + '  built ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        [IO.File]::WriteAllText((Join-Path $plugins 'SR_UCH.version.txt'), $verLine, (New-Object System.Text.UTF8Encoding($false)))
    } catch { Write-Host ('版本文件写入失败（忽略）: ' + $_.Exception.Message) }
}

# 产物绝对路径：上层编排脚本（CC\build.ps1）据此定位 SR_UCH.dll 传给 EX 工程。

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

} finally { Pop-Location }
