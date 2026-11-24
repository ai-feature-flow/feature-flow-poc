# Guardrails and policy

Status: draft, 2026-10-31.

This document owns every rule the engine enforces regardless of the workflow
(the safety floors), the organisation's policy model, and what a human may
change in a running run. Principles P1 to P3 and P8 (`docs/00-principles.md`)
are the basis. The settings themselves are listed in `docs/07`; this document
gives their meaning.

---

## 1. Safety floors

A safety floor passes the guardrail test (P2): it is needed for safety or for a
trustworthy record, whatever the workflow says. **No template, patch, agent or
run-time override relaxes a floor**, with two named exceptions for people, both
audited: F3 (a person with the right role may change a protected step) and F4
(an administrator may grant attempts beyond the hard ceiling for one run).
Only organisation configuration may set the values a floor allows, and some
floors have no setting at all.

| # | Floor | Setting |
|---|---|---|
| F1 | **Default-branch merges.** The engine merges into a repository's default branch only where the organisation allows it. Default: never; a person merges on the code host (section 2) | Organisation, per repository pattern |
| F2 | **Secrets by reference.** No configuration, template or input may hold a secret value, only a reference to the secrets provider | None |
| F3 | **Protected content.** An agent cannot change, remove or bypass a protected step, or make it unreachable; such a change becomes a decision for a person (section 6) | None |
| F4 | **Attempt ceilings.** Allowances stay within the organisation's ceiling per step and per run, checked on every execution of a step, including when a loop re-enters it. A steerer's grants stay within the hard ceiling; an administrator may grant beyond it for one run, audited, so a long run is never stranded by its ceilings | Organisation |
| F5 | **No push credential for agents.** Agents hold no code host credential: the checkout arrives as a bundle with no remote (LOG 95). Only the engine pushes and merges. The engine's own identity cannot change branch protection | None |
| F6 | **Model credentials stay out of the sandbox.** A model proxy adds the key; the agent never holds it (`docs/07` section 2, `model_access`) | Installation configures the proxy |
| F7 | **Evidence integrity.** Evidence artifact types are produced only by engine actions (`docs/01` section 5.2), and not by `artifact.import`, which brings in files the engine did not make (`docs/04` section 10.1) | None |
| F8 | **Record integrity.** Every decision and override records the actor from authentication, never from the request. An answer that requires a person cannot be given by policy, automation or an agent | None |
| F9 | **Run status integrity.** A run reports `completed` only by reaching `{ end: completed }`. A run that stops without reaching any `end` route is `incomplete` | None |
| F10 | **Privilege from role.** A step may tighten its role's write access, never widen it, and may not change its tools. A setting that governs safety is never read from a file an agent can edit | None |
| F11 | **Work-item budget.** A run tree creates no more descendant runs than its budget, within the organisation's ceiling | Organisation |

The first design's floors F1 to F4 keep their numbers and meanings. F3 now
binds agents only: a person with the right role may change a protected step
(RW8, section 5). F4 binds workflows, agents and steerers; an administrator
may grant beyond the hard ceiling (section 5).

---

## 2. F1 in detail: merges into the default branch

| Rule | Detail |
|---|---|
| Setting | Organisation policy: `merge.default_branch.engine_may_merge`, a list of repository patterns where the engine may merge. Default: empty |
| Where it is read | Organisation configuration only. Not from the repository's own file, so no agent commit and no repository maintainer can change it (P8) |
| The workflow's part | A merge step's `engine_can_merge` is the workflow's request. Into the default branch, it may be `true` only where F1 allows |
| Enforcement | At validation (start and every patch), and again by `scm.merge` at merge time, reading the target and the default branch from the code host, never from the plan |
| When the engine may not merge | The step opens a `<level>.merge_action` decision and, once it is approved, asks a person to merge on the code host and waits (an action request, `docs/04` section 4.5) |
| A person merges on the code host before the decision is answered | Accepted; the run continues, the decision closes as answered on the code host, and the timeline records the code host login and that the decision was not given through Feature Flow. Branch protection on the code host governs merges made there; approval rules on `*.merge_action` govern merges requested through Feature Flow (`docs/04` section 4.5; LOG 341) |
| When the code host refuses an allowed engine merge | The same: an action request |
| Final fallback | Branch protection on the default branch, configured by an identity the engine does not hold. The engine checks it exists at run start and never holds a bypass |

This replaces the first design's seven related settings (review finding 5.5).
The repository-level setting is removed: the organisation grants the engine
merge rights per repository pattern instead.

---

## 3. The policy model

### 3.1 One pattern: the workflow requests, the organisation caps

Most policy follows one pattern (P4). The template states what it wants; the
organisation states the limit; the effective value is the request within the
limit. Validation reports where a request was capped.

| Topic | Workflow requests | Organisation caps |
|---|---|---|
| Auto-resolution of a decision | `auto: <outcome>` on the decision | Whether auto-resolution is allowed, per decision type pattern |
| Engine merge into the default branch | `engine_can_merge: true` | F1 |
| Checks before merging | `require_checks` on `scm.merge` | `merge.require_checks`: locked true, a workflow cannot turn checks off. The step may leave `require_checks` unset or set it to a literal `true`; any other value, an expression included, is refused, since run start cannot show that an expression gives `true` (LOG 347) |
| Tests before merging | `require_tested` on `scm.merge` | `merge.require_tested`, applied as `merge.require_checks` is (LOG 342) |
| Attempts | `limits.attempts` | Ceilings per step form and per run (F4) |
| Merge races | `limits.races` on `scm.merge` | None of its own: the per-step attempts ceiling (LOG 340). A `limits.races` above it is refused; a merge step that sets none, itself or in its document's `defaults.limits`, gets the installation's default held to the ceiling (LOG 348) |
| Work items | `budget.max_work_items` on the root template | Ceiling (F11), always locked (LOG 187) |
| Review of agent patches | `planning.review` | `planning.require_review`, per template name pattern (section 6) |
| Agent roles | The roles a template's steps name | `planning.allowed_roles` for steps an agent adds (section 6) |
| Toolsets | The role's toolsets | None: toolsets are not in version 1 (`docs/06` section 3.9) |

A request that exceeds its cap is not silently lowered:

| Cap | Unlocked (`locked: false`) | Locked |
|---|---|---|
| Merge floors (`merge.require_checks`, `merge.require_tested`) | The step's request overrides the default | A step that asks otherwise fails validation with the setting's name |
| An approval rule's `auto: false` | A template's `auto` has no effect: a person answers, and run start warns | The template is refused (check 22; LOG 88) |
| Attempt and work-item ceilings | Always enforced | Always enforced |
| `planning.require_review` | Its `value` applies; its `locked` is read nowhere | As unlocked |

Run start checks every step against the per-step caps (`StepCaps`), and a patch
the steps it adds or changes, by the same function (check 22), so a patch cannot
reach an attempt, race or merge-floor value a template could not start with
(F1). A patch's `auto` under a rule that disallows it has no effect. The caps are carried in the run's
state for this (section 8); the step itself does not check them again, since
no step can hold a value the two checks refused (LOG 347).

### 3.2 Who may decide: approval

Approval is the one topic the organisation owns outright: **who may answer a
decision**. It is identity governance, so templates do not carry it.

```yaml
approval:
  - match: "*.merge_action"        # decision type pattern
    roles: [approver]
    min_count: 2
    from_chat: false
    auto: false
  - match: "*"
    roles: [approver]
    min_count: 1
    from_chat: true
    auto: true
```

| Field | Meaning |
|---|---|
| `match` | A decision type pattern. The first matching rule applies |
| `roles` | People roles that may answer |
| `min_count` | How many distinct people must answer the same way: the same choice, whatever words each gives as their reason (`docs/04` section 4.1) |
| `from_chat` | Whether the decision may be answered from a chat client |
| `auto` | Whether auto-resolution is allowed. The template asks; this allows |

Rules are keyed by decision type pattern, not by level name, because levels
are the workflow's (review finding 3.4).

One rule decides whether a person may answer a wait, in the API's
`CheckAnswer`, for every answer route (an answer, a session's submission,
abandonment and checks) and for the `can_answer` a read shows (`GET /runs/{id}`,
`GET /attention`), so a read never offers what the answer would refuse:

| Wait | May answer |
|---|---|
| Gate, agent question, patch approval, breakpoint | The governing rule's `roles` in the run's scope (a steerer's rule admits an administrator) |
| Intervention | The `recover` override's roles; a grant of attempts needs `grant_attempts`'s as well |
| `stalled`, `stuck` | No one: they are the engine's status (409) |

Then, for every kind: an answer through chat needs the rule's `from_chat`; and
where the rule's `min_count` is above 1 beside OIDC, the local token, which has
no groups, is not a person who can be counted (LOG 221). A read's `can_answer`
is also false for a closed wait and for a run no longer `starting` or `running`.

### 3.3 People roles

| Role | May, by default |
|---|---|
| `viewer` | Read runs, artifacts, findings, decisions |
| `approver` | Answer decisions where approval rules name the role |
| `steerer` | The run-time overrides in section 5, except those reserved for `admin` |
| `admin` | Everything `steerer` may do, plus changes to protected steps, role changes, library overrides affecting protected steps, and attempt grants beyond the hard ceiling |

Roles map to identity-provider groups and may be scoped by team or repository.
These are people's roles, distinct from the library's agent roles (planner,
implementer, reviewer).

---

## 4. Decision types

Every decision has a type (`docs/01` section 7). The library declares the types
its templates use, with a default approval rule; the organisation's `approval`
rules override by pattern (review finding 6.3).

```yaml
# In a library
decision_types:
  - name: ticket.spec_approval
    default_approval: { roles: [approver], min_count: 1, auto: true }
```

| Rule | Detail |
|---|---|
| Unknown type | A decision type no library declares is a validation error at load |
| Precedence | An organisation rule that matches wins over the library's default |
| Engine-raised types | The engine raises decisions of type `<level>.escalation`, `<level>.question`, `<level>.patch_approval`, `<level>.breakpoint` and `<level>.merge_action`, with `<level>` the run's level. Every intervention, whatever raised it (an `escalate` route, a step that cannot answer, a timeout), has type `<level>.escalation`. The standard library declares them with patterns (`*.escalation` and so on). `<level>.merge_action` is raised by `scm.merge` when a person must merge (`docs/04` section 4.5). A rule on it governs merges requested through Feature Flow, not a merge a person makes directly on the code host, which branch protection governs (LOG 341) |

---

## 5. Run-time overrides

A person may change a running run in any way the organisation's roles allow
(P3). Every override goes through the API, is authorised by role, and is
recorded with the actor, the change and the person's reason (a comment) when
they give one. A reason is optional, except where it matters (LOG 502): an
answer whose choice routes the step as a failure (a gate's outcome of class
`failure`, such as `rejected`, or `reject` on `merge_action`), ending a run at
an intervention or a breakpoint (`end`), an abort, a library override, and a
`go_to` or `replan` override (which cancel the work in flight, LOG 572) are
refused without one (400), and so is a grant beyond the hard ceiling, which the
run refuses (409) since only it knows the ceilings. For the privileged ones the
reason is their audit trail. The answer
record keeps it beside the choice (`docs/04` section 4.1); where none is given,
none is recorded or invented.

| Override | Key | Effect | Default role |
|---|---|---|---|
| Answer a decision | `answer` | `docs/04` section 4 | Per approval rules |
| Recovery action at an intervention | `recover` | Retry, go to, re-plan, grant attempts, end (`docs/01` section 7.2) | `steerer` |
| Go to a step | `go_to` | On a running run, outside an intervention: cancel every step in flight and its child run, end every active parallel or chain's branches without deciding its join, withdraw every open wait, and enter the step, with `change.clean_up` (`true` or `false`, default `false`) as a route's. Checked as the recovery go to is: the step is in the plan, `clean_up` does not aim at a decision, and no protected step is left unrun. A step inside a parallel or chain that is running or has not started is refused (go to the container); one inside a decided join runs its branch again. An agent step entered so is told who sent the run there and why. A reason is required. A run that has ended is refused. The result names what was cancelled (LOG 572) | `steerer` |
| Re-plan | `replan` | As `go_to`, to an agent step with `planning` (LOG 572) | `steerer` |
| Instructions for a step's next attempt | `instruct` | `change.text`, at most 4,096 bytes, added to an agent step's next prompt with who gave it, until the step next succeeds (`docs/04` section 3.5). Replacing the prompt text is not offered: it would let a person drop the reporting instructions (LOG 573) | `steerer`; `admin` for a step in protected content (`instruct_protected`) |
| Set or clear a breakpoint | `breakpoint` | Pause before a step | `steerer` |
| Skip a step | `skip` | Mark it as having ended with a chosen outcome, without running it. The record shows it was skipped | `steerer`; `admin` for a protected step |
| Amend the plan | `amend` | Apply a patch (`docs/03` section 10). Not bound by F3 | `steerer`; `admin` for protected content |
| Change a step's role | `change_role` | Only by a person, never by an agent's patch | `admin` |
| Grant attempts | `grant_attempts` | Up to the hard ceiling (F4); beyond it, for one run, with a reason, its audit trail (LOG 502) | `steerer`; `admin` beyond the hard ceiling (`grant_attempts_beyond`) |
| Override library content | `override_library` | For future steps (`docs/08` section 5.2), with a reason (LOG 502) | `steerer`; `admin` where a protected step resolves the content |
| Answer for an agent | `answer_question` | Answer an agent's question | Per approval rules |
| Abort | `abort` | End the run with status `aborted`. Branches and pull requests are left as they are unless the person asks otherwise | `steerer` |

**Never overridable:** the floors in section 1. A patch or override that would
relax one is rejected outright and recorded.

The API delivers every override but answers and recoveries (which go as answers,
`docs/04` section 4) to the run as one update, which the run's core routes: an
override is idempotent by the id the API gave it, so the same override again
succeeds with its first result and the same id with another key is refused as a
conflict; then the key's arguments, the protected-content variants and the run's
own checks decide whether it applies. The update's validator and handler route
it with the same function, so they refuse the same overrides (LOG 383).

A patch names the revision it applies to (`base_revision`). Each accepted
revision is recorded as a `plan_revision` artifact (LOG 404), and
`GET /runs/{id}/plan` (`ff run plan <run>`) shows the current one: its number,
its readable form and each step with its status. The CLI sends an argument's
value from a file with `--change <name>=@<file>`, for `amend`'s `patch` and
`override_library`'s `with`; `@@` at the start sends a value that begins with a
literal `@` (LOG 406).

The organisation may change the default role for any override by key, in
`policy.overrides` (`docs/07` section 3). A `_protected` suffix (`skip_protected`,
`amend_protected`, `override_library_protected`, `instruct_protected`) sets the role for the same
override on protected content; `grant_attempts_beyond` sets who may grant beyond
the hard ceiling.

---

## 6. Agent proposals

An agent step with `planning` proposes patches through the engine's
`inspect_plan` and `propose_patch` tools (`docs/04` section 6.1). Every rule
below is the core's, checked when the patch arrives with the step's report; the
tools only shape the call. An agent's proposal becomes a **patch approval**
decision (check 29), rather than being applied, when any of these hold; a library override
always would, though no tool proposes one yet:

| Condition | Why |
|---|---|
| It touches protected content, or leaves a protected step unrun that the run would otherwise run (F3). A step it adds inside protected content (a protected fragment, `parallel` or chain) touches it, whatever the operation | The workflow marked it as not the agent's to change |
| It adds or changes a step to use a role outside `planning.allowed_roles` | Otherwise an agent could give a new step more access than itself (F10) |
| It adds a `run.child`, or changes one's template, to a template not in the proposer's `planning.templates`; a template given by an expression counts as unlisted. A patch to its own run only | The same reasoning for the child runs an agent may start. A child's patch to its parent is not held to it: the parent does not hold the child step's grant, and consented by starting the child's template (LOG 522) |
| The step's `planning.review` is `required`, or the organisation requires review at this level | The workflow or organisation asked for it |
| It adds a step, or replaces one, whose `planning` is wider than the proposer's: its `targets` are not a subset of the proposer's; it lists a template outside the proposer's `planning.templates`, when the proposer's lists any; or its `review` is `auto` where the proposer's is `required`. A proposer without `planning` cannot propose (check 27) | Otherwise an agent could give a step more planning authority than its own, and a later patch from that step would need no person (F10's reasoning, applied to `planning`; LOG 526) |
| For a child's patch to its parent: any of the above at the parent but `planning.templates`, or the child step's own `planning.review` is `required`. The parent does not hold the child step's grant, so a step the patch adds or replaces with `planning` is always held | The parent's rules govern its plan; the proposer's own review and grant still apply (LOG 521, 526) |

An agent never changes a step's role (P8): a patch that would is refused. A
proposal that would relax a floor, or that breaks a load check, a cap or a
ceiling, is refused outright, with no decision. An agent is never silently
refused and never silently trusted: it proposes, a person decides, and the
decision is recorded.

---

## 7. The effective policy view

The first design spread one decision across many settings (review finding
5.5). To make the result visible, validation produces, for every merge step and
every decision step, the **effective policy**: the request, the cap, the
result, and which setting decided it.

| Example: a project's merge into `main` | Value | From |
|---|---|---|
| Who merges | A person (action request) | F1: `acme/api` not in `engine_may_merge` |
| Checks required | Yes | `merge.require_checks: true`, locked |
| Tests required | Yes | `merge.require_tested: true`, locked |
| Who may act on the merge request | `approver`, 2 people, console only | Approval rule `*.merge_action` |

The view is part of the plan's readable form and of the run start record.
For a running run, `GET /runs/{id}/policy` (the console's Run actions →
Policy in force) gives each decision type of its plan with who may answer and
where the rule comes from, every override key with its roles and whether the
caller holds it, and the attempt limits (`docs/02` section 7, LOG 578).

---

## 8. When configuration changes take effect

| Kind of setting | Takes effect | Why |
|---|---|---|
| Checked by the API at the moment of use: who may answer a decision, who may perform an override, chat permissions | Immediately, for running runs too. A read's `can_answer` and `GET /attention` may use library decision types up to a minute old (LOG 395); the answer itself reads them now | The API reads current configuration on every request |
| Enforced inside the run: attempt ceilings, locked caps, F1, budgets, `notifications.reminder_interval` | Fixed for the run when it starts and carried in its state (`RunPolicy`, the run start activity's result, passed on at each hand-over). F1 and the locked merge caps are also listed in the run start record as the effective policy view (section 7); the ceilings, budgets and reminder interval are not (LOG 431) | Workflow code cannot read configuration (it would break deterministic replay), so these values are carried in the run's state |
| Library content | Pinned in the run's lock at start (`docs/08` section 5.1) | Reproducibility |

To change an in-run value for a running run, a person uses an override: for
ceilings, an attempt grant (section 5). There is no mechanism that pushes new
configuration into running runs.

---

## 9. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | F1 drops the repository-level setting (the first design read it from the repository's file on the default branch). A repository maintainer who wants the engine to merge into a proof-of-concept repository now asks the organisation | Accept. One place to look, and the repository file is no longer a safety input |
| 2 | Should `min_count` greater than 1 be in version 1? | Yes; the enterprise profile asks for 2 on default-branch merges |

Judgement calls made in this document are in `docs/LOG.md`, entries 25 to 30.
