# Spike 1 crash-resume results

Captured 2026-10-27T08:45:07Z. The harness starts and kills the worker itself (Process.Kill(entireProcessTree: true) on the pid it started). Sandbox http://127.0.0.1:5199, Temporal localhost:7233, queue spike-1. 0 stray worker(s) stopped before the run.

## 1-resume-after-crash

worker started pid=12260
before kill: handle=cmd-9 status=running session=sess-9 turns=3 elapsed=3.46s commit=d2a1dc6 key=sbx-long_agent resumeCount=0 broken=False
before kill: /admin/state has 1 command(s), hookDown=[]
worker 12260 killed at 08:45:12.805
worker restarted pid=24296 at +0.9s after the kill
result=completed outcome=completed attempts=1 resumes=1 infrastructureFailures=1
workflow resume evidence: handle=cmd-9 key=sbx-long_agent session=sess-9
lastError=step long_agent failure 1: ActivityFailureException: Activity task timed out
after: handle=cmd-9 status=completed session=sess-9 turns=91 elapsed=91.15s commit=4b18e54 key=sbx-long_agent resumeCount=0 broken=False
- exactly one command in /admin/state (no second command started): True
- same handle (cmd-9 -> cmd-9): True
- same sandboxKey (sbx-long_agent -> sbx-long_agent): True
- same sessionRef (sess-9 -> sess-9): True
- elapsedSeconds continued (3.46s -> 91.15s, not reset to 0): True
- step outcome=completed: True
- attempts=1 (a crash must not consume one): True
- resumes=1 (>=1 means the crash was seen as a resume): True

**PASS** — exactly one command in /admin/state (no second command started): True; same handle (cmd-9 -> cmd-9): True; same sandboxKey (sbx-long_agent -> sbx-long_agent): True; same sessionRef (sess-9 -> sess-9): True; elapsedSeconds continued (3.46s -> 91.15s, not reset to 0): True; step outcome=completed: True; attempts=1 (a crash must not consume one): True; resumes=1 (>=1 means the crash was seen as a resume): True

## 5-wall-clock-cost-per-crash

| crash | kill -> step making progress again |
|---|---|
| test1 kill mid-agent.run | 87.4s |

n=1 min=87.4s max=87.4s mean=87.4s

## 6-restarted-when-should-have-resumed

- 1-resume-after-crash: none

