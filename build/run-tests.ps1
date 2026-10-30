# SR_UCH 最小单元测试（item 5）
# 只测纯逻辑：编译「契约层 SR.Contracts.cs + tests\Tests.cs」成一个控制台程序，直接运行。
# 依赖只有 mscorlib / System / System.Core / BepInEx + UnityEngine.CoreModule（Color 类型）。
# 用法：.\build\run-tests.ps1          跑全部测试；退出码 0 = 全通过，1 = 有失败，2 = 编译失败
param(
    [string]$GameDir = ''
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here

if (-not $GameDir) { $GameDir = $env:UCH_DIR }
if (-not $GameDir) {
    try {
        $csproj = Join-Path $root 'SR_UCH.csproj'
        $m = [regex]::Match((Get-Content -LiteralPath $csproj -Raw), '<UCH_DIR[^>]*>([^<]+)</UCH_DIR>')
        if ($m.Success) { $GameDir = $m.Groups[1].Value.Trim() }
    } catch { $GameDir = '' }
}
if (-not $GameDir -or -not (Test-Path -LiteralPath $GameDir)) {
    Write-Host '找不到游戏目录：请设置 UCH_DIR 或用 -GameDir 指定' -ForegroundColor Red
    exit 2
}

# 找 csc：优先 dotnet SDK 里的 Roslyn
$csc = ''
$sdkRoot = Join-Path $env:ProgramFiles 'dotnet\sdk'
if (Test-Path -LiteralPath $sdkRoot) {
    $latest = Get-ChildItem -LiteralPath $sdkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if ($latest) {
        $p = Join-Path $latest.FullName 'Roslyn\bincore\csc.dll'
        if (Test-Path -LiteralPath $p) { $csc = $p }
    }
}
if (-not $csc) { Write-Host '找不到 csc（需要 dotnet SDK）' -ForegroundColor Red; exit 2 }

$managed = Join-Path $GameDir 'UltimateChickenHorse_Data\Managed'
$outDir = Join-Path $here '.cache'
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$outExe = Join-Path $outDir 'SR_UCH_Tests.exe'

$refs = @(
    '/r:"' + (Join-Path $managed 'mscorlib.dll') + '"',
    '/r:"' + (Join-Path $managed 'System.dll') + '"',
    '/r:"' + (Join-Path $managed 'System.Core.dll') + '"',
    '/r:"' + (Join-Path $GameDir 'BepInEx\core\BepInEx.dll') + '"'
)

$sources = @(
    '"' + (Join-Path $root 'SR_UCH\SR.Contracts.cs') + '"',
    '"' + (Join-Path (Join-Path $root 'tests') 'Tests.cs') + '"',
    '"' + (Join-Path (Join-Path $root 'tests') 'UnityStubs.cs') + '"'
)

 $cscArgs = @('/nologo', '/target:exe', ('/out:"' + $outExe + '"')) + $refs + $sources
Write-Host '== 编译测试程序 =='
# 用 rsp 传参：路径里有空格（"Ultimate Chicken Horse"），直接 splat 会被拆开。
$rsp = Join-Path $outDir 'sr_uch_tests.rsp'
[System.IO.File]::WriteAllLines($rsp, $cscArgs)
& dotnet $csc ('@' + $rsp)
if ($LASTEXITCODE -ne 0) {
    Write-Host '编译失败 ❌' -ForegroundColor Red
    exit 2
}

Write-Host '== 运行测试 =='
& $outExe
if ($LASTEXITCODE -ne 0) {
    Write-Host '测试失败 ❌' -ForegroundColor Red
    exit 1
}
Write-Host '测试全部通过 ✅' -ForegroundColor Green
exit 0
