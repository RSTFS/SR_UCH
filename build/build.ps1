# ============================================================
# SR_UCH 一键构建：单一产物 SR_UCH.dll
#   .\build\build.ps1                      构建 SR（+ EX，若同级存在 SR_UCH_EX 仓库）
#   .\build\build.ps1 -Deploy             构建并复制 SR（+ EX）进 BepInEx\plugins
#   .\build\build.ps1 -Ex                 只构建 EX（失败即报错）
#   .\build\build.ps1 -NoEx               只构建 SR，跳过 EX
#   .\build\build.ps1 -Check              统一回归（uch.py verify --strict）
#   .\build\build.ps1 -ExportAudit        从运行日志导出启动审计报告
# 源码清单、引用清单、部署产物都由 build/specs/features.spec.json 派生。
# 构建链只有 build/rsp/sr_uch.rsp 一条；EX 与独立插件在各自项目构建，本仓库不放其清单。
# ============================================================
param([switch]$Check, [switch]$Deploy, [switch]$Ex, [switch]$NoEx,
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
$workspaceRoot = Split-Path -Parent $Root

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
Write-Host ("游戏目录: " + $GameDir)

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
    if ($dn) { $useDotnetBuild = $true; Write-Host '编译器: dotnet build（未找到独立 csc，回退 MSBuild，结果等价）' }
    else {
        Write-Host '找不到任何 C# 编译器：需要 .NET SDK，或 _tools\roslyn_toolset\csc.dll / _tools\net472\csc.exe'
        exit 1
    }
} else {
    Write-Host ("编译器: csc  " + $csc)
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
    Write-Host "== 构建 $name =="
    if ($csc.EndsWith('.dll')) { & dotnet $csc "@$rsp" 2>&1 | Out-Null }
    else { & $csc "@$rsp" 2>&1 | Out-Null }
    if ($LASTEXITCODE -ne 0) { Write-Host "  [FAIL] $name"; exit 1 }
    Write-Host "  [ OK ] $name"
    $summary.Add("SR   build  OK   $name")
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
    & dotnet build (Join-Path $Root 'SR_UCH.csproj') -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { Write-Host '  [FAIL] SR_UCH.dll (dotnet)'; exit 1 }
    # csproj 的 OutputPath 是 bin\MSBuild；-Deploy 只认 $outDir（bin\Release），
    # 这里把产物同步一份过去，两种构建路线对部署表现为一致。
    $builtDll = Join-Path $Root 'bin\MSBuild\SR_UCH.dll'
    if (Test-Path -LiteralPath $builtDll) {
        Copy-Item -LiteralPath $builtDll -Destination (Join-Path $outDir 'SR_UCH.dll') -Force
    }
    Write-Host '  [ OK ] SR_UCH.dll (dotnet)'
    $summary.Add('SR   build  OK   bin\MSBuild\SR_UCH.dll')
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
    Write-Host ("已部署: " + (Join-Path $plugins $dll))
    $srHash = ''
    try { $srHash = (Get-FileHash -LiteralPath (Join-Path $plugins $dll) -Algorithm SHA256).Hash.Substring(0, 12) } catch { }
    $summary.Add("SR   deploy OK   $dll  $srHash")
    try {
        $hash = (Get-FileHash $src -Algorithm SHA256).Hash.Substring(0, 16)
        $verLine = 'SR_UCH ' + $hash + '  built ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        [IO.File]::WriteAllText((Join-Path $plugins 'SR_UCH.version.txt'), $verLine, (New-Object System.Text.UTF8Encoding($false)))
    } catch { Write-Host ('版本文件写入失败（忽略）: ' + $_.Exception.Message) }
}

# ---- EX：默认随 -All 一起构建（-NoEx 跳过）。EX 是独立仓库，失败不影响 SR 出包。----
$exRoot = Join-Path $workspaceRoot 'SR_UCH_EX'
$exProj = Join-Path $exRoot 'SR_UCH_EX.csproj'
if ($Ex -and -not $NoEx) {
    # 单独构建 EX：EX 编译期必须引用 SR_UCH.dll，先确保 SR 已产出。
    $needSr = Join-Path $outDir 'SR_UCH.dll'
    if (-not (Test-Path -LiteralPath $needSr)) {
        Write-Host '== EX 依赖 SR 产物，先构建 SR_UCH.dll =='
        & dotnet build (Join-Path $Root 'SR_UCH.csproj') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { Write-Host '  [FAIL] 构建 SR 失败，EX 无法继续'; exit 1 }
        $builtForEx = Join-Path $Root 'bin\MSBuild\SR_UCH.dll'
        if (Test-Path -LiteralPath $builtForEx) {
            Copy-Item -LiteralPath $builtForEx -Destination $needSr -Force
        }
    }
}
if (-not $NoEx) {
    if (-not (Test-Path -LiteralPath $exProj)) {
        if ($Ex) { Write-Host "找不到 EX 工程: $exProj"; exit 1 }
        Write-Host 'EX：跳过（未找到 SR_UCH_EX.csproj）'
    } else {
        Write-Host '== 构建 SR_UCH_EX.dll =='
        $exArgs = @('build', $exProj, '-c', 'Release', '--nologo',
                    ('-p:UCH_DIR=' + $GameDir), ('-p:SrDll=' + (Join-Path $outDir $dll)))
        & dotnet @exArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host '  [FAIL] SR_UCH_EX.dll'
            # -Ex 是显式要求，失败必须报错；随 -All 时降级为警告，不拖累 SR 产物。
            if ($Ex) { exit 1 }
            Write-Host '  [WARN] EX 构建失败（SR 已成功，可单独跑 SR_UCH_EX\build\check-ex.ps1 排查）'
        } else {
            Write-Host '  [ OK ] SR_UCH_EX.dll'
            $summary.Add('EX   build  OK   bin\MSBuild\SR_UCH_EX.dll')
            if ($Deploy) {
                $exBuilt = Join-Path $exRoot 'bin\MSBuild\SR_UCH_EX.dll'
                if (Test-Path -LiteralPath $exBuilt) {
                    try {
                        Copy-Item -LiteralPath $exBuilt -Destination (Join-Path $plugins 'SR_UCH_EX.dll') -Force
                        Write-Host ("已部署: " + (Join-Path $plugins 'SR_UCH_EX.dll'))
                        $exHash = ''
                        try { $exHash = (Get-FileHash -LiteralPath (Join-Path $plugins 'SR_UCH_EX.dll') -Algorithm SHA256).Hash.Substring(0, 12) } catch { }
                        $summary.Add("EX   deploy OK   SR_UCH_EX.dll  $exHash")
                    } catch { Write-Host ('EX 部署失败（游戏是否正在运行？）: ' + $_.Exception.Message) }
                } else {
                    Write-Host ("找不到 EX 产物: $exBuilt")
                    if ($Ex) { exit 1 }
                }
            }
        }
    }
}

# ---- 构建总结：每次运行都打印，避免出现"看起来成功了但没有产物" ----
Write-Host ''
Write-Host '================= 构建总结 ================='
Write-Host ('  游戏目录 : ' + $GameDir)
Write-Host ('  编译路线 : ' + $(if ($useDotnetBuild) { 'dotnet build (MSBuild)' } else { 'csc' }))
if ($Check) { Write-Host '  目标     : -Check（统一回归，不构建）' }
elseif ($Ex -and $NoEx) { Write-Host '  目标     : 参数冲突（-Ex 与 -NoEx 同时给出）' }
elseif ($Ex) { Write-Host '  目标     : 只构建 EX' }
elseif ($NoEx) { Write-Host '  目标     : 只构建 SR' }
else { Write-Host '  目标     : SR + EX' }
Write-Host ('  部署     : ' + $(if ($Deploy) { '是' } else { '否（未加 -Deploy）' }))
Write-Host ''
if ($summary.Count -gt 0) {
    Write-Host '  组件  阶段    结果  详情'
    Write-Host '  ----  ------  ----  ------------------------------------------'
    foreach ($s in $summary) { Write-Host ('  ' + $s) }
} else {
    Write-Host '  （本次没有产出任何构建结果）'
}
if ($Deploy) {
    Write-Host ''
    Write-Host ('  部署目录 : ' + (Join-Path $GameDir 'BepInEx\plugins'))
    Write-Host '  提示     : 游戏运行时部署可能失败，请先完全退出游戏'
}
Write-Host '============================================='
Write-Host ''

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
