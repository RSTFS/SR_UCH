# 重建 build\file-size.txt（与 Check-FileSize 完全同口径：ReadAllLines 行数 + 相对路径键）
# 用在：拆分文件后、或确认某文件确实需要超过 900 行时。
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$limit = 900

$dirs = @((Join-Path $root 'SR_UCH'),
          (Join-Path $root 'Features'),
          (Join-Path (Split-Path $root -Parent) 'SR_UCH_EX'))
$rows = @()
foreach ($d in $dirs) {
    if (-not (Test-Path -LiteralPath $d)) { continue }
    foreach ($f in (Get-ChildItem -LiteralPath $d -Filter '*.cs' -File)) {
        $n = [System.IO.File]::ReadAllLines($f.FullName).Length
        if ($n -le $limit) { continue }
        $rows += [pscustomobject]@{ Rel = ($f.FullName.Substring($root.Length) -replace '\\', '/').TrimStart('/'); N = $n }
    }
}
foreach ($f in (Get-ChildItem -LiteralPath $root -Filter '*.cs' -File)) {
    $n = [System.IO.File]::ReadAllLines($f.FullName).Length
    if ($n -le $limit) { continue }
    $rows += [pscustomobject]@{ Rel = $f.Name; N = $n }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 单文件行数守卫基线（build\guards-text.ps1 的 Check-FileSize 读这一份）')
$lines.Add('# 规则：任何 .cs 超过 ' + $limit + ' 行都必须在这里登记；登记的数字只允许变小（拆分后同步更新）。')
$lines.Add('# 计数口径：与守卫一致（ReadAllLines 的行数）。触发脚本就是本文件。')
$lines.Add('# 格式：相对仓库根的路径=行数')
foreach ($r in ($rows | Sort-Object -Property N -Descending)) { $lines.Add($r.Rel + '=' + $r.N) }
[System.IO.File]::WriteAllLines((Join-Path $here 'file-size.txt'), $lines)

Write-Host ('基线已重建：' + (Join-Path $here 'file-size.txt')) -ForegroundColor Green
Write-Host ('  超过 ' + $limit + ' 行：' + $rows.Count + ' 个文件')
