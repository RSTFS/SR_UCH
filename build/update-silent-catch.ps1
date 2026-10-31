# 用与 Check-SilentCatch 完全相同的计数方式重建基线（避免两套正则不一致导致棘轮失效）
param([string]$Out = '')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
if (-not $Out) { $Out = Join-Path $here 'silent-catch.txt' }

$dirs = @((Join-Path $root 'SR_UCH'),
          (Join-Path $root 'Features'),
          (Join-Path (Split-Path $root -Parent) 'SR_UCH_EX'))
$rootFiles = @(Get-ChildItem -LiteralPath $root -Filter '*.cs' -File)
$rows = @()
$total = 0
foreach ($d in $dirs) {
    if (-not (Test-Path -LiteralPath $d)) { continue }
    $files = @(Get-ChildItem -LiteralPath $d -Recurse -Filter '*.cs' -File |
        Where-Object { ($_.FullName -replace '\\', '/') -notmatch '/bin/|/obj/|/tests/' })
    if ($d -eq $root) { $files = @() }
    foreach ($f in ($files + $(if ($d -eq $root) { $rootFiles } else { @() }))) {
        $txt = [System.IO.File]::ReadAllText($f.FullName)
        # 与守卫同一口径：注释形态 + 空 catch 都算（review 2026-10-31 §3.4）。
$n = ([regex]::Matches($txt, 'catch\s*(\([^)]*\))?\s*\{\s*(\}|/\*\s*静默)')).Count
        if ($n -gt 0) {
            # PS5.1 没有 [IO.Path]::GetRelativePath：手算相对仓库根的路径（与守卫同口径）。
            $rel = if ($f.FullName.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                $f.FullName.Substring($root.Length + 1)
            } else {
                '../' + $f.FullName.Substring((Split-Path -Parent $root).Length + 1)
            }
            $rel = $rel -replace '\\','/'
            $total += $n
            $rows += [pscustomobject]@{ Name = $rel; N = $n }
        }
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 静默 catch 棘轮基线（build\guards-text.ps1 的 Check-SilentCatch 读这一份）')
$lines.Add('# 背景：全仓大量 catch { /* 静默：非关键路径… */ }，失败不可见（audit item 8）。')
$lines.Add('# 规则：per-file 计数只允许变小。新增静默 catch 要么改成 SRApi.Guard.Log，')
$lines.Add('#       要么确认确实无害后跑 update-silent-catch.ps1 重建基线。')
$lines.Add('# 格式：文件名=数量（由 update-silent-catch.ps1 用与守卫相同的计数方式生成）')
foreach ($r in ($rows | Sort-Object Name)) { $lines.Add($r.Name + '=' + $r.N) }
[System.IO.File]::WriteAllLines($Out, $lines)

Write-Host ("基线已重建: " + $Out) -ForegroundColor Green
Write-Host ("  文件 " + $rows.Count + " 个 / 合计 " + $total + " 处静默 catch")
