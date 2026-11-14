# 从契约源码生成 docs/CONTRACT.md（只读扫描，不改任何代码）。
# 用途：给功能/第三方模块一份"当前契约面"清单，避免直接翻 1700 行 partial 文件。
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $Root 'SR_UCH\Contracts'
if (-not (Test-Path -LiteralPath $dir)) { throw '缺少 SR_UCH\Contracts' }
$doc = Join-Path $Root 'docs\CONTRACT.md'
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $doc)

$version = 'unknown'; $api = 'unknown'
$verSrc = [IO.File]::ReadAllText((Join-Path $dir 'Contracts.cs'))
$m = [regex]::Match($verSrc, 'public\s+const\s+int\s+Version\s*=\s*(\d+)'); if ($m.Success) { $version = $m.Groups[1].Value }
$m = [regex]::Match($verSrc, 'public\s+const\s+int\s+ApiLevel\s*=\s*(\d+)'); if ($m.Success) { $api = $m.Groups[1].Value }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('# SR_UCH 契约面（自动生成）')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('> 由 `build/gen-contract-doc.ps1` 从 `SR_UCH/Contracts/*.cs` 生成，**不要手工编辑**。')
[void]$sb.AppendLine('> 契约破坏性版本 `SRContract.Version = ' + $version + '`；门面 API 等级 `ApiLevel = ' + $api + '`。')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 文件与公开类型')
[void]$sb.AppendLine('')
foreach ($f in Get-ChildItem -LiteralPath $dir -Filter '*.cs' -File | Sort-Object Name) {
    $src = [IO.File]::ReadAllText($f.FullName)
    $types = @()
    foreach ($t in [regex]::Matches($src, '(?m)^\s*public\s+(?:static\s+)?(?:partial\s+)?(?:class|struct|enum|delegate\s+\w+\s+)\s*(\w+)')) {
        if (-not $types.Contains($t.Groups[1].Value)) { $types += $t.Groups[1].Value }
    }
    [void]$sb.AppendLine('### `' + $f.Name + '`')
    [void]$sb.AppendLine('')
    if ($types.Count -gt 0) { [void]$sb.AppendLine('- 类型：' + (($types | ForEach-Object { '`' + $_ + '`' }) -join '、')) } else { [void]$sb.AppendLine('- 类型：（无 public 类型）') }
    $members = @()
    foreach ($mm in [regex]::Matches($src, '(?m)^\s*(?:public|internal)\s+static\s+[^();\r\n]*?(\w+)\s*\(')) {
        if (-not $members.Contains($mm.Groups[1].Value)) { $members += $mm.Groups[1].Value }
    }
    if ($members.Count -gt 0) { [void]$sb.AppendLine('- 静态成员：' + (($members | Sort-Object | ForEach-Object { '`' + $_ + '`' }) -join '、')) }
    [void]$sb.AppendLine('')
}
[void]$sb.AppendLine('## 使用约定')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('- 功能层只允许使用契约层 `public` 成员；白名单由 `guards-text.ps1` 从契约源码自动生成（`build/api_surface.txt` 仅作显式登记/禁用）。')
[void]$sb.AppendLine('- 新增 public API 时 `SRContract.ApiLevel` +1；删除或改签名属于破坏性变更，必须抬 `SRContract.Version`。')
[void]$sb.AppendLine('- 槽位名从 `SRApi.SlotKeys` 取；网络魔数用 `SRApi.NetMagic.Register` 登记，自检会检查唯一性。')
[IO.File]::WriteAllText($doc, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ('  [ OK ] 已生成 ' + $doc)
