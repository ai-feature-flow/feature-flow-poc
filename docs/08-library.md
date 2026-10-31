# Library

Status: draft, 2026-10-31.

This document owns workflow content: what a library holds, how it is found,
pinned and overridden, and what the standard library provides. The language
the content is written in is `docs/03`; the mechanisms it composes are
`docs/04`.

The library is where policy lives (P1). Everything the guardrail test (P2)
keeps out of the engine is a default here, which an organisation or a workflow
author may change.

---

## 1. Content types

| Type | Folder | What it is |
|---|---|---|
| Templates | `templates/` | Workflow definitions (`docs/03`) |
| Fragments | `fragments/` | Reusable step groups (`docs/03` section 11) |
| Tasks | `tasks/` | Markdown instructions for one agent step: objective, procedure, what each outcome means |
| Skills | `skills/` | Reusable guidance an agent reads on demand: one folder per skill with a `SKILL.md` |
| Resources | Named in the manifest | Folders of shared reference material, mounted read-only beside the skills so a skill can link to them (from FF) |
| Roles | `roles/` | Agent roles: write access, tools, toolsets, required runtime capabilities (`docs/01` section 2.2) |
| Artifact types | `artifact_types/` | Schema (JSON Schema for data; required headings, front matter and identifier patterns for Markdown), the validators to run on production, `evidence: true` for engine-only types, `compatible_with` |
| Decision types | `decision_types/` | Names and default approval rules (`docs/05` section 4) |
| Prompts | `prompts/` | Markdown for decisions, pull request bodies, the agent reporting preamble |
| Status map | `status_map.yaml` | Abstract tracker statuses templates use (`in_progress`, `code_review`, `done`), which tracker provider configuration maps to real ones |

Validators themselves are code, supplied by validator providers (`docs/06`
section 3.8). Library content names them and sets their options.

### 1.1 References

One format everywhere:

```
<library>[@<version constraint>]:<type>/<name>
```

For example `std:roles/implementer`, `acme@>=2.0.0:tasks/implement-ticket`.
`std` is the standard library. A reference names a content item, never a file
path; the manifest maps names to paths, so a library can reorganise its files
without breaking a plan.

---

## 2. The manifest

`library.yaml` at the library root, from FF's manifest.

```yaml
name: acme-delivery
version: 1.4.0
description: Acme's delivery process

requires:
  engine: ">=1.0.0"
  plan_schema: 1
  capabilities: [tracker.hierarchy, code_host.pull_request]
  toolsets: [jira, confluence]

dependencies:
  - { name: std, version: ">=1.0.0 <2.0.0" }

inputs:                       # values for parameters templates declare (docs/03 section 6.2)
  planning_repository: acme/plans

resources: [references]

entrypoints:                  # templates a person starts from, listed first in the console
  - { template: ticket-delivery, when: "One ticket" }
  - { template: project-delivery, when: "A project spanning several epics" }

notifications:
  events: [decision_needed, failed, completed]
```

| Key | Effect |
|---|---|
| `requires` | Checked when a root run is created. An unmet requirement refuses the run and names what is missing |
| `dependencies` | Other libraries this one references |
| `inputs` | Library-wide parameter values |
| `resources` | Folders mounted for every agent step that has skills, unless it opts out |
| `entrypoints` | Templates people start; others (child templates) are not offered |
| `notifications` | Default events to notify on. Organisation configuration wins; a library cannot name a channel |

An unknown key or a malformed manifest makes the library invalid: new runs
using it are refused with the reason; runs already started are unaffected.

---

## 3. Sources

**Changed from the first design (review finding 5.4).** A library is a folder.
Where the folder comes from is a **source**:

| Source | Use |
|---|---|
| `directory` | A path the API service can read. The solo profile's normal source: edit a file, start a run, and the change is used |
| `git` | A repository and a reference (branch or tag), fetched by the API service. The usual source for a team or organisation |
| `package` | A NuGet package. Optional, for distributing versioned libraries publicly |

Whatever the source, at run start the API resolves every content item the run
needs, stores each in the artifact store by content hash, and writes the run's
**lock** (section 5). Workers resolve content only by hash from the lock, so a
library edit never changes a run already under way, and no worker needs access
to the source.

`ff library init <folder>` copies the standard library into a folder to start
from (from FF).

---

## 4. Layers

| Layer | May |
|---|---|
| Base: `std` | Define everything |
| Organisation: libraries named in organisation configuration | Override any content item by name; add content. An override of a role may tighten its write access or remove tools, never widen them (F10) |
| Repository: the `library` section of `feature-flow.yaml` | Name a default template; **add** skills, resources and tasks under its own name. Not override. Read from the default branch, so an agent cannot change what it is given |
| Run: a library override | Replace content for this run's future steps (section 5.2) |

A template refers to content by library name, so an organisation override is
explicit: `acme:tasks/implement-ticket` replaces `std:tasks/implement-ticket`
only where the organisation's configuration maps one to the other.

---

## 5. The lock and mid-run override

### 5.1 The lock

At run start the engine pins every library and content item the run uses to an
exact version and content hash, and records the lock with the run. A child run
uses its root's lock. The lock is what lets an audit say exactly what an agent
read (P7).

### 5.2 Mid-run override

A **library override** replaces the content that future steps of a running run
resolve, for example a corrected task prompt. It does not change the plan.

| Rule | Detail |
|---|---|
| Who | A person with the role for content overrides (`docs/05` section 5). An agent may propose one, which becomes a decision |
| Scope | Steps that start after the override; steps in flight keep what they resolved. It applies to the run tree from that run down |
| Record | Actor, reason, old and new hash, and the step it applies from. A lock entry, not a plan revision |
| Protected steps | Allowed: correcting a broken prompt on a protected step is the main case. The decision shows that a protected step is affected |
| Any run | Works on every run, whatever its template. FF could override only runs whose template spawned children (`../feature-factory/issues.md`) |

---

## 6. The standard library

`std` ships with the engine. Its templates reproduce the process FF proved,
with the first design's review and merge improvements. Everything here is
policy and may be replaced.

### 6.1 Roles

| Role | Write access | Notes |
|---|---|---|
| `planner` | none | May hold planning authority; tracker toolset where allowed |
| `designer` | none | — |
| `implementer` | branch | `ask_human` enabled by its standard steps |
| `fixer` | branch | — |
| `reviewer` | none | `context: none` in its standard steps, so it judges independently. Mapped to a different, ideally stronger, model than the implementer in the example organisation configuration; the engine records both models |
| `triager` | none | — |
| `conflict_resolver` | branch | — |

These replace FF's `orchestrator`, `worker` and `validator`, which mixed role
and permission.

### 6.2 Artifact types

| Type | Evidence | Content |
|---|---|---|
| `requirements` | No | Requirements with ids, `supports`, `owner`, behaviour (FF's verification plan) |
| `delivery_plan` | No | Acceptance criteria with `supports`, `owner` and verification methods (FF's delivery plan) |
| `evidence_claims` | No | The implementer's mapping from methods to test identities |
| `work_list` | No | Child units of work (tickets, epics) with repositories and parameters, for `plan.expand` |
| `findings_report` | No | Findings from a reviewer, with fingerprint fields, severity, blocking and basis |
| `triage` | No | Per finding: new state, reason, and "regression test not applicable" with a reason where claimed |
| `contract` | No | A parent's interface or behaviour contract for its children |
| `test_results`, `analysis_results`, `check_results`, `merge_record`, `validation_report`, `trace_report` | Yes | Produced only by engine actions |

### 6.3 Fragments

| Fragment | Steps | Exits |
|---|---|---|
| `baseline` | `test.run` and `analysis.run` with `baseline: true` | `done` |
| `plan_and_validate` | Planner agent producing `requirements` and `delivery_plan`; `artifact.validate` with `requirement-support`, `requirement-ownership` or `requirement-inheritance` (by level), `verification-method-support`; on `invalid` back to the planner | `planned` |
| `standard_review` | `for_each` over `angles` (default: correctness, design, tests, security, specification) of a reviewer agent step, each producing a `findings_report`; `findings.record` for each; a reviewer with `changed_files_unread > 0` re-runs | `reviewed` |
| `fix_loop` | Triager agent producing `triage`; `findings.triage`; fixer agent; a verifying reviewer checking the fix against the original finding on the full diff; `artifact.validate` with `tests-added`; back to tests | `fixed`, `escalated` |
| `verify_and_review` | `test.run`; `analysis.run`; `artifact.validate` with `verification-satisfaction`; `standard_review`; then the merge bar: `findings.open_blocking == 0` to `ready`, otherwise `fix_loop`. `repeat_fixes > 0` escalates | `ready`, `escalated` |
| `merge_into_parent` | `scm.pull_request`; optionally `findings.record` from an external source; `ci.checks`; `scm.catch_up`; re-test if `changed`; `scm.merge`; `base_moved` back to catch-up; `conflict` to a conflict-resolver agent, then back through tests and review | `merged`, `declined` |
| `plan_children` | Planner producing a `work_list` and child-level requirements; validation; an approval decision that ratifies them; `plan.expand` into a `parallel` of `run.child` | `planned` |
| `trace` | `artifact.validate` with `requirement-trace`, producing a `trace_report` | `traced` |
| `deliver_in_order` | A `for_each` chain of `merge_into_parent` per repository; `delivery.report`; on `partial`, a decision offering retry, fix forward or revert | `delivered`, `partial` |

### 6.4 Templates

| Template | Level | Shape |
|---|---|---|
| `ticket-delivery` | ticket | `baseline`; `plan_and_validate`; spec approval (auto-resolvable); implement; `verify_and_review`; `merge_into_parent`. Works standalone (parent branch is the default branch) or as a child |
| `epic-delivery` | epic | Design and contract; `plan_children` (tickets); the epic's own `verify_and_review` over the combined branch with the tickets' ledgers as prior findings; `trace`; `merge_into_parent` or `deliver_in_order` |
| `project-delivery` | project | An interactive session for the project's requirements and architecture; approval that ratifies them; `plan_children` (epics); integration tests where the project owns requirements itself; `trace`; `deliver_in_order` into the default branch, where by default a person merges (F1) |

Budget: `project-delivery` sets `max_work_items: 20`, within the organisation's
ceiling.

### 6.5 Default policies moved out of the engine

Each default below was an engine rule in the first design (RW4) and is now set
by the standard library. The last column says how to change it.

| Policy | Where in `std` | To change |
|---|---|---|
| The merge bar: no open blocking finding | The guard in `verify_and_review` | Change the guard, for example to also require `open == 0` |
| A demonstrated bug blocks; a reasoned finding does not | The reviewer task's rule for setting `blocking` | Edit the reviewer task |
| A fix adds a regression test, unless the triage says why not | The fixer task, and `tests-added` in `fix_loop` | Remove the validator from the fragment |
| A fixed or declined finding reopens only when its code changes | `reopen_on: code_change` in `standard_review` | Set `reopen_on` |
| A finding fixed twice and raised again escalates | The `repeat_fixes` guard in `verify_and_review` | Change or remove the guard |
| The reviewer reads every changed file | The `changed_files_unread` guard in `standard_review` | Narrow or remove it for a specialised review |
| A merge conflict goes to an agent first | `merge_into_parent` | Route `conflict` to a decision instead |
| Squash tickets into the epic; plain merges above | `with: { squash: ... }` on each template's `merge_into_parent` | Change the parameter |
| Delivery to `main` once, at project level | `project-delivery` | Use `merge_into_parent` at epic level instead |

---

## 7. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | Is the `git` library source fetched by the API at run start acceptable for the enterprise profile, where the API may not reach every repository host? | Yes, with `package` as the fallback for hosts the API cannot reach |

Judgement calls made in this document are in `docs/LOG.md`, entries 38 to 42.
