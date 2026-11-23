#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从零恢复全部派生物：fresh clone / CI 第一条命令。

只做三件事：播种单条 rsp 的 / 头行、写最小 JSON 占位、依次调用现有生成器填充。
判定逻辑仍只在 spec_engine.py / uch_rules.py，本文件不复制任何规则。
"""
import os, sys, json, subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)
GEN = 'build/specs/generated'
CONTRACTS_GEN = 'SR_UCH/Contracts/Generated'

HEAD_MAIN = ['/nologo', '/target:library', '/out:bin\\Release\\SR_UCH.dll', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\mscorlib.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\netstandard.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\System.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\System.Core.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\System.Xml.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\Assembly-CSharp.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.CoreModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.UNETModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.IMGUIModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.InputLegacyModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.Physics2DModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.TextRenderingModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.UI.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.UIModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.AudioModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\UnityEngine.AnimationModule.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\InControl.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\com.unity.multiplayer-hlapi.Runtime.dll"', '/r:"%GAME%\\UltimateChickenHorse_Data\\Managed\\AK.Wwise.Unity.API.dll"', '/r:"%GAME%\\BepInEx\\core\\BepInEx.dll"', '/r:"%GAME%\\BepInEx\\core\\0Harmony.dll"']

# 历史基线与手写编译输入随仓库入库，materialize 不创建、不覆盖它们。
BASELINES = [
    'build/specs/generated/contract-fingerprint.json',
    'build/specs/generated/ratchets.json',
    'build/specs/generated/health.schema.json',
    'build/specs/generated/selftest.expected.json',
    'build/specs/generated/removal-plan.json',
    'SR_UCH/Contracts/Generated/NetMessages.g.cs',
    'SR_UCH/Contracts/Generated/SrBuildGraph.g.cs',
    'SR_UCH/Contracts/Generated/SrStateMap.g.cs',
]

def wr(p, text, bom=True):
    os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + text.encode('utf-8'))

def seed_rsp():
    spec = json.load(open('build/specs/features.spec.json', encoding='utf-8'))
    n = 0
    for name, head in [('sr_uch.rsp', HEAD_MAIN)]:
        p = 'build/rsp/' + name
        if not os.path.exists(p):
            wr(p, '\n'.join(head) + '\n'); n += 1
    return n

def seed_gen():
    os.makedirs(GEN, exist_ok=True)
    os.makedirs(CONTRACTS_GEN, exist_ok=True)
    miss = [x for x in BASELINES if not os.path.exists(x)]
    if miss:
        raise SystemExit('[materialize] 基线缺失（应从仓库恢复）: ' + ', '.join(miss))
    return 0

def run(args):
    r = subprocess.run([sys.executable] + args, cwd=ROOT,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
    out = r.stdout.decode('utf-8', 'replace')
    print('  [materialize] %-52s exit=%d' % (' '.join(args[1:]), r.returncode))
    if r.returncode:
        print(out[-1800:])
    return r.returncode

def main():
    print('== materialize: 恢复派生物 ==')
    print('  seed rsp=%d  gen=%d' % (seed_rsp(), seed_gen()))
    rc = 0
    # 顺序有依赖：先出 sources.manifest，rsp 守卫才能对账；再补齐其余派生物。
    rc |= run(['tools/spec_engine.py', 'generate'])
    rc |= run(['tools/uch.py', 'rules', '--suite', 'rsp', '--generate'])
    rc |= run(['tools/uch.py', 'rules', '--suite', 'all', '--generate'])
    rc |= run(['tools/uch.py', 'docs'])
    rc |= run(['tools/uch.py', 'codemod', '--apply'])
    # 收口：上述步骤会增删磁盘文件，派生的「磁盘快照」必须在最终状态下重算一次。
    rc |= run(['tools/spec_engine.py', 'generate'])
    rc |= run(['tools/uch.py', 'seal'])
    missing = [x for x in ('facts.generated.json', 'sources.manifest.json',
                           'compile.targets', 'references.props', 'verify.manifest.json')
               if not os.path.exists(GEN + '/' + x)]
    gcs = len([x for x in os.listdir(CONTRACTS_GEN) if x.endswith('.g.cs')]) if os.path.isdir(CONTRACTS_GEN) else 0
    print('  rsp=%d  generated=%d  .g.cs=%d  缺=%s' % (
        len([x for x in os.listdir('build/rsp') if x.endswith('.rsp')]),
        len(os.listdir(GEN)), gcs, missing or 0))
    if rc or missing or gcs < 6:
        print('== materialize FAILED =='); return 1
    print('== materialize OK ==')
    return 0

if __name__ == '__main__':
    sys.exit(main())
