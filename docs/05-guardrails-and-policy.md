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
run-time override relaxes a floor.** Only organisation configuration may set
the values a floor allows, and some floors have no setting at all.

| # | Floor | Setting |
|---|---|---|
| F1 | **Default-branch merges.** The engine merges into a repository's default branch only where the organisation allows it. Default: never; a person merges on the code host (section 2) | Organisation, per repository pattern |
| F2 | **Secrets by reference.** No configuration, template or input may hold a secret value, only a reference to the secrets provider | None |
| F3 | **Protected content.** An agent cannot change, remove or bypass a protected step, or make it unreachable; such a change becomes a decision for a person (section 6) | None |
| F4 | **Attempt ceilings.** Allowances stay within the organisation's ceiling per step and per run, checked on every execution of a step, including when a loop re-enters it. Grants by a person stay within the hard ceiling unless made by an administrator | Organisation |
| F5 | **No push credential for agents.** Agents hold a read-only code host credential, per step. Only the engine pushes and merges. The engine's own identity cannot change branch protection | None |
| F6 | **Model credentials stay out of the sandbox.** A proxy adds the key, or a per-run key with a spend limit and expiry is used (`docs/02`) | Installation chooses the mode |
| F7 | **Evidence integrity.** Evidence artifact types are produced only by engine actions (`docs/01` section 5.2) | None |
| F8 | **Record integrity.** Every decision and override records the actor from authentication, never from the request. An answer that requires a person cannot be given by policy, automation or an agent | None |
| F9 | **Run status integrity.** A run reports `completed` only by reaching `{ end: completed }`; otherwise it is `incomplete` | None |
| F10 | **Privilege from role.** A step may tighten its role's write access, never widen it, and may not change its tools. A setting that governs safety is never read from a file an agent can edit | None |
| F11 | **Work-item budget.** A run tree creates no more descendant runs than its budget, within the organisation's ceiling | Organisation |

The first design's floors F1 to F4 keep their numbers and meanings, except F3,
which now binds agents only: a person with the right role may change a
protected step (RW8, section 5).

---

## 2. F1 in detail: merges into the default branch

| Rule | Detail |
|---|---|
| Setting | Organisation policy: `merge.default_branch.engine_may_merge`, a list of repository patterns where the engine may merge. Default: empty |
| Where it is read | Organisation configuration only. Not from the repository's own file, so no agent commit and no repository maintainer can change it (P8) |
| The workflow's part | A merge step's `engine_can_merge` is the workflow's request. Into the default branch, it may be `true` only where F1 allows |
| Enforcement | At validation (start and every patch), and again by `scm.merge` at merge time, reading the target and the default branch from the code host, never from the plan |
| When the engine may not merge | The step asks a person to merge on the code host and waits (an action request, `docs/04` section 4.5) |
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
| Checks before merging | `require_checks` on `scm.merge` | `merge.require_checks`: if set, a workflow cannot turn checks off |
| Attempts | `limits.attempts` | Ceilings per step form and per run (F4) |
| Work items | `budget.max_work_items` on the root template | Ceiling (F11) |
| Review of agent patches | `planning.review` | `planning.require_review`, per level pattern |
| Agent roles | The roles a template's steps name | `planning.allowed_roles` for steps an agent adds (section 6) |
| Toolsets | The role's toolsets | `toolsets.allowed`: which external tool servers exist at all |

A request that exceeds its cap is not silently lowered. Where the cap is an
organisation **default** (`locked: false`), the request applies; where it is
**locked**, a template that exceeds it fails validation with the setting's name.

### 3.2 Who may decide: approval

Approval is the one topic the organisation owns outright: **who may answer a
decision**. It is identity governance, so templates do not carry it.

```yaml
approval:
  - match: "*.merge_action"        # decision type pattern
    roles: [approver]
    min_count: 1
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
| `min_count` | How many distinct people must answer the same way |
| `from_chat` | Whether the decision may be answered from a chat client |
| `auto` | Whether auto-resolution is allowed. The template asks; this allows |

Rules are keyed by decision type pattern, not by level name, because levels
are the workflow's (review finding 3.4).

### 3.3 People roles

| Role | May, by default |
|---|---|
| `viewer` | Read runs, artifacts, findings, decisions |
| `approver` | Answer decisions where approval rules name the role |
| `steerer` | The run-time overrides in section 5, except those reserved for `admin` |
| `admin` | Everything, including changes to protected steps and grants beyond the hard ceiling |

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
| Engine-raised types | The engine raises decisions of type `<level>.escalation`, `<level>.question`, `<level>.patch_approval`, `<level>.breakpoint` and `<level>.merge_action`, with `<level>` the run's level. The standard library declares them with patterns (`*.escalation` and so on) |

---

## 5. Run-time overrides

A person may change a running run in any way the organisation's roles allow
(P3). Every override goes through the API, is authorised by role, and is
recorded with the actor, reason and the change.

| Override | Effect | Default role |
|---|---|---|
| Answer a decision | `docs/04` section 4 | Per approval rules |
| Recovery action at an intervention | Retry, go to, re-plan, grant attempts, end (`docs/01` section 7.2) | `steerer` |
| Set or clear a breakpoint | Pause before a step | `steerer` |
| Skip a step | Mark it as having ended with a chosen outcome, without running it. The record shows it was skipped | `steerer`; `admin` for a protected step |
| Amend the plan | Apply a patch (`docs/03` section 10). Not bound by F3 | `steerer`; `admin` for protected content |
| Change a step's role | Only by a person, never by an agent's patch | `admin` |
| Grant attempts | Within the hard ceiling; beyond it | `steerer`; `admin` |
| Override library content | For future steps (`docs/08`) | `steerer` |
| Answer for an agent | Answer an agent's question | Per approval rules |
| Abort | End the run with status `aborted`. Branches and pull requests are left as they are unless the person asks otherwise | `steerer` |

**Never overridable:** the floors in section 1. A patch or override that would
relax one is rejected outright and recorded.

The organisation may change the default role for any override, for example
reserving skips for `admin`.

---

## 6. Agent proposals

An agent may propose a patch (planning authority, `docs/04` section 6.1) or a
library override. The proposal becomes a **patch approval** decision, rather
than being applied, when any of these hold:

| Condition | Why |
|---|---|
| It touches protected content (F3) | The workflow marked it as not the agent's to change |
| It adds or changes a step to use a role outside `planning.allowed_roles` | Otherwise an agent could give a new step more access than itself (F10) |
| The step's `planning.review` is `required`, or the organisation requires review at this level | The workflow or organisation asked for it |

A proposal that would relax a floor is rejected outright, with no decision. An
agent is never silently refused and never silently trusted: it proposes, a
person decides, and the decision is recorded.

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
| Who may act on the merge request | `approver`, 2 people, console only | Approval rule `*.merge_action` |

The view is part of the plan's readable form and of the run start record.

---

## 8. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | F1 drops the repository-level setting (the first design read it from the repository's file on the default branch). A repository maintainer who wants the engine to merge into a proof-of-concept repository now asks the organisation | Accept. One place to look, and the repository file is no longer a safety input |
| 2 | Should `min_count` greater than 1 be in version 1? | Yes; the enterprise profile asks for 2 on default-branch merges |

Judgement calls made in this document are in `docs/LOG.md`, entries 25 to 30.
