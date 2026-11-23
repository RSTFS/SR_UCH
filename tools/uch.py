#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SR_UCH 唯一维护入口（零外部依赖，Python 3.8+）。

  uch.py verify [--strict]       统一回归：固定 CHK 表 + 单一 manifest 基线
  uch.py seal                    登记生成器产物哈希（生成器写回产物后调用）
  uch.py smoke                   一条命令冒烟：读基线 + 环境诊断 + 双轨闸门
  uch.py doctor                  环境诊断，给出明确的下一步命令
  uch.py trace-gate              双轨退役闸门（批次覆盖率）
  uch.py rules --suite <name>    机械守卫唯一实现
  uch.py precompile              第一次真编译前的离线预检
  uch.py fingerprint --dump <.cs> 重比绑定表 + 变更影响报告（无需 DLL）
  uch.py fingerprint --strict-signatures  签名形状漂移也判红
  uch.py freeze-check            与已提交基线逐字节核对
  uch.py migrate [--dry-run]     列出/核对历轮结构改动是否齐备
  uch.py docs                    生成 docs/INDEX.md 单一文档入口
  uch.py add <Name>              生成功能骨架（无 pwsh 也能跑）
  uch.py remove <Name>           列出删除残渣清单
  uch.py analyze [--out FILE]    Awake 时序离线对账（装载顺序 + 门面注入前置）

设计原则：判定逻辑只有一份（spec_engine.py 派生 / uch_rules.py 守卫），
本入口统一调度、统一报告、统一基线；不再有第二套解析或转发薄壳。
"""
import argparse, datetime, glob, hashlib, json, os, re, shutil, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import maint_ops
import uch_rules

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)
SPEC = 'build/specs/features.spec.json'
GENDIR = 'build/specs/generated'
MANIFEST = GENDIR + '/verify.manifest.json'
UCH_VERSION = 2
REQUIRED_SPEC_SCHEMA = 3
UCH_CHECKS = GENDIR + '/checks.generated.json'


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


def sha(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()


def exists(p):
    return os.path.isfile(p)


# 构建中间产物目录：不是源码，不能进任何源码统计（obj 里是自动生成的 AssemblyAttributes.cs）。
PRUNE_DIRS = {'.git', 'obj', 'bin', '__pycache__', 'node_modules', '.vs'}


def cs_files(sub):
    out = []
    for dp, dn, fn in os.walk(sub):
        dn[:] = [d for d in dn if d not in PRUNE_DIRS]
        for f in sorted(fn):
            if f.endswith('.cs'):
                out.append(os.path.join(dp, f).replace('\\', '/'))
    return sorted(out)


def run_script(rel):
    try:
        r = subprocess.run([sys.executable, rel], cwd=ROOT,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        tail = r.stdout.decode('utf-8', 'replace').strip().splitlines()
        last = tail[-1] if tail else '(no output)'
        return r.returncode == 0, 'exit=%d / %s' % (r.returncode, last[:110])
    except Exception as e:
        return False, '无法执行 %s: %s' % (rel, e)


def gen_hashes(exclude_self=True):
    out = {}
    if not os.path.isdir(GENDIR):
        return out
    for f in sorted(os.listdir(GENDIR)):
        p = GENDIR + '/' + f
        if not os.path.isfile(p):
            continue
        if exclude_self and f == 'verify.manifest.json':
            continue
        out[f] = sha(p)
    return out


def source_hashes():
    out = {}
    for d in ['SR_UCH', 'Features', 'Pure', 'tests', 'build', 'tools']:
        for dp, dn, fn in os.walk(d):
            for f in sorted(fn):
                p = os.path.join(dp, f).replace('\\', '/')
                if (f.endswith(('.cs', '.ps1', '.rsp', '.py', '.json', '.yml', '.props', '.targets'))
                        and p != MANIFEST):
                    out[p] = sha(p)
    out['MainPlugin.cs'] = sha('MainPlugin.cs')
    return out


# ---------------------------------------------------------------- 固定 CHK 表
# 每项 (id, 描述, 函数, 级别)：级别 fail 计入 FAIL；info 只报告。
# ID 与顺序稳定 —— 任何报告都引用同一套，不再出现 85/56/46/32 的口径漂移。
def _rules_quiet(action, *args):
    r = subprocess.run([sys.executable, 'tools/uch_rules.py', action] + list(args),
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return r.returncode, r.stdout.decode('utf-8', 'replace')


def C():
    s = js(SPEC)
    tc = s['toolchain']
    facts = js(GENDIR + '/facts.generated.json')
    checks = []
    # 单一引擎的纯函数（facts_block / status_doc）在生成器与守卫间共用；
    # 延迟 import 并还原 cwd：spec_engine 顶层有 os.chdir(ROOT)。
    _cwd = os.getcwd()
    try:
        import spec_engine as _SE
        SE = _SE
    finally:
        os.chdir(_cwd)

    def add(i, d, fn, level='fail'):
        checks.append((i, d, fn, level))

    # --- 环境 ---
    add('ENV-001', 'Python >= 3.8', lambda: (sys.version_info >= (3, 8), 'Python %d.%d.%d' % sys.version_info[:3]))
    add('ENV-002', 'pwsh 可用（生成器本体）',
        lambda: (shutil.which('pwsh') is not None, '本地需安装；CI 用 setup 自带'), 'info')
    add('ENV-003', 'dotnet 可用（真编译）',
        lambda: (shutil.which('dotnet') is not None, 'CI compile-gate / pure-tests 会跑'), 'info')
    add('ENV-004', 'UCH_MANAGED 已配置（全源码 IL）',
        lambda: (bool(os.environ.get('UCH_MANAGED') or os.environ.get('UCH_MANAGED_DIR')),
                 '未配置则跳过全源码 IL 编译，这是唯一硬缺口'), 'info')

    # --- 声明 ---
    add('SPEC-001', 'spec 存在且可解析',
        lambda: (exists(SPEC) and isinstance(s, dict) and
                 all(k in s for k in ['schema', 'groups', 'features', 'core_features', 'deployment', 'toolchain']) and
                 isinstance(s.get('schema'), int) and
                 isinstance(s.get('groups'), list) and isinstance(s.get('features'), list) and
                 isinstance(s.get('core_features'), list) and
                 isinstance(s.get('deployment'), dict) and isinstance(s.get('toolchain'), dict),
                 '%d 个顶层键，类型齐备' % len(s) if isinstance(s, dict) else '非对象'))
    add('SPEC-002', 'spec schema == %d' % REQUIRED_SPEC_SCHEMA,
        lambda: (s['schema'] == REQUIRED_SPEC_SCHEMA, 'schema=%s' % s['schema']))
    # 只比数量时「换掉一个 id、数量不变」不会红；这里逐集合对账：
    # 组 id ↔ 唯一源码清单的组键 / 磁盘 Features 文件，核心功能 ↔ 声明的文件。
    srcm_groups = set(js(GENDIR + '/sources.manifest.json')['groups'])
    gids = set(g['id'] for g in s['groups'])
    fids = set(f.get('id') for f in s['features'])
    core_files = [c.get('file') for c in s['core_features']]
    spec3_bad = []
    if (len(s['groups']), len(s['features']), len(s['core_features'])) != (
            facts['groups'], facts['features'], facts['core_features']):
        spec3_bad.append('数量与 facts 不符')
    if gids != srcm_groups:
        spec3_bad.append('组 id 与源码清单差 %d' % len(gids ^ srcm_groups))
    for g in gids:
        if not glob.glob('Features/' + g + '.*'):
            spec3_bad.append('组 %s 无磁盘源码' % g)
    if not all(isinstance(i, str) and i.startswith('sr.') for i in fids):
        spec3_bad.append('功能 id 形态异常')
    if not all(f and exists(f) for f in core_files):
        spec3_bad.append('核心功能声明的文件缺失')
    add('SPEC-003', '组/功能/核心功能逐集合与磁盘一致',
        lambda: (not spec3_bad, ', '.join(spec3_bad) or
                 '%d 组 / %d 功能 / %d 核心' % (len(gids), len(fids), len(core_files))))
    ids = [f.get('id') for f in s['features']]
    add('SPEC-004', 'feature id 唯一', lambda: (len(ids) == len(set(ids)), '%d 个 id' % len(ids)))
    need = ['entry', 'ci_entry_command', 'rsp_scope', 'api_level_scope', 'required_generated',
            'legacy_scripts', 'migrations', 'trace_gate', 'generated_policy']
    spec5_bad = []
    for k in need:
        if k not in tc:
            spec5_bad.append(k + ' 缺')
        elif tc[k] in (None, '', [], {}):
            spec5_bad.append(k + ' 为空')
    add('SPEC-005', 'toolchain 段完整且非空',
        lambda: (not spec5_bad, ', '.join(spec5_bad) or
                 '%d/%d 键' % (len(need) - len(spec5_bad), len(need))))

    # --- 元层冻结 ---
    def _all_rules():
        rc, out = _rules_quiet('run', '--suite', 'all', '--json')
        try:
            j = json.loads(out)
            return (rc == 0 and j['failed'] == 0 and j['total'] > 0,
                    '%d 项 / %d FAIL' % (j['total'], j['failed']))
        except Exception as e:
            return (False, '规则输出不可解析: %s' % e)
    add('META-001', '机械守卫唯一实现全绿', _all_rules)

    # --- 派生产物 ---
    add('GEN-001', 'generated/ 目录存在', lambda: (os.path.isdir(GENDIR), GENDIR))
    miss = [x for x in tc['required_generated'] if not exists(GENDIR + '/' + x)]
    gen_disk = set(f for f in os.listdir(GENDIR) if os.path.isfile(GENDIR + '/' + f))
    gen_req = set(tc['required_generated'])
    def _gen002():
        # 原实现只读 spec 自己声明的清单：把某项从 required_generated 删掉，守卫就跟着放宽。
        # 这里要求声明集合 == 磁盘实际生成物集合（除 verify.manifest.json），双向卡住。
        bad = []
        if miss: bad.append('缺 ' + ', '.join(miss))
        extra = gen_disk - gen_req - {'verify.manifest.json'}
        if extra: bad.append('声明外: ' + ', '.join(sorted(extra)))
        orphan = gen_req - gen_disk
        if orphan: bad.append('声明了但不在磁盘: ' + ', '.join(sorted(orphan)))
        return (not bad, ', '.join(bad) or '%d 项与磁盘集合一致' % len(gen_req))
    add('GEN-002', '必需生成物齐备且与声明互卡', _gen002)
    badjson = []
    for f in sorted(os.listdir(GENDIR)):
        if f.endswith('.json'):
            try:
                js(GENDIR + '/' + f)
            except Exception as e:
                badjson.append(f)
    add('GEN-003', '生成物 JSON 全部可解析', lambda: (not badjson, ', '.join(badjson) or '全部 OK'))
    sealed = exists(MANIFEST) and bool(js(MANIFEST).get('generator_seal'))
    def _gen004():
        # 原实现：未 seal 直接返回 (True, ...) 且降级为 info —— 删掉 seal 字段就永远不再校验哈希。
        # fresh clone 自举会先 materialize（内含 seal），所以「未 seal」只可能是真缺件。
        if not sealed:
            return False, '未 seal（manifest 缺失或 generator_seal 为空）：跑 uch.py gen'
        h = js(MANIFEST)['generator_seal']['hashes']
        cur = gen_hashes()
        return (h == cur, '一致' if h == cur else '哈希漂移：跑 uch.py gen')
    add('GEN-004', '生成器产物哈希与 seal 一致', _gen004)
    srcm = js(GENDIR + '/sources.manifest.json')
    srclist = [x.replace('\\', '/') for x in srcm['compile']]
    miss_src = [x for x in srclist if not exists(x)]
    disk_cs = set(p.replace('\\', '/') for p in glob.glob('**/*.cs', recursive=True)
                  if not any(x in p for x in ('/bin/', '/obj/', '.cache')))
    want_cs = disk_cs - set(cs_files('tests'))  # tests/ 4 个桩不在主链编译清单
    def _gen005():
        # 原实现只查清单里的路径存在：清单漏写一个 .cs、或多写一个无关路径都不发现。
        bad = []
        if miss_src: bad.append('缺 %d 条' % len(miss_src))
        if set(srclist) != want_cs:
            d = set(srclist) ^ want_cs
            bad.append('清单与磁盘源码差 %d：%s' % (len(d), ', '.join(sorted(d)[:3])))
        return (not bad, ', '.join(bad) or '%d 条与磁盘源码集合一致' % len(srclist))
    add('GEN-005', '唯一源码清单与磁盘源码集合一致', _gen005)

    # --- 构建清单 ---
    rspgroups = [f for f in os.listdir('build/rsp') if f.startswith('feat_') and f.endswith('.rsp')]
    add('RSP-001', '单链构建：无 feat_*.rsp / 无 split 残留',
        lambda: (not rspgroups and not exists('build/rsp/sr_uch_core.rsp')
                 and not exists('build/rsp/sr_uch_contracts.rsp'),
                 'feat=%d / core=%s' % (len(rspgroups), exists('build/rsp/sr_uch_core.rsp'))))
    rspbad = []
    rsp_all = glob.glob('build/rsp/**/*.rsp', recursive=True)
    for p in rsp_all:
        b = open(p, 'rb').read()
        if not b.startswith(b'\xef\xbb\xbf') or b'\r\n' in b:
            rspbad.append(p)
    extra_rsp = [p for p in rsp_all if p.replace('\\', '/') != 'build/rsp/sr_uch.rsp']
    add('RSP-002', '全部 .rsp 为 BOM + LF 且单链只有 sr_uch.rsp',
        lambda: (not rspbad and not extra_rsp,
                 ', '.join(rspbad + [x + ' (多余)' for x in extra_rsp]) or '全部合规'))
    main_rsp = rd('build/rsp/sr_uch.rsp')
    n_src = len([l for l in main_rsp.splitlines() if re.search(r'\.cs"?\s*$', l)])
    add('RSP-003', 'sr_uch.rsp 源码数与 facts 一致',
        lambda: (n_src == facts['rsp_sources'], '%d / %d' % (n_src, facts['rsp_sources'])))
    rsp_scope = tc['rsp_scope']
    comp = set(x.replace('\\', '/') for x in js(GENDIR + '/sources.manifest.json')['compile'])
    def _inscope(f):
        for pat in rsp_scope['checked']:
            if pat == f:
                return True
            if '*' in pat:
                a, b = pat.split('*', 1)
                if f.startswith(a) and f.endswith(b):
                    return True
        return False
    rsp_ref_miss, rsp_checked = [], 0
    for f in sorted(os.listdir('build/rsp')):
        if not f.endswith('.rsp') or not _inscope(f):
            continue  # 外部插件清单按声明豁免
        for l in rd('build/rsp/' + f).splitlines():
            t = l.strip()
            if t.startswith('/'):
                continue
            m = re.search(r'([A-Za-z0-9_./\\-]+\.cs)"?\s*$', t)
            if m:
                rsp_checked += 1
                p = m.group(1).replace('\\', '/')
                if p not in comp:
                    rsp_ref_miss.append('%s:%s' % (f, p))
    add('RSP-004', '主链 rsp 源码行全部命中唯一源码清单',
        lambda: (not rsp_ref_miss and rsp_checked > 0,
                 '%d 行核对 / %d 条漂移（单链，外部清单 0 份）' %
                 (rsp_checked, len(rsp_ref_miss))))

    # --- 源码 ---
    allcs = cs_files('SR_UCH') + cs_files('Features') + cs_files('Pure') + cs_files('tests') + ['MainPlugin.cs']
    enc_bad = []
    for p in allcs:
        b = open(p, 'rb').read()
        if b.startswith(b'\xef\xbb\xbf') or b'\r\n' in b:
            enc_bad.append(p)
    add('SRC-001', '.cs 编码合规（无 BOM / LF）',
        lambda: (not enc_bad, '%d 个文件，%d 异常' % (len(allcs), len(enc_bad))))

    # --- 棘轮（口径来自 spec，不再各自硬编码）---
    scope = tc['api_level_scope']
    ex = tuple(scope['exclude_dirs'])
    afiles = [p for p in cs_files(scope['root']) if not any(x in p for x in ex)]
    am = re.search(r'public\s+const\s+int\s+ApiLevel\s*=\s*(\d+)', rd('SR_UCH/Contracts/Contracts.cs'))
    a_level, a_members = int(am.group(1)), sum(
        1 for p in afiles for l in rd(p).splitlines() if re.match(r'^\s*public\s', l))
    rat = js(GENDIR + '/ratchets.json')
    add('RATCHET-001', 'api_level 从声明口径派生且与 ratchets 一致',
        lambda: ((rat['api_level']['ApiLevel'], rat['api_level']['members']) == (a_level, a_members),
                 'ApiLevel=%d / public=%d（排除 Generated/）' % (a_level, a_members)))
    pat = re.compile(r'catch\s*(\([^)]*\))?\s*\{\s*(\}|/\*\s*静默)')
    sc_total = 0
    for d in ['SR_UCH', 'Features', 'Pure']:
        for p in cs_files(d):
            if '/tests/' in p:
                continue
            sc_total += len(pat.findall(rd(p)))
    sc_total += len(pat.findall(rd('MainPlugin.cs')))
    add('RATCHET-002', '静默 catch 棘轮一致',
        lambda: (sc_total == rat['silent_catch_total'], '%d / %d' % (sc_total, rat['silent_catch_total'])))

    # --- 工具链收敛 ---
    add('TOOL-001', '权威脚本齐备',
        lambda: (all(exists(x) for x in tc['legacy_scripts'] + ['tools/uch.py']),
                 '%d 个' % (len(tc['legacy_scripts']) + 1)))
    wf = rd('.github/workflows/guards.yml')
    ci_calls = re.findall(r'python3\s+tools/([A-Za-z0-9_.\-/]+)', wf)
    def _tool002():
        # 原实现只查两个子串：新加一条 python3 tools/xxx.py 旁路不会被发现。
        bad = [c for c in ci_calls if c != 'uch.py']
        if 'python3 tools/uch.py verify' not in wf:
            bad.append('缺统一回归入口')
        return (not bad, ', '.join(bad) or '%d 次调用全部经 uch.py' % len(ci_calls))
    add('TOOL-002', 'CI 每条 Python 调用都经 uch.py', _tool002)
    eng_src = rd('tools/spec_engine.py')
    eng_has_seal = bool(re.search(
        r"subprocess\.run\(\[sys\.executable,\s*'tools/uch\.py',\s*'seal'\]", eng_src))
    add('TOOL-003', '单一引擎 generate 末尾会 seal',
        lambda: (eng_has_seal, '精确 seal 调用' if eng_has_seal else '引擎未真正调用 uch.py seal'))

    # --- 权威实现统一调度 ---
    add('ARCH-001', '源码分层与依赖方向', lambda: run_script('tools/check_architecture.py'))
    add('UI-001', '声明式页面模型对账', lambda: run_script('tools/check_uimodel.py'))
    add('SINGLE-001', '单产物声明与部署一致',
        lambda: (s['deployment']['mode'] == 'single' and s['deployment']['dll'] == s['artifacts']['dll'],
                 '%s / %s' % (s['deployment']['mode'], s['deployment']['dll'])))

    # --- 页面与双轨 ---
    pages = js('build/specs/ui.pages.json')['pages']
    uim = js(GENDIR + '/ui-model.generated.json')
    page_ids = set(p['id'] for p in pages)
    trace_ids = set(uim['traces'])
    def _ui002():
        # 原实现只比 29 这个数：换掉一页 id、数量不变就不红。
        bad = []
        if len(pages) != uim['page_count'] or uim['page_count'] != 29:
            bad.append('数量 %d/%d' % (len(pages), uim['page_count']))
        if page_ids != trace_ids:
            bad.append('页 id 与 traces 差 %d' % len(page_ids ^ trace_ids))
        return (not bad, ', '.join(bad) or '29 页 id 与 traces 集合一致')
    add('UI-002', '29 页 id 逐集合与生成物一致', _ui002)
    add('UI-003', 'trace-gate 状态可读',
        lambda: (isinstance(uim.get('traces'), (list, dict)), 'traces 字段存在'), 'info')

    # --- 文档 / 健康 / 契约 ---
    idx = 'docs/INDEX.md'
    def _doc001():
        # 原实现只查标记在不在；标记留着、正文被改空照样通过。改为全文逐字比对。
        if not exists(idx):
            return False, 'INDEX.md 缺失：跑 uch.py docs'
        bad = []
        if rd(idx) != docs_text():
            bad.append('内容与 uch.py docs 派生不一致')
        mds = sorted(f for f in os.listdir('docs') if f.endswith('.md'))
        if mds != ['INDEX.md', 'STATUS.md']:
            bad.append('文档集合异常: %s' % mds)
        return (not bad, ', '.join(bad) or '全文逐字一致 / 2 份文档')
    add('DOC-001', 'docs/INDEX.md 单一入口全文与派生一致', _doc001)
    def _doc002():
        # 原实现只查 5 个数值子串出现；命令段、其余数值漂移不发现。改为整份逐字比对。
        p = 'docs/STATUS.md'
        if not exists(p):
            return False, 'STATUS.md 缺失：跑 uch.py gen'
        cur, want = rd(p), SE.status_doc(facts)
        return (cur == want, '整份逐字一致' if cur == want else '整份漂移：跑 uch.py gen')
    add('DOC-002', 'STATUS.md 整份与 facts 派生逐字一致', _doc002)
    rmbad = []
    # 区块文本只由 spec_engine.facts_block 构造（C() 开头已缓存 SE），守卫不另拼口径。
    expect_block = SE.facts_block(facts)
    end_tag = '<!-- generated:facts:end -->'
    for f in ['README.md', 'README.zh-CN.md']:
        t = rd(f)
        b = t.find('<!-- generated:facts:begin')
        e = t.find(end_tag)
        if b < 0 or e <= b:
            rmbad.append(f + ' 缺生成标记')
        elif t[b:e + len(end_tag)] != expect_block:
            rmbad.append(f + ' 区块与 facts 派生值不一致')
    add('DOC-003', '中英 README 生成区块与 facts 一致',
        lambda: (not rmbad, ', '.join(rmbad) or '两份'))
    hs = js(GENDIR + '/health.schema.json')
    hsrc = rd('SR_UCH/Contracts/HealthSnapshot.cs')
    hm = re.search(r'Fields\s*=\s*new\s+string\[\]\s*\{(.*?)\}', hsrc, re.S)
    cs_fields = re.findall(r'"([a-z_]+)"', hm.group(1)) if hm else []
    def _health001():
        # 原实现只比字段数：字段名改了、数量不变就不红。与 C# 里的权威声明逐集合对账。
        bad = []
        if len(hs['fields']) != facts['health_fields']:
            bad.append('数量与 facts 不符')
        if not cs_fields or set(hs['fields']) != set(cs_fields) or len(hs['fields']) != len(cs_fields):
            bad.append('字段名与 HealthSchema.Fields 不一致')
        if hs.get('version') != 1:
            bad.append('version 非 1')
        return (not bad, ', '.join(bad) or '%d 字段名与 C# 声明一致' % len(cs_fields))
    add('HEALTH-001', '健康 schema 字段名逐集合与 C# 一致', _health001)
    exp = js(GENDIR + '/selftest.expected.json')
    all_text = '\n'.join(rd(p) for p in cs_files('SR_UCH') + cs_files('Features') + ['MainPlugin.cs'])
    ref_keys = set(re.findall(r'Ref\.Override(?:<[^>]*>)?\s*\(\s*"([^"]+)"', all_text))
    key_digest = hashlib.sha256('\n'.join(sorted(ref_keys)).encode('utf-8')).hexdigest()
    def _contract001():
        # 原实现只比三个计数：换掉一个 key、补一个同数量假 key，数量不变就不红。
        bad = []
        if (exp['reflection_unique_keys'], exp['features_declared'], exp['groups_declared']) != (
                len(ref_keys), len(s['features']), len(s['groups'])):
            bad.append('计数漂移')
        if exp.get('reflection_key_digest') != key_digest:
            bad.append('反射 key 集合漂移')
        return (not bad, ', '.join(bad) or '%d 个 key，集合摘要一致' % len(ref_keys))
    add('CONTRACT-001', '自检期望与源码逐集合一致', _contract001)
    # 判定逻辑只有一份：直接复用 uch_rules 的 CTR-001 实现，不再另写一份 verdict 子串检查。
    add('CONTRACT-002', '契约绑定表结构完整且离线比对 OK',
        lambda: uch_rules.contract_fingerprint())
    net = rd('SR_UCH/Contracts/Generated/NetMessages.g.cs')
    add('NET-001', '网络消息零裸号', lambda: (not re.search(r'=\s*\d{3,}', net), 'NetMsgId 常量'))
    add('CFG-001', 'cfg schema 门 == 3', lambda: (s['cfg']['schema'] == 3, 'schema=%s' % s['cfg']['schema']))

    # --- CI ---
    wj = wf[wf.find('\njobs:'):] if '\njobs:' in wf else wf
    jobs = re.findall(r'^  ([a-z][a-z0-9_-]*):\s*$', wj, re.M)
    bg = rd('SR_UCH/Contracts/Generated/SrBuildGraph.g.cs')
    bgm = re.search(r'CiJobCount = (\d+)', bg)
    def _ci001():
        # 原实现只比数量：把入口 job 改名、数量不变就不红，而 CI 入口会真的消失。
        bad = []
        if len(jobs) != facts['ci_jobs']:
            bad.append('数量 %d/%d' % (len(jobs), facts['ci_jobs']))
        if s['ci']['job'] not in jobs:
            bad.append('入口 job %s 不在' % s['ci']['job'])
        if len(set(jobs)) != len(jobs):
            bad.append('job 名重复')
        if not bgm or int(bgm.group(1)) != len(jobs):
            bad.append('CiJobCount 与 workflow 不符')
        return (not bad, ', '.join(bad) or '%d 个 job，入口/常量互卡' % len(jobs))
    add('CI-001', 'CI job 集合与入口/生成常量互卡', _ci001)
    def _ci002():
        # 原实现 wf.count('steps:') >= len(jobs)：某个 job 完全没 steps、别处多写一行仍能凑够。
        parts = re.split(r'^  ([a-z][a-z0-9_-]*):\s*$', wj, flags=re.M)
        bad = []
        seen = 0
        for i in range(1, len(parts) - 1, 2):
            name, body = parts[i], parts[i + 1]
            seen += 1
            # 必须整行匹配：'#steps:' / 注释里提到 steps: 都不算真有 steps 段。
            if not re.search(r'^\s+steps:\s*$', body, re.M):
                bad.append(name + ' 无 steps')
        if seen != len(jobs):
            bad.append('分块数 %d != job 数 %d' % (seen, len(jobs)))
        return (not bad, ', '.join(bad) or '%d 个 job 均有 steps' % seen)
    add('CI-002', '逐 job 块确认 steps 段在位', _ci002)

    # --- 本轮：单一引擎 / 错误模型 / 双轨 / 路由 / 重放 ---
    add('ENGINE-001', '单一声明引擎可跑且派生自洽', _engine_check)
    add('ENGINE-002', '判定逻辑只有一份实现（无第二套解析）',
        lambda: (not any(x in rd('build/build.ps1') + rd('build/run-tests.ps1')
                         for x in ['function Sync-', 'Get-Ratchet', 'WriteBaseline'])
                 and exists('tools/spec_engine.py') and exists('tools/uch_rules.py'),
                 'build/ 仅 2 个实脚本，判定全在 Python'))
    dupe_bad = _dupes()
    add('DUPE-001', '无重复注入块（历轮脚本累加垃圾已清）',
        lambda: (not dupe_bad, ', '.join(dupe_bad) or 'pwsh / CI / 生成器均干净'))
    chk_owners = js(UCH_CHECKS) if exists(UCH_CHECKS) else {'checks': {}}
    add('IMPACT-001', '固定 CHK 与影响路由表一一对应',
        lambda: (set(chk_owners['checks']) == set(i for i, d, f, l in checks) and
                 len(chk_owners['checks']) == len(checks),
                 '%d 项' % len(chk_owners['checks'])))
    es = js(GENDIR + '/error-sites.generated.json') if exists(GENDIR + '/error-sites.generated.json') else {'count': -1}
    es_sites = es.get('sites', [])
    gcs = rd('SR_UCH/Contracts/Generated/SrCatchSites.g.cs') if exists(
        'SR_UCH/Contracts/Generated/SrCatchSites.g.cs') else ''
    gcodes = set(re.findall(r'"(SR-H-[A-Z0-9_-]+)"', gcs))
    def _error001():
        # 原实现只比 count + 文件在不在：站点内容换成假数据、count 凑对就不红。
        bad = []
        if es['count'] != js(GENDIR + '/ratchets.json')['silent_catch_total']:
            bad.append('数量与棘轮不符')
        if len(es_sites) != es['count']:
            bad.append('sites 长度 %d != count %d' % (len(es_sites), es['count']))
        need_k = ['site', 'owner', 'domain', 'kind', 'code', 'severity', 'action']
        if any(any(k not in x for k in need_k) for x in es_sites):
            bad.append('站点字段缺失')
        if any(not exists(x['site'].split(':')[0]) for x in es_sites if 'site' in x):
            bad.append('站点指向的文件不存在')
        scodes = set(x['code'] for x in es_sites if 'code' in x)
        if scodes != gcodes:
            bad.append('生成物 code 集合与站点差 %d' % len(scodes ^ gcodes))
        if not exists('SR_UCH/Contracts/SrError.cs'):
            bad.append('SrError.cs 缺失')
        return (not bad, ', '.join(bad) or '%d 站点结构/文件/code 集合齐备' % len(es_sites))
    add('ERROR-001', '结构化错误站点逐条与生成物一致', _error001)
    sh = js(GENDIR + '/ui-shadow.generated.json') if exists(GENDIR + '/ui-shadow.generated.json') else {}
    add('SHADOW-001', '双轨闸门：声明与实机证据分离',
        lambda: (sh.get('declaration_ready') is True and sh.get('allowed') ==
                 (bool(sh.get('cutover_requested')) and bool(sh.get('runtime_ab_evidence'))),
                 'cutover=%s / ab=%s' % (sh.get('allowed'), sh.get('runtime_ab_evidence'))))
    add('RUNTIME-001', '组合根关机顺序可注入替身单测',
        lambda: (all(x in rd('SR_UCH/Contracts/SrRuntime.cs') for x in
                     ['Configure(ISrServices', 'RuntimeRules.ShutdownOrder', 'SrErrorSink.Pending']) and
                 'SrErrorSink.Flush(s)' in rd('SR_UCH/SR.Health.cs') and
                 'RuntimeRules.OrderIsValid' in rd('tests/PureTests.cs'),
                 '4 域 + 错误合流 + 纯函数断言'))
    add('CODEMOD-001', '结构规则可重放且当前齐备',
        lambda: (maint_ops.status(), '10 条幂等规则全 OK' if maint_ops.status() else '有待重放项'))
    smoke = js(GENDIR + '/smoke.generated.json') if exists(GENDIR + '/smoke.generated.json') else {'steps': []}
    def _smoke001():
        # 原实现只查 steps>=7 + golden 文件存在：步骤换成 7 条无关项、golden 清空都不发现。
        bad = []
        ids = [x.get('id') for x in smoke['steps'] if isinstance(x, dict)]
        if ids != ['S1', 'S2', 'S3', 'S4', 'S5', 'S6', 'S7']:
            bad.append('步骤序列异常: %s' % ids)
        gp = GENDIR + '/health.golden.json'
        if not exists(gp):
            bad.append('golden 缺失')
        else:
            g = js(gp)
            if not g.get('expect') or not isinstance(g['expect'], dict):
                bad.append('golden expect 为空')
            elif set(g['expect']) != {'features_declared', 'groups_declared',
                                      'reflection_unique_keys', 'contract'}:
                bad.append('golden expect 键集合漂移')
        return (not bad, ', '.join(bad) or '7 步序列 + golden expect 齐备')
    add('SMOKE-001', '全链路 smoke 步骤序列与 golden 齐备', _smoke001)

    # --- 退役 / 自洽 ---
    stale = [f for f in tc['retired_baselines'] if exists('build/' + f)]
    add('LEGACY-001', '手填基线全部退役',
        lambda: (not stale, ', '.join(stale) or '%d 项已清' % len(tc['retired_baselines'])))
    def _manifest001():
        # 原实现：manifest 不存在直接 okm=True 放行。首次运行确实会生成，
        # 但「检查自身」不该对缺失无条件点头；自举发生在 main() 之前，此处必已有文件。
        if not exists(MANIFEST):
            return False, 'manifest 缺失：跑 uch.py gen'
        m = js(MANIFEST)
        bad = []
        if m.get('schema') != UCH_VERSION: bad.append('schema')
        if m.get('spec_schema') != REQUIRED_SPEC_SCHEMA: bad.append('spec_schema')
        if not (m.get('generator_seal') or {}).get('hashes'): bad.append('generator_seal 空')
        if m.get('generated_hashes') != gen_hashes(): bad.append('generated_hashes 漂移')
        csx = m.get('checks') or []
        if csx:  # 空壳是自举中间态，合法；非空时必须与本表 ID 集合一致
            ids = set(x['id'] for x in csx if isinstance(x, dict) and 'id' in x)
            if ids != set(i for i, d, f, l in checks):
                bad.append('checks ID 集合与本表不符')
        return (not bad, ', '.join(bad) or 'schema / seal / 生成物哈希 / CHK ID 集合齐备')
    add('MANIFEST-001', 'verify.manifest.json 自洽', _manifest001)
    add('CATCH-001', '静默 catch 可见性（诊断）',
        lambda: (True, '%d 处；事件码见 health.schema.json event_codes' % sc_total), 'info')
    return checks, s, facts



def _engine_check():
    r = subprocess.run([sys.executable, 'tools/spec_engine.py', 'derive'], cwd=ROOT,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
    out = r.stdout.decode('utf-8', 'replace') if r.stdout else ''
    if r.returncode != 0:
        return False, 'derive 退出码 %d / %s' % (r.returncode, out.strip()[-90:])
    # 原实现只看退出码：引擎算错了但只要没崩就「自洽」。逐项对账派生的 8 个数。
    nums = dict(re.findall(r'(\w+)=(\d+)', out))
    f = js(GENDIR + '/facts.generated.json')
    pairs = {'groups': f['groups'], 'features': f['features'], 'core': f['core_features'],
             'compile': f['rsp_sources'], 'pure': f['pure_files'], 'ci_jobs': f['ci_jobs'],
             'ref_unique': f['reflection_unique'], 'health_fields': f['health_fields']}
    bad = ['%s=%s/%s' % (k, nums.get(k), v) for k, v in pairs.items()
           if nums.get(k) != str(v)]
    return (not bad, '派生 %d 项与 facts 一致' % len(pairs) if not bad else '漂移: ' + ' '.join(bad))


def _dupes():
    bad = []
    for p in ['.github/workflows/guards.yml', 'tools/spec_engine.py', 'tools/uch.py']:
        if not exists(p):
            continue
        seen = {}
        for l in rd(p).splitlines():
            k = l.strip()
            if len(k) > 25:
                seen[k] = seen.get(k, 0) + 1
        if any(v > 3 for k, v in seen.items()
               if 'doctor' in k or 'Get-Command python3' in k or '统一维护入口' in k or 'uch.py seal' in k):
            bad.append(p)
    return bad


def cmd_verify(strict=False, as_json=False, fast=False, changed=None, nowrite=False):
    checks, s, facts = C()
    sel = None
    if fast or changed:
        paths = list(changed or [])
        if fast and exists(MANIFEST):
            base = js(MANIFEST).get('source_hashes', {})
            paths += [p for p, h in base.items() if not exists(p) or sha(p) != h]
        routed = set()
        for p in paths:
            for cid, why in maint_ops.route(p):
                routed.add(cid)
        for cid, meta in maint_ops.IMPACT.items():
            if '#' in meta['owners']:
                routed.add(cid)
        sel = routed
        print('[fast] 变更 %d 个文件 → 路由命中 %d / %d 项 CHK（含全局项）' %
              (len(set(paths)), len(sel), len(checks)))
    owners_map = js(UCH_CHECKS)['checks'] if exists(UCH_CHECKS) else {}
    results, nfail, ninfo = [], 0, 0
    for i, d, fn, level in checks:
        if sel is not None and i not in sel:
            continue
        try:
            ok, detail = fn()
        except Exception as e:
            ok, detail = False, '检查自身异常: %s' % e
        status = 'ok' if ok else ('info' if level == 'info' else 'fail')
        if status == 'fail':
            nfail += 1
        if status == 'info':
            ninfo += 1
        results.append({'id': i, 'desc': d, 'status': status, 'detail': detail,
                       'owners': owners_map.get(i, {}).get('owners', [])})
        if not as_json:
            print('[%s] %-9s %-34s %s' % (
                {'ok': ' OK ', 'info': 'INFO', 'fail': 'FAIL'}[status], i, d, detail))
    if nowrite:
        print('\n== UCH verify (fast)  PASS %d / FAIL %d / INFO %d ==' %
              (len(results) - nfail - ninfo, nfail, ninfo))
        return 1 if nfail else 0
    mf = {
        'schema': UCH_VERSION, 'tool': 'tools/uch.py', 'spec_schema': s['schema'],
        'checks': results, 'counts': {'total': len(results), 'ok': len(results) - nfail - ninfo,
                                      'fail': nfail, 'info': ninfo},
        'generated_hashes': gen_hashes(), 'source_hashes': source_hashes(),
        'generator_seal': (js(MANIFEST).get('generator_seal') if exists(MANIFEST) else None),
        'engine': 'tools/spec_engine.py',
        'impact': UCH_CHECKS,
        'next': 'uch.py doctor'
    }
    wr(MANIFEST, json.dumps(mf, ensure_ascii=False, indent=2) + '\n')
    if not as_json:
        print('\n== UCH verify  PASS %d / FAIL %d / INFO %d  (共 %d 项) ==' %
              (len(results) - nfail - ninfo, nfail, ninfo, len(results)))
        # 基线路径与后续建议是噪音：只有失败时才提示怎么查。
        if nfail:
            print('基线: ' + MANIFEST)
            print('下一步: uch.py doctor')
    else:
        print(json.dumps(mf, ensure_ascii=False))
    return 1 if (nfail and (strict or True)) else 0


def cmd_seal():
    h = gen_hashes()
    mf = js(MANIFEST) if exists(MANIFEST) else {
        'schema': UCH_VERSION, 'tool': 'tools/uch.py', 'spec_schema': js(SPEC)['schema'],
        'checks': [], 'counts': {}, 'generated_hashes': {}, 'source_hashes': {}, 'next': ''}
    mf['schema'] = UCH_VERSION
    mf['generator_seal'] = {'sealed_by': 'tools/spec_engine.py generate', 'count': len(h), 'hashes': h}
    mf['generated_hashes'] = h
    mf['spec_schema'] = js(SPEC)['schema']
    wr(MANIFEST, json.dumps(mf, ensure_ascii=False, indent=2) + '\n')
    print('[seal] %d 个生成物哈希已写入 %s' % (len(h), MANIFEST))
    return 0


def cmd_trace_gate(enforce=False):
    uim = js(GENDIR + '/ui-model.generated.json')
    pages = js('build/specs/ui.pages.json')['pages']
    tc = js(SPEC)['toolchain']['trace_gate']
    traces = uim.get('traces', {})
    cov = {}
    for b in ['1', '2', '3']:
        batch = [p for p in pages if str(p.get('batch')) == b]
        traced = [p for p in batch if isinstance(traces, dict) and p['id'] in traces]
        cov[b] = (len(traced), len(batch))
    print('== 双轨退役闸门（target batch %s / %s） ==' % (tc['target_batch'], tc['mode']))
    for b, (t, n) in cov.items():
        print('  批 %s: %d/%d 页有 trace  %s' % (b, t, n, 'READY' if n and t == n else 'BLOCKED'))
    t, n = cov[str(tc['target_batch'])]
    ready = n > 0 and t == n
    print('结论: %s' % ('声明层 READY（三批 trace 齐备）' if ready else '声明层 BLOCKED'))
    sh = js(GENDIR + '/ui-shadow.generated.json') if exists(GENDIR + '/ui-shadow.generated.json') else {}
    ab = bool(sh.get('runtime_ab_evidence'))
    want = bool(sh.get('cutover_requested'))
    allowed = bool(sh.get('allowed'))
    print('运行时证据: %s / cutover 请求: %s / 允许: %s' %
          ('PASS' if ab else 'MISSING', 'YES' if want else 'no', 'YES' if allowed else 'NO'))
    print('注意: 声明齐备只说明节点树完整；移除旧 IMGUI 必须同时有实机 A/B 证据。')
    if enforce and want and not allowed:
        print('结论: BLOCKED —— cutover_requested=true 但实机 A/B 证据缺失，禁止合入。')
        return 1
    if enforce and not want:
        print('结论: 声明层 READY / 未请求 cutover，旧绘制路径保持（CI 不红）。')
        return 0
    return 0 if (ready or tc['mode'] != 'enforce') else 1


def _dump_present(e, types, members, text):
    """在反编译源码里查一个 Type.Member 是否还在。宽松哨兵：宁可不报，不造假红。"""
    typ = (e.get('type') or '').split('`')[0]
    mem = e.get('member') or ''
    if not mem:
        return True, ''
    alias = re.sub(r'^(?:get_|set_)', '', mem)
    type_ok = (not typ) or typ in types \
        or re.search(r'\b(?:class|struct|interface|enum)\s+' + re.escape(typ) + r'\b', text) \
        or (typ + '.') in text
    mem_ok = mem in members or alias in members \
        or re.search(r'\b' + re.escape(mem) + r'\b', text) \
        or re.search(r'\b' + re.escape(alias) + r'\b', text)
    if not type_ok:
        return False, 'type missing: ' + typ
    if not mem_ok:
        return False, 'member missing: ' + mem
    return True, ''


def _strip_code_line(line):
    """去掉行注释与字符串字面量，避免括号计数被字符串/注释里的 { } 带偏。"""
    s = line
    # 去掉 // 之后
    s = re.sub(r'//.*$', '', s)
    # 去掉字符串字面量（含 @"" 与逐字字符串，简化处理即可）
    s = re.sub(r'@?"(?:\\.|[^"\\])*"', '""', s)
    # 去掉字符字面量
    s = re.sub(r"'(?:\\.|[^'\\])*'", "''", s)
    return s


def _dump_bindings(text):
    """一次扫描反编译源码：用大括号作用域栈精确归属 (Type.Member) 声明。

    返回 (exact, by_name, parents)：
      exact['Type.Member'] = {return_type, static, access, params, kind,
                              decl_line, raw, shape}
      by_name['Member']    = [同名 exact key]（最后的回退依据）
      parents['Type']      = [直接基类名]（继承链校准用）
    形状 shape 是可比较的稳定字符串；按名回退只在作用域栈无法归属时使用。
    """
    lines = text.splitlines()
    type_re = re.compile(
        r'^(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|'
        r'abstract|partial|unsafe|readonly|new|global)\s+)*'
        r'(?:class|struct|interface|enum)\s+([A-Za-z_]\w*)\s*'
        r'(?::\s*(?P<bases>[^{\n]+))?')
    mods_re = re.compile(
        r'^(?:(?:public|private|protected|internal|static|virtual|override|abstract|'
        r'sealed|extern|unsafe|new|partial|async|readonly|const|event|global)\s+)+')
    stack = []          # (depth_at_open, typename)
    parents = {}
    exact = {}
    depth = 0
    pending = None

    for i, raw in enumerate(lines, 1):
        st = raw.strip()
        code = _strip_code_line(raw).strip()

        # 1) 类型声明（可能与其 { 分行）
        tm = type_re.match(st) if st and not st.startswith('//') else None
        if tm:
            tn = tm.group(1)
            pending = tn
            bases = tm.group('bases') or ''
            ps = []
            for b in bases.split(','):
                b = b.strip()
                if not b:
                    continue
                # 去掉泛型实参，只留基类名
                b = re.sub(r'<.*>', '', b).strip()
                if re.match(r'^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$', b):
                    ps.append(b.split('.')[-1])
            parents.setdefault(tn, [])
            for x in ps:
                if x not in parents[tn]:
                    parents[tn].append(x)

        # 2) 成员声明：先切声明头，再取最后一个标识符为成员名
        if (st and not st.startswith('//') and not st.startswith('*')
                and len(st) <= 400 and not tm and stack):
            head = re.split(r'[(=;{]', st, 1)[0].strip()
            mm = mods_re.match(head)
            mods = mm.group(0) if mm else ''
            rest_head = head[len(mods):].strip()
            toks = rest_head.split()
            if len(toks) >= 2:
                name = toks[-1]
                ret = ' '.join(toks[:-1]) or toks[0]
                if re.match(r'^[A-Za-z_]\w*$', name):
                    owner = stack[-1][1]
                    access = ('public' if 'public' in mods else
                              'private' if 'private' in mods else
                              'protected' if 'protected' in mods else 'internal')
                    is_static = 'static' in mods
                    pm = re.search(r'\(([^)]*)\)', st)
                    params = []
                    if pm:
                        for pp in pm.group(1).split(','):
                            pp = pp.strip()
                            if not pp:
                                continue
                            toks2 = [t for t in pp.split()
                                     if t not in ('ref', 'out', 'in', 'params', 'this')]
                            params.append(toks2[0] if toks2 else pp)
                    tail = st[len(head):]
                    if tail.lstrip().startswith('('):
                        kind = 'method'
                    elif ('get;' in tail or 'set;' in tail or
                          tail.lstrip().startswith('{') or tail.lstrip().startswith('=>')):
                        kind = 'property'
                    else:
                        kind = 'field'
                    shape = '%s|%s|%s|%s|(%s)|%s' % (
                        access, 'static' if is_static else 'instance',
                        ret, name, ','.join(params), kind)
                    key = owner + '.' + name
                    rec = {
                        'type': owner, 'member': name, 'return_type': ret,
                        'static': is_static, 'access': access, 'params': params,
                        'kind': kind, 'decl_line': i,
                        'raw': re.sub(r'\s+', ' ', st)[:220], 'shape': shape,
                    }
                    if key not in exact:
                        exact[key] = rec
                        exact[key]['_shapes'] = [shape]
                    else:
                        exact[key]['_shapes'].append(shape)

        # 3) 大括号栈（用去注释/字符串后的行）
        for ch in code:
            if ch == '{':
                depth += 1
                if pending is not None:
                    stack.append((depth, pending))
                    pending = None
            elif ch == '}':
                if stack and stack[-1][0] == depth:
                    stack.pop()
                depth -= 1

    by_name = {}
    for k, v in exact.items():
        v['shape'] = ' || '.join(sorted(set(v.pop('_shapes'))))
        by_name.setdefault(v['member'], []).append(k)
    return exact, by_name, parents


def _inherits(child, parent, parents, depth=0):
    """child 是否（沿 dump 里的继承链）可访问 parent 的成员。"""
    if child == parent:
        return True
    if depth > 8:
        return False
    for b in parents.get(child, []):
        if _inherits(b, parent, parents, depth + 1):
            return True
    return False



def _feature_of(origin):
    if origin.startswith('Features/'):
        return origin.split('/')[1].split('.')[0]
    if origin.startswith('SR_UCH/'):
        return 'core'
    return 'bootstrap'


def _code_sites(member, origins):
    """建议改哪一行：优先真实调用/注册点，跳过纯注释（否则报告指向说明文字）。"""
    if not member:
        return []
    files = [o for o in sorted(origins) if o.endswith('.cs') and os.path.exists(o)]
    if not files:
        for d, _, fs in os.walk('.'):
            if '__pycache__' in d:
                continue
            for f in fs:
                if f.endswith('.cs') and (d.startswith('./Features') or d.startswith('./SR_UCH')):
                    files.append(os.path.join(d, f).replace('./', ''))
    pat = re.compile(r'\b' + re.escape(member) + r'\b')
    strong = re.compile(r'HarmonyPatch|Ref\.(?:Field|Method|Override)|typeof\(')
    primary, fallback = [], []
    for f in files:
        try:
            for i, l in enumerate(rd(f).splitlines(), 1):
                s = l.strip()
                if s.startswith('//') or s.startswith('*') or not pat.search(s):
                    continue
                rec = {'file': f, 'line': i, 'text': s[:120]}
                (primary if strong.search(s) else fallback).append(rec)
        except Exception:
            pass
    return (primary + fallback)[:4]



def _impact_map(entries, exact, by_name, parents):
    """把 (type,member) 映射到：影响哪些功能 / 哪些注册种类 / 建议改哪一行 / dump 形状。

    解析分四级（可信度递减，绝不把低可信伪装成精确）：
      exact     作用域栈给出的声明类 == 期望类型
      inherited 声明类是期望类型的基类（项目通过派生类反射访问基类成员，合法）
      by-name   只能按成员名回退（同名多载合并，标注候选数）
      none      dump 未解析到声明
    """
    agg = {}
    for e in entries:
        if e['kind'] in ('scene', 'net_magic', 'patch_implicit'):
            continue
        if e.get('external') or e.get('member_alias'):
            continue
        key = '%s.%s' % (e.get('type') or '', e.get('member') or '')
        x = agg.setdefault(key, {'origins': set(), 'kinds': set(),
                                 'member': e.get('member') or '',
                                 'type': e.get('type') or ''})
        x['origins'].add(e.get('origin', ''))
        x['kinds'].add(e['kind'])
    out = {}
    for key, x in agg.items():
        member = x['member']
        want = x['type']
        rec = exact.get(key)
        match = 'exact'
        if rec is None:
            # 继承校准：找同名候选里，声明类是期望类型基类的那一条
            cand = []
            for k in sorted(by_name.get(member, [])):
                owner = k.rsplit('.', 1)[0]
                if want and _inherits(want, owner, parents):
                    cand.append(exact[k])
            if cand:
                rec = dict(cand[0])
                match = 'inherited(%s)' % rec['type']
            else:
                alts = [exact[k] for k in sorted(by_name.get(member, []))]
                if alts:
                    shapes = sorted(set(a['shape'] for a in alts))
                    rec = dict(alts[0])
                    rec['shape'] = ' || '.join(shapes)
                    rec['raw'] = ' || '.join(sorted(set(a['raw'] for a in alts)))
                    rec['decl_line'] = min(a['decl_line'] for a in alts)
                    match = 'by-name(%d)' % len(alts)
                else:
                    rec, match = {}, 'none'
        origins = sorted(x['origins'])
        kinds = sorted(x['kinds'])
        acts = []
        if any(k.startswith('patch') for k in kinds):
            acts.append('核对 HarmonyPatch 目标签名')
        if 'reflection' in kinds:
            acts.append('核对 Ref.Override 字符串与 GetMethod/GetField 参数')
        out[key] = {
            'features': sorted(set(_feature_of(o) for o in origins)),
            'kinds': kinds,
            'origins': origins,
            'sites': _code_sites(member, origins),
            'action': ' / '.join(acts) or '核对绑定',
            'dump_shape': rec.get('shape', ''),
            'match': match,
            'decl_line': rec.get('decl_line', 0),
            'raw': rec.get('raw', ''),
        }
    return out



def cmd_fingerprint(dump=None, write=True, strict_signatures=False):
    """用反编译源码重比唯一绑定表，并输出「哪条变了 → 影响哪几个功能 → 建议改哪一行」。

    不需要游戏 DLL，也不需要编译器。绑定表 contract-fingerprint.json 同时承载：
    entries（声明清单）/ bindings（每条的 dump 形状 + 影响面）/ verification（结论）/ changes（变更影响）。
    """
    path = dump or os.environ.get('UCH_DUMP') or '/data/inputs/Assembly-CSharp.decompiled.cs'
    if not path or not os.path.exists(path):
        print('[fingerprint] 找不到反编译源码：%s' % path)
        print('  用法：python3 tools/uch.py fingerprint --dump <反编译.cs>')
        return 2
    raw = open(path, 'rb').read()
    text = raw.decode('utf-8', 'replace').replace('\r\n', '\n')
    sha = hashlib.sha256(raw).hexdigest()

    fp = js(GENDIR + '/contract-fingerprint.json')
    old_impact = fp.get('bindings', {}) or {}
    old_dump = (fp.get('verification') or {}).get('dump_sha256', '')

    types = set(re.findall(
        r'^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|sealed|'
        r'abstract|partial|unsafe|readonly|new|global)\s+)*'
        r'(?:class|struct|interface|enum)\s+([A-Za-z_]\w*)', text, re.M))
    members = set(re.findall(
        r'^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|virtual|'
        r'override|abstract|extern|unsafe|readonly|const|new|partial|async|sealed|event)\s+)+'
        r'[\w<>,\[\]\.\?`]+\s+([A-Za-z_]\w*)\s*(?:\(|\{|;|=|=>)', text, re.M))
    exact, by_name, parents = _dump_bindings(text)
    impact = _impact_map(fp['entries'], exact, by_name, parents)

    seen = set()
    checked = 0
    missing = []
    reviewed = []
    changes = []
    for e in fp['entries']:
        kind = e['kind']
        if kind in ('scene', 'net_magic', 'patch_implicit'):
            continue
        if e.get('external'):
            reviewed.append(e['signature'] + ' (external, skipped)')
            continue
        if e.get('member_alias'):
            reviewed.append(e['signature'] + ' (property alias: ' + str(e['member_alias']) + ')')
            continue
        key = (e.get('type'), e.get('member'))
        if key in seen:
            continue
        seen.add(key)
        checked += 1
        sig = e['signature']
        ok, why = _dump_present(e, types, members, text)
        if not ok:
            missing.append({'signature': sig, 'origin': e.get('origin', ''), 'reason': why})
        shape_now = impact.get(sig, {}).get('dump_shape', '')
        shape_old = (old_impact.get(sig) or {}).get('dump_shape', '')
        if old_impact and shape_old and shape_old != shape_now:
            changes.append({'key': sig, 'change': 'shape' if shape_now else 'decl-lost',
                            'old': shape_old, 'new': shape_now or '(dump 未解析到声明行)'})
        elif old_impact and sig not in old_impact:
            changes.append({'key': sig, 'change': 'new-binding', 'old': '(无)', 'new': shape_now})
        if not shape_now:
            reviewed.append(sig + ' (dump 未解析到声明行，仅按名字比对)')

    for c in changes:
        im = impact.get(c['key'], {})
        c['features'] = im.get('features', [])
        c['kinds'] = im.get('kinds', [])
        c['sites'] = im.get('sites', [])
        c['action'] = im.get('action', '核对绑定')
    for m in missing:
        im = impact.get(m['signature'], {})
        m.update({'features': im.get('features', []), 'sites': im.get('sites', []),
                  'action': im.get('action', '核对绑定')})
        changes.append(dict(m, change='missing'))

    match_stats = {}
    for v in impact.values():
        m = v['match']
        k = ('exact' if m == 'exact' else
             'inherited' if m.startswith('inherited') else
             'by-name' if m.startswith('by-name') else 'none')
        match_stats[k] = match_stats.get(k, 0) + 1
    # 同一份 dump 重跑：结论幂等，不重复报告上一次的变更。
    if sha == old_dump and not missing:
        changes = []
    verdict = 'MISSING' if missing else ('DRIFT' if changes and strict_signatures else 'OK')
    at = datetime.datetime.fromtimestamp(os.path.getmtime(path),
                                         datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
    fp['schema'] = 2
    fp['note'] = ('唯一绑定表：entries=声明清单，bindings=每条 dump 形状+影响面，'
                  'verification=比对结论，changes=换 dump 后的变更影响报告。'
                  '由 uch.py fingerprint --dump 写回；hash 只覆盖 entries，结论写回不改动它。')
    fp['bindings'] = impact
    fp['verification'] = {
        'verdict': verdict, 'checked': checked, 'suspected_missing': missing,
        'reviewed': sorted(set(reviewed)), 'match_stats': match_stats,
        'dump_sha256': sha, 'at': at,
        'checked_basis': 'unique (type,member); scene/net_magic/patch_implicit/external/alias excluded',
        'fingerprint_hash': fp['hash'], 'tool': 'uch.py fingerprint --dump',
    }
    fp['changes'] = changes

    print('== uch.py fingerprint ==')
    print('  dump        : %s' % path)
    print('  sha256      : %s…  （上次 %s…）' % (sha[:16], (old_dump or '(无)')[:12]))
    print('  类型/成员   : %d / %d（解析自反编译源码）' % (len(types), len(members)))
    print('  指纹条目    : %d（去重后实查 %d）' % (len(fp['entries']), checked))
    print('  绑定形状    : %d 条  [精确 %d / 继承 %d / 按名 %d / 未解析 %d]' %
          (len(impact), match_stats.get('exact', 0), match_stats.get('inherited', 0),
           match_stats.get('by-name', 0), match_stats.get('none', 0)))
    print('  疑似缺失    : %d / 人工确认 %d' % (len(missing), len(set(reviewed))))
    print('')
    print('== 变更影响报告 ==')
    if not changes:
        print('  0 条变更：dump 形状与基线一致，无需改代码。')
    else:
        print('  %d 条变更：' % len(changes))
        for n, c in enumerate(changes[:30], 1):
            print('  %d) %-46s [%s]' % (n, c['key'], c['change']))
            if c.get('old') != c.get('new'):
                print('      旧 %s' % str(c.get('old', ''))[:150])
                print('      新 %s' % str(c.get('new', ''))[:150])
            ft = ', '.join(c.get('features', [])) or '(未定位)'
            print('      影响功能 %s  [%s]' % (ft, ', '.join(c.get('kinds', []))))
            for s in c.get('sites', [])[:3]:
                print('      建议改 %s:%d  %s' % (s['file'], s['line'], s['text'][:70]))
            print('      动作 %s' % c.get('action', ''))
        if len(changes) > 30:
            print('  … 其余 %d 条见 generated/contract-fingerprint.json 的 changes' % (len(changes) - 30))
    print('')
    print('  verdict     : %s' % verdict)
    if write:
        wr(GENDIR + '/contract-fingerprint.json', json.dumps(fp, ensure_ascii=False, indent=2) + '\n')
        print('  已写回: %s/contract-fingerprint.json（唯一绑定表）' % GENDIR)
        cmd_seal()
        print('  随后跑: python3 tools/uch.py verify --strict  # 全套回归')
    if verdict == 'MISSING' or verdict == 'DRIFT':
        return 1
    return 0



def cmd_doctor(code=None):
    if code:
        return cmd_doctor_code(code)
    print('== uch.py doctor ==')
    have_pwsh = shutil.which('pwsh') is not None
    have_dotnet = shutil.which('dotnet') is not None
    have_managed = bool(os.environ.get('UCH_MANAGED') or os.environ.get('UCH_MANAGED_DIR'))
    have_dump = bool(os.environ.get('UCH_DUMP')) or os.path.exists('/data/inputs/Assembly-CSharp.decompiled.cs')
    print('  pwsh        : %s' % ('有' if have_pwsh else '无（不影响统一入口）'))
    print('  dotnet      : %s' % ('有' if have_dotnet else '无（CI tests / build-graph 承担真编译）'))
    print('  UCH_MANAGED : %s' % ('有 → 可跑全源码 IL 编译' if have_managed else '无 → 全源码 IL 编译跳过'))
    print('  game dump   : %s' % ('有 → 可离线重比契约指纹' if have_dump else '无 → 用 --dump 指定反编译源码'))
    try:
        b = js(GENDIR + '/contract-fingerprint.json')
        v = b.get('verification', {})
        print('  契约绑定    : %s / 实查 %d / 缺失 %d / 变更 %d / dump %s…' %
              (v.get('verdict'), v.get('checked', 0),
               len(v.get('suspected_missing', [])), len(b.get('changes', [])),
               (v.get('dump_sha256') or '')[:12]))
        print('               复验于 %s；游戏更新后重跑 fingerprint 才会刷新此行' % v.get('at', '?'))
    except Exception:
        pass
    print('')
    print('下一步（按序，任选起点）：')
    print('  1) python3 tools/uch.py verify --strict              # 日常只需要这条')
    print('  2) python3 tools/uch.py fingerprint --dump <反编译.cs> --strict-signatures  # 游戏更新后重比')
    print('  3) python3 tools/uch.py gen && python3 tools/uch.py seal # 改声明后重算 + 重新封印')
    print('  4) dotnet build SR_UCH.csproj -c Release -warnaserror  # 需 UCH_MANAGED')
    print('  5) 进游戏取 [SR.SelfTest] / 实机 A-B 证据 → uch.py ship')
    print('')
    print('按事件码查站点：uch.py doctor --code <CODE>')
    print('核对是否有人偷改：uch.py freeze-check')
    return 0


def cmd_smoke(full=False):
    if full:
        return cmd_smoke_full()
    print('== uch.py smoke ==')
    r = cmd_verify(strict=False)
    print()
    cmd_doctor()
    print()
    t = cmd_trace_gate()
    if exists(MANIFEST):
        mf = js(MANIFEST)
        print('\n[health] %s / 检查 %d 项 / 生成物 %d / 源码 %d' %
              ('PASS' if mf['counts'].get('fail', 0) == 0 else 'FAIL',
               mf['counts'].get('total', 0), len(mf['generated_hashes']), len(mf['source_hashes'])))
    return r or t




def _needs_bootstrap():
    try:
        req = js(SPEC)['toolchain']['required_generated']
    except Exception:
        req = ['facts.generated.json', 'sources.manifest.json']
    if any(not exists(GENDIR + '/' + x) for x in req):
        return True
    return not exists('build/rsp/sr_uch.rsp')


def cmd_materialize():
    return subprocess.run([sys.executable, 'tools/materialize.py'], cwd=ROOT).returncode


def _ensure():
    if os.environ.get('UCH_NO_BOOTSTRAP') == '1':
        return 0
    try:
        if _needs_bootstrap():
            print('[bootstrap] 派生物缺失，自动恢复…')
            os.environ['UCH_NO_BOOTSTRAP'] = '1'
            r = cmd_materialize()
            os.environ.pop('UCH_NO_BOOTSTRAP', None)
            return r
    except Exception:
        pass
    return 0

def cmd_gen(mode='generate'):
    r = subprocess.run([sys.executable, 'tools/spec_engine.py', mode], cwd=ROOT)
    print('完成后跑: python3 tools/uch.py verify --strict')
    return r.returncode


def cmd_impact(path):
    rows = maint_ops.route(path)
    print('== 影响路由: %s ==' % path)
    for cid, why in rows:
        print('  %-12s %s' % (cid, why))
    print('\n共 %d / %d 项 CHK；只跑这些用：uch.py verify --changed %s' %
          (len(rows), len(maint_ops.IMPACT), path))
    return 0


def cmd_smoke_full():
    steps = js(GENDIR + '/smoke.generated.json')['steps']
    print('== uch.py smoke --full（全链路可复现） ==')
    failed, skipped = [], []
    for st in steps:
        cmd = st['cmd']
        optional = bool(st.get('optional'))
        if cmd.startswith('dotnet') and shutil.which('dotnet') is None:
            skipped.append(st['id']); print('  [%s] %-20s SKIP（无 dotnet）' % (st['id'], st['name'])); continue
        if cmd.startswith('spec_engine.py'):
            args = [sys.executable, 'tools/spec_engine.py'] + cmd.split()[1:]
        elif cmd.startswith('maint_ops.py'):
            args = [sys.executable, 'tools/maint_ops.py'] + cmd.split()[1:]
        elif cmd.startswith('uch.py'):
            args = [sys.executable, 'tools/uch.py'] + cmd.split()[1:]
        else:
            args = cmd.split()
        r = subprocess.run(args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
        out = r.stdout.decode('utf-8', 'replace').strip().splitlines()
        tail = out[-1][:90] if out else '(no output)'
        ok = r.returncode == 0
        if not ok and not optional:
            failed.append(st['id'])
        print('  [%s] %-20s %s / %s' % (st['id'], st['name'], 'OK' if ok else 'FAIL', tail))
    print('\n== smoke: FAIL %d / SKIP %d / 共 %d 步 ==' % (len(failed), len(skipped), len(steps)))
    if skipped:
        print('跳过（本地补跑）: ' + ', '.join(skipped))
    return 1 if failed else 0


def cmd_ship():
    p = GENDIR + '/ship.checklist.json'
    if not exists(p):
        print('缺 ' + p + '，先跑 uch.py gen')
        return 1
    d = js(p)
    print('== uch.py ship（兑现清单，由单一引擎派生） ==')
    for x in d['items']:
        print('  %s  %-38s [%s] %s' % (x['id'], x['name'], x['owner'], x['why']))
    print('\n本侧已就绪：单一引擎 / 固定 CHK / 影响路由 / 错误码 / 闸门 / 重放。')
    print('清单: ' + p)
    return 0


def cmd_doctor_code(code):
    p = GENDIR + '/error-sites.generated.json'
    if not exists(p):
        print('缺 ' + p); return 1
    d = js(p)
    hit = [x for x in d['sites'] if x['code'] == code]
    print('== %s：%d 个站点 / %s ==' % (code, len(hit), d['sites'][0]['action'] if d['sites'] else ''))
    for x in hit[:20]:
        print('  %-46s %-14s %-8s %s' % (x['site'], x['owner'], x.get('kind', x['severity']), x.get('scope', '')))
    return 0


def cmd_freeze_check():
    """与已提交基线逐字节核对：谁在我不知情时改了源码或生成物。"""
    if not exists(MANIFEST):
        print('[frozen] 缺基线，先跑 uch.py verify')
        return 1
    mf = js(MANIFEST)
    src_base = {k: v for k, v in mf.get('source_hashes', {}).items() if k != MANIFEST}
    gen_base = mf.get('generated_hashes', {})
    drift = []
    for p, h in src_base.items():
        if not exists(p):
            drift.append('源码 缺失: %s' % p)
        elif sha(p) != h:
            drift.append('源码 变更: %s' % p)
    for f, h in gen_base.items():
        p = GENDIR + '/' + f
        if not exists(p):
            drift.append('生成物 缺失: %s' % f)
        elif sha(p) != h:
            drift.append('生成物 变更: %s' % f)
    cur = gen_hashes()
    extra = [f for f in cur if f not in gen_base]
    print('== uch.py freeze-check ==')
    print('  基线: 源码 %d / 生成物 %d' %
          (len(mf.get('source_hashes', {})), len(mf.get('generated_hashes', {}))))
    if drift or extra:
        print('  漂移 %d 项：' % (len(drift) + len(extra)))
        for d in (drift + ['生成物新增: ' + f for f in extra])[:20]:
            print('   - ' + d)
        print('  改完确认无误后跑 uch.py verify 重新写基线。')
        return 1
    print('  OK：当前树与已提交基线逐字节一致')
    return 0


def cmd_migrate(dry=False, apply=False):
    tc = js(SPEC)['toolchain']
    print('== 结构改动清单（可重放） ==')
    for m in tc['migrations']:
        print('  %s  %-42s → %s' % (m['id'], m['title'], m['chk']))
    if apply:
        print('\n[apply] 逐条真重放（幂等）…')
        return maint_ops.codemod(True)
    checks, _, _ = C()
    by = {i: fn for i, d, fn, lv in checks}
    misses = []
    for m in tc['migrations']:
        i = m['chk']
        if i not in by:
            misses.append('%s 缺 CHK %s' % (m['id'], i)); continue
        ok, _ = by[i]()
        if not ok:
            misses.append('%s (%s) 未通过' % (m['id'], i))
    print('\n结论: %s（加 --apply 真重放）' % ('全部齐备' if not misses else '缺 %d 项' % len(misses)))
    return 1 if misses else 0


def docs_text():
    """INDEX.md 全文的唯一构造实现：cmd_docs 写回、DOC-001 守卫逐字比对都用它。"""
    L = ['<!-- generated:uch.py docs -->', '# SR_UCH 维护手册', '',
         '本文件是唯一文档入口，由 `python3 tools/uch.py docs` 生成。',
         '数值来自 `build/specs/generated/facts.generated.json` 与 `docs/STATUS.md`。', '',
         '## 结构：一个入口 / 一个引擎 / 一份规则', '',
         '- 唯一声明：`build/specs/features.spec.json`（schema 3，旧版直接拒绝）',
         '- 唯一入口：`tools/uch.py`（verify / gen / doctor / smoke / add / remove / docs …）',
         '- 唯一引擎：`tools/spec_engine.py`（从 spec + 磁盘源码派生全部生成物）',
         '- 唯一规则：`tools/uch_rules.py`（%d 条机械守卫，`uch.py rules --suite all`）' % len(uch_rules.RULES),
         '- 独立守卫：`tools/check_architecture.py`（分层）、`tools/check_uimodel.py`（页面）',
         '- 构建：`build/build.ps1`（rsp 单链）、`build/run-tests.ps1`（真实程序集测试）',
         '- 派生物：`build/rsp/`、`build/specs/generated/`、`*.g.cs` 由 `uch.py gen` 重算，勿手改', '',
         '## 日常命令', '', '```',
         'python3 tools/uch.py verify --strict   # 统一回归（固定 CHK + 单一基线）',
         'python3 tools/uch.py doctor            # 环境诊断与下一步',
         'python3 tools/uch.py gen               # 改了声明/源码后重算派生物',
         'python3 tools/uch.py analyze          # 装载顺序 / SRApi 门面注入前置离线对账',
         '```', '',
         '加功能：`uch.py add <Name>`（生成骨架并打印声明片段）；',
         '删功能：`uch.py remove <Name>`（列出残渣清单）→ 手工删除后 `gen` + `verify`。', '',
         '## 口径（既定，不再改动）', '',
         '- 单 DLL 构建；split 需求已移除；EX 与独立插件在各自项目构建',
         '- 关闭混乱牌组 → 所有人的修改器清空（无快照回滚）',
         '- 派对盒炸弹不处理；不加搜索框；README 描述文案人工维护',
         '- 不兼容旧版本：Tid 只认 @id，cfg schema 门 == 3', '',
         '## 诊断与游戏更新', '',
         '- 游戏更新后跑 `uch.py fingerprint --dump <反编译.cs>`：重比签名，红哪条改哪个功能',
         '- 运行时健康状态由 `[SR.SelfTest]` / 诊断报告 / 结构化事件码给出',
         '- 双轨 UI 仍并跑：`uch.py trace-gate --enforce` 校验声明完整；',
         '  旧 IMGUI 路径的退役需实机 A/B 证据，未达标不请求 cutover', '',
         '## 验证边界', '',
         '- CI（3 个 job）：统一回归 / 契约层与纯函数层真实编译 / 构建图与全源码 IL',
         '- 编译输入（rsp / compile.targets / references.props）已入库，fresh clone 直接 `dotnet build` 只需 `UCH_MANAGED_DIR`',
         '- 报告与基线：`build/specs/generated/verify.manifest.json`（固定 CHK ID）', '']
    return '\n'.join(L)


def cmd_docs():
    """唯一文档入口：所有维护信息收在一份 INDEX.md（由本命令生成，勿手改）。"""
    wr('docs/INDEX.md', docs_text())
    print('[docs] docs/INDEX.md 已生成（唯一文档入口）')
    return 0

def cmd_add_remove(name, remove=False):
    """加功能：生成骨架 + 打印声明片段；删功能：列出残渣清单（真删仍需人工）。"""
    if remove:
        plan = GENDIR + '/removal-plan.json'
        print('== 删除 %s：残渣清单 ==' % name)
        if exists(plan):
            for x in js(plan).get('items', [])[:40]:
                print('  -', x if isinstance(x, str) else json.dumps(x, ensure_ascii=False))
        print('手工删除后跑: python3 tools/uch.py gen && python3 tools/uch.py verify --strict')
        return 0
    if not re.match(r'^[A-Za-z][A-Za-z0-9]*$', name):
        print('功能名只能用字母/数字且以字母开头')
        return 1
    fid = 'sr.' + name.lower()
    spec = js(SPEC)
    if any(f['id'] == fid for f in spec['features']):
        print('声明里已有该功能 id：' + fid)
        return 1
    path = 'Features/' + name + '.cs'
    if exists(path):
        print('已存在，不覆盖：' + path)
        return 1
    ranks = set()
    for p in glob.glob('Features/**/*.cs', recursive=True):
        ranks.update(int(x) for x in re.findall(r'SRApi\.Nav\([^,]+,\s*(\d+)\s*\)', rd(p)))
    rank = 131
    while rank in ranks:
        rank += 1
    tpl = """// <N>：<一句话说明>（由 tools/uch.py add 生成，按需补实现）。
using System;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    [SrFeature("<ID>", "<N>", "1.0")]
    public class <N> : ITweak {
        private const string Sec = "<N>";
        private static ConfigEntry<bool> _enabled;

        private static void SelfReg() {
            SRApi.LocSec(Sec, "<N>", "<N>");
            SRApi.Nav(Sec, <RANK>);
            SRApi.RegisterPage(Sec, "Main", "主页", "Main", 10, RenderPage);
            SRApi.LocKey(Sec, "Enabled", "启用<N>", "Enable <N>");
            SRApi.LocDesc(Sec, "Enabled", "启用<N>\n打开后本功能才生效。",
                "Enable <N>\nTurn on to activate this feature.");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Enabled", false, "总开关：打开后本功能才生效。");
                SRApi.OnShutdown("<N>", Shutdown);
            } catch (Exception __ex) { SRApi.Guard.Log("<N>/初始化", __ex); }
        }

        public void Shutdown() { }

        private static void RenderPage() {
            if (_enabled != null) SRApi.Ctl.DrawEntryRow(_enabled, SRApi.Ctl.Sc(180f));
        }
    }
}
"""
    tpl = tpl.replace('<N>', name).replace('<ID>', fid).replace('<RANK>', str(rank))
    wr(path, tpl)
    print('已生成: %s (rank=%d)' % (path, rank))
    print('回填声明片段：')
    print('  groups[]  : { "id": "%s", "feature_ids": [ "%s" ] }' % (name, fid))
    print('  features[]: { "id": "%s", "name": "%s", "version": "1.0", "modes": "all", "provides": [] }' % (fid, name))
    print('随后跑: python3 tools/uch.py gen && python3 tools/uch.py verify --strict')
    return 0

def main():
    ap = argparse.ArgumentParser(description='SR_UCH 唯一维护入口')
    sub = ap.add_subparsers(dest='cmd')
    p = sub.add_parser('verify'); p.add_argument('--strict', action='store_true')
    p.add_argument('--json', action='store_true'); p.add_argument('--fast', action='store_true')
    p.add_argument('--changed', nargs='*', default=None)
    sub.add_parser('gen'); sub.add_parser('seal'); sub.add_parser('materialize'); sub.add_parser('smoke').add_argument('--full', action='store_true')
    p = sub.add_parser('doctor'); p.add_argument('--code', default=None)
    p = sub.add_parser('fingerprint'); p.add_argument('--dump', default=None)
    p.add_argument('--strict-signatures', action='store_true')
    sub.add_parser('freeze-check')
    p = sub.add_parser('trace-gate'); p.add_argument('--enforce', action='store_true')
    p = sub.add_parser('migrate'); p.add_argument('--dry-run', action='store_true'); p.add_argument('--apply', action='store_true')
    p = sub.add_parser('codemod'); p.add_argument('--dry-run', action='store_true'); p.add_argument('--apply', action='store_true')
    p = sub.add_parser('impact'); p.add_argument('path')
    p = sub.add_parser('analyze'); p.add_argument('--out', default=None)
    sub.add_parser('ship'); sub.add_parser('docs')
    sub.add_parser('precompile')
    p = sub.add_parser('rules'); p.add_argument('--suite', default='all')
    p.add_argument('--json', action='store_true'); p.add_argument('--generate', action='store_true')
    p.add_argument('--log'); p.add_argument('--health'); p.add_argument('--update', action='store_true')
    p = sub.add_parser('add'); p.add_argument('name')
    p = sub.add_parser('remove'); p.add_argument('name')
    a = ap.parse_args()
    if a.cmd not in (None, 'materialize', 'gen', 'seal'):
        if _ensure(): return 1
    if a.cmd == 'materialize': return cmd_materialize()
    if a.cmd == 'verify':
        return cmd_verify(a.strict, a.json, a.fast, a.changed, nowrite=(a.fast or a.changed is not None))
    if a.cmd == 'gen': return cmd_gen('generate')
    if a.cmd == 'seal': return cmd_seal()
    if a.cmd == 'smoke': return cmd_smoke(a.full)
    if a.cmd == 'doctor': return cmd_doctor(a.code)
    if a.cmd == 'fingerprint': return cmd_fingerprint(a.dump, True, a.strict_signatures)
    if a.cmd == 'freeze-check': return cmd_freeze_check()
    if a.cmd == 'trace-gate': return cmd_trace_gate(a.enforce)
    if a.cmd == 'migrate': return cmd_migrate(a.dry_run, a.apply)
    if a.cmd == 'codemod': return cmd_migrate(False, a.apply)
    if a.cmd == 'impact': return cmd_impact(a.path)
    if a.cmd == 'analyze': return maint_ops.cmd_analyze(a.out)
    if a.cmd == 'ship': return cmd_ship()
    if a.cmd == 'docs': return cmd_docs()
    if a.cmd == 'rules':
        return uch_rules.run_suite(a.suite, a.json, a.generate, a.log, a.health, a.update)
    if a.cmd == 'precompile': return uch_rules.precompile()
    if a.cmd == 'add': return cmd_add_remove(a.name, False)
    if a.cmd == 'remove': return cmd_add_remove(a.name, True)
    ap.print_help(); return 0


if __name__ == '__main__':
    sys.exit(main())
