# Validation rules and workflow testing

Status: second draft. Date: 2026-10-27. Deliverables 4 and 5 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 09 applied.** The blocking-versus-warning **rule** is stated (F2). The
missing checks are added (F3). Unbound versus undeclared references are separated
by phase (F1). Temporal replay and re-driving a run against recorded provider
responses are separated (G1). The test file format is defined (G2). Tests live
with the library package they test (answer 10). Golden tests are for the readable
form only (answer 11). No runtime warnings (answer 12).

Companion to `docs/09-workflow-language.md` section 16, which states the rule,
and `docs/13-workflow-examples.md`, which these tests exercise.

---

## 1. The blocking rule

Review 09 F2. Brief lesson 7: warnings about logic were easy to ignore.

> **Anything that can change routing or execution blocks. Everything else warns,
> and warnings are cosmetic only.**

That is the whole rule. It is stated as one sentence because a rule that needs a
table to explain is a rule that gets argued with.

Consequences:

- A warning never changes behaviour. It is recorded in the run start record and
  shown to the author.
- **A template cannot register a runtime warning.** Validation is static only
  (review 09, answer 12). There is no mechanism for a template to emit a warning
  during a run, because a warning that appears mid-run is a warning nobody acts
  on.
- If a check is not in the blocking list below, it is a warning, and it is
  cosmetic.

---

## 2. Validation table

| # | Check | Phase | Blocks | Why |
|---|---|---|---|---|
| 1 | Schema conformance (`docs/10`) | Load | Yes | — |
| 2 | Unknown step kind | Load | Yes | Routing |
| 3 | Duplicate step id | Load | Yes | Routing |
| 4 | Step id looks like a step kind | Load | Yes | Ambiguity with directives (B2) |
| 5 | **Undeclared** reference (`params.foo` not declared) | Load | Yes | Type error |
| 6 | **Unbound** required parameter | Binding | Yes | Would reach an agent as literal text (brief lesson 6) |
| 7 | Expression parses | Load | Yes | Routing |
| 8 | Expression type checks | Load | Yes | Routing |
| 9 | Expression within cost and length bounds | Load | Yes | Determinism, heartbeat |
| 10 | **Every outcome the kind can return is routed** | Load | Yes | Routing |
| 11 | Route target is a step id that exists, or a directive | Load | Yes | Routing |
| 12 | A guarded route list ends with an unguarded fallback | Load | Yes | Routing |
| 13 | **Unreachable step** | Load | Yes | Execution |
| 14 | **Every cycle has a route out** | Load | Yes | Execution |
| 15 | **Producer and consumer contract compatibility** | Load | Yes | Execution |
| 16 | Consumed artifact's producer step exists and declares it | Load | Yes | Execution |
| 17 | Library reference resolves | Binding | Yes | Execution |
| 18 | Package `requires` satisfied | Binding | Yes | Execution |
| 19 | Repository layer adds only, never overrides | Binding | Yes | D9 |
| 20 | Capability negotiation against configured providers | Run start | Yes | S3 |
| 21 | `gate.decision_type` is known to `policy.approval` | Run start | Yes | L12 |
| 22 | `limits.attempts` within `policy.attempts.ceiling` | Run start | Yes | F4 |
| 23 | Patch: `base_revision` is current | Patch | Yes | E3 |
| 24 | Patch: operation is permitted to the proposer | Patch | Yes | E6 |
| 25 | Patch: protected step not removed, weakened or **made unreachable** | Patch | **Three results** | F3, E1, E2, review 10 B2 |
| 26 | Patch: result passes every load check | Patch | Yes | — |
| 27 | Step inputs satisfy the kind's contract | Step start | Yes | Execution |
| 28 | Consumed artifact exists and validates | Step start | Yes | Execution |
| 29 | Unused parameter | Load | **No** | Cosmetic |
| 30 | Deprecated library reference with a valid replacement | Load | **No** | Cosmetic |
| 31 | Step id not in snake_case style | Load | **No** | Cosmetic |

Checks 10, 13, 14, 15 and 25 are the ones review 09 F3 said were missing.

### 2.2 Check 25 has three results, not two

Review 10 B2. A patch has three possible outcomes, and the table's "blocks"
column cannot express the middle one:

| Result | When | Effect |
|---|---|---|
| **Applied** | The patch is valid and touches nothing protected | A new plan revision |
| **Pending human decision** | An **agent** patch touches protected content, or uses a role outside `policy.replan.allowed_roles` | Recorded with the diff and reason; a human may approve with a reason (E2) |
| **Rejected** | The patch would relax a safety floor, or is invalid, or `base_revision` is stale | Recorded, not silently dropped (R7) |

Only the third is a hard block. The second is a **wait**, and it is the case the
prototype got wrong by either silently applying or silently refusing.

### 2.1 On check 6

The first draft checked "unresolved `${...}`" at template load. That is wrong: a
template is *supposed* to have unbound parameters. The split is check 5 (load)
versus check 6 (binding).

---

## 3. Testing

### 3.1 Two different things

Review 09 G1. The first draft conflated them.

| | Temporal replay | Re-driving against recorded providers |
|---|---|---|
| What it tests | **Interpreter code** changed | **A template** changed |
| Input | A recorded Temporal history | Recorded provider responses |
| Same code? | Yes — that is the point | No — the template differs |
| Same history? | Yes | No; a new history is produced |
| Catches | Non-determinism in workflow code (R2, R12) | A routing or content regression |
| Where | Engine CI | Library package CI |

Both are needed. Neither substitutes for the other.

### 3.2 Test types

| Type | Tests | Where |
|---|---|---|
| Schema and validation | Every template in the package passes checks 1-19; negative cases for each blocking check | Library package |
| Replay | Recorded histories replay against a changed interpreter | Engine CI |
| Re-drive | A recorded provider script re-drives a changed template | Library package |
| Golden, readable form | The rendered readable form (`docs/09` D7) is unchanged | Library package |
| Failure injection | Worker crash, Temporal unavailable, protected-step patch, stale `base_revision` | Engine CI, Spike 1 |

### 3.3 The test file format

Review 09 G2. One format, five fields.

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
  plan_spec: completed
  design: completed
  implement: completed
  run_tests: failed
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
| `provider_script` | Step id to outcome, **per visit**. A scalar means every visit; a list gives one outcome per visit in order. A step not listed returns `completed` |
| `expected_path` | The ordered list of step ids the run executes |
| `expected_status` | `completed`, `failed`, `partial`, `rejected`, or `null` for still in flight |
| `expected_validation_errors` | Expected blocking errors, by check number |

**Per-visit scripting** (review 10, answer). A step may be visited more than once
— the test, fix, retest loop is the normal case — so a single outcome per step id
cannot express a loop. `review: [findings_open, completed]` says the first visit
opens findings and the second passes. Without this, no loop can be tested.

**Error classes too** (review 10, answer). A scripted value may be an outcome or
an error class, so the attempt rules can be exercised:

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

With the library package they test (review 09, answer 10). A package ships its
own tests; the engine ships the harness. That is what lets an organisation's
distribution repository prove its overrides still work.

### 3.6 Golden tests

Functional expectations — resulting path and validation result — are the norm.
**Golden files are used only for the readable form** (review 09, answer 11),
because that is the one output where a diff is genuinely informative to a human
and to a planner agent.

---

## 4. The readable form (D7)

Review 09 D7. Brief lesson 2: planners read a readable form and submit small
patches. Reading the plan back as one-line JSON cost minutes per step in the
prototype.

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

Rules (review 10 B4):

- One line per step: sequence, id, kind, role, **effective `write_access`**,
  status, and every **non-default** route on one compact line.
- **Every** step's routes are shown, not only the current step's. An agent patches
  routes and inputs of steps other than the one running, so it needs to see them.
- The effective `write_access` is shown, which keeps review 06 answer 1's "floor
  visible in the plan" true in the form the agent actually reads.
- Protected steps are marked.
- **Inputs stay hidden.** An agent asks for a step's detail by id rather than
  having the whole plan inlined — that is what keeps the form small enough to
  read in a context window, which was the prototype's problem.
- No artifact hashes, no expressions. The agent patches by step id.
- Generated by the engine from the plan. It is not a second representation an
  author maintains.

This is the only golden-tested output (section 3.6).

---

## 5. Open questions

- **Replay history storage.** Recorded histories contain references only (payload
  codec, `docs/07`), are recorded from fixture repository runs (review 08, answer
  3), and are stored with the engine tests, with a CI check that rejects anything
  matching the secret scanner (review 10, answer).
- **Per-branch expected paths.** A `parallel` step's branches interleave, so
  `expected_path` is the **parent's** path and each branch gets its own
  `expected_branch_paths` entry. No interleaving assertions (review 10, answer).
  Is one path per branch enough, or do concurrent branches in one repository need
  an ordering assertion after all?
