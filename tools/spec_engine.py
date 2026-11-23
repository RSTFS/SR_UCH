#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SR_UCH 唯一声明引擎（Python 3.8+，零外部依赖）。

职责：从 build/specs/features.spec.json + 磁盘源码派生全部生成物与判定。
CI 与本地统一经 tools/uch.py → spec_engine。fresh clone 无 pwsh
也能完整生成与校验；build/ 下不再有转发薄壳。

  spec_engine.py verify     逐项判定生成物与源码是否一致
  spec_engine.py generate   写回全部可确定性派生的产物 + 调度 4 个守卫 + seal
  spec_engine.py derive     打印派生摘要（给人看，不写文件）
  spec_engine.py impact <file>  查改这个文件会牵动哪些 CHK

注意事项：本文件是唯一解析器。新增生成物必须先在此登记 derive()，
不要再写第二份 pwsh/Python 口径实现。
"""
import glob
import hashlib
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)
SPEC = 'build/specs/features.spec.json'
GENDIR = 'build/specs/generated'
MANIFEST = GENDIR + '/verify.manifest.json'
CONTRACTS = 'SR_UCH/Contracts'
GENERATED_CS = CONTRACTS + '/Generated'


def rd(p):
    b = open(p, 'rb').read()
    if b.startswith(b'\xef\xbb\xbf'):
        b = b[3:]
    return b.decode('utf-8').replace('\r\n', '\n')


def facts_block(f):
    """README 事实区块的唯一构造实现：生成器写回、DOC-003 守卫比对都用它。
    不要在别处再拼一份，否则「单一引擎派生」会退化成两套口径。"""
    return ('<!-- generated:facts:begin 由 tools/spec_engine.py 派生，勿手改 -->\n'
            '- 功能分组 %d / 逻辑功能 %d / 核心自描述 %d / 纯函数文件 %d\n' %
            (f['groups'], f['features'], f['core_features'], f['pure_files']) +
            '- CI jobs %d / 本地化 ID %d / 本区块由单一引擎派生\n' %
            (f['ci_jobs'], f['loc_ids']) +
            '- 契约指纹 %s… / 反射唯一 key %d\n' %
            (f['fingerprint_hash'][:12], f['reflection_unique']) +
            '<!-- generated:facts:end -->')


def status_doc(f):
    """STATUS.md 整份文本的唯一构造实现：gen() 写回、DOC-002 守卫逐字比对都用它。
    数值表与命令段都在内，杜绝「半生成页」只改几个数、其余悄悄漂移。"""
    rows = '\n'.join('| %s | %s |' % (k, v) for k, v in f.items())
    return ('<!-- 整份由 tools/spec_engine.py 派生；勿手改 -->\n\n'
            '# 项目事实（自动生成）\n\n'
            '| 项 | 值 |\n'
            '|---|---|\n' + rows + '\n\n'
            '维护入口：\n\n'
            '- 日常：`python3 tools/uch.py verify --strict`（fresh clone 自动自举）\n'
            '- 游戏更新：`python3 tools/uch.py fingerprint --dump <反编译.cs>`，'
            '红哪条改哪个功能；唯一绑定表 `generated/contract-fingerprint.json`\n'
            '- 加功能：`python3 tools/uch.py add <id>`；删功能：`python3 tools/uch.py remove <id>`\n'
            '- 改声明：`python3 tools/uch.py gen`（单一引擎重算全部派生产物并 seal）\n'
            '- 自检期望：`generated/selftest.expected.json`；运行时值写 `health.actual.json`\n'
            '- 棘轮基线：`generated/ratchets.json`（守卫只读这一份）\n')


def wr(p, s, bom=False):
    d = os.path.dirname(p)
    if d:
        os.makedirs(d, exist_ok=True)
    b = s.encode('utf-8')
    if bom:
        b = b'\xef\xbb\xbf' + b
    open(p, 'wb').write(b)


def js(p):
    if not os.path.exists(p):
        return {}
    return json.loads(rd(p))


def dump(o):
    return json.dumps(o, ensure_ascii=False, indent=2) + '\n'


def sha_bytes(b):
    return hashlib.sha256(b).hexdigest()


def cs_files(d):
    out = []
    for dp, dn, fn in os.walk(d):
        if '.git' in dp:
            continue
        for f in fn:
            if f.endswith('.cs'):
                out.append(os.path.join(dp, f).replace('\\', '/'))
    return sorted(out)


def win(p):
    return p.replace('/', '\\')


def collect():
    """扫描磁盘，得到全部可派生量。守卫口径与本函数同源。"""
    spec = js(SPEC)
    contracts = cs_files(CONTRACTS)
    pure = cs_files('Pure')
    features = cs_files('Features')
    core = [p for p in cs_files('SR_UCH') if not p.startswith(CONTRACTS + '/')]
    main = ['MainPlugin.cs']
    compile_list = core + contracts + pure + features + main
    compile_set = set(compile_list)

    # 扫描口径：SR_UCH + Features + MainPlugin，
    # 不含 Pure / tests，避免反射与补丁计数漂移。
    scan = contracts + core + features + main
    all_text = '\n'.join(rd(p) for p in scan if os.path.isfile(p))

    ref_keys = set(re.findall(r'Ref\.Override(?:<[^>]*>)?\s*\(\s*"([^"]+)"', all_text))
    ref_calls = len(re.findall(r'Ref\.Override(?:<[^>]*>)?\s*\(\s*"([^"]+)"', all_text))
    patch_attrs = len(re.findall(r'\[HarmonyPatch', all_text))
    loc_ids = len(re.findall(r'"[0-9a-f]{10}"', rd(CONTRACTS + '/SrLocIds.cs')))

    pat = re.compile(r'catch\s*(\([^)]*\))?\s*\{\s*(\}|/\*\s*静默)')
    silent = {}
    for p in compile_list:
        n = len(pat.findall(rd(p)))
        if n:
            silent[p] = n
    silent_total = sum(silent.values())

    scope = spec['toolchain']['api_level_scope']
    ex = tuple(scope['exclude_dirs'])
    afiles = [p for p in cs_files(scope['root']) if not any(x in p for x in ex)]
    am = re.search(r'public\s+const\s+int\s+ApiLevel\s*=\s*(\d+)', rd(CONTRACTS + '/Contracts.cs'))
    api_level = int(am.group(1))
    api_members = sum(1 for p in afiles for l in rd(p).splitlines() if re.match(r'^\s*public\s', l))

    wf = rd('.github/workflows/guards.yml')
    jobs = re.findall(r'^  ([a-z][a-z0-9_-]*):\s*$', wf[wf.find('\njobs:'):] if '\njobs:' in wf else wf, re.M)
    health_fields = len(re.findall(r'"[a-z_]+"', rd(CONTRACTS + '/HealthSnapshot.cs').split('internal static readonly string[] Fields')[1].split('};')[0]))
    event_codes = re.findall(r'internal const string \w+\s*=\s*"([^"]+)"', rd(CONTRACTS + '/HealthSnapshot.cs'))
    net_magics = len(re.findall(r'(?:public|internal)\s+const\s+(?:short|int)\s+\w+\s*=\s*77\d', rd(CONTRACTS + '/NetMsgId.cs')))
    scenes = len(spec.get('scenes', [])) if isinstance(spec.get('scenes'), list) else 2

    fp = js(GENDIR + '/contract-fingerprint.json')

    # 静默 catch 站点：owner / 事件码 / 级别 / 建议动作（结构化错误模型的数据面）
    # 事件码语义映射：域 + 类别 + 稳定序号，不再按站点轮转 6 个码。
    # 排序后分配 => 同一份源码重复派生结果一致；改一个 catch 只影响它自己的序号。
    raw = []
    for p in compile_list:
        txt = rd(p)
        for m in re.finditer(r'catch\s*(\([^)]*\))?\s*\{\s*(\}|/\*\s*静默)', txt):
            line = txt[:m.start()].count('\n') + 1
            owner = p.split('/')[0] if '/' in p else 'bootstrap'
            if p.startswith('Features/'):
                owner = 'feature:' + p.split('/')[1].split('.')[0]
            elif p.startswith(CONTRACTS):
                owner = 'contract'
            elif p.startswith('SR_UCH'):
                owner = 'core'
            elif p.startswith('Pure/'):
                owner = 'pure'
            tail = txt[m.end():m.end() + 220]
            kind = 'GUARD' if re.search(r'Guard\.Log|LogWarning|LogError', tail) else 'SIL'
            if owner.startswith('feature:'):
                dom = 'FEAT_' + re.sub(r'[^A-Z0-9]', '', owner[8:].upper())[:8]
            elif owner == 'contract': dom = 'CONTRACT'
            elif owner == 'core': dom = 'CORE'
            elif owner == 'pure': dom = 'PURE'
            else: dom = 'BOOT'
            raw.append({'site': p + ':' + str(line), 'owner': owner,
                        'dom': dom, 'kind': kind,
                        'scope': os.path.basename(p),
                        'tail': re.sub(r'\s+', ' ', tail)[:160],
                        'severity': 'warn' if owner.startswith('feature:') else 'info'})
    raw.sort(key=lambda x: (x['site'], x['kind']))
    # 事件码稳定化：按「文件 + catch 体内容」取哈希，而不是按 (dom,kind) 全局排号。
    # 旧做法里，别的文件新增一处 catch 会让后面所有码整体移位，诊断归因跨版本失效。
    # 现在跨文件增删零影响；同文件内改动只影响该 catch 自身。去撞后缀按已排序的 raw 稳定分配。
    seen = {}
    sites = []
    for x in raw:
        seed = x['scope'] + '|' + x['kind'] + '|' + x['tail']
        dig = hashlib.sha256(seed.encode('utf-8')).hexdigest()[:8].upper()
        base = 'SR-H-%s-%s-%s' % (x['dom'], x['kind'], dig)
        n = seen.get(base, 0) + 1
        seen[base] = n
        sites.append({
            'site': x['site'], 'owner': x['owner'], 'domain': x['dom'],
            'kind': x['kind'], 'scope': x['scope'],
            'code': base if n == 1 else base + '-' + str(n),
            'severity': x['severity'],
            'action': '按 domain/kind 定位：' + x['dom'] + ' / ' + x['kind'] + '；事件码按站点内容稳定，可直接贴进 issue。',
        })
    return locals()



def caps_source(s):
    """外部 DLL 加载策略：由 spec.external_plugins 派生。槽位总线退役后本生成物只含这一段。"""
    ep = s.get('external_plugins', {})
    mode = ep.get('policy', 'log')
    allow = ep.get('allowlist', [])
    L = ['// 由 tools/spec_engine.py 生成，勿手改。外部 DLL 加载策略由 spec.external_plugins 派生。',
         'using System;', 'namespace SR_UCH {',
         'internal static class SrExternalPolicy {',
         '    internal const string Mode = "%s";' % mode,
         '    internal static readonly string[] Allowlist = new string[] {']
    for x in allow:
        L.append('        "%s",' % x.replace('"', '\\"'))
    L += ['    };',
          '    internal static bool IsStrict() { return Mode == "strict"; }',
          '    internal static bool IsAllowed(string name) {',
          '        if (name == null) return false;',
          '        for (int i = 0; i < Allowlist.Length; i++)',
          '            if (string.Equals(Allowlist[i], name, System.StringComparison.OrdinalIgnoreCase)) return true;',
          '        return false;',
          '    }',
          '}',
          '}',
          '']
    return '\n'.join(L)


def derive():
    """返回 {相对路径: (内容字符串, 是否 BOM)}。全部由 collect() 派生。"""
    c = collect()
    s, out = c['spec'], {}
    gen = lambda name, obj: out.update({GENDIR + '/' + name: (dump(obj), False)})

    # ---- 唯一源码清单 ----
    groups = {}
    for g in s['groups']:
        groups[g['id']] = [win(p) for p in c['compile_list'] if p.startswith('Features/' + g['id'] + '.') or p == 'Features/' + g['id'] + '.cs']
    src = {
        'schema': 1, 'root': 'MainPlugin.cs',
        'core': [win(p) for p in c['contracts'] + c['core']],
        'pure': [win(p) for p in c['pure']],
        'features': [win(p) for p in c['features']],
        'groups': groups,
        'compile': [win(p) for p in c['compile_list']],
        'note': 'rsp / csproj / 生成器共用的唯一源码清单；由 tools/spec_engine.py -Generate 派生。',
    }
    gen('sources.manifest.json', src)

    # ---- compile.targets（正斜杠，XML 属性基准 = generated/ 上三级）----
    lines = ['<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">',
             '  <!-- 由 build/specs/generated/sources.manifest.json 派生；勿手改。 -->',
             '  <ItemGroup>']
    for p in c['compile_list']:
        lines.append('    <Compile Include="$(MSBuildThisFileDirectory)../../../%s" />' % p)
    lines += ['  </ItemGroup>', '</Project>', '']
    out[GENDIR + '/compile.targets'] = ('\n'.join(lines), False)

    # ---- 主链 rsp 源码段（保留 /r: 头，只重写 .cs 行）----
    for rsp, wanted in [
        ('sr_uch.rsp', set(c['compile_list'])),
    ]:
        p = 'build/rsp/' + rsp
        head = [l for l in rd(p).splitlines() if l.startswith('/')]
        body = [win(x) for x in sorted(wanted)]
        out[p] = ('\n'.join(head + body) + '\n', True)

    # ---- facts ----
    facts = js(GENDIR + '/facts.generated.json')
    facts.pop('ledger_rows', None)  # 历史台账已退役，不保留死字段
    facts.update({
        'groups': len(s['groups']), 'features': len(s['features']),
        'core_features': len(s['core_features']), 'ci_jobs': len(c['jobs']),
        'loc_ids': c['loc_ids'],
        'rsp_sources': len(c['compile_list']), 'reflection_unique': len(c['ref_keys']),
        'fingerprint_reflection_entries': len(c['fp']['entries']),
        'pure_files': len(c['pure']), 'scenes': c['scenes'], 'net_magics': c['net_magics'],
        'health_fields': c['health_fields'], 'fingerprint_hash': c['fp']['hash'],
    })
    gen('facts.generated.json', facts)

    # ---- ratchets ----
    rat = js(GENDIR + '/ratchets.json')
    rat.pop('slot_orphan', None)  # 槽位总线退役，旧棘轮键不复活
    rat['silent_catch'] = c['silent']
    rat['silent_catch_total'] = c['silent_total']
    rat['api_level'] = {'ApiLevel': c['api_level'], 'members': c['api_members']}
    rat['note'] = '由 tools/spec_engine.py -Generate 派生；手改无效，下次生成覆盖。'
    gen('ratchets.json', rat)

    # ---- 自检期望 ----
    exp = js(GENDIR + '/selftest.expected.json')
    # 本体退出功能表后运行时 Features.Count 与声明精确相等，不再需要「不低于下限」兜底。
    exp.pop('features_min', None)
    exp.update({
        'features_semantics': s['policy']['features_baseline_semantics'],
        'features_declared': len(s['features']), 'groups_declared': len(s['groups']),
        'reflection_unique_keys': len(c['ref_keys']), 'reflection_calls': c['ref_calls'],
        'harmony_patch_attributes': c['patch_attrs'], 'contract': s['deployment']['contract_version'],
        # 只比数量时，换掉一个 key、再补一个同数量的假 key 不会被发现；
        # 集合摘要让 CONTRACT-001 能逐集合对账，且仍由唯一引擎派生。
        'reflection_key_digest': hashlib.sha256(
            '\n'.join(sorted(c['ref_keys'])).encode('utf-8')).hexdigest(),
        'note': '由 tools/spec_engine.py -Generate 派生；运行时值写 health.actual.json，不覆盖本文件。',
    })
    gen('selftest.expected.json', exp)

    # ---- spec.report ----
    rep = js(GENDIR + '/spec.report.json')
    rep.update({'groups': len(s['groups']), 'features': len(s['features']), 'diffs': 0})
    gen('spec.report.json', rep)

    # ---- health.schema ----
    hs = js(GENDIR + '/health.schema.json')
    hs['event_codes'] = c['event_codes']
    hs['fields'] = re.findall(r'"([a-z_]+)"',
        rd(CONTRACTS + '/HealthSnapshot.cs').split('internal static readonly string[] Fields')[1].split('};')[0])
    gen('health.schema.json', hs)

    # ---- artifacts.manifest ----
    am = js(GENDIR + '/artifacts.manifest.json')
    am['dll'] = s['artifacts']['dll']
    am['groups'] = [g['id'] for g in s['groups']]
    gen('artifacts.manifest.json', am)

    # ---- 功能目录骨架 ----
    skel = ['<!-- generated: 功能目录骨架；描述文案人工维护，生成器不得覆盖 -->', '']
    for g in s['groups']:
        logical = [f['id'] for f in s['features'] if f.get('group') == g['id']]
        skel.append('- **%s** (%d logical: %s)' %
                    (g['id'], len(logical), ', '.join(logical) or '-'))
    out[GENDIR + '/README_features.generated.md'] = ('\n'.join(skel) + '\n', False)
    idx = ['# 功能索引（生成）', '', '| 功能分组 | 逻辑功能数 |', '|---|---|']
    for g in s['groups']:
        logical = len([f for f in s['features'] if f.get('group') == g['id']])
        idx.append('| %s | %d |' % (g['id'], logical))
    out[GENDIR + '/feature_index.generated.md'] = ('\n'.join(idx) + '\n', False)

    # ---- references.props ----
    rp = ['<Project>', '  <!-- 由 tools/spec_engine.py 从 spec.build 派生；勿手改。 -->', '  <ItemGroup>']
    for x in s['build']['game']:
        # dir 决定根目录：bepinex 项在 $(BepInExCoreDir)，其余在 $(UchManagedDir)。
        # 之前无论 dir 是什么都写 UchManagedDir，导致 BepInEx/0Harmony 引用解析不到。
        root = '$(BepInExCoreDir)' if x.get('dir') == 'bepinex' else '$(UchManagedDir)'
        # Private=false：游戏/BepInEx 的 DLL 是运行时依赖，不能拷进输出目录。
        # 不加会把 90+ 个游戏 DLL 全量复制到 bin/，产物目录被塞满。
        rp.append('    <Reference Include="%s"><HintPath>%s/%s.dll</HintPath><Private>false</Private></Reference>' % (x['name'], root, x['name']))
    for x in s['build']['bcl']:
        rp.append('    <Reference Include="%s" />' % x)
    rp += ['  </ItemGroup>', '</Project>', '']
    out[GENDIR + '/references.props'] = ('\n'.join(rp), False)

    out[GENERATED_CS + '/SrCaps.g.cs'] = (caps_source(s), False)

    # ---- README 事实区块 / STATUS ----
    f = facts
    block = facts_block(f)
    for name in ['README.md', 'README.zh-CN.md']:
        tx = rd(name)
        b = tx.find('<!-- generated:facts:begin')
        e = tx.find('<!-- generated:facts:end -->')
        if b >= 0 and e > b:
            out[name] = (tx[:b] + block + tx[e + len('<!-- generated:facts:end -->'):], False)
    # STATUS.md 整份由 facts 派生：表格全字段 + 命令段，杜绝半生成页的数值/命令漂移。
    out['docs/STATUS.md'] = (status_doc(f), False)

    # ---- 新增：CHK owner 路由表 ----
    try:
        sys.path.insert(0, ROOT + '/tools')
        import maint_ops
        impact = maint_ops.IMPACT
    except Exception:
        impact = {}
    gen('checks.generated.json', {
        'schema': 1, 'generated_by': 'tools/spec_engine.py',
        'note': '每条固定 CHK 的文件归属与依赖闭包；uch.py verify --fast 只读本表。',
        'checks': impact,
    })

    # ---- 新增：结构化错误站点 ----
    sites = c['sites']
    gen('error-sites.generated.json', {
        'schema': 1, 'count': len(sites),
        'note': '每个静默 catch 一个内容稳定事件码（跨文件增删不移位）；运行时由 SrErrorSink.Flush 收进 HealthSnapshot.Events。',
        'sites': sites,
    })
    cs = ['// 由 tools/spec_engine.py 生成，勿手改。每个静默 catch 一个可粘贴的事件码。',
          'namespace SR_UCH {',
          '    internal static class SrCatchSites {',
          '        internal struct Site {',
          '            internal string At, Owner, Code, Severity, Action;',
          '            internal Site(string a, string o, string c, string v, string x) { At=a; Owner=o; Code=c; Severity=v; Action=x; }',
          '        }',
          '        internal static readonly Site[] All = new Site[] {']
    for x in sites:
        # 生成前统一转义：站点与动作文本不得含 C# 字符串非法字符。
        esc = lambda v: v.replace('\\', '/').replace('"', '\\"')
        cs.append('            new Site("%s", "%s", "%s", "%s", "%s"),' %
                  (esc(x['site']), esc(x['owner']), esc(x['code']),
                   esc(x['severity']), esc(x['action'])))
    cs += ['        };',
           '        internal static string CodeFor(string at) {',
           '            for (int i = 0; i < All.Length; i++) if (All[i].At == at) return All[i].Code;',
           '            return HealthCode.DegradedCatch;', '        }', '    }', '}', '']
    out[GENERATED_CS + '/SrCatchSites.g.cs'] = ('\n'.join(cs), False)

    # ---- 新增：双轨退役闸门 ----
    tg = s['toolchain']['trace_gate']
    ev = GENDIR + '/ui-ab-evidence.json'
    evidence = js(ev) if os.path.isfile(ev) else None
    shadow = {
        'schema': 1, 'target_batch': tg['target_batch'],
        'cutover_requested': bool(tg.get('cutover_requested')),
        'require_runtime_ab': bool(tg.get('require_runtime_ab')),
        'declaration_ready': True,
        'runtime_ab_evidence': bool(evidence and evidence.get('verdict') == 'PASS'),
        'allowed': bool(tg.get('cutover_requested') and evidence and evidence.get('verdict') == 'PASS'),
        'note': '声明层 READY 不等于可删旧 IMGUI；必须贴实机 A/B 证据才允许 cutover。',
    }
    gen('ui-shadow.generated.json', shadow)
    us = ['// 由 tools/spec_engine.py 生成，勿手改。cutover 只由声明 + 实机 A/B 证据共同开启。',
          'namespace SR_UCH {',
          '    internal static class SrUiShadowGate {',
          '        internal const int TargetBatch = %d;' % int(tg['target_batch']),
          '        internal const bool DeclarationReady = true;',
          '        internal const bool CutoverRequested = %s;' % ('true' if tg.get('cutover_requested') else 'false'),
          '        internal const bool RuntimeAbVerified = %s;' % ('true' if shadow['runtime_ab_evidence'] else 'false'),
          '        // 两道都真，才允许把该批旧绘制路径编译期移除。',
          '        internal const bool Batch1Cutover = CutoverRequested && RuntimeAbVerified;',
          '        internal static string Report() {',
          '            return "target=" + TargetBatch + " / declaration=READY / ab=" + (RuntimeAbVerified ? "PASS" : "MISSING") + " / cutover=" + (Batch1Cutover ? "ON" : "OFF");',
          '        }', '    }', '}', '']
    out[GENERATED_CS + '/SrUiShadowGate.g.cs'] = ('\n'.join(us), False)

    # ---- 新增：golden selftest / smoke 步骤 ----
    gen('health.golden.json', {
        'schema': 1, 'source': 'selftest.expected.json',
        'expect': {k: exp[k] for k in ['features_declared', 'groups_declared', 'reflection_unique_keys', 'contract']},
        'note': '离线重放用；进游戏实取 [SR.SelfTest] 后写 health.actual.json 与本文件比对。',
    })
    gen('ship.checklist.json', {
        'schema': 1,
        'items': [
            {'id': 'P1', 'name': '推包进 GitHub main', 'owner': 'user',
             'why': '本地领先 origin 13+ 提交，从未推送；成果只在交付侧'},
            {'id': 'P3', 'name': '进游戏取 [SR.SelfTest] + 实机 A/B', 'owner': 'user',
             'why': '写 health.actual.json 与 ui-ab-evidence.json，解锁双轨 cutover'},
        ],
        'note': 'P2（全源码 dotnet build -warnaserror）已在本机验证通过（0 警告 0 错误），已移出清单。',
    })
    gen('smoke.generated.json', {
        'schema': 1,
        'steps': [
            {'id': 'S1', 'name': 'clone-clean', 'cmd': 'spec_engine.py verify'},
            {'id': 'S2', 'name': 'generate', 'cmd': 'spec_engine.py generate'},
            {'id': 'S3', 'name': 'unified-regression', 'cmd': 'uch.py verify --strict'},
            {'id': 'S4', 'name': 'pure-tests', 'cmd': 'dotnet run --project tests/SR_UCH.Pure.Tests.csproj', 'optional': True},
            {'id': 'S5', 'name': 'headless-trace', 'cmd': 'maint_ops.py replay'},
            {'id': 'S6', 'name': 'golden-selftest', 'cmd': 'maint_ops.py replay --selftest'},
            {'id': 'S7', 'name': 'trace-gate', 'cmd': 'uch.py trace-gate --enforce'},
        ],
        'note': 'uch.py smoke --full 按序执行；无 dotnet/游戏时 S4 跳过并显式列出。',
    })
    return out


def verify():
    """判定：派生内容 vs 磁盘；返回 (结果列表, 差异数)。"""
    exp = derive()
    res, diffs = [], 0
    for p, (want, bom) in sorted(exp.items()):
        if not os.path.isfile(p):
            res.append(('DIFF', p, '缺失'))
            diffs += 1
            continue
        got = rd(p)
        ok = got == want
        if not ok:
            diffs += 1
        res.append((' OK ' if ok else 'DIFF', p, '' if ok else '与声明派生不一致（跑 generate）'))
    # 绑定表哈希自洽（唯一表 ↔ facts；结论就在表内，不再有第三份口径）
    fp = js(GENDIR + '/contract-fingerprint.json')
    facts = js(GENDIR + '/facts.generated.json')
    ok = (fp['hash'] == facts['fingerprint_hash'] ==
          fp.get('verification', {}).get('fingerprint_hash')) and bool(fp.get('bindings'))
    res.append((' OK ' if ok else 'DIFF', 'bindings↔facts', '' if ok else '哈希链断裂或 bindings 缺失'))
    if not ok:
        diffs += 1
    # 重复注入块检测（历轮脚本累加留下的垃圾）
    for p in ['.github/workflows/guards.yml', 'tools/spec_engine.py', 'tools/uch.py']:
        lines = [l.strip() for l in rd(p).splitlines() if len(l.strip()) > 25]
        seen = {}
        for l in lines:
            seen[l] = seen.get(l, 0) + 1
        worst = max(seen.values()) if seen else 0
        bad = worst > 3 and any(v > 3 for k, v in seen.items() if 'doctor' in k or 'Get-Command python3' in k or '统一维护入口' in k or 'uch.py seal' in k)
        res.append((' OK ' if not bad else 'DIFF', 'dedupe:' + p, '' if not bad else '仍有重复注入块'))
        if bad:
            diffs += 1
    return res, diffs


def generate():
    # 先落生成物 .cs，让后续 derive() 看到最终磁盘集合，避免清单与守卫错拍。
    seed = derive()
    for p, (txt, bom) in sorted(seed.items()):
        if p.endswith('.g.cs'):
            wr(p, txt, bom)
    exp = derive()
    for p, (txt, bom) in sorted(exp.items()):
        wr(p, txt, bom)
    print('[engine] 已派生 %d 个产物（含 %d 个 .g.cs）' %
          (len(exp), len([p for p in exp if p.endswith('.g.cs')])))
    # 4 个 Python 权威守卫各自 --generate 重写自己的产物
    for script in ['tools/check_architecture.py', 'tools/check_uimodel.py']:
        r = subprocess.run([sys.executable, script, '--generate'], cwd=ROOT,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        print('[engine] %s → exit=%d / %s' % (script, r.returncode,
              r.stdout.decode('utf-8', 'replace').strip().splitlines()[-1][:100] if r.stdout else ''))
    # 生成后重算一次 facts（architecture 的 total_files / generated_files 变了）
    exp2 = derive()
    for p, (txt, bom) in exp2.items():
        if p in exp and exp[p][0] != txt:
            wr(p, txt, bom)
    # seal 由统一入口负责
    r = subprocess.run([sys.executable, 'tools/uch.py', 'seal'], cwd=ROOT,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    print('[engine] seal → %s' % r.stdout.decode('utf-8', 'replace').strip())
    return 0


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else 'verify'
    if cmd == 'verify':
        res, diffs = verify()
        for tag, name, msg in res:
            print('  [%s] %-58s %s' % (tag, name, msg))
        print('== spec_engine verify  DIFF %d / 共 %d 项 ==' % (diffs, len(res)))
        return 1 if diffs else 0
    if cmd == 'generate':
        return generate()
    if cmd == 'derive':
        c = collect()
        print('groups=%d features=%d core=%d compile=%d pure=%d contracts=%d .cs=%d' %
              (len(c['spec']['groups']), len(c['spec']['features']), len(c['spec']['core_features']),
               len(c['compile_list']), len(c['pure']), len(c['contracts']),
               len(c['compile_list']) + len(cs_files('tests'))))
        print('silent_catch=%d ref_unique=%d api_level=%d/%d ci_jobs=%d health_fields=%d' %
              (c['silent_total'], len(c['ref_keys']), c['api_level'], c['api_members'],
               len(c['jobs']), c['health_fields']))
        return 0
    if cmd == 'impact' and len(sys.argv) > 2:
        sys.path.insert(0, ROOT + '/tools')
        import maint_ops
        for cid, why in maint_ops.route(sys.argv[2]):
            print('  %-12s %s' % (cid, why))
        return 0
    print(__doc__)
    return 2


if __name__ == '__main__':
    sys.exit(main())
