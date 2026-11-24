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
| 5 | Expression parses, type-checks and is within its bounds (`docs/03` section 5.2); a step output it reads is one the step declares or its action publishes (`docs/04` sections 2 and 12; LOG 490) | Load | Yes |
| 6 | An agent step or decision declares at least one outcome; no step declares `attempts_exhausted` | Load | Yes |
| 7 | **Every outcome has a route after defaults are applied** (`docs/03` section 4.3) | Load | Yes |
| 8 | Every route target exists, or is a directive; a guarded list ends with a fallback | Load | Yes |
| 9 | Every step is reachable from the first | Load | Yes |
| 10 | Every cycle has a route out | Load | Yes |
| 11 | Each consumed artifact has a producer declaring a compatible type, or is `optional`, or comes from a parent, root or child; no artifact is named `parent`, `root` or `child`, which name other runs' artifacts in a step's inputs (`docs/04` section 3.1) | Load | Yes |
| 12 | No agent step, decision or `artifact.import` produces an evidence type (F7) | Load | Yes |
| 13 | A step's `write_access` does not exceed its role's; no step sets tools (F10) | Load | Yes |
| 14 | Fragments: parameters bound, every exit routed, nesting depth at most 3 | Load | Yes |
| 15 | `parallel` branches have no routes | Load | Yes |
| 16 | Every decision type is declared by a library in the run (`docs/05` section 4) | Load | Yes |
| 17 | Plan size: carried state under 1 MB, about 2,000 steps (section 2.1) | Load, Patch | Yes |
| 18 | Required parameters bound | Run start | Yes |
| 19 | Every library reference resolves; library requirements met; the lock written | Run start | Yes |
| 20 | Retired: a repository's `library` section, which it checked, is refused (`docs/07` section 4, LOG 487). The number is not reused | None | None |
| 21 | Capability negotiation: every capability the plan needs is offered by the instance that serves it. A step needs its action's capabilities and those of the inputs it sets (`wait.condition`'s condition; LOG 489); an agent step needs a sandbox, a VCS provider, and `agent_runtime.hooks` and `agent_runtime.mcp` of the runtime its role maps to (`role_mapping`), not the type's default; a dry run asks the `dry_run` instance. The message names the capability, the instance and the step, such as `capability agent_runtime.mcp is not offered by agent_runtime instance 'x' (role_mapping.implementer); needed by step implement`. A library naming a capability no provider declares (`requires.capabilities`) is refused the same way (F16) | Run start | Yes |
| 22 | No request exceeds a locked organisation cap (`docs/05` section 3.1): at run start, attempts and races, the merge floors (checks and tests before merging), auto-resolution and the root budget; on a patch, the steps it adds or changes, for attempts, races and the merge floors | Run start, Patch | Yes |
| 23 | **F1**: no merge step into a default branch has `engine_can_merge: true` unless the organisation allows it for that repository. The default branch is read from the code host | Run start | Yes |
| 24 | Branch protection exists on each target repository's default branch; classic protection or rulesets both count (LOG 147) | Run start | Yes |
| 25 | Patch: `base_revision` is current | Patch | Yes |
| 26 | Patch: `resume_at` exists after the patch (unless sent to a parent) | Patch | Yes |
| 27 | Patch: the proposer may use the operation; an agent never changes a role | Patch | Yes |
| 28 | Patch: the result passes every load check | Patch | Yes |
| 29 | Patch: an agent's patch needs a person: protected content touched or left unrun, or a step added inside it; a role outside `planning.allowed_roles`; a child template outside `planning.templates`; a step added or replaced with `planning` wider than the proposer's; or review required by the step (a child's, at its parent) or the organisation | Patch | **Pending decision** (`docs/05` section 6) |
| 30 | Patch: relaxes a safety floor | Patch | **Rejected** |
| 31 | Step inputs satisfy the action's input schema (`docs/04` section 2): every `with` key is an input of the action, and a value has the input's kind and allowed values. Literals are checked at load, expressions when evaluated at step start (LOG 359), for every action, `run.child` and `plan.expand` included (LOG 488) | Load, Step start | Yes |
| 32 | Consumed artifacts exist and validate | Step start | Yes |
| 33 | Every `tier_tags` tag matches a tier in the manifest read at this step | Step start | Yes |
| 34 | Unused parameter | Load | No |
| 35 | Deprecated library reference with a replacement | Load | No |
| 36 | The run's work branch is neither a repository's default branch nor the run's parent branch (`metadata.branch_pattern` is free text). Checked once: the work branch is fixed when the run starts (LOG 147) | Run start | Yes |
| 37 | A route on a failure outcome, from step A to step B, where B consumes (optionally or not) an artifact A declares but not for that outcome: B would start without A's evidence, or with an earlier execution's. A list-form `produces` keeps its artifacts for success outcomes only. A `blocked` outcome is not checked: it gives a reason, which reaches B (LOG 513, 515) | Load | Yes |

Checks 23, 24 and 36 at run start, and 31 to 33 at step start, are as built in M5 (check 36 at run start only since LOG 147) (LOG 125 to 127, 133,
134, 141). Checks 21 and 23 are not run on a patch (a follow-up; no milestone); `scm.merge` re-checks F1 at merge time.

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
| Runs in | CI, default category | CI, default category |

Replay works: Spike 1's nine recorded histories replay cleanly, and removing a
version guard from Spike 3's interpreter change made a Spike 1 history fail
replay (`archive/docs/15` section 7; `archive/docs/18` section 4). From version
1, every behaviour change to the interpreter is versioned with a Temporal patch
marker. Before version 1 there is no backward compatibility (LOG 263): a change
that breaks a recorded history re-records it instead of adding a marker.
Recorded histories hold references and the run's library text, which is not
secret (LOG 83). They pass a secret scan before they are committed.

### 4.2 Test types

One workflow, `.github/workflows/ci.yml`, runs the tests in jobs by test
category: `default`, `integration` and `cluster`. Its header maps each type
below to its job and test classes. The `live` category is run by hand, not in
CI.

| Type | What | Category |
|---|---|---|
| Validation | Every template and fragment passes the load checks; a negative case for each blocking check | default |
| Re-drive | Scripted outcomes drive a template; a person's answers to its waits and an agent's questions go through the core's routing as the engine's do; the path, final status and how each question closed are asserted | default |
| Readable form | The rendered readable form is unchanged (golden file) | default |
| Replay | Recorded histories replay against the changed interpreter | default |
| Failure injection | Worker crash, lost sandbox, Temporal unavailable, hook endpoint unreachable, continue-as-new under load with children and open decisions, stale patch | integration; stale patch and hook endpoint unreachable: default |
| Run-time rules | Each rule in section 3 | default, integration |
| Provider contracts | Each interface's suite, including every documented success response | default, integration, cluster |
| Merge race | Two work branches catch up from the same parent head; the second merge gets `base_moved` and loops; repeated refusals exhaust the allowance and escalate | integration |
| Agent reporting | `report`, `ask_human` (including across a worker restart), `checkpoint`; `inspect_plan` and `propose_patch`, a staged patch carried by the report and held for a person (`docs/04` section 6.1) | integration |
| Pre-submit parity | A session's check and the later step receive the same task, tools, model and commit | integration |
| Architecture | The shell holds no routing; project reference rules | default |
| Deployment | Images, network policies, stop on SIGTERM | cluster, integration |
| Console (serving) | The API serves the built console; the console sources never build markup or links from data | default |
| Console (browser) | The console in Chromium (Playwright) against the API in process over the local stores, with a scripted engine writing the records a run would: find a wait in "Needs me" and answer it with a reason, start a run, set a breakpoint and continue, a change arriving over `/events`, the notification link, sign-in, markup in API data shown as text (LOG 461), and a patch approval's prompt shown as text, line by line | default |
| Live | A real agent runtime and the GitHub App | live, by hand |

### 4.3 Workflow test file

```yaml
api_version: feature-flow/v1
kind: workflow_test
name: ticket_fix_loop
template: std:templates/ticket_delivery
parameters: { ticket_id: FF-1042 }
repositories: [acme/api]

script:                           # outcome per step id, per visit; unlisted steps take their first success outcome
  verify.fix.verify_fix: [not_fixed, verified]
outputs:                          # outputs and namespace values per visit, to drive route guards
  "findings.open": 1
  "findings.open_blocking": [1, 0]

expect:
  status: completed
  path_includes: [verify.review.review_0.assess, verify.fix.fix, verify.fix.verify_fix,
                  verify.fix.fix, verify.fix.verify_fix, verify.tests, deliver.merge]
```

An excerpt of `library/std/tests/ticket_fix_loop.yaml`.

| Field | Meaning |
|---|---|
| `description` | Optional: what the test shows |
| `script` | Step id to outcome, per visit. A scalar applies to every visit. A value may be an error class (`{ error: infrastructure, times: 2 }`, with an optional `then: <outcome>` after the errors) to exercise retry rules, or, for a decision, the person's outcome with their reason (`{ outcome: rejected, reason: "requirements omit the audit log" }`) |
| `outputs` | Outputs per visit, and namespace values such as `findings.*`, so guards can be driven |
| `patch` | Optional: a patch to apply, to test patch checks. A person's (`proposed_by: { type: human }`) applies at the start; an agent's (`{ type: agent, step_id: <id> }`) is carried by that step's first report, as `propose_patch` stages it for the report, and a held one is answered by `answers` (`approve` or `reject`; std's `ticket_implementer_patch_approved` and `_rejected`) |
| `expect.path` or `expect.path_includes` | The steps executed, exactly or as a subsequence. For a `parallel`, branch paths are given separately, without asserting interleaving |
| `expect.status` | The final run status, or `running` |
| `expect.validation_errors` | Check numbers expected to block |
| `expand` | LOG 75: the items each `plan.expand` step reads: one list, or a list of lists, one per execution (the last applying to every later one), as `script` gives an outcome per visit (LOG 506) |
| `expect.executions`, `expect.escalated`, `expect.clean_up` | LOG 75: how many times a step ran; the steps with an open intervention when the run settles; the steps a `clean_up` route reset the branch for |
| `overrides`, `answers`, `expect.skipped` | A person's steering (M7): `breakpoint`, `skip` (with `outcome`) or `grant_attempts` on a step, made at the run's start as an administrator; answers to the breakpoints, interventions and patch approvals that open at a step, in order (`continue`, `retry`, `approve`, `reject`, `{ go_to: <step> }`, `{ replan: <step> }`, either with `clean_up: true`, `{ grant_attempts: n }`, `{ end: <status> }`); a patch approval opens at the step that proposed the patch; the steps a skip ended without running |
| `questions`, `expect.questions` | An agent's questions (`docs/04` section 3.4), by step id, in order: each `ask` (with optional `options`, `id`, default `<step>-q<n>`, and `visit`, default 1) is held while that visit runs, then answered (`answer: <text>`), timed out (`timeout: true`), or with neither left to close when the step ends; how each closed, by id: `answered`, `timed_out` or `step_ended` (LOG 386). Only a question has a deadline: gates, breakpoints, interventions and patch approvals wait until answered. The `merge_action` gate an action raises while it runs is not scripted |
| `expect.context` | Agent step id to execution number to texts the run context rendered into that execution's specification must contain (`docs/04` section 3.5), such as `{ plan.plan: { 2: ["requirements omit the audit log"] } }` |
| `expect.consumed` | Step id to execution number to keys of artifacts the step must have consumed, resolved, when that execution started (`docs/03` section 7.2), such as `{ plan.plan: { 2: [previous/requirements] } }` or `{ verify.fix.fix: { 2: [fix_test_results] } }`, the ledger view (`findings_ledger`) included; other keys it consumed are allowed (LOG 505, 517) |
| `expect.branches` | A `parallel` or chain's id to its branches in the final plan, each by its first step's id, exactly and in order, such as `{ tickets.children: [tickets.children_0, tickets.children_1, tickets.children_2] }`: what shows that expanding again added and removed only what changed (`docs/03` section 10.4; LOG 506) |

LOG 75: a list's last entry applies to every later visit; a
`findings.<name>` list gives one value per route evaluation that reads the
name; an output the file does not give takes its type's zero value, and one
given as `~` is not reported; a work error fails `times` executions; steps
finish one at a time, in the order they started. `ff test` runs the files.

Tests live with the library they test.

### 4.4 The readable form

What a person sees in the console (`GET /runs/{id}/plan`) and what `ff show`
prints. Generated from the plan; never maintained by hand. Abridged from the
golden file for `ticket_delivery` (`tests/FeatureFlow.Validation.Tests/Golden`):

```
Template ticket_delivery (level ticket)
  policy: deliver.merge engine_can_merge = params.engine_can_merge (default false)
──────────────────────────────────────────────────────────────────────────────────────────
  #  id                             form      role / action         access  status   routes (non-default)
  1  baseline.tests                 action    test.run              -       pending  failed->escalate(std:prompts/baseline_failed)
  3  plan.plan                      agent     planner               none    pending
  4  plan.validate                  action    artifact.validate     -       pending  invalid->plan.plan
  5  approve_spec                   decision  ticket.spec_approval  -       pending  rejected->plan.plan (clean_up)
  6  implement                      agent     implementer           branch  pending
  7  verify.tests                   action    test.run              -       pending  failed->verify.repair  [protected]
 10  verify.review.review_0.assess  agent     reviewer              none    pending  reviewed->[...]  [protected]
 ...
 37  deliver.merge                  action    scm.merge             -       pending  base_moved->deliver.catch_up  [protected]

Protected: verify.*, deliver.*
Defaults: blocked, failure -> escalate; success -> next; attempts_exhausted -> escalate; in a branch, other outcomes -> join
```

| Rule | Detail |
|---|---|
| One line per step | Sequence, id, form, role or action, effective write access, status, non-default routes |
| Defaults stated once | At the foot, so the lines show only what differs |
| Protection and policy shown | Protected steps marked; the effective policy summarised |
| Inputs hidden | A step's inputs are not shown, which keeps the form small |

---

## 5. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entry 47.
