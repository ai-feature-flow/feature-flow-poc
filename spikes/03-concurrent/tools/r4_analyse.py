#!/usr/bin/env python3
"""R4 analysis for Spike 3 (docs/17 6.3, 6.4). Rubric and thresholds are fixed
here before any series runs; they are the pilot's (pilot/pilot-v2.py).

Usage: r4_analyse.py <results dir>   (reads r4-*.json, writes r4-summary.json
and r4-audit.md beside them)
"""
import glob, json, math, os, re, sys

CASE = re.compile(r'case|lower|ToLower|normalis|normaliz|Ordinal|IgnoreCase', re.I)
LOOK = re.compile(r'FindByName|by-name|lookup|look up|look-up', re.I)
FAIL = re.compile(r"cannot|can't|not found|404|miss|fail|returns? null|no longer|never|break|won't", re.I)


def field(f, k):
    return f.get(k) or f.get(k[0].upper() + k[1:]) or ''


def detects(f):
    t = ' '.join(str(field(f, k)) for k in ('rule', 'symbol', 'snippet', 'message'))
    return bool(CASE.search(t) and LOOK.search(t) and FAIL.search(t))


def wilson(k, n, z=1.959964):
    if n == 0:
        return (0.0, 1.0)
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return (max(0.0, c - h), min(1.0, c + h))


def fisher_greater(a, n1, c, n2):
    """One-sided Fisher exact: P(X >= a) for group-1 successes, margins fixed."""
    k = a + c
    n = n1 + n2

    def hyp(x):
        return math.comb(n1, x) * math.comb(n2, k - x) / math.comb(n, k)

    return sum(hyp(x) for x in range(a, min(n1, k) + 1))


# docs/17 6.4, fixed in advance.
D_EPIC_MIN = 0.5      # d_epic >= 10/20
D_TICKET_MAX = 0.1    # d_A, d_B <= 2/20
POS_MIN = 0.8         # Pos >= 4/5
F_MAX = 0.1           # f <= 2/20
P_MAX = 0.05


def main(d):
    series = {}
    audit = ['# R4 audit: every finding, with the rubric verdict', '']
    for path in sorted(glob.glob(os.path.join(d, 'r4-*.json'))):
        if path.endswith('r4-summary.json'):
            continue
        r = json.load(open(path))
        ok = [x for x in r['results'] if x.get('ok')]
        det = [any(detects(f) for f in x['findings']) for x in ok]
        hashes = sorted({x.get('InputHash') or x.get('inputHash') for x in ok})
        cost = sum((x.get('CostUsd') or x.get('costUsd') or 0) for x in ok)
        series[r['series']] = dict(k=sum(det), n=len(ok), failed=len(r['results']) - len(ok),
                                   ci=wilson(sum(det), len(ok)), input_hashes=len(hashes),
                                   findings_per_pass=[len(x['findings']) for x in ok], cost_usd=round(cost, 4),
                                   spec=r['spec'], diff=r['diff'])
        audit.append(f"## {r['series']} ({r['diff']}, spec {r['spec']}): {sum(det)}/{len(ok)}")
        audit.append('')
        for x in ok:
            for f in x['findings']:
                audit.append(f"- pass {x['pass']} **{'DETECTS' if detects(f) else 'no'}** "
                             f"`{field(f, 'rule')}` {field(f, 'symbol')}: {str(field(f, 'message')).strip()}")
            if not x['findings']:
                audit.append(f"- pass {x['pass']}: no findings")
        audit.append('')

    def get(name):
        return series.get(name, dict(k=0, n=0))

    e, a, b, pos, neg = get('I-epic'), get('I-A'), get('I-B'), get('Pos'), get('Neg')
    out = dict(series=series)
    if e['n'] and a['n'] and b['n']:
        out['fisher_epic_vs_A'] = fisher_greater(e['k'], e['n'], a['k'], a['n'])
        out['fisher_epic_vs_B'] = fisher_greater(e['k'], e['n'], b['k'], b['n'])
    if e['n']:
        de = e['k'] / e['n']
        out['two_of_three_keep_d_epic'] = 3 * de ** 2 - 2 * de ** 3
    if neg['n']:
        f = neg['k'] / neg['n']
        out['two_of_three_keep_f'] = 3 * f ** 2 - 2 * f ** 3
    c = out['conditions'] = dict(
        controls_valid=pos['n'] > 0 and neg['n'] > 0 and pos['k'] >= POS_MIN * pos['n'] and neg['k'] <= F_MAX * neg['n'],
        epic_detects=e['n'] > 0 and e['k'] >= D_EPIC_MIN * e['n'],
        tickets_miss=a['n'] > 0 and b['n'] > 0 and a['k'] <= D_TICKET_MAX * a['n'] and b['k'] <= D_TICKET_MAX * b['n'],
        rates_differ=out.get('fisher_epic_vs_A', 1) < P_MAX and out.get('fisher_epic_vs_B', 1) < P_MAX,
    )
    out['E2'] = ('inconclusive (controls failed)' if not c['controls_valid']
                 else 'met' if all(c.values()) else 'not met')
    json.dump(out, open(os.path.join(d, 'r4-summary.json'), 'w'), indent=1)
    open(os.path.join(d, 'r4-audit.md'), 'w').write('\n'.join(audit) + '\n')
    for name, s in series.items():
        print(f"{name:7s} {s['k']:2d}/{s['n']:2d}  Wilson [{s['ci'][0]:.2f}, {s['ci'][1]:.2f}]  "
              f"hashes={s['input_hashes']} failed={s['failed']} cost=${s['cost_usd']}")
    for k in ('fisher_epic_vs_A', 'fisher_epic_vs_B', 'two_of_three_keep_d_epic', 'two_of_three_keep_f'):
        if k in out:
            print(f"{k}: {out[k]:.4g}")
    print('conditions:', out['conditions'], '=> E2', out['E2'])


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == '--selftest':
        lo, hi = wilson(10, 20)
        assert abs(fisher_greater(10, 20, 2, 20) - 0.0068) < 0.001
        assert abs(fisher_greater(5, 10, 1, 10) - 0.0704) < 0.001
        assert abs(lo - 0.299) < 0.01 and abs(hi - 0.701) < 0.01
        print('selftest ok: Fisher 10/20 vs 2/20 = %.4f, 5/10 vs 1/10 = %.4f, Wilson 10/20 = [%.2f, %.2f]'
              % (fisher_greater(10, 20, 2, 20), fisher_greater(5, 10, 1, 10), lo, hi))
        sys.exit(0)
    main(sys.argv[1])
