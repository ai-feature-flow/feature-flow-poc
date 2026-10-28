# Spike 1 crash-resume results

Captured 2026-10-27T23:22:03Z. The harness starts and kills the worker itself (Process.Kill(entireProcessTree: true) on the pid it started). Sandbox http://127.0.0.1:5199, Temporal localhost:7233, queue spike-1. 0 stray worker(s) stopped before the run.

## 4-break-connection

worker started pid=33524
before outage: handle=cmd-3 status=running session=sess-3 turns=2 elapsed=2.47s commit=206ddb3 key=sbx-k3_agent resumeCount=0 broken=False
connection broken for 20s on cmd-3
during outage: handle=cmd-3 status=running session=sess-3 turns=6 elapsed=6.49s commit=bf35394 key=sbx-k3_agent resumeCount=0 broken=True
  GET /commands/cmd-3 -> 200
during outage: handle=cmd-3 status=running session=sess-3 turns=10 elapsed=10.49s commit=8033f39 key=sbx-k3_agent resumeCount=0 broken=True
  GET /commands/cmd-3 -> 200
during outage: handle=cmd-3 status=running session=sess-3 turns=14 elapsed=14.80s commit=232c1fb key=sbx-k3_agent resumeCount=0 broken=True
  GET /commands/cmd-3 -> 200
during outage: handle=cmd-3 status=running session=sess-3 turns=18 elapsed=18.81s commit=e1a75f7 key=sbx-k3_agent resumeCount=0 broken=True
  GET /commands/cmd-3 -> 200
result=completed outcome=completed attempts=1 resumes=0 infrastructureFailures=39
lastError=
after: handle=cmd-3 status=completed session=sess-3 turns=60 elapsed=60.99s commit=3fed2b7 key=sbx-k3_agent resumeCount=0 broken=False
- elapsed and turns advanced during the outage (6.49s/6 turns -> 18.81s/18 turns): True
- same handle after the outage (cmd-3): True
- step completed (outcome=completed): True
- attempts=1 (outage is infrastructure, free): True

**PASS** — elapsed and turns advanced during the outage (6.49s/6 turns -> 18.81s/18 turns): True; same handle after the outage (cmd-3): True; step completed (outcome=completed): True; attempts=1 (outage is infrastructure, free): True

## 5-wall-clock-cost-per-crash

No crash was measured.

## 6-restarted-when-should-have-resumed

- 4-break-connection: none

