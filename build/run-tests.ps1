# SR_UCH 最小单元测试（item 5）
# 只测纯逻辑：编译「契约层 SR.Contracts.cs + tests\Tests.cs + 两个桩文件」成控制台程序，直接运行。
# 依赖：mscorlib / System / System.Core / BepInEx（ConfigEntry 等）；UnityEngine 由 tests\UnityStubs.cs 提供，
#       不引用游戏里的 UnityEngine.*.dll（脱离 Unity 运行时加载会 TypeLoadException）。
# 两条路径：
#   A) 有游戏目录（UCH_DIR / csproj 的 UCH_DIR / -GameDir）：用 csc 直接编，引用真实 BepInEx.dll（最快）
#   B) 没有游戏 / 没有 csc / 加 -NoGame：改用 tests\SR_UCH.Tests.csproj（dotnet run），
#      BepInEx 类型走 tests\BepInExStubs.cs —— CI 与"没装游戏的贡献者"也能跑。
# 用法：.\build\run-tests.ps1 [-NoGame]   退出码 0 = 全通过，1 = 有失败，2 = 编译/环境失败
param(
    [string]$GameDir = '',
    [switch]$NoGame
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
# 无游戏（CI / 新贡献者）：走 tests 工程的 dotnet 路径，BepInEx 用桩。
function Invoke-DotnetTests {
    $proj = Join-Path (Join-Path $root 'tests') 'SR_UCH.Tests.csproj'
    if (-not (Test-Path -LiteralPath $proj)) { Write-Host ('缺少测试工程：' + $proj) -ForegroundColor Red; exit 2 }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { Write-Host '既没有游戏目录，也没有 dotnet SDK：无法运行测试' -ForegroundColor Red; exit 2 }
    Write-Host '== 单元测试（无游戏模式：dotnet + BepInEx 桩）==' -ForegroundColor DarkGray
    & $dotnet.Source run --project $proj -v:q --nologo
    exit $LASTEXITCODE
}

if ($NoGame) { Invoke-DotnetTests }
if (-not $GameDir -or -not (Test-Path -LiteralPath $GameDir)) {
    Write-Host '找不到游戏目录：改用 tests\SR_UCH.Tests.csproj（dotnet + BepInEx 桩）' -ForegroundColor Yellow
    Invoke-DotnetTests
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
