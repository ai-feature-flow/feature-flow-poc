# Spike 3 plan: concurrent tickets

Written 2026-10-30. **Accepted by the product owner, 2026-10-30**, with the changes from the pilot (section 6.6). Not yet built.

Review 16: *"Propose the Spike 3 plan (`docs/08` 2.3, concurrent tickets with the
seeded cross-ticket defect) in light of what Spikes 1 and 2 changed, before
building it."*

Product owner direction: the minimum needed to prove what the spike is for, on
this Mac, once. Agents run natively (no Docker) on `claude-code` with `haiku` for
every role, pending worker A's proof of that runtime. Results will go in
`docs/18-spike-3-results.md`; code in `spikes/03-concurrent/`.

## 1. Question, exit criteria and checks

**Question** (`docs/08` 2.3): do several tickets of one epic in one repository
stay correct? Three tickets on `feature-flow-example` (integration branch
`ff/epic/spike-3`), `max_concurrency: 2`, overlapping code, a seeded defect.

| # | Exit criterion or check (`docs/08` 2.3) | Risk | Evidenced in |
|---|---|---|---|
| E1 | Three tickets complete | — | C6 |
| E2 | The epic-level review sees cross-ticket findings that per-ticket review missed | R4 | Section 6 |
| E3 | No ticket is blocked by another's failure | R10 | C7 |
| C1 | Ticket branches merge into the integration branch serially, under the merge lock | R10 | Section 5 |
| C2 | `scm.merge_base_forward` runs before merge, and checks rerun only when the branch changed | R10 | Section 5 |
| C3 | A merge conflict routes to an agent step, not to a human | R10 | Section 5 |
| C4 | Epic-level review runs on the aggregate diff of the integration branch | R10 | Section 5 |
| C5 | An interactive session can run a pre-submit check and get the same findings the gated step would produce | R17 | Section 5.2 |
| C6 | `max_concurrency: 2` holds and all three children complete (for E1) | — | Section 5 |
| C7 | A ticket that fails or stalls blocks neither a sibling's work nor its merge (for E3) | R10 | Section 5.1 |

## 2. What Spikes 1 and 2 changed

| From | Consequence for this plan |
|---|---|
| `docs/16` 11, item 1: the provider seam holds | New behaviour goes behind `ISpikeActivities` and into the interpreter; nothing is redesigned |
| `docs/16` 2 (R4) and 8: verdicts are unstable | E2 is a **detection rate** over repeated passes, with controls, not one pass |
| `docs/16` 9: a claimed result was canned content | Every agent step is traceable to a Claude Code session and usage record; the seeds are confirmed by an oracle, not assumed (section 3.4) |
| `docs/08` 1.3 R2: F1 enforced at merge time | Nothing merges into `main`; F1 is not engaged |
| `docs/15` 3: interruptions became continuations | The merge lock is a new place to be interrupted; I2 covers it |
| Spike 2 `WorkerProgram.cs`: one ticket per worker process | Concurrency needs one worker per ticket (section 7.2) |
| Spike 2 `ReviewerModel`: finding ids are positional (`f-{stepId}-{index}`) | Counting re-raises needs the `docs/01` 7.2 fingerprint, which is new |
| Spike 1 and 2 code | Built: `parallel`, `max_concurrency`, guarded routes; `MergeBaseForwardAsync` (returns `BranchChanged`, maps 409 to `Work/conflict`) and `CreateBranchAsync`. Not built: `run.child`, the lock, those two methods as steps |

## 3. The epic

### 3.1 Three tickets

Base: fixture `main` (`WidgetStore` with `All`, `Find`, `Add`, `Count`; endpoints
`GET /widgets`, `GET /widgets/{id}`, `POST /widgets`). Texts are prescriptive so
the seeds land where designed. Each ticket uses its **own new test file**, so the
tickets do not all collide on `WidgetStoreTests.cs`.

| Ticket | Text given to the agents (abridged) |
|---|---|
| **A** `EPIC3-A` | In `WidgetStore.Add`, normalise the name with a new private static `Normalise(string)` that trims and lower-cases (invariant culture), used **inside the `new Widget(...)` expression**. Tests in `WidgetNameNormalisationTests.cs`: `Add("  Alpha ")` gives name `alpha` |
| **B** `EPIC3-B` | Add `WidgetStore.FindByName(string)` **after `Count()`**, using an **exact, ordinal comparison**, and `GET /widgets/by-name/{name}` (200 or 404). Tests in `WidgetFindByNameTests.cs`: `FindByName("alpha")` finds `Add("alpha")`; `FindByName("ALPHA")` is null |
| **C** `EPIC3-C` | Add `DateTimeOffset CreatedAt` as the third member of `Widget`, set to `DateTimeOffset.UtcNow` **inside the `new Widget(...)` expression in `Add`**. Tests in `WidgetCreatedAtTests.cs` |

**Epic description**, given to the epic review only (section 6.1): *"Widget names
are case- and whitespace-insensitive: the store keeps each name normalised
(trimmed, lower-case), and a client can always look a widget up by the name it
created it with, in any case or spacing. Widgets record their creation time."*
Normalisation is part of the contract, so A is correct by definition and the only
cross-ticket defect is B's raw lookup. The first wording left normalisation out,
and the pilot showed the reviewer then objects to A itself (section 6.6).

### 3.2 The seeds

| Seed | Design | Why it works |
|---|---|---|
| **Cross-ticket defect** (A with B) | A stores `"Alpha"` as `"alpha"`; B looks up ordinally. Combined, `GET /widgets/by-name/Alpha` returns 404 for a widget created as `"Alpha"` | Each ticket is correct against its own text. A ticket review sees only its own diff: A's has no lookup, B's has no normalisation. The aggregate diff has both, a few lines apart. B's tests use lower-case names only, so **no test catches it**; otherwise C2's rerun would, and E2 would measure the tests, not the review |
| **Merge conflict** (A with C) | Both rewrite `var widget = new Widget(_nextId++, name);`. Correct resolution: `new Widget(_nextId++, Normalise(name), DateTimeOffset.UtcNow)` | A and C start together from the same base (section 3.3), so the second to merge conflicts in `scm.merge_base_forward`. B and C also both edit `Program.cs`; any conflict there is resolved by the same step and reported |

If the agents do not collide, the harness commits a conflicting edit to the
integration branch before C's forward merge, in a short second run, reported as
**injected**.

### 3.3 Order and timing

- `branches: [ticket_a, ticket_c, ticket_b]`: with `max_concurrency: 2`, A and C
  start first from the same head; B when one finishes. Checked from child start
  times (C6).
- Each child creates its own branch at its first step, so B contains whichever of
  A or C has merged. That does not affect the defect.
- The harness approves `gate_merge` for A and C **at the same moment**, forcing
  real contention for the lock (C1).

### 3.4 Oracles

Agents may not do what the ticket says, so before any review result is reported
the harness checks the integration branch with a throwaway, uncommitted test:

| Seed | Oracle | If it fails |
|---|---|---|
| Defect | `Add("Alpha")` then `FindByName("Alpha")` returns null | E2 is void for this run; reported, not re-interpreted |
| Conflict | A forward-merge step recorded `blocked`/`conflict` on `WidgetStore.cs` | C3 uses the injected conflict (section 3.2) |
| Resolution | `Add("  Beta ")` gives name `beta` and a `CreatedAt` within a second of now | C3 not met: the resolution dropped one side |

## 4. Plans and the merge lock

### 4.1 Epic plan

A reduction of `docs/13` section 2: fixed tickets, no fix path, no delivery. The
run ends at the epic review so the aggregate diff stays fixed for section 6.

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata: { name: spike-3-epic, scope: epic, revision: 1 }
parameters: { epic_id: { type: string, required: true }, repositories: { type: array, required: true } }
steps:
  - id: tickets
    kind: parallel
    inputs: { branches: [ticket_a, ticket_c, ticket_b], join: all, max_concurrency: 2 }
    routes: { completed: epic_review, any_failed: gate_epic_escalate }
  - id: ticket_a
    kind: run.child
    inputs: { child_scope: ticket, plan_ref: "spike-3:templates/ticket-delivery",
              parameters: { ticket_id: "EPIC3-A", repositories: [ai-feature-flow/feature-flow-example] } }
  - id: ticket_c
    kind: run.child
    inputs: { child_scope: ticket, plan_ref: "spike-3:templates/ticket-delivery",
              parameters: { ticket_id: "EPIC3-C", repositories: [ai-feature-flow/feature-flow-example] } }
  - id: ticket_b
    kind: run.child
    inputs: { child_scope: ticket, plan_ref: "spike-3:templates/ticket-delivery",
              parameters: { ticket_id: "EPIC3-B", repositories: [ai-feature-flow/feature-flow-example] } }
  - id: epic_review
    kind: review.run
    inputs: { scope: epic, diff_ref: { expr: "run.aggregate_diff" },
              checks: ["feature-flow-default:checks/epic-review"], prior_findings: { expr: "findings.all" } }
    routes: { completed: { end: completed }, findings_open: gate_epic_findings,
              failed: gate_epic_escalate, attempts_exhausted: gate_epic_escalate }
  - id: gate_epic_findings
    kind: human.gate
    gate: { decision_type: epic.findings_review }
    routes: { approved: { end: completed }, rejected: { end: failed } }
  - id: gate_epic_escalate
    kind: human.gate
    gate: { decision_type: epic.escalation }
    routes: { approved: { end: failed }, rejected: { end: failed } }
```

Branches carry no routes (`docs/09` 7.0). `run.aggregate_diff` is
`main...ff/epic/spike-3` at the integration head when the step starts.

### 4.2 Ticket plan: the delta from Spike 2

`spikes/02-real-run/plans/ticket-delivery.yaml` is reused, except that the entry
becomes `create_branch`, `gate_merge` routes `approved: merge_forward`, and four
steps are added:

```yaml
  - id: create_branch
    kind: scm.create_branch
    inputs: { repository: { expr: "run.repositories[0]" }, branch: { expr: "run.branch" },
              from_ref: { expr: "run.integration_branch" } }
    routes: { completed: plan_spec, failed: gate_escalate, attempts_exhausted: gate_escalate }

  - id: merge_forward
    kind: scm.merge_base_forward
    inputs:
      repository: { expr: "run.repositories[0]" }
      branch: { expr: "run.integration_branch" }   # merged from
      into: { expr: "run.branch" }                 # merged into
    outputs: { branch_changed: { type: boolean } }
    routes:
      completed:
        - when: "steps.merge_forward.outputs.branch_changed == true"
          to: recheck
        - to: merge_pr
      blocked: resolve_conflict
      failed: gate_escalate
      attempts_exhausted: gate_escalate

  - id: recheck
    kind: test.run_tiers
    inputs: { tier_tags: { expr: "steps.plan_spec.outputs.tier_tags" } }
    routes: { completed: merge_pr, failed: triage_findings, attempts_exhausted: gate_escalate }

  - id: resolve_conflict
    kind: agent.run
    inputs: { role: "feature-flow-default:roles/implementer",
              task_ref: "spike-3:tasks/resolve-merge-conflict", write_access: branch }
    routes: { completed: run_tests, blocked: gate_escalate, failed: gate_escalate,
              attempts_exhausted: gate_escalate, replan_requested: replan }
```

- `branch` and `into` follow the provider's `MergeBaseForwardAsync(repo, branch,
  into)`; `docs/06` 6.2 does not say which is which (J10).
- After `resolve_conflict` (task: merge locally, keep **both** intents, run the
  unit tier, push, touch nothing else) the ticket goes back through tests, review
  and the gate, so resolution code is never merged unreviewed. `review` sees it
  because Spike 2's `RunReviewAsync` diffs the live branch; its `diff_ref` is
  nominal in the spike.

### 4.3 The merge lock

`docs/06` 6.5 and review 06 S3: per target branch, as a Temporal-native lock
workflow. Smallest form:

| Element | Spike 3 form |
|---|---|
| Lock workflow | Id `lock:ai-feature-flow/feature-flow-example:ff/epic/spike-3`, on the epic queue. Request by signal-with-start, grant and release by signal, FIFO |
| Acquired | By the interpreter, before an `scm.merge_base_forward` step into an integration branch |
| Held across | Only `scm.merge_base_forward`, `test.run_tiers`, `ci.wait_checks` and `scm.merge`. **Never across an agent step or a gate**, so a conflicted ticket's resolver cannot block other merges. An engine rule on step kind, not plan content |
| Released | By the holder when `scm.merge` returns, when its cursor moves to any other kind, or when its run ends. By the lock workflow when the holder workflow is found closed (checked every 30 s), because a terminated workflow cannot release its own lock |
| Audit | Requested, granted, released, with holder and time |

**This differs from `docs/06` 6.5** (lock inside `scm.merge` only), under which a
sibling can land between forward merge and merge, so C2's rerun checks a
combination that is not the one merged. `docs/01` 6.3 reads the wider way. Open
question 1.

## 5. Checks: evidence, and what would make each come out differently

Handover 3.4: a check that passes by construction proves nothing.

| # | Evidence | Would come out differently if | Sanity check of the method |
|---|---|---|---|
| C1 | Lock records and lock-workflow history: holder intervals for the three merges, each covering forward, any rerun and merge. Each GitHub merge commit falls inside its holder's interval. At least one ticket waited | Intervals overlap; a merge commit is outside its holder's interval. If no ticket ever waited, C1 is **inconclusive**, not met | Three dummy holders, 20 s hold: no overlap, waits of about 20 s and 40 s. **Then the same with the lock bypassed: intervals must overlap.** If they do not, the method cannot see a failure |
| C2 | Per ticket: integration head, ticket head before and after forward merge, `branch_changed`, and whether `recheck` ran (step record and its CI dispatch key). Needs one ticket with no change (no rerun) and one with a change (rerun on the new head) | `branch_changed` disagrees with the oracle `git merge-base --is-ancestor <integration head> <ticket head before>`; a rerun on unchanged or none on changed. Only one case seen: half-evidenced | The oracle. The fixture's `unit.yml` also runs on every push to `ff/**`, so the evidence is the engine's step and dispatch key, not the presence of a CI run |
| C3 | A `blocked`/`conflict` record followed by a `resolve_conflict` run, **with no human decision between**. The resolver's commit has two parents, from a recorded Claude Code session. The resolution oracle passes (section 3.4) | Routes to `gate_escalate`; the resolution drops a side; no conflict occurred (then injected, section 3.2) | Hand-made branches with A's and C's edits through `MergeBaseForwardAsync` return `Work/conflict`; two non-conflicting branches return `completed` |
| C4 | The epic review's recorded base (`main`), head SHA and diff hash. Head equals the integration head after the third merge; the diff contains every file each ticket changed | The reviewer got a ticket diff, or a head read before the last merge | None needed: compared against independently recorded SHAs |
| C5 | Section 5.2 | Section 5.2 | Section 5.2 |
| C6 | Child start and close times: never more than two open; A and C first; all three `completed` | Three at once; any other close status | None: `max_concurrency` was exercised in Spike 1 |
| C7 | Section 5.1, plus: the lock is free or held by a sibling whenever `resolve_conflict` runs | A sibling waits on the lock, a slot or the join because of another ticket | I3 |

### 5.1 Injections for C7

Tickets couple only through the lock, the slots, the integration branch and the join.

| # | Injection | Must happen | Would show blocking if |
|---|---|---|---|
| I1 | Kill the worker of the third ticket as its `implement` starts; keep it down until the other in-flight ticket has merged (at least 5 minutes); restart | The other ticket merges while it is down. The killed ticket resumes, not restarts (`docs/15` 3.1), and completes | The other ticket waits on the lock or a slot |
| I2 | Kill the lock holder's worker just after the grant; down 60 s; restart | A query during the outage shows the same holder. On restart the merge completes **once** and the lock is released | The lock is lost, never released, or the PR merges twice |
| I3 | No agents: a three-branch `parallel` of `run.child` with single-step child plans, one returning `failed` | The other two complete; the join reports `any_failed` only after all three are terminal | A failed branch cancels or stalls its siblings |
| I4 | *Optional, only if time.* No agents: terminate a lock holder's workflow while another waits | The lock workflow releases within one check interval and grants the waiter | The lock stays held by a closed workflow |

### 5.2 C5: pre-submit check parity (R17)

`docs/01` 7.4 and `docs/06` 3.2. Spike 3 builds only the engine side: a CLI,
`ff-presubmit --worktree <path> --plan <ticket plan> --step review`, which
resolves the named step's validator and inputs **through the same code path as
the gated step**, run from an interactive Claude Code session in a ticket's
worktree.

An LLM check does not repeat itself (section 6), so parity is evidenced on what
is deterministic: the session path **reaches** the same validator with the same
inputs.

| Evidence | Would come out differently if |
|---|---|
| The hash of the fully rendered reviewer input (prompt version, diff hash, prior findings, model) is identical between `ff-presubmit` and the gated `review` at the same head | The session path assembles anything differently: base ref, prior findings, prompt or model |
| `findings.evaluate_readiness` at the same ledger state is byte-identical | Same, for a pure function |

Sanity check: with the base set to `main`, and with prior findings omitted, the
hash must differ from the gated step's.

**Stated plainly:** if `ff-presubmit` calls the gated step's code path, an
identical hash is close to guaranteed. The sanity check proves the hash covers the
inputs, not that parity is hard to achieve. C5 is evidenced at the level a spike
can reach, and `docs/18` will say so rather than claim more.

## 6. R4: measuring the epic review deliberately

`docs/16` 2: four passes on unchanged input gave 1, 1, 1, 2 findings, re-raising
a fixed one. So E2 is a rate, compared across scopes, with controls.

### 6.1 Inputs held constant

Both scopes: same prompt, runtime, model and `checks`, and a reviewer **with no
repository access**. A ticket review is given **its ticket text**; the epic review
is given **the epic description**. Each review checks against its own
specification, as it would in use. Output gains a one-sentence `message`. Every
pass records its rendered-input hash, so "unchanged input" is checked.

This is a change from the first version of this plan, which gave the epic
description to both scopes. The pilot (section 6.6) showed that a ticket review
given the epic contract catches B's departure from it 8 times in 10. That is a
finding about the design, recorded in section 6.6, but it means the defect is no
longer cross-ticket: it is B's ticket text contradicting the epic.

### 6.2 The series

| Series | Diff | Passes | Prior findings | Measures |
|---|---|---|---|---|
| In-run | Aggregate, as the plan runs it | 1 | `findings.all` | What the engine did |
| **Pos** | Aggregate plus a comment beside `FindByName` saying names are stored lower-cased | 5 | None | Can the reviewer and rubric see the obvious case |
| **Neg** | Aggregate with `FindByName` comparing against `Normalise(name)`, and B's case-sensitivity test replaced by one that agrees with the contract: the contract holds for case and whitespace (oracle with `"Alpha"` and `"  Alpha "`) | 20 | None | False detection rate *f* |
| **I-epic** | Aggregate, after the run | 20 | None | *d*<sub>epic</sub> |
| **I-A**, **I-B** | The diff each of A's and B's in-run `review` saw | 20 each | None | *d*<sub>A</sub>, *d*<sub>B</sub> |
| **Chain** | Aggregate | 5 | The ledger after the previous pass | *Optional, only if time.* Re-raised, new and dropped findings |

Pos and Neg are the sanity check and run first: if Pos misses, the method cannot
see the defect; if Neg detects as often as I-epic, the reviewer reacts to words.
91 `haiku` passes in total.

### 6.3 Classification and counting

Fixed before the run; every finding is dumped for manual audit and disagreements
reported. A finding **detects the seed** if its `rule`, `symbol`,
`snippet` or `message` mentions case or normalisation (`case`, `lower`,
`ToLower`, `normalis`, `normaliz`, `Ordinal`, `IgnoreCase`), **and** lookup by
name (`FindByName`, `by-name`, `lookup`, `look up`, `look-up`), **and** a failure
(`cannot`, `can't`, `not found`, `404`, `miss`, `fail`, `return(s) null`, `no
longer`, `never`, `break`, `won't`). A pass detects if any finding does. The
failure term was added after the pilot: without it, a finding about duplicate
names after normalisation matched the rubric (section 6.6).

Chain is optional. If it runs, then with the `docs/01` 7.2 fingerprint, each finding in pass *k* is
**re-raised** (fingerprint open after *k*−1), **re-raised, drifted** (not
fingerprint-equal, but the same issue by rubric or audit), or **new**; each ledger
finding not raised is **dropped**. Also reported: how many distinct fingerprints
the seed receives across all passes, which measures fingerprint drift.

### 6.4 What E2 needs

Set now, so the result cannot choose them:

| Condition | Threshold |
|---|---|
| Epic review detects | *d*<sub>epic</sub> ≥ 10/20 |
| Ticket review misses (else the seed is too easy) | *d*<sub>A</sub> ≤ 2/20 and *d*<sub>B</sub> ≤ 2/20 |
| Controls valid | Pos ≥ 4/5 and *f* ≤ 2/20 |
| Rates differ | One-sided Fisher exact test, epic against each ticket series, *p* < 0.05. 10/20 against 2/20 gives *p* ≈ 0.007; 5/10 against 1/10 gives only *p* ≈ 0.07, hence 20 passes |

Results give exact counts with 95% Wilson intervals (10/20 is 0.30 to 0.70).
Failed controls make E2 **inconclusive**, not failed (`docs/16` 3).

### 6.5 The findings ledger: version 1 position

`docs/16` 8, still-open 1. **Accepted by the product owner, 2026-10-30, as the
version 1 design position now**, not conditional on Spike 3's numbers. Spike 3
reports what its numbers say about each row, but does not reopen them.

| # | Position | What Spike 3's numbers could change |
|---|---|---|
| L1 | **The ledger is the source of truth; a review pass is evidence**, as with *events wake, reads confirm* (`docs/04` 1.3.1) | — |
| L2 | **"Fixed" is sticky, enforced by the engine**, as "declined" already is (`docs/06` 9.1). When `fix.verify` passes, the ledger records the location (path, enclosing symbol) and that symbol's code hash after the fix. A later finding there is a recorded re-raise, not acted on, while the hash is unchanged. Cost: a genuinely new issue in an unchanged fixed symbol waits until the code changes | Location drift in Chain: the engine cannot recognise the re-raise and L3 must absorb it |
| L3 | **New findings on unchanged input are kept; no agreement voting in version 1.** Two-of-three voting keeps a real finding with probability 3*d*²−2*d*³: 0.5 at *d* = 0.5, 0.22 at *d* = 0.3. Noise costs a triage; a suppressed defect costs a wrong outcome | Low *f* and high *d*<sub>epic</sub> make voting cheap. Results report what two-of-three would have done to both |
| L4 | **At epic scope `findings.all` includes the children's ledgers**, so the engine matches epic findings against ticket findings by L2's rule. Telling the reviewer too is harmless but, per Spike 2, does not stop re-raises | — |

### 6.6 Pilot, run before building (2026-10-30)

Before any engine work, the review series was piloted with no engine at all. The
three tickets were written by hand on a copy of the fixture, and their diffs were
reviewed through `claude -p --model haiku` with Spike 2's reviewer prompt (plus
`message`), 10 passes per variant. Artefacts: `spikes/03-concurrent/pilot/`.

Seeds checked first, with real tests on the .NET 10 runtime
(`DOTNET_ROLL_FORWARD=Major`): every ticket branch, the aggregate and Neg pass
their own unit tests. The section 3.4 defect oracle **fails on the aggregate**
(the defect is present and no test catches it) and **passes on Neg**.

| Variant | Diff | Review given | v1: first epic wording, first rubric | v2: sections 3.1 and 6.3 as now |
|---|---|---|---|---|
| epic | Aggregate | Epic description | 10/10 | **10/10** |
| neg | Neg | Epic description | **10/10** (control failed) | **0/10** |
| A+epic | A | Ticket A and epic description | **8/10** | 0/10 |
| B+epic | B | Ticket B and epic description | 0/10 | **8/10** |
| A-only | A | Ticket A text | 0/10 | **0/10** |
| B-only | B | Ticket B text | 0/10 | **0/10** |

Total reported cost: about $0.05 for each 60-pass run.

**What it changed.**

1. **v1's Neg control failed.** The first epic wording ("look up by the name it
   created it with") made A's normalisation itself a violation, so the reviewer
   flagged every diff containing A, correct lookup or not. That was a defect in the
   seed, not in the reviewer. Putting normalisation into the contract fixed it:
   Neg went from 10/10 to 0/10. The rubric gained a failure term at the same time
   (section 6.3).
2. **Which specification a ticket review is given decides whether a defect is
   cross-ticket.** Given the epic contract, whichever ticket departs from it is
   caught by its own review: A in v1 (8/10), B in v2 (8/10). Given only its ticket
   text, neither is (0/10 each). So ticket reviews get the ticket text (section
   6.1, J4).
3. **This is a design finding.** If ticket reviews are routinely given the epic's
   contract, a class of cross-ticket defects becomes a per-ticket finding — caught
   earlier and cheaper. What remains for the epic review is the defects no single
   ticket's diff can show. Recorded for the design, not built in Spike 3.
4. **E2 looks reachable.** 10/10 against 0/10 is well past the section 6.4
   thresholds at n = 10; the run uses n = 20 as planned. The pilot does not replace
   the in-run evidence: there, agents author the tickets, and the reviewer sees
   what they actually wrote.

## 7. Building it

### 7.1 Reused and new

| Reused unchanged | New (smallest delta) |
|---|---|
| Interpreter: `parallel`, `max_concurrency`, guarded routes, `applyPatch` (Spike 1) | **N0 Loops: a step that already has an outcome can run again when routed to.** `PlanReady.Ready` skips any step with an outcome, so the section 4.2 conflict path (and Spike 2's fix loop) dead-ends. Found by worker A during the Claude Code proof; its fix lands first, and Spike 1's replay tests are re-run after it |
| Every ticket activity, the GitHub code host and CI providers, credential broker, cost, decision and findings records, F1 in `scm.merge`, harness gate approval (Spike 2) | N1 `run.child` as a Temporal child workflow, id `<epic run id>/<step id>` (`docs/06` 4.1) |
| `ticket-delivery.yaml`, with the section 4.2 delta | N2 Lock workflow with holder-liveness check; acquire and release in the interpreter (section 4.3) |
| Worker A's `claude-code` runtime on `haiku`, every role including reviewer | N3 `scm.create_branch` and `scm.merge_base_forward` steps; the latter publishes `branch_changed` |
| The pilot's diffs, oracle and rubric (section 6.6) | N4 Sync the worktree to the remote head at every step start, since the forward merge is server-side (`docs/01` 5.1.2) |
| | N5 Epic plan, `resolve-merge-conflict` task, decision types `epic.findings_review` and `epic.escalation` in the organisation (run-start checks 20 to 22 refuse unknown types) |
| | N6 Reviewer: `message` field, ticket text or epic description by scope (section 6.1). The `docs/01` 7.2 fingerprints only if Chain runs |
| | N7 `ff-presubmit` (section 5.2) |
| | N8 Harness: worktrees and workers, gate timing, injections, R4 series, oracles, evidence |

Shared spike code changes wait until worker A's work is on `main`.

### 7.2 Per-ticket isolation

Agents run natively, so concurrent tickets must not share a checkout: **one `git
worktree` per ticket** (a native local-process "sandbox") and **one worker per
ticket on its own queue**, its `RunContext` holding that ticket's worktree, branch
and base as Spike 2's worker does. The epic worker runs the parent, the lock and
the epic review with `RunContext` on the integration branch against `main`, so
`RunReviewAsync` already yields the aggregate diff. Per-ticket queues are a spike
device, not a design proposal.

| Worker | Worktree | Branch | Queue |
|---|---|---|---|
| Epic | `…/spike-3/epic` | `ff/epic/spike-3` | `spike-3-epic` |
| A, B, C | `…/spike-3/ticket-a` (`-b`, `-c`), detached until the child creates its branch | `ff/ticket/spike-3-a` (`-b`, `-c`) | `spike-3-ticket-a` (`-b`, `-c`) |

Accepted limits: **no confinement** (a native agent can write a sibling's
worktree; the evidence checks each ticket's commits touch only its files); worktrees
**share Git metadata** (if concurrent fetches contend on ref locks, use one clone
per ticket); the GitHub token is per repository and the Claude login is visible to
every agent (`docs/08` 1.3 R1). Docker sandboxing (`docs/01` 1.4) would add
confinement and per-ticket credentials; it is not in scope.

### 7.3 Infrastructure and run sequence

| Item | Value |
|---|---|
| Temporal | `~/.temporalio/bin/temporal server start-dev --port 7533 --ui-port 8533 --db-filename <spike-3 dir>/temporal.db --headless` (Spike 1: 7233; Spike 2: 7333) |
| .NET | .NET 10 for everything. The fixture stays `net9.0`, untouched, and its CI is unchanged; the local unit tier runs on the .NET 10 runtime with `DOTNET_ROLL_FORWARD=Major` set in each ticket worker's environment. Known gap: local tests run on a newer runtime than CI |
| Credentials | The existing GitHub App, from a secrets file outside the repository (`CONTRIBUTING.md`, local setup); the Anthropic subscription login, via worker A |
| CI | The fixture's existing workflows |

1. Prerequisites: worker A merged, including N0 and a green Spike 1 replay;
   Temporal on 7533; four worktrees; `ff/epic/spike-3` from `main`; confirm the
   fixture's unit tier passes locally with `DOTNET_ROLL_FORWARD=Major` (done in
   the pilot, section 6.6).
2. Method sanity checks: lock (obvious and bypassed), conflict, I3.
3. The main run, with I1, I2 and the simultaneous approval. Then I4 if time.
4. Oracles (section 3.4). If a seed is absent, stop and report.
5. Pos and Neg, then I-epic, I-A and I-B; then C5. Chain only if time.
6. Write `docs/18-spike-3-results.md`.

The cross-platform requirement is design requirement R5 in `docs/08` 1.3. Spike 3
does not evidence it.

## 8. Not in scope

Docker or VM sandboxes and agent confinement (section 7.2); Windows and Linux
(`docs/08` 1.3 R5); hook-based per-tool-call policy (`docs/16` 4); the epic fix
path and delivery to `main`; `human.session` as a step and its MCP server (C5
needs only the engine-side action); ticket expansion and `replan_parent`
(`docs/09` 7.0); the scheduler's preference for non-overlapping areas (`docs/01`
6.3) — Spike 3 wants overlap; code host merge queues; lock leases and fairness
beyond FIFO; several repositories; epic continue-as-new; B4 and the K4
decline-path re-run (`docs/16` 9 and 6; review 16: only if needed, and they are
not); Copilot CLI.

## 9. Judgement calls made without being asked

| # | Call | Alternative |
|---|---|---|
| J1 | The lock spans forward, rerun and merge, never an agent step or gate (section 4.3) | `docs/06` 6.5: inside `scm.merge` only |
| J2 | One worker process and queue per ticket (section 7.2) | One worker, context per run |
| J3 | Prescriptive ticket texts and separate test files (section 3.1) | Natural texts, accepting the seeds may not appear |
| J4 | A ticket review is given its ticket text and the epic review the epic description; the reviewer is diff-only (section 6.1). Changed after the pilot (section 6.6) | Both scopes get the epic description, which turns this seed into a per-ticket finding |
| J5 | E2 is a rate over 20 passes with controls, thresholds and a Fisher test (section 6.4) | One in-run pass, as `docs/08` 2.3 literally reads |
| J6 | A resolved ticket is re-tested and re-reviewed in full (section 4.2) | Merge straight after resolution |
| J7 | The run ends at the epic review, with no fix path (section 4.1) | `docs/13` section 2's fix loop, which changes the R4 input |
| J8 | The harness approves ticket merge gates, as in Spike 2 | A human; F1 does not require it |
| J9 | A conflict that does not occur naturally is injected and reported as such (section 3.2) | C3 not met |
| J10 | `scm.merge_base_forward`: `branch` is the integration branch, `into` the ticket branch, as in the provider (section 4.2) | The opposite reading of `docs/06` 6.2 |
| J11 | The lock workflow releases a lock whose holder has closed, checked every 30 s (section 4.3) | A lease with expiry and renewal |
| J12 | Each child creates its own branch (section 4.2) | Harness-created branches, giving B the same base as A and C |
| J13 | I4 is optional (section 5.1) | Required |
| J14 | The epic contract states that names are normalised (section 3.1), so A is correct by definition. Made by the orchestrator after the pilot's v1 control failed | The first wording, under which A itself violates the contract |
| J15 | Chain and the `docs/01` 7.2 fingerprints are optional (section 6.2). Made by the orchestrator: they feed the ledger question, which is now decided (section 6.5), not the spike's question | Required |

## 10. Open questions

None open.

### Answered

| Question | Answer |
|---|---|
| Merge lock span | **Hold it across forward, rerun and merge** (section 4.3); amend `docs/06` 6.5 after Spike 3. Product owner, 2026-10-30. **Superseded 2026-10-31:** the lock is dropped for version 1; see `docs/20-merge-design.md` |
| The findings ledger (`docs/16` 8, still-open 1) | **L1 to L4 accepted as the version 1 design position now** (section 6.5). Product owner, 2026-10-30 |
| E2 thresholds | **Accepted** (section 6.4), conditional on the pilot, which supports them (section 6.6). Product owner, 2026-10-30 |
| Starting the build | **After the pilot and the N0 interpreter fix; then build, run and report without stopping unless a major decision is needed.** Product owner, 2026-10-30 |
| Record the cross-platform requirement? | **Decided by the product owner, 2026-10-30.** Recorded as R5 in `docs/08` 1.3 |
| .NET version | **.NET 10 for everything** (product owner); `DOTNET_ROLL_FORWARD=Major` for the fixture (section 7.3) |
