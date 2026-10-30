# Validation rules and workflow testing

Status: revised 2026-10-31 for the spike results (`docs/15`, `docs/16`, `docs/18`),
the merge design (`docs/20`) and the code review design (`docs/19`).

This document says which checks run on a workflow, when, and whether each one
blocks; and how workflows and the interpreter are tested. Related documents:

| Document | What it holds |
|---|---|
| `docs/09` section 16 | The validation rule and phases, from the language side |
| `docs/06` | The step contracts the checks compare against |
| `docs/13` | The worked examples these tests exercise |

---

## 1. The blocking rule

> **Anything that can change routing or execution blocks. Everything else warns,
> and warnings are cosmetic only.**

That is the whole rule. It is one sentence because a rule that needs a table to
explain gets argued with. The reason for it: in the prototype, warnings about
logic were easy to ignore.

Consequences:

- A warning never changes behaviour. It is recorded in the run start record and
  shown to the author.
- **A template cannot register a runtime warning.** Validation is static only. A
  warning that appears mid-run is a warning nobody acts on.
- A check not marked as blocking in section 2 is a warning, and cosmetic.
- Some rules cannot be checked before a run and are enforced by the engine as the
  run executes instead (section 2.5). They are not warnings.

---

## 2. Validation table

Phases: **Load** (the template is read), **Binding** (parameters and library
references are resolved), **Run start** (against the configured providers and
policy), **Patch** (a plan change is proposed), **Step start** (just before a step
runs).

| # | Check | Phase | Blocks | Why |
|---|---|---|---|---|
| 1 | Schema conformance (`docs/10`) | Load | Yes | — |
| 2 | Unknown step kind | Load | Yes | Routing |
| 3 | Duplicate step id | Load | Yes | Routing |
| 4 | Step id looks like a step kind | Load | Yes | A step id could be confused with a terminal directive such as `{ end: completed }` |
| 5 | **Undeclared** reference (`params.foo` not declared) | Load | Yes | Type error |
| 6 | **Unbound** required parameter | Binding | Yes | Would reach an agent as literal text (section 2.1) |
| 7 | Expression parses | Load | Yes | Routing |
| 8 | Expression type checks | Load | Yes | Routing |
| 9 | Expression within cost and length bounds | Load | Yes | Determinism, heartbeat |
| 10 | **Every outcome the kind can return is routed** | Load | Yes | Routing. Includes `attempts_exhausted` on every step that is not local and pure, and `base_moved` on `scm.merge` (`docs/09` section 4.2; `docs/06` section 6.5). In Spike 2 an unrouted `failed` on a merge step ended the run looking like a success (`docs/16` section 5) |
| 11 | Route target is a step id that exists, or a directive | Load | Yes | Routing |
| 12 | A guarded route list ends with an unguarded fallback | Load | Yes | Routing |
| 13 | **Unreachable step** | Load | Yes | Execution. A step named in an `expand` mapping's `target` or `on_other` counts as reachable (section 2.3) |
| 14 | **Every cycle has a route out** | Load | Yes | Execution. For example the merge cycle (catch-up, re-test, merge, `base_moved` back to catch-up) leaves through `attempts_exhausted` |
| 15 | **Producer and consumer contract compatibility** | Load | Yes | Execution |
| 16 | Consumed artifact's producer step exists and declares it | Load | Yes | Execution |
| 17 | Library reference resolves | Binding | Yes | Execution |
| 18 | Package `requires` satisfied | Binding | Yes | Execution |
| 19 | Repository layer adds only, never overrides | Binding | Yes | Decision D9: a repository may add skills and checks, never override roles, policies or tasks |
| 20 | Capability negotiation against configured providers | Run start | Yes | Decision S3: a run fails in its first minute, not at step 40 |
| 21 | `gate.decision_type` is known to `policy.approval` | Run start | Yes | Decision L12: a gate names a decision type and policy decides who may approve |
| 22 | `limits.attempts` within `policy.attempts.ceiling` | Run start | Yes | Floor F4: a run sets attempt budgets within the configured ceiling, never above it. The ceiling is also enforced on every execution (section 2.5) |
| 23 | Patch: `base_revision` is current | Patch | Yes | Decision E3: a patch made against an older plan revision is rejected and re-proposed |
| 24 | Patch: operation is permitted to the proposer | Patch | Yes | Decision E6: which operations an agent may propose and which a human may apply |
| 25 | Patch: protected step not removed, weakened or **made unreachable** | Patch | **Three results** (section 2.2) | Floor F3: a protected step cannot be removed or weakened. Making it unreachable counts as weakening (decision E1) |
| 26 | Patch: result passes every load check | Patch | Yes | — |
| 27 | Step inputs satisfy the kind's contract | Step start | Yes | Execution |
| 28 | Consumed artifact exists and validates | Step start | Yes | Execution |
| 32 | **Plan size within the carried-state budget** | Load **and** Patch | Yes | Carried state must stay under 1 MB, or continue-as-new cannot carry it (section 2.4) |
| 29 | Unused parameter | Load | **No** | Cosmetic |
| 30 | Deprecated library reference with a valid replacement | Load | **No** | Cosmetic |
| 31 | Step id not in snake_case style | Load | **No** | Cosmetic |

Check numbers are stable identifiers; other documents cite them. Check 32 was
added after Spike 1 measured carried state, so it is out of numeric order.

### 2.1 On check 6

A template is **supposed** to have unbound parameters, so "unresolved `${...}`"
cannot be a load check. The split is check 5 (an undeclared reference, at load)
versus check 6 (a required parameter left unbound, at binding). In the prototype
an unbound parameter reached an agent as literal text.

### 2.2 Check 25 has three results, not two

A patch that touches protected content has three possible results. A
"blocks: yes or no" column cannot express the middle one:

| Result | When | Effect |
|---|---|---|
| **Applied** | The patch is valid and touches nothing protected | A new plan revision |
| **Pending human decision** | An **agent** patch touches protected content, or comes from a role outside `policy.replan.allowed_roles` | Recorded with the diff and reason; a human may approve it with a reason (decision E2) |
| **Rejected** | The patch would relax a safety floor, is invalid, or has a stale `base_revision` | Recorded, not silently dropped (risk R7 in `docs/08`: a re-plan removing a protected step) |

Only the third is a hard block. The second is a **wait**. The prototype got this
case wrong by either silently applying or silently refusing.

### 2.3 Reachability and expansion

`mode: expand` generates steps that do not exist at load. In `docs/13` section 2
(the epic example), `delivery_gate` is reachable **only** from the `on_other` of
the delivery expansion. A naive reachability check would flag it and block a
valid plan.

> A step is reachable if it is the target of a route, **or** it is named in an
> `expand` mapping's `target` or `on_other`, **or** it is named in a patch
> operation's `target`, `on_other` or `resume_at`.

So check 13 runs over the plan **plus** every `expand` mapping in it. A step
reachable only through an expansion is still validated for its own routes and
outcomes at load, because its shape is known from the mapping's template.

**Patch targets are checked at patch time.** A patch names steps by id, and may
name a step it is itself inserting. So patch targets are resolved against the
plan **as it will be after the patch**:

| Check | When it runs | What it compares against |
|---|---|---|
| Route targets resolve | Load | The plan as loaded, plus `expand` mappings |
| Patch `target`, `on_other` and `resume_at` resolve | **Patch time** | The plan as it will be after the patch is applied |

A patch whose `resume_at` (the step the run resumes at after the patch,
`docs/09` section 15.1.1) does not exist after application is a blocking error at
patch time. It cannot be detected at load and must not be reported as a load
error.

### 2.4 Plan size limit (check 32)

Spike 1 measured carried state (what the workflow carries into each new run at
continue-as-new) at **about 500 bytes per plan step**, stable across
continue-as-new (`docs/15` section 6.1). The continue-as-new input is **one
payload**, so its ceiling is Temporal's 2 MB payload limit, not the 50 MB
history limit. For large plans the payload ceiling binds before the event ceiling.

> **A plan must be small enough that its carried state stays under 1 MB.** At the
> measured 500 bytes per step that is about **2,000 steps**. The check runs at
> load **and** at patch time, because a patch can add steps.

- The limit is a budget in bytes, not a step count, so it stays correct if
  per-step state changes. Half the payload ceiling leaves room for the plan's
  own content, which varies with inputs and routes.
- **The continue-as-new threshold** is separate: Spike 1 recommends **5,120
  events** (about 815 steps at the measured 6.28 events per step), half
  Temporal's warning threshold. It triggers on events **or** bytes, whichever
  comes first.
- **Large work is not meant to hit this.** It already splits into child runs:
  one ticket, one plan (`docs/09` section 13.1, child plans). A plan approaching
  the limit is a signal to split, not to raise the limit.

### 2.5 Enforced as the run executes

These rules depend on what happens during a run, so no static check can prove
them. The engine enforces them; a template cannot turn them off.

| Rule | What the engine does | Evidence |
|---|---|---|
| Attempt ceilings on every execution (DR10) | Each entry into a step counts against its allowance and the run's ceiling, including when a loop routes back. Exhaustion returns `attempts_exhausted` | Without it, a loop with no agent ran its fix step 213 times in 15 seconds (`docs/18` section 4) |
| Re-entry runs the step again (DR12) | Routing to a step that already has an outcome clears that outcome and runs the step | Loops depend on it (`docs/16` section 12) |
| No `completed` without an `end:` route (DR6) | A run that stops with no step ready and no `end:` route never reports `completed` | Spike 2's silent "completed" after an unrouted merge failure (`docs/16` section 5) |
| An unmapped success is a work error (DR8) | A provider response that is a success but maps to no outcome fails the step as work, so it is not retried forever | GitHub's empty 204 for a no-op merge was retried as infrastructure until fixed (`docs/18` section 4) |
| Review coverage (`docs/06` section 8.2) | A review that never opened a changed file is re-run, not accepted | Spike 3's reviewer missed a bug in code it never saw (`docs/18` section 4) |

---

## 3. Testing

### 3.1 Two different things

Temporal replay and re-driving a run against recorded provider responses test
different changes. Both are needed; neither substitutes for the other.

| | Temporal replay | Re-driving against recorded providers |
|---|---|---|
| What it tests | A change to **interpreter code** | A change to **a template** |
| Input | A recorded Temporal history | Recorded provider responses |
| Same code? | Yes: that is the point | No: the template differs |
| Same history? | Yes | No; a new history is produced |
| Catches | Non-determinism in workflow code (risks R2 and R12 in `docs/08`) | A routing or content regression |
| Where | Engine CI | Library package CI |

Replay is proven to catch what it should:

- Spike 1 committed nine recorded histories covering its failure paths (worker
  crash, outage, continue-as-new, a patch while branches were in flight), and all
  nine replay cleanly. They are the engine's first replay suite (`docs/15`
  section 7).
- An interpreter change must be versioned with Temporal `Workflow.Patched`
  (DR7). With the version guard removed from Spike 3's dispatch change, a Spike 1
  history failed replay (`docs/18` section 4).

### 3.2 Test types

| Type | Tests | Where |
|---|---|---|
| Schema and validation | Every template in the package passes checks 1 to 19; a negative case for each blocking check | Library package |
| Replay | Recorded histories replay against a changed interpreter | Engine CI |
| Re-drive | A recorded provider script re-drives a changed template | Library package |
| Golden, readable form | The rendered readable form (section 4) is unchanged | Library package |
| Failure injection | Worker crash, Temporal unavailable, protected-step patch, stale `base_revision`. Spikes 1 and 3 ran these first (`docs/15` section 3; `docs/18` section 2) | Engine CI |
| Run-time rules | Each rule in section 2.5. For example: a loop with no agent stops at its ceiling; a run with no `end:` route does not report `completed` | Engine CI |
| Provider response mapping | Every documented success response of each provider call, including empty ones, maps to an outcome (DR8) | Provider package CI |
| Merge race | Two tickets catch up from the same integration head; the second merge returns `base_moved` and routes back to catch-up; repeated refusals escalate (`docs/06` section 6.5) | Engine CI |
| Pre-submit parity | The pre-submit check and the gated step get the same brief, tools, model and commit (`docs/06` section 3.2) | Engine CI |

### 3.3 The test file format

One format, six fields.

```yaml
apiVersion: feature-flow/v1
kind: WorkflowTest
name: findings-open-routes-to-triage
template: feature-flow-default:templates/ticket-delivery
parameters:
  ticket_id: FF-1042
  repositories: [acme/api]

# Outcome per step id, per visit. A step not listed returns completed.
provider_script:
  review: [findings_open, completed]     # first visit, then second
  triage_findings: completed
  fix_findings: completed
  verify_fix: completed

expected_path: [plan_spec, design, implement, run_tests, review, triage_findings,
                fix_findings, verify_fix, run_tests, review, evaluate_readiness]
expected_status: null          # null means the run is still in flight at the end
expected_validation_errors: []
```

| Field | Meaning |
|---|---|
| `template` | The template under test |
| `parameters` | Bound parameters |
| `provider_script` | Step id to outcome, **per visit**. A scalar means every visit; a list gives one outcome per visit, in order. A step not listed returns `completed` |
| `expected_path` | The ordered list of step ids the run executes |
| `expected_status` | `completed`, `failed`, `partial`, `rejected`, or `null` for still in flight |
| `expected_validation_errors` | Expected blocking errors, by check number |

**Per-visit scripting.** A step may be visited more than once (test, fix, re-test
is the normal case), so a single outcome per step id cannot express a loop.
`review: [findings_open, completed]` says the first visit opens findings and the
second passes.

**Error classes too.** A scripted value may be an outcome or an error class, so
the attempt rules can be exercised:

```yaml
provider_script:
  implement: { error: infrastructure, times: 2 }   # free retries, then completed
```

A test that expects validation errors uses the same file with an empty
`provider_script` and a populated `expected_validation_errors`.

### 3.4 Negative test example

```yaml
apiVersion: feature-flow/v1
kind: WorkflowTest
name: protected-step-bypass-rejected
template: feature-flow-default:templates/ticket-delivery
parameters: { ticket_id: FF-1042 }
patch:
  base_revision: 1
  changes:
    - op: change_route
      target: plan_spec
      outcome: completed
      to: implement          # bypasses the protected design step
expected_validation_errors: [25]
```

### 3.5 Where tests live

With the library package they test. A package ships its own tests; the engine
ships the harness. That lets an organisation's distribution repository prove its
overrides still work.

### 3.6 Golden tests

Functional expectations (the resulting path and validation result) are the norm.
**Golden files are used only for the readable form** (section 4), because that is
the one output where a diff is informative to a human and to a planner agent.

---

## 4. The readable form (D7)

Decision D7: a planner agent reads a readable form of the plan and submits small
patches. In the prototype, reading the plan back as one-line JSON cost minutes
per step.

The readable form is what `plan.replan` gives the agent, and what it patches
against.

```
Plan ticket-delivery rev 7 (ticket FF-1042)
──────────────────────────────────────────────────────────────────────────
 #  id               kind            role         access  status     routes
 1  plan_spec        agent.run       planner      none    completed  completed->design
 2  design           agent.run       designer     none    running    [protected]
 3  implement        agent.run       implementer  branch  pending    completed->run_tests
                                                                     blocked->replan
 4  run_tests        test.run_tiers  -            -       pending    completed->review
                                                                     failed->triage_findings
 5  review           review.run      -            -       pending    findings_open->triage_findings
 6  triage_findings  findings.triage -            -       pending    completed->fix_findings
 7  merge_pr         scm.merge       -            -       pending    completed->end:completed
 8  gate_merge       human.gate      -            -       pending    ticket.merge_approval

Protected: design
```

Rules:

- One line per step: sequence, id, kind, role, **effective `write_access`**,
  status, and every **non-default** route on one compact line.
- **Every** step's routes are shown, not only the current step's. An agent patches
  routes and inputs of steps other than the one running, so it needs to see them.
- The effective `write_access` is shown, so the access floor is visible in the
  form the agent actually reads.
- Protected steps are marked.
- **Inputs stay hidden.** An agent asks for a step's detail by id rather than
  having the whole plan inlined. That keeps the form small enough for a context
  window, which was the prototype's problem.
- No artifact hashes, no expressions. The agent patches by step id.
- Generated by the engine from the plan. It is not a second representation an
  author maintains.

This is the only golden-tested output (section 3.6).

---

## 5. Open questions

### Answered

- **Replay history storage.** Recorded histories contain references only (the
  payload codec, `docs/07`), are recorded from runs against fixture repositories,
  and are stored with the engine tests. A CI check rejects anything the secret
  scanner matches.

### Still open

- **Per-branch expected paths.** A `parallel` step's branches interleave, so
  `expected_path` is the **parent's** path and each branch gets its own
  `expected_branch_paths` entry, with no interleaving assertions. Is one path per
  branch enough, or do concurrent branches in one repository need an ordering
  assertion?
- **The status of a run that stops without an `end:` route.** DR6 says it is never
  `completed`; `expected_status` has no value for it. Spike 3 used `incomplete`
  (`docs/18` section 7).
