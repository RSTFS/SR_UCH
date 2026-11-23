# -*- coding: utf-8 -*-
"""声明式页面模型守卫。零外部依赖，CI（spec-verify）与本地共用。

用法：
  python3 tools/check_uimodel.py               # 校验：任一 DIFF 即 exit 1
  python3 tools/check_uimodel.py --generate    # 由声明重写 SrPageModel.cs 与派生快照

对账的四类事实：
  1) 声明 ↔ 源码注册点（页面集合、renderer、order、file）
  2) 声明 ↔ 生成物（SrPageModel.cs 由本守卫生成；spec_engine.py 统一调度）
  3) 结构不变量（唯一 id / 合法 kind / 合法 layer / 合法 policy / 无嵌套滚动）
  4) 绘制纪律（页面级滚动区只在主窗口、无硬编码分派、PolicyFor 被调用）
"""
import io, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)

SPEC = 'build/specs/features.spec.json'
PAGE_MODEL_OUT = 'SR_UCH/Contracts/SrPageModel.cs'
GENERATED = 'build/specs/generated/ui-model.generated.json'

fails, passes = [], []


def rd(p):
    return io.open(p, 'r', encoding='utf-8-sig').read().replace('\r\n', '\n')


def wr(p, s):
    io.open(p, 'wb').write(s.encode('utf-8'))


def ok(name, cond, detail=''):
    if cond:
        passes.append(name)
        print('  [ OK ] ' + name + ((' — ' + detail) if detail else ''))
    else:
        fails.append(name + ((' — ' + detail) if detail else ''))
        print('[DIFF] ' + name + ((' — ' + detail) if detail else ''))


def cs_files():
    out = []
    for dp, dn, fn in os.walk('.'):
        if 'tests' in dp or 'Generated' in dp:
            continue
        for f in fn:
            if f.endswith('.cs'):
                out.append(os.path.join(dp, f).replace('\\', '/'))
    return sorted(out)


def split_args(s):
    out, buf, q, esc = [], '', None, False
    for ch in s:
        if esc:
            buf += ch; esc = False; continue
        if ch == '\\' and q:
            buf += ch; esc = True; continue
        if q:
            buf += ch
            if ch == q:
                q = None
            continue
        if ch in '"\'':
            q = ch; buf += ch; continue
        if ch == ',':
            out.append(buf.strip()); buf = ''; continue
        buf += ch
    if buf.strip():
        out.append(buf.strip())
    return out


def resolve(tok, mapping):
    tok = tok.strip()
    m = re.match(r'^"([^"]*)"$', tok)
    return m.group(1) if m else mapping.get(tok, tok)


def scan_registrations():
    """与 build/specs/ui.pages.json 的派生算法同源：重扫一遍做双向对账。"""
    pages = []
    for p in cs_files():
        s = rd(p)
        s = '\n'.join(('' if l.lstrip().startswith('//') else l) for l in s.splitlines())
        mapping = {}
        for m in re.finditer(r'\b(?:const|static\s+readonly|static)\s+string\s+(\w+)\s*=\s*"([^"]+)"', s):
            mapping[m.group(1)] = m.group(2)
        for m in re.finditer(r'\b(\w+)\s*=\s*"([^"]+)"\s*;', s):
            mapping.setdefault(m.group(1), m.group(2))
        for m in re.finditer(r'(?:SRApi|SR)\.RegisterPage\s*\(([^;\n]*?)\)\s*;', s):
            a = split_args(m.group(1))
            if len(a) == 2:
                sec, tab, order, renderer = resolve(a[0], mapping), 'main', 0, a[1].strip()
            elif len(a) >= 6:
                sec = resolve(a[0], mapping)
                tab = a[1].strip().strip('"')
                try:
                    order = int(a[4].strip())
                except ValueError:
                    order = 0
                renderer = a[5].strip()
            else:
                continue
            if not re.match(r'^[A-Za-z_][\w.]*$', renderer):
                continue
            pages.append({'id': sec + '/' + tab, 'section': sec, 'tab': tab,
                          'order': order, 'renderer': renderer, 'file': p.lstrip('./')})
    seen, uniq = set(), []
    for x in pages:
        if x['id'] not in seen:
            seen.add(x['id']); uniq.append(x)
    return uniq


def section_policies(decl):
    agg = {}
    for p in decl['pages']:
        agg.setdefault(p['section'], set()).add(p['policy'])
    bad = {k: v for k, v in agg.items() if len(v) != 1}
    ids = sorted(agg.keys(), key=lambda x: (-len(x), x))
    return ids, [sorted(agg[i])[0] for i in ids], bad


def page_model_text(spec, decl):
    ids, pol, _ = section_policies(decl)
    L = [
        '// <page-model:begin> 由 tools/spec_engine.py 重写，勿手改。',
        '// 注意事项：页级滚动策略由 build/specs/ui.pages.json 派生；换页行为只看这里的常量。',
        'namespace SR_UCH {',
        '    internal static class SrPageModel {',
        '        internal const string ScrollRestore = "' + spec['ui']['scroll_restore'] + '";',
        '        internal const string WheelPositive = "' + spec['ui']['wheel_positive'] + '";',
        '        internal static readonly string[] PageIds = new string[] {',
    ]
    for i in ids:
        L.append('            "' + i + '",')
    L += ['        };', '        internal static readonly string[] Policies = new string[] {']
    for p in pol:
        L.append('            "' + p + '",')
    L += [
        '        };',
        '        internal static string PolicyFor(string pageKey) {',
        '            if (string.IsNullOrEmpty(pageKey)) return ScrollRestore;',
        '            for (int i = 0; i < PageIds.Length; i++) {',
        '                if (pageKey.IndexOf(PageIds[i], System.StringComparison.OrdinalIgnoreCase) >= 0 && i < Policies.Length) return Policies[i];',
        '            }',
        '            return ScrollRestore;',
        '        }',
        '    }',
        '}',
        '// <page-model:end>',
    ]
    return '\n'.join(L) + '\n'


def generated_obj(decl, ids, pol):
    return {
        'schema': 1,
        'source': 'build/specs/ui.pages.json',
        'page_count': len(decl['pages']),
        'section_count': len(ids),
        'batch_counts': {str(b): sum(1 for p in decl['pages'] if p['batch'] == b) for b in (1, 2, 3)},
        'sections': ids,
        'policies': pol,
        'traces': {p['id']: '|'.join(n['kind'] + ':' + n['id'] for n in p['nodes'])
                   for p in decl['pages']},
        'note': '派生快照；实机 A/B 时以 traces 为期望轨迹。',
    }


def main():
    gen = '--generate' in sys.argv
    print('== 声明式页面模型守卫 ==')
    spec = json.loads(rd(SPEC))
    rel = spec['ui'].get('page_model')
    ok('spec 登记页面声明', bool(rel), str(rel))
    if not rel:
        return finish()
    decl = json.loads(rd(rel))
    ok('声明 schema', decl.get('schema') == 1)

    rules = decl['rules']
    allowed_pol = set(rules['scroll_policies'])
    allowed_layer = set(rules['layers'])
    allowed_kind = {'page', 'header', 'tabs', 'sections', 'rows', 'footer',
                    'toolbar', 'table', 'viewport', 'cards', 'verdict', 'scroll',
                    'row', 'control'}

    # 1) 声明 ↔ 源码注册点
    scanned = scan_registrations()
    want = {(p['id'], p['section'], p['renderer'], p['order'], p['file']) for p in scanned}
    got = {(p['id'], p['section'], p['renderer'], p['order'], p['file']) for p in decl['pages']}
    ok('页面集合与源码注册点一致', want == got,
       '源码 %d / 声明 %d' % (len(want), len(got)))
    if want != got:
        for x in sorted(want ^ got):
            print('       差异: ' + str(x))
    renderers = set()
    for p in cs_files():
        for m in re.finditer(r'\bvoid\s+(\w+)\s*\(', rd(p)):
            renderers.add(m.group(1))
    missing = sorted({p['renderer'] for p in decl['pages']
                      if p['renderer'].split('.')[-1] not in renderers})
    ok('每页 renderer 均存在', not missing, ','.join(missing[:6]))

    # 2) 结构不变量
    ids = [p['id'] for p in decl['pages']]
    ok('页 id 全局唯一', len(ids) == len(set(ids)))
    node_ids, bad_kind, bad_layer, bad_policy, nested = [], [], [], [], []
    for p in decl['pages']:
        if p['policy'] not in allowed_pol:
            bad_policy.append(p['id'])
        if p['layer'] not in allowed_layer:
            bad_layer.append(p['id'])
        if p['batch'] not in (1, 2, 3):
            bad_kind.append(p['id'] + ':batch')
        kinds = [n['kind'] for n in p['nodes']]
        if not kinds or kinds[0] != 'page':
            bad_kind.append(p['id'] + ':root')
        for n in p['nodes']:
            node_ids.append(n['id'])
            if n['kind'] not in allowed_kind:
                bad_kind.append(n['id'])
        if len([k for k in kinds if k == 'scroll']) > 1:
            nested.append(p['id'])
    ok('节点 kind 合法', not bad_kind, ','.join(bad_kind[:6]))
    ok('layer 合法', not bad_layer, ','.join(bad_layer[:6]))
    ok('policy 合法', not bad_policy, ','.join(bad_policy[:6]))
    ok('节点 id 全局唯一', len(node_ids) == len(set(node_ids)))
    ok('无嵌套滚动节点', not nested, ','.join(nested))
    ok('三批均有页面', all(any(p['batch'] == b for p in decl['pages']) for b in (1, 2, 3)))
    sids, spol, sbad = section_policies(decl)
    ok('同栏目策略一致', not sbad, ','.join(sorted(sbad.keys())))
    ok('栏目按长度降序（防子串错配）',
       all(len(sids[i]) >= len(sids[i + 1]) for i in range(len(sids) - 1)))
    ok('滚动策略与 spec 一致',
       all(p in (spec['ui']['scroll_restore'],) for p in spol))

    # 3) 绘制纪律
    sites = []
    for p in cs_files():
        for m in re.finditer(r'GUILayout\.BeginScrollView\s*\(', rd(p)):
            sites.append(p)
    ok('页面级滚动区只在主窗口',
       all(s.lstrip('./') == rules['root_scroll_file'] for s in sites),
       ','.join(sorted(set(s.lstrip('./') for s in sites))))
    win = rd('SR_UCH/SR.Window.cs')
    ok('主窗口走声明式策略', 'SrPageModel.PolicyFor' in win)
    hard = re.findall(r'if\s*\(\s*_selectedInternalSection\s*==\s*"', win)
    ok('无硬编码 section 分派', not hard, '%d 处' % len(hard))
    ok('滚动注释与实现一致',
       '换页必须复位滚动位置' not in win and '换页回到顶部' not in win)
    ok('双轨：绘制路径未切换', 'UiShadow' not in win and 'UiModel.Render' not in win)

    # 4) 生成物一致
    want_cs = page_model_text(spec, decl)
    if gen:
        wr(PAGE_MODEL_OUT, want_cs)
        wr(GENERATED, json.dumps(generated_obj(decl, sids, spol),
                                 ensure_ascii=False, indent=2, sort_keys=True) + '\n')
        print('  [INFO] 已写回 SrPageModel.cs 与 ui-model.generated.json')
    ok('SrPageModel.cs 与声明逐字一致', rd(PAGE_MODEL_OUT) == want_cs)
    ok('派生快照与声明一致',
       json.loads(rd(GENERATED)) == generated_obj(decl, sids, spol))
    eng = rd('tools/spec_engine.py')
    ok('单一引擎调度页面守卫（读同一份声明）',
       'check_uimodel.py' in eng and '--generate' in eng and 'ui.pages.json' in rd('tools/check_uimodel.py'))
    ok('页面模板含 Policies 查表', 'Policies.Length' in rd(PAGE_MODEL_OUT))
    ci = rd('.github/workflows/guards.yml')
    _entry = json.loads(rd(SPEC))['toolchain']['ci_entry_command']
    ok('CI 经统一入口调度页面守卫', _entry in ci and 'check_uimodel.py' in rd('tools/uch.py'))
    ok('CI job 数与 facts 一致',
       ci.count('    runs-on:') == json.loads(rd('build/specs/generated/facts.generated.json'))['ci_jobs'])
    ok('文档入口齐备', os.path.isfile('docs/INDEX.md'))
    pt = rd('tests/PureTests.cs')
    ok('纯函数断言已接入', 'UiNodeRules.Validate' in pt and 'UiShadow' in pt)
    for f in ('Pure/UiNode.cs', 'Pure/UiTrace.cs'):
        s = rd(f)
        ok('纯函数层无引擎依赖: ' + f,
           not re.search(r'\b(UnityEngine|BepInEx|HarmonyLib)\b', s))
    return finish()


def finish():
    print('\n[ui-model] PASS %d / DIFF %d' % (len(passes), len(fails)))
    if fails:
        for f in fails:
            print('  - ' + f)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())

# 本脚本由 tools/uch.py 统一调度；保留独立入口供本地单机排查。
