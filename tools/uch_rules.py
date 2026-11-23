#!/usr/bin/env python3
# SR_UCH 机械守卫的唯一实现。
# build/ 下的 PowerShell 脚本只转发参数，不含任何正则、阈值或文件枚举。
# 判定与派生产物一律读 build/specs/features.spec.json 及其 generated/ 输出。
import os
import re
import sys
import json
import glob
import hashlib

ROOT = os.getcwd()
SPEC = 'build/specs/features.spec.json'
GEN = 'build/specs/generated'


def rb(p):
    with open(p, 'rb') as f:
        return f.read()


def tx(p):
    return rb(p).decode('utf-8-sig')


def js(p):
    return json.loads(tx(p))


def win(p):
    return p.replace('/', '\\')


def rel(p):
    return p.replace('\\', '/')


def spec():
    return js(SPEC)


def rat():
    return js(GEN + '/ratchets.json')


def cs(dirname, recursive=True):
    pat = dirname + '/**/*.cs' if recursive else dirname + '/*.cs'
    return sorted(rel(p) for p in glob.glob(pat, recursive=recursive)
                  if '/bin/' not in rel(p) and '/obj/' not in rel(p) and '/tests/' not in rel(p))


def text_files():
    ext = ('.cs', '.ps1', '.rsp', '.md', '.txt', '.json')
    out = []
    for dp, dn, fn in os.walk('.'):
        dn[:] = [d for d in dn if d not in ('.git', '__pycache__', 'bin', 'obj', '.cache')]
        for f in fn:
            p = rel(os.path.join(dp, f))
            if f.lower().endswith(ext):
                out.append(p.lstrip('./'))
    return sorted(out)


def strip_line(s):
    s = s.strip()
    if s.startswith('//') or s.startswith('*') or s.startswith('/*'):
        return ''
    s = re.sub(r'"(?:[^"\\]|\\.)*"', '""', s)
    i = s.find('//')
    if i >= 0:
        s = s[:i]
    return s


# ---------------------------------------------------------------- 文本守卫
def api_surface():
    allow = set()
    for l in tx('build/api_surface.txt').splitlines():
        s = l.strip()
        if s and not s.startswith('#'):
            allow.add(s)
    contracts = '\n'.join(tx(p) for p in cs('SR_UCH/Contracts', False))
    for m in re.finditer(r'(?m)^\s*public[^;\r\n]*?(\w+)\s*[({;=]', contracts):
        allow.add(m.group(1))
    bad = set()
    for p in cs('Features', False):
        for m in re.finditer(r'\bSRApi\.(\w+)', tx(p)):
            if m.group(1) not in allow:
                bad.add(m.group(1))
    if bad:
        return False, '白名单外 SRApi.*: ' + ', '.join(sorted(bad))
    fake = [n for n in allow if not re.search(
        r'(?m)^\s*(?:\[[^\]]*\]\s*)*(?:public|internal)\s+[^\r\n]*\b' + re.escape(n) + r'\b', contracts)]
    if fake:
        return False, '白名单条目在契约层不存在: ' + ', '.join(sorted(fake)[:5])
    return True, '功能层只用白名单内成员（%d 个，均真实存在）' % len(allow)


def boundary(d, banned):
    bad = []
    for p in cs(d, False):
        for i, l in enumerate(tx(p).splitlines(), 1):
            code = strip_line(l)
            if not code:
                continue
            if re.search(banned, code) or re.search(r'(?<![A-Za-z_.])SR\.', code):
                bad.append('%s:%d' % (os.path.basename(p), i))
    if bad:
        return False, '%s 引用实现层标识: %s' % (d, ', '.join(bad[:5]))
    return True, '%s 不含实现层标识' % d


def contract_boundary():
    return boundary('SR_UCH/Contracts', r'\b(MainPlugin|ConfigMigration|SRHost)\b')


def feature_leak():
    return boundary('Features', r'\b(ConfigMigration|MainPlugin)\b')


def src_manifest():
    return js(GEN + '/sources.manifest.json')


def compile_set():
    return set(rel(x) for x in src_manifest()['compile'])


def rsp_lines(p):
    return [l.strip() for l in tx(p).splitlines() if l.strip().endswith('.cs')]


def rsp_coverage():
    disk = set(cs('SR_UCH') + cs('Features') + cs('Pure') + ['MainPlugin.cs'])
    listed = set(rel(x) for x in rsp_lines('build/rsp/sr_uch.rsp'))
    if listed != disk:
        return False, 'sr_uch.rsp 漏 %d / 多 %d' % (len(disk - listed), len(listed - disk))
    extra = [f for f in glob.glob('build/rsp/**/*.rsp', recursive=True)
            if f.replace('\\', '/') != 'build/rsp/sr_uch.rsp']
    if extra:
        return False, '单链之外仍有清单: ' + ', '.join(extra[:4])
    return True, 'rsp 精确覆盖 %d 个源码（单链，外部清单 0 份）' % len(disk)


def encoding_utf8():
    bad = []
    for p in text_files():
        try:
            rb(p).decode('utf-8')
        except UnicodeDecodeError:
            bad.append(p)
    if bad:
        return False, '非 UTF-8 文本: ' + ', '.join(bad[:5])
    return True, '全部 %d 个文本文件为合法 UTF-8' % len(text_files())


def split_args(body):
    inner = body[1:-1]
    out, cur, depth, instr = [], [], 0, False
    i = 0
    while i < len(inner):
        ch = inner[i]
        if instr:
            cur.append(ch)
            if ch == '\\' and i + 1 < len(inner):
                cur.append(inner[i + 1]); i += 1
            elif ch == '"':
                instr = False
            i += 1
            continue
        if ch == '"':
            instr = True; cur.append(ch); i += 1; continue
        if ch in '([{': depth += 1
        elif ch in ')]}': depth -= 1
        if ch == ',' and depth == 0:
            out.append(''.join(cur).strip()); cur = []; i += 1; continue
        cur.append(ch); i += 1
    out.append(''.join(cur).strip())
    return out


def desc_format():
    files = cs('Features', False)
    core = 'SR_UCH/SR.Core.cs'
    if os.path.exists(core):
        files.append(core)
    bad, total, skipped = 0, 0, 0
    # 配置登记两种合法形态：收敛后的 TweakCfg.Entry(6 参) 与残余的 LocDesc(4 参)。
    # 判定仍是同一份：说明文字必须是 2-4 行字面量，不受调用形态变化影响。
    forms = (('TweakCfg.Entry(', (4, 5)), ('LocDesc(', (2, 3)))
    for p in files:
        t = tx(p)
        for needle, check in forms:
            idx = 0
            while True:
                j = t.find(needle, idx)
                if j < 0: break
                k = t.find('(', j); depth = 0; m = k
                while m < len(t):
                    if t[m] == '(': depth += 1
                    elif t[m] == ')':
                        depth -= 1
                        if depth == 0: break
                    m += 1
                if m >= len(t): break
                args = split_args(t[k:m + 1])
                if len(args) <= max(check):
                    skipped += 1; idx = m + 1; continue
                for ai in check:
                    a = args[ai]
                    if not re.match(r'^"(?:[^"\\]|\\.)*"$', a):
                        skipped += 1; continue
                    s = a[1:-1]; total += 1
                    parts = s.split('\\n')
                    if not 2 <= len(parts) <= 4 or any(not x.strip() for x in parts): bad += 1; continue
                    if re.search(r'是什么：|干什么：|注意事项：|What: |Does: |Notes: ', s): bad += 1
                idx = m + 1
    if bad:
        return False, 'LocDesc 格式不合规 %d 条' % bad
    return True, 'LocDesc 2-4 行统一（%d 条 / 跳过动态 %d 处）' % (total, skipped)


def config_entry_single_source():
    """凡带说明的配置一律走 TweakCfg.Entry；功能层不得再有独立的 LocDesc 调用。
    只有没有说明的孤立标题（FreePlay Page）允许保留单独的 LocKey。"""
    kit = 'Features/TweakCfg.cs'
    if not os.path.exists(kit): return False, '缺 Features/TweakCfg.cs'
    if 'internal static class TweakCfg' not in tx(kit): return False, 'TweakCfg 实现缺失'
    entries, bare_desc, bare_key = 0, 0, 0
    for p in cs('Features', False):
        if p == kit: continue
        t = tx(p)
        entries += len(re.findall(r'TweakCfg\.Entry\s*\(', t))
        bare_desc += len(re.findall(r'SRApi\.LocDesc\s*\(', t))
        bare_key += len(re.findall(r'SRApi\.LocKey\s*\(', t))
    if bare_desc: return False, '仍有 %d 处独立 LocDesc 登记（应改用 TweakCfg.Entry）' % bare_desc
    return True, '配置登记单一入口（%d 处 / 独立说明 0 / 孤立标题 %d）' % (entries, bare_key)


def cs_encoding():
    bad = []
    for p in glob.glob('**/*.cs', recursive=True) + glob.glob('**/*.csproj', recursive=True):
        r = rel(p)
        if '/bin/' in r or '/obj/' in r or '/.git/' in r: continue
        b = rb(p)
        if b.startswith(b'\xef\xbb\xbf'): bad.append(r + ': 不应带 BOM'); continue
        if b'\r\n' in b: bad.append(r + ': 应为 LF')
    for p in glob.glob('**/*.ps1', recursive=True):
        b = rb(p)
        if not b.startswith(b'\xef\xbb\xbf'):
            bad.append(rel(p) + ': 缺 BOM')
    if bad:
        return False, '编码不合规: ' + '; '.join(bad[:5])
    return True, '.cs/.csproj 无 BOM+LF；.ps1 有 BOM'

def bare_catch():
    bad = []
    for p in cs('SR_UCH') + cs('Features') + cs('Pure') + ['MainPlugin.cs']:
        n = len(re.findall(r'catch\s*\{\s*\}', tx(p)))
        if n: bad.append('%s=%d' % (p, n))
    if bad:
        return False, '裸 catch %d 处: %s' % (sum(int(x.split('=')[1]) for x in bad), ', '.join(bad[:5]))
    return True, '裸 catch 0 处'


def region_fields():
    files = glob.glob('SR_UCH/SR.*.cs')
    texts = {os.path.basename(p): tx(p) for p in files}
    decl = {}
    for k, t in texts.items():
        for m in re.finditer(r'(?m)^\s*(?:private|internal|public)\s+static\s+(?:readonly\s+)?[\w<>\[\],\.\?]+\s+(_\w+)\s*(?:=|;)', t):
            decl.setdefault(m.group(1), k)
    # 归属登记的唯一真值是声明；旧 build/region-access.txt 已退役。
    allow = set(spec()['policy']['region_access'])
    bad = []
    for name, owner in decl.items():
        if name in allow: continue
        for k, t in texts.items():
            if k != owner and re.search(r'\b' + re.escape(name) + r'\b', t):
                bad.append(name); break
    if bad:
        return False, 'SR 分区越界 %d 个: %s' % (len(bad), ', '.join(bad[:5]))
    return True, 'SR 分区无新增越界（已登记 %d 个）' % len(allow)


def silent_catch():
    base = {k: int(v) for k, v in rat().get('silent_catch', {}).items()}
    pat = re.compile(r'catch\s*(\([^)]*\))?\s*\{\s*(\}|/\*\s*静默)')
    bad, total = [], 0
    for p in cs('SR_UCH') + cs('Features') + cs('Pure') + ['MainPlugin.cs']:
        n = len(pat.findall(tx(p)))
        if not n: continue
        total += n
        limit = base.get(rel(p), 0)
        if n > limit: bad.append('%s %d->%d' % (rel(p), limit, n))
    if bad:
        return False, '静默 catch 变多: ' + '; '.join(bad[:4])
    return True, '静默 catch 未新增（%d 处 / 基线 %d 文件）' % (total, len(base))


def indent():
    bad = [p for p in cs('SR_UCH') + cs('Features') + cs('Pure') + ['MainPlugin.cs']
           if re.search(r'(?m)^\t', tx(p))]
    if bad:
        return False, '行首 Tab: ' + ', '.join(bad[:5])
    return True, '缩进统一（仅空格）'


def tip_composer():
    loc = tx('SR_UCH/SR.Localization.cs')
    setf = tx('SR_UCH/SR.Settings.cs')
    bad = []
    if 'internal static string ComposeTip' not in loc: bad.append('缺 ComposeTip')
    if 'ComposeTip(entry, desc, null)' not in loc: bad.append('RowTip 未走 ComposeTip')
    if 'ComposeTip(entry, tip, hk)' not in setf: bad.append('RestoreLabel 未走 ComposeTip')
    if 'DefaultSuffix(' in setf: bad.append('RestoreLabel 自行追加默认值')
    if bad:
        return False, '悬浮条目组装被绕过: ' + '；'.join(bad)
    return True, '悬浮条目统一由 ComposeTip 追加'


def doc_scripts():
    bad, checked = [], 0
    for d in ('README.md', 'README.zh-CN.md', 'DESIGN.zh-CN.md'):
        if not os.path.exists(d): continue
        for m in re.finditer(r'([A-Za-z0-9_\-]+\.ps1)', tx(d)):
            name = m.group(1); checked += 1
            if not os.path.exists('build/' + name) and not os.path.exists(name):
                bad.append(d + ' -> ' + name)
    if bad:
        return False, '文档引用不存在的脚本: ' + '; '.join(sorted(set(bad))[:5])
    return True, '文档引用的脚本都存在（%d 处）' % checked


def api_level():
    s = spec(); sc = s['toolchain']['api_level_scope']
    files = [p for p in cs(sc['root']) if not any(x in p for x in sc['exclude_dirs'])]
    m = re.search(r'public\s+const\s+int\s+ApiLevel\s*=\s*(\d+)', tx('SR_UCH/Contracts/Contracts.cs'))
    cur_api = int(m.group(1)) if m else -1
    cur_members = sum(1 for p in files for l in tx(p).splitlines() if re.match(r'^\s*public\s', l))
    b = rat()['api_level']
    if cur_api != b['ApiLevel'] or cur_members != b['members']:
        return False, 'API 棘轮漂移: ApiLevel %d/%d public %d/%d' % (cur_api, b['ApiLevel'], cur_members, b['members'])
    if cur_members > b['members'] and cur_api == b['ApiLevel']:
        return False, '新增 public 未 bump ApiLevel'
    return True, 'ApiLevel=%d / public=%d（声明口径一致）' % (cur_api, cur_members)


BANNED_COMMENT = re.compile(r'反编译\s*\d|v\d+\.\d+|历轮|旧版|已删|以前|上轮|之前|Opt-\d+|评审\s*[A-Z]?\d|review\s*20\d\d|2026-|历史教训|审计\s*§|阶段\s*\d')


def comment_discipline():
    bad = []
    for d in ('SR_UCH', 'Features', 'Pure', 'tests'):
        for p in cs(d):
            for l in tx(p).splitlines():
                s = l.strip()
                if s.startswith('//') and BANNED_COMMENT.search(s):
                    bad.append(os.path.basename(p) + ': ' + s[:60])
    if bad:
        return False, '注释含 changelog 词 %d 处: %s' % (len(bad), bad[0])
    return True, '注释纪律：0 处 changelog / 版本 / 行号印记'


# ---------------------------------------------------------------- 结构守卫
def _const_map():
    """返回 (字面量常量表, 别名常量表)。别名表支持生成常量的链式引用。"""
    files = cs('Features', False) + cs('SR_UCH', False) + cs('SR_UCH/Contracts')
    literals, aliases = {}, {}
    pat = re.compile(r'(?m)^\s*(?:private|internal|public)\s+(?:static\s+)?(?:readonly\s+)?const\s+string\s+(\w+)\s*=\s*([^;]+);')
    for p in files:
        for m in pat.finditer(tx(p)):
            name, val = m.group(1), m.group(2).strip()
            if val.startswith('"') and val.endswith('"'):
                literals[name] = val[1:-1]
            else:
                aliases[name] = val
    return literals, aliases


def _resolve2(arg, literals, aliases):
    a = arg.strip()
    if a.startswith('"') and a.endswith('"'):
        return a.strip('"')
    short = a.split('.')[-1]
    if short in literals:
        return literals[short]
    if short in aliases:
        target = aliases[short].split('.')[-1]
        if target in literals:
            return literals[target]
    return None


def loc_coverage():
    s = spec()
    base = set(rat().get('loc_coverage', []))
    missing = []
    for p in cs('Features', False) + cs('SR_UCH', False) + cs('SR_UCH/Contracts', False):
        t = tx(p); name = os.path.basename(p)
        binds = []
        for m in re.finditer(r'Bind(?:<[^>]*>)?\(\s*("[^"]+"|[\w.]+)\s*,\s*"([^"]+)"\s*(\+?)', t):
            if m.group(1).strip('"') == 'Internal' or m.group(3) == '+': continue
            binds.append((m.group(1).strip('"'), m.group(2)))
        keys = set(re.findall(r'LocKey\(\s*(?:"[^"]+"|[\w.]+)\s*,\s*"([^"]+)"', t))
        keys |= set(re.findall(r'TweakCfg\.Entry\(\s*(?:"[^"]+"|[\w.]+)\s*,\s*"([^"]+)"', t))
        keys |= set(re.findall(r'\bLoc\(\s*"([^"]+)"', t))
        pairs = set(re.findall(r'LocKey\(\s*("[^"]+"|[\w.]+)\s*,\s*"([^"]+)"', t))
        pairs |= set(re.findall(r'TweakCfg\.Entry\(\s*("[^"]+"|[\w.]+)\s*,\s*"([^"]+)"', t))
        pairs = set((a.strip('"'), b) for a, b in pairs)
        for sec, key in binds:
            if key in keys or (sec, key) in pairs: continue
            missing.append('%s|%s|%s' % (name, sec, key))
    new = sorted(set(missing) - base)
    if new: return False, '新增未登记本地化绑定: ' + '; '.join(new[:4])
    return True, '本地化覆盖未退化（缺登记 %d 条均在基线内）' % len(set(missing))


# ---------------------------------------------------------------- 其余 suite
def selftest_expected(log=None, health=None, update=False):
    exp = js(GEN + '/selftest.expected.json')
    alltext = '\n'.join(tx(p) for p in cs('SR_UCH') + cs('Features') + cs('Pure') + ['MainPlugin.cs'])
    keys = set(re.findall(r'Ref\.Override(?:<[^>]*>)?\s*\(\s*"([^"]+)"', alltext))
    calls = len(re.findall(r'Ref\.Override(?:<[^>]*>)?\s*\(\s*"([^"]+)"', alltext))
    patches = len(re.findall(r'\[HarmonyPatch', alltext))
    for k, got, want in [('reflection_unique_keys', exp['reflection_unique_keys'], len(keys)),
                         ('reflection_calls', exp['reflection_calls'], calls),
                         ('harmony_patch_attributes', exp['harmony_patch_attributes'], patches),
                         ('features_declared', exp['features_declared'], len(spec()['features'])),
                         ('groups_declared', exp['groups_declared'], len(spec()['groups']))]:
        if got != want: return False, 'selftest.expected.%s %s != 源码 %s' % (k, got, want)
    cur = None
    if health and os.path.exists(health):
        h = js(health)
        schema = js(GEN + '/health.schema.json')
        miss = [f for f in schema['fields'] if f not in h]
        if miss: return False, '健康快照缺字段: ' + ', '.join(miss)
        cur = {'ok': str(h.get('verdict')), 'patches': '%s/%s' % (h.get('patches_ok'), h.get('patches_total')),
               'reflection': '%s/%s' % (h.get('reflection_ok'), h.get('reflection_total')),
               'api': '%s/%s' % (h.get('sink_ok'), h.get('sink_total')),
               'features': str(h.get('features')), 'contract': str(h.get('contract'))}
    elif log and os.path.exists(log):
        line = [l for l in tx(log).splitlines() if '[SR.SelfTest]' in l]
        if not line: return False, '日志无 [SR.SelfTest] 行'
        l = line[-1]
        def num(p):
            m = re.search(p, l)
            return m.group(1) if m else ''
        cur = {'ok': 'OK' if '[SR.SelfTest] OK' in l else 'FAIL',
               'patches': num(r'补丁=(\d+/\d+)'), 'reflection': num(r'反射=(\d+/\d+)'),
               'api': num(r'API=(\d+/\d+)'), 'features': num(r'功能=(\d+)'), 'contract': num(r'契约=v(\d+)')}
    if cur:
        if update:
            with open(GEN + '/health.actual.json', 'w', encoding='utf-8', newline='\n') as f:
                json.dump({'schema': 2, 'source': health or log, 'values': cur}, f, ensure_ascii=False, indent=2)
            return True, '运行时值已写入 health.actual.json'
        for k in ('patches', 'reflection', 'api'):
            a = cur[k].split('/')
            if len(a) != 2 or a[0] != a[1] or not a[0]: return False, '%s 未全部命中 %s' % (k, cur[k])
        if cur['reflection'].split('/')[-1] != str(exp['reflection_unique_keys']):
            return False, 'reflection 总数与声明不符'
        if cur['contract'] != str(exp['contract']): return False, 'contract 与期望不符'
        try:
            if int(cur['features']) != int(exp['features_declared']): return False, 'features 与声明不一致 (%s != %s)' % (cur['features'], exp['features_declared'])
        except ValueError:
            return False, 'features 非数字'
        if cur['ok'] != 'OK': return False, '自检结论 FAIL'
        return True, '运行时自检与生成期望一致'
    return True, '自检期望自洽（%d 个反射唯一 key）' % len(keys)


def contract_fingerprint():
    """CTR-001：唯一绑定表自洽（hash 覆盖 entries；结论写在同文件内）。"""
    fp = js(GEN + '/contract-fingerprint.json')
    h = hashlib.sha256('\n'.join(sorted(
        e['kind'] + '|' + e['signature'] + '|' + e['origin'] for e in fp['entries'])).encode()).hexdigest()
    if h != fp['hash']:
        return False, '绑定表 hash 与 entries 不一致'
    ext = [e for e in fp['entries'] if e.get('external')]
    alias = [e for e in fp['entries'] if e.get('member_alias')]
    if len(ext) != 2 or len(alias) != 1:
        return False, '待确认标记数不符: %d/%d' % (len(ext), len(alias))
    v = fp.get('verification', {})
    if v.get('verdict') != 'OK' or v.get('suspected_missing'):
        return False, '离线比对仍有未决项'
    if v.get('fingerprint_hash') != fp['hash']:
        return False, 'verification 记的 hash 已过期，跑 uch.py fingerprint --dump 重比'
    if not fp.get('bindings'):
        return False, '绑定表缺 bindings（dump 形状未落地）'
    return True, '绑定表自洽（%d 条 / 形状 %d / 0 缺失）' % (len(fp['entries']), len(fp['bindings']))


def build_graph():
    s = spec()['build']
    if js('global.json')['sdk']['version'] != s['sdk']: return False, 'global.json 与 build.sdk 不一致'
    props = tx(GEN + '/references.props')
    rsp = tx('build/rsp/sr_uch.rsp')
    csproj = tx('SR_UCH.csproj')
    for x in s['game']:
        if 'Include="%s"' % x['name'] not in props: return False, 'references.props 缺 ' + x['name']
        if x['name'] + '.dll' not in rsp: return False, 'sr_uch.rsp 缺 ' + x['name']
    for b in s['bcl']:
        if b + '.dll' not in rsp: return False, 'sr_uch.rsp 缺 ' + b
    if '<Reference Include=' in csproj: return False, 'csproj 仍有手写引用'
    if 'references.props' not in csproj: return False, 'csproj 未 Import 生成引用'
    want = sorted(compile_set())
    got = sorted(rel(x) for x in rsp_lines('build/rsp/sr_uch.rsp'))
    if want != got: return False, 'sources.manifest 与 sr_uch.rsp 源码清单不一致'
    targets = tx(GEN + '/compile.targets')
    if targets.count('<Compile Include=') != len(want): return False, 'compile.targets 条目数不符'
    bg = tx('SR_UCH/Contracts/Generated/SrBuildGraph.g.cs')
    for v in (s['sdk'], s['target_framework'], s['lang_version'], s['stub_mode']):
        if '"' + v + '"' not in bg: return False, 'SrBuildGraph 缺 ' + v
    return True, 'SDK / 引用 / 源码 / targets 四链同源（%d 个 .cs）' % len(want)


def rsp_expected():
    """单链：保留 /r: 头，只重写 .cs 源码行。"""
    m = src_manifest()
    p = 'build/rsp/sr_uch.rsp'
    head = [l for l in tx(p).replace('\r\n', '\n').splitlines() if l.startswith('/')]
    return {p: ('\n'.join(head + [win(x) for x in sorted(set(m['compile']))]) + '\n', True)}


def rsp_verify(generate=False):
    want = rsp_expected()
    bad = []
    for p, (text, bom) in sorted(want.items()):
        cur = tx(p).replace('\r\n', '\n') if os.path.exists(p) else None
        if cur != text:
            if generate:
                data = text.encode('utf-8')
                with open(p, 'wb') as f:
                    f.write((b'\xef\xbb\xbf' if bom else b'') + data)
            else:
                bad.append(p)
    stale = [p for p in glob.glob('build/rsp/**/*.rsp', recursive=True)
            if p.replace('\\', '/') not in want]
    for p in stale:
        if generate: os.remove(p)
        else: bad.append(p + ' (多余)')
    if bad:
        return False, '清单与磁盘源码不一致 %d 处: %s' % (len(bad), ', '.join(bad[:4]))
    return True, ('已生成 ' if generate else '清单一致 ') + '%d 份 rsp' % len(want)


def migration_rules():
    sep = chr(1)
    binds = set()
    for p in cs('SR_UCH') + cs('Features') + cs('Pure') + ['MainPlugin.cs']:
        for m in re.finditer(r'\.Bind(?:<[^<>()]*>)?\s*\(\s*"([^"]+)"\s*,\s*"([^"]+)"', tx(p)):
            binds.add(m.group(1) + sep + m.group(2))
    mig = tx('SR_UCH/SR.ConfigMigration.cs')
    bad = []
    for m in re.finditer(r'RemoveKeys\s*\(\s*text\s*,\s*"([^"]+)"\s*,\s*new\[\]\s*\{([^}]*)\}', mig):
        for k in re.findall(r'"([^"]+)"', m.group(2)):
            if m.group(1) + sep + k in binds: bad.append('[%s] %s' % (m.group(1), k))
    for m in re.finditer(r'MoveKeyBetweenSections\s*\(\s*text\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"', mig):
        if m.group(1) + sep + m.group(3) in binds: bad.append('[搬移] ' + m.group(3))
    if bad:
        return False, '迁移清理名单撞上活键: ' + ', '.join(bad[:5])
    return True, '迁移规则与 %d 条 Config.Bind 无冲突' % len(binds)


def sink_coverage():
    ctext = '\n'.join(tx(p) for p in cs('SR_UCH/Contracts', False))
    main = tx('MainPlugin.cs')
    names = re.findall(r'public static void (Set\w+Sinks)\s*\(', ctext)
    bad = [n for n in names if n + '(' not in main]
    if bad: return False, 'MainPlugin 未调用注入点: ' + ', '.join(bad)
    return True, '注入点全部已调用（%d 组）' % len(names)


# ---------------------------------------------------------------- 口径回归与脚本配平
# 单 DLL 后唯一的判定入口：close-all 清零、无搜索框、build/ 脚本词法配平。
def pol_check():
    ch = tx('Features/Chaos.cs')
    if 'ClearAll(true)' not in ch or 'RestoreAll()' in ch:
        return False, '关闭牌组清零链路被回退'
    r = tx('README.md')
    if 'searchable sidebar' in r or '搜索框' in r:
        return False, 'README 出现搜索框文案'
    def bal(s):
        st, i, n = [], 0, len(s)
        while i < n:
            c = s[i]
            if c == '#':
                j = s.find('\n', i); i = n if j < 0 else j; continue
            if c in '"\'':
                q = c; i += 1
                while i < n:
                    if q == '"' and s[i] == '`': i += 2; continue
                    if s[i] == q:
                        if q == '"' and i + 1 < n and s[i+1] == '"': i += 2; continue
                        i += 1; break
                    i += 1
                continue
            if c in '([{': st.append(c)
            elif c in ')]}':
                if not st: return False
                st.pop()
            i += 1
        return not st
    files = sorted(os.path.basename(f) for f in glob.glob('build/*.ps1'))
    if files != ['build.ps1', 'run-tests.ps1']:
        return False, 'build/ 出现多余/缺失的 ps1: ' + ', '.join(files)
    for f in files:
        if not bal(tx('build/' + f)):
            return False, f + ' 词法级括号不配平'
    docs = sorted(f for f in os.listdir('docs') if f.endswith('.md'))
    if docs != ['INDEX.md', 'STATUS.md']:
        return False, 'docs/ 出现冗余文档: ' + ', '.join(docs)
    return True, '清零/无搜索框/2 个 ps1/2 份文档'


# ---- 猛攻 1：把审查发现的漂移点变成可证伪断言 ----
def preset_policy():
    p = tx('Pure/PresetXmlPolicy.cs')
    m = re.search(r'internal const string RootName = "([^"]+)"', p)
    if not m: return False, 'Pure 未声明 RootName'
    root = m.group(1)
    used = re.findall(r'DocumentElement\.Name != "([^"]+)"', tx('Features/BuildingPresets.Io.cs'))
    used += re.findall(r'DocumentElement\.Name != "([^"]+)"', tx('Features/BuildingPresets.Load.cs'))
    bound = 'SR_UCH.Pure.PresetXmlPolicy.RootName' in tx('Features/BuildingPresets.Io.cs')
    if used and any(u != root for u in used): return False, '运行时写死根名 %s != Pure %s' % (used, root)
    if not bound: return False, '导入校验未绑定 Pure.RootName'
    if 'PresetXmlPolicy.ForbidsDtd' not in tx('Features/BuildingPresets.Util.cs'):
        return False, 'ParseXmlSafe 未接入 Pure 策略'
    return True, '根名 %s / 运行时已绑定 / 策略已落地' % root


def build_graph_constants():
    wf = tx('.github/workflows/guards.yml')
    jobs = re.findall(r'^  ([a-z][a-z0-9_-]*):\s*$', wf[wf.find('\njobs:'):], re.M)
    g = tx('SR_UCH/Contracts/Generated/SrBuildGraph.g.cs')
    m = re.search(r'CiJobCount = (\d+)', g)
    if not m: return False, 'SrBuildGraph 缺 CiJobCount'
    if int(m.group(1)) != len(jobs): return False, 'CiJobCount %s != workflow %d' % (m.group(1), len(jobs))
    return True, 'CiJobCount = %d' % len(jobs)


def docs_rule_count():
    d = tx('docs/INDEX.md')
    m = re.search(r'（(\d+) 条机械守卫', d)
    if not m: return False, 'INDEX 未标注规则数'
    if int(m.group(1)) != len(RULES): return False, 'INDEX %s != RULES %d' % (m.group(1), len(RULES))
    return True, 'INDEX 规则数 %d == RULES' % len(RULES)


def state_map_registered():
    g = tx('SR_UCH/Contracts/Generated/SrStateMap.g.cs')
    names = re.findall(r'^\s*"(_\w+)",?\s*$', g, re.M)
    if not names: return False, '状态图无登记项'
    allsrc = ''
    for p in glob.glob('SR_UCH/SR.*.cs'):
        allsrc += tx(p)
    orphan = [n for n in names if n not in allsrc]
    if orphan: return False, '登记了源码里不存在的字段: ' + ', '.join(orphan[:5])
    return True, '%d 个静态字段归属均有源码背书' % len(names)


def core_pure_framework():
    """SR_UCH/ 只含框架本体：零 [SrFeature]、零 ITweak 实现、零独立兼容容器文件。

    功能一律在 Features/，且特性数必须与 spec.features 逐一对账。
    """
    core = [rel(p) for p in glob.glob('SR_UCH/**/*.cs', recursive=True)
            if not rel(p).startswith('SR_UCH/Contracts/')]
    feats, tweaks = [], []
    for p in core:
        s = tx(p)
        if re.search(r'\[SrFeature\s*\(', s): feats.append(p)
        if re.search(r':\s*ITweak\b', s): tweaks.append(p)
    bad = []
    if feats: bad.append('SR_UCH/ 内出现 [SrFeature]: ' + ', '.join(feats[:3]))
    if tweaks: bad.append('SR_UCH/ 内出现 ITweak 实现: ' + ', '.join(tweaks[:3]))
    if os.path.exists('SR_UCH/SR.CmAttributes.cs'): bad.append('SR.CmAttributes.cs 复活')
    fsrc = '\n'.join(tx(p) for p in glob.glob('Features/*.cs'))
    n = len(re.findall(r'\[SrFeature\s*\(', fsrc))
    want = len(spec()['features'])
    if n != want: bad.append('Features/ 声明 %d != spec %d' % (n, want))
    if bad: return False, ' / '.join(bad)
    return True, '本体 %d 文件零功能标记 / Features/ %d 个功能与 spec 一致' % (len(core), n)


def external_policy():
    s = spec().get('external_plugins', {})
    if s.get('policy') not in ('log', 'strict'): return False, 'external policy 非法'
    c = tx('SR_UCH/Contracts/Generated/SrCaps.g.cs')
    if 'SrExternalPolicy' not in c: return False, '生成物缺 SrExternalPolicy'
    if ('Mode = "%s"' % s['policy']) not in c: return False, '生成物 policy 与 spec 不符'
    if 'SrExternalPolicy.IsAllowed' not in tx('MainPlugin.cs'): return False, '运行时未使用生成策略'
    return True, 'policy=%s / allowlist=%d / 运行时已引用' % (s['policy'], len(s.get('allowlist', [])))


def pure_runtime_binding():
    want = set(spec()['pure']['runtime'])
    got = set()
    for p in glob.glob('**/*.cs', recursive=True):
        if p.startswith('Pure/') or p.startswith('tests/'):
            continue
        t = tx(p)
        for n in want:
            if n + '.' in t or ('using SR_UCH.Pure;' in t and re.search(r'\b' + n + r'\b', t)):
                got.add(n)
    for p in glob.glob('**/*.cs', recursive=True):
        if p.startswith('Pure/') or p.startswith('tests/'):
            continue
        for m in re.finditer(r'SR_UCH\.Pure\.(\w+)\.', tx(p)):
            got.add(m.group(1))
    if got != want:
        return False, '运行时 Pure 引用 %s != 声明 %s' % (sorted(got), sorted(want))
    return True, '%d 个 Pure 类型接入运行时且无额外引用' % len(want)

def error_code_stability():
    """ERR-001：事件码内容稳定、唯一、且生成物与基线一一对应。"""
    d = js(GEN + '/error-sites.generated.json')
    sites = d.get('sites', [])
    if not sites:
        return False, 'error-sites 无站点'
    codes = [x['code'] for x in sites]
    if len(codes) != len(set(codes)):
        dup = [c for c in set(codes) if codes.count(c) > 1]
        return False, '事件码重复: ' + ', '.join(dup[:3])
    pat = re.compile(r'^SR-H-[A-Z0-9_]+-[A-Z]+-[0-9A-F]{8}(?:-\d+)?$')
    bad = [c for c in codes if not pat.match(c)]
    if bad:
        return False, '事件码格式不合法（疑退回序号制）: ' + ', '.join(bad[:3])
    g = tx('SR_UCH/Contracts/Generated/SrCatchSites.g.cs')
    gcnt = len(re.findall(r'new Site\(', g))
    if gcnt != len(sites):
        return False, 'SrCatchSites.g.cs 站点数 %d != 基线 %d' % (gcnt, len(sites))
    return True, '%d 个内容稳定事件码 / 0 重复 / 生成物一致' % len(sites)


def contract_freshness():
    """CTR-002：结论自洽；设了 UCH_DUMP 时真比对 dump 未变（变了必须重比）。"""
    fp = js(GEN + '/contract-fingerprint.json')
    v = fp.get('verification', {})
    if v.get('verdict') != 'OK' or v.get('suspected_missing'):
        return False, '绑定结论未决：verdict=%s / 缺失 %d' % (v.get('verdict'), len(v.get('suspected_missing', [])))
    if v.get('fingerprint_hash') != fp['hash']:
        return False, 'verification 记的指纹哈希已过期，跑 uch.py fingerprint --dump 重比'
    if not v.get('dump_sha256'):
        return False, 'verification 缺 dump_sha256'
    dump = os.environ.get('UCH_DUMP')
    if dump and os.path.exists(dump):
        cur = hashlib.sha256(rb(dump)).hexdigest()
        if cur != v['dump_sha256']:
            return False, 'dump 已更新（%s…），跑 uch.py fingerprint --dump %s 重比后再提交' % (cur[:12], dump)
        return True, '绑定 OK / dump 一致（%s…）/ 变更 %d' % (cur[:12], len(fp.get('changes', [])))
    return True, '绑定 OK / 未设 UCH_DUMP，跳过新鲜度比对（换 dump 后请重跑 fingerprint）'



RULES = [
    ('TXT-001', 'text', 'API 棘轮', api_surface),
    ('TXT-002', 'text', '契约层边界', contract_boundary),
    ('TXT-003', 'text', '功能层边界', feature_leak),
    ('TXT-004', 'text', 'rsp 精确覆盖', rsp_coverage),
    ('TXT-005', 'text', 'UTF-8 合法', encoding_utf8),
    ('TXT-006', 'text', 'LocDesc 格式', desc_format),
    ('TXT-007', 'text', '源码编码规范', cs_encoding),
    ('TXT-008', 'text', '裸 catch', bare_catch),
    ('TXT-009', 'text', 'SR 分区字段', region_fields),
    ('TXT-010', 'text', '静默 catch 棘轮', silent_catch),
    ('TXT-011', 'text', '缩进统一', indent),
    ('TXT-012', 'text', '悬浮条目统一入口', tip_composer),
    ('TXT-015', 'text', '文档脚本引用', doc_scripts),
    ('TXT-016', 'text', 'API level', api_level),
    ('TXT-017', 'text', '注释纪律', comment_discipline),
    ('TXT-018', 'text', '配置登记单一入口', config_entry_single_source),
    ('SCH-003', 'schema', '本地化覆盖', loc_coverage),
    ('SLT-001', 'selftest', '自检期望自洽', lambda: selftest_expected()),
    ('CTR-001', 'contract', '契约指纹', contract_fingerprint),
    ('BG-001', 'build-graph', '构建图同源', build_graph),
    ('RSP-001', 'rsp', '构建清单', lambda: rsp_verify(False)),
    ('BC-001', 'build-checks', '迁移清理名单', migration_rules),
    ('BC-002', 'build-checks', '注入点覆盖', sink_coverage),
    ('POL-001', 'text', '口径回归与脚本配平', pol_check),
    ('CORE-001', 'schema', 'SR_UCH 仅本体', core_pure_framework),
    ('PUR-001', 'schema', '预设 XML 策略落地', preset_policy),
    ('BG-002', 'build-graph', '构建图常量与 workflow 一致', build_graph_constants),
    ('DOC-002', 'text', '文档规则数与 RULES 一致', docs_rule_count),
    ('STA-001', 'schema', '共享静态字段归属有源码背书', state_map_registered),
    ('EXT-001', 'schema', '外部 DLL 策略生成且被引用', external_policy),
    ('PUR-002', 'schema', 'Pure 运行时接入清单一致', pure_runtime_binding),
    ('ERR-001', 'schema', '事件码内容稳定且唯一', error_code_stability),
    ('CTR-002', 'contract', '契约指纹复验新鲜度', contract_freshness),
]




SUITES = ['text', 'schema', 'selftest', 'contract', 'build-graph', 'rsp', 'build-checks', 'all']


def run_suite(suite, as_json=False, generate=False, log=None, health=None, update=False):
    if suite not in SUITES:
        print('未知 suite: ' + suite)
        return 2
    items = [r for r in RULES if suite == 'all' or r[1] == suite]
    results = []
    for rid, _, desc, fn in items:
        try:
            if rid == 'SLT-001' and (log or health):
                ok, detail = selftest_expected(log, health, update)
            elif rid == 'RSP-001' and generate:
                ok, detail = rsp_verify(True)
            else:
                ok, detail = fn()
        except Exception as e:
            ok, detail = False, '规则异常: %s: %s' % (type(e).__name__, e)
        results.append({'id': rid, 'desc': desc, 'ok': bool(ok), 'detail': str(detail)})
    fails = [r for r in results if not r['ok']]
    if as_json:
        print(json.dumps({'suite': suite, 'total': len(results), 'failed': len(fails),
                          'results': results}, ensure_ascii=False, indent=2))
    else:
        print('== SR_UCH 机械守卫 [%s]（唯一实现 tools/uch_rules.py）==' % suite)
        for r in results:
            print('  [%s] %s %s' % (' OK ' if r['ok'] else 'FAIL', r['id'], r['detail']))
        print('== %d 项 / %d FAIL ==' % (len(results), len(fails)))
    return 1 if fails else 0


# ---------------------------------------------------------------- 离线预检
def precompile():
    s = spec(); b = s['build']
    issues = []
    print('== uch.py precompile（第一次真编译前离线预检） ==')
    for must in ['build/rsp/sr_uch.rsp', GEN + '/compile.targets', GEN + '/references.props']:
        if not os.path.exists(must): issues.append('编译输入缺失（fresh clone 无法直接 build）: ' + must)
    if js('global.json')['sdk']['version'] != b['sdk']:
        issues.append('global.json SDK != %s' % b['sdk'])
    props = tx(GEN + '/references.props')
    for x in b['game']:
        if 'Include="%s"' % x['name'] not in props: issues.append('references.props 缺 ' + x['name'])
    rsp = tx('build/rsp/sr_uch.rsp')
    for x in b['game']:
        if x['name'] + '.dll' not in rsp: issues.append('sr_uch.rsp 缺 ' + x['name'])
    for x in b['bcl']:
        if x + '.dll' not in rsp: issues.append('sr_uch.rsp 缺 ' + x)
    csproj = tx('SR_UCH.csproj')
    if '<Reference Include=' in csproj: issues.append('csproj 仍有手写 <Reference>')
    if 'compile.targets' not in csproj: issues.append('csproj 未 Import compile.targets')
    targets = tx(GEN + '/compile.targets')
    base = os.path.normpath(os.path.join(GEN, '..', '..', '..'))
    for m in re.finditer(r'<Compile Include="\$\(MSBuildThisFileDirectory\)(.*?)"', targets):
        if not os.path.exists(os.path.normpath(os.path.join(GEN, m.group(1)))) and not os.path.exists(
                os.path.normpath(os.path.join(base, m.group(1).lstrip('/')))):
            issues.append('compile.targets 路径不存在: ' + m.group(1))
    for p in compile_set():
        if not os.path.exists(p): issues.append('源码不存在: ' + p)
    for x in s['toolchain']['required_generated']:
        if not os.path.exists(GEN + '/' + x): issues.append('缺生成物: ' + x)
    print('  [ OK ] SDK / 引用三链 / 源码 / 生成物清单对账完成' if not issues else
          '  [FAIL] 离线项 %d 条' % len(issues))
    managed = os.environ.get('UCH_MANAGED') or os.environ.get('UCH_MANAGED_DIR')
    if managed:
        dlls = [x['name'] + '.dll' for x in b['game']] + ['BepInEx.dll', '0Harmony.dll']
        miss = [d for d in dlls if not os.path.exists(os.path.join(managed, d))]
        if miss: issues.append('UCH_MANAGED 缺 %d 个 DLL: %s' % (len(miss), ', '.join(miss[:5])))
        else: print('  [ OK ] UCH_MANAGED 下 %d 个参考 DLL 齐全' % len(dlls))
    else:
        print('  [SKIP] 未配置 UCH_MANAGED：全源码 IL 编译仍待本地')
    for i in issues: print('  [FAIL] ' + i)
    print('== 预判：%s ==' % ('第一次 dotnet build 应通过' if not issues else '先修上述 %d 条再编译' % len(issues)))
    return 1 if issues else 0


def main():
    import argparse
    ap = argparse.ArgumentParser(description='SR_UCH 唯一规则实现')
    ap.add_argument('action', choices=['run', 'list', 'precompile'])
    ap.add_argument('--suite', default='all')
    ap.add_argument('--json', action='store_true')
    ap.add_argument('--generate', action='store_true')
    ap.add_argument('--log'); ap.add_argument('--health'); ap.add_argument('--update', action='store_true')
    a = ap.parse_args()
    if a.action == 'list':
        for rid, suite, desc, _ in RULES: print('%s\t%s\t%s' % (rid, suite, desc))
        return 0
    if a.action == 'run':
        return run_suite(a.suite, a.json, a.generate, a.log, a.health, a.update)
    if a.action == 'precompile': return precompile()
    return 0


if __name__ == '__main__':
    sys.exit(main())
