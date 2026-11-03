# Validation and testing

Status: draft, 2026-10-31.

This document owns the checks run on templates, plans and patches, the rules
the engine enforces as a run executes, and how workflows, the interpreter and
providers are tested. It carries the first design's approach
(`archive/docs/12`), with the check list rewritten for the new language.

---

## 1. The blocking rule

> **Anything that can change routing or execution blocks. Everything else
> warns, and warnings are cosmetic only.** (P11)

A warning never changes behaviour and is listed in the run start record. A
template cannot add warnings of its own. A rule that can only be enforced during
a run is enforced by the engine (section 3); it is not a warning.

---

## 2. Checks

Phases: **Load** (a template is read, fragments and `for_each` expanded),
**Run start** (parameters bound, library locked, configuration known),
**Patch** (a plan change is proposed), **Step start**. Check numbers are stable;
other documents cite them.

| # | Check | Phase | Blocks |
|---|---|---|---|
| 1 | Schema conformance | Load | Yes |
| 2 | Unknown action, unknown construct | Load | Yes |
| 3 | Duplicate step id, or an id reused from the run's history for a different step | Load, Patch | Yes |
| 4 | Undeclared parameter referenced | Load | Yes |
| 5 | Expression parses, type-checks and is within its bounds (`docs/03` section 5.2) | Load | Yes |
| 6 | An agent step or decision declares at least one outcome; no step declares `attempts_exhausted` | Load | Yes |
| 7 | **Every outcome has a route after defaults are applied** (`docs/03` section 4.3) | Load | Yes |
| 8 | Every route target exists, or is a directive; a guarded list ends with a fallback | Load | Yes |
| 9 | Every step is reachable from the first | Load | Yes |
| 10 | Every cycle has a route out | Load | Yes |
| 11 | Each consumed artifact has a producer declaring a compatible type, or is `optional`, or comes from a parent, root or child | Load | Yes |
| 12 | No agent step or decision produces an evidence type (F7) | Load | Yes |
| 13 | A step's `write_access` does not exceed its role's; no step sets tools (F10) | Load | Yes |
| 14 | Fragments: parameters bound, every exit routed, nesting depth at most 3 | Load | Yes |
| 15 | `parallel` branches have no routes | Load | Yes |
| 16 | Every decision type is declared by a library in the run (`docs/05` section 4) | Load | Yes |
| 17 | Plan size: carried state under 1 MB, about 2,000 steps (section 2.1) | Load, Patch | Yes |
| 18 | Required parameters bound | Run start | Yes |
| 19 | Every library reference resolves; library requirements met; the lock written | Run start | Yes |
| 20 | A repository's library section adds only, never overrides | Run start | Yes |
| 21 | Capability negotiation: every capability the plan needs is offered by a configured provider | Run start, Patch | Yes |
| 22 | No request exceeds a locked organisation cap: attempts, auto-resolution, checks, budget, toolsets, agent roles (`docs/05` section 3.1) | Run start, Patch | Yes |
| 23 | **F1**: no merge step into a default branch has `engine_can_merge: true` unless the organisation allows it for that repository. The default branch is read from the code host | Run start, Patch | Yes |
| 24 | Branch protection exists on each target repository's default branch; classic protection or rulesets both count (LOG 147) | Run start | Yes |
| 25 | Patch: `base_revision` is current | Patch | Yes |
| 26 | Patch: `resume_at` exists after the patch (unless sent to a parent) | Patch | Yes |
| 27 | Patch: the proposer may use the operation; an agent never changes a role | Patch | Yes |
| 28 | Patch: the result passes every load check | Patch | Yes |
| 29 | Patch: protected content touched, or a role outside the allowed list | Patch | **Pending decision** (`docs/05` section 6) |
| 30 | Patch: relaxes a safety floor | Patch | **Rejected** |
| 31 | Step inputs satisfy the action's contract | Step start | Yes |
| 32 | Consumed artifacts exist and validate | Step start | Yes |
| 33 | Every `tier_tags` tag matches a tier in the manifest read at this step | Step start | Yes |
| 34 | Unused parameter | Load | No |
| 35 | Deprecated library reference with a replacement | Load | No |
| 36 | The run's work branch is neither a repository's default branch nor the run's parent branch (`metadata.branch_pattern` is free text); every step that writes refuses it again | Run start, Step start | Yes |

Checks 20, 23, 24 and 36 at run start, and 31 to 33 and 36 at step start, are as built in M5 (LOG 125 to 127, 133,
134, 141). Check 23 at patch time is not built yet; `scm.merge` re-checks F1 at merge time.

### 2.1 Plan size

Spike 1 measured carried state at about 500 bytes per plan step
(`archive/docs/15` section 6.1). Continue-as-new carries state as one payload,
bounded by Temporal's 2 MB payload limit, so a plan is limited to 1 MB of
carried state: about 2,000 steps. The check runs at load and at every patch. A
plan near the limit should be split into child runs, not given a higher limit.

### 2.2 Reachability with expansion

A step reachable only through a `plan.expand` mapping's `target` or `on_other`
counts as reachable. A step a patch inserts is checked against the plan as it
will be after the patch.

### 2.3 The effective policy view

Run start also produces the effective policy for each merge and decision step
(`docs/05` section 7). It is information, not a check: anything it would block
is already a check above.

---

## 3. Rules enforced as the run executes

| Rule | What the engine does | Why |
|---|---|---|
| Attempt ceilings on every execution (F4) | Checks the step allowance and run ceiling before each start, including loop re-entry | Without it, a Spike 3 loop ran its fix step 213 times in 15 seconds (`archive/docs/18` section 4) |
| Re-entry runs a step again | Clears the step's current outcome, outputs and artifacts; keeps the previous execution readable | Loops depend on it (`archive/docs/16` section 12) |
| No success without an end route (F9) | A run that stops otherwise is `incomplete` | Spike 2's silent success after an unrouted merge failure (`archive/docs/16` section 5) |
| F1 at merge time | `scm.merge` re-reads the default branch from the code host | A plan could name a branch that later becomes the default |
| Unmapped success is a work error | `unexpected_response`, never retried forever | GitHub's 204 for a no-op merge (`archive/docs/18` section 4) |
| Output contracts | An agent's outcome, artifacts, outputs and patches are validated when it reports, with correction turns | Malformed output is fixed in place, not by a re-run |
| Evidence integrity (F7) | Evidence artifacts are written only by engine actions | — |
| Actor from identity (F8) | The API derives the actor and roles from the authenticated session | A caller cannot assert it is a person or an approver |

---

## 4. Testing

### 4.1 Two kinds of regression test

| | Temporal replay | Re-driving a template |
|---|---|---|
| Tests a change to | Interpreter code | A template or fragment |
| Input | A recorded Temporal history | A scripted set of step outcomes |
| Catches | Non-determinism in workflow code | Routing and content regressions |
| Runs in | Engine CI | Library CI |

Replay works: Spike 1's nine recorded histories replay cleanly, and removing a
version guard from Spike 3's interpreter change made a Spike 1 history fail
replay (`archive/docs/15` section 7; `archive/docs/18` section 4). Every
behaviour change to the interpreter is versioned with a Temporal patch marker.
Recorded histories hold references and the run's library text, which is not
secret (LOG 83). They pass a secret scan before they are committed.

### 4.2 Test types

| Type | What | Where |
|---|---|---|
| Validation | Every template and fragment passes the load checks; a negative case for each blocking check | Library CI |
| Re-drive | Scripted outcomes drive a template; the path and final status are asserted | Library CI |
| Readable form | The rendered readable form is unchanged (golden file) | Library CI |
| Replay | Recorded histories replay against the changed interpreter | Engine CI |
| Failure injection | Worker crash, lost sandbox, Temporal unavailable, hook endpoint unreachable, continue-as-new under load with children and open decisions, stale patch | Engine CI |
| Run-time rules | Each rule in section 3 | Engine CI |
| Provider contracts | Each interface's suite, including every documented success response | Provider CI |
| Merge race | Two work branches catch up from the same parent head; the second merge gets `base_moved` and loops; repeated refusals exhaust the allowance and escalate | Engine CI |
| Agent reporting | `report`, `ask_human` (including across a worker restart), `checkpoint`, `propose_patch` | Engine CI |
| Pre-submit parity | A session's check and the later step receive the same task, tools, model and commit | Engine CI |

### 4.3 Workflow test file

```yaml
api_version: feature-flow/v1
kind: workflow_test
name: blocking_finding_goes_through_fix_loop
template: std:templates/ticket_delivery
parameters: { ticket_id: FF-1042 }
repositories: [acme/api]

script:                           # outcome per step id, per visit; unlisted steps take their first success outcome
  verify.review.findings_record: [recorded, recorded]
  verify.fix_loop.fix: done
outputs:                          # outputs and namespace values per visit, to drive route guards
  verify.review.findings_record: [{ new: 1 }, { new: 0 }]
  "findings.open_blocking": [1, 0]

expect:
  path_includes: [implement, verify.fix_loop.fix, verify.review.findings_record, deliver.merge]
  status: completed
  validation_errors: []
```

| Field | Meaning |
|---|---|
| `script` | Step id to outcome, per visit. A scalar applies to every visit. A value may be an error class (`{ error: infrastructure, times: 2 }`) to exercise retry rules |
| `outputs` | Outputs per visit, and namespace values such as `findings.*`, so guards can be driven |
| `patch` | Optional: a patch to apply, to test patch checks |
| `expect.path` or `expect.path_includes` | The steps executed, exactly or as a subsequence. For a `parallel`, branch paths are given separately, without asserting interleaving |
| `expect.status` | The final run status, or `running` |
| `expect.validation_errors` | Check numbers expected to block |
| `expand` | LOG 75: the items each `plan.expand` step reads |
| `expect.executions`, `expect.escalated`, `expect.clean_up` | LOG 75: how many times a step ran; the steps with an open intervention when the run settles; the steps a `clean_up` route reset the branch for |

LOG 75: a list's last entry applies to every later visit; a
`findings.<name>` list gives one value per route evaluation that reads the
name; an output the file does not give takes its type's zero value, and one
given as `~` is not reported; a work error fails `times` executions; steps
finish one at a time, in the order they started. `ff test` runs the files.

Tests live with the library they test. An organisation's distribution
repository runs `std`'s tests against its overrides.

### 4.4 The readable form

What a person sees in the console and what a planner agent reads through
`inspect_plan`. Generated from the plan; never maintained by hand.

```
Plan ticket_delivery rev 3 (ticket FF-1042)            policy: merge by engine (into epic branch)
──────────────────────────────────────────────────────────────────────────────────────────
 #  id                     form        role / action   access  status    routes (non-default)
 1  baseline.tests         action      test.run        -       done
 2  plan_spec              agent       planner         none    done
 3  approve_spec           decision    ticket.spec_approval  -  done(auto) rejected->plan_spec (clean_up)
 4  implement              agent       implementer     branch  running
 5  verify.tests           action      test.run        -       pending   [protected]
 6  verify.review.angle_0  agent       reviewer        none    pending   [protected]
 ...
12  deliver.merge          action      scm.merge       -       pending   base_moved->deliver.catch_up

Protected: verify.*      Defaults: failure, blocked -> escalate; success -> next
```

| Rule | Detail |
|---|---|
| One line per step | Sequence, id, form, role or action, effective write access, status, non-default routes |
| Defaults stated once | At the foot, so the lines show only what differs |
| Protection and policy shown | Protected steps marked; the effective policy summarised |
| Inputs hidden | An agent asks for one step's detail by id, which keeps the form small |

---

## 5. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entry 47.
