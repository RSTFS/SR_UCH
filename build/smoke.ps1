# 运行时冒烟测试：启动游戏 -> 等日志里的 [SR.SelfTest] 标记 -> 判定 -> 关游戏
# 依赖：游戏本体 + 已部署 SR_UCH.dll（-Deploy）
param(
    [string]$GameDir = '',
    [int]$TimeoutSec = 120,
    [switch]$KeepRunning,
    [switch]$DryRun          # 只检查日志/环境，不启动游戏
)
$ErrorActionPreference = 'Stop'

if (-not $GameDir) { $GameDir = $env:UCH_DIR }
if (-not $GameDir) {
    foreach ($c in @(
        'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Chicken Horse',
        'D:\Steam\steamapps\common\Ultimate Chicken Horse',
        'D:\EF\Steam\steamapps\common\Ultimate Chicken Horse',
        'E:\Steam\steamapps\common\Ultimate Chicken Horse')) {
        if (Test-Path -LiteralPath (Join-Path $c 'UltimateChickenHorse_Data\Managed')) { $GameDir = $c; break }
    }
}
if (-not $GameDir -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'UltimateChickenHorse_Data\Managed'))) {
    Write-Host '找不到游戏目录：用 -GameDir 或设置 UCH_DIR'
    exit 2
}
$exe = Join-Path $GameDir 'UltimateChickenHorse.exe'
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
Write-Host ('游戏目录: ' + $GameDir)
Write-Host ('日志: ' + $log)

function Find-Marker {
    if (-not (Test-Path -LiteralPath $log)) { return $null }
    try {
        return (Get-Content -LiteralPath $log -Tail 300 -ErrorAction SilentlyContinue |
                Where-Object { $_ -match '\[SR\.SelfTest\]' } | Select-Object -Last 1)
    } catch { return $null }
}

if ($DryRun) {
    $m = Find-Marker
    if ($m) { Write-Host ('当前日志中的标记: ' + $m); exit 0 }
    Write-Host '当前日志里没有 [SR.SelfTest] 标记（还没跑过带新 DLL 的游戏）'
    exit 1
}

if (-not (Test-Path -LiteralPath $exe)) { Write-Host ('找不到游戏 EXE: ' + $exe); exit 2 }

Write-Host '== 启动游戏 ...'
$proc = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru
$marker = $null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds 700
    $marker = Find-Marker
    if ($marker) { break }
    if ($proc.HasExited -and -not (Test-Path -LiteralPath $log)) { break }
}
$sw.Stop()

if (-not $KeepRunning) {
    try { if (-not $proc.HasExited) { $proc.Kill(); Write-Host '（已结束游戏进程）' } } catch { }
}

if (-not $marker) {
    Write-Host ('== 冒烟测试失败：' + [int]$TimeoutSec + ' 秒内未在日志找到 [SR.SelfTest] 标记')
    Write-Host '   排查：游戏是否真启动 / BepInEx 是否加载了 SR_UCH / 日志路径对不对'
    exit 1
}
Write-Host ('== 冒烟标记: ' + $marker)
if ($marker -match '\[SR\.SelfTest\] OK') {
    Write-Host '== 冒烟测试通过 ✅'
    exit 0
}
Write-Host '== 冒烟测试失败：标记为 FAIL'
exit 1
