# Library packaging, versioning and override

Status: second draft. Date: 2026-10-27. Deliverable 3 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 09 applied.** One package manifest, not two (D8). One reference format,
not three (D8). NuGet-style packaging addressed and justified (D8). Versions
resolve to a lock at run start (D8). The repository layer is **additions only**,
per the product owner's decision (D9). A package declares the plan schema version
and minimum engine version it needs (answer 9).

Companion to `docs/09-workflow-language.md` (roles, tasks, skills) and
`docs/07-configuration.md` (the configuration layers this sits beside).

---

## 1. What a library package is

One package type, holding all content types (review 09, answer 7). The first
draft asked whether tasks and checks should be separate packages; they should
not, because a check and the task that produces its input are usually changed
together, and splitting them would make the lock file a dependency graph for no
gain.

| Content type | Directory | Referenced by |
|---|---|---|
| `templates` | `templates/` | A run's `plan.ref` |
| `tasks` | `tasks/` | `agent.run` `task_ref` |
| `skills` | `skills/` | A role's `skills` |
| `checks` | `checks/` | `review.run` `checks` |
| `roles` | `roles/` | `agent.run` `role` |
| `schemas` | `schemas/` | An artifact type's `schema_ref` |
| `prompts` | `prompts/` | `human.gate` `prompt_ref` |

### 1.1 The manifest

One manifest, at the package root. The first draft had two different shapes in
two sections of the same document.

```yaml
apiVersion: feature-flow/v1
kind: LibraryPackage
name: feature-flow-default
version: 1.4.2

requires:
  plan_schema_version: 1
  min_engine_version: 0.1.0

dependencies:
  - name: feature-flow-core
    version: ">=1.0.0 <2.0.0"

contents:
  templates:
    - name: ticket-delivery
      path: templates/ticket-delivery.yaml
    - name: epic-delivery
      path: templates/epic-delivery.yaml
  tasks:
    - name: plan-ticket
      path: tasks/plan-ticket.md
    - name: implement-ticket
      path: tasks/implement-ticket.md
  skills:
    - name: patch-review
      path: skills/patch-review.yaml
  checks:
    - name: merge-bar
      path: checks/merge-bar.yaml
  roles:
    - name: planner
      path: roles/planner.yaml
    - name: implementer
      path: roles/implementer.yaml
  schemas:
    - name: ticket-plan
      path: schemas/ticket-plan.schema.json
  prompts:
    - name: spec-approval
      path: prompts/spec-approval.md
```

`requires` is checked at load (review 09, answer 9). A package whose
`plan_schema_version` the engine does not support, or whose `min_engine_version`
is newer than the running engine, fails to load with a named error.

---

## 2. Packaging: NuGet, with a content overlay

Review 09 D8 asks for NuGet-style packaging or a justified alternative.

**Decision: NuGet for the package format, and a directory overlay for
organisation content.** They are different problems.

| Concern | Mechanism | Why |
|---|---|---|
| Distributing a versioned, immutable, content-addressed bundle with dependencies | **NuGet package** (`.nupkg`) | The engine is .NET (Q1). Providers are already NuGet (Q7). One toolchain, one feed, one versioning scheme. Semantic versioning, dependency ranges and integrity hashes come free |
| An organisation adding or overriding content without building a package | **A directory in the distribution repository** | An organisation editing a prompt should not need a packaging pipeline. The distribution repository already exists (Q7) |

The overlay directory has the same layout as an extracted package and is loaded
as if it were a package named `org/<org-name>` at version `0.0.0-local`. It is
built into the worker image at deployment time, so at runtime there is no
difference between the two: both are resolved packages in the lock.

**Rejected: a bespoke archive format.** It would need its own versioning,
integrity, dependency resolution and tooling for no benefit the .NET ecosystem
does not already provide.

**Rejected: Git as the distribution mechanism.** `docs/01` 2.1: Git is not a
store.

---

## 3. One reference format

Review 09 D8. The first draft used `roles/implementer`, `lib/tasks/x` and
`lib/tasks/x@1.2.0` in the same document.

```
<package>[@<version-constraint>]:<type>/<name>
```

| Part | Rule |
|---|---|
| `package` | Lowercase, hyphenated. `feature-flow-default`, `org-acme` |
| `version-constraint` | Optional. A semver range: `1.2.0`, `>=1.2.0 <2.0.0`. Omitted means "whatever the lock resolves" |
| `type` | One of the seven content types in section 1 |
| `name` | The `name` from the manifest's `contents`, not the path |

Examples:

```yaml
task_ref: feature-flow-default:tasks/implement-ticket
role: feature-flow-default:roles/implementer
schema_ref: feature-flow-default:schemas/ticket-plan
task_ref: org-acme@>=2.0.0:tasks/implement-ticket
```

**Paths are never referenced.** A reference names a content item; the manifest
maps it to a path. That is what lets a package reorganise its files without
breaking every plan that references it.

---

## 4. Resolution and the lock

### 4.1 Resolution

At run start, before any step executes:

1. Collect the effective package set (section 5).
2. Resolve every reference in the plan and in the library content to a concrete
   package version, honouring version constraints and dependencies.
3. Resolve every content item to a **content hash**.
4. Write a **lock** to the artifact store and record its hash in the run.

### 4.2 The lock

```yaml
apiVersion: feature-flow/v1
kind: LibraryLock
run_id: run-1234
resolved_at_revision: 1
packages:
  - name: feature-flow-default
    version: 1.4.2
    hash: sha256:9f2a...
  - name: org-acme
    version: 0.0.0-local
    hash: sha256:c81d...
items:
  feature-flow-default:tasks/implement-ticket: sha256:ab12...
  feature-flow-default:roles/implementer:     sha256:7e40...
  org-acme:tasks/implement-ticket:            sha256:31ff...
```

### 4.3 Why a lock

| Without a lock | With a lock |
|---|---|
| A package updated mid-run changes what a later step resolves | Every step in the run resolves to the same content |
| An audit cannot say what the agent read | The run records the exact hash |
| A re-run of the same plan may behave differently | A re-run resolves identically |

The lock is what makes "content-addressed in the artifact store once resolved, so
a run records exactly what it used" (brief L5) actually true.

---

## 5. Effective library: the layers

Review 09 D9, decided by the product owner 2026-10-27.

> A repository may choose a template and **add** its own skills and checks. It may
> not override organisation roles, policies or tasks.

| Layer | May | May not |
|---|---|---|
| **Base** — the packages the engine ships | Define everything | — |
| **Organisation** — the distribution repository overlay | Override any content type; map roles to runtime instances and models; tighten `write_access` | Relax `write_access` |
| **Repository** — `.feature-flow/` in the repository | **Choose a template**; **add** skills and checks | Override anything. No roles, no policies, no tasks |
| **Run** — an API override (section 6) | Override content for this run, future steps only | Change the plan structure (that is a patch) |

The first draft gave the repository layer a general override power it was never
decided to have, and which contradicts `docs/07` 1.1: repositories may not set
role mapping or policy. Additions only.

`.feature-flow/` is reserved in `docs/01` 5.1.1 and unused in version 1. This
brief gives it its first use: repository-added skills and checks.

```yaml
# .feature-flow/library.yaml
apiVersion: feature-flow/v1
kind: RepositoryLibrary
template: feature-flow-default:templates/ticket-delivery
adds:
  skills:
    - name: acme-api-conventions
      path: .feature-flow/skills/acme-api-conventions.yaml
  checks:
    - name: acme-openapi-lint
      path: .feature-flow/checks/acme-openapi-lint.yaml
```

A repository that tries to override a task or a role is a **validation error at
run start**, not a silent override. That is the same rule `docs/07` applies to
`merge.method`.

---

## 6. Mid-run override (L8)

Review 09 E5: one mechanism, separate from a plan patch.

### 6.1 What it is

An override replaces the **content** a future step resolves. It does not change
the plan.

```yaml
apiVersion: feature-flow/v1
kind: LibraryOverride
run_id: run-1234
applies_from_step: implement
actor: { type: human, id: "stuart@acme.internal" }
reason: "the check prompt mis-stated the merge bar"
changes:
  - ref: feature-flow-default:checks/merge-bar
    to: { package: org-acme, type: checks, name: merge-bar }
    old_hash: sha256:aa11...
    new_hash: sha256:bb22...
```

### 6.2 Semantics

| Question | Answer |
|---|---|
| Which steps see it? | Steps that **start after** the override. A step already in flight keeps its resolved content |
| Does it change the plan? | No. The plan revision is unchanged |
| Does it change the lock? | Yes. The lock gains an override entry with old and new hash |
| Is it audited? | Yes: actor, reason, timestamp, old hash, new hash, and the step it applies from |
| May it use a newer package version? | Yes, **if** it is in the distribution repository (review 09, answer 8). Recorded with both hashes |

### 6.3 Why this is not a patch operation

A patch changes plan structure and produces a new revision. An override changes
content resolution and produces a lock entry. Mixing them would make a plan
revision mean two different things, and would put prompt text into the plan
revision history, which is meant to be a record of structure.

The first draft had both a `override_library_reference` patch operation and a
separate mid-run override section. The operation is removed.

---

## 7. Versioning

| Thing | Versioned how | Bumped when |
|---|---|---|
| Package | Semantic version | Major: a breaking content contract change. Minor: new content, or a backward-compatible change. Patch: a prompt or check fix |
| Content item | Content hash | Every change |
| Plan schema | `plan_schema_version`, an integer | A breaking change to `docs/10` |
| Interpreter | Temporal patching API (A14) | A behaviour change in workflow code |

A package version and a plan schema version are independent. A library change
needs no interpreter change unless the schema changes (S14).

---

## 8. Validation at load

| Check | Blocks |
|---|---|
| Every referenced package resolves | Yes |
| Every referenced content item exists in its package | Yes |
| `requires.plan_schema_version` is supported by the engine | Yes |
| `requires.min_engine_version` is not newer than the engine | Yes |
| Dependencies resolve without conflict | Yes |
| Content hashes match the manifest | Yes |
| A repository layer entry is an addition, not an override | Yes |
| A role's `write_access` is not relaxed by an organisation override | Yes |

---

## 9. Open questions

- **Overlay build step.** The organisation overlay is a directory in the
  distribution repository, built into the worker image. Is that build step part of
  the distribution repository's host project (Q7), or a separate tool?
- **Package feed.** Public packages from nuget.org, organisation packages from an
  internal feed. Does the engine need to support both in one installation, and
  does the solo profile need a feed at all, or can it read a local directory?
- **Content item versioning.** Items are content-hashed but not individually
  versioned. Is a per-item version needed for anything the hash does not already
  answer?
