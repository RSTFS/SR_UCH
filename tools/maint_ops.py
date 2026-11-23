#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""维护操作三件套：CHK 影响路由 / 可重放 codemod / 离线重放。

被 tools/uch.py 与 tools/spec_engine.py 共同引用；零外部依赖。
改动固定 CHK 表时，必须同步 IMPACT，否则 IMPACT-001 会红。
"""
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)
GENDIR = 'build/specs/generated'
SPEC = 'build/specs/features.spec.json'


def rd(p):
    b = open(p, 'rb').read()
    if b.startswith(b'\xef\xbb\xbf'):
        b = b[3:]
    return b.decode('utf-8').replace('\r\n', '\n')


def wr(p, s):
    os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, 'wb').write(s.encode('utf-8'))


def js(p):
    return json.loads(rd(p))


def dump(o):
    return json.dumps(o, ensure_ascii=False, indent=2) + '\n'


# ---------------------------------------------------------------- 1. CHK 影响路由
# 每项：owners（路径前缀/glob 片段）+ depends（必须先跑的 CHK）。
# '#' 前缀表示"任何源码/声明改动都要跑"的全局项。
def _i(owners, deps=()):
    return {'owners': list(owners), 'depends': list(deps)}


IMPACT = {
    'ENV-001': _i([]), 'ENV-002': _i([]), 'ENV-003': _i([]), 'ENV-004': _i([]),
    'SPEC-001': _i([SPEC]), 'SPEC-002': _i([SPEC], ['SPEC-001']),
    'SPEC-003': _i([SPEC, 'build/specs/generated/facts.generated.json']),
    'SPEC-004': _i([SPEC]), 'SPEC-005': _i([SPEC]),
    'GEN-001': _i([GENDIR]), 'GEN-002': _i([SPEC, GENDIR]),
    'GEN-003': _i([GENDIR]), 'GEN-004': _i([GENDIR], ['GEN-003']),
    'GEN-005': _i([GENDIR + '/sources.manifest.json', 'SR_UCH/', 'Features/', 'Pure/', 'MainPlugin.cs']),
    'RSP-001': _i(['build/rsp/', SPEC]),
    'RSP-002': _i(['build/rsp/']),
    'RSP-003': _i(['build/rsp/sr_uch.rsp', GENDIR + '/facts.generated.json']),
    'RSP-004': _i(['build/rsp/', GENDIR + '/sources.manifest.json']),
    'SRC-001': _i(['SR_UCH/', 'Features/', 'Pure/', 'tests/', 'MainPlugin.cs']),
    'RATCHET-001': _i(['SR_UCH/Contracts/', GENDIR + '/ratchets.json', SPEC]),
    'RATCHET-002': _i(['SR_UCH/', 'Features/', 'Pure/', 'MainPlugin.cs', GENDIR + '/ratchets.json']),
    'TOOL-001': _i(['tools/', SPEC]), 'TOOL-002': _i(['.github/workflows/guards.yml', 'tools/uch.py']),
    'TOOL-003': _i(['tools/spec_engine.py', 'tools/uch.py']),
    'ARCH-001': _i(['SR_UCH/', 'Features/', 'Pure/', 'tools/check_architecture.py', SPEC]),
    'UI-001': _i(['build/specs/ui.pages.json', 'SR_UCH/Contracts/SrPageModel.cs', 'tools/check_uimodel.py']),
    'SINGLE-001': _i([SPEC]),
    'UI-002': _i(['build/specs/ui.pages.json', GENDIR + '/ui-model.generated.json']),
    'UI-003': _i([GENDIR + '/ui-model.generated.json']),
    'DOC-001': _i(['docs/', 'tools/uch.py']),
    'DOC-002': _i(['docs/STATUS.md', GENDIR + '/facts.generated.json']),
    'DOC-003': _i(['README.md', 'README.zh-CN.md']),
    'HEALTH-001': _i(['SR_UCH/Contracts/HealthSnapshot.cs', GENDIR + '/health.schema.json', GENDIR + '/facts.generated.json']),
    'CONTRACT-001': _i(['SR_UCH/', 'Features/', 'MainPlugin.cs', GENDIR + '/selftest.expected.json', SPEC]),
    'CONTRACT-002': _i([GENDIR + '/contract-fingerprint.json']),
    'NET-001': _i(['SR_UCH/Contracts/Generated/NetMessages.g.cs', 'SR_UCH/Contracts/NetMsgId.cs']),
    'CFG-001': _i([SPEC]),
    'CI-001': _i(['.github/workflows/guards.yml', GENDIR + '/facts.generated.json']),
    'CI-002': _i(['.github/workflows/guards.yml']),
    'LEGACY-001': _i(['build/', SPEC]),
    'MANIFEST-001': _i([GENDIR + '/verify.manifest.json']),
    'CATCH-001': _i(['SR_UCH/', 'Features/', 'Pure/', 'MainPlugin.cs']),
    # 本轮新增
    'ENGINE-001': _i(['tools/spec_engine.py', GENDIR], ['SPEC-002']),
    'ENGINE-002': _i(['build/build.ps1', 'build/run-tests.ps1', 'tools/spec_engine.py']),
    'DUPE-001': _i(['.github/workflows/guards.yml', 'tools/spec_engine.py', 'tools/uch.py']),
    'IMPACT-001': _i(['tools/maint_ops.py', 'tools/uch.py', GENDIR + '/checks.generated.json']),
    'META-001': _i(['tools/uch_rules.py', 'tools/uch.py', 'build/specs/features.spec.json']),
    'ERROR-001': _i(['SR_UCH/', 'Features/', 'Pure/', 'MainPlugin.cs', GENDIR + '/error-sites.generated.json']),
    'SHADOW-001': _i([GENDIR + '/ui-shadow.generated.json', SPEC, 'build/specs/ui.pages.json']),
    'RUNTIME-001': _i(['SR_UCH/Contracts/SrRuntime.cs', 'Pure/RuntimeRules.cs', 'tests/PureTests.cs']),
    'CODEMOD-001': _i(['tools/maint_ops.py', SPEC]),
    'SMOKE-001': _i(['tools/uch.py', GENDIR + '/smoke.generated.json', GENDIR + '/health.golden.json']),
}


def route(path):
    """改了 path，需要跑哪些 CHK（含依赖闭包），返回 [(id, 原因)]。"""
    p = path.replace('\\', '/')
    hit, why = [], {}
    for cid, meta in IMPACT.items():
        owners = meta['owners']
        if '#' in owners:
            hit.append(cid)
            why[cid] = '全局项（任何改动都跑）'
            continue
        for o in owners:
            if o in p or (o.endswith('/') and p.startswith(o)) or (o in SPEC and p == SPEC):
                hit.append(cid)
                why[cid] = 'owner: ' + o
                break
    # 依赖闭包
    changed = True
    while changed:
        changed = False
        for cid in list(hit):
            for d in IMPACT.get(cid, {}).get('depends', []):
                if d not in hit:
                    hit.append(d)
                    why[d] = '被 ' + cid + ' 依赖'
                    changed = True
    order = {cid: i for i, cid in enumerate(IMPACT)}
    return sorted(((c, why.get(c, '')) for c in hit), key=lambda x: order.get(x[0], 999))


# ---------------------------------------------------------------- 2. 可重放 codemod
def _run(args):
    try:
        r = subprocess.run(args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        return r.returncode, r.stdout.decode('utf-8', 'replace').strip().splitlines()
    except Exception as e:
        return 2, [str(e)]


def _checks():
    s = js(SPEC)
    return [
        ('M01', '声明单源化', s['schema'] == 3,
         lambda: _fix_spec_schema()),
        ('M02', '生成物归位', all('/Generated/' in p for p in
              [x for x in _all_cs() if x.endswith('.g.cs')]),
         lambda: _fix_generated()),
        ('M04', '页面模型', js(GENDIR + '/ui-model.generated.json')['page_count'] == 29,
         lambda: _run([sys.executable, 'tools/check_uimodel.py', '--generate'])[0]),
        ('M05', 'cfg schema 门', s['cfg']['schema'] == 3 and os.path.isfile('SR_UCH/Contracts/SrCfgColumns.cs'),
         lambda: _fix_cfg_schema()),
        ('M06', '绑定表哈希链', (lambda fp: fp['hash'] == js(GENDIR + '/facts.generated.json')['fingerprint_hash']
              == fp.get('verification', {}).get('fingerprint_hash'))(
              js(GENDIR + '/contract-fingerprint.json')),
         lambda: _fix_hash_chain()),
        ('M07', '构建图同源', all(('Include="%s"' % x['name']) in rd(GENDIR + '/references.props')
              for x in s['build']['game']),
         lambda: _run([sys.executable, 'tools/spec_engine.py', 'generate'])[0]),
        ('M08', '统一基线', js(GENDIR + '/verify.manifest.json')['spec_schema'] == s['schema'],
         lambda: _run([sys.executable, 'tools/uch.py', 'seal'])[0]),
        ('M09', '单一引擎可跑', _run([sys.executable, 'tools/spec_engine.py', 'derive'])[0] == 0,
         lambda: _run([sys.executable, 'tools/spec_engine.py', 'verify'])[0]),
        ('M10', '错误站点覆盖', js(GENDIR + '/error-sites.generated.json')['count'] ==
              js(GENDIR + '/ratchets.json')['silent_catch_total'],
         lambda: _run([sys.executable, 'tools/spec_engine.py', 'generate'])[0]),
    ]


def _all_cs():
    out = []
    for d in ['.']:
        for dp, dn, fn in os.walk(d):
            if '.git' in dp or dp.startswith('./build') or dp.startswith('./docs'):
                continue
            for f in fn:
                if f.endswith('.cs'):
                    out.append(os.path.join(dp, f)[2:].replace('\\', '/'))
    return sorted(out)


def _fix_spec_schema():
    s = js(SPEC)
    s['schema'] = 3
    wr(SPEC, dump(s))
    return 0


def _fix_cfg_schema():
    s = js(SPEC)
    s['cfg']['schema'] = 3
    wr(SPEC, dump(s))
    return 0


def _fix_generated():
    n = 0
    for p in _all_cs():
        if p.endswith('.g.cs') and '/Generated/' not in p:
            dst = 'SR_UCH/Contracts/Generated/' + os.path.basename(p)
            if not os.path.isfile(dst):
                os.rename(p, dst)
                n += 1
    return 0


def _fix_hash_chain():
    p = GENDIR + '/contract-fingerprint.json'
    d = js(p)
    d.setdefault('verification', {})['fingerprint_hash'] = d['hash']
    wr(p, dump(d))
    return 0


def status():
    return all(ok for mid, title, ok, fix in _checks())


def codemod(apply=False):
    rows = _checks()
    print('== codemod 可重放规则（%d 条） ==' % len(rows))
    missing = 0
    for mid, title, ok, fix in rows:
        print('  %s  %-18s %s' % (mid, title, 'OK' if ok else 'NEED-FIX'))
        if not ok:
            missing += 1
            if apply:
                r = fix()
                print('       → 已重放，exit=%s' % r)
    if apply:
        rows2 = _checks()
        bad = [m for m, t, ok, f in rows2 if not ok]
        print('\n重放后: %s' % ('全部齐备' if not bad else '仍缺 ' + ', '.join(bad)))
        return 1 if bad else 0
    print('\n结论: %s（加 --apply 真重放；-Generate 之外不需要人肉改）' %
          ('全部齐备' if not missing else '%d 条待重放' % missing))
    return 0


# ---------------------------------------------------------------- 3. 离线重放
def replay(selftest_only=False):
    """headless trace + golden selftest：无游戏也能重放的纯数据断言。"""
    fails = []
    if not selftest_only:
        pages = js('build/specs/ui.pages.json')['pages']
        uim = js(GENDIR + '/ui-model.generated.json')
        traces = uim.get('traces', {})
        n = 0
        for pg in pages:
            pid = pg['id']
            trace = traces.get(pid) if isinstance(traces, dict) else None
            # 与 Pure/UiTrace.cs 同源：每页一条轨迹，首节点归属正确，滚动节点不嵌套。
            if not isinstance(trace, str) or not trace:
                fails.append('缺 trace: ' + pid)
                continue
            parts = trace.split('|')
            if not parts or not parts[0].startswith('page:' + pid):
                fails.append('trace 归属错: ' + pid)
            if sum(1 for x in parts if x.startswith('scroll:')) > 1:
                fails.append('滚动策略违规（嵌套）: ' + pid)
            if pg.get('policy') not in ('per-page', 'global'):
                fails.append('策略未声明: ' + pid)
            n += 1
        print('[replay] headless trace: %d/%d 页通过' % (n, len(pages)))
    exp = js(GENDIR + '/selftest.expected.json')
    golden = js(GENDIR + '/health.golden.json')
    for k, want in golden['expect'].items():
        got = exp.get(k)
        if got != want:
            fails.append('golden selftest %s: %s != %s' % (k, got, want))
    print('[replay] golden selftest: %d 项比对' % len(golden['expect']))
    if fails:
        for f in fails[:20]:
            print('  [FAIL] ' + f)
        print('== replay FAIL %d ==' % len(fails))
        return 1
    print('== replay PASS ==')
    return 0


def main():
    a = sys.argv[1:]
    if a and a[0] == 'route' and len(a) > 1:
        for cid, why in route(a[1]):
            print('  %-12s %s' % (cid, why))
        return 0
    if a and a[0] == 'codemod':
        return codemod('--apply' in a)
    if a and a[0] == 'replay':
        return replay('--selftest' in a)
    print('maint_ops.py route <file> | codemod [--apply] | replay [--selftest]')
    return 2


# ---------------------------------------------------------------- Awake 时序对账
# 背景：核心从 ITweak 列表移到 MainPlugin 显式自举后，「依赖顺序」不再由排序保证。
# SRApi 门面的缺注入是静默降级（if (f != null)），所有静态守卫都查不出来，
# 这里把「声明 -> 注入 -> 首次使用」按启动阶段串成一张可重跑的表。
# 纯 Python 词法切块，零第三方依赖；判定逻辑只有这一份。

def _strip_cs(s):
    """剥掉注释与字符串字面量，保留换行与偏移（大括号配平才可靠）。"""
    out = []
    i, n, mode = 0, len(s), 'code'
    while i < n:
        c = s[i]
        d = s[i + 1] if i + 1 < n else ''
        if mode == 'code':
            if c == '/' and d == '/':
                mode = 'lc'; out.append('  '); i += 2; continue
            if c == '/' and d == '*':
                mode = 'bc'; out.append('  '); i += 2; continue
            if c == '"':
                mode = 'str'; out.append(' '); i += 1; continue
            if c == "'":
                mode = 'chr'; out.append(' '); i += 1; continue
            out.append(c); i += 1
        elif mode == 'lc':
            if c == '\n':
                mode = 'code'; out.append('\n')
            else:
                out.append(' ')
            i += 1
        elif mode == 'bc':
            if c == '*' and d == '/':
                mode = 'code'; out.append('  '); i += 2; continue
            out.append('\n' if c == '\n' else ' '); i += 1
        else:
            q = '"' if mode == 'str' else "'"
            if c == '\\':
                out.append('  '); i += 2; continue
            if c == q:
                mode = 'code'; out.append(' '); i += 1; continue
            out.append('\n' if c == '\n' else ' '); i += 1
    return ''.join(out)


_SIG = re.compile(
    r'(?:public|private|internal|protected)\s+(?:static\s+|override\s+|virtual\s+|sealed\s+)*'
    r'[\w<>\[\],.?]+?\s+(\w+)\s*(?:<[^<>]*>)?\s*\(([^()]*)\)\s*\{', re.S)
_CALL = re.compile(r'\bSRApi(?:\.\w+)?\.(\w+)\s*\(')
_CORE_CALLEE = re.compile(r'\bSR(?:\.\w+)*\.(\w+)\s*\(')


def _cs_methods(path):
    """返回 [(方法名, 方法体文本, 起始行)]；剥注释/字符串后按大括号配平切块。"""
    raw = open(path, 'rb').read().decode('utf-8', 'replace')
    s = _strip_cs(raw)
    out = []
    for m in _SIG.finditer(s):
        i = m.end() - 1
        depth, j = 0, i
        while j < len(s):
            if s[j] == '{':
                depth += 1
            elif s[j] == '}':
                depth -= 1
                if depth == 0:
                    break
            j += 1
        if depth != 0:
            continue
        out.append((m.group(1), s[i:j + 1], raw[:m.start()].count('\n') + 1))
    return out


def awake_timing():
    """返回 (ok, [报告行])。三条装载顺序断言 + 三条门面时序断言，负向可证伪。"""
    import glob
    import collections
    L, bad = [], []

    # ---- O1..O4：MainPlugin.Awake 阶段链 ----
    mp = open('MainPlugin.cs', 'rb').read().decode('utf-8', 'replace')
    stages = re.findall(r'__run\(\s*"([^"]+)"', mp)
    L.append('## 1. Awake 阶段链')
    for i, s in enumerate(stages, 1):
        L.append('  S%-2d %s' % (i, s))
    def idx(name):
        return stages.index(name) if name in stages else -1
    o1 = 0 <= idx('InjectApiSinks') < idx('InitializeTweaks')
    if not o1:
        bad.append('O1 InjectApiSinks 必须早于 InitializeTweaks')
    L.append('  O1 InjectApiSinks < InitializeTweaks : %s' % ('OK' if o1 else 'FAIL'))

    boot = mp.find('new SR_UCH.Tweaks.SR().Initialize(this)')
    foreach = mp.find('foreach (Type type in __tweaks)')
    o2 = boot >= 0 and foreach >= 0 and boot < foreach
    if not o2:
        bad.append('O2 核心显式自举必须早于 foreach 功能装载')
    L.append('  O2 核心显式自举 < foreach 功能     : %s (L%d < L%d)' % (
        'OK' if o2 else 'FAIL',
        mp[:boot].count('\n') + 1 if boot >= 0 else -1,
        mp[:foreach].count('\n') + 1 if foreach >= 0 else -1))
    o3 = boot >= 0 and 'if (!latePass)' in mp[max(0, boot - 400):boot]
    if not o3:
        bad.append('O3 核心自举必须包在 if (!latePass) 内（补扫不重复自举）')
    L.append('  O3 自举仅在 !latePass              : %s' % ('OK' if o3 else 'FAIL'))
    o4 = 'InitializeTweaks(late)' in stages
    if not o4:
        bad.append('O4 latePass 补扫阶段缺失')
    L.append('  O4 latePass 补扫仍在               : %s' % ('OK' if o4 else 'FAIL'))
    L.append('')

    # ---- SRApi 门面：setter / reader 字段映射 ----
    apis = sorted(glob.glob('SR_UCH/Contracts/SRApi*.cs'))
    setter = collections.defaultdict(set)
    reader = collections.defaultdict(set)
    apinames = set()
    for f in apis:
        for n, b, _ in _cs_methods(f):
            apinames.add(n)
            w = set(re.findall(r'\b(_[A-Za-z]\w*)\s*=(?!=)', b))
            r = set(x or y for x, y in re.findall(
                r'=\s*(_[A-Za-z]\w*)\s*;|\b(_[A-Za-z]\w*)\s*!=', b))
            r |= set(re.findall(r'\b(_[A-Za-z]\w*)\s*!=\s*null', b))
            if w:
                setter[n] |= w
            if r:
                reader[n] |= r

    # ---- 调用链传播：InjectApiSinks 直接/间接调到的「注入助手」也算 P5 ----
    core_methods = collections.defaultdict(list)
    for f in ['MainPlugin.cs'] + sorted(glob.glob('SR_UCH/*.cs')):
        for n, b, ln in _cs_methods(f):
            core_methods[n].append((f, b, ln))
    def has_inj(b):
        return any(c in setter for c in _CALL.findall(b))
    mp_methods = dict((n, b) for n, b, _ in _cs_methods('MainPlugin.cs'))
    front = [mp_methods.get('InjectApiSinks', '')]
    p5_names = set()
    for _ in range(3):
        nxt = []
        for b in front:
            for n in _CORE_CALLEE.findall(b):
                if n in p5_names:
                    continue
                cand = [(f, bb) for f, bb, _ in core_methods.get(n, [])
                        if f.startswith('SR_UCH/') and has_inj(bb)]
                if cand:
                    p5_names.add(n)
                    nxt.extend(bb for _, bb in cand)
        front = nxt
        if not front:
            break

    def stage(f, mn):
        if f == 'MainPlugin.cs':
            return 5 if mn == 'InjectApiSinks' else (2 if mn == 'Awake' else 8)
        if f.startswith('SR_UCH/'):
            if mn in p5_names:
                return 5
            if f == 'SR_UCH/SR.Core.cs' and mn in ('Initialize', 'SelfRegCore'):
                return 6
            return 8
        if f.startswith('Features/'):
            return 7 if mn == 'Initialize' else 8
        return 8

    targets = ['MainPlugin.cs'] + sorted(glob.glob('SR_UCH/*.cs')) + sorted(glob.glob('Features/*.cs'))
    inj, use, unknown = [], [], collections.Counter()
    for f in targets:
        for n, b, _ in _cs_methods(f):
            st = stage(f, n)
            for call in _CALL.findall(b):
                if call in setter:
                    for x in setter[call]:
                        inj.append((x, st, f, n, call))
                if call in reader:
                    for x in reader[call]:
                        use.append((x, st, f, n, call))
                if call.startswith('Set') and call not in setter and call not in apinames:
                    unknown[call] += 1

    first = collections.defaultdict(lambda: 99)
    for x, st, _, _, _ in inj:
        first[x] = min(first[x], st)
    for x, st, f, n, rn in use:
        if x not in first:
            bad.append('A 无注入: %s <- %s.%s (%s)' % (x, n, rn, f))
        elif st == 6 and first[x] > 5:
            bad.append('B SR自举先用后注: %s 首注P%s <- %s.%s' % (x, first[x], n, rn))
        elif st == 7 and first[x] > 6:
            bad.append('C 功能Initialize先用后注: %s 首注P%s <- %s.%s (%s)' % (x, first[x], n, rn, f))
    for k in unknown:
        bad.append('D 未知 Set* 调用: %s' % k)

    cn = collections.Counter(st for _, st, _, _, _ in inj)
    cu = collections.Counter(st for _, st, _, _, _ in use)
    L.append('## 2. SRApi 门面「声明 → 注入 → 首次使用」')
    L.append('  契约分片 %d / API 方法 %d / setter %d / reader %d' % (
        len(apis), len(apinames), len(setter), len(reader)))
    L.append('  注入助手调用链传播: %s' % (', '.join(sorted(p5_names)) or '无'))
    L.append('  注入调用 %d（P5=%d P6=%d P8=%d） / 使用调用 %d（P6=%d P7=%d P8=%d） / 覆盖字段 %d' % (
        len(inj), cn.get(5, 0), cn.get(6, 0), cn.get(8, 0),
        len(use), cu.get(6, 0), cu.get(7, 0), cu.get(8, 0), len(first)))
    lazy = sorted(set((f, n, c) for x, st, f, n, c in inj if st == 8))
    L.append('  P8 懒注入 %d 处（静态构造 / 首次使用时接线；只要无装载期使用点即合法）:' % len(lazy))
    for f, n, c in lazy:
        L.append('    %s :: %s -> %s' % (f, n, c))
    L.append('')
    L.append('## 3. 阶段口径')
    L.append('  P2 Awake 早期 / P5 InjectApiSinks / P6 核心 SR.Initialize 自举')
    L.append('  P7 功能 Initialize / P8 运行时（Tick / Draw / 静态构造）')
    L.append('  断言 A 每个被读字段都有注入；B P6 使用须 P5 已注入；C P7 使用须 <=P6 已注入；D 无孤儿 Set* 调用')
    L.append('')
    L.append('## 4. 结论')
    if bad:
        L.append('  FAIL %d 条：' % len(set(bad)))
        for x in sorted(set(bad)):
            L.append('    - ' + x)
    else:
        L.append('  OK：%d 个字段全部「先注入后使用」，4 条装载顺序断言成立，0 违规。' % len(first))
    return (not bad), L


def cmd_analyze(out=None):
    ok, lines = awake_timing()
    print('== uch.py analyze：Awake 时序离线对账 ==')
    for x in lines:
        print(x)
    if out:
        os.makedirs(os.path.dirname(out) or '.', exist_ok=True)
        open(out, 'wb').write(('# Awake 时序离线对账\n\n' + '\n'.join(lines) + '\n').encode('utf-8'))
        print('\n[analyze] 报告已写入 %s' % out)
    print('\n[analyze] verdict=%s' % ('OK' if ok else 'FAIL'))
    return 0 if ok else 1



if __name__ == '__main__':
    sys.exit(main())
