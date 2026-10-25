# Design brief: an open-source engine for autonomous software delivery

Status: draft. Date: 2026-10-25.

## 1. What this brief asks for

Design the engine of a new, open-source orchestrator for agent-assisted software delivery. This is a new project with no existing code. The output is design documents and interface definitions, not production code.

In scope:
- Overall architecture and layering.
- How pluggable parts are defined, selected and configured.
- The catalogue of engine steps that custom workflows can use, with their contracts.
- Deployment profiles from a solo developer to an enterprise.

Out of scope for now (later briefs):
- The workflow definition language (syntax of templates, routing rules, re-plan format). Design the engine so a workflow interpreter can sit on top; assume the plan is data.
- Workflow content: templates, agent task prompts, skills.
- Console UI design beyond the API it needs.

Working name in this brief: "the engine". Choose a project name if useful.

## 2. What the product does

A developer starts a run for a ticket, an epic or a larger project. The engine drives it through a workflow of steps: planning, specification, design, implementation, review, testing and merging, across one or more repositories. LLM agents do most of the work inside sandboxes. The engine verifies their work independently and stops for a human only at an explicit decision point or an unexpected condition.

It complements an organisation's existing process. It does not replace the tracker, the code host, CI or human review.

Target adopters range from one developer on a laptop to a large organisation with its own tracker, code host, CI and hosting. A large organisation is expected to fork or extend the project for its own environment, so organisation-specific behaviour must come from configuration and plug-ins, not from code changes in the core.

## 3. Lessons from a previous prototype (background only)

A previous prototype (a local TypeScript process, Git-backed state, agents in Docker, one tracker and one code host) delivered a real production feature across 3 repositories: 2 epics, 16 tickets, about 27 hours. It completed the build but showed these problems:

| Problem | What happened | Design implication |
|---|---|---|
| Engine fragility | The orchestrator hung when the code host was unreachable (git had no timeout); it stalled after every step; network errors were not classified as infrastructure failures; long commands hit idle timeouts; a laptop sleeping or losing network stopped runs | Use a durable execution platform; timeouts, heartbeats and error classification on every external call; run on a server, not a laptop |
| Agent environment did not match CI | Agent containers lacked Docker and some tools; repository hooks were skipped; a test deadlocked only on CI runners; lint and static analysis did not run until the final PR | Sandboxes must be configurable per repository and able to run the same checks as CI, or delegate to CI |
| Review findings piled up at the end | An AI code review on the final PRs into main raised 11 real findings: 4 missed by per-ticket review, 3 introduced by fixes for earlier review comments (the re-review then reported no issues), 3 only visible across tickets (no epic-level review ran), 1 re-raised after it had been declined as a known limitation | Reviews at every scope (ticket, epic, delivery); a findings ledger for the whole run; fixes verified against the original finding |
| Unstable LLM gates | An LLM spec check gave different verdicts on unchanged text and used up its attempts | LLM findings must be structured, fingerprinted and carried forward between passes |
| A supervising agent was needed | A separate supervising agent used scripts and manual overrides to keep runs moving and to patch workflow content mid-run | Mid-run steering (amend a step, override workflow content, grant extra attempts) must be a first-class, audited engine capability |
| Settings conflicted | Separate settings for repository merge gates, review mode, mandatory steps and auto-merge interacted and stopped the run | One clear policy model with predictable precedence |

What worked and must be kept:
- Routing on failure: back to implementation, or back to planning (re-plan).
- Re-planning mid-run: inserting or changing steps while the run continues.
- Traceability from requirements to acceptance criteria to tests.
- Independent verification of agent work.
- Per-repository manifests declaring test tiers, setup commands and static analysis.

## 4. Decisions already made

| # | Decision |
|---|---|
| D1 | Re-planning mid-run (insert or change steps) is essential. Discovery during implementation is normal. |
| D2 | Generic, open-source product. No assumptions about a particular tracker, code host, CI or cloud. |
| D3 | Temporal provides durable execution. The engine has one Temporal workflow that interprets plans: the plan is data held in workflow state; a re-plan is an activity that returns a new plan. The engine builds the interpreter, the providers and the API. |
| D4 | The same code runs in every deployment profile; only configuration changes (section 7). |
| D5 | Implementation language is .NET or Python. The first enterprise adopter supports only these Temporal SDKs. Leaning .NET. Not decided: see Q1. |
| D6 | How tests that need Docker run (inside the sandbox, or by triggering a CI job) is pluggable. |
| D7 | Two kinds of worker: orchestration workers (stateless; plan interpreter and light activities) and agent workers (run agent steps inside sandboxes created by a pluggable provider). |
| D8 | Merge bar: no unresolved blocking finding, and every finding is either fixed or has a recorded decline reason. Not "zero findings". |

## 5. Target architecture (starting point; refine it)

| Layer | Responsibility | Delivery-specific? |
|---|---|---|
| 1. Durable execution | Temporal: event history, retries, timeouts, heartbeats, signals and updates, timers, child workflows, task queues | No |
| 2. Workflow runtime | One interpreter workflow: holds the plan as data, picks the next step, runs it as an activity or child workflow, applies the outcome's route, waits for human decisions, accepts re-plans and steering | No, but agent-aware |
| 3. Workflow content | Templates, agent tasks, skills, check definitions | Yes (out of scope here) |
| 4. Providers | Implementations of the pluggable interfaces in section 6 | Each knows its own system |

Plus:
- An API service to start, steer, decide on and query runs. A console and CLI use only this API.
- An artifact and audit store outside Temporal. Temporal deletes history after a retention period, and payloads must stay small.

Temporal-specific requirements:
- Workflow code must be deterministic. All I/O in activities.
- Keep payloads small: pass references to artifacts, never transcripts, code or secrets. Respect Temporal history limits; use continue-as-new for long runs. A 27-hour run with hundreds of steps must fit.
- Version the interpreter so workflow content and plan changes need no workflow code change, and interpreter changes are replay-safe.
- Design for Temporal being unavailable: workers wait and resume; the API and console report it clearly.
- Support a pluggable payload codec (some organisations require encryption of payloads).

## 6. Pluggable parts

| Part | Examples to support (first; later) | Notes |
|---|---|---|
| Temporal platform | `temporal server start-dev`, Docker Compose, self-hosted on Kubernetes, Temporal Cloud | Connection config only: address, namespace, auth (API key, mTLS), TLS, codec |
| Worker platform | Local Docker; Kubernetes (AWS EKS, Azure AKS) | Where orchestration and agent workers run |
| Sandbox provider | Local Docker, Kubernetes pod; later hosted sandbox services | Creates the environment for one agent step: checkout, toolchain, credentials, network policy, optional Docker. Evaluate building it from the repository's devcontainer.json |
| Tracker | Jira, GitHub Issues; later Azure Boards, Linear | Issues and epics, transitions, links, comments |
| Code host (VCS) | GitHub; later GitLab, Azure Repos, Bitbucket | Branches, PRs, review comments and replies, merge, check status |
| CI | GitHub Actions, TeamCity; later others | Trigger, wait for and read results; may be served by the code host's checks API |
| Agent runtime | Claude Agent SDK, GitHub Copilot SDK; later others | Run an agent with a task, tools and limits; stream heartbeats; return structured output |
| Review provider | GitHub Copilot code review, the engine's own reviewer agent, static analysis (SonarCloud, SARIF) | Produce structured findings for the findings ledger |
| Artifact and audit store | Git repository, S3 or Azure Blob, Postgres | Plans, specs, findings ledger, transcripts, decision log |
| Secrets | Environment or file (solo), AWS Secrets Manager, Azure Key Vault, Kubernetes secrets | Referenced from config, never stored in it |
| Notifications | Slack, Microsoft Teams, email | Human decision needed, run finished, run stuck |

Requirements for the plug-in model:
- Each part is an interface with a capability declaration (for example, a code host that cannot request an AI review says so). Engine steps check capabilities before use and fail with a clear message; they never assume success.
- Providers are selected by name in configuration. Third parties, including a forking organisation, can add providers without changing the core.
- Every provider call has a timeout and classifies errors as infrastructure (retry with backoff; does not use a work attempt) or work (route by outcome).
- Contract tests per interface, so a new provider can prove it conforms.

## 7. Configuration

Requirements:
- Layered configuration with defined precedence: installation, organisation or team, repository, run. Define what each layer may set.
- Repository-level settings live in the repository: test tiers, setup commands, static analysis, sandbox definition, CI mapping.
- Secrets by reference only.
- One policy model for gates and merging: who must approve what, at which scope (ticket, epic, delivery into the default branch). A merge into a repository's default branch requires a human by default.
- Validation with clear errors at startup and when a run starts.

Deliverable: a configuration schema and three worked example profiles:

| Profile | Temporal | Workers and sandboxes | Tracker / code host / CI |
|---|---|---|---|
| Solo developer | `temporal server start-dev` or one Docker container | Local Docker | GitHub Issues, GitHub, GitHub Actions |
| Small company | Docker Compose or Helm on own cloud, or Temporal Cloud | Kubernetes (EKS or AKS) | Jira, GitHub, GitHub Actions |
| Enterprise (see section 9) | Temporal Cloud, organisation-managed namespaces | Organisation's Kubernetes | Jira, GitHub, GitHub Actions and TeamCity |

## 8. Engine steps

An engine step is executed by the engine itself, not by an LLM agent, though some steps start agents. Workflows compose these steps; the workflow language is designed later, so define each step as a contract.

Each step definition must state:
- Inputs and required capabilities (which providers it needs).
- Outcomes: named results the workflow routes on.
- Idempotency: how an interrupted step detects what it already did.
- Timeouts, heartbeats and error classification.
- Artifacts consumed and produced.
- Scope where relevant: ticket, epic or delivery.

The catalogue must cover at least these capabilities. Group, split or rename them as the design needs.

| Area | Capability |
|---|---|
| Agents | Run an agent with a role and task in a sandbox; produce artifacts. Roles differ in tools and write access (for example, a reviewer cannot change the repository) |
| Humans | Wait for a decision at a gate; host an interactive session (for example, spec or design discussion); automatic approval where policy allows |
| Composition | Start a child run (epic -> tickets) and wait for it; propagate its outcome, including a request to re-plan the parent |
| Re-planning | An agent proposes plan changes; validate them; the interpreter applies them while the run continues; protected steps cannot be removed |
| Steering | Amend a step, override workflow content, grant extra attempts, abort. Audited, through the API |
| Source control | Create branches; merge the integration base forward into a run's branch; open and update PRs; merge with a chosen method (merge, squash) |
| CI | Wait for every check on a PR, including checks that register late; trigger a CI job for a test tier and read its results |
| Testing | Run a repository's declared test tiers in the sandbox; run several repositories together as services for integration tests |
| Static analysis | Fail a change that adds issues, using the analyser the repository declares |
| Review | Review a diff at any scope (ticket, epic, delivery) with any review provider; write structured findings to the ledger |
| Findings ledger | Fingerprint findings; triage each once (fix, decline with reason, escalate); post the decline reason on the PR; reopen only when the code it refers to changes; evaluate merge readiness (D8) |
| Fix verification | A fix for a finding must add a regression test and be checked against the original finding on the full diff |
| Waiting | Wait until a PR, check or tracker issue reaches a state |
| Artifacts | Bundle, validate, import and publish artifacts (specs, plans, verification plans) |
| Multi-repository delivery | Deploy order across repositories; deploy gates; report which repositories delivered |
| Tracker | Create and update tickets and epics; transition status; link PRs |

## 9. Example enterprise constraints

The first enterprise adopter will fork the project. Its environment is a useful test of the design:
- Temporal Cloud, single region, 99.9% availability SLA. Workers must tolerate Temporal being unavailable.
- One namespace per environment (test, uat, prod), with immutable names. Auth by API key issued by an internal platform; OIDC planned.
- Only the .NET and Python Temporal SDKs are supported.
- All workers run as Kubernetes deployments.
- The application team owns workers, activity idempotency, workflow versioning and worker observability. A platform team owns retention, search attributes, a shared payload codec and dashboards.
- Several CI systems in use (GitHub Actions, TeamCity).
- Some repositories are Windows-only (.NET Framework) and cannot build in Linux containers.

## 10. Open questions to analyse (recommend, do not decide)

| # | Question |
|---|---|
| Q1 | .NET or Python: Temporal SDK maturity, agent SDK support (Claude Agent SDK, GitHub Copilot SDK), suitability for AI-assisted development, enterprise robustness, appeal to open-source contributors |
| Q2 | Running Docker-dependent tests from sandboxes on Kubernetes: privileged pods, sysbox, a remote Docker host, or delegate to CI. Security trade-offs |
| Q3 | Sandbox definition: devcontainer.json, the engine's own repository manifest, or both |
| Q4 | Artifact and audit store: Git, object storage, a database, or a combination |
| Q5 | How human decisions and steering reach the workflow (Temporal signals or updates), and how the API authenticates and authorises them |
| Q6 | Plan-as-data within Temporal limits: history size, continue-as-new, how re-plans are versioned and audited |
| Q7 | Provider packaging: in-process packages, out-of-process plug-ins, or both. Must suit an organisation maintaining a fork |
| Q8 | Identity: which identity agents and the engine use against the tracker and code host (user, bot, GitHub App), per profile |
| Q9 | Windows-only repositories: can a sandbox provider support Windows hosts? |

## 11. Deliverables

1. Architecture document: layers, components, worker types, and the data flow for one ticket run and for an epic with a mid-run re-plan.
2. Interface definitions for every pluggable part, with capability declarations and error classification.
3. Configuration schema, precedence rules, policy model, and the three example profiles.
4. Engine step catalogue with full contracts.
5. One decision record per open question, with options and a recommendation.
6. Risks and a proposed spike plan, including an interpreter workflow that survives a worker crash and applies a mid-run re-plan.

Writing style for all deliverables: plain technical English, concise, tables and lists over prose, no unexplained references or abbreviations.

