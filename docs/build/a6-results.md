# A6 results: documents match the code

Milestone A6 of `docs/12-build-plan-phase-2.md` section 4, built 2026-11-22 on the A6 branch from four parallel
read-only audits (one per set of documents), four parallel editors on the same sets, an independent review and one
fix pass. Every owning document was read against the code; each mismatch was fixed, or the mechanism marked planned
with its milestone, or removed and listed below as a follow-up. Two lines of code changed. No live run was needed. No
replay history changed.

## What was built

The audits found about 120 mismatches: 24 in `docs/00` to `03`, 24 in `docs/04`, 38 in `docs/05` to `07`, 34 in
`docs/08` to `10` and the other documents. None was rejected on re-checking against the code.

| Document | Found and fixed |
|---|---|
| `docs/00` | P3: an agent proposes nothing in version 1 |
| `docs/01` | The run status `starting` added; run position described, not given names the code lacks; an artifact is always stored bytes (no "reference" kind); a role is write access and tools; agent patches and library overrides marked not in version 1; the question timeout and `reminder_interval` named |
| `docs/02` | Workers: the orchestration worker hosts every action and sessions, the agent worker only agent steps, one agent queue (LOG 318; capability pools removed); a wait is a retried read, with no heartbeating poll or merge queue; the bare clone with `git merge-tree`, no partial clones; findings counts, not a capped summary; API routes listed; startup retry and platform status planned (R4); library `inputs` removed |
| `docs/03` | `kind` is `template` or `fragment`; `ratifies` on decisions only; `planning` loads and gives the agent nothing; parameter type `reference`; `consumes.type`; `level` removed from the `run.child` example; library `inputs` removed from the value precedence (LOG 434); patches' sources in version 1 |
| `docs/04` | Merge queue claim deleted; `inspect_plan`, `propose_patch` and `run_check` removed, and s6.1 says once that an agent cannot propose a patch; `plan.expand` reads a list artifact only, and the core builds the patch; `wait.condition`'s `poll_every`, default `timeout`, `checks: passed`, and no manual end while it runs; `test.integration` starts no services; SonarCloud planned (R3); `artifact.import`, `artifact.publish`, `scm.revert` inputs and the outputs of `repo.inspect`, `tracker.update`, `delivery.report`; carried test results by head and tiers; step-start check 31 covers engine-handled actions only. `run_gate` (F14) was already refused at load; reminders (F14) are built as described |
| `docs/05` | Unlocked caps (F14): an unlocked `auto: false` stops auto-resolution and run start warns; only a locked rule refuses the template. F5: agents hold no code-host credential; F6: the model proxy is the only mode; s6's agent-proposal rules prefaced by "not in version 1"; toolsets row shortened to a reference |
| `docs/06` | Capability flags read only at run start (reading before a call is R1, F16); per-type timeouts; contract suites and fakes as built; `ICodeHostProvider` lists built methods, the merge-queue members marked unread; SonarCloud and SARIF findings sources removed |
| `docs/07` | The installation, organisation and repository examples load through the real readers with no diagnostics, apart from settings marked planned (R3): `code_host.github`, `credential_broker.github_app`, `session_surface` kind `local`, `secrets` kind `file`, `defaults.limits`, a team's `repositories` corrected; the code-host identity startup check removed (the guarantee is by construction); the run request's members; the repository `library` section checked and not used; rows added for settings the code reads and the table lacked |
| `docs/08` | Manifest keys read but not used (`inputs`, `resources`, `notifications`, version constraints, a role's `requires`); repository layer not applied; `ff library init` removed; std's evidence types, parameters, fragments and templates as built |
| `docs/09` | Checks 21 to 23 by phase; test types by CI category from `ci.yml`, with Architecture, Deployment, Console and Live rows; `propose_patch` and `inspect_plan` removed; the golden sample and the workflow-test example taken from the repository |
| `docs/10` | S1 and Q9 (capability queues, F14): changed by LOG 318; Q2, Q7, Q8, R4, R6, R9 brought up to date; R8 (reminders) kept, as they are built |
| `docs/OUTLINE.md` | Marked historical; unbuilt facilities marked not in version 1 |
| `CONTRIBUTING.md` | Where reviews, results notes and spikes live; the repository layout |
| `deploy/README.md` | Temporal Cloud planned (R3) |
| Code | `artifact.import` drops its unread `record` input and needs `code_host`, which it reads through, rather than `vcs` (`src/FeatureFlow.Model/Actions.cs`; LOG 482) |

The "planned" markers `docs/12` added are each confirmed: `docs/00` and `docs/07` (R2, OIDC sign-in), `docs/07` (R3,
the enterprise Temporal settings and TeamCity). A6 added R1, R3 and R4 markers only where those milestones' text
covers the mechanism.

## Checks

| Done when | Check |
|---|---|
| A reviewer reading each owning document against the code finds no described mechanism that does not exist, other than ones marked planned with a milestone | The independent review (below) sampled claims across every document against the code; what it found was fixed. The sub-orchestrator checked six more: `artifact.import` reads through `run.CodeHost.ReadFileAsync` (`Artifacts.cs:92`); an unlocked `auto` only warns (`RunStartChecks.cs:202-205`); one agent queue (`TaskQueues.Agent`, `EngineServices.cs:17-23`); the sandbox's MCP server serves `report`, `ask_human` and `checkpoint` only (`McpServer.cs:94-141`); only `sarif` analyses (`Testing.cs:214`); `code_host.github` takes `api_base` and `webhook_secret_ref` (`GitHubProviders.cs:12`) |
| Findings listed in the results note | This note: per document above, deferred below |

## Verification

| Suite | Result |
|---|---|
| Default (`Category!=Integration&Category!=Live&Category!=Cluster`) | 1,775 passed, 0 failed, 25 projects, before and after A6 |

The `record` removal is covered by the validator's refusal of unknown action inputs (`PlanValidator.CheckInputs`); no
test, template or test library set `record` or relied on the old needs. No test reads a design document directly;
the editor of `docs/07` loaded its three examples through the configuration readers in a scratch harness.

## Deviations and judgement calls

LOG 472 to 486, unreviewed. Four need a look: LOG 472 (library `inputs`: refuse or apply), 473 (keys read and not
used: refuse or build), 474 (agent planning authority: build or remove the unreachable paths), 483 (step-start check
31 for `run.child` and `plan.expand`). The suggested worker shape (one editor) was changed to four editors on
disjoint documents, since about 120 findings would not fit one session's context, and disjoint files cannot conflict.

## Follow-ups

Deferred, not built in A6. None has a `docs/12` milestone unless the table says so.

| # | Follow-up | Code | Milestone |
|---|---|---|---|
| 1 | Library `inputs` (LOG 434, 472): refuse the key or apply it, and mark supplied parameters in `GET /templates` | `LibraryItems.cs`; `library/std/library.yaml` | None |
| 2 | Keys read and not used: a manifest's `resources`, `notifications`, version constraints, a role's `requires`, a repository's `library.default_template` and `library.adds` (LOG 473) | `LibraryItems.cs`, `LibraryValidator.cs`, `RunStartChecks.RepositoryLibrary` | None |
| 3 | Agent planning authority: `inspect_plan`, `propose_patch`, an agent's library override, a child's patch to its parent, `planning.templates` enforcement (LOG 264, 474) | `SandboxClient/McpServer.cs`; `Patches.cs`; `PlanValidator.cs` | None |
| 4 | Merge queue: enqueue and wait, or remove `RequiresMergeQueue` and `MergeQueue` | `CodeHost.cs`; `GitHubCodeHost.cs` | R1 (F16, the flags) |
| 5 | Capability task queues and agent pools; a Windows sandbox | `TaskQueues.Agent` | None |
| 6 | `plan.expand` from a parameter list | `Interpreter/Artifacts.cs` `ExpandList` | None |
| 7 | `test.integration` starting services from `integration_environment` (LOG 120) | `Testing.cs` | None |
| 8 | Step-start check 31 for `run.child` and `plan.expand` (LOG 483) | `RunWorkflow.cs` `ChildSpec`; `PlanExpansion.Build` | None |
| 9 | `wait.condition`'s needs derived from its condition (a tracker condition needs a tracker, not a code host) | `Actions.cs` | None |
| 10 | Descriptor outputs for `repo.inspect`, `tracker.update` and `delivery.report`, so check 5 types them | `Actions.cs` | None |
| 11 | Checks 21 and 23, auto-resolution and budget caps on a patch | `Patches.cs` | None |
| 12 | `per_run_key` model access (F6's second mode) | `ClaudeCodeRuntime.cs`, `CopilotCliRuntime.cs` | None |
| 13 | A cloud secrets provider; contract suites for secrets and the credential broker | `Fakes.cs`; `Providers.Contracts` | None |
| 14 | `planning.require_review`'s `locked`, accepted and read nowhere | `SteeringActivities.cs` | None |
| 15 | Run-request `limits` (per-step attempt overrides) | `Runs.cs` `StartRunRequest` | None |
| 16 | Docker-needing tiers inside a sandbox; the solo profile's privileged container | `DockerSandbox.cs`, `KubernetesSandbox.cs` | None |
| 17 | Partial clones for large repositories | `GitVcs.cs` | None |
| 18 | Pruning the record store's `changes` rows; paging `GET /attention` by the caller's scope | Stores; `ApiApp` | None |
| 19 | `ff library init`, or the user guide saying "copy `library/std`" | `CliApp.cs` | R5 (guide) |
| 20 | A SonarCloud analyser for `analysis.run` | `Testing.cs:214` | R3 |
