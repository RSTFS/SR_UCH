# 结构守卫：槽位图 + 本地化覆盖。
# 由 build.ps1 -Check 与 _tools/build_sr_uch.ps1 -Check 调用；-WriteBaseline 用来重写基线。
param([switch]$WriteBaseline)

$Root = Split-Path -Parent $PSScriptRoot
$ok = $true
function Ok2([string]$m)  { Write-Host ('  [ OK ] ' + $m) }
function Bad2([string]$m) { Write-Host ('  [FAIL] ' + $m); $script:ok = $false }

$files = @()
foreach ($d in @('Features','SR_UCH')) {
    $p = Join-Path $Root $d
    if (Test-Path -LiteralPath $p) { $files += @(Get-ChildItem -LiteralPath $p -Filter *.cs -File) }
}
$texts = @{}
$consts = @{}
foreach ($f in $files) {
    $t = [IO.File]::ReadAllText($f.FullName)
    $texts[$f.Name] = $t
    foreach ($m in [regex]::Matches($t, '(?m)^\s*(?:private|internal|public)\s+(?:static\s+)?(?:readonly\s+)?const\s+string\s+(\w+)\s*=\s*"([^"]+)"')) {
        $consts[$f.Name + '|' + $m.Groups[1].Value] = $m.Groups[2].Value
    }
}

function Resolve2([string]$file, [string]$arg) {
    $a = $arg.Trim()
    if ($a.StartsWith('"')) { return $a.Trim('"') }
    $k = $file + '|' + $a
    if ($consts.ContainsKey($k)) { return $consts[$k] }
    $short = ($a -split '\.')[-1]
    foreach ($e in $consts.GetEnumerator()) {
        if ($e.Key.EndsWith('|' + $short)) { return $e.Value }
    }
    return $null
}

# ---------- 1) 槽位图 ----------
$provided = @{}
$required = @{}
$attrProv = @()
$attrReq  = @()
$dynamic  = 0
foreach ($name in $texts.Keys) {
    $t = $texts[$name]
    foreach ($m in [regex]::Matches($t, 'Slots\.Provide\(\s*([^,\)]+)')) {
        $v = Resolve2 $name $m.Groups[1].Value
        if ($v) { $provided[$v] = $name } else { $dynamic++ }
    }
    foreach ($m in [regex]::Matches($t, 'Slots\.Require<[^(]*>\(\s*([^,\)]+)')) {
        $v = Resolve2 $name $m.Groups[1].Value
        if ($v) { $required[$v] = $name } else { $dynamic++ }
    }
    foreach ($m in [regex]::Matches($t, 'Provides\s*=\s*new\s+string\[\]\s*\{(.*?)\}', [Text.RegularExpressions.RegexOptions]::Singleline)) {
        foreach ($s in [regex]::Matches($m.Groups[1].Value, '"([^"]+)"')) { $attrProv += $s.Groups[1].Value }
    }
    foreach ($m in [regex]::Matches($t, 'Requires\s*=\s*new\s+string\[\]\s*\{(.*?)\}', [Text.RegularExpressions.RegexOptions]::Singleline)) {
        foreach ($s in [regex]::Matches($m.Groups[1].Value, '"([^"]+)"')) { $attrReq += $s.Groups[1].Value }
    }
}
$optPath = Join-Path $PSScriptRoot 'slot-optional.txt'
$optional = @{}
if (Test-Path -LiteralPath $optPath) {
    foreach ($l in [IO.File]::ReadAllLines($optPath)) {
        $s = $l.Trim()
        if ($s -and -not $s.StartsWith('#')) { $optional[$s] = $true }
    }
}
$ghost = @($attrProv | Where-Object { -not $provided.ContainsKey($_) } | Sort-Object -Unique)
$need  = @($attrReq) + @($required.Keys)
$missAll = @($need | Where-Object { -not $provided.ContainsKey($_) } | Sort-Object -Unique)
$miss = @($missAll | Where-Object { -not $optional.ContainsKey($_) })
$optUsed = @($missAll | Where-Object { $optional.ContainsKey($_) })
if ($ghost.Count) { Bad2 ('槽位声明了 Provide 但代码里没有: ' + ($ghost -join ', ')) }
if ($miss.Count)  { Bad2 ('需要但没有 Provider 的槽位: ' + ($miss -join ', ')) }
if (-not $ghost.Count -and -not $miss.Count) {
    Ok2 ('槽位图完整（提供 ' + $provided.Count + ' 个 / 需要 ' + ($need | Sort-Object -Unique).Count + ' 个；可选外部槽位 ' + $optUsed.Count + ' 个；动态 ' + $dynamic + ' 处未静态核对）')
}

# ---------- 1b) 孤儿槽位棘轮：Provide 了但全仓库没有任何 Require（只减不增） ----------
$needSet = @{}
foreach ($k in $need) { $needSet[$k] = $true }
$orphans = @($provided.Keys | Where-Object { -not $needSet.ContainsKey($_) } | Sort-Object -Unique)
$orPath = Join-Path $PSScriptRoot 'slot-orphan.txt'
$orBase = @{}
if (Test-Path -LiteralPath $orPath) {
    foreach ($l in [IO.File]::ReadAllLines($orPath)) {
        $s = $l.Trim()
        if ($s -and -not $s.StartsWith('#')) { $orBase[$s] = $true }
    }
}
if ($WriteBaseline) {
    [IO.File]::WriteAllText($orPath, ((@('# 孤儿槽位基线：Provide 了但本仓库没有 Require 的槽位名；只减不增。',
        '# 注意：本守卫只扫 SR_UCH 仓库；被独立 EX 模块消费的槽位会出现在这里（属正常，不要删空）。') + $orphans) -join "`n") + "`n", (New-Object Text.UTF8Encoding($true)))
    Write-Host ('已写入孤儿槽位基线: ' + $orPath + '（' + $orphans.Count + ' 条）')
} else {
    $orNew = @($orphans | Where-Object { -not $orBase.ContainsKey($_) })
    $orStale = @($orBase.Keys | Where-Object { $orphans -notcontains $_ })
    if ($orNew.Count) { Bad2 ('新增提供但无人 Require 的槽位: ' + ($orNew -join ', ')) }
    if ($orStale.Count) { Write-Host ('  [ 注意 ] slot-orphan.txt 里有 ' + $orStale.Count + ' 条已不再孤儿，可删') }
    if (-not $orNew.Count) { Ok2 ('无新增孤儿槽位（提供但无人 Require：' + $orphans.Count + ' 个，均在基线内）') }
}

# ---------- 2) 本地化覆盖（棘轮：基线内的允许，新增必须失败） ----------
$bindKeys = @{}
$lockPairs = @{}
$lockKeys = @{}
foreach ($name in $texts.Keys) {
    $t = $texts[$name]
    $bk = @()
    foreach ($m in [regex]::Matches($t, 'Bind(?:<[^>]*>)?\(\s*("[^"]+"|[\w.]+)\s*,\s*"([^"]+)"\s*(\+?)')) {
        $secRaw = $m.Groups[1].Value
        if ($secRaw.Trim('"') -eq 'Internal') { continue }          # Internal 段是迁移/内部标记，不面向用户
        if ($m.Groups[3].Value -eq '+') { continue }                 # 动态拼接的键（"Page " + n）
        $bk += ($secRaw + '|' + $m.Groups[2].Value)
    }
    $bindKeys[$name] = $bk
    $lk = @()
    foreach ($m in [regex]::Matches($t, 'LocKey\(\s*("[^"]+"|[\w.]+)\s*,\s*"([^"]+)"')) { $lk += $m.Groups[2].Value }
    foreach ($m in [regex]::Matches($t, '\bLoc\(\s*"([^"]+)"')) { $lk += $m.Groups[1].Value }
    $lockKeys[$name] = @($lk | Sort-Object -Unique)
    foreach ($m in [regex]::Matches($t, 'LocKey\(\s*("[^"]+"|[\w.]+)\s*,\s*"([^"]+)"')) {
        $lockPairs[(($m.Groups[1].Value.Trim('"')) + '|' + $m.Groups[2].Value)] = $true
    }
}
$missing = @()
foreach ($name in $bindKeys.Keys) {
    foreach ($pair in $bindKeys[$name]) {
        $parts = $pair.Split('|')
        $sec = $parts[0].Trim('"'); $key = $parts[1]
        if ($lockKeys[$name] -contains $key) { continue }
        if ($lockPairs.ContainsKey($sec + '|' + $key)) { continue }
        $missing += ($name + '|' + $sec + '|' + $key)
    }
}
$missing = @($missing | Sort-Object -Unique)
$basePath = Join-Path $PSScriptRoot 'loc-coverage.txt'
if ($WriteBaseline) {
    [IO.File]::WriteAllText($basePath, ((@('# 本地化覆盖基线：这些 Config.Bind 没有对应 LocKey（占位/内部/伴生键），只减不增。') + $missing) -join "`n") + "`n", (New-Object Text.UTF8Encoding($true)))
    Write-Host ('已写入基线: ' + $basePath + '（' + $missing.Count + ' 条）')
    exit 0
}
$base = @()
if (Test-Path -LiteralPath $basePath) {
    foreach ($l in [IO.File]::ReadAllLines($basePath)) {
        $s = $l.Trim()
        if ($s -and -not $s.StartsWith('#')) { $base += $s }
    }
}
$new = @($missing | Where-Object { $base -notcontains $_ })
$stale = @($base | Where-Object { $missing -notcontains $_ })
if ($new.Count) { Bad2 ('新增未登记本地化的绑定: ' + (($new | Select-Object -First 5) -join '; ')) }
if (-not $new.Count) { Ok2 ('本地化覆盖未退化（缺登记 ' + $missing.Count + ' 条，均在基线内）') }
if ($stale.Count) { Write-Host ('  [ 注意 ] 基线里有 ' + $stale.Count + ' 条已不再缺失，可更新 build/loc-coverage.txt') }

if (-not $ok) { exit 1 }
exit 0
