import os

p = 'build/build.ps1'
raw = open(p, 'rb').read()
bom = raw[:3] == b'\xef\xbb\xbf'
t = raw.decode('utf-8-sig')

# 1) 记录每步结果，供末尾总结使用
old_head = (
    "# ---- 游戏目录探测：-GameDir > UCH_DIR > 固定候选 > 注册表 ----\n"
)
new_head = (
    "# 构建结果账本：末尾统一打印总结（每个产物一行）。\n"
    "$summary = New-Object System.Collections.Generic.List[string]\n"
    "\n"
    "# ---- 游戏目录探测：-GameDir > UCH_DIR > 固定候选 > 注册表 ----\n"
)
assert old_head in t, 'head anchor'
t = t.replace(old_head, new_head, 1)

# 2) SR 成功/部署处记账
old_sr_ok = "    Write-Host '  [ OK ] SR_UCH.dll (dotnet)'\n"
new_sr_ok = (
    "    Write-Host '  [ OK ] SR_UCH.dll (dotnet)'\n"
    "    $summary.Add('SR  构建  OK   bin\\MSBuild\\SR_UCH.dll')\n"
)
if old_sr_ok in t:
    t = t.replace(old_sr_ok, new_sr_ok, 1)

old_sr_csc = "    Write-Host \"  [ OK ] $name\"\n"
new_sr_csc = (
    "    Write-Host \"  [ OK ] $name\"\n"
    "    $summary.Add(('SR  ' + $name + '  OK') )\n"
)
if old_sr_csc in t:
    t = t.replace(old_sr_csc, new_sr_csc, 1)

# 3) SR 部署记账
old_sr_dep = "    Write-Host (\"已部署: \" + (Join-Path $plugins $dll))\n"
new_sr_dep = (
    "    Write-Host (\"已部署: \" + (Join-Path $plugins $dll))\n"
    "    $srHash = ''\n"
    "    try { $srHash = (Get-FileHash -LiteralPath (Join-Path $plugins $dll) -Algorithm SHA256).Hash.Substring(0, 12) } catch { }\n"
    "    $summary.Add(\"SR  部署  OK   $dll  $srHash\")\n"
)
if old_sr_dep in t:
    t = t.replace(old_sr_dep, new_sr_dep, 1)

# 4) EX 记账
old_ex_ok = "            Write-Host '  [ OK ] SR_UCH_EX.dll'\n"
new_ex_ok = (
    "            Write-Host '  [ OK ] SR_UCH_EX.dll'\n"
    "            $summary.Add('EX  构建  OK   bin\\MSBuild\\SR_UCH_EX.dll')\n"
)
if old_ex_ok in t:
    t = t.replace(old_ex_ok, new_ex_ok, 1)

old_ex_dep = "                        Write-Host (\"已部署: \" + (Join-Path $plugins 'SR_UCH_EX.dll'))\n"
new_ex_dep = (
    "                        Write-Host (\"已部署: \" + (Join-Path $plugins 'SR_UCH_EX.dll'))\n"
    "                        $exHash = ''\n"
    "                        try { $exHash = (Get-FileHash -LiteralPath (Join-Path $plugins 'SR_UCH_EX.dll') -Algorithm SHA256).Hash.Substring(0, 12) } catch { }\n"
    "                        $summary.Add(\"EX  部署  OK   SR_UCH_EX.dll  $exHash\")\n"
)
if old_ex_dep in t:
    t = t.replace(old_ex_dep, new_ex_dep, 1)

# 5) 末尾总结：插在 -ExportAudit 块之前（保证非 -ExportAudit 时也会执行）
old_tail = (
    "if ($ExportAudit) {\n"
    "    $log = Join-Path $GameDir 'BepInEx\\LogOutput.log'\n"
)
new_tail = (
    "# ---- 构建总结：无论成功与否都打印一次，避免"看起来成功了但没产物" ----\n"
    "Write-Host ''\n"
    "Write-Host '================ 构建总结 ================'\n"
    "Write-Host ('游戏目录 : ' + $GameDir)\n"
    "Write-Host ('编译路线 : ' + $(if ($useDotnetBuild) { 'dotnet build（MSBuild）' } else { 'csc' }))\n"
    "if ($Check) { Write-Host ('目标     : -Check（统一回归）') }\n"
    "elseif ($Ex -and $NoEx) { Write-Host '目标     : （参数冲突：-Ex 与 -NoEx 同时给出）' }\n"
    "elseif ($Ex) { Write-Host '目标     : 只构建 EX' }\n"
    "elseif ($NoEx) { Write-Host '目标     : 只构建 SR' }\n"
    "else { Write-Host '目标     : SR + EX' }\n"
    "Write-Host ('部署     : ' + $(if ($Deploy) { '是' } else { '否（未加 -Deploy）' }))\n"
    "Write-Host ''\n"
    "if ($summary.Count -gt 0) {\n"
    "    Write-Host ' 组件  阶段  结果  详情'\n"
    "    Write-Host ' ----  ----  ----  ------------------------------------------'\n"
    "    foreach ($s in $summary) { Write-Host (' ' + $s) }\n"
    "} else {\n"
    "    Write-Host ' （本次没有产出任何构建结果）'\n"
    "}\n"
    "if ($Deploy) {\n"
    "    Write-Host ''\n"
    "    Write-Host ('  部署目录 : ' + (Join-Path $GameDir 'BepInEx\\plugins'))\n"
    "    Write-Host '  提示     : 游戏正在运行时部署可能失败，请先完全退出游戏'\n"
    "}\n"
    "Write-Host '=========================================='\n"
    "Write-Host ''\n"
    "\n"
    "if ($ExportAudit) {\n"
    "    $log = Join-Path $GameDir 'BepInEx\\LogOutput.log'\n"
)
assert old_tail in t, 'tail anchor'
t = t.replace(old_tail, new_tail, 1)

open(p + '.tmp', 'wb').write((b'\xef\xbb\xbf' if bom else b'') + t.encode('utf-8'))
os.replace(p + '.tmp', p)
print('summary block added')
