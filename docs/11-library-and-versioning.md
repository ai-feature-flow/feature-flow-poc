# Library packaging and versioning

Status: draft. Companion to `docs/09-workflow-language.md` and `docs/07-configuration.md`.

## 1. Why a library exists

The workflow language is data-first, but real delivery workflows need reusable content: role definitions, prompts, skills, static checks, templates and policy references. Those are not part of the engine core; they are loaded from a versioned library package.

The design therefore treats workflow content as a package ecosystem, not as ad hoc YAML scattered in repositories.

## 2. Library package model

A library package is a versioned bundle of workflow content. It contains:

- `templates`
- `roles`
- `tasks`
- `skills`
- `checks`
- `policies`
- `providerMappings`
- `manifest` metadata and dependency declarations

Example package layout:

```yaml
name: feature-flow-default
version: 1.4.2
kind: LibraryPackage
content:
  templates:
    - path: templates/feature-ticket.yaml
  roles:
    - path: roles/implementer.yaml
  tasks:
    - path: tasks/plan-ticket.md
  skills:
    - path: skills/patch-review.yaml
  checks:
    - path: checks/merge-bar.yaml
```

A package is versioned semantically and pinned by the run at plan resolution time. The engine records the exact content hash used for every step.

## 3. Distribution model

The engine follows the distribution repository model.

- The base engine ships with a stable distribution package set.
- Organisations add or override content in a distribution repository.
- The repository is not a fork of the engine. It is a content overlay.
- The organisation may supply custom roles, checker policies, provider mappings and repository templates without modifying engine code.

The effective library for a run is:

`base library + organisation overrides + repository overrides + run overrides`

This ordering preserves the engine default while allowing enterprise-specific policy.

## 4. Versioning strategy

### 4.1 Package versioning

Library packages use semantic versioning:

- `major`: breaking workflow contract changes
- `minor`: backward-compatible addition of content or rules
- `patch`: bug fixes and prompt fixes

A plan and a library package are versioned separately. A library change does not require an interpreter change unless the underlying schema changes.

### 4.2 Content-addressed references

Once resolved, each library item is stored in the artifact store with a content hash.

Example:

```yaml
agentTask: lib/tasks/plan-ticket@1.2.0
artifactRef: sha256:ab12...fe98
```

The run stores the exact package version and hash used. That is how later audits determine what the agent actually processed.

## 5. Role model

A role is not a local operating system concept; it is workflow content.

Example:

```yaml
name: implementer
writeAccess: branch
capabilities:
  - os-linux
  - git
  - review
model:
  default: claude-sonnet
runtime:
  agentRuntime: claude-code
```

Rules:

- `writeAccess` is enforced by the engine and the hook endpoint
- a role may not grant more than the workflow allows via safety floors
- a repository cannot override a role to exceed a stricter policy floor

This keeps policy in library content while keeping enforcement in the engine.

## 6. Task and skill binding

Tasks and skills are bound to steps through references, not inline text.

```yaml
inputs:
  taskRef: lib/tasks/implement-ticket@3.0.0
  skills:
    - lib/skills/patch-review@2.1.0
```

A task may receive parameter bindings at runtime. For example, a planning task can receive `ticketId` and `repo` without embedding them in the prompt text.

The engine resolves the parameters and records the final rendered task context as an artifact reference, not as inlined data in workflow state.

## 7. Override semantics

### 7.1 Organisation override

An organisation may replace a task or role in its distribution repository. Example:

```yaml
overrides:
  - path: lib/tasks/plan-ticket.md
    package: org/feature-flow-enterprise
    version: 2.0.0
```

### 7.2 Mid-run override

A running plan may be amended with a library override. This is a run-scoped change, not a global one.

Semantics:

- future steps use the new content
- completed steps keep their original artifact refs
- the change is recorded with actor, reason and old/new hash
- the override may be rejected if it would modify protected policy or already-executed steps

This is required by the prototype lesson that mid-run fixes are essential.

## 8. Policy references

Policy is not hard-coded into templates. Instead, each step or library item can reference a named policy object.

```yaml
policy:
  approval: policies/default-approval
  merge: policies/release-merge
```

Policy references are resolved at run start, and the engine validates them against the configured organisation and repository policy model.

## 9. Validation and packaging checks

The package manager or loader validates:

- referenced package names exist
- version constraints resolve
- content hashes match package manifests
- tasks, skills and roles are all valid against the schema
- role `writeAccess` and provider requirements do not exceed the engine floors
- no unresolved references remain in the package

This is done before the run starts, and again if an override changes the package in-flight.

## 10. Working example

```yaml
apiVersion: feature-flow/v1
kind: LibraryPackage
name: feature-flow-default
version: 1.4.0
roles:
  - name: planner
    writeAccess: none
    capabilities: [agent-core]
  - name: implementer
    writeAccess: branch
    capabilities: [git, agent-core]

templates:
  - name: ticket-delivery
    path: templates/ticket-delivery.yaml

tasks:
  - name: plan-ticket
    path: tasks/plan-ticket.md
    version: 1.2.0

skills:
  - name: patch-review
    path: skills/patch-review.yaml
    version: 2.1.0
```

## 11. Risks

The biggest packaging risks are:

- hidden dependency drift between package and engine version
- late override of a policy object that changes implications mid-run
- template authors assuming a role or capability exists in every installation

The mitigations are:

- explicit capability negotiation at run start
- content-addressed references to exact package contents
- required validation before a run begins
- recorded override history with restriction to future steps only

## 12. Open questions

- Should a library package support a single source of truth for both task text and check logic, or should tasks and checks be separate packages?
- Should run overrides be limited to the current package version, or may they depend on a newer one already in the distribution repo?
- How should a library package declare its “minimum engine version” for compatibility checks?
