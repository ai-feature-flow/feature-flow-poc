# Spike 3 results: concurrent tickets

Written 2026-10-30 by worker C from the run of that day. Section 3.1 was added on
2026-10-31: a pre-registered replication of the R4 series with D4 fixed, on which E2 is
judged. Plan: `docs/17-spike-3-plan.md`.

**Question** (`docs/08` 2.3): do several tickets of one epic in one repository stay correct?

**Answer: yes.** Three agent-authored tickets ran concurrently (`max_concurrency: 2`),
merged serially under the merge lock, with a real conflict routed to an agent and resolved
without a human. Injected failures did not block siblings.

The epic review catches the seeded cross-ticket defect that both ticket reviews miss:
20/20 against 0/20 and 0/20, with Neg at 0/20 (E2 met, section 3.1). Getting there took
two attempts:

- **First, inconclusive.** The pre-registered series failed its Neg control (15/20). The
  engine's reviewer silently cut every diff to 6000 characters, and the aggregate is about
  9.5 KB, so the reviewer never saw `FindByName`.
- **Then met.** With that fixed (requirement D4: never cut, refuse visibly), a fresh
  pre-registered replication on the same inputs met every pre-set threshold.

| Deliverable | Where |
|---|---|
| Code | `spikes/03-concurrent/` (engine deltas in `spikes/01-interpreter`, `spikes/02-real-run`) |
| Plans | `spikes/03-concurrent/plans/` |
| Main-run evidence | `spikes/03-concurrent/results/spike-3/` |
| Sanity checks | `spikes/03-concurrent/results/{sanity-lock,sanity-conflict,i3,i4,loop-cap}.json` |
| R4 audit (every finding) | `results/spike-3/r4-replication/r4-audit.md` (E2); history: `results/spike-3/r4-audit.md`, `results/spike-3/r4-full/r4-audit.md` |

## 1. The run

One main run, `spike-3`, on 2026-10-30, 10:31:21–10:45:22 UTC (14 min). There was no
re-run. Integration branch `ff/epic/spike-3` was created from fixture `main` `5dd0108`, and
fixture `main` is untouched. Every model step ran on `claude-code`, model
`claude-haiku-5-5`, for USD 0.046 in total (A 0.020, C 0.016, B 0.008, epic review
0.002). The R4 series and C5 cost about USD 0.17 more. No rate limits were hit.

| Ticket | Started | Closed | Path | PR, merge commit |
|---|---|---|---|---|
| C `EPIC3-C` | 10:31:21.6 | 10:35:49.5 | straight; held the lock through I2 | #13, `9ae9879` |
| A `EPIC3-A` | 10:31:21.6 | 10:38:11.6 | forward-merge **conflict** -> `resolve_conflict` -> tests, review, gate -> merged | #12, `b420020` |
| B `EPIC3-B` | 10:35:49.5 | 10:45:07.6 | I1 outage in `implement`; forward merge changed the branch -> `recheck` | #14, `fd463f0` |
| Epic review | 10:45:07 | 10:45:22 | `findings_open` (blocking, on the seed) -> gate approved -> `completed` | — |

**Oracles** (docs/17 3.4) on `ff/epic/spike-3` @ `fd463f0`, with a throwaway test in a
scratch clone (`results/spike-3/oracle-integration.json`). Seeds present:

- **Defect present:** `Add("Alpha")` then `FindByName("Alpha")` returns null. The unit tier
  is 7/7 green, so no test catches it.
- **Resolution keeps both sides:** `Add("  Beta ")` gives `beta`, with `CreatedAt` within 1 s.
- **Conflict:** the engine recorded `blocked` / `Work/conflict` on `WidgetStore.cs`
  (`spike-3-a.forward.jsonl`).

## 2. Exit criteria and checks

| # | Result | Decisive evidence | What would have made it come out differently |
|---|---|---|---|
| **E1** | **met** | All three children `Completed` with result `completed`; PRs #12–#14 merged into `ff/epic/spike-3` (`gh api`) | Any child ending `failed`/`incomplete` (the loop cap, section 4, would have ended a spinning ticket `failed`) |
| **E2** | **met** (on the pre-registered replication, 3.1) | I-epic 20/20 [0.84, 1.00]; I-A and I-B 0/20 [0.00, 0.16]; Pos 5/5; Neg 0/20; Fisher p = 7.3 × 10⁻¹² against each ticket series. The first pre-registered series was inconclusive (Neg 15/20, truncated input) | Neg detecting (the reviewer reacting to words); Pos missing; ticket reviews catching the seed |
| **E3** | **met** (C7, with a caveat) | I1: A merged while B's worker was down; I3; the lock was free while the resolver ran | A sibling waiting on a slot, the lock or the join because of another ticket |
| C1 | **met** | Lock audit: C and A requested at the same instant (10:34:27.108); C granted; **A waited 82 s**. Holder intervals C [10:34:27.1, 10:35:49.5], A [10:35:49.5, 10:35:55.0] then [10:37:54.5, 10:38:11.6], B [10:44:01.8, 10:45:07.6] do not overlap. GitHub `merged_at` C 10:35:47, A 10:38:09, B 10:45:05, each inside its holder's interval | Overlapping intervals; a merge outside its interval; no wait (would be inconclusive). Sanity: bypassed lock overlaps 3/3 pairs |
| C2 | **met** (both cases seen) | `branch_changed` against the oracle `git merge-base --is-ancestor <integration> <ticket before>`: C (`5dd0108` in `ce37bc3`) false, no rerun; A after resolution (`9ae9879` in `36d8f61`) false, no rerun; B (`b420020` not in `b91b485`) **true**, `recheck` ran and dispatched the integration tier on the new head `55a95d0` (key `c760c1dc…`, `workflow_dispatch` run success) | Disagreement with the oracle; a rerun on no change, or none on change. Push-triggered CI runs are not the evidence; the step and its dispatch key are |
| C3 | **met** (natural conflict, not injected) | A's forward merge returned `blocked` at 10:35:55; the next step was `resolve_conflict` (lock released "cursor moved to agent.run"). A's only decisions are its two `gate_merge` approvals (10:34:27, 10:37:54). The resolver's commit `36d8f61` has parents `0ec637e` and `9ae9879`; its SHA is in Claude Code session `471d2df4` (19 turns, all `claude-haiku-5-5`), which ran `git merge`, edited, ran the unit tier and committed. The resolution oracle passes | Routing to `gate_escalate`; a dropped side (oracle fails); no conflict. Sanity: hand-made A+C branches conflict, B+C do not |
| C4 | **met** | Epic review: base `main` `5dd0108`, head `fd463f0` (the integration head after B merged at 10:45:05; the review ran at 10:45:21). Its diff contains all 8 files the three tickets changed. **But** the reviewer saw only the first 6000 characters (section 3) | A ticket diff; a head read before the last merge |
| C5 | **evidenced at spike level, close to by construction** | Section 5 | Section 5 |
| C6 | **met** | A and C started at 10:31:21.6; B started 27 ms after C closed; never more than two open; all `completed` | Three open at once; B starting before a slot freed (Spike 1's loop would have made B wait for both) |
| C7 | **met**, with a caveat | I1 below; I3 (no agents): the failed child closed at 2.1 s, the third started 0.0 s later, the join reported `any_failed` only after all three closed. The lock was free from 10:35:55 to 10:37:54 while A's resolver ran (10:35:56–10:36:27) | Caveat: in I2 the **stalled lock holder** (C) delayed A's merge for 82 s. That is the designed behaviour (J1, J11), but an open-and-stuck holder would block merges indefinitely (requirement D3) |

### Injections

| # | Result | Evidence |
|---|---|---|
| I1 | **Partly met: the other ticket merged and B completed, but B's agent session restarted rather than resumed** | B's worker was killed (process tree) 18.5 s into `implement` and was down for **301 s**; A merged at 10:38:09 during the outage. The retried activity carried session `0efdbf02` and `AttemptNumber 1`, so no attempt was consumed, but it completed under a **new session `e0464571`**. That session's first message is the original prompt, and over 11 turns it redid the edits. Cause: `ClaudeCodeAgentRuntimeProvider.ResumeAsync` requires the in-memory capability probe, which a freshly started worker has not run. It returns `capability_missing`, and the step falls back to a fresh start. The uncommitted edits of the first session were also discarded by the N4 sync |
| I2 | **met** | C's worker was killed 2.2 s after the grant. A query at +33 s showed holder `ticket_c`, waiter `ticket_a`. The worker was restarted at 60 s. C's `merge_forward` heartbeat-timed out and ran once more, completing; `merge_pr` ran **once** (history: 1 activity); one merge commit, `9ae9879`. Released at 10:35:49 with reason "scm.merge returned" |
| I3 | **met** | No agents; see C7 |
| I4 (optional) | **met** | No agents: holder terminated; the liveness check released it and granted the waiter **29.7 s** later |

## 3. R4: the epic review (E2)

Rubric (pilot v2, re-applied to the pilot's raw results it reproduces their counts
exactly) and thresholds (docs/17 6.4) were fixed in `tools/r4_analyse.py` before any series
ran. Each series has one rendered-input hash, so the input was unchanged across its passes.
The reviewer has no repository access. A and B ticket reviews get their ticket text; the
epic review gets the epic description.

**First series, as pre-registered** (`results/spike-3/r4-summary.json`). Series inputs:

- **I-epic:** the in-run aggregate.
- **I-A:** A's last in-run review diff (the pass that cleared it for merge).
- **I-B:** B's only in-run review diff.
- **Pos and Neg:** built from the real aggregate (`results/spike-3/series/*-delta.patch`).
  Neg passes its own tests and the oracle: the contract holds for `"Alpha"` and
  `"  Alpha "`, and the defect is absent.

| Series | Detected | 95% Wilson | Threshold | |
|---|---|---|---|---|
| Pos | 5/5 | 0.57–1.00 | ≥ 4/5 | ok |
| **Neg** | **15/20** | 0.53–0.89 | f ≤ 2/20 | **fails** |
| I-epic | 20/20 | 0.84–1.00 | ≥ 10/20 | ok |
| I-A | 0/20 | 0.00–0.16 | ≤ 2/20 | ok |
| I-B | 0/20 | 0.00–0.16 | ≤ 2/20 | ok |

Fisher one-sided, epic against each ticket series: p = 7.3 × 10⁻¹². **Controls failed, so
this series was inconclusive** (history; E2 is judged in 3.1).

**Why Neg failed.** Every Neg detection reads "the WidgetStore.cs hunk is truncated, so
FindByName and Normalise cannot be verified". `ReviewerModel` (Spike 2) inserts
`Truncate(diffText, 6000)`, and the aggregate diffs are 9.5–9.6 KB. The cut falls inside
`WidgetStore.cs`, before `Add` and `FindByName`. So Pos's comment, Neg's fix and the
defective code itself were all invisible. The rubric's words ("FindByName", "Normalise",
"cannot") matched the reviewer's complaint about truncation. The in-run epic review was cut
the same way. Its blocking finding was correct, but it came from B's **plan document**
(`docs/plans/EPIC3-B.md`, written by B's planner: "ordinal, case-sensitive"), not from the
code. The ticket diffs (3.3–3.4 KB) were not cut.

**Post hoc, not pre-registered** (`results/spike-3/r4-full/`). The same series with the
limit lifted (`--diff-limit 50000`), and the thresholds unchanged. I-A and I-B were reused:
their rendered-input hashes with and without the limit are identical (checked).

| Series | Detected | 95% Wilson |
|---|---|---|
| Pos | 5/5 | 0.57–1.00 |
| Neg | **0/20** | 0.00–0.16 |
| I-epic | 19/20 (manual audit 20/20) | 0.76–0.99 |
| I-A, I-B | 0/20 each | 0.00–0.16 |

Fisher p = 1.5 × 10⁻¹⁰ against each ticket series. Every condition is met. The single
rubric miss (pass 6) says FindByName "gets null… which violates the requirement": that is
the defect, but "gets null" is not in the failure term. That is the only disagreement
between rubric and audit. No Neg pass raised anything about case or normalisation.

### 3.1 Replication (pre-registered 2026-10-31, before any pass ran)

Decided by the orchestrator under the product owner's standing instruction, 2026-10-31:
**E2 is judged on this replication.** The pre-registered (truncated) result and the
post-hoc full-diff result above stay as history.

- **Reviewer.** The engine's reviewer with D4 fixed (commit `6684f5d`): limit 32000
  characters; a larger diff fails visibly with `Work/diff_too_large`, and is never cut.
  Every pass records `diffChars`, `diffLimit` and `truncated`.
- **Inputs.** The same `results/spike-3/series/*.diff` files, by SHA-256 prefix:

  | File | SHA-256 prefix | Size |
  |---|---|---|
  | Pos | `6a2d27dcb1810d6f` | 9642 B |
  | Neg | `bf25c5e0c82b84c3` | 9615 B |
  | I-epic | `fbf3b7f7a35f2aab` | 9561 B |
  | I-A | `997fff55e5a0cf1f` | 3280 B |
  | I-B | `319673cc65ac419d` | 3436 B |

  Specifications: the epic description for Pos, Neg and I-epic; ticket A's text for I-A;
  ticket B's text for I-B. No prior findings.
- **Passes.** Fresh: Pos 5; Neg, I-epic, I-A and I-B 20 each. No pass is re-run or
  discarded except for an infrastructure error, which is retried and reported.
- **Rubric and thresholds.** Unchanged: docs/17 6.3, and the 6.4 thresholds in
  `tools/r4_analyse.py` (Pos ≥ 4/5, f ≤ 2/20, d<sub>epic</sub> ≥ 10/20, d<sub>A</sub> and
  d<sub>B</sub> ≤ 2/20, one-sided Fisher p < 0.05 against each ticket series). Failed
  controls make E2 inconclusive.
- **Reporting.** Counts with 95% Wilson intervals; every detection audited by hand, with any
  disagreement between rubric and audit reported. The rubric count decides.
- **Output.** `results/spike-3/r4-replication/`.

**Result** (`results/spike-3/r4-replication/r4-summary.json`):

| Series | Detected | 95% Wilson | Threshold | |
|---|---|---|---|---|
| Pos | 5/5 | 0.57–1.00 | ≥ 4/5 | ok |
| Neg | 0/20 | 0.00–0.16 | f ≤ 2/20 | ok |
| I-epic | 20/20 | 0.84–1.00 | ≥ 10/20 | ok |
| I-A | 0/20 | 0.00–0.16 | ≤ 2/20 | ok |
| I-B | 0/20 | 0.00–0.16 | ≤ 2/20 | ok |

Fisher one-sided, epic against each ticket series: p = 7.3 × 10⁻¹². **E2 is met.**

- **Inputs.** Every pass recorded `diffChars` equal to its file (9641, 9614, 9560, 3279,
  3436), limit 32000, `truncated=false`. Each series' rendered-input hash is identical to
  the post-hoc run's, so the inputs are the planned ones. No infrastructure retries.
- **Manual audit.** It agrees with the rubric in every pass:
  - I-epic and Pos: all 25 detections describe the seed. They name the raw-input ordinal
    `FindByName`, the by-name route, or the test that locks in case sensitivity.
  - Neg: 23 findings. One (pass 19) concerns interior whitespace ("Big  Alpha"), a real
    but different gap; neither the rubric nor the audit counts it.
  - I-A: 14 findings, all about null handling in `Normalise`.
  - I-B: no findings at all.
- **Cost.** USD 0.08 for 85 passes.

**What the in-run epic review saw.** The main run was not re-run. Its epic review got the
truncated aggregate: the first 6000 of 9561 characters, cut inside `WidgetStore.cs`. It
still raised the seed as a blocking finding, but **from the plan document
`docs/plans/EPIC3-B.md`, not from the code**: B's planner had written "ordinal,
case-sensitive" there. The code-level detection rate above comes from the replication, not
from the in-run pass.

**Two-of-three voting** (L3, reported as asked). Replication: it would keep a real
finding with probability 1.00 and a Neg false finding with 0.00. The first pre-registered
series gives 1.00 and 0.84; the post-hoc series 0.99 and 0.00.
The ledger positions are not reopened (docs/17 6.5).

## 4. What Spike 3 taught that changes the design

1. **The interpreter must dispatch on first completion.** Spike 1's loop awaited a whole
   batch, so with A and C in flight B could start only when both finished. That is a
   sibling blocked on a slot by construction (C7), and it is invisible with single-step
   branches. Fixed under `Workflow.Patched`. With the patch removed, a Spike 1 fixture fails
   replay. *Design finding (judgement call 1).*
2. **A provider that misclassifies an unexpected success shape retries forever.** GitHub's
   no-op forward merge returns 204 with no body; the provider read it as
   `Infrastructure/server_error`, which the engine retries free and indefinitely. The first
   ticket to merge would have hung. Found by the conflict sanity check. *Design finding
   (judgement call 4).*
3. **The reviewer's input budget is silent.** An epic's aggregate diff is the largest input
   a review gets, and the 6000-character cut hid most of it. The in-run epic review was
   right for a reason that would not survive a planner that wrote no plan doc. This made the
   first E2 series inconclusive. D4 is now fixed in the engine: limit 32000, and a larger
   diff fails the step visibly with `diff_too_large`.
4. **Resume after a worker restart does not work** for `claude-code`. Resume depends on
   process memory (the capability probe), and the sync discards uncommitted edits. The free
   attempt (docs/01 3.7) still held.
5. **Attempt ceilings were not enforced on loop re-entry.** Without ceilings, a
   no-agent loop executed its fix step 213 times in 15 s. They are now enforced per
   execution: agent.run 5, perRun 40.
6. **The wider lock span (J1) worked**: forward, rerun and merge were held together, and no
   sibling landed in between. Its cost is that a stalled holder delays everyone until it
   resumes or closes.
7. **Ticket reviews given only ticket text miss this defect class** (0/20 and 0/20, in run
   and in series). The pilot's finding that giving ticket reviews the epic contract catches
   it earlier (docs/17 6.6) stands.

## 5. C5: pre-submit parity

ff-presubmit was run by a Claude Code session in a ticket worktree. It was `claude -p`
(session `cebd9e43`, haiku), not an interactive session (judgement call 8), and only the
wrapper was allowed. The session's transcript shows its three calls, and each call appended
to the run's records. The setup was B's in-run review head `b91b485` and base `b420020`,
re-pushed as `ff/ticket/spike-3-c5` and `ff/epic/spike-3-c5base`. The gated steps were run
by the engine with their entry moved to that step.

- Gated review #1 on an empty ledger rendered hash `d1687957…`, identical to B's in-run
  review, so the setup reproduced the in-run input. It raised nothing, so one non-blocking
  finding was seeded.
- ff-presubmit's hash (`d6d823fc…`, prior `[f-c5-seed]`) **equals** gated review #2's at
  the same head and ledger.
- `findings.evaluate_readiness` from ff-presubmit and from the gated step are
  **byte-identical**.
- Sanity: with base `main` the hash differs (`2c727d06…`), and so does omitting prior
  findings (`d1687957…`).

**Stated plainly:** ff-presubmit calls the gated step's own input builder and renderer, so
an equal hash is close to guaranteed. The sanity check proves only that the hash covers
base and prior findings. Readiness was "ready" on a non-blocking seed, a trivial case.
**C5 is evidenced at spike level, close to by construction.**

## 6. Design requirements

| # | Requirement | From |
|---|---|---|
| D1 | The interpreter starts a ready step as soon as a slot frees; interpreter changes are versioned for replay | 4.1 |
| D2 | A provider maps every documented success response, including empty ones, to an outcome; an unmapped success is Work, not Infrastructure, so it cannot retry forever | 4.2 |
| D3 | A lock holder that is open but stalled (D24) must lose the lock by lease or by the stalled state, not only when it closes. Amend `docs/06` 6.5 to the J1 span | 4.6, I2 |
| D4 | The reviewer sees the whole diff, or the engine chunks it, or the step fails visibly; the coverage of what it saw is recorded. Never a silent cut. **Minimal form built** (refuse above 32000 characters, recording `diffChars`, limit and `truncated`); whether to chunk is open | 4.3 |
| D5 | Session resume must not depend on worker-process memory; a resumed agent step must keep the sandbox's uncommitted work | 4.4 |
| D6 | Attempt ceilings are enforced on every execution, including loop re-entry | 4.5 |
| D7 | The epic review's prior findings include the children's ledgers (L4; not built here) | judgement call 11 |

## 7. Judgement calls made without being asked

The first twelve were accepted at the review gate.

| # | Call | Alternative |
|---|---|---|
| 1 | **Design finding.** Dispatch on first completion, under `Workflow.Patched` | Spike 1's batch loop: B waits for both A and C, and I1 and C7 fail by construction |
| 2 | A run ending with nothing ready and no `end:` returns `incomplete` (R6) | `completed` |
| 3 | **Spike-only switch** (`Readiness.BlockingOnly`). Review outcome and readiness use the documented bar `noUnresolvedBlocking`; non-blocking findings are recorded but do not open the fix loop | Spike 2's code: every open finding loops |
| 4 | **Design finding.** `MergeBaseForwardAsync` treats GitHub's 204 as a completed no-op | Leave it: the engine retries forever |
| 5 | Ticket text drives the agent prompts; plans go to `docs/plans/<ticket>.md`; the implementer writes the tests the ticket prescribes; the allowlist gains `git merge/log/show` and `dotnet test/build` for every role | Spike 2's shared `docs/ticket-plan.md`, which would conflict across tickets |
| 6 | One clone per ticket, not `git worktree` (allowed by docs/17 7.2) | Shared-ref worktrees cannot force-update a branch checked out in another |
| 7 | Per-run queue names | Fixed names with stale pollers |
| 8 | C5 from a `claude -p` session; parity at a fixed head and ledger by a C5 mini-run | An interactive session, which an unattended run cannot drive |
| 9 | I1 kill 15 s into `implement`, process tree | Kill at step start, before any session id exists |
| 10 | The first of A and C at `gate_merge` held for the other (max 25 min; A waited about 12 s for C, and both were approved at 10:34:27.0) | Approve on arrival |
| 11 | L4 not built: the epic review's prior findings exclude the children's ledgers | Build it |
| 12 | Squash merges into the integration branch, as the fixture manifest says | — |
| 13 | **Attempt ceilings enforced on re-entry** (orchestrator's condition): attempts_exhausted -> `gate_escalate`, and the harness rejects every escalation, so the ticket ends failed. Not triggered in the run | Uncapped loops |
| 14 | Neg also corrects B's doc comment and plan doc, so Neg has no contract violation anywhere | Code and test only, leaving a document that contradicts the epic |
| 15 | I-A uses A's last in-run review diff (after resolution) | Its first |
| 16 | The post-hoc full-diff series was run and reported beside the result, not in its place. Superseded by the orchestrator's decision to judge E2 on a pre-registered replication (3.1) | Report only the inconclusive result, or replace it |
| 17 | The lock request is signal-with-start **from an activity**; a workflow cannot signal-with-start | Pre-start the lock workflow |

## 8. Open questions

None open.

### Answered

| Question | Answer |
|---|---|
| An agent's unfinished work when its worker crashes (requirement D5): restore uncommitted files, or require work-in-progress commits? | **Keep it simple; this is rare.** The agent runs in its own sandbox, separate from the worker, so routine worker restarts such as deploys cost nothing (already the design; the spikes ran both on one machine, which is why Spike 3's crash test lost work). On resume, uncommitted files are kept if the sandbox survived, and the agent's conversation is reloaded (fix the bug where resume relied on the crashed worker's memory). If the sandbox is lost, the step restarts from the last pushed commit. No snapshots; revisit only if real runs show sandbox loss is common. Product owner, 2026-10-31 |
| A stuck ticket holding the merge lock blocks every other merge (requirement D3). Lease, or release on stall? | **Neither: drop the lock.** Tickets merge like developers do: catch up, re-test if changed, merge; a merge onto a moved base is refused by Git and the ticket catches up again, escalating after about 3 tries. Agents never hold a push credential; the engine pushes and merges. See `docs/20-merge-design.md`. Product owner, 2026-10-31 |
| What should happen when a change is too big for the reviewer? (requirement D4) | **Redesign review instead.** The reviewer becomes a read-only agent that reviews the branch with full repository access; nothing is pre-loaded into its prompt, so there is no prompt size limit, and the engine checks it opened every changed file. See `docs/19-code-review-design.md`. Product owner, 2026-10-31 |
| Should ticket reviews get the epic's contract? | **Yes.** Part of the same review design (`docs/19`, point 8). Product owner, 2026-10-31 |
| Does the post-hoc full-diff R4 series count for E2? | **No.** It stays reported as post hoc. E2 is judged on a fresh, pre-registered replication with D4 fixed in the engine (section 3.1), which met every threshold. Orchestrator decision under the product owner's standing instruction, 2026-10-31; open to review |
