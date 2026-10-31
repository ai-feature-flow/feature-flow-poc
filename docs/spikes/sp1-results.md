# SP1: continue-as-new with running children and open decisions

Status: plan and checks pre-registered 2026-11-01, before any run. Results
follow in section 5 once run.

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

Not run yet.

## 6. Open questions

None yet.
