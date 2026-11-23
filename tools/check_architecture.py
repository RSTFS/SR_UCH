# -*- coding: utf-8 -*-
"""源码分层与依赖方向守卫。零外部依赖，CI 与 tools/spec_engine.py 共用。

用法：
  python3 tools/check_architecture.py              # 校验：违规或派生过期即 exit 1
  python3 tools/check_architecture.py --generate   # 重新派生 json（文档统一由 uch.py docs 生成）
"""
import os, re, json, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)
SPEC = 'build/specs/features.spec.json'
OUT = 'build/specs/generated/architecture.generated.json'
DOC = 'docs/INDEX.md'


def rd(p):
    return open(p, 'rb').read().decode('utf-8-sig').replace('\r\n', '\n')


def strip_comments_strings(t):
    """词法级剥离：注释与字符串里的字样不算引用，避免误报。"""
    out = []
    i, n = 0, len(t)
    while i < n:
        c = t[i]
        if c == '/' and i + 1 < n and t[i + 1] == '/':
            j = t.find('\n', i)
            i = n if j < 0 else j
            continue
        if c == '/' and i + 1 < n and t[i + 1] == '*':
            j = t.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == '@' and i + 1 < n and t[i + 1] == '"':
            i += 2
            while i < n:
                if t[i] == '"':
                    if i + 1 < n and t[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            out.append(' ')
            continue
        if c == '"' or c == "'":
            q = c
            i += 1
            while i < n:
                if t[i] == '\\':
                    i += 2
                    continue
                if t[i] == q:
                    i += 1
                    break
                i += 1
            out.append(' ')
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def layer_of(rel):
    if rel.startswith('Pure/'):
        return 'pure'
    if rel.startswith('SR_UCH/Contracts/'):
        return 'contracts'
    if rel.startswith('SR_UCH/'):
        return 'core'
    if rel.startswith('Features/'):
        return 'features'
    if rel.startswith('tests/') or rel == 'MainPlugin.cs':
        return 'bootstrap'
    return ''


# 构建中间产物目录：不是源码，绝不能进分层统计（obj 里是自动生成的 AssemblyAttributes.cs）。
PRUNE_DIRS = {'.git', 'obj', 'bin', 'build', 'docs', '__pycache__', 'node_modules', '.vs'}


def collect():
    files = []
    for dp, dn, fn in os.walk('.'):
        dn[:] = [d for d in dn if d not in PRUNE_DIRS]
        parts = dp.replace('\\', '/').split('/')
        if any(seg in PRUNE_DIRS for seg in parts if seg and seg != '.'):
            continue
        for f in fn:
            if f.endswith('.cs'):
                files.append(os.path.join(dp, f)[2:].replace('\\', '/'))
    return sorted(files)


def feature_types(files):
    """由 Features/ 源码派生真实类型集；规则据此检查，不猜命名空间前缀。"""
    names = set()
    for f in files:
        if not f.startswith('Features/'):
            continue
        code = strip_comments_strings(rd(f))
        for x in re.findall(r'\bclass\s+([A-Za-z_]\w*)', code):
            names.add(x)
    own = set()
    for f in files:
        if f.startswith('SR_UCH/Contracts/'):
            for x in re.findall(r'\bclass\s+([A-Za-z_]\w*)', strip_comments_strings(rd(f))):
                own.add(x)
    return sorted(names - own)


def compute(spec):
    files = collect()
    by_layer = {l['id']: [] for l in spec['architecture']['layers']}
    unlayered, violations = [], []
    generated = sorted(f for f in files if f.endswith('.g.cs'))
    ftypes = feature_types(files)
    for f in files:
        lay = layer_of(f)
        if not lay:
            unlayered.append(f)
            continue
        by_layer[lay].append(f)
        code = strip_comments_strings(rd(f))
        if lay == 'pure':
            for bad in ['UnityEngine', 'BepInEx', 'HarmonyLib']:
                if re.search(r'\busing\s+' + bad + r'\b', code) or re.search(r'\b' + bad + r'\.', code):
                    violations.append({'rule': 'pure-no-engine', 'file': f, 'symbol': bad})
            if 'SR_UCH.Tweaks' in code:
                violations.append({'rule': 'pure-no-engine', 'file': f, 'symbol': 'SR_UCH.Tweaks'})
        elif lay == 'contracts':
            if 'SR_UCH.Tweaks' in code:
                violations.append({'rule': 'contracts-no-core', 'file': f, 'symbol': 'SR_UCH.Tweaks'})
            for t in ftypes:
                if re.search(r'\b' + re.escape(t) + r'\s*\.', code) or re.search(r'\bnew\s+' + re.escape(t) + r'\s*\(', code):
                    violations.append({'rule': 'contracts-no-features', 'file': f, 'symbol': t})
        elif lay == 'core':
            for t in ftypes:
                if re.search(r'\b' + re.escape(t) + r'\s*\.', code) or re.search(r'\bnew\s+' + re.escape(t) + r'\s*\(', code):
                    violations.append({'rule': 'core-no-features', 'file': f, 'symbol': t})
    for f in generated:
        if '/Generated/' not in f:
            violations.append({'rule': 'generated-separated', 'file': f, 'symbol': '.g.cs 未归位'})
    if unlayered:
        violations.append({'rule': 'no-unlayered', 'file': ','.join(unlayered), 'symbol': '未归属文件'})
    return {
        'schema': 1,
        'generated_by': 'tools/check_architecture.py',
        'layers': [{'id': l['id'], 'dirs': l['dirs'], 'forbid': l['forbid'],
                    'desc': l['desc'], 'files': len(by_layer[l['id']])}
                   for l in spec['architecture']['layers']],
        'total_files': len(files),
        'feature_types': ftypes,
        'generated_files': generated,
        'unlayered': unlayered,
        'violations': violations,
        'verdict': 'OK' if not violations else 'FAIL',
    }


def render_doc(d):
    L = ['# 源码结构', '',
         '> 由 `tools/check_architecture.py --generate` 派生，勿手改。层次与依赖规则的唯一声明在 '
         '`build/specs/features.spec.json` 的 `architecture` 段。', '',
         '共 %d 个 `.cs`，分 %d 层，依赖方向自上而下单向。' % (d['total_files'], len(d['layers'])), '',
         '| 层 | 目录 | 文件数 | 禁止引用 |', '|---|---|---|---|']
    for l in d['layers']:
        L.append('| %s | %s | %d | %s |' % (l['id'], ', '.join('`%s`' % x for x in l['dirs']),
                                            l['files'], ', '.join('`%s`' % x for x in l['forbid']) or '—'))
    L += ['', '## 规则', '']
    for r in ['pure-no-engine', 'contracts-no-core', 'contracts-no-features',
              'core-no-features', 'generated-separated', 'no-unlayered']:
        L.append('- `%s`：error；由守卫按词法剥离后的代码检查' % r)
    L += ['', '## 生成物', '',
          '所有 `.g.cs` 必须位于 `*/Generated/`，与手写源码分开：', '']
    L += ['- `%s`' % x for x in d['generated_files']]
    L += ['', '## 结论', '', '`%s`，违规 %d 条，未归属 %d 个。' %
          (d['verdict'], len(d['violations']), len(d['unlayered'])), '',
          '修改层次或新增源码后跑 `--generate` 重写本页与 `architecture.generated.json`；'
          'CI 的 `spec-verify` 会校验两者与磁盘一致。', '']
    return '\n'.join(L)


def main():
    spec = json.loads(rd(SPEC))
    got = compute(spec)
    gen = '--generate' in sys.argv
    if gen:
        open(OUT, 'wb').write((json.dumps(got, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))
        print('派生 %s，verdict=%s' % (OUT, got['verdict']))
    else:
        if not os.path.exists(OUT):
            print('FAIL: 缺 %s（跑 --generate）' % OUT)
            return 1
        want = json.loads(rd(OUT))
        if want != got:
            diff = [k for k in got if want.get(k) != got[k]]
            print('FAIL: 派生产物与磁盘不一致: %s（跑 --generate）' % ', '.join(diff))
            return 1
    for v in got['violations']:
        print('VIOLATION %s: %s -> %s' % (v['rule'], v['file'], v['symbol']))
    if got['violations']:
        return 1
    print('OK: %d 个 .cs / %d 层 / %d 个生成物归位 / 0 违规' %
          (got['total_files'], len(got['layers']), len(got['generated_files'])))
    return 0


if __name__ == '__main__':
    sys.exit(main())

# 本脚本由 tools/uch.py 统一调度；保留独立入口供本地单机排查。
