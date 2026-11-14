param([switch]$NoExit)   # -NoExit：被 dot-source 时只跑检查、不 exit（供 _tools/build_sr_uch.ps1 -Check 复用）
# 文本类机械守卫（不需要游戏 DLL / 不需要 csc）：可在本地与 GitHub Actions 直接运行
# 用法：pwsh build\guards-text.ps1      退出码 0 = 全绿，1 = 有失败
# 被 _tools\build_sr_uch.ps1 -Check dot-source 时用 -NoExit：只跑检查，不 exit。
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
$script:fail = 0
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
    # 白名单条目必须真实存在于契约层：防止 API 删除后留下假条目，或手滑写错名字。
    $contractDir = Join-Path $Root 'SR_UCH\Contracts'
    if (Test-Path -LiteralPath $contractDir) {
        $csrc = (@(Get-ChildItem -LiteralPath $contractDir -Filter '*.cs' -File | ForEach-Object { Read-Text $_.FullName }) -join "`n")
        $fake = @{}
        foreach ($n in $allow.Keys) {
            if (-not [regex]::IsMatch($csrc, '(?m)^\s*(?:\[[^\]]*\]\s*)*(?:public|internal)\s+[^\r\n]*\b' + [regex]::Escape($n) + '\b')) { $fake[$n] = $true }
        }
        if ($fake.Count -gt 0) { Bad ('白名单条目在契约层不存在: ' + (($fake.Keys | Sort-Object) -join ', ')) }
        else { Ok ('白名单条目均真实存在于契约层（' + $allow.Count + ' 个）') }
    }
}

# ---------- 2) 契约层不得出现实现层标识 ----------
function Check-ContractBoundary {
    $dir = Join-Path $Root 'SR_UCH\Contracts'
    if (-not (Test-Path -LiteralPath $dir)) { Bad '缺少 SR_UCH\Contracts 目录'; return }
    $bad = @()
    foreach ($f in Get-ChildItem -LiteralPath $dir -Filter '*.cs' -File) {
        $ln = 0
        foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
            $ln++
            $s = $line.Trim()
            if ($s.StartsWith('//') -or $s.StartsWith('*') -or $s.StartsWith('/*')) { continue }
            $code = [regex]::Replace($s, '"(?:[^"\\]|\\.)*"', '""')
            $i = $code.IndexOf('//'); if ($i -ge 0) { $code = $code.Substring(0, $i) }
            if ([regex]::IsMatch($code, '\b(MainPlugin|ConfigMigration|SRHost)\b') -or
                [regex]::IsMatch($code, '(?<![A-Za-z_.])SR\.')) { $bad += ($f.Name + ' 第 ' + $ln + ' 行: ' + $s) }
        }
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

    # 拆分构建：清单由 build/gen-rsp.ps1 从磁盘生成（build.ps1 的 $rspFeatures 也由 feat_*.rsp 推导），
    # 这里直接跑生成器的 -Verify（-NoExit 不退出本脚本），保证 CI 也能发现"手改清单/漏生成"。
    $gen = Join-Path $Root 'build\gen-rsp.ps1'
    if (Test-Path -LiteralPath $gen) {
        $before = $fail
        . $gen -Verify -NoExit
        if ($script:RspVerifyBad -gt 0) { Bad ('构建清单与磁盘源码不一致：' + $script:RspVerifyBad + ' 处（运行 build\gen-rsp.ps1 重新生成）') }
        elseif ($fail -eq $before) { Ok '拆分构建清单由磁盘源码生成且一致' }
    }
}

# ---------- 5) 编码：全部文本文件必须是合法 UTF-8 ----------
function Check-Encoding {
    $bad = @()
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    # 注意：-LiteralPath 与 -Include 同用时不生效（PowerShell 5.1 的坑），会把 .dll/.png 等二进制
    # 也当文本扫进来误报“非 UTF-8”。改成按扩展名显式过滤。
    $textExt = @('.cs','.ps1','.rsp','.md','.txt','.json')
    foreach ($f in Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object { $textExt -contains $_.Extension.ToLower() }) {
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


# ---------- 6) 悬浮条目统一格式：每条 LocDesc 说明恰好 3 行（无行首标签、不写默认值行）----------
# 说明：前两行固定是「是什么 / 做什么」，第 3 行起是可选的多行「注意」；默认值与按键由核心按参数类型追加。
function Split-Args([string]$body) {
    # body 形如 "(a, b, c)"；按顶层逗号切分，忽略字符串内的逗号与嵌套括号。
    $inner = $body.Substring(1, $body.Length - 2)
    $result = New-Object System.Collections.Generic.List[string]
    $cur = New-Object System.Text.StringBuilder
    $depth = 0
    $inStr = $false
    for ($i = 0; $i -lt $inner.Length; $i++) {
        $ch = $inner[$i]
        if ($inStr) {
            [void]$cur.Append($ch)
            if ($ch -eq '\') {
                if ($i + 1 -lt $inner.Length) { [void]$cur.Append($inner[$i + 1]); $i++ }
            } elseif ($ch -eq '"') { $inStr = $false }
            continue
        }
        if ($ch -eq '"') { $inStr = $true; [void]$cur.Append($ch); continue }
        if ($ch -eq '(' -or $ch -eq '[' -or $ch -eq '{') { $depth++ }
        elseif ($ch -eq ')' -or $ch -eq ']' -or $ch -eq '}') { $depth-- }
        if ($ch -eq ',' -and $depth -eq 0) {
            $result.Add($cur.ToString().Trim())
            [void]$cur.Clear()
            continue
        }
        [void]$cur.Append($ch)
    }
    $result.Add($cur.ToString().Trim())
    return ,$result
}

function Check-DescFormat {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Root 'Features') -Filter '*.cs' -File)
    $core = Join-Path $Root 'SR_UCH\SR.Core.cs'
    if (Test-Path -LiteralPath $core) { $files += Get-Item -LiteralPath $core }
    $bad = 0
    $total = 0
    $skipped = 0
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
            $callArgs = Split-Args $body
            if ($callArgs.Count -lt 4) { $skipped++; $idx = $m + 1; continue }
            # 只检查第 3、4 个实参（中文说明 / 英文说明）；变量或字符串拼接无法静态判定，跳过并计数。
            for ($ai = 2; $ai -le 3; $ai++) {
                $arg = $callArgs[$ai]
                if ($arg -notmatch '^"(?:[^"\\]|\\.)*"$') { $skipped++; continue }
                $s = $arg.Substring(1, $arg.Length - 2)
                $total++
                $parts = $s -split '\\n'
                if ($parts.Count -lt 2 -or $parts.Count -gt 4) { $bad++; continue }
                $empty = $false
                foreach ($p in $parts) { if ([string]::IsNullOrWhiteSpace($p)) { $empty = $true; break } }
                if ($empty) { $bad++; continue }
                if ($s -match '是什么：|干什么：|注意事项：|What: |Does: |Notes: ') { $bad++ }
            }
            $idx = $m + 1
        }
    }
    if ($bad -gt 0) {
        Bad ('悬浮条目格式不合规（LocDesc 需要 2-4 行：是什么/做什么/可选注意，且无行首标签）: ' + $bad + ' 条')
    } else {
        Ok ('悬浮条目格式统一（' + $total + ' 条：2-4 行、无行首标签；跳过动态文案 ' + $skipped + ' 处）')
    }
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


# P1-6 第一步：裸 catch 计数（只报告，不阻塞构建）。后续按文件分批补注释/接 Guard。
function Check-BareCatch {
    $files = Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.cs' -File |
        Where-Object { ($_.FullName -replace '\\', '/') -notmatch '/bin/|/obj/|\.git/|/tests/' }
    $total = 0
    $rows = @()
    foreach ($f in $files) {
        $txt = [System.IO.File]::ReadAllText($f.FullName)
        $n = ([regex]::Matches($txt, 'catch\s*\{\s*\}')).Count
        if ($n -gt 0) { $total += $n; $rows += [pscustomobject]@{ Name = $f.Name; N = $n } }
    }
    if ($total -eq 0) { Ok '裸 catch 全部带注释（0 处）' ; return }
    $top = ($rows | Sort-Object N -Descending | Select-Object -First 6 | ForEach-Object { $_.Name + '=' + $_.N }) -join ', '
    Bad ('裸 catch { } 未注释：' + $total + ' 处（' + $rows.Count + ' 个文件；最多：' + $top + '）')
}



# item 10：SR 分区分界守卫 —— private static 字段跨分部文件引用必须登记（棘轮，只减不增）。
# 背景：SR 由 17 个 partial 拼成一个大静态类，"分区"以前只是注释约定；这里把它变成机器检查。
function Check-RegionFields {
    $srcDir = Join-Path $Root 'SR_UCH'
    if (-not (Test-Path -LiteralPath $srcDir)) { Ok 'SR 分区分界（找不到目录，跳过）'; return }
    $files = Get-ChildItem -LiteralPath $srcDir -Filter 'SR.*.cs' -File
    $texts = @{}
    foreach ($f in $files) { $texts[$f.Name] = [System.IO.File]::ReadAllText($f.FullName) }
    $decl = @{}
    foreach ($k in $texts.Keys) {
        foreach ($m in [regex]::Matches($texts[$k], '(?m)^\s*(?:private|internal|public)\s+static\s+(?:readonly\s+)?[\w<>\[\],\.\?]+\s+(_\w+)\s*(?:=|;)')) {
            $name = $m.Groups[1].Value
            if (-not $decl.ContainsKey($name)) { $decl[$name] = $k }
        }
    }
    $allowPath = Join-Path $PSScriptRoot 'region-access.txt'
    $allow = @{}
    if (Test-Path -LiteralPath $allowPath) {
        foreach ($l in [System.IO.File]::ReadAllLines($allowPath)) {
            $s = $l.Trim()
            if ($s -and -not $s.StartsWith('#')) { $allow[$s] = $true }
        }
    }
    $bad = @()
    foreach ($name in $decl.Keys) {
        $owner = $decl[$name]
        if ($allow.ContainsKey($name)) { continue }
        foreach ($k in $texts.Keys) {
            if ($k -eq $owner) { continue }
            if ([regex]::IsMatch($texts[$k], '\b' + [regex]::Escape($name) + '\b')) {
                $bad += ($name + '（' + $owner + ' -> ' + $k + '）')
                break
            }
        }
    }
    if ($bad.Count -gt 0) {
        Bad ('SR 分区越界：' + $bad.Count + ' 个 private 字段被其它分部文件使用，且未登记：' + (($bad | Select-Object -First 5) -join '; '))
    } else {
        Ok ('SR 分区未新增越界字段（已登记 ' + $allow.Count + ' 个：见 build/region-access.txt）')
    }
}


# audit item 8：静默 catch 棘轮 —— 带注释的 catch { /* 静默 … */ } 每文件计数只允许变小。
# 新增静默 catch 会被拦下：要么改成 SRApi.Guard.Log / SR.Guard.Log，要么同步更新基线数字。
function Check-SilentCatch {
    $basePath = Join-Path $PSScriptRoot 'silent-catch.txt'
    if (-not (Test-Path -LiteralPath $basePath)) { Ok '静默 catch 棘轮（缺基线文件，跳过）'; return }
    $base = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($basePath)) {
        $s = $l.Trim()
        if (-not $s -or $s.StartsWith('#')) { continue }
        $i = $s.IndexOf('=')
        if ($i -gt 0) { $base[$s.Substring(0, $i)] = [int]$s.Substring($i + 1) }
    }
    # 覆盖范围：核心分片 + 功能层 + 仓库根（MainPlugin.cs 等）+ EX。以前漏了 Features 与根目录，
    # 导致 99/153 处静默 catch 脱离棘轮（评审 B-2）。
    $dirs = @((Join-Path $Root 'SR_UCH'),
              (Join-Path $Root 'Features'),
              $Root,   # 仓库根：MainPlugin.cs 等也要进棘轮（以前漏掉，注释与实现不符）
              (Join-Path (Split-Path $Root -Parent) 'SR_UCH_EX'))
    $bad = @()
    $total = 0
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        $files = Get-ChildItem -LiteralPath $d -Recurse -Filter '*.cs' -File |
            Where-Object { ($_.FullName -replace '\\', '/') -notmatch '/bin/|/obj/|/tests/' }
        foreach ($f in $files) {
            $txt = [System.IO.File]::ReadAllText($f.FullName)
            # 同时计空 catch（catch (...) { } / catch { }）：以前只认带「/* 静默」注释的形态，
            # 空 catch 完全逃脱棘轮（review 2026-10-31 §3.4）。注释形态与空形态都算一处。
            $n = ([regex]::Matches($txt, 'catch\s*(\([^)]*\))?\s*\{\s*(\}|/\*\s*静默)')).Count
            if ($n -eq 0) { continue }
            $total += $n
            $limit = 0
            # 基线键用"相对仓库根的路径"：同名文件（如 Level.cs 在核心与功能层各有一份）不会互相覆盖。
            $rel = if ($f.FullName.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    ($f.FullName.Substring($Root.Length) -replace '\\', '/').TrimStart('/')
                } else {
                    # 仓库外的文件（如 ../SR_UCH_EX）：用与仓库根的相对路径，避免切出半截目录名
                    ('../' + ((Split-Path $Root -Parent) | ForEach-Object {
                        $f.FullName.Substring($_.Length) -replace '\\', '/'
                    }).TrimStart('/'))
                }
            if ($base.ContainsKey($rel)) { $limit = $base[$rel] }
            if ($n -gt $limit) { $bad += ($rel + ' ' + $limit + '->' + $n) }
        }
    }
    if ($bad.Count -gt 0) {
        Bad ('静默 catch 变多：' + (($bad | Select-Object -First 5) -join '; ') + '（基线见 build/silent-catch.txt）')
    } else {
        Ok ('静默 catch 未新增（基线 ' + $base.Count + ' 个文件 / 当前 ' + $total + ' 处）')
    }
}

# P1-4：缩进统一（.cs 只允许空格缩进，禁止行首 Tab）。
function Check-Indent {
    $bad = @()
    $files = Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.cs' -File |
        Where-Object { ($_.FullName -replace '\\', '/') -notmatch '/bin/|/obj/|\.git/|/tests/' }
    foreach ($f in $files) {
        $txt = [System.IO.File]::ReadAllText($f.FullName)
        if ([regex]::IsMatch($txt, '(?m)^\t')) { $bad += $f.Name }
    }
    if ($bad.Count -gt 0) { Bad ('行首使用 Tab 缩进: ' + (($bad | Select-Object -First 6) -join ', ')) }
    else { Ok '缩进统一（仅空格）' }
}


# ---------- 13) 悬浮条目组装不得绕过统一入口 ----------
# 用户要求的顺序：是什么 -> 做什么 -> 注意事项 -> 默认值 -> 按键。
# 默认值与按键由 SR.Localization.ComposeTip 统一追加；任何地方自己拼默认值都会破坏顺序。
function Check-TipComposer {
    $loc = Join-Path $Root 'SR_UCH\SR.Localization.cs'
    $set = Join-Path $Root 'SR_UCH\SR.Settings.cs'
    foreach ($f in @($loc, $set)) {
        if (-not (Test-Path -LiteralPath $f)) { Bad ('找不到 ' + (Split-Path $f -Leaf)); return }
    }
    $bad = @()
    $locTxt = Read-Text $loc
    $setTxt = Read-Text $set
    if ($locTxt -notmatch 'internal static string ComposeTip') { $bad += 'Localization 缺少 ComposeTip' }
    if ($locTxt -notmatch 'ComposeTip\(entry, desc, null\)') { $bad += 'RowTip 未走 ComposeTip' }
    if ($setTxt -notmatch 'ComposeTip\(entry, tip, hk\)') { $bad += 'RestoreLabel 未走 ComposeTip' }
    if ($setTxt -match 'DefaultSuffix\(') { $bad += 'RestoreLabel 仍自行追加默认值' }
    if ($bad.Count -gt 0) { Bad ('悬浮条目组装被绕过：' + ($bad -join '；')); return }
    Ok '悬浮条目组装统一（描述 -> 默认值 -> 按键 均由 ComposeTip 追加）'
}


# ---------- 14) 单文件行数守卫（结构优化，防再次长成巨兽） ----------
# 任何 .cs 超过 FileSizeLimit 行都要在 build\file-size.txt 里登记；登记值只允许变小。
function Check-FileSize {
    $limit = 900
    $basePath = Join-Path $PSScriptRoot 'file-size.txt'
    if (-not (Test-Path -LiteralPath $basePath)) { Ok '单文件行数（缺基线文件，跳过）'; return }
    $base = @{}
    foreach ($l in [System.IO.File]::ReadAllLines($basePath)) {
        $s = $l.Trim()
        if (-not $s -or $s.StartsWith('#')) { continue }
        $i = $s.IndexOf('=')
        if ($i -gt 0) { $base[$s.Substring(0, $i)] = [int]$s.Substring($i + 1) }
    }
    $dirs = @((Join-Path $Root 'SR_UCH'), (Join-Path $Root 'Features'), (Join-Path (Split-Path $Root -Parent) 'SR_UCH_EX\ExModule'))
    $bad = @()
    $count = 0
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { continue }
        foreach ($f in (Get-ChildItem -LiteralPath $d -Filter '*.cs' -File)) {
            $n = [System.IO.File]::ReadAllLines($f.FullName).Length
            if ($n -le $limit) { continue }
            $count++
            $allowed = 0
            # 基线键 = 相对仓库根的路径（与 silent-catch 一致；同名文件不会互相覆盖）
            $rel = if ($f.FullName.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                ($f.FullName.Substring($Root.Length) -replace '\\', '/').TrimStart('/')
            } else {
                ('../' + ((Split-Path $Root -Parent) | ForEach-Object { $f.FullName.Substring($_.Length) -replace '\\', '/' }).TrimStart('/'))
            }
            if ($base.ContainsKey($rel)) { $allowed = $base[$rel] }
            if ($n -gt $allowed) { $bad += ($rel + ' ' + $allowed + '->' + $n) }
        }
    }
    if ($bad.Count -gt 0) {
        Bad ('单文件行数超出登记值：' + (($bad | Select-Object -First 4) -join '; ') + '（基线见 build/file-size.txt）')
    } else {
        Ok ('单文件行数受控（超过 ' + $limit + ' 行的 ' + $count + ' 个文件均已登记，且未增长）')
    }
}

# UI 约定棘轮：功能层"手写布局组/手写 GUI.enabled"的次数只允许减少。
# 历史教训：手写 BeginHorizontal 漏 EndHorizontal、漏还原 GUI.enabled，会整页错位/变灰。
# 新代码请用 SRApi.Ctl 的行控件与 using(SRApi.Ctl.Disabled(...))。
function Check-DocScripts {
    # 文档里引用的 .ps1 必须真实存在：脚本改名/搬迁后，README/DESIGN 很容易继续指向旧路径
    # （本仓库就发生过"文档写 _tools 路径、实际入口已迁到 build/"）。
    $docs = @('README.md', 'README.zh-CN.md', 'DESIGN.zh-CN.md')
    $missing = @()
    $checked = 0
    foreach ($d in $docs) {
        $p = Join-Path $Root $d
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $txt = Read-Text $p
        foreach ($m in [regex]::Matches($txt, '([A-Za-z0-9_\-]+\.ps1)')) {
            $name = $m.Groups[1].Value
            $checked++
            if (-not (Test-Path -LiteralPath (Join-Path $Root ('build\' + $name))) -and
                -not (Test-Path -LiteralPath (Join-Path $Root $name))) { $missing += ($d + ' -> ' + $name) }
        }
    }
    $missing = @($missing | Sort-Object -Unique)
    if ($missing.Count -gt 0) { Bad ('文档引用了不存在的脚本: ' + (($missing | Select-Object -First 5) -join '; ')) }
    else { Ok ('文档引用的脚本都存在（检查 ' + $checked + ' 处）') }
}

function Check-UiConventions {
    $checks = @(
        @{ Name = '裸 GUILayout.BeginHorizontal'; File = 'ui-raw-layout.txt'; Pattern = 'GUILayout\.BeginHorizontal\(' },
        @{ Name = '手写 GUI.enabled';            File = 'ui-enabled.txt';     Pattern = 'GUI\.enabled\s*=' }
    )
    foreach ($c in $checks) {
        $basePath = Join-Path $PSScriptRoot $c.File
        if (-not (Test-Path -LiteralPath $basePath)) { continue }
        $base = @{}
        foreach ($l in [System.IO.File]::ReadAllLines($basePath)) {
            $x = $l.Trim()
            if (-not $x -or $x.StartsWith('#')) { continue }
            $i = $x.IndexOf('=')
            if ($i -gt 0) { $base[$x.Substring(0, $i)] = [int]$x.Substring($i + 1) }
        }
        $bad = @()
        foreach ($f in (Get-ChildItem -LiteralPath (Join-Path $Root 'Features') -Filter '*.cs' -File)) {
            $rel = 'Features/' + $f.Name
            $n = ([regex]::Matches([System.IO.File]::ReadAllText($f.FullName), $c.Pattern)).Count
            if ($n -eq 0) { continue }
            $allowed = 0
            if ($base.ContainsKey($rel)) { $allowed = $base[$rel] }
            if ($n -gt $allowed) { $bad += ($rel + ' ' + $allowed + '->' + $n) }
        }
        if ($bad.Count -gt 0) { Bad ($c.Name + ' 变多：' + (($bad | Select-Object -First 3) -join '; ') + '（改用 SRApi.Ctl 的行控件 / Disabled 作用域）') }
        else { Ok ($c.Name + ' 未增长') }
    }
}

Write-Host '== SR_UCH 文本类守卫（不需要游戏 / 不需要 csc）=='
Check-ApiSurface
Check-ContractBoundary
Check-FeatureLeak
Check-RspCoverage
Check-Encoding
Check-DescFormat
Check-CsEncoding
Check-BareCatch
Check-Indent
Check-RegionFields
Check-SilentCatch
Check-TipComposer
Check-FileSize
Check-UiConventions
Check-DocScripts
if ($script:fail -gt 0) { Write-Host ('== 有 ' + $script:fail + ' 项未通过 ❌ =='); if (-not $NoExit) { exit 1 }; return }
Write-Host '== 文本类守卫全部通过 ✅ =='
if (-not $NoExit) { exit 0 }
