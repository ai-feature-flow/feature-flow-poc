# Validation rules and workflow testing

Status: draft. Companion to `docs/09-workflow-language.md`, `docs/10-workflow-schema.md`, and `docs/11-library-and-versioning.md`.

## 1. Validation model

Validation is multi-phase so workflow authors receive errors in the right place, and the engine keeps safety guarantees.

## 2. Validation table

| Phase | What is checked | Typical failures | Blocking? |
|---|---|---|---|
| Template load | schema, expression syntax, route ids, parameter declarations, duplicate ids | unknown step kind, unresolved `${...}` | Yes |
| Library resolve | package version, role existence, requirement satisfaction, task refs, skill refs | missing library item, invalid package version | Yes |
| Run start | provider capability negotiation, policy eligibility, repo manifest compatibility | capability missing, role not allowed, invalid repo config | Yes |
| Step start | inputs satisfy contract, required artifact refs exist, task resolves | missing artifact, invalid input value | Yes |
| Patch validation | protected steps, route targets, scope consistency, step existence | patch tries to remove protected step | Yes |
| Warning checks | style, maybe redundant route, unused parameter, optional check drift | deprecated policy reference | No |

### 2.1 Blocking vs warning

Blocking checks should be reserved for conditions where a run cannot safely continue.

Warnings are used for:

- unused parameters
- duplicate route definitions that still work
- deprecated task refs with valid replacements
- policy references that do not yet affect a run

Warnings should not silently pass into production logic.

## 3. Rule categories

### 3.1 Structural rules

These ensure the plan is valid as data.

- every step has a unique `id`
- every route `to` target exists
- no route points to a missing step
- every expression parses and resolves
- every required parameter is set or has a default
- no protected step is removed or weakened in a patch

### 3.2 Semantic rules

These ensure the plan is valid for execution.

- `writeAccess` does not exceed the role's allowed entry points
- provider requirements match configured providers
- a `parallel` step has a valid join strategy
- a child plan scope is compatible with the parent workflow generation rules
- no step uses a capability not negotiated at run start

### 3.3 Safety-floor rules

These are enforced by the engine, not by workflow content.

- human approval is required before default-branch merge
- secrets are only references, never inline values
- protected steps are immutable to re-plan or steering
- attempt ceilings cannot be exceeded

## 4. Workflow testing strategy

The workflow author must be able to test templates without real agents, real repositories, or long-lived external systems.

## 5. Test harness

The testing model is built on the dry-run and recording provider approach already described in the architecture and configuration docs.

### 5.1 Fake providers

The harness provides fake implementations for:

- sandbox
- code host
- tracker
- CI provider
- review provider
- notification provider
- agent runtime

Fake providers should:

- return deterministic structured responses
- allow the test to simulate findings, branch state and commit outcomes
- record the exact API sequence for replay

### 5.2 Recorded runs

A recorded run is a real run executed once with real or fake providers in a capture mode. It records:

- plan revisions
- provider calls
- outcomes and route transitions
- human decisions and gate timing
- artifact references

The captured history is then replayed against a changed template, interpreter or library version.

## 6. Test types

### 6.1 Golden tests for plan and patch shape

Each template gets a golden file for:

- resolved plan JSON
- route graph
- patch semantics after re-plan
- expected validation errors

These are compact and deterministic.

### 6.2 Replay tests

Replay tests are mandatory for interpreter code, because the .NET engine can be non-deterministic without runtime detection. The goal is to ensure the plan history replays to the same state.

### 6.3 Dry-run tests for workflow logic

Dry-run tests should validate:

- a step resolves to the expected outcome
- a route chooses the right next step
- a patch adds or changes the correct branch
- review findings trigger the correct fix path

### 6.4 Failure injection tests

Workflow tests should deliberately exercise:

- worker crash during long-running agent step
- transient provider failure during patch application
- conflict on merge or replan
- protected-step removal attempt
- loss of inbound event with eventual read confirmation

## 7. Example test set

```yaml
tests:
  - name: single-ticket-success
    template: templates/ticket-delivery.yaml
    params:
      ticketId: FF-1042
      repo: service
    expectedOutcome: completed
  - name: protected-step-replan-fails
    patch: patches/replan/remove-protected-step.yaml
    expectedError: protected_step_violation
  - name: findings-open-routes-to-fix
    fixture: fixtures/findings-open.json
    expectedNext: fix.verify
```

## 8. Regression and stability checks

A workflow library should have a CI gate that runs:

- schema validation for every template
- library reference resolution
- replay tests for changed interpreter versions
- patch validation tests for protected step behavior
- a minimal dry-run of each library template

This ensures library content cannot silently drift away from engine semantics.

## 9. Open questions

- Should workflow tests live with the library package or in a separate repo?
- Is there a requirement for “golden” patch diffs, or is a functional expectation sufficient?
- Should plan validation be purely static, or should templates be allowed to register low-priority runtime warns only?
