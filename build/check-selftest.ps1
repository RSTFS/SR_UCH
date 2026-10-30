# SR_UCH 启动自检（[SR.SelfTest]）回归基线检查
# 用法：
#   .\build\check-selftest.ps1              用 build\selftest-baseline.txt 对比最新一次游戏日志
#   .\build\check-selftest.ps1 -Update      用当前日志覆盖基线（确认是"期望值"时才用）
#   .\build\check-selftest.ps1 -Log <路径>  指定 LogOutput.log
# 退出码：0 = 一致；1 = 有回归；2 = 找不到日志或没有自检行
param(
    [switch]$Update,
    [string]$Log = ''
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$baselinePath = Join-Path $here 'selftest-baseline.txt'

if (-not $Log) {
    $game = $env:UCH_DIR
    if (-not $game) {
        try {
            $csproj = Join-Path (Split-Path $here -Parent) 'SR_UCH.csproj'
            $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw), '<UCH_DIR[^>]*>([^<]+)</UCH_DIR>')
            if ($m.Success) { $game = $m.Groups[1].Value.Trim() }
        } catch { $game = $null }
    }
    if (-not $game) {
        Write-Host '找不到游戏目录：请设置 UCH_DIR 或用 -Log 指定日志路径' -ForegroundColor Red
        exit 2
    }
    $Log = Join-Path $game 'BepInEx\LogOutput.log'
}

if (-not (Test-Path -LiteralPath $Log)) {
    Write-Host ('找不到日志: ' + $Log) -ForegroundColor Red
    exit 2
}

$line = Get-Content -LiteralPath $Log -Encoding UTF8 | Where-Object { $_ -match '\[SR\.SelfTest\]' } | Select-Object -Last 1
if (-not $line) {
    Write-Host '日志里没有 [SR.SelfTest] 行（游戏还没跑到自检）' -ForegroundColor Red
    exit 2
}

function Get-Num([string]$pattern) {
    $m = [regex]::Match($line, $pattern)
    $v = ''
    if ($m.Success) { $v = $m.Groups[1].Value }
    return $v
}

$okText = 'FAIL'
if ($line -match '\[SR\.SelfTest\] OK') { $okText = 'OK' }

$cur = @{
    'ok'         = $okText
    'game'       = (Get-Num '游戏版本=([^\s]+)')
    'contract'   = (Get-Num '契约=v(\d+)')
    'patches'    = (Get-Num '补丁=(\d+/\d+)')
    'reflection' = (Get-Num '反射=(\d+/\d+)')
    'features'   = (Get-Num '功能=(\d+)')
    'api'        = (Get-Num 'API=(\d+/\d+)')
}
$order = @('ok', 'game', 'contract', 'patches', 'reflection', 'features', 'api')

if ($Update) {
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('# SR_UCH [SR.SelfTest] 回归基线（check-selftest.ps1 读这一份；只在"确认这是期望值"时用 -Update 覆盖）')
    foreach ($k in $order) { $out.Add(($k + '=' + $cur[$k])) }
    $out.Add('# 说明：patches/reflection/api 的 a/b 必须是"全部命中"（a = b），否则说明有补丁或反射成员失效')
    [System.IO.File]::WriteAllLines($baselinePath, $out)
    Write-Host ('基线已更新: ' + $baselinePath) -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $baselinePath)) {
    Write-Host '还没有基线文件：先跑一次 .\build\check-selftest.ps1 -Update' -ForegroundColor Yellow
    exit 2
}

$base = @{}
foreach ($l in (Get-Content -LiteralPath $baselinePath -Encoding UTF8)) {
    $s = $l.Trim()
    if ($s.Length -gt 0 -and -not $s.StartsWith('#')) {
        $i = $s.IndexOf('=')
        if ($i -gt 0) { $base[$s.Substring(0, $i)] = $s.Substring($i + 1) }
    }
}

$bad = 0
Write-Host '== SR_UCH 启动自检回归基线 =='
foreach ($k in $order) {
    $want = $base[$k]
    $got = $cur[$k]
    if (-not $want) {
        Write-Host ("  [ ? ] {0,-10} 基线缺字段，当前 {1}" -f $k, $got) -ForegroundColor Yellow
    } elseif ($want -eq $got) {
        Write-Host ("  [ OK ] {0,-10} {1}" -f $k, $got) -ForegroundColor Green
    } else {
        Write-Host ("  [FAIL] {0,-10} 基线 {1} -> 当前 {2}" -f $k, $want, $got) -ForegroundColor Red
        $bad = $bad + 1
    }
}

if ($cur['ok'] -ne 'OK') {
    Write-Host '  自检结论是 FAIL：看日志里的 [SR.SelfTest] FAIL 明细' -ForegroundColor Red
    $bad = $bad + 1
}

if ($bad -gt 0) {
    Write-Host ('== 有 ' + $bad + ' 项与基线不一致 ==') -ForegroundColor Red
    exit 1
}
Write-Host '== 与基线一致 ==' -ForegroundColor Green
exit 0
