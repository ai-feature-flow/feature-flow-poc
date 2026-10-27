# Spike 1 crash-resume results

Captured 2026-10-27T21:35:14Z. The harness starts and kills the worker itself (Process.Kill(entireProcessTree: true) on the pid it started). Sandbox http://127.0.0.1:5199, Temporal localhost:7233, queue spike-1. 0 stray worker(s) stopped before the run.

## 4-break-connection

threw HttpRequestException: No connection could be made because the target machine actively refused it. (127.0.0.1:5199)

**INCONCLUSIVE** — threw HttpRequestException: No connection could be made because the target machine actively refused it. (127.0.0.1:5199)

## 5-wall-clock-cost-per-crash

No crash was measured.

## 6-restarted-when-should-have-resumed

- 4-break-connection: none

