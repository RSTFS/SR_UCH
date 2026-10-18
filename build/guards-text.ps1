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


# ---------- 6) 悬浮条目统一格式：每条说明恰好 3 行内容（无行首标签、不写默认值行）----------
function Check-DescFormat {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Root 'Features') -Filter '*.cs' -File)
    $core = Join-Path $Root 'SR_UCH\SR.Core.cs'
    if (Test-Path -LiteralPath $core) { $files += Get-Item -LiteralPath $core }
    $bad = 0
    $total = 0
    foreach ($f in $files) {
        $txt = Read-Text $f.FullName
        $idx = 0
        while ($true) {
            $j = $txt.IndexOf('LocDesc(', $idx)
            if ($j -lt 0) { break }
            $k = $txt.IndexOf('(', $j); $depth = 0; $m = $k
            while ($m -lt $txt.Length) {
                $ch = $txt[$m]
                if ($ch -eq '(') { $depth++ } elseif ($ch -eq ')') { $depth--; if ($depth -eq 0) { break } }
                $m++
            }
            if ($m -ge $txt.Length -or $k -lt 0) { break }
            $body = $txt.Substring($k, $m - $k + 1)
            foreach ($lit in [regex]::Matches($body, '"((?:[^"\\]|\\.)*)"')) {
                $s = $lit.Groups[1].Value
                if (-not $s.Contains('\n')) { continue }   # 单行说明不参与（如占位/简短提示）
                $total++
                $lines = ([regex]::Matches($s, '\\n')).Count + 1
                if ($lines -lt 2 -or $lines -gt 3) { $bad++; continue }
                if ($s -match '是什么：|干什么：|注意事项：|What: |Does: |Notes: ') { $bad++ }
            }
            $idx = $m + 1
        }
    }
    if ($bad -gt 0) { Bad ('悬浮条目格式不合规（应为 2~3 行且无行首标签）: ' + $bad + ' 条') }
    else { Ok ('悬浮条目格式统一（' + $total + ' 条：2~3 行、无行首标签）') }
}



# ---------- 7) 源码编码规范：.cs/.csproj = UTF-8 无 BOM + LF；.ps1 = UTF-8 BOM ----------
function Check-CsEncoding {
    $bad = @()
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object { $_.Extension -in '.cs', '.csproj' }) {
        $full = ($f.FullName -replace '\\', '/')
        if ($full -match '/bin/|/obj/|/\.git/') { continue }
        $b = [System.IO.File]::ReadAllBytes($f.FullName)
        if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { $bad += ($f.Name + ': 不应带 BOM'); continue }
        $txt = [System.Text.Encoding]::UTF8.GetString($b)
        if ($txt.Contains("`r`n")) { $bad += ($f.Name + ': 应为 LF') }
    }
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.ps1' -File) {
        $full = ($f.FullName -replace '\\', '/')
        if ($full -match '/bin/|/obj/|/\.git/') { continue }
        $b = [System.IO.File]::ReadAllBytes($f.FullName)
        if (-not ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) { $bad += ($f.Name + ': 缺 BOM') }
    }
    if ($bad.Count -gt 0) { Bad ('源码编码不合规: ' + (($bad | Select-Object -First 6) -join '; ')) }
    else { Ok '编码规范通过（.cs/.csproj = UTF-8 无 BOM+LF；.ps1 = UTF-8 BOM）' }
}

Write-Host '== SR_UCH 文本类守卫（不需要游戏 / 不需要 csc）=='
Check-ApiSurface
Check-ContractBoundary
Check-FeatureLeak
Check-RspCoverage
Check-Encoding
Check-DescFormat
Check-CsEncoding
if ($fail -gt 0) { Write-Host ('== 有 ' + $fail + ' 项未通过 ❌ =='); exit 1 }
Write-Host '== 文本类守卫全部通过 ✅ =='
exit 0
