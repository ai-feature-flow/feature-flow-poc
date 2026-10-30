# Library packaging, versioning and override

Status: revised 2026-10-31 for readability. Deliverable 3 of
`docs/briefs/brief-02-workflow-language.md`.

**Scope.** What a library package is, how it is packaged and published, how
content is referenced, how versions are pinned for a run, which layers may add
or override content, how content is overridden mid-run, and what is validated.

**Companions.** `docs/09-workflow-language.md` defines the content itself
(roles in section 11, tasks and skills in section 12). `docs/07-configuration.md`
holds the configuration layers this sits beside, including `roleMapping`, which
maps library roles to agent runtimes and models.

**Library content** is everything a workflow uses that is not engine code:
plan templates, task prompts, skills, review checks, agent roles, artifact
schemas and gate prompts. Library content uses snake_case keys (`docs/09`
section 1).

---

## 1. What a library package is

One package type holds every content type. Tasks and checks are not split into
separate packages: a check and the task that produces its input usually change
together, and splitting them would turn the lock (section 4) into a dependency
graph for no gain.

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

One manifest, at the package root.

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

**`requires` is checked when the package loads.** A package whose
`plan_schema_version` the engine does not support, or whose
`min_engine_version` is newer than the running engine, fails to load with a
named error.

---

## 2. Packaging: NuGet, with a content overlay

**Decision: NuGet for the package format, and a directory overlay for
organisation content.** They solve different problems.

| Concern | Mechanism | Why |
|---|---|---|
| Distributing a versioned, immutable bundle with dependencies | **NuGet package** (`.nupkg`) | The engine is .NET (decision Q1) and providers are already NuGet packages (decision Q7). One toolchain, one feed, one versioning scheme. Semantic versioning, dependency ranges and integrity hashes come free |
| An organisation adding or overriding content without building a package | **A directory in the distribution repository**, published to the artifact store | Editing a prompt should not need a packaging pipeline. The distribution repository (the organisation's repository that assembles an installation, decision Q7) already exists |

The overlay directory has the same layout as an extracted package. It is loaded
as if it were a package named `org-<org-name>` at version `0.0.0-local`.

**Rejected: a bespoke archive format.** It would need its own versioning,
integrity, dependency resolution and tooling, which NuGet already provides.

**Rejected: Git as the distribution mechanism.** Git is not a store (`docs/01`
section 2.1, the storage model).

### 2.1 Content lives in the artifact store, not the worker image

> **Library content, overlay included, is published to the artifact store by
> content hash when the distribution repository merges. Workers resolve it by
> hash from the lock (section 4). Images carry the engine and providers, not
> content.**

Why: if content were built into the worker image, fixing a broken check prompt
on a running run would need an image build and a worker deploy, and in-flight
workers on the old image could not resolve the new content at all.

| | Content in the image | Content in the artifact store |
|---|---|---|
| Fixing a check prompt mid-run | Image build plus worker deploy | Publish, then a lock entry |
| An in-flight worker resolving new content | Cannot; it has the old image | Yes; it resolves by hash |
| Rollback | Redeploy | Point the lock at the old hash |

A mid-run override (section 6) is then a lock entry pointing at content that
**already exists**, with no redeploy.

**Publish step.** A step in the distribution repository's pipeline writes
content to the artifact store by hash. It is the same store the engine already
uses, so no new infrastructure is needed.

---

## 3. One reference format

Every reference to library content uses one format:

```
<package>[@<version-constraint>]:<type>/<name>
```

| Part | Rule |
|---|---|
| `package` | Lowercase, hyphenated. `feature-flow-default`, `org-acme` |
| `version-constraint` | Optional. A semantic version range: `1.2.0`, `>=1.2.0 <2.0.0`. Omitted means "whatever the lock resolves" |
| `type` | One of the seven content types in section 1 |
| `name` | The `name` from the manifest's `contents`, not the file path |

Examples:

```yaml
task_ref: feature-flow-default:tasks/implement-ticket
role: feature-flow-default:roles/implementer
schema_ref: feature-flow-default:schemas/ticket-plan
task_ref: org-acme@>=2.0.0:tasks/implement-ticket
```

**Paths are never referenced.** A reference names a content item; the manifest
maps it to a path. A package can reorganise its files without breaking any plan
that references it.

---

## 4. Resolution and the lock

The **lock** is a file that pins every library package and content item a run
uses to an exact version and content hash. It has nothing to do with merging:
there is no merge lock (`docs/20`, the merge design).

### 4.1 Resolution

At run start, before any step executes:

1. Collect the effective package set (section 5).
2. Resolve every reference in the plan and in the library content to a concrete
   package version, honouring version constraints and dependencies.
3. Resolve every content item to a **content hash**.
4. Write the lock to the artifact store and record its hash in the run.

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

The lock is what makes the brief's requirement true that library content is
content-addressed once resolved, so a run records exactly what it used (item L5
of `docs/briefs/brief-02-workflow-language.md`).

---

## 5. Effective library: the layers

Decided by the product owner (decision D9, `docs/14`):

> A repository may choose a template and **add** its own skills and checks. It
> may not override organisation roles, policies or tasks.

| Layer | May | May not |
|---|---|---|
| **Base**: the packages the engine ships | Define everything | — |
| **Organisation**: the distribution repository overlay | Override any content type; map roles to runtime instances and models (`roleMapping`, `docs/07` section 4); tighten a role's `write_access` (how much of the repository an agent may change) | Relax `write_access` |
| **Repository**: the `library` section of `feature-flow.yaml`, with content files in `.feature-flow/` | **Choose a default template**; **add** skills and checks | Override anything. No roles, no policies, no tasks |
| **Run**: an API override (section 6) | Override content for this run, future steps only | Change the plan structure (that is a patch, `docs/09` section 15) |

**Role mapping is configuration, not library content.** The organisation's
`roleMapping` (`docs/07` section 4) maps each role to an agent runtime instance
and a model; the role itself never names one. The reviewer's model must differ from the
authoring roles' models (`docs/19`, the code review design).

Repository additions follow the configuration rule that a repository may not
set role mapping or policy (`docs/07` section 1.1).

**One repository file.** The library section goes **in** `feature-flow.yaml`,
not in a second file: two files for one repository's configuration is the
"settings conflicted" shape the prototype failed on.

```yaml
# feature-flow.yaml
apiVersion: feature-flow/v1
kind: Repository

# ... other sections: environment, setup, testTiers, staticAnalysis, ci, branches, merge (docs/07 section 5)

library:
  default_template: feature-flow-default:templates/ticket-delivery
  adds:
    skills:
      - name: acme-api-conventions
        path: .feature-flow/skills/acme-api-conventions.yaml
    checks:
      - name: acme-openapi-lint
        path: .feature-flow/checks/acme-openapi-lint.yaml
```

`.feature-flow/` holds only the **content files** this section references.

| Question | Answer |
|---|---|
| Who chooses the template? | The repository names a **default**; a run may choose another, if the organisation allows it |
| When are added skills and checks resolved? | Into the **lock** at run start, not read per step |

Resolving into the lock keeps additions auditable: the run records the exact
hashes it used, and a later change to the repository does not silently alter
what a running run resolves.

A repository that tries to override a task or a role is a **validation error at
run start**, never a silent override. `docs/07` section 5 applies the same rule
to the repository's merge method.

---

## 6. Mid-run override

A **mid-run override** (item L8 of `docs/briefs/brief-02-workflow-language.md`)
replaces the library content that
future steps of a running run resolve. It is one mechanism, separate from a
plan patch.

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
| May it use a newer package version? | Yes, **if** that version is in the distribution repository. Recorded with both hashes |

### 6.2.1 Who may make an override

| Actor | May |
|---|---|
| A human with the `steerer` or `admin` role (`docs/07` section 7.2) | Apply an override. Audited |
| An agent | **Propose** one. It becomes a **pending human decision**, the same way an agent's patch that touches protected content does (`docs/09` section 15.5) |
| Anyone else | No |

An override of content used by a **protected step** (one no re-plan or
steering may remove or weaken, floor F3) is **allowed**. That is the case the
mechanism exists for: a broken check prompt corrected on a running run. The
decision request **shows that it affects a protected step**, so the human
approving it knows what they are approving.

An agent is never silently refused and never silently trusted: it proposes, a
human decides, and the decision is recorded.

### 6.3 Why this is not a patch operation

A patch changes plan structure and produces a new plan revision. An override
changes content resolution and produces a lock entry. Mixing them would make a
plan revision mean two different things, and would put prompt text into the
plan revision history, which is a record of structure. There is no patch
operation for overriding library content (`docs/09` section 15.6).

---

## 7. Versioning

| Thing | Versioned how | Bumped when |
|---|---|---|
| Package | Semantic version | Major: a breaking content contract change. Minor: new content, or a backward-compatible change. Patch: a prompt or check fix |
| Content item | Content hash | Every change |
| Plan schema | `plan_schema_version`, an integer | A breaking change to the plan schema (`docs/10`) |
| Interpreter | Temporal's patching API (`Workflow.Patched`) | A behaviour change in workflow code. Every interpreter change is versioned so running workflows replay correctly (DR7) |

A package version and a plan schema version are independent. A library change
needs no interpreter change unless the plan schema changes (decision S14: the
plan schema is versioned separately from the interpreter).

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

- **Package feed.** Public packages from nuget.org, organisation packages from
  an internal feed. Does the engine need to support both in one installation,
  and does the solo profile (`docs/07` section 10.1) need a feed at all, or can
  it read a local directory?
- **Content item versioning.** Items are content-hashed but not individually
  versioned. Is a per-item version needed for anything the hash does not
  already answer?
