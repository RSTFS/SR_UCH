# 文本类机械守卫（不需要游戏 DLL / 不需要 csc）：可在本地与 GitHub Actions 直接运行
# 用法：pwsh build\guards-text.ps1      退出码 0 = 全绿，1 = 有失败
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$fail = 0
function Ok($m)   { Write-Host ("  [ OK ] " + $m) }
function Bad($m)  { Write-Host ("  [FAIL] " + $m); $script:fail++ }

function Read-Text([string]$p) { return [System.IO.File]::ReadAllText($p) }

# ---------- 1) API 棘轮：功能层只允许使用白名单里的 SR.* 成员 ----------
function Check-ApiSurface {
    $api = Join-Path $Root 'build\api_surface.txt'
    $feat = Join-Path $Root 'Features'
    if (-not (Test-Path $api)) { Bad '缺少 build\api_surface.txt'; return }
    $allow = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($api)) {
        $s = $l.Trim(); if ($s -and -not $s.StartsWith('#')) { $allow[$s] = $true }
    }
    $bad = @{}
    foreach ($f in Get-ChildItem -LiteralPath $feat -Filter '*.cs' -File) {
        $txt = Read-Text $f.FullName
        foreach ($m in [regex]::Matches($txt, '\bSRApi\.(\w+)')) {
            $n = $m.Groups[1].Value
            if (-not $allow.ContainsKey($n)) { $bad[$n] = $true }
        }
    }
    if ($bad.Count -gt 0) { Bad ('功能层使用了白名单外的 SRApi.* 成员: ' + (($bad.Keys | Sort-Object) -join ', ')) }
    else { Ok ('API 棘轮：功能层只使用白名单内的 SRApi.* 成员（白名单 ' + $allow.Count + ' 个）') }
}

# ---------- 2) 契约层不得出现实现层标识 ----------
function Check-ContractBoundary {
    $f = Join-Path $Root 'SR_UCH\SR.Contracts.cs'
    if (-not (Test-Path $f)) { Bad '缺少 SR_UCH\SR.Contracts.cs'; return }
    $bad = @(); $ln = 0
    foreach ($line in [System.IO.File]::ReadAllLines($f)) {
        $ln++
        $s = $line.Trim()
        if ($s.StartsWith('//') -or $s.StartsWith('*') -or $s.StartsWith('/*')) { continue }
        $code = [regex]::Replace($s, '"(?:[^"\\]|\\.)*"', '""')
        $i = $code.IndexOf('//'); if ($i -ge 0) { $code = $code.Substring(0, $i) }
        if ([regex]::IsMatch($code, '\b(MainPlugin|ConfigMigration|SRHost)\b') -or
            [regex]::IsMatch($code, '(?<![A-Za-z_.])SR\.')) { $bad += ("第 $ln 行: " + $s) }
    }
    if ($bad.Count -gt 0) { Bad ('契约层引用了实现层标识: ' + ($bad[0])) }
    else { Ok '契约层不含实现层标识（边界可验证）' }
}

# ---------- 3) 功能层不得出现实现层标识 ----------
function Check-FeatureLeak {
    $dir = Join-Path $Root 'Features'
    $bad = @()
    foreach ($f in Get-ChildItem -LiteralPath $dir -Filter '*.cs' -File) {
        $ln = 0
        foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
            $ln++
            $s = $line.Trim()
            if ($s.StartsWith('//') -or $s.StartsWith('*') -or $s.StartsWith('/*')) { continue }
            $code = [regex]::Replace($s, '"(?:[^"\\]|\\.)*"', '""')
            $i = $code.IndexOf('//'); if ($i -ge 0) { $code = $code.Substring(0, $i) }
            if ([regex]::IsMatch($code, '\b(ConfigMigration|MainPlugin)\b') -or
                [regex]::IsMatch($code, '(?<![A-Za-z_.])SR\.')) { $bad += ($f.Name + ':' + $ln) }
        }
    }
    if ($bad.Count -gt 0) { Bad ('功能层引用了实现层标识: ' + (($bad | Select-Object -First 5) -join ', ')) }
    else { Ok '功能层只依赖契约（无实现层标识）' }
}

# ---------- 4) rsp 覆盖一致性（文本比对，不编译）----------
function Check-RspCoverage {
    $rsp = Join-Path $Root 'build\rsp\sr_uch.rsp'
    if (-not (Test-Path $rsp)) { Bad '缺少 build\rsp\sr_uch.rsp'; return }
    # 统一用 / 作为分隔符比较：rsp 模板里写的是 Windows 风格的 \，而 Linux/macOS 上 FullName 用 /。
    $listed = @(Get-Content $rsp | ForEach-Object { ($_.Trim() -replace '\\', '/') } | Where-Object { $_ -and -not $_.StartsWith('/') })
    $disk = @()
    foreach ($d in @('SR_UCH', 'Features')) {
        $dir = Join-Path $Root $d
        if (Test-Path $dir) {
            $disk += Get-ChildItem -LiteralPath $dir -Recurse -Filter '*.cs' -File |
                ForEach-Object { ($_.FullName.Substring($Root.Length) -replace '\\', '/').TrimStart('/') }
        }
    }
    $disk += 'MainPlugin.cs'
    $miss = @($disk | Where-Object { $listed -notcontains $_ })
    $extra = @($listed | Where-Object { $disk -notcontains $_ })
    if ($miss.Count -gt 0) { Bad ('sr_uch.rsp 漏了源文件: ' + (($miss | Select-Object -First 6) -join ', ')) }
    if ($extra.Count -gt 0) { Bad ('sr_uch.rsp 多出源文件: ' + (($extra | Select-Object -First 6) -join ', ')) }
    if ($miss.Count -eq 0 -and $extra.Count -eq 0) { Ok ('rsp 精确覆盖全部 ' + $disk.Count + ' 个源文件') }
}

# ---------- 5) 编码：全部文本文件必须是合法 UTF-8 ----------
function Check-Encoding {
    $bad = @()
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File -Include *.cs, *.ps1, *.rsp, *.md, *.txt, *.json) {
        $full = ($f.FullName -replace '\\', '/')
        if ($full -match '/bin/|/obj/|/\.cache/|/\.git/') { continue }
        try {
            $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
            [void]$utf8.GetString($bytes)
        } catch { $bad += ($f.FullName.Substring($Root.Length) -replace '\\', '/') }
    }
    if ($bad.Count -gt 0) { Bad ('非 UTF-8 文本文件: ' + (($bad | Select-Object -First 6) -join ', ')) }
    else { Ok '全部文本文件均为合法 UTF-8' }
}


# ---------- 6) 悬浮条目统一格式：不得再有 是什么/干什么/注意事项 行首标签 ----------
function Check-DescFormat {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Root 'Features') -Filter '*.cs' -File)
    $core = Join-Path $Root 'SR_UCH\SR.Core.cs'
    if (Test-Path -LiteralPath $core) { $files += Get-Item -LiteralPath $core }
    $bad = 0
    $total = 0
    foreach ($f in $files) {
        $txt = Read-Text $f.FullName
        foreach ($m in [regex]::Matches($txt, 'LocDesc\([^\r\n]*')) {
            $total++
            if ($m.Value -match '是什么：|干什么：|注意事项：|What: |Does: |Notes: ') { $bad++ }
        }
    }
    if ($bad -gt 0) { Bad ('仍有 ' + $bad + ' 条悬浮条目带行首标签（应已统一移除）') }
    else { Ok ('悬浮条目格式统一（' + $total + ' 条，无行首标签）') }
}


Write-Host '== SR_UCH 文本类守卫（不需要游戏 / 不需要 csc）=='
Check-ApiSurface
Check-ContractBoundary
Check-FeatureLeak
Check-RspCoverage
Check-Encoding
Check-DescFormat
if ($fail -gt 0) { Write-Host ('== 有 ' + $fail + ' 项未通过 ❌ =='); exit 1 }
Write-Host '== 文本类守卫全部通过 ✅ =='
exit 0
