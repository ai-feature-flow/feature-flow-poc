# SP1: continue-as-new with running children and open decisions

Status: plan and checks pre-registered 2026-11-01 (commit `1093a55`), before
any run. Run the same day. **The claim holds**: in 39 hand-overs, 24 of them
with children running and decisions open, nothing was lost. The spike also
showed that rejecting answers during the hand-over is unnecessary, and harmful
when activities are long (section 5.3).

## 1. Claim under test

`docs/02` section 3 changes when a run may continue-as-new (restart its
Temporal workflow with its state carried over, to stay under history limits).
The first design waited until nothing was in flight. The new rule:

| In flight | At continue-as-new |
|---|---|
| Activities | Drain: start no new ones, let in-flight ones finish |
| Child runs | Do not block. Started with a parent-close policy that leaves them running; each signals the parent's **workflow id** when it ends; the parent confirms by reading the child's result |
| Open decisions | Do not block. Carried as state. Answers are Temporal Updates; the update validator rejects an answer during the hand-over and the API retries |

The claim is that this rule **loses nothing**: no child's end, no answer, no
activity result. Risks R21 (the new rule loses a child's end or an answer) and
R3 (a decision lost during continue-as-new) in `docs/10` section 3.

## 2. What is built

`spikes/04-continue-as-new`: one .NET console project with a parent workflow, a
child workflow, two activities, a worker and a harness. It does not reuse the
Spike 1 interpreter (`spikes/01-interpreter`), which implements the old rule;
it reuses that spike's stale-rejection pattern in the update validator.

| Part | Behaviour |
|---|---|
| Parent | Runs 300 short work activities (200 to 1,500 ms), four at a time. Starts 10 children at once and 10 more halfway through, so some children start in a later run. Opens 20 decisions at start. Continues-as-new every 150 history events, by the rule above. Ends when the work is done, every child's end is confirmed and every decision is answered |
| Child | Waits a seeded time (5 to 60 s), signals the parent's workflow id with its id, and returns a seeded outcome (`completed` or `failed`) |
| Confirm read | On a child's signal, the parent runs an activity that reads the child's status and result from Temporal. Pending confirmations are carried over a hand-over |
| Answers | An Update `answer(decision_id, outcome)`. The validator rejects with `stale` while draining and rejects a different answer to an answered decision. A repeat of the same answer succeeds without a second record |
| Harness | Sends each answer, half timed to land while the parent reports it is draining. Retries a rejected answer up to 20 times, 100 ms apart, and logs each error. Sends a duplicate of 5 answers and a conflicting answer to 2. After the run, reads every child directly from Temporal and every run's history |

Temporal dev server on port 7633, task queue `sp1`, so it does not share
infrastructure with the earlier spikes.

## 3. Pre-registered checks

Main runs: three, seeds 1, 2 and 3. A check passes only if it passes in all
three.

**Exercised** (the test reached the situation it is meant to test; without
these, a pass proves nothing):

| # | Check | Threshold |
|---|---|---|
| E1 | Continue-as-new happened while children were running and decisions were open | At least 3 such hand-overs per run |
| E2 | A child's end signal arrived while the parent was draining | At least 3, across the three runs |
| E3 | An answer was rejected as `stale` during a hand-over and then accepted on retry | At least 3, across the three runs |

**Nothing lost** (the claim):

| # | Check | Pass |
|---|---|---|
| C1 | Child ends | All 20 children recorded as ended, each once, with the outcome Temporal reports for that child when read directly by the harness |
| C2 | Answers | All 20 decisions recorded with the answer the harness sent; one record each; both conflicting answers rejected and the first answer kept |
| C3 | Drain | No activity started after draining began; no activity in flight at any hand-over |
| C4 | Completion | The run completes with `completed` within 10 minutes, and only once C1 and C2 hold |
| C5 | No errors | No `WorkflowTaskFailed` event in any run's history; no answer failed after 20 retries; no child failed to signal |

**Negative controls.** Each must make the named check fail. If it does not, the
check cannot detect the loss it is meant to detect, and the main result is
void.

| # | Change | Must fail |
|---|---|---|
| N1 | Children signal the parent's **first run id** instead of its workflow id | C1: a child that ends after the first hand-over cannot reach the parent |
| N2 | Children are started with the default parent-close policy (terminate) | C1: children are terminated at the first hand-over |
| N3 | The parent does not carry answers across a hand-over | C2. This is a sanity check of the checker only: it shows C2 reads the parent's final state, not the harness's own record |

**Exploratory, not pass or fail:**

| # | Change | Question |
|---|---|---|
| X1 | Validator does not reject during draining | Is the stale rejection needed at all when the answer handler only records state? |

## 4. Out of scope

Worker restarts during a hand-over (Spike 1 tested crash recovery for the
interpreter; `archive/docs/15`). Load at production scale. Children that are
themselves continuing-as-new.

## 5. Results

Raw results: `spikes/04-continue-as-new/results/<mode>-s<seed>.json`. Each
holds every answer attempt, the hand-over log and every run's history counts.
Run with `dotnet run -- --mode <mode> --seed <n>` in
`spikes/04-continue-as-new/Sp1`, against a dev server on port 7633.

### 5.1 Main runs

| Seed | Hand-overs | With children running and decisions open (E1) | Child signals while draining (E2) | Answers stale, then accepted (E3) | Most attempts for one answer | Elapsed |
|---|---|---|---|---|---|---|
| 1 | 13 | 8 | 3 | 11 | 13 | 92 s |
| 2 | 13 | 9 | 2 | 11 | 11 | 94 s |
| 3 | 13 | 7 | 4 | 11 | 11 | 93 s |

All exercise thresholds met: E1 at least 3 per run (7 to 9), E2 at least 3
across runs (9), E3 at least 3 across runs (33).

| Check | Seed 1 | Seed 2 | Seed 3 |
|---|---|---|---|
| C1 child ends: 20 of 20 recorded once, matching Temporal's direct read | Pass | Pass | Pass |
| C2 answers: 20 of 20 recorded once as sent; 2 conflicts rejected; 5 duplicates acknowledged | Pass | Pass | Pass |
| C3 drain: no start while draining; 320 activities scheduled and 320 completed, and every run that continued-as-new had none outstanding in its history | Pass | Pass | Pass |
| C4 completed within 10 minutes | Pass | Pass | Pass |
| C5 no `WorkflowTaskFailed`, no answer exhausted its retries, no child signal failed | Pass | Pass | Pass |

Up to 3 confirm reads of a child (the read that follows its signal) were
pending at a hand-over and completed in the next run.

### 5.2 Negative controls

Each control made its target check fail, so the checks can detect the loss
they are for.

| Control | Target check | Result |
|---|---|---|
| N1 signal to the first run id | C1 | Fail: 0 of 20 child ends recorded. Every child's signal failed (`signal_failed` in its result); the parent waited until the 4-minute timeout |
| N2 default parent-close policy | C1 | Fail: 0 of 20. Temporal reports every child `Terminated` |
| N3 answers not carried | C2 | Fail: 0 of 20 answers in the final state, though the harness had 20 `recorded` replies |

### 5.3 Exploratory X1: no stale rejection

With the validator accepting answers during the drain, one run (seed 1, 12
hand-overs, 6 with children and decisions open) passed every check. No answer
needed a retry, and no update failed in any way.

The reason: the answer handler only changes state, and the hand-over waits for
running handlers to finish (`Workflow.AllHandlersFinished`) before it
continues-as-new. An answer accepted during the drain is therefore in the
state that is carried. Temporal delivered every update to the current run; the
only update errors in the main runs were the validator's own `stale`
rejections.

The rejection has a cost the main runs show. The longest drain lasts as long
as the longest in-flight activity. Here that was 1.5 seconds, and one answer
needed 13 attempts 100 ms apart. `docs/02` section 3 allows activities of
hours, so a person's answer could be refused for hours, beyond any API retry
budget.

### 5.4 What changes in the design

| Finding | Change | Where |
|---|---|---|
| The rule loses nothing | R21 (the new rule loses a child's end or an answer) and R3 (a decision lost at continue-as-new) mitigated | `docs/10` section 2 |
| Rejecting answers during the drain is unnecessary (X1) and would refuse answers for as long as the longest activity | Answers are accepted during the drain. The validator still rejects unknown and conflicting answers | `docs/02` section 3; LOG 58, unreviewed |
| Signals and updates can reach a new run before its run method starts (Temporal delivers buffered ones first) | The carried state is set in the workflow's initialiser, not the run method | `docs/10` section 1.2, DR14 |
| The hand-over must wait for running handlers; signals must address the workflow id; children must use the abandon policy (N1, N2) | Already in the rule; the first is added as a requirement | `docs/10` section 1.2, DR15 |

### 5.5 Limits of the evidence

- Activities lasted at most 1.5 s. A drain of hours was not run; its cost is
  argued in section 5.3, not measured.
- X1 is one run. It shows answers are not lost without the rejection; it does
  not prove it.
- No worker was restarted during a hand-over (section 4).
- One harness defect was found and fixed during the controls: the wait for the
  result used an RPC timeout that a long poll ignores, so the first N1 to N3
  runs hung. The workflows were terminated and the controls re-run with a
  cancellation-token deadline. The main runs completed before the deadline and
  are unaffected.

## 6. Open questions

1. Should answers be accepted during the drain (LOG 58)? The spike recommends
   it; the alternative keeps the rejection and caps how long an activity in a
   parent may run.
