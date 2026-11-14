# 从 git 历史生成 docs/CHANGELOG.md（只读 git + 契约版本，不改代码）。
# 按提交前缀分组：feat / fix / perf / refactor / chore / docs / test。
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $Root 'docs\CHANGELOG.md'
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out)

$apiLv = '?'
$contracts = Join-Path $Root 'SR_UCH\Contracts\Contracts.cs'
if (Test-Path -LiteralPath $contracts) {
    $m = [regex]::Match([IO.File]::ReadAllText($contracts), 'public\s+const\s+int\s+ApiLevel\s*=\s*(\d+)')
    if ($m.Success) { $apiLv = $m.Groups[1].Value }
}
$head = (& git -C $Root rev-parse --short HEAD 2>$null); if (-not $head) { $head = 'unknown' }

$groups = [ordered]@{ feat = @(); fix = @(); perf = @(); refactor = @(); chore = @(); docs = @(); test = @(); other = @() }
$lines = @(& git -C $Root log --oneline -n 60 2>$null)
foreach ($l in $lines) {
    if (-not $l) { continue }
    $msg = $l -replace '^[0-9a-f]+\s+', ''
    $type = 'other'
    $mm = [regex]::Match($msg, '^([a-zA-Z]+)(\([^)]*\))?:')
    if ($mm.Success) { $type = $mm.Groups[1].Value.ToLowerInvariant() }
    if (-not $groups.Contains($type)) { $type = 'other' }
    $groups[$type] += $msg
}
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# 变更记录（自动生成）')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('> 由 `build/gen-release-notes.ps1` 从 git 历史生成，**不要手工编辑**。')
[void]$sb.AppendLine('> 当前 HEAD `' + $head + '`；契约 ApiLevel `' + $apiLv + '`。')
[void]$sb.AppendLine('')
$titles = @{ feat = '新功能 / 优化'; fix = '修复'; perf = '性能'; refactor = '重构'; chore = '杂项'; docs = '文档'; test = '测试'; other = '其它' }
foreach ($k in $groups.Keys) {
    if ($groups[$k].Count -eq 0) { continue }
    [void]$sb.AppendLine('## ' + $titles[$k])
    [void]$sb.AppendLine('')
    foreach ($msg in $groups[$k]) { [void]$sb.AppendLine('- ' + $msg) }
    [void]$sb.AppendLine('')
}
[IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ('  [ OK ] 已生成 ' + $out)
