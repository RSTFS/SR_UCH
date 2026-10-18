# ============================================================
# SR_UCH 一键构建脚本
# 用法：
#   .\build\build.ps1                          合并模式（默认，部署用）：核心 + 全部功能 → 单个 SR_UCH.dll
#   & '.\_tools\build_sr_uch.ps1 -Split'     拆分模式：核心 SR_Manager.dll + 每个功能一个 SR_UCH_<功能>.dll
#   & '.\_tools\build_sr_uch.ps1 -All'       合并模式 + 再编译 EX 与 Others 下的独立插件
#   （-Split 与 -All 可组合）
# 架构说明：核心 SR_UCH.dll 只提供 UI/控件/注册表/门控，不认识任何功能；
#   功能源码在 SR_UCH\Features\<功能>.cs，靠 SR_UCH.ITweak + SR 的注册 API 自挂。
#   → 合并模式把功能一起编进 SR_UCH.dll（当前发布形态，配置与部署都最简单）；
#   → 拆分模式把功能各自编成 DLL（架构支持：它们只引用核心，互相 0 引用）。
#   两种模式的产物互斥：切换时会自动清掉另一种模式的残留 DLL，避免"功能被初始化两次"。
#   ★拆分模式的核心**编译时仍叫 SR_UCH.dll**（功能与 EX 的 rsp 都引用它），
#     全部编完之后才改名成 $splitCoreName.dll —— 只动文件名，程序集标识仍是 SR_UCH。
# 注：新增功能源码后，在 build\rsp\ 下建 feat_<功能>.rsp，
#     并把文件名加进下面的 $rspFeatures；合并模式的 sr_uch.rsp 也要补上该 .cs。
# 注：本机无 dotnet SDK 时会自动回退到 tasks\net472\csc.exe。
# ============================================================
param([switch]$All, [switch]$Split, [switch]$Check, [switch]$Deploy, [switch]$Separate, [string]$GameDir = '', [switch]$ExportAudit, [string]$PluginsDir = '', [string]$Root = (Split-Path -Parent $PSScriptRoot))
# 拆分模式核心的**文件名**（程序集标识仍是 SR_UCH，改名只动文件名）。
# 目的：与合并模式的 SR_UCH.dll 区分开 —— 否则同一个 bin\Release 里会有两份同名不同内容的核心。
$splitCoreName = 'SR_Manager'
$ErrorActionPreference = 'Stop'
# 仓库根 = build\ 的上一级（本脚本位于 <repo>\build\build.ps1）
$Root = Split-Path -Parent $PSScriptRoot

# ---- 游戏目录探测：-GameDir > 环境变量 UCH_DIR > 常见 Steam 路径 ----
if (-not $GameDir) { $GameDir = $env:UCH_DIR }
if (-not $GameDir) {
    $cands = @(
        'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Chicken Horse',
        'D:\Steam\steamapps\common\Ultimate Chicken Horse',
        'D:\EF\Steam\steamapps\common\Ultimate Chicken Horse',
        'E:\Steam\steamapps\common\Ultimate Chicken Horse'
    )
    foreach ($c in $cands) { if (Test-Path -LiteralPath (Join-Path $c 'UltimateChickenHorse_Data\Managed')) { $GameDir = $c; break } }
}
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'UltimateChickenHorse_Data\Managed'))) {
    Write-Host '找不到游戏目录：请用 -GameDir "…\Ultimate Chicken Horse" 或设置环境变量 UCH_DIR'
    exit 1
}
Write-Host ("游戏目录: " + $GameDir)
Set-Location $Root   # csc 的相对路径以仓库根为基准
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'bin\Release') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot '.cache') | Out-Null

# ---- 准备 rsp：把 %GAME% 替换成实际游戏路径，写入 build\.cache ----
$cache = Join-Path $PSScriptRoot '.cache'
if (-not (Test-Path -LiteralPath $cache)) { New-Item -ItemType Directory -Force -Path $cache | Out-Null }
$tplDir = Join-Path $PSScriptRoot 'rsp'
Get-ChildItem -LiteralPath $tplDir -Filter '*.rsp' | ForEach-Object {
    $txt = [System.IO.File]::ReadAllText($_.FullName).Replace('%GAME%', $GameDir)
    [System.IO.File]::WriteAllText((Join-Path $cache $_.Name), $txt, (New-Object System.Text.UTF8Encoding($true)))
}

# **Opt-24（审计 B1）**：这里原来把工程根写死成 'D:\RS_TFS\D\...'，而该目录在本机**根本不存在**
#  （工程实际在 D:\RS_TFS\CC）-> README 承诺的"一键构建"直接失败，且没有任何提示指向路径问题。
# 现在统一从脚本位置推导（$PSScriptRoot 的上一级 = 工程根），也可以用 -Root 显式指定：
#   .\build\build.ps1 -GameDir 'E:\...\Ultimate Chicken Horse'
# 工程整体挪目录 / 换机器都不用再改脚本。
if (-not (Test-Path -LiteralPath $Root)) { Write-Host "工程根不存在: $Root（可用 -Root 指定）"; exit 1 }
# csc 解析：优先用系统 dotnet SDK 自带的 Roslyn 编译器，其次工程内 vendored 工具链，最后 net472 csc.exe。
# 说明：_tools\roslyn_toolset 已删除，这里不再依赖它（只作为"哪天又放回来"的兜底路径）。
$csc = ''
try {
    foreach ($line in (& dotnet --list-sdks 2>$null)) {
        if ($line -match '^\s*(\S+)\s+\[(.+)\]\s*$') {
            $p = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
            if (Test-Path -LiteralPath $p) { $csc = $p; break }
        }
    }
} catch { }
if (-not $csc) {
    $vendor = Join-Path $Root '_tools\roslyn_toolset\tasks\netcore\bincore\csc.dll'
    if (Test-Path -LiteralPath $vendor) { $csc = $vendor }
}
if (-not $csc) {
    $net472 = Join-Path $Root '_tools\roslyn_toolset\tasks\net472\csc.exe'
    if (Test-Path -LiteralPath $net472) { $csc = $net472 }
}
if (-not $csc) { Write-Host '找不到 csc：需要 dotnet SDK（含 Roslyn），或把 roslyn_toolset 放回 _tools\'; exit 1 }
Write-Host "csc: $csc"
$rspDir = Join-Path $PSScriptRoot '.cache'
if ($Separate) { Write-Host '本仓库构建链不含 -Separate（契约程序集分离）：请改用本机 _tools 构建链（_tools\build_sr_uch.ps1 -Separate）'; exit 2 }
$rspMain = Join-Path $rspDir 'sr_uch.rsp'          # 合并模式：核心 + 全部功能
$rspCore = Join-Path $rspDir 'sr_uch_core.rsp'     # 拆分模式：仅核心

# 功能 DLL（拆分模式用；都引用核心 SR_UCH.dll，必须排在核心之后编译）
$rspFeatures = @(
    'feat_PlayerTracker.rsp',
    'feat_QuickAdjust.rsp',
    'feat_BuildingTools.rsp',
    'feat_DestroyBlocks.rsp',
    'feat_Level.rsp',
    'feat_Chat.rsp',
    'feat_Online.rsp',
    'feat_Experiments.rsp',
'feat_BuildingPresets.rsp',
'feat_HUD.rsp',
'feat_ConfigTools.rsp'
)
$outDir = Join-Path $Root 'bin\Release'

# dotnet 不可用时的回退已在上面解析 csc 时处理（net472 csc.exe）

function Invoke-Build([string]$rsp, [string]$name) {
    # 源码缺失（插件已移除/改名）时跳过，避免 -All 因历史 rsp 残留而整体失败
    $missing = @()
    if (Test-Path $rsp) {
        $srcs = Get-Content $rsp | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\.cs$' }
        foreach ($s in $srcs) { if (-not (Test-Path $s)) { $missing += $s } }
    } else {
        $missing += $rsp
    }
    if ($missing.Count -gt 0) {
        Write-Host "==> 跳过 $name（源码缺失: $($missing -join '; ')）"
        return
    }
    Write-Host "==> 编译 $name ..."
    if ($csc.EndsWith('.dll')) { & dotnet $csc "@$rsp" } else { & $csc "@$rsp" }
    if ($LASTEXITCODE -ne 0) { Write-Host "$name 编译失败"; exit 1 }
    Write-Host "    $name OK"
}

# 是否需要重编：输出 DLL 缺失，或 rsp 里任一 .cs 源文件比 DLL 新
function Test-NeedRebuild([string]$rsp, [string]$outDll) {
    if (-not (Test-Path $outDll)) { return $true }
    $dllTime = (Get-Item $outDll).LastWriteTime
    $srcs = Get-Content $rsp | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\.cs$' }
    foreach ($s in $srcs) {
        if (Test-Path $s) {
            if ((Get-Item $s).LastWriteTime -gt $dllTime) { return $true }
        }
    }
    return $false
}

# 从 rsp 里取 /out: 路径
function Get-OutPath([string]$rsp) {
    $line = Get-Content $rsp | Where-Object { $_ -match '^/out:' } | Select-Object -First 1
    if (-not $line) { return $null }
    return $line.Substring(5).Trim()
}

# ============================================================
# **Opt-41（可维护性）**：-Check —— 把"想起来才手工做一次"的核对变成机械守卫
#   & '.\_tools\build_sr_uch.ps1' -Check
# 三条守卫（都来自分析报告 §4 / §8 / §9 里手工做过一次的核对）：
#   1) 契约层 sr_uch_contracts.rsp 必须能**独立编译**
#      —— 这是"契约不依赖实现"这条架构约定的**唯一**机械证明；
#   2) sr_uch.rsp 必须**恰好**覆盖 SR_UCH 下所有 .cs（漏一个 = 该功能静默不进部署产物）；
#      核心 rsp 不得含 Features；每个 feat_<X>.rsp 必须与 X 的文件精确匹配；
#   3) IDE 工程（csproj）必须能编 —— 它和部署构建各有一份引用清单，最容易漂移。
# ============================================================
function Invoke-ContractCheck {
    $rsp = Join-Path $rspDir 'sr_uch_contracts.rsp'
    if (-not (Test-Path $rsp)) { Write-Host '  [FAIL] 缺少 sr_uch_contracts.rsp'; return $false }
    if ($csc.EndsWith('.dll')) { & dotnet $csc "@$rsp" 2>&1 | Out-Null } else { & $csc "@$rsp" 2>&1 | Out-Null }
    if ($LASTEXITCODE -ne 0) { Write-Host '  [FAIL] 契约层不能独立编译 -> 契约文件里引用了实现'; return $false }
    Write-Host '  [ OK ] 契约层（SR.Contracts.cs）可独立编译'
    return $true
}
function Get-RspSources([string]$rsp) {
    if (-not (Test-Path $rsp)) { return @() }
    Get-Content $rsp | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '\.cs$' } | ForEach-Object {
        $_.Replace('/', '\').Replace($Root, '').TrimStart('\')
    }
}
function Invoke-RspCoverageCheck {
    $ok = $true
    $srcRoot = $Root
    $disk = @(Get-ChildItem $srcRoot -Recurse -Include *.cs -File |
        Where-Object { $_.FullName -notmatch '\\Others\\' } |   # Others 是独立插件，不属于主模块 rsp
        Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
        ForEach-Object { $_.FullName.Replace($Root, '').TrimStart('\') })
    $main = @(Get-RspSources (Join-Path $rspDir 'sr_uch.rsp'))
    $miss  = @($disk | Where-Object { $main -notcontains $_ })
    $extra = @($main | Where-Object { $disk -notcontains $_ })
    if ($miss.Count)  { Write-Host ('  [FAIL] sr_uch.rsp 漏了源文件: ' + ($miss -join ', ')); $ok = $false }
    if ($extra.Count) { Write-Host ('  [FAIL] sr_uch.rsp 多出源文件: ' + ($extra -join ', ')); $ok = $false }
    if ($miss.Count -eq 0 -and $extra.Count -eq 0) { Write-Host ('  [ OK ] sr_uch.rsp 精确覆盖全部 ' + $disk.Count + ' 个源文件') }
    $core = @(Get-RspSources (Join-Path $rspDir 'sr_uch_core.rsp'))
    $coreFeat = @($core | Where-Object { $_ -match '\\Features\\' })
    if ($coreFeat.Count) { Write-Host ('  [FAIL] sr_uch_core.rsp 误收 Features: ' + ($coreFeat -join ', ')); $ok = $false }
    else { Write-Host '  [ OK ] sr_uch_core.rsp 只含核心（不含 Features）' }
    $diskFeat = @($disk | Where-Object { $_ -match '\\Features\\' } | ForEach-Object { Split-Path $_ -Leaf })
    $featOk = $true
    foreach ($f in Get-ChildItem $rspDir -Filter 'feat_*.rsp') {
        $base = $f.BaseName.Substring(5)
        $got = @(Get-RspSources $f.FullName | Where-Object { $_ -match '\\Features\\' } | ForEach-Object { Split-Path $_ -Leaf })
        $exp = @($diskFeat | Where-Object { ($_ -replace '\.cs$','') -eq $base -or ($_ -replace '\.cs$','') -like ($base + '.*') })
        $d1 = @($exp | Where-Object { $got -notcontains $_ })
        $d2 = @($got | Where-Object { $exp -notcontains $_ })
        if ($d1.Count -or $d2.Count) {
            Write-Host ('  [FAIL] ' + $f.Name + ' 与功能文件不匹配（缺: ' + ($d1 -join ',') + ' 多: ' + ($d2 -join ',') + '）')
            $ok = $false; $featOk = $false
        }
    }
    if ($featOk) { Write-Host '  [ OK ] 全部 feat_*.rsp 与各自功能文件精确匹配' }
    return $ok
}
function Invoke-CsprojCheck {
    $csproj = Join-Path $Root 'SR_UCH.csproj'
    if (-not (Test-Path $csproj)) { Write-Host '  [FAIL] 缺少 SR_UCH.csproj'; return $false }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host '  [SKIP] 本机没有 dotnet，跳过 IDE 工程检查'; return $true }
    $o = & dotnet build $csproj -v:q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  [FAIL] IDE 工程（csproj）编译失败：'
        $o | Select-Object -Last 8 | ForEach-Object { Write-Host ('        ' + $_) }
        return $false
    }
    Write-Host '  [ OK ] IDE 工程（csproj）编译通过（两条构建链的引用清单一致）'
    return $true
}
function Invoke-MigrationRuleCheck {
    # **Opt-43（可维护性）**：迁移的"清理/搬移"名单是手工维护的。一旦名单里的键**后来又被打回来**
    #  （R432 就把 Level Bounds Cursor / Camera 加回来了），迁移就会每次启动把用户的设置删掉，
    #  而且没有任何报错。这里在**构建期**做静态交叉核对：名单里的 (段,键) 不得同时出现在 Config.Bind 里。
    $sep = [string][char]1
    $binds = @{}
      foreach ($d in @('.')) {
          $r2 = $Root
        if (-not (Test-Path $r2)) { continue }
        Get-ChildItem $r2 -Recurse -Include *.cs -File |
            Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } | ForEach-Object {
                $txt = Get-Content $_.FullName -Raw
                foreach ($m in [regex]::Matches($txt, '\.Bind(?:<[^<>()]*>)?\s*\(\s*"([^"]+)"\s*,\s*"([^"]+)"')) {
                    $binds[$m.Groups[1].Value + $sep + $m.Groups[2].Value] = $true
                }
            }
    }
    $migPath = Join-Path $Root 'SR_UCH\SR.ConfigMigration.cs'
    if (-not (Test-Path $migPath)) { Write-Host '  [FAIL] 找不到 SR.ConfigMigration.cs'; return $false }
    $mig = Get-Content $migPath -Raw
    $ok = $true
    foreach ($m in [regex]::Matches($mig, 'RemoveKeys\s*\(\s*text\s*,\s*"([^"]+)"\s*,\s*new\[\]\s*\{([^}]*)\}')) {
        $sec = $m.Groups[1].Value
        foreach ($k in [regex]::Matches($m.Groups[2].Value, '"([^"]+)"')) {
            $key = $k.Groups[1].Value
            if ($binds.ContainsKey($sec + $sep + $key)) {
                Write-Host ('  [FAIL] 迁移清理名单撞上活键: [' + $sec + '] ' + $key + '（当前仍有 Config.Bind -> 该设置每次启动会被删掉）')
                $ok = $false
            }
        }
    }
    foreach ($m in [regex]::Matches($mig, 'MoveKeyBetweenSections\s*\(\s*text\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"')) {
        $from = $m.Groups[1].Value; $key = $m.Groups[3].Value
        if ($binds.ContainsKey($from + $sep + $key)) {
            Write-Host ('  [FAIL] 迁移搬移的源段仍是活段: [' + $from + '] ' + $key + '（该键在被搬的同时仍被绑定）')
            $ok = $false
        }
    }
    if ($ok) { Write-Host ('  [ OK ] 迁移规则与 ' + $binds.Count + ' 条 Config.Bind 无冲突（清理名单未撞上活键）') }
    return $ok
}
function Invoke-ApiSurfaceCheck {
    # 微内核 API 棘轮：功能层只允许使用 build\api_surface.txt 里的 SRApi.* 成员，新增耦合会被拦下。
    $api = Join-Path $PSScriptRoot 'api_surface.txt'
    if (-not (Test-Path -LiteralPath $api)) { Write-Host '  [FAIL] 缺少 build\api_surface.txt'; return $false }
    $allow = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($api)) { $s = $l.Trim(); if ($s -and -not $s.StartsWith('#')) { $allow[$s] = $true } }
    $bad = @{}
    $featDir = Join-Path $Root 'Features'
    if (Test-Path -LiteralPath $featDir) {
        foreach ($f in Get-ChildItem -LiteralPath $featDir -Filter '*.cs' -File) {
            $txt = [System.IO.File]::ReadAllText($f.FullName)
            foreach ($m in [regex]::Matches($txt, '\bSRApi\.(\w+)')) {
                $n = $m.Groups[1].Value
                if (-not $allow.ContainsKey($n)) { $bad[$n] = $true }
            }
        }
    }
    if ($bad.Count -gt 0) {
        Write-Host ('  [FAIL] 功能层使用了白名单外的 SRApi.* 成员: ' + (($bad.Keys | Sort-Object) -join ', ') + '（要么改代码，要么加进 api_surface.txt）')
        return $false
    }
    Write-Host ('  [ OK ] 功能层只使用白名单内的 API（' + $allow.Count + ' 个）')
    return $true
}
function Invoke-ContractBoundaryCheck {
    # 微内核 B2：契约层只允许依赖 BCL / 引擎 / BepInEx，不得出现实现层标识。
    $file = Join-Path $Root 'SR_UCH\SR.Contracts.cs'
    if (-not (Test-Path -LiteralPath $file)) { Write-Host '  [FAIL] 缺少 SR.Contracts.cs'; return $false }
    $bad = @()
    $ln = 0
    foreach ($line in [System.IO.File]::ReadAllLines($file)) {
        $ln++
        $s = $line.Trim()
        if ($s.StartsWith('//') -or $s.StartsWith('*') -or $s.StartsWith('/*')) { continue }
        $code = [regex]::Replace($s, '"(?:[^"\\]|\\.)*"', '""')   # 去掉字符串字面量
        $i = $code.IndexOf('//'); if ($i -ge 0) { $code = $code.Substring(0, $i) }   # 去掉行尾注释
        if ([regex]::IsMatch($code, '\b(MainPlugin|ConfigMigration|SRHost)\b') -or
            [regex]::IsMatch($code, '(?<![A-Za-z_.])SR\.', [System.Text.RegularExpressions.RegexOptions]::None)) {
            $bad += ("第 $ln 行: " + $s)
        }
    }
    if ($bad.Count -gt 0) {
        Write-Host ('  [FAIL] 契约层引用了实现层标识：')
        $bad | Select-Object -First 5 | ForEach-Object { Write-Host ('        ' + $_) }
        return $false
    }
    Write-Host '  [ OK ] 契约层不含实现层标识（边界可验证）'
    return $true
}
function Invoke-FeatureImplLeakCheck {
    # 阶段B：功能层不得出现任何实现层标识（ConfigMigration / MainPlugin / SRHost / SR.*），只能走契约。
    $dir = Join-Path $Root 'Features'
    $bad = @()
    if (Test-Path -LiteralPath $dir) {
        foreach ($f in Get-ChildItem -LiteralPath $dir -Filter '*.cs' -File) {
            $ln = 0
            foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
                $ln++
                $s = $line.Trim()
                if ($s.StartsWith('//') -or $s.StartsWith('*') -or $s.StartsWith('/*')) { continue }
                $code = [regex]::Replace($s, '"(?:[^"\\]|\\.)*"', '""')
                $i = $code.IndexOf('//'); if ($i -ge 0) { $code = $code.Substring(0, $i) }
                if ([regex]::IsMatch($code, '\b(ConfigMigration|MainPlugin)\b') -or
                    [regex]::IsMatch($code, '(?<![A-Za-z_.])SR\.')) {
                    $bad += ($f.Name + ":$ln  " + $s)
                }
            }
        }
    }
    if ($bad.Count -gt 0) {
        Write-Host '  [FAIL] 功能层引用了实现层标识（应只走 SRApi/Slots）：'
        $bad | Select-Object -First 5 | ForEach-Object { Write-Host ('        ' + $_) }
        return $false
    }
    Write-Host '  [ OK ] 功能层只依赖契约（无实现层标识）'
    return $true
}
function Invoke-SinkCoverageCheck {
    # P0-1：契约层每个 Set*Sinks 注入点都必须在 MainPlugin 里被调用（静态可验证，不需要跑游戏）。
    $contract = Join-Path $Root 'SR_UCH\SR.Contracts.cs'
    $main = Join-Path $Root 'MainPlugin.cs'
    if (-not (Test-Path -LiteralPath $contract) -or -not (Test-Path -LiteralPath $main)) { Write-Host '  [FAIL] 缺少契约层或 MainPlugin'; return $false }
    $ctext = [System.IO.File]::ReadAllText($contract)
    $mtext = [System.IO.File]::ReadAllText($main)
    $bad = @(); $total = 0
    foreach ($m in [regex]::Matches($ctext, 'public static void (Set\w+Sinks)\s*\(')) {
        $total++
        $name = $m.Groups[1].Value
        if ($mtext -notmatch [regex]::Escape($name + '(')) { $bad += $name }
    }
    if ($bad.Count -gt 0) {
        Write-Host ('  [FAIL] MainPlugin 未调用注入点: ' + ($bad -join ', '))
        return $false
    }
    Write-Host ('  [ OK ] 注入点全部已调用（' + $total + ' 组）')
    return $true
}
if ($Check) {
    Write-Host '== SR_UCH 机械守卫（-Check）=='
    $allOk = $true
    if (-not (Invoke-ContractCheck))      { $allOk = $false }
    if (-not (Invoke-RspCoverageCheck))   { $allOk = $false }
    if (-not (Invoke-MigrationRuleCheck)) { $allOk = $false }
    if (-not (Invoke-CsprojCheck))        { $allOk = $false }
    if (-not (Invoke-ApiSurfaceCheck))    { $allOk = $false }
    if (-not (Invoke-ContractBoundaryCheck)) { $allOk = $false }
    if (-not (Invoke-FeatureImplLeakCheck)) { $allOk = $false }
    if (-not (Invoke-SinkCoverageCheck)) { $allOk = $false }
    if (-not $allOk) { Write-Host '== 有守卫未通过 ❌ =='; exit 1 }
    Write-Host '== 全部守卫通过 ✅ =='
    exit 0
}

if ($Separate) {
    $rspContracts = Join-Path (Join-Path $Root '_tools\rsp') 'sr_uch_contracts.rsp'
    Invoke-Build $rspContracts 'SR.Contracts.dll'
}
if (-not $Split) {
    # ---- 合并模式（默认，发布形态）----
    Invoke-Build $rspMain 'SR_UCH.dll'
    # 清掉拆分模式的残留：否则 plugins 里会同时有"合并版功能 + 独立功能 DLL"，功能会被初始化两次
    $stale = Get-ChildItem $outDir -Filter 'SR_UCH_*.dll' -ErrorAction SilentlyContinue
    if ($stale) {
        $stale | ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "    已清理拆分模式残留: $($_.Name)" }
    }
    # 拆分模式的核心（改名后的那份）同样要清，否则同一个 bin\Release 里躺着两份核心
    foreach ($n in @($splitCoreName, 'SR_UCH_Manager')) {
        $sp = Join-Path $outDir ($n + '.dll')
        if (Test-Path $sp) { Remove-Item $sp -Force; Write-Host "    已清理拆分模式残留: $($n).dll" }
    }
    Write-Host "合并模式产物: SR_UCH.dll（核心 + 8 个功能）"
} else {
    # ---- 拆分模式：核心 + 每个功能各自一个 DLL ----
    Invoke-Build $rspCore 'SR_UCH.dll (核心)'
    $coreDll = Get-OutPath $rspCore
    $coreTime = (Get-Item $coreDll).LastWriteTime
    $featBuilt = 0
    foreach ($r in $rspFeatures) {
        $rsp = Join-Path $rspDir $r
        if (-not (Test-Path $rsp)) { Write-Host "跳过（缺少 $r）"; continue }
        $out = Get-OutPath $rsp
        $need = (Test-NeedRebuild $rsp $out) -or ((Get-Item $out).LastWriteTime -lt $coreTime)
        if ($need) {
            Invoke-Build $rsp ([System.IO.Path]::GetFileName($out))
            $featBuilt++
        }
    }
    Write-Host "拆分模式产物: $splitCoreName.dll(核心) + $featBuilt 个功能 DLL（核心在收尾时改名）"
}

if ($All) { Write-Host '（本仓库只含主模块，EX/独立插件不在其中）' }


# ---- 拆分模式收尾：核心改名 ----
# 必须放在最末尾：功能与 EX 的 rsp 都引用 SR_UCH.dll，改名早了它们就找不到引用程序集。
if ($Split) {
    $built  = Join-Path $outDir 'SR_UCH.dll'
    $target = Join-Path $outDir ($splitCoreName + '.dll')
    if (Test-Path $built) {
        Move-Item $built $target -Force
        Write-Host "拆分模式：核心已改名 -> $($splitCoreName).dll（程序集标识仍是 SR_UCH）"
    } else {
        Write-Host "拆分模式：未找到 $built，跳过改名"
    }
}

# ---- **Opt-47（方便维护）**：-Deploy = 构建完直接复制进游戏的 BepInEx\plugins ----
# plugins 目录从 csproj 的 UchManagedDir 推导（单一真值），也可用 -PluginsDir 显式指定。
# 游戏运行时插件 DLL 被映射进进程 -> 复制必然失败；这里把它翻译成一句人话，而不是抛 IOException。
function Get-PluginsDir {
    if ($PluginsDir) { return $PluginsDir }
    $csproj = Join-Path $Root 'SR_UCH.csproj'
    if (Test-Path $csproj) {
        $m = [regex]::Match((Get-Content $csproj -Raw), '<UchManagedDir[^>]*>([^<]+)</UchManagedDir>')
        if ($m.Success) {
            $managed = $m.Groups[1].Value.Trim()
            $game = Split-Path (Split-Path $managed -Parent) -Parent   # ...\<游戏>\<游戏>_Data\Managed -> ...\<游戏>
            return (Join-Path $game 'BepInEx\plugins')
        }
    }
    return ''
}
if ($Deploy) {
    $dll = Join-Path $outDir 'SR_UCH.dll'
    if (-not (Test-Path $dll)) { Write-Host "找不到产物: $dll（先去掉 -Deploy 跑一次构建）"; exit 1 }
    $plugins = Get-PluginsDir
    if (-not $plugins -or -not (Test-Path $plugins)) {
        Write-Host "找不到 plugins 目录；请用 -PluginsDir '<游戏>\BepInEx\plugins' 指定"
        exit 1
    }
    $target = Join-Path $plugins 'SR_UCH.dll'
    try {
        Copy-Item $dll $target -Force -ErrorAction Stop
        Write-Host ("已部署: " + $target)
    } catch {
        Write-Host "部署失败：插件 DLL 正被占用 —— 请**先完全退出游戏**再重跑 -Deploy"
        Write-Host ("  目标: " + $target)
        Write-Host ("  原因: " + $_.Exception.Message)
        exit 2
    }
    # **Opt-53（方便维护）**：EX 也是同一套部署目标（它由 SR_UCH 从 plugins 目录加载）。
    #  以前 -Deploy 只搬 SR_UCH.dll -> 只改 EX 源码时很容易忘记搬，plugins 里会留着旧 EX
    #  （真发生过：plugins 里是 10/6 的旧 EX 配新 SR）。这里一并搬。
    #  注意：Others\ 下的独立插件**不在** SR 这套里（用户已说明不用管），不在这里处理。
    $exDll = Join-Path $Root 'SR_UCH_EX\bin\Release\SR_UCH_EX.dll'
    $exTarget = Join-Path $plugins 'SR_UCH_EX.dll'
    if ((Test-Path $exDll) -and (Test-Path $exTarget)) {
        $h1 = (Get-FileHash $exDll -Algorithm SHA256).Hash
        $h2 = (Get-FileHash $exTarget -Algorithm SHA256).Hash
        if ($h1 -eq $h2) {
            Write-Host ("EX 已是最新: " + $exTarget)
        } else {
            try {
                Copy-Item $exDll $exTarget -Force -ErrorAction Stop
                Write-Host ("已部署: " + $exTarget)
            } catch {
                Write-Host ("EX 部署失败（被占用？）：" + $_.Exception.Message)
            }
        }
    }
}
if ($Separate -and $Deploy) {
    # 阶段B1：分离模式下契约程序集作为依赖放进 BepInEx\core（BepInEx 会先加载 core 里的程序集）
    $coreDir = Join-Path (Join-Path 'D:\EF\Steam\steamapps\common\Ultimate Chicken Horse' 'BepInEx') 'core'
    $srcC = Join-Path $Root 'bin\Release\SR.Contracts.dll'
    if (Test-Path -LiteralPath $srcC) {
        if (-not (Test-Path -LiteralPath $coreDir)) { New-Item -ItemType Directory -Force -Path $coreDir | Out-Null }
        Copy-Item -LiteralPath $srcC -Destination (Join-Path $coreDir 'SR.Contracts.dll') -Force
        Write-Host ('已部署契约程序集: ' + (Join-Path $coreDir 'SR.Contracts.dll'))
    }
}
# P1-2：从运行日志导出启动审计报告（补丁/反射/功能表/API通道/自检结论）
if ($ExportAudit) {
    $log = Join-Path $GameDir 'BepInEx\LogOutput.log'
    if (-not (Test-Path -LiteralPath $log)) { Write-Host ('找不到日志: ' + $log); exit 1 }
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
    [void]$sb.AppendLine('- 导出时间: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    $dll = Join-Path $GameDir 'BepInEx\plugins\SR_UCH.dll'
    if (Test-Path -LiteralPath $dll) { [void]$sb.AppendLine('- SR_UCH.dll SHA256(前16位): ' + (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0,16)) }
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## 审计行')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('```')
    foreach ($l in $lines) { if ($l -match '\[SR\.(SelfTest|Gate|Cfg|Audit|Patch|Ref)\]') { [void]$sb.AppendLine($l.Trim()) } }
    [void]$sb.AppendLine('```')
    [IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ('已导出: ' + $out)
    exit 0
}
