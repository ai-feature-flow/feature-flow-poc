import json,subprocess,sys,os,re,concurrent.futures as cf
P=os.path.dirname(os.path.abspath(__file__)); D=P+'/diffs'
SYS="""You are a code reviewer for a merge bar. You are given a unified diff and
the findings raised by earlier review passes.

Reply with a JSON object and nothing else — no prose, no markdown fences:

{"outcome": "completed" | "findings_open",
 "findings": [{"rule": "<rule id>", "path": "<file path>", "symbol": "<symbol>",
               "snippet": "<the offending line>", "message": "<one sentence>", "blocking": true|false}]}

Rules:
- Raise a finding only for something the diff actually shows, or for
  something the ticket requires that the diff does not contain.
- A finding listed in the prior findings is already known. Raise it again
  only if the diff still shows it; if it was fixed, do not raise it.
- A finding that was declined must not be raised again.
- "blocking" is true only when the issue should stop the merge.
- If the diff shows no problem, return an empty findings list and
  outcome "completed"."""
SCHEMA=json.dumps({"type":"object","required":["outcome","findings"],"properties":{
 "outcome":{"type":"string","enum":["completed","findings_open"]},
 "findings":{"type":"array","items":{"type":"object","required":["rule","path","symbol","snippet","message","blocking"],
  "properties":{"rule":{"type":"string"},"path":{"type":"string"},"symbol":{"type":"string"},"snippet":{"type":"string"},"message":{"type":"string"},"blocking":{"type":"boolean"}}}}}})
EPIC="Widget names are case- and whitespace-insensitive: the store keeps each name normalised (trimmed, lower-case), and a client can always look a widget up by the name it created it with, in any case or spacing. Widgets record their creation time."
TA="EPIC3-A: Names are stored normalised. In WidgetStore.Add, normalise the name with a new private static Normalise(string) that trims and lower-cases it (invariant culture), used inside the new Widget(...) expression. Add unit tests in WidgetNameNormalisationTests.cs: Add(\"  Alpha \") returns a widget named alpha."
TB="EPIC3-B: Add WidgetStore.FindByName(string name) after Count(), returning the first widget whose name equals name using an exact, ordinal (case-sensitive) comparison, or null. Add GET /widgets/by-name/{name} returning 200 or 404. Add unit tests in WidgetFindByNameTests.cs: Add(\"alpha\") then FindByName(\"alpha\") returns it; FindByName(\"ALPHA\") returns null."
VARIANTS={
 'epic':('EPIC',f"Epic: {EPIC}"),
 'neg':('NEG',f"Epic: {EPIC}"),
 'A+epic':('A',f"{TA}\nEpic: {EPIC}"),
 'B+epic':('B',f"{TB}\nEpic: {EPIC}"),
 'A-only':('A',TA),
 'B-only':('B',TB),
}
CASE=re.compile(r'case|lower|ToLower|normalis|normaliz|Ordinal|IgnoreCase',re.I)
LOOK=re.compile(r'FindByName|by-name|lookup|look up|look-up',re.I)
FAIL=re.compile(r"cannot|can't|not found|404|miss|fail|returns? null|no longer|never|break|won't",re.I)
def detects(f):
    t=' '.join(str(f.get(k,'')) for k in ('rule','symbol','snippet','message'))
    return bool(CASE.search(t) and LOOK.search(t) and FAIL.search(t))
def one(v,i):
    diff,ticket=VARIANTS[v]
    user=f"Ticket: {ticket}\n\nDiff hash: pilot\n\nPrior findings:\n(none)\n\nDeclined findings (never raise these again): (none)\n\nUnified diff:\n\n{open(f'{D}/{diff}.diff').read()}\n\nReview the diff. Reply with the JSON object only."
    env={k:v2 for k,v2 in os.environ.items() if k not in('ANTHROPIC_API_KEY','ANTHROPIC_BASE_URL')}
    r=subprocess.run(['claude','-p','--model','haiku','--output-format','json','--tools','','--setting-sources','','--strict-mcp-config','--no-session-persistence','--system-prompt',SYS,'--json-schema',SCHEMA,'-'],input=user,capture_output=True,text=True,env=env,timeout=300)
    try:
        d=json.loads(r.stdout); so=d.get('structured_output')
        if so is None: so=json.loads(re.sub(r'^```(json)?|```$','',d['result'].strip()))
        return dict(v=v,i=i,findings=so.get('findings',[]),cost=d.get('total_cost_usd',0),ok=True)
    except Exception as e:
        return dict(v=v,i=i,ok=False,err=str(e)[:200],stdout=r.stdout[:300],stderr=r.stderr[:300])
N=int(sys.argv[1]) if len(sys.argv)>1 else 10
jobs=[(v,i) for v in VARIANTS for i in range(N)]
res=[]
with cf.ThreadPoolExecutor(6) as ex:
    for r in ex.map(lambda a: one(*a),jobs): res.append(r)
json.dump(res,open(P+'/pilot-v2-results.json','w'),indent=1)
cost=sum(r.get('cost',0) or 0 for r in res)
print(f"total cost ${cost:.3f}; failures {sum(not r['ok'] for r in res)}")
for v in VARIANTS:
    rs=[r for r in res if r['v']==v and r['ok']]
    det=sum(any(detects(f) for f in r['findings']) for r in rs)
    nf=[len(r['findings']) for r in rs]
    print(f"{v:8s} detect {det}/{len(rs)}  findings/pass {nf}")
