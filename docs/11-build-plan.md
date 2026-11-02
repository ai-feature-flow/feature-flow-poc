# Version 1 build plan

Status: draft for review, 2026-11-01.

This document owns how version 1 of Feature Flow is built: where the code
lives, the solution layout, the order in which components are built, the
milestones and how each is verified, and what is taken from spike code. What
the engine does is owned by `docs/01` to `docs/10`; this document cites them
and does not restate them. Judgement calls made here are LOG 66 to 70,
approved 2026-11-01.

---

## 1. Scope

Version 1 is everything `docs/01` to `docs/10` place in version 1, in the three
deployment profiles of `docs/07` section 6 (solo developer, small company,
enterprise). It excludes what those documents exclude, chiefly the items in
`docs/06` section 5 (Windows sandbox, deployment provider, remote provider
adapter, VCS systems other than Git) and cost budgets (`docs/02` section 8).

The plan builds the **solo profile end to end first** (LOG 69): a local Docker
sandbox, SQLite, the local filesystem, a directory library, GitHub and Claude
Code. The spikes ran on a local Temporal server, GitHub and Claude Code, so the
first real run needs no new infrastructure. The small-company and enterprise providers come last; the
provider interfaces (`docs/06`) make them additions, not changes.

---

## 2. Where the code lives

### 2.1 Repository

This repository, `ai-feature-flow/feature-flow-poc`, with the code under
`src/`, `hosts/` and `tests/` beside `docs/` and `library/` (product owner,
2026-11-01).

Reasons: the standard library ships with the engine (`docs/08` section 6) and
is the first input to the code; the design documents are cited by the code's
tests; and a design change and the code change it causes can land in one
commit. The repository can be renamed later without losing history.

`spikes/` stays where it is, unchanged, as evidence (section 6).

### 2.2 Toolchain

| Item | Choice |
|---|---|
| Runtime | .NET 10 (`net10.0`), as every spike used. `global.json` pins the SDK |
| Temporal SDK | `Temporalio`, at the spikes' version (1.21.0) or the latest at M0 |
| Expressions | `Cel.NET` (2.3.1 in Spike 1), restricted as Spike 1 measured (`archive/docs/15` section 9.2) |
| YAML | `YamlDotNet` (16.3.0 in Spike 3), configured to refuse unknown keys (`docs/07` section 4) |
| Tests | xUnit v3. Tests that need Temporal, Docker, a real agent or GitHub are tagged by category, so the default run needs none of them |
| Package versions | Central package management (`Directory.Packages.props`) |
| Build settings | `Directory.Build.props`: nullable enabled, warnings as errors, deterministic builds |

### 2.3 Solution layout

```
FeatureFlow.slnx
global.json, Directory.Build.props, Directory.Packages.props
src/
  FeatureFlow.Model/                    Records for plans, library items, configuration and records; YAML reading
  FeatureFlow.Expressions/              CEL, restricted (docs/03 section 5.2)
  FeatureFlow.Library/                  Sources, manifest, references, fragment and for_each expansion, defaults, the lock
  FeatureFlow.Validation/               Checks 1 to 35 (docs/09 section 2), the effective policy view, the readable form
  FeatureFlow.Interpreter/              The pure core: ready, route, apply, patches, attempt counting. No Temporal reference
  FeatureFlow.Testing.Workflows/        The workflow test file runner (re-drive, docs/09 section 4.3)
  FeatureFlow.Providers.Abstractions/   Provider interfaces, capability records, ProviderResult, error classes (docs/06)
  FeatureFlow.Providers.Contracts/      The contract test suites, shipped with the engine (docs/06 section 1)
  FeatureFlow.Engine/                   The Temporal workflow around the core; activities; the 21 actions and the agent step; the provider decorator; policy evaluation
  FeatureFlow.Engine.Agent/             Agent worker services: hook endpoint, engine MCP server, model proxy, per-step tokens, commit detection
  FeatureFlow.SandboxClient/            The executable copied into each sandbox: stdio MCP server and hook command (LOG 67)
  FeatureFlow.Api/                      The API service: authentication, authorisation, audit, inbound gateway
  FeatureFlow.Hosting/                  Composition: selects and registers providers from installation configuration
  FeatureFlow.Cli/                      `ff`
  Providers/
    FeatureFlow.Providers.Local/        Filesystem artifact store, SQLite record store, file secrets, local hand-off session surface, recording providers
    FeatureFlow.Providers.Git/          VCS provider and the engine's clone cache (docs/02 section 6)
    FeatureFlow.Providers.GitHub/       Code host, GitHub Actions CI, GitHub Issues tracker, GitHub App credential broker, Copilot review findings source
    FeatureFlow.Providers.Docker/       Local Docker sandbox
    FeatureFlow.Providers.ClaudeCode/   Agent runtime
    FeatureFlow.Providers.CopilotCli/   Agent runtime
    FeatureFlow.Providers.StdValidators/  The standard validators (docs/04 section 10.2)
    FeatureFlow.Providers.Slack/, .Jira/, .Postgres/, .Kubernetes/, an object store provider (open question 4)
hosts/
  FeatureFlow.Host.Api/, FeatureFlow.Host.OrchestrationWorker/, FeatureFlow.Host.AgentWorker/
tests/
  <Project>.Tests/                      One per src project
  FeatureFlow.Replay.Tests/             Recorded Temporal histories, in tests/histories/
  FeatureFlow.Integration.Tests/        Temporal dev server and Docker; failure injection
  FeatureFlow.Live.Tests/               A real agent runtime and the GitHub fixture repository
library/std/                            Unchanged; its workflow tests in library/std/tests/
```

Reference rules, checked by a test that reads the project files:

| Project | May reference |
|---|---|
| `Model`, `Expressions`, `Library`, `Validation`, `Interpreter` | Each other, in that order. No Temporal, no providers, no I/O except reading library files |
| `Providers.*` | `Providers.Abstractions` and `Model` only |
| `Engine`, `Engine.Agent`, `Api` | The core, `Providers.Abstractions`, Temporal |
| `hosts/*` | `Hosting` and the provider projects they ship |
| `Testing.Workflows` | The core only. No Temporal: re-drive runs without it (LOG 66). Added in M2, LOG 80 |

The `hosts/` projects are the images the project ships. An organisation's
distribution repository (`docs/06` section 4) writes its own host projects
against `Hosting` and adds its providers.

The pure core is a project of its own (LOG 66). `docs/02` section 2.1 already
says everything outside `await` is a pure function of plan and state; making it
a separate project with no Temporal reference enforces that by the compiler,
and lets re-drive tests and library CI run without a Temporal server. The
Temporal workflow in `Engine` is a thin loop that calls the core and awaits.

---

## 3. Build order

| # | Component | Why it comes here |
|---|---|---|
| 1 | Model, expressions, library loading, load-phase validation | Needs nothing else. `library/std/` is a real input waiting for it: 3 templates, 9 fragments, never loaded. It settles the formats of LOG 62 to 65 before anything is built on them |
| 2 | The interpreter's pure core and the re-drive runner | Depends only on 1. Lets std's routing be tested before any infrastructure exists |
| 3 | The Temporal shell, local stores, the API and CLI, with fake providers | The smallest runnable engine. Brings in the requirements SP1 found (DR14, DR15) |
| 4 | The agent worker: sandbox, runtime, sandbox client, hook endpoint, engine MCP server, model proxy, credential broker | The riskiest real integration, and SP2, SP3 and SP5 give it ready-made tests |
| 5 | Source control, tests and CI: Git, GitHub, GitHub Actions, the `scm`, `test` and `ci` actions | Completes the first real ticket run |
| 6 | Findings, validators, child runs, delivery | Completes epics and projects |
| 7 | People and policy: organisation configuration, approvals, overrides, sessions, notifications, the console | Needs a working run to steer |
| 8 | Team and enterprise providers, observability, packaging | Additions behind existing interfaces |

Stores come early in their local form (step 3) because the API reads only the
record store (`docs/02` section 5); their hosted forms come in step 8.

---

## 4. Milestones

Each milestone ends with a commit on `main` and a short results note in
`docs/build/`, in the shape of the spike results: what was built, the checks,
what they showed, and any design change with its LOG entry. "Verified" follows
`CONTRIBUTING.md`, "Verify, do not assert": every check named below has been
seen to fail when the thing it guards is broken.

### M0. Skeleton

| | |
|---|---|
| Builds | The solution and projects of section 2.3, empty; build settings; the reference-rule test; CI on GitHub Actions that builds and runs the default and integration test categories on every pull request; the live category excluded |
| Done when | The M0 pull request merges with CI passing; `ff --version` runs |
| Verified by | The CI run. The reference-rule test fails when a forbidden reference (`Interpreter` to `Temporalio`) is added |

### M1. Plan loader and validator

The first slice. Input: `library/std/`.

| | |
|---|---|
| Builds | YAML reading into the model, refusing unknown keys. The `directory` library source, the manifest (`docs/08` section 2), references (`docs/08` section 1.1). Fragment expansion and `for_each` expansion, in the formats of LOG 62 and 65. Route defaults (`docs/03` section 4.3; RW13, list order gives the next step for a `success` outcome). CEL parsing and type-checking with the restrictions of `docs/03` section 5.2. The **load-phase checks**: 1 to 17, 34 and 35 of `docs/09` section 2. The readable form (`docs/09` section 4.4). CLI: `ff validate <library or template>` and `ff show <template>` |
| Done when | Every std template and fragment loads with no blocking error. Every blocking load check has at least one negative test that triggers that check number and no other. The readable forms of the three std templates are golden files |
| Verified by | The negative tests are mutations of std's own templates (remove a route, add an unreachable step, give a reviewer step write access, and so on), so each check is shown to fire on realistic input (LOG 70). A check with no failing mutation is not done |

Errors M1 finds in std are fixed in std, each listed in the results note. A
format that turns out unworkable is a design change with a LOG entry. M1
implements the formats of LOG 62 to 65, approved 2026-11-01.

### M2. Interpreter core and re-drive

| | |
|---|---|
| Builds | The pure core of `docs/02` section 2.1: ready steps, starting on first completion, routing, re-entry clearing a step's current outcome (DR12), attempt allowances and run ceilings checked on every execution (F4), the race allowance for `base_moved` (LOG 61), `incomplete` without an end route (F9), patches with checks 3, 17 and 25 to 30, `plan.expand` as a deterministic patch. The workflow test file runner (`docs/09` section 4.3) and `ff test`. Std's workflow tests, which its README lists as not yet written |
| Done when | Each std template has re-drive tests for its main path, the fix loop, escalation on exhausted attempts, a `base_moved` race, and (for `project_delivery`) partial delivery. Each run-time rule of `docs/09` section 3 that the core owns has a test |
| Verified by | Removing the allowance check on re-entry makes the fix-loop test run past its ceiling and fail (Spike 3 ran a fix step 213 times this way, `archive/docs/18` section 4). Removing the end-route rule makes the unrouted-failure test report `completed` and fail |

### M3. A runnable engine with fake providers

| | |
|---|---|
| Builds | The Temporal workflow around the core, with its carried state set in the initialiser (DR14) and the continue-as-new rule of `docs/02` section 3: drain activities, wait for running handlers (DR15), children with the abandon parent-close policy signalling the parent's workflow id. Decisions as Updates. `run.child`. Local providers: filesystem artifact store, SQLite record store, file secrets, recording providers for `dry_run`. Run-start checks 18 to 22 against configured providers. The API with a local token: start, query, answer, abort. The CLI over the API. The agent step backed by a scripted fake runtime |
| Done when | A std `ticket_delivery` run completes on a Temporal dev server with fake providers, through the CLI. An epic with children runs across several continues-as-new |
| Verified by | SP1's scenario rewritten as an integration test against this interpreter: children ending and answers arriving during hand-overs, none lost. The SP1 negative controls (signalling a run id; the default parent-close policy) make it fail. Recorded histories from these runs are committed and replayed in CI; removing a version guard from a changed path makes replay fail, as in Spike 3 |

### M4. Agent steps in a sandbox

| | |
|---|---|
| Builds | The agent worker: the Docker sandbox provider; the Claude Code runtime provider; the sandbox client (LOG 67); the hook endpoint and engine MCP server on the worker (RW17), any replica serving any step from the signed per-step token; the model proxy on the worker (LOG 68); the GitHub App credential broker; commit detection and bundle push to the hidden reference; resume by re-attach. The agent step's lifecycle (`docs/04` section 3.1) with correction turns |
| Requirements met | DR16: the runtime's MCP tool timeout exceeds `questions.timeout`, and the hook command's timeout exceeds the client's hold. DR17: the engine creates the step's output folder before the agent starts. DR19: the sandbox gives the runtime a home directory and shell startup files the engine controls. DR20: the model proxy and every code-host client use the platform trust store. LOG 59 and 60: the sandbox client owns question ids and never returns an infrastructure error to the agent |
| Done when | An implementer step on the fixture repository (`ai-feature-flow/feature-flow-example`) commits, and the commit appears on the hidden reference, then on the work branch |
| Verified by | SP2 as a test: a question answered after the agent worker is restarted. SP3's tasks as contract tests of the runtime provider: report, correct, fall back to a file, wait, stop. SP5's checks: no credential in the sandbox's environment, home or transcript, and with the proxy unreachable the step stops resumable rather than succeeding. A worker killed mid-step resumes from the same sandbox |

### M5. Source control, testing and the first real ticket

| | |
|---|---|
| Builds | The Git VCS provider with the engine's clone cache and expected-revision pushes, reusing connections and checking whether a refused merge already landed (DR18). The GitHub code host provider (every documented success mapped, including the 204 for an empty merge), the GitHub Actions CI provider with `GetExpectedRunsAsync`. `scm.catch_up`, `scm.pull_request`, `scm.merge` (F1 re-read at merge time, action requests), `scm.revert`, `ci.checks`, `wait.condition`, `test.run` (TRX parsing, tiers, CI delegation), `test.integration`, `analysis.run` (SARIF), `repo.inspect`. Run-start checks 23 and 24, and step-start checks 31 to 33 |
| Done when | `ticket_delivery` runs on the fixture repository from start to a merge into a parent branch, with a real implementer, reviewer and test evidence. The contract suites pass for the Git, GitHub and GitHub Actions providers |
| Verified by | SP4's race scenario as an integration test of the engine's own merge path: no lost merge, nothing untested lands. SP4's control N1b (a plain forced push instead of the expected-revision push, natural timing) is run once and loses merges, showing the test can fail. A standalone ticket with `engine_can_merge: true` into the default branch is refused at run start (F1) |

### M6. Findings, validators and levels

| | |
|---|---|
| Builds | `findings.record` and `findings.triage`, the ledger and the `findings` namespace; the standard validator provider (seven validators) and `artifact.validate`; engine-only evidence types (F7); `for_each` over repositories; `delivery.report`; `artifact.compose`, `artifact.import`, `artifact.publish`; `tracker.update` and declarative tracker updates with the GitHub Issues tracker; the work-item budget (F11) |
| Done when | `epic_delivery` with three tickets runs on the fixture repository to a merge into its parent branch. `project_delivery` over two repositories delivers in order, with a person's merge into the default branch detected |
| Verified by | Each validator has a failing and a passing fixture. An agent step declaring an evidence type is refused at load (check 12). A ticket whose fix adds no test is caught by `tests_added` |

### M7. People, policy and steering

| | |
|---|---|
| Builds | Organisation configuration and its validation (`docs/07` sections 3 and 7). Approval rules, people roles, auto-resolution within caps. Every run-time override key (LOG 55), with the actor from authentication (F8). Agent patches touching protected content becoming decisions (F3). Mid-run library override. Breakpoints. Interactive sessions with local hand-off and pre-submit parity. Inbound webhooks. Notifications through Slack, with chat answers through the inbound gateway. The effective policy view. OIDC authentication. A minimal web console: runs, a run's readable form and timeline, and decisions to answer (open question 5). Anything more is later |
| Done when | Every override has a test showing who may and may not use it. A person runs a project's requirements session on their machine and submits |
| Verified by | A request asserting a different actor in its body is recorded with the authenticated actor. A pre-submit check and the later step receive the same task, tools, model and commit (`docs/09` section 4.2) |

### M8. Team profile and operations

| | |
|---|---|
| Builds | Postgres record store; an object store artifact store; Kubernetes pod sandbox; the Copilot CLI runtime; Jira tracker; the `git` library source; the payload codec; OpenTelemetry; stuck-run detection; the reconcile job. Container images for the three hosts; a distribution repository template; deployment notes |
| Done when | The M6 epic runs on the small-company profile in a cluster. Every test type of `docs/09` section 4.2 runs in CI |
| Verified by | The contract suites for every new provider. SP5's checks for Copilot CLI, which SP5 did not test (`docs/spikes/sp5-results.md`) |

Version 1 is complete when M8 is done.

### 4.1 Where tests run

| Category | Needs | Runs |
|---|---|---|
| Default | Nothing | Every pull request, required |
| Integration | Temporal CLI dev server, Docker | Every pull request, required |
| Live | A model credential and the GitHub App's key | Locally at first. When CI may hold those secrets, in a separate CI workflow that is optional or scheduled, so a missing secret never fails a pull request (open question 2) |

Development moves to a Windows machine soon. The Docker sandbox there uses
Linux containers. Running the M4 agent-step tests with Claude Code on that
machine tests in passing what SP6 (session resume on Windows, shelved) would
have.

---

## 5. Design requirements from the spikes

| Requirement | Milestone |
|---|---|
| DR14: the interpreter sets its carried state in the workflow initialiser, because Temporal can deliver signals and updates before the run method starts | M3 |
| DR15: a hand-over waits for running signal and update handlers before it continues-as-new | M3 |
| DR16: the runtime's MCP tool timeout exceeds `questions.timeout`; the hook command's timeout exceeds the client's hold | M4 |
| DR17: the engine creates the step's output folder before the agent starts | M4 |
| DR18: engine-side VCS operations reuse connections, retry connection failures as infrastructure, and check whether a refused merge already landed | M5 |
| DR19: the sandbox gives the runtime a home directory and shell startup files the engine controls | M4 |
| DR20: components that call model or code-host APIs use the platform trust store | M4 |

---

## 6. What carries over from spike code

Spike code is throwaway by convention (`CONTRIBUTING.md`, "Spikes"). Nothing in
`src/` references `spikes/`. Where spike code is useful, the relevant part is
copied into `src/`, rewritten to the version 1 interfaces and naming, and given
tests before it is relied on. The spikes stay unchanged as evidence.

| Spike code | Carries over | How |
|---|---|---|
| `spikes/01-interpreter`: interpreter, plan, patches | The approach only | The plan schema and continue-as-new rule changed. Rewritten in M2 and M3 |
| `spikes/01-interpreter/src/CelChecks` | The CEL restriction settings and the four checks (restriction, determinism, cost estimate, cost limit) | Ported as `Expressions` tests in M1 |
| `spikes/01-interpreter`: crash, outage and history-capture harnesses | The test methods | Rewritten as M3 integration tests |
| `spikes/01-interpreter/tests/histories` | Nothing | They record the spike's workflow, not version 1's. M3 records new ones |
| `spikes/02-real-run`: GitHub App client and credential broker | Token minting and refusing wider scopes | Ported in M4 |
| `spikes/02-real-run`: GitHub code host provider | Response mapping, including the 204 | Ported in M5 to the new interface, without `CreateBranchAsync` (`docs/06` section 2) |
| `spikes/02-real-run`: GitHub Actions CI provider | Triggering and polling | Ported in M5; `GetExpectedRunsAsync` is new |
| `spikes/02-real-run`: Claude Code and Copilot CLI runtime providers | Command lines, output parsing, usage | Ported in M4 and M8; reporting moves to the MCP tools (SP3) |
| `spikes/02-real-run`: plan loader, findings; `spikes/03-concurrent`: plan YAML | Nothing | The language and the ledger changed |
| `spikes/03-concurrent`: pre-submit | The parity method | Reference for M7 |
| `spikes/04-continue-as-new` | The scenario and its controls | Rewritten as the M3 integration test |
| `spikes/05-ask-human`: replica | Endpoint shapes (open a question, long-poll for its answer) | Reference for the M4 agent worker |
| `spikes/05-ask-human`, `spikes/06-reporting`: Python sandbox and hook clients | Behaviour: retry across replicas, hold then stop, contract checks, correction turns | Rewritten in .NET in M4 (LOG 67) |
| `spikes/07-lock-free-merge` | Merge-commit construction and the expected-revision push; the scenario | Reference for the M5 VCS provider; the scenario becomes its test |
| `spikes/08-model-proxy` | Header handling and response streaming | Reference for the M4 model proxy |
| `tools/gh_*.py` | Branch-protection inspection | Kept as tools, for setting up the fixture repository |

---

## 7. Judgement calls

Recorded in `docs/LOG.md`; approved by the product owner, 2026-11-01.

| LOG | Call |
|---|---|
| 66 | The interpreter's pure core is a project with no Temporal reference; re-drive tests run it without Temporal |
| 67 | The sandbox client and hook command are one .NET executable, published self-contained per operating system, replacing the spikes' Python clients |
| 68 | The model proxy is served by the agent worker, authenticated by the per-step token. The design names no component for it |
| 69 | The solo profile is built end to end first; team and enterprise providers come last |
| 70 | Negative tests for load checks are mutations of std templates, each expected to trigger one check number |

---

## 8. Open questions

| # | Question | Recommendation |
|---|---|---|
| 4 | Which object store and which container platform does the first team deployment use? `docs/07` allows S3 or Azure Blob, and Kubernetes | Build only the one the first adopter needs, in M8. Deferred by the product owner, 2026-11-01, to be revisited |

### Answered

Product owner, 2026-11-01.

| # | Question | Answer |
|---|---|---|
| 1 | Where does the version 1 code live: this repository under `src/`, or a new repository with `docs/` and `library/std/` moved into it? | This repository (section 2.1) |
| 2 | How is code work reviewed and where does CI run? May CI hold a model credential and the GitHub App key for the live tests? | Pull requests with required CI on GitHub Actions from M0. Live tests are a separate category, run locally at first, and enabled in CI when the secrets are available, possibly as an optional or separate workflow (section 4.1) |
| 3 | LOG 62 to 65, the formats chosen while writing std, are unreviewed and M1 implements them. Review before M1 finishes? | Accepted: LOG 62 to 65 approved |
| 5 | Is a web console in version 1? No document designs one | Yes: a minimal console in M7 |
