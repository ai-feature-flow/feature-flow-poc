stopped stray worker pid=37244
# Spike 1 crash-resume results

Captured 2026-10-27T09:48:51Z. The harness starts and kills the worker itself (Process.Kill(entireProcessTree: true) on the pid it started). Sandbox http://127.0.0.1:5199, Temporal localhost:7233, queue spike-1. 1 stray worker(s) stopped before the run.

## 2-repeated-kill-no-progress

worker started pid=30360
stalled cmd-2 at turns=2 commit=45bf7fb
timed out waiting for a resume after the kill
kill 1: resumes= attempts= outcome='' turns=2 recovered in 180.1s
  lastError=step stalled_agent heartbeat timeout after 0 turns commit= handle=cmd-2
timed out waiting for a resume after the kill
kill 2: resumes= attempts= outcome='' turns=2 recovered in 180.0s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
timed out waiting for a resume after the kill
kill 3: resumes= attempts= outcome='' turns=2 recovered in 180.0s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
timed out waiting for a resume after the kill
kill 4: resumes= attempts= outcome='' turns=2 recovered in 180.2s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
kill 5: resumes=5 attempts=1 outcome='' turns=2 recovered in 10.0s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2 [counted as restart: 3 resumes without progress, attempts=1]
timed out waiting for a resume after the kill
kill 6: resumes=5 attempts=1 outcome='' turns=2 recovered in 180.1s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2 [counted as restart: 3 resumes without progress, attempts=1]
kill 7: resumes=6 attempts=1 outcome='' turns=2 recovered in 19.6s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
timed out waiting for a resume after the kill
kill 8: resumes=6 attempts=1 outcome='' turns=2 recovered in 180.2s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
kill 9: resumes=7 attempts=1 outcome='' turns=2 recovered in 28.3s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
timed out waiting for a resume after the kill
kill 10: resumes=7 attempts=1 outcome='' turns=2 recovered in 180.1s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2
kill 11: resumes=8 attempts=2 outcome='' turns=2 recovered in 29.1s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2 [counted as restart: 3 resumes without progress, attempts=2]
timed out waiting for a resume after the kill
kill 12: resumes=8 attempts=2 outcome='' turns=2 recovered in 180.1s
  lastError=step stalled_agent heartbeat timeout after 2 turns commit=45bf7fb handle=cmd-2 [counted as restart: 3 resumes without progress, attempts=2]
final: outcome= attempts=2 resumes=8 terminal=
- attempt count rose (0->0->0->0->1->1->1->1->1->1->2->2): True
- step reached attempts_exhausted (outcome=): False
- resumes=8 > attempts=2 (resumes free until 3 without progress): True

**FAIL** — attempt count rose (0->0->0->0->1->1->1->1->1->1->2->2): True; step reached attempts_exhausted (outcome=): False; resumes=8 > attempts=2 (resumes free until 3 without progress): True

## 5-wall-clock-cost-per-crash

| crash | kill -> step making progress again |
|---|---|
| test2 kill 1 | 180.1s |
| test2 kill 2 | 180.0s |
| test2 kill 3 | 180.0s |
| test2 kill 4 | 180.2s |
| test2 kill 5 | 10.0s |
| test2 kill 6 | 180.1s |
| test2 kill 7 | 19.6s |
| test2 kill 8 | 180.2s |
| test2 kill 9 | 28.3s |
| test2 kill 10 | 180.1s |
| test2 kill 11 | 29.1s |
| test2 kill 12 | 180.1s |

n=12 min=10.0s max=180.2s mean=127.3s

## 6-restarted-when-should-have-resumed

- 2-repeated-kill-no-progress: none

