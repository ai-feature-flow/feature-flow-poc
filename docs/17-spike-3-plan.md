# Spike 3 plan: concurrent tickets

Written 2026-10-30. **A plan for review, not a result.** Nothing here is built.

Review 16: *"Propose the Spike 3 plan (`docs/08` 2.3, concurrent tickets with the
seeded cross-ticket defect) in light of what Spikes 1 and 2 changed, before
building it."*

Product owner direction for this plan: do the minimum needed to prove what the
spike is for, on this Mac, now. It does not need to be cross-platform and will not
be re-run. Agents run as native processes (no Docker sandbox) on `claude-code`
with model `haiku` for every role, pending worker A's proof of that runtime on the
Spike 2 code (`docs/16`). Tickets are deliberately short and simple.

| Deliverable when built | Where |
|---|---|
| Spike code | `spikes/03-concurrent/` (new), plus the changes to shared code in section 7.2 |
| Results | `docs/18-spike-3-results.md` |
| Fixture repository | `ai-feature-flow/feature-flow-example`, integration branch `ff/epic/spike-3` |

---

## 1. Question and exit criteria

**Question** (`docs/08` 2.3): do several tickets of one epic in one repository
stay correct?

**Setup.** One epic, three tickets, one repository, `max_concurrency: 2`. At least
two tickets touch overlapping code. A seeded cross-ticket defect gives the
epic-level review something to find.

**Exit criteria** (`docs/08` 2.3), unchanged:

| # | Criterion | Evidenced in |
|---|---|---|
| E1 | Three tickets complete | Section 5, C6 |
| E2 | The epic-level review sees cross-ticket findings that per-ticket review missed | Section 6 |
| E3 | No ticket is blocked by another's failure | Section 5, C7 |

**The five checks** (`docs/08` 2.3), plus two that the exit criteria need:

| # | Check | Risk |
|---|---|---|
| C1 | Ticket branches merge into the integration branch serially, under the merge lock | R10 |
| C2 | `scm.merge_base_forward` runs before merge, and checks rerun only when the branch changed | R10 |
| C3 | A merge conflict routes to an agent step, not to a human | R10 |
| C4 | Epic-level review runs on the aggregate diff of the integration branch | R10 |
| C5 | An interactive session can run a pre-submit check and get the same findings the gated step would produce | R17 |
| C6 | `max_concurrency: 2` holds, and all three children reach `completed` | E1 |
| C7 | A ticket that stops making progress blocks neither a sibling's work nor its merge | E3 |

---

## 2. What Spikes 1 and 2 changed

| From | What it means for this plan |
|---|---|
| `docs/16` 11, item 1: the provider seam holds | New behaviour goes behind `ISpikeActivities` and into the interpreter; the workflow shape is not redesigned |
| `docs/16` 4 and 6: Claude Code could not reach OpenRouter | Superseded by direction: `claude-code` on `haiku` through the Anthropic subscription, if worker A proves it. Copilot CLI is not used |
| `docs/16` 2 (R4) and 8, still-open 1: verdicts are unstable | E2 cannot be a single pass. It is a **detection rate** over repeated passes, with controls (section 6) |
| `docs/16` 9: a claimed result was canned content | Every agent step in the run must be model-authored and traceable to a Claude Code session and usage record. The seeds are confirmed present by an independent oracle, not assumed (section 3.5) |
| `docs/16` 10, R2: F1 is enforced at merge time | Spike 3 never merges into `main`. Every merge targets `ff/epic/spike-3`, so F1 is not engaged and not re-litigated |
| `docs/15` 3: interruptions became continuations | The merge lock is a new place to be interrupted. One injection covers it (section 5, I3) |
| Spike 2 code: `RealWorker` binds one ticket per process | `RunContext` fixes the checkout, branch and base at worker start (`WorkerProgram.cs`). Concurrency therefore needs one worker per ticket, or a context per run (section 7.3) |
| Spike 2 code: finding ids are positional | `ReviewerModel` names findings `f-{stepId}-{index}`, not by fingerprint. Counting re-raised findings needs the `docs/01` 7.2 fingerprint, which is new (section 7.2) |

What Spike 1 and 2 code already provides: `parallel` with `max_concurrency` and
guarded routes in the interpreter; `MergeBaseForwardAsync` in the GitHub code host
provider, returning `BranchChanged` and mapping HTTP 409 to `Work/conflict`; the
whole ticket plan and its activities. What it does not: `run.child`, the merge
lock, and any wiring of `scm.merge_base_forward` as a step.

---

## 3. The epic

### 3.1 Three tickets

Base: `main` of the fixture, which has `WidgetStore` with `All`, `Find`, `Add` and
`Count`, and endpoints `GET /widgets`, `GET /widgets/{id}` and `POST /widgets`.
The tickets are worded as below, deliberately prescriptive, so the seeds land
where they are designed to.

| Ticket | Text given to the agents | Files it should touch |
|---|---|---|
| **A** `EPIC3-A` normalise names | "Names are stored normalised. In `WidgetStore.Add`, normalise the name with a new private static `Normalise(string)` that trims and lower-cases it (invariant culture), and use it **inside the `new Widget(...)` expression**. Add unit tests in a new file `WidgetNameNormalisationTests.cs`: `Add("  Alpha ")` returns a widget named `alpha`." | `WidgetStore.cs`, new test file |
| **B** `EPIC3-B` find by name | "Add `WidgetStore.FindByName(string name)`, placed **after `Count()`**, returning the first widget whose name equals `name` using an **exact, ordinal (case-sensitive) comparison**, or null. Add `GET /widgets/by-name/{name}` returning 200 or 404. Add unit tests in a new file `WidgetFindByNameTests.cs`: `Add("alpha")` then `FindByName("alpha")` returns it; `FindByName("ALPHA")` returns null." | `WidgetStore.cs`, `Program.cs`, new test file |
| **C** `EPIC3-C` creation time | "Widgets record when they were created. Add `DateTimeOffset CreatedAt` as the third member of the `Widget` record, and set it to `DateTimeOffset.UtcNow` **inside the `new Widget(...)` expression in `WidgetStore.Add`**. Add unit tests in a new file `WidgetCreatedAtTests.cs`." | `Program.cs`, `WidgetStore.cs`, new test file |

Each ticket uses its **own new test file**, so the tickets do not collide on the
end of `WidgetStoreTests.cs`. Without this, all three would conflict pairwise and
the conflict check would measure noise.

**Epic description**, given to every review at both scopes (section 6.1): *"Widget
names: a client can always look up a widget by the name it created it with.
Widgets record their creation time."*

### 3.2 The seeded cross-ticket defect: A and B

A stores `"Alpha"` as `"alpha"`. B looks names up with an ordinal comparison. Each
ticket is correct against its own text, and every unit test passes in both
tickets and in the aggregate. Combined, a client that creates `"Alpha"` gets 404
from `GET /widgets/by-name/Alpha`, which breaks the epic's stated contract.

Why per-ticket review should not see it: a ticket review is given the diff of the
ticket branch against the integration branch, and the reviewer is diff-only (as
Spike 2's `ReviewerModel` is). A's diff contains no lookup; B's diff contains no
normalisation. Why the epic review should: the aggregate diff contains
`Normalise(name)` in `Add` and `StringComparison.Ordinal` in `FindByName`, a few
lines apart in one file.

**Why it is not detectable by tests.** B's specified tests use lower-case names
only. This is deliberate: if a test caught it, C2's check rerun would catch it at
merge time, and E2 would measure the test suite, not the review.

### 3.3 The forced merge conflict: A and C

A and C both rewrite the same line, `var widget = new Widget(_nextId++, name);`,
in `WidgetStore.Add`: A to `Normalise(name)`, C to add `DateTimeOffset.UtcNow`.
Both branch from the same integration head (section 3.4), so whichever merges
second gets a conflict from `scm.merge_base_forward`. The correct resolution keeps
both: `new Widget(_nextId++, Normalise(name), DateTimeOffset.UtcNow)`.

B and C both edit `Program.cs`, in different places (a new endpoint; the record
line). Any extra conflict this produces is resolved by the same agent step and
reported, not suppressed.

**If the agents do not collide** (for example, A normalises on a separate line and
Git merges cleanly), C3 is **not met in the main run** and is exercised in a
second, short run in which the harness commits a conflicting edit to the
integration branch between C's review and its merge. The results say which
happened. A harness-made conflict is legitimate failure injection; it is reported
as such, not as a natural conflict.

### 3.4 Order and timing

| Choice | Why |
|---|---|
| `branches: [ticket_a, ticket_c, ticket_b]` | With `max_concurrency: 2`, the first two start together from the same integration head. That is what makes A and C conflict. B starts when the first of them finishes |
| Ticket branches are created **by the child**, at its first step, from the integration head at that moment (section 4.2) | So A and C share a base, and B, starting later, contains whichever of them has merged. It does not affect the defect: B's text fixes ordinal comparison |
| The harness approves `gate_merge` for A and C **at the same moment**, once both are waiting | Forces real contention for the lock (C1). Without it, two merges minutes apart serialise trivially |

The interpreter is assumed to start branches in list order. That is checked, not
assumed: child start times are recorded (C6).

### 3.5 Confirming the seeds are present

Model-authored work may not do what the ticket says. After the run, and before
any review result is reported, an **oracle** checks the integration branch:

| Seed | Oracle | If absent |
|---|---|---|
| Cross-ticket defect | A throwaway test, run by the harness and never committed: `Add("Alpha")` then `FindByName("Alpha")` returns null | E2 is void for this run. Reported, not re-interpreted |
| Conflict | The forward-merge step's outcome was `blocked` with reason `conflict`, and the conflicting file is `WidgetStore.cs` | C3 falls back to the section 3.3 injection |
| Resolution kept both intents | The same throwaway test: `Add("  Beta ")` returns name `beta` and a `CreatedAt` within a second of now | C3 is not met: the agent resolved the conflict by dropping one side |

---

## 4. Plans

### 4.1 Epic plan

A reduction of `docs/13` section 2: no ticket expansion (the three tickets are
fixed), no epic fix path, no delivery. The run ends after the epic review so the
aggregate diff stays unchanged for the R4 series (section 6).

```yaml
apiVersion: feature-flow/v1
kind: Plan
metadata:
  name: spike-3-epic
  scope: epic
  revision: 1

parameters:
  epic_id: { type: string, required: true }
  repositories: { type: array, required: true }

steps:
  - id: tickets
    kind: parallel
    inputs:
      branches: [ticket_a, ticket_c, ticket_b]
      join: all
      max_concurrency: 2
    routes:
      completed: epic_review
      any_failed: gate_epic_escalate

  - id: ticket_a
    kind: run.child
    inputs:
      child_scope: ticket
      plan_ref: spike-3:templates/ticket-delivery
      parameters: { ticket_id: "EPIC3-A", repositories: ["ai-feature-flow/feature-flow-example"] }

  - id: ticket_c
    kind: run.child
    inputs:
      child_scope: ticket
      plan_ref: spike-3:templates/ticket-delivery
      parameters: { ticket_id: "EPIC3-C", repositories: ["ai-feature-flow/feature-flow-example"] }

  - id: ticket_b
    kind: run.child
    inputs:
      child_scope: ticket
      plan_ref: spike-3:templates/ticket-delivery
      parameters: { ticket_id: "EPIC3-B", repositories: ["ai-feature-flow/feature-flow-example"] }

  - id: epic_review
    kind: review.run
    inputs:
      scope: epic
      diff_ref: { expr: "run.aggregate_diff" }
      checks: [feature-flow-default:checks/epic-review]
      prior_findings: { expr: "findings.all" }
    routes:
      completed: { end: completed }
      findings_open: gate_epic_findings
      failed: gate_epic_escalate
      attempts_exhausted: gate_epic_escalate

  - id: gate_epic_findings
    kind: human.gate
    gate: { decision_type: epic.findings_review }
    inputs:
      context_refs: { expr: "[steps.epic_review.artifacts.findings]" }
    routes:
      approved: { end: completed }
      rejected: { end: failed }

  - id: gate_epic_escalate
    kind: human.gate
    gate: { decision_type: epic.escalation }
    routes:
      approved: { end: failed }
      rejected: { end: failed }
```

Branches carry no routes (`docs/09` 7.0). `run.aggregate_diff` is the diff of
`main...ff/epic/spike-3` at the integration head when the step starts.

### 4.2 Ticket plan: the delta from Spike 2

`spikes/02-real-run/plans/ticket-delivery.yaml` is reused unchanged except that
the entry becomes a new `create_branch` step, `gate_merge` routes
`approved: merge_forward` instead of `approved: merge_pr`, and four steps are
added:

```yaml
  - id: create_branch
    kind: scm.create_branch
    inputs:
      repository: { expr: "run.repositories[0]" }
      branch: { expr: "run.branch" }
      from_ref: { expr: "run.integration_branch" }
    routes:
      completed: plan_spec
      failed: gate_escalate
      attempts_exhausted: gate_escalate

  - id: merge_forward
    kind: scm.merge_base_forward
    inputs:
      repository: { expr: "run.repositories[0]" }
      branch: { expr: "run.integration_branch" }   # merged from
      into: { expr: "run.branch" }                 # merged into: the ticket branch
    outputs:
      branch_changed: { type: boolean }
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
    inputs:
      tier_tags: { expr: "steps.plan_spec.outputs.tier_tags" }
    routes:
      completed: merge_pr
      failed: triage_findings
      attempts_exhausted: gate_escalate

  - id: resolve_conflict
    kind: agent.run
    inputs:
      role: feature-flow-default:roles/implementer
      task_ref: spike-3:tasks/resolve-merge-conflict
      write_access: branch
    routes:
      completed: run_tests
      blocked: gate_escalate
      failed: gate_escalate
      attempts_exhausted: gate_escalate
      replan_requested: replan
```

`scm.merge_base_forward`'s `branch` and `into` follow the provider's
`MergeBaseForwardAsync(repo, branch, into)`, which merges `branch` into `into`.
`docs/06` 6.2 does not say which input is which; this is the reading the code
already has.

After `resolve_conflict` the ticket goes back through `run_tests`, `review` and
`gate_merge`, then `merge_forward` again. The resolved branch is therefore tested
and reviewed like any other change. It costs one more review; it avoids a special
path that would merge unreviewed resolution code. This relies on the reused
`review` step reviewing the branch as it is now: Spike 2's `RunReviewAsync`
computes the diff of the ticket branch against the integration branch when the
step starts, not from `steps.implement.artifacts.diff`, so it does see the
resolution. The plan's `diff_ref` expression is therefore nominal in the spike;
in the language it should name the current branch diff, not `implement`'s.

The resolver's task: fetch the integration branch, merge it into the ticket branch
locally, resolve keeping **both** intents, run the unit tier, commit and push. It
must not touch files outside the conflict.

### 4.3 The merge lock

`docs/06` 6.5 and review 06 S3: per target branch (repository plus branch), as a
Temporal-native mutex, a lock workflow per target branch. Spike 3 builds the
smallest form of that:

| Element | Spike 3 form |
|---|---|
| Lock workflow | One per target, id `lock:ai-feature-flow/feature-flow-example:ff/epic/spike-3`, on the epic queue. Requests arrive by signal-with-start; the holder is the requesting workflow id; grants are a signal back to the requester; release is a signal. FIFO |
| Acquire | By the interpreter, before running an `scm.merge_base_forward` step whose `branch` is an integration branch |
| Held across | Only steps of kind `scm.merge_base_forward`, `test.run_tiers`, `ci.wait_checks` and `scm.merge` |
| Released | By the holder: when `scm.merge` returns any outcome, when the cursor moves to a step of any other kind, or when the run ends normally. **By the lock workflow**: when the holder workflow is found closed (terminated, cancelled, timed out), checked by an activity every 30 s while the lock is held. A terminated workflow runs no code, so it cannot release its own lock |
| Audit | Acquire requested, granted, released, with holder and timestamps, as records and as lock-workflow history |

**The span is a proposal that differs from `docs/06` 6.5**, which says *"the step
acquires, merges, releases"*, which is to say inside `scm.merge` only. `docs/01` 6.3
reads the other way: merges are serialised by the lock, and *before merging*,
forward and rerun. The difference matters. If the lock covers only the merge,
another ticket can land between this ticket's forward merge and its merge, so the
combination that was checked is not the combination that lands, and C2's rerun
checks the wrong thing. Holding it across forward, rerun and merge makes C2
meaningful, at the cost of serialising check reruns.

**The lock is never held across an agent step or a human gate.** That rule is what
keeps a conflicted ticket, whose resolver may run for minutes, from blocking every
other merge (C7). It is an engine mechanism keyed on step kind, so no plan can
hold the lock across a gate. Open question 1.

---

## 5. Checks: evidence, and what would make each come out differently

Handover 3.4: a check that passes by construction proves nothing. Each row says
what would make it fail, and where an obvious case should be reproduced first,
the sanity check that does it.

| # | Evidence | Would come out differently if | Sanity check first |
|---|---|---|---|
| C1 | Lock-workflow history and lock records: holder intervals for the three merges, each covering forward, any rerun, and merge. GitHub's integration-branch history: one merge commit per ticket, with timestamps inside the holder's interval. **At least one waiter waited** (from the simultaneous approval, section 3.4) | Intervals overlap; a merge commit falls outside its holder's interval; or no ticket ever waited, in which case the check is reported **inconclusive**, not met | Three dummy holders contend for the lock with a 20 s hold. Intervals must not overlap, and the second and third wait about 20 s and 40 s. **Then the same with the lock bypassed**: intervals must overlap. If the bypassed run also shows no overlap, the method cannot see a failure |
| C2 | Per ticket: integration head, ticket head before and after the forward merge, `branch_changed`, and whether `recheck` ran (step record and a CI dispatch with that step's idempotency key). Needs **both** cases: one ticket whose forward merge changed nothing (no rerun) and one whose forward merge changed the branch (rerun on the new head SHA) | `branch_changed` disagrees with an independent oracle: `git merge-base --is-ancestor <integration head> <ticket head before>` (true means no change expected). Or a rerun on unchanged, or none on changed. Or only one case occurs, which is reported as half-evidenced | None beyond the oracle. Note: the fixture's `unit.yml` also runs on every push to `ff/**`, so GitHub reruns on its own. The claim is about the **engine's** step, so the evidence is the engine's step record and its dispatch key, not the presence of a CI run |
| C3 | The forward-merge step's `blocked`/`conflict` record, followed by a `resolve_conflict` step run, with **no human decision recorded in between**. The resolver's commit is a two-parent merge, authored in the ticket's worktree by a Claude Code session whose id and usage are recorded. The section 3.5 oracle passes | The agent cannot resolve and routes to `gate_escalate`; the resolution drops one side (oracle fails); or the conflict was never produced (falls back to the section 3.3 injection, reported as injected) | Before the run: two hand-made branches applying A's and C's edits to the same line, merged with the provider's `MergeBaseForwardAsync`, must return `Work/conflict`. And two non-conflicting branches must return `completed` |
| C4 | The epic review's recorded input: base `main`, head SHA, diff hash. The head equals the integration head after the third merge, and the diff contains every file each ticket changed | The reviewer was given a ticket diff, or a head read before the last merge | None needed: the comparison is against two independently recorded SHAs |
| C5 | Section 5.1 | Section 5.1 | Section 5.1 |
| C6 | Child workflow start and close times: never more than two open at once; A and C start first; all three close `completed` | Three run at once; or one closes other than `completed` | None: the interpreter's `max_concurrency` was exercised in Spike 1 |
| C7 | Injections I1 to I4 below, including I3b | Below | Below |

**Injections for C7.** Siblings cannot tell a dead ticket from a failed one: the
only couplings between tickets are the lock, the concurrency slots, the
integration branch, and the join. I1 to I3 exercise the first three on the real
run; I3b and I4 cover lock ownership and the join, cheaply and without agents.

| # | Injection | Must happen | Would show blocking if |
|---|---|---|---|
| I1 | Kill the worker of the ticket that starts third as soon as its `implement` starts, and keep it down until the other in-flight ticket has merged (at least 5 minutes). Then restart it | The other ticket reaches `merge_pr` and merges while the killed one is down. The killed ticket then resumes and completes (a continuation, not a restart: `docs/15` 3.1) | The other ticket waits on the lock or a slot while its sibling is down |
| I2 | None: observed. The conflicted ticket's resolver runs while its sibling is ready to merge | The lock is free, or is taken by the sibling, while `resolve_conflict` runs | The lock is held by the conflicted ticket during `resolve_conflict` |
| I3 | Kill the lock holder's worker immediately after the lock is granted, keep it down 60 s, restart | A lock-workflow query during the outage shows the same holder. On restart the holder's merge completes **once** (one merge commit for that PR) and the lock is released | The lock is lost (another ticket acquires it mid-merge), never released, or the PR is merged twice |
| I3b | A short run without agents: one workflow takes the lock and is terminated while holding it; a second is waiting | The lock workflow records the holder closed, releases within one check interval, and grants the waiter | The lock stays held by a closed workflow |
| I4 | No real providers. A three-branch `parallel` of `run.child` steps whose child plans are a single local step, one of which returns `failed` | The other two children run to `completed`; the join reports `any_failed` only after all three are terminal | A failed branch cancels or stalls its siblings under `join: all` |

### 5.1 C5: pre-submit check parity (R17)

`docs/01` 7.4 and `docs/06` 3.2: a session can run the validators of later gated
steps on demand, with the same structured findings. Spike 3 builds only the
engine side: a CLI command,

```
ff-presubmit --worktree <path> --plan <ticket plan> --step review
```

which resolves the named step's validator and its inputs **through the same code
path the gated step uses**, runs it, and prints the findings. It is run from an
interactive Claude Code session in a ticket's worktree, which is surface A in
`docs/01` 7.4 reduced to its smallest form.

"The same findings" cannot mean identical output for an LLM check: section 6 is
measuring exactly how far two passes on identical input disagree. Parity is
therefore evidenced at three levels:

| Level | Evidence | Would come out differently if |
|---|---|---|
| Same inputs | The hash of the fully rendered reviewer input (prompt version, diff hash, prior findings, model) is **identical** between the pre-submit call and the gated `review` step at the same ticket head | The session path assembles anything differently: another base ref, prior findings omitted, another prompt or model |
| Same deterministic result | For `findings.evaluate_readiness` at the same ledger state, the result is byte-identical | Same as above, for a pure function |
C5 is met on the first two levels. Identical input through the same validator
is close to parity by construction, which is why the sanity check below matters:
the claim worth testing is that the session path **reaches** the same validator
with the same inputs, and the hash is what could show it does not. Five
pre-submit passes are also classified with the section 6.3 rubric and reported
beside the gated step's, as information only: with R4 as it is, no
acceptance rule on two small samples would be falsifiable.

Sanity check: run the pre-submit command with the base deliberately set to `main`
instead of the integration branch, and again with prior findings omitted. Each
input hash must differ from the gated step's. If either does not, the hash does
not cover that input and cannot evidence parity.

---

## 6. R4: measuring the epic review deliberately

`docs/16` 2 (R4) found four passes on unchanged input going 1, 1, 1, 2 findings,
with a fixed finding re-raised. E2 therefore cannot be "the epic review found it
once". It is a rate, compared across scopes, with controls.

### 6.1 Inputs held constant

Both scopes use the **same reviewer prompt**, the same runtime and model
(`claude-code`, `haiku`), the same `checks` text, the same epic description
(section 3.1), and a reviewer with **no repository access**: it sees only the
diff. The only difference between scopes is the diff. If the epic review were
given more context than the ticket review, E2 would measure the context, not the
scope.

The reviewer's output schema gains one field, `message` (one sentence), for both
scopes. The Spike 2 schema has only `rule`, `path`, `symbol`, `snippet` and
`blocking`, which is too little to classify by.

### 6.2 The series

A **pass** is one `review.run` call with fully fixed input. All passes record the
rendered-input hash, so "unchanged input" is checked, not asserted.

| Series | Diff | Passes | Prior findings | Measures |
|---|---|---|---|---|
| In-run | Aggregate, as the epic plan runs it | 1 | `findings.all` | What the engine actually did |
| **I-epic** | Aggregate, after the run | 20 | None | Detection rate at epic scope, *d*<sub>epic</sub> |
| **I-A**, **I-B** | The diff each of A's and B's in-run `review` steps saw | 20 each | None | Detection rate at ticket scope, *d*<sub>A</sub>, *d*<sub>B</sub> |
| **Neg** | Aggregate with `FindByName` changed by the harness to compare against `Normalise(name)`: the contract holds for case **and** whitespace. Confirmed by the section 3.5 oracle with `"Alpha"` and `"  Alpha "` | 20 | None | False detection rate *f* |
| **Pos** | Aggregate with a comment beside `FindByName` saying names are stored lower-cased | 5 | None | Whether the reviewer and rubric can detect the obvious case |
| **Chain** | Aggregate, after the run | 5 | The ledger after the previous pass, as the engine would pass it | Re-raised, new and dropped findings on unchanged input |

Neg and Pos are the method's sanity check. If Pos does not detect, the
measurement cannot see the defect at all. If Neg detects as often as I-epic, the
reviewer is reacting to the words "case" and "name", not to the defect. Both are
run **before** I-epic.

Reviews with `haiku` on diffs this size are cheap; 91 passes in total.

### 6.3 Classifying a finding as the seeded defect

Fixed before the run and not tuned after seeing output, with every finding dumped
for manual audit. A finding **detects the seed** if its `rule`, `symbol`,
`snippet` or `message`:

1. refers to case or normalisation (any of `case`, `lower`, `ToLower`, `normalis`,
   `normaliz`, `Ordinal`, `IgnoreCase`); **and**
2. refers to lookup by name (any of `FindByName`, `by-name`, `lookup`, `look up`).

A pass detects the seed if any of its findings does. Disagreements between the
rubric and manual audit are reported, not resolved silently.

### 6.4 Counting findings across passes

The ledger's fingerprint is the `docs/01` 7.2 one: rule, repository, path,
enclosing symbol and normalised snippet hash, never line numbers. For each Chain
pass *k*, each finding is exactly one of:

| Class | Definition |
|---|---|
| Re-raised | Its fingerprint is open in the ledger after pass *k*−1 |
| Re-raised, drifted | Not fingerprint-equal to any ledger finding, but the rubric (or manual audit, for non-seed findings) says it is the same issue. Measures **fingerprint drift** |
| New | Neither |
| Dropped | Counted per ledger finding: open after pass *k*−1, not raised in pass *k* |

Also reported: the number of distinct fingerprints the seeded defect receives
across all passes. If the model names the same issue differently each time, a
fingerprint-keyed ledger cannot recognise a re-raise, and the ledger question
below has to be answered differently.

### 6.5 What E2 needs

Proposed thresholds, set now so the result cannot pick them:

| Condition | Threshold | Why |
|---|---|---|
| Epic review detects | *d*<sub>epic</sub> ≥ 10/20 | Detection is the usual case, not an occasional one |
| Ticket review misses | *d*<sub>A</sub> ≤ 2/20 and *d*<sub>B</sub> ≤ 2/20 | If one side alone reveals it, the seed is too easy and E2 shows nothing |
| Controls valid | Pos ≥ 4/5, *f* ≤ 2/20 | Otherwise the rate is not about the defect |
| Epic and ticket rates differ | One-sided Fisher exact test, epic against each ticket series, *p* < 0.05 | 10/20 against 2/20 gives *p* ≈ 0.007; at 10 passes, 5/10 against 1/10 gives only *p* ≈ 0.07, which is why the series are 20 passes |

Results report exact counts with 95% Wilson intervals (10/20 is 0.30 to 0.70;
2/20 is 0.03 to 0.30), not only rates. If the controls fail, E2 is
**inconclusive**, not failed, and the results say why. A gap of one or two passes
is not read as a difference (`docs/16` 3 made the same point).

### 6.6 A proposed position on the findings ledger

`docs/16` 8, still-open 1: is R4 instability acceptable for version 1, or does the
review loop need a stability mechanism? Proposed, for the product owner to decide
(open question 2), with what Spike 3's numbers would change:

| # | Position | What would change it |
|---|---|---|
| L1 | **The ledger is the source of truth; a review pass is evidence.** The same principle as *events wake, reads confirm* (`docs/04` 1.3.1): a pass proposes findings, the engine decides their status | — |
| L2 | **"Fixed" is sticky in the same way "declined" already is, and the engine enforces it.** When `fix.verify` passes, the ledger records the finding's **location** (path and enclosing symbol) and the hash of that symbol's code **after** the fix. A later finding at the same location is a re-raise while that hash is unchanged: recorded, not acted on. The snippet hash cannot do this, because the fix changes the snippet. This extends `docs/06` 9.1's decline rule to fixes and removes Spike 2's re-raised-after-fixed loop without relying on the prompt | Drift in *location*, not only in rule or snippet (section 6.4): then the engine cannot recognise the re-raise at all, and L3 alone has to absorb it. The cost of L2 as stated: a genuinely new issue in an unchanged, already-fixed symbol is suppressed until that code changes |
| L3 | **A new finding on unchanged input is kept and acted on. No agreement voting in version 1.** Requiring two agreeing passes suppresses noise, but it also suppresses real defects the reviewer finds only sometimes: with a per-pass rate *d*, two-of-three agreement keeps a real finding with probability 3*d*²−2*d*³, which at *d* = 0.5 is still 0.5 and at *d* = 0.3 is 0.22. Noise costs a triage (decline with a reason, `docs/06` 9.1); a suppressed real defect costs a wrong outcome | A low *f* and a high *d*<sub>epic</sub> would make voting cheap; a high *f* would make noise expensive. The results report what two-of-three would have done to *d*<sub>epic</sub> and *f* on the measured passes |
| L4 | **At epic scope, `findings.all` includes the children's ledgers**, so the **engine** can match an epic finding against ticket findings by L2's rule. Passing them to the reviewer as well is harmless, but Spike 2 showed it does not stop re-raises; the engine's match is what does | — |

---

## 7. Building it

### 7.1 Reused unchanged

| From | What |
|---|---|
| `spikes/01-interpreter` | The interpreter: `parallel`, `max_concurrency`, guarded routes, `applyPatch` |
| `spikes/02-real-run` | Activities for every ticket step; `GitHubCodeHostProvider` including `MergeBaseForwardAsync` and `GetDiffAsync`; the credential broker; cost, decision and findings records; F1 enforcement in `scm.merge`; the harness's gate approval |
| `spikes/02-real-run/plans/ticket-delivery.yaml` | The ticket plan, with the section 4.2 delta |
| Worker A | The `claude-code` runtime on `haiku` for every role, including the reviewer |

### 7.2 New, the smallest delta

| # | Piece | Where |
|---|---|---|
| N1 | `run.child` as a Temporal child workflow (`docs/01` 3.2), id `<epic run id>/<step id>`, idempotent by that id (`docs/06` 4.1), reporting its terminal outcome to the join | Interpreter |
| N2 | Merge lock workflow with its holder-liveness check, and acquire and release in the interpreter by the section 4.3 rule | Interpreter |
| N3 | `scm.create_branch` and `scm.merge_base_forward` as steps, over the existing provider methods; the latter publishes `branch_changed` | `RealWorker` |
| N4 | Sync the worktree to the remote branch head at the start of every step. The forward merge is server-side, so the local checkout is otherwise stale (`docs/01` 5.1.2: facts are read per step) | `RealWorker` |
| N5 | The `resolve-merge-conflict` task, the epic plan, and the decision types `epic.findings_review` and `epic.escalation` in the organisation policy (run-start checks 20 to 22 refuse unknown ones) | `spikes/03-concurrent/plans/` |
| N6 | Reviewer: the `message` field, the epic description in the prompt, and `docs/01` 7.2 fingerprints in place of positional ids | `RealWorker` |
| N7 | `ff-presubmit` CLI (section 5.1) | `spikes/03-concurrent/` |
| N8 | Harness: worktrees and per-ticket workers, gate timing, injections I1 to I4, the R4 series, the oracles, evidence collection | `spikes/03-concurrent/` |

Changes to `spikes/01-interpreter` and `spikes/02-real-run` start **after worker
A's runtime work is merged to `main`**, because worker A owns
`spikes/02-real-run/**` until then.

### 7.3 Per-ticket isolation

Agents run as native processes on this Mac, so two concurrent tickets must not
share a checkout. The minimum is **one `git worktree` per ticket**, a native
local-process "sandbox":

| Ticket or role | Worktree | Branch | Worker queue |
|---|---|---|---|
| Epic | `…/spike-3/epic` | `ff/epic/spike-3` | `spike-3-epic` |
| A | `…/spike-3/ticket-a` | `ff/ticket/spike-3-a` | `spike-3-ticket-a` |
| B | `…/spike-3/ticket-b` | `ff/ticket/spike-3-b` | `spike-3-ticket-b` |
| C | `…/spike-3/ticket-c` | `ff/ticket/spike-3-c` | `spike-3-ticket-c` |

All from one local clone of the fixture, outside the design repository.

**One worker process per ticket, on its own queue.** The child workflow for a
ticket is started on that ticket's queue, and that worker's `RunContext` holds the
ticket's worktree, branch and base, which is exactly what Spike 2's worker already
does for one ticket. The epic worker runs the parent workflow, the lock workflow
and the epic review, with `RunContext` pointing at the integration branch against
`main`, so the existing `RunReviewAsync` already computes the aggregate diff. The
alternative, a context per run inside one worker, is the production shape but a
larger change, and proves nothing more here. The per-ticket queue is a spike
device; it is not a design proposal.

Known limits of the worktree form, accepted for the spike and recorded rather than
fixed:

- **No confinement.** A native agent with tools can read or write a sibling's
  worktree. Isolation is by convention and by branch. The evidence guards against
  it after the fact: each ticket's commits must touch only that ticket's files.
- **Shared Git metadata.** Worktrees share one object store and ref namespace, so
  concurrent fetches can contend on ref locks. If that happens, fall back to one
  clone per ticket; the cost is disk only.
- **Credentials.** The GitHub token is per repository, not per branch, and the
  Claude Code subscription login is visible to every agent process (design
  requirement R1 in `docs/08` 1.3 already records the model-credential gap).

**Docker sandboxing** (`docs/01` 1.4, pattern C) would give confinement and
per-ticket credentials. It is **not in scope**: it adds infrastructure without
changing what Spike 3 proves.

### 7.4 Minimum infrastructure

| Item | Spike 3 value |
|---|---|
| Temporal dev server | `~/.temporalio/bin/temporal server start-dev --port 7533 --ui-port 8533 --db-filename <spike-3 dir>/temporal.db --headless`. Spike 1 used 7233 and `spike-1`; Spike 2 used 7333 and `spike-2-real` |
| Queues | `spike-3-epic`, `spike-3-ticket-a`, `spike-3-ticket-b`, `spike-3-ticket-c` |
| Fixture branches | `ff/epic/spike-3` from `main`; ticket branches as above. `main` is never written |
| .NET | The spike code targets .NET 10. The fixture targets .NET 9 and this Mac has no .NET 9 runtime, so the local unit tier cannot run. Install the .NET 9 runtime user-local beside the existing SDKs |
| Credentials | The existing GitHub App, from a local secrets file outside the repository (`HANDOVER-spike-3.md` section 5). The Anthropic subscription login on this Mac, via worker A |
| CI | The fixture's existing GitHub Actions workflows. Nothing new |

### 7.5 Run sequence

1. Prerequisites: worker A merged; .NET 9 runtime; Temporal on 7533; four
   detached worktrees (each ticket's branch is created by its child, section 3.4);
   integration branch created.
2. Sanity checks: the lock (C1 obvious case and bypassed case), the conflict (C3),
   the join (I4).
3. The main run, with I1 and I3, and the simultaneous approval for A and C.
   Then the short I3b run.
4. The section 3.5 oracles. If a seed is absent, stop and report before step 5.
5. The reviewer controls Pos and Neg (built from the real aggregate diff), then
   the R4 series I-epic, I-A, I-B and Chain; then C5's parity runs.
6. Write `docs/18-spike-3-results.md`.

---

## 8. Design requirement recorded, not built

Product owner direction, 2026-10-30. Proposed as the fifth entry in the design
requirements of `docs/08` 1.3:

> **Development on Windows or macOS; runtime on Windows, macOS or Linux
> containers.** .NET itself is cross-platform. Docker is used only for packaging
> and for sandboxes, never as a requirement for developing or running the engine.

Spike 3 runs on macOS only and does not evidence this.

---

## 9. Not in scope

| Item | Why |
|---|---|
| Docker or VM sandboxes, and agent confinement | Section 7.3. Worktrees are enough to keep concurrent tickets off one checkout |
| Running on Windows or Linux | Section 8. One run, on this Mac |
| Hook-based per-tool-call policy | Not needed to answer the question; `docs/16` 4 records the hook evidence |
| The epic fix path and delivery to `main` | The run ends after the epic review so the aggregate diff stays fixed. F1 is untouched |
| `human.session` as a step, and an MCP server for the session | C5 needs only the engine-side action and one interactive invocation |
| Ticket expansion by `plan.replan`, and `replan_parent` | Fixed tickets. `replan_parent` is already specified (`docs/09` 7.0) and is not what Spike 3 asks |
| The scheduler's preference for non-overlapping code areas (`docs/01` 6.3) | Spike 3 wants overlap |
| Code host merge queues (`MergeQueue`) | The engine's lock is the default for integration branches (`docs/01` 6.3) |
| Lock leases, fairness beyond FIFO, and locks at scale | One lock, three holders |
| Several repositories, continue-as-new of the epic | Spikes 1 and 2 cover them |
| B4 and the K4 decline-path re-run (`docs/16` 9 and 6) | Review 16: only if Spike 3 needs them. It does not |
| Copilot CLI | Direction: every role on `claude-code` |

---

## 10. Judgement calls made without being asked

| # | Call | Alternative |
|---|---|---|
| J1 | The lock spans forward, rerun and merge, and is never held across an agent step or gate (section 4.3) | `docs/06` 6.5: lock inside `scm.merge` only |
| J2 | One worker process and queue per ticket (section 7.3) | One worker with a context per run |
| J3 | Ticket texts are prescriptive about *where* to edit and which test file to use, so the seeds land (section 3.1) | Natural ticket texts, accepting that the seeds may not appear |
| J4 | Both review scopes get the epic description; the reviewer is diff-only at both (section 6.1) | Ticket text only at ticket scope; or repository access for the reviewer, which would let ticket review see the other side once merged forward |
| J5 | E2 is a rate over 20 passes with thresholds, controls and a significance test (section 6.5) | One in-run pass, as `docs/08` 2.3 literally reads |
| J6 | After conflict resolution, the ticket is re-tested and re-reviewed in full (section 4.2) | Merge straight after resolution, which lands unreviewed code |
| J7 | The run ends at the epic review; there is no epic fix path (section 4.1) | `docs/13` section 2's epic fix loop, which would change the input the R4 series needs fixed |
| J8 | Ticket merge gates into the integration branch are approved by the harness, as in Spike 2 | A human approves each. F1 does not require it: no merge targets the default branch |
| J9 | A conflict that does not occur naturally is injected in a second run and reported as injected (section 3.3) | Treat C3 as not met |
| J10 | Install the .NET 9 runtime on this Mac (section 7.4) | Retarget the fixture to .NET 10, which changes its CI; or `DOTNET_ROLL_FORWARD`, which tests on a runtime CI does not use |
| J11 | `scm.merge_base_forward`'s `branch` is the integration branch and `into` the ticket branch, following the provider code (section 4.2) | The opposite reading of `docs/06` 6.2 |
| J12 | The lock workflow releases a lock whose holder workflow has closed, checked every 30 s (section 4.3) | A lease with expiry, which needs a renewal protocol and a duration that suits CI reruns |
| J13 | Each ticket creates its own branch at its first step (`create_branch`, section 4.2), rather than the harness creating all three up front | Harness-created branches, which would give B the same base as A and C |

---

## 11. Open questions

| # | Question | Proposed answer |
|---|---|---|
| 1 | **Merge lock span.** Hold it across forward, rerun and merge (section 4.3), and amend `docs/06` 6.5 to match? Or keep it inside `scm.merge` and accept that the checked combination may not be the merged one? | Hold it across the span; amend `docs/06` 6.5 after Spike 3 |
| 2 | **The findings ledger** (`docs/16` 8, still-open 1). Accept L1 to L4 in section 6.6 as the version 1 position, subject to Spike 3's numbers? In particular L3, no agreement voting | Yes |
| 3 | **E2 thresholds.** Are *d*<sub>epic</sub> ≥ 10/20, ticket ≤ 2/20, Pos ≥ 4/5, *f* ≤ 2/20 and the Fisher test (section 6.5) the right bar for a feasibility claim? | Yes; they are set now so the result cannot choose them |
| 4 | **The cross-platform requirement** (section 8). Record it in `docs/08` 1.3 as worded? | Yes |
| 5 | **Starting the build.** Start as soon as worker A's runtime proof is merged, or wait for review of this plan first? | Wait for review: review 16 asked for the plan before the build |
