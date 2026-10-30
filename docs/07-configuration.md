# Configuration schema, precedence and profiles

Status: revised 2026-10-31 for the spike results, the merge design (`docs/20`)
and the code review design (`docs/19`). Deliverable 3 of `brief.md` section 11.

**Scope.** The four configuration layers, what each may set, precedence, the
safety floors, the policy model, secrets and credentials, validation, and three
worked deployment profiles.

**Companions.** `docs/01` section 5 (configuration) holds the architecture
decisions this document turns into a schema. `docs/09` section 11 (roles) and
`docs/11` (library packaging) cover workflow content, which this document
configures but does not define.

**Naming.** Configuration keys are camelCase; plans and library content are
snake_case (`docs/09` section 1, the naming boundary).

---

## 1. Layers

| Layer | Where | Set by | Applies to |
|---|---|---|---|
| Installation | Deployment config | Operator | One installation |
| Organisation / team | Config service or file | Organisation | One organisation, or a team within it |
| Repository | Root `feature-flow.yaml`, in the repository | Repository maintainers | One repository |
| Run | API at run start | Requester | One run |

**Precedence:** the most specific layer wins (run, then repository, then
organisation, then installation), **except the safety floors in section 2, which
no lower layer may relax** (decision DP6).

A layer may set only what section 1.1 allows. Anything else is a validation
error, never a silent override.

**There is no fifth layer for teams.** Team-specific policy lives in the
organisation file as **team-scoped overrides**: same layer, same file, no extra
precedence step (section 4).

### 1.1 What each layer may set

| Layer | May set | May not set |
|---|---|---|
| Installation | Temporal connection, worker platform, provider selection and instances, credential references, expected retention, payload codec, observability, API auth, defaults | Anything repository-specific |
| Organisation / team | Policy floors, the default-branch merge setting and whether it is locked, provider bindings, role-to-runtime mapping, notification routing, budgets, who may approve and steer, defaults overrides | Repository build and test detail |
| Repository | Test tiers and how to run them, setup commands, static analysis, environment requirements, CI mapping, branch conventions, **the default-branch merge setting, unless the organisation locked it** (read from the default branch only), library additions (`docs/11` section 5) | Policy floors, credentials, role mapping |
| Run | Scope, target repositories, plan overrides, attempt budgets within the ceiling, dry run | Policy floors, the default-branch merge setting, provider selection |

The repository layer is the only one that lives in the repository and travels
with the code. The others are deployment or organisation configuration.

Workflow content (plans and library templates, `docs/09`) is not a
configuration layer, but two kinds of setting also take a workflow value: the
default-branch merge setting (section 2, F1) and limits such as attempt
allowances (section 6).

---

## 2. Safety floors

A **floor** is a value no lower layer may relax. **Any layer may tighten one**,
including a repository tightening a floor for itself. Validation checks that
floors only move in the stricter direction down the layers. The list is short
on purpose. F1 is the exception: it is now a setting with a safe default that
may be relaxed, unless the organisation locks it (below).

Floors exist because the prototype failed with "settings conflicted": separate
settings for merge gates, review mode, mandatory steps and auto-merge interacted
and stopped runs.

| # | Floor | Meaning |
|---|---|---|
| F1 | Default-branch merge setting | Whether the engine may merge into a repository's default branch. **Default: it may not**, so a merge step into the default branch must have `engine_can_merge: false` and a human merges on the code host. No longer a fixed floor; see section 2.1 |
| F2 | Secrets by reference | No layer may inline a secret value; only a reference to the secrets provider |
| F3 | Protected steps | A step marked `protected` in the plan cannot be removed or weakened by a re-plan or by steering |
| F4 | Attempt ceilings | Attempt allowances (set hierarchically, section 6) stay **within** the organisation's ceiling, never above it. Steering may grant more up to a hard ceiling; beyond that needs the admin role. The ceiling is checked **on every execution of a step, including when a loop routes back to it** (DR10) |

Consequences:

- With F1 at its default, no workflow can make the engine merge into the
  default branch; the engine raises a human decision request, a human merges on
  the code host, and the engine detects the merge and continues.
- Neither a repository nor a run can grant itself unlimited attempts (F4).
- A fix loop cannot spin. Spike 3 measured why this matters: with no ceiling on
  re-entry, a loop ran its fix step 213 times in 15 seconds (`docs/18` section 4,
  item 5).

The engine enforces floors, not workflow content. This is the one exception to
the rule that the engine supplies mechanism and workflows supply policy
(`docs/01` section 1.5): no workflow may relax a floor, so a workflow cannot be
the thing that enforces it.

### 2.1 F1: the default-branch merge setting

F1 used to read "a human must approve every merge into the default branch; no
layer may relax it". It is now a configurable setting with the same default.
The ID F1 is kept so references still resolve.

| Rule | Detail |
|---|---|
| Default | The engine may **not** merge into the default branch |
| Levels | Organisation (`policy.merge.defaultBranch.engineMayMerge`), then repository (`merge.defaultBranch.engineMayMerge` in `feature-flow.yaml`), then workflow. The most specific level wins. It may be relaxed, for example for a proof-of-concept repository |
| Lock | The organisation can set `locked: true`, so repositories and workflows cannot relax it |
| Nothing an agent can edit changes it | The repository value is read from `feature-flow.yaml` **on the default branch only**, never from a ticket or integration branch. A plan patch (re-plan) during a run cannot change it. A run cannot set it |
| Enforcement | At plan validation, and again at merge time, reading the merge's base branch and the repository's default branch from the code host, not from the plan (DR2) |
| Final fallback | Code host branch protection on the default branch, requiring independent review. The engine never holds a bypass (DR3, section 9) |

---

## 3. Installation schema

```yaml
apiVersion: feature-flow/v1
kind: Installation

temporal:
  address: temporal.prod.internal:7233
  namespace: feature-flow-prod
  auth: { mode: apiKey, secretRef: secrets/temporal-api-key }
  tls: { enabled: true, caCertRef: secrets/temporal-ca }
  payloadCodec: { name: aes-gcm, keyRef: secrets/payload-key }   # optional
  expectedRetention: 30d      # checked at startup, warned on mismatch; not applied

workerPlatform:
  mode: kubernetes
  orchestration: { queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]
      - queue: feature-flow-agent:os-windows
        capabilities: [os-windows]

# Every provider type may have several named instances, one marked default.
providers:
  sandbox:
    default: kubernetes-pod
    instances:
      kubernetes-pod: { name: kubernetes-pod }
  agentRuntime:
    default: claude-code
    instances:
      # Claude Code speaks only the Anthropic API.
      claude-code:
        name: claude-code
        config: { modelAccess: { mode: proxy, keyRef: secrets/anthropic-key } }
      # Copilot CLI works through an OpenAI-compatible model router.
      copilot-cli:
        name: copilot-cli
        config:
          endpoint: https://llm-router.acme.internal/v1
          modelAccess: { mode: proxy, keyRef: secrets/router-key }
  codeHost:
    default: github
    instances:
      github: { name: github, config: { appId: 12345, secretRef: secrets/github-app-key } }
  ci:
    default: github-actions
    instances:
      github-actions: { name: github-actions }
      teamcity:       { name: teamcity, config: { baseUrl: https://tc.acme.internal, secretRef: secrets/teamcity-token } }
  tracker:
    default: jira
    instances:
      jira: { name: jira, config: { baseUrl: https://acme.atlassian.net, secretRef: secrets/jira-token } }
  review:
    default: copilot-review
    instances:
      copilot-review: { name: copilot-review }
      engine-reviewer: { name: engine-reviewer }
  artifactStore:
    default: azure-blob
    instances:
      azure-blob: { name: azure-blob, config: { container: ff-artifacts } }
  recordStore:
    default: postgres
    instances:
      postgres: { name: postgres, config: { connectionSecretRef: secrets/ff-db } }
  secrets:
    default: azure-keyvault
    instances:
      azure-keyvault: { name: azure-keyvault, config: { vault: acme-kv } }
  notifications:
    default: slack
    instances:
      slack: { name: slack, config: { secretRef: secrets/slack-token } }
  credentialBroker:
    default: github-app
    instances:
      github-app: { name: github-app }

api:
  baseUrl: https://feature-flow.acme.internal
  auth:
    mode: oidc
    issuer: https://idp.acme.internal
    audience: feature-flow
    roleClaim: groups

observability:
  otlpEndpoint: https://otel.acme.internal:4317
  logLevel: info

defaults:
  attempts: { agent.run: 3, plan.replan: 2, findings.triage: 2, scm.merge: 3 }
  timeouts: { agent.run: 4h }
```

Notes:

- **Named instances.** Every provider type may have several named instances,
  one marked `default`. This covers two CI systems, two review providers (an AI
  review on the pull request and the engine's own reviewer agent) and two agent
  runtimes. Other configuration selects an instance by name: the repository's
  `ci.mapping` per test tier (section 5), and the organisation's `roleMapping`
  per agent role (section 4).
- **Providers are selected by name in configuration, not in code** (`docs/01`
  section 4.5, provider packaging). A host project references the provider
  packages it ships.
- **Agent runtimes differ in what they can reach.** Measured in Spike 2 and the
  Claude Code proof (`docs/16` sections 4 and 12):

  | Runtime | Model access | Hooks (the engine's per-tool-call control) |
  |---|---|---|
  | `claude-code` | Anthropic API only: a first-party subscription or key. **Cannot** be pointed at an OpenAI-compatible router; it rejects the model before any request | Session start, before and after each tool call, and stop all fired, on Windows and macOS |
  | `copilot-cli` | Works through an OpenAI-compatible router (`endpoint`) | Session start and stop fired. Before- and after-tool-call hooks did **not** fire with a third-party model; untested with a first-party model |

  So `endpoint` is valid only on a runtime that supports a custom endpoint.
  Both runtimes can stream their tool calls, which the engine uses to record
  what an agent opened when hooks are not available (section 4, review).
- **`modelAccess` keeps model credentials out of the agent's environment**
  (DR1). Version 1 uses one of two modes: `proxy` (a proxy outside the agent's
  sandbox adds the key to each request) or `perRunKey` (a key issued per run,
  with a spending limit and an expiry). Neither mode puts a long-lived key in
  the agent's environment. The proxy's own configuration is not yet designed
  (section 11).
- **The code host identity must not be able to change branch protection**
  (DR3). For GitHub, the App named here has no `administration` permission.
  Protection is configured by a separate identity; the engine only checks it
  exists (section 9).
- **Every credential is a `secretRef`** (F2). No inline secrets.
- **`expectedRetention` is checked, not applied.** Retention is a Temporal
  namespace setting owned by the platform team on managed Temporal. The engine
  warns on a mismatch at startup.
- **Scaling is not engine configuration.** Replica counts belong to the
  deployment (Helm values, a Kubernetes autoscaler, or compose). The engine
  names the task queues and the capabilities each pool serves.
- **Pool sizing rule:** worker slots at least equal to the number of concurrent
  long steps, plus headroom. Spike 1 confirmed it: with 20 concurrent 15-second
  steps on 10 slots, about 10 steps waited roughly one step's length, and peak
  occupancy never exceeded 9 of 10 (`docs/15` section 6.3). The measurement has
  two repetitions, not the three asked for, so the headroom figure is not set.
- **`defaults` are the engine defaults** for limits such as attempt allowances
  and timeouts, to be tuned from recorded data. They are **not floors**: the
  organisation may override them, and so may a workflow, a child workflow and a
  step (section 6), all within the organisation's ceilings.
- **`scm.merge: 3`** is the merge step's attempt allowance. Every attempt
  counts, including a re-entry after the ticket catches up or after a conflict
  is resolved. When it runs out the ticket escalates to a human (section 4,
  merge policy).

---

## 4. Organisation / team schema

```yaml
apiVersion: feature-flow/v1
kind: Organisation
name: acme

policy:
  approval:
    ticket:  { roles: [approver], minCount: 1, agentCounts: false }
    epic:    { roles: [approver], minCount: 1, agentCounts: false }
    defaultBranch: { roles: [approver], minCount: 2, agentCounts: false }
  merge:
    defaultBranch:
      requireChecks: true
      engineMayMerge: false       # F1, the default
      locked: true                # repositories and workflows may not relax it
    integrationBranch: { requireChecks: true }    # epic and project branches
  findings:
    mergeBar: noUnresolvedBlocking
    requireDeclineReason: true
  replan:
    allowedRoles: [planner, implementer, fixer]   # roles an agent-proposed step may use
  attempts:
    ceiling: { agent.run: 5, perRun: 40 }
    hardCeiling: { agent.run: 8, perRun: 60 }     # steering may not exceed without admin
  timeouts:
    heartbeat: 30s                                # how long a step may go silent
    stalledAfter: 5m                              # no successful contact before the run is marked stalled
  steering:
    mayAmendStep: [steerer, admin]
    mayOverrideContent: [admin]
    mayGrantAttempts: [steerer, admin]
    mayAbort: [steerer, admin]
  chat:
    allowedDecisions: [ticket.gate, epic.gate]    # default-branch merge is console only

# Which agent runtime instance and model each library role runs on.
roleMapping:
  planner:     { agentRuntime: claude-code, model: claude-haiku }
  implementer: { agentRuntime: claude-code, model: claude-haiku }
  fixer:       { agentRuntime: claude-code, model: claude-haiku }
  reviewer:    { agentRuntime: claude-code, model: claude-sonnet }   # example only

roles:
  viewer:   { idpGroups: [acme-engineering] }
  approver: { idpGroups: [acme-leads] }
  steerer:  { idpGroups: [acme-leads] }
  admin:    { idpGroups: [acme-platform] }

notifications:
  routing:
    decisionNeeded: { channel: "#ff-approvals" }
    runStuck:       { channel: "#ff-ops" }
    runFinished:    { channel: "#ff-runs" }

# Team-scoped overrides: same layer, same file, no extra precedence step.
teams:
  payments:
    policy:
      approval:
        defaultBranch: { minCount: 3 }        # stricter approval for this team
      attempts:
        ceiling: { agent.run: 4 }
    roles:
      approver: { idpGroups: [acme-payments-leads] }
```

Notes:

- **Roles** (`viewer`, `approver`, `steerer`, `admin`; section 7.2) are mapped
  to identity-provider groups. Scoping a role by team or repository is a
  role-mapping detail (`docs/01` section 5.4, control plane).
- **`chat.allowedDecisions`** lists the decisions that may be made from chat.
  Ticket and epic gates may; a default-branch merge may be made only in the
  console.
- **`attempts.ceiling`** is what a run may set budgets within (F4).
  **`hardCeiling`** is what steering may grant up to; beyond it needs the admin
  role. Both are checked on every execution of a step, including loop re-entry
  (DR10). Spike 3 enforced exactly these values (`agent.run` 5, `perRun` 40).

### Merge policy

- **What the organisation sets:** whether checks are required at each branch
  level, and the default-branch merge setting with its lock (F1, section 2.1).
  `integrationBranch` covers the epic and project branches that tickets and
  epics merge into.
- **What the workflow sets:** each merge step takes `squash` (squash merge or
  plain merge) and `engine_can_merge` (the engine merges, or a human merges on
  the code host while the engine waits) (`docs/06` section 6.5, the merge step).
  Usual values:

  | Merge | `squash` | `engine_can_merge` |
  |---|---|---|
  | Ticket branch into epic branch | true | true |
  | Epic branch into project branch | false (each ticket's commit stays visible) | true |
  | Project branch into the default branch | false | false, required by F1 at its default |

- **There is no merge lock.** Tickets merge the way developers do (`docs/20`,
  the merge design): the engine catches the ticket branch up with the target
  branch, re-runs tests if that changed anything, then merges. Where the engine
  merges, it builds the merge or squash commit from exactly what was tested and
  pushes it; if the target branch moved in the meantime, Git refuses the push
  and the step reports `base_moved`, which routes back to catch-up.
- **The cap on refused merges is the merge step's attempt allowance**
  (`scm.merge`, engine default 3, section 3). Every attempt counts, including
  re-entry after a conflict is resolved. Like every limit it may be overridden
  by the workflow, a child workflow and the step (section 6). When it runs out
  the ticket escalates to a human, so a slow ticket cannot be starved by faster
  siblings. The value 3 is a starting value, not measured.

### Review

Review choices are workflow content, not engine configuration (`docs/19`, the
code review design):

- **Reviewer model.** Using a different, ideally stronger, model for review
  than for authoring is **workflow policy**, set through `roleMapping`. The
  engine does not enforce it, because another model may not be available. The
  engine records which model authored and which reviewed, so the choice is
  visible.
- **Review angles** (correctness, design and structure, tests, security,
  conformance to the specification) are steps in the library's review
  templates.
- **Coverage is engine mechanism, never an engine rule.** For every agent step
  the engine records which files the agent opened, from hooks or from the
  runtime's streamed tool-call output, or "unknown" if neither is available.
  A review step publishes a coverage output (changed files, files read, files
  not read, whether the record is verified). The engine never fails a step for
  coverage; a review step may set `require_coverage` (`none` by default, `all`,
  or a list of paths), or a route may act on the coverage output. The library's
  standard code-review template sets `require_coverage: all`.

### Role mapping

- **`roleMapping`** maps each library role (`docs/09` section 11, roles) to a
  named `agentRuntime` instance (section 3) and a model. A role never names a
  runtime product or model itself, so the same library works in an installation
  that ships a different runtime.
- **Any role may map to any runtime** that offers the capabilities the role's
  `runtime.requires` lists (capability negotiation, decision S3). The reviewer
  can run on either runtime. The mappings in this document are examples.
- **`replan.allowedRoles`** limits which roles an agent-proposed step may use.
  A patch introducing any other role becomes a pending human decision, not a
  rejection (`docs/09` section 11.0).

### Timeouts

- **`timeouts.heartbeat` is an operational setting, not a plan value.** It is how
  long a step may go silent before the engine treats it as interrupted, and it
  dominates crash recovery time: Spike 1 measured 87.4 seconds from a worker
  kill to the step making progress again, almost all of it this timeout plus
  worker restart (`docs/15` section 6.4). Lower detects crashes sooner but risks
  false positives on a slow step. It is set per organisation, not per step,
  because it describes the platform, not the work.
- **`timeouts.stalledAfter`** is how long a run may go without successful
  contact with a provider before it is marked stalled on infrastructure
  (decision D24, the stalled state). It is **time-based, not a retry count**: a
  count took about 8 minutes to reach and depended on two other settings. A
  stalled run alerts and waits; it never fails on its own, and a human may abort
  it. Any successful contact clears the state. Spike 1 observed the transition
  in a shortened test configuration (15 seconds); the 5-minute value is a
  starting value (`docs/15` section 3.9).

### Team overrides

Team overrides live in the organisation file and are selected by team, so
precedence is unchanged. A team override may tighten a floor, never relax it.

---

## 5. `feature-flow.yaml` (repository schema)

One root-level file, authoritative, applying to every execution target. The
engine reads it at each step from the branch, never copying it into the plan
(`docs/01` section 5.1.2).

```yaml
apiVersion: feature-flow/v1
kind: Repository

environment:
  # Either inline, or referenced from an existing devcontainer.json
  from: .devcontainer/devcontainer.json
  # or inline:
  # image: ghcr.io/acme/build:net8
  # setup: [ "./scripts/bootstrap.sh" ]
  os: linux
  docker: false

setup:
  commands: [ "dotnet restore" ]

testTiers:
  - name: unit
    commands: [ "dotnet test --filter Category=unit" ]
    target: sandbox
  - name: integration
    commands: [ "docker compose -f compose.integration.yaml up --abort-on-container-exit" ]
    target: ci                 # Docker-dependent: CI delegation is the default
    ci: { instance: github-actions, workflow: integration.yml }
  - name: windows
    commands: [ "msbuild /t:Test" ]
    target: ci                 # Windows via CI delegation works in version 1
    ci: { instance: teamcity, workflow: windows-tests }

staticAnalysis:
  analyser: sonarcloud
  failOn: newIssues

ci:
  mapping:
    unit:        { instance: github-actions, workflow: unit.yml }
    integration: { instance: github-actions, workflow: integration.yml }
    windows:     { instance: teamcity, workflow: windows-tests }
  queueBuilds: true            # report results for merge_group events

branches:
  default: main
  integrationPrefix: ff/epic/
  ticketPrefix: ff/ticket/

# F1: read only from this file on the default branch; ignored if the organisation locked it.
merge:
  defaultBranch: { engineMayMerge: false }

# Template choice and added skills and checks: docs/11 section 5.
library:
  default_template: feature-flow-default:templates/ticket-delivery
```

Notes:

- **`target` is the execution target:** `sandbox`, `local`, or `ci` (`docs/01`
  section 5.1.1). A Docker-dependent tier defaults to CI delegation (decision
  Q2); Spike 2 ran one through CI delegation to green (`docs/16` section 2,
  the Docker-dependent tier check).
- **`os` applies to the `sandbox` target only.** A Windows tier delegated to CI
  works in version 1 (decision Q9). Only a Windows **sandbox** requirement fails
  capability negotiation, because version 1 ships no Windows sandbox provider
  (`docs/01` section 4.7).
- **`ci.mapping`** selects a named CI instance per tier (section 3), so GitHub
  Actions and TeamCity coexist without special cases.
- **`merge.defaultBranch.engineMayMerge`** is the repository level of F1
  (section 2.1). It is the one value in this file the engine reads from the
  **default branch**, not from the branch a step is working on, so no agent
  commit can change it. Setting it when the organisation has locked F1 is a
  validation error. The merge method is not set here: it is the merge step's
  `squash` parameter (section 4, merge policy). `requireChecks` is policy, and
  only the organisation sets it.
- **`branches`** names the default branch and the integration (epic) and
  ticket branch prefixes. Code host rules must allow only the engine's identity to update the
  default branch and integration branches (section 9). Agents never push any
  branch; the engine pushes ticket branches for them (section 8).
- **`library`** names the repository's default template and any skills and
  checks it adds. Repositories may add, never override (`docs/11` section 5).
  It keeps the snake_case keys of library content.
- Other files (Dockerfiles, compose files) are referenced by relative path.
- **`.feature-flow/`** holds only the content files that `library` adds
  (`docs/11` section 5). It holds no configuration.

---

## 6. Run schema (API at start)

```yaml
scope: epic                       # ticket | epic | project | delivery
repositories: [ acme/api, acme/web ]
plan:
  ref: artifact://plans/epic-42.yaml
  overrides:
    - step: implement
      limits: { attempts: 4 }     # within the ceiling (F4)
dryRun: false
```

Notes:

- **Every limit is set hierarchically:** the engine default (installation
  `defaults`, which the organisation may override), then the workflow, a child
  workflow, and the step. The most specific wins, within the organisation's
  ceiling (F4: no level may exceed it, and it is checked on every execution of a
  step). This includes the merge step's attempt allowance.
- A run's `plan.overrides` set limits for named steps, within the same ceiling.
  A run may not set policy floors, the default-branch merge setting (F1), or
  select providers.
- `scope: project` is a run that starts epic runs as children, the way an epic
  run starts ticket runs.
- **`dryRun` selects fake or recording providers.** The same plan runs against
  providers that record instead of act, chosen by configuration. No step
  declares dry-run support or needs a dry-run branch. Workflow testing uses the
  same mechanism.

---

## 7. Policy model

One model and one evaluation function, used by gates, merges and steering
(`docs/01` section 5.2). The engine evaluates; workflow content supplies the
plan.

| Object | Contents |
|---|---|
| `approval` | Scope (ticket, epic, delivery into the default branch), required approvers by role, minimum count, whether an agent counts |
| `merge` | Target branch level, required checks, and for the default branch whether the engine may merge (F1) and whether that is locked |
| `findings` | The merge bar (decision D8): no unresolved blocking finding, and every finding fixed or declined with a recorded reason |
| `replan` | Roles an agent-proposed step may use without a human decision |
| `attempts` | Ceilings per step kind and per run, checked on every execution; allowances below them are set hierarchically (section 6); steering may grant more within a hard ceiling. Counting follows `docs/01` section 3.7 (attempt budgets) |
| `steering` | Who may amend a step, override content, grant attempts, abort |
| `cost` | Not in version 1. When added: per-run and per-step budgets, with the action configurable (warn, gate, abort). Exceeding one always stops at a gate, never a hard stop |

### 7.1 Evaluation

```
evaluate(policy, request, context) -> allow | deny(reason)
```

- The **API** evaluates policy for human decisions and steering, and writes the
  audit record before sending the update to the workflow (decision DP8). The
  workflow trusts the API.
- The **workflow** evaluates policy for gates and merges, with the same function
  and the same model.
- A gate decision is evaluated against **`approval`**. **`steering`** covers
  amendments, content overrides, extra attempts and abort.

### 7.2 Roles

| Role | May |
|---|---|
| viewer | Read runs, findings, artifacts |
| approver | Decide at gates within their scope |
| steerer | Amend a step, grant attempts, abort |
| admin | Override content, exceed the hard attempt ceiling, administer |

These are people's roles, mapped to identity-provider groups and scoped by team
or repository (`docs/01` section 5.4). They are not the library's agent roles
(`planner`, `reviewer` and so on), which `roleMapping` maps to runtimes
(section 4).

---

## 8. Secrets

- **By reference only** (F2). A reference names the secrets provider and a key:
  `secrets/github-app-key`.
- **Providers never read credentials from configuration.** All go through the
  credential broker (`docs/01` section 4.6), which issues short-lived
  credentials. Spike 2 measured one: valid 60 minutes, scoped to one
  repository, expiry checkable, wider requests refused (`docs/16` section 2, the
  short-lived credentials check).
- **Model credentials never enter the agent's environment** (DR1). The agent
  runtime's `modelAccess` (section 3) uses a proxy that adds the key, or a per-run
  key with a spending limit and expiry. Not yet evidenced: Spike 2 passed the
  model key in the agent's environment, and the Claude Code proof passed the
  subscription token there; both are recorded as known gaps against DR1
  (`docs/16` sections 10 and 12).
- **Agents get read-only code host credentials, per step.** Scoped to the run's
  repository plus any other repositories the workflow declares the agent may
  read (for example to verify a contract).
- **Agents never hold a credential that can push** (DR13). The broker issues
  push and merge credentials to the engine only. An agent commits in its own
  sandbox; as part of the agent step the engine fetches those commits and pushes
  them with its own credential, at the end of the step and at work-in-progress
  points. A failed step's commits go to a hidden work-in-progress ref, not the
  ticket branch. The engine performs every merge it is allowed to (F1). GitHub
  cannot limit a token to particular branches, so withholding the credential is
  the only reliable control (`docs/20`, "Who can push").
- **The engine's code host identity cannot change branch protection** (DR3).
- Values are scrubbed at the logging layer (`docs/01` section 4.8,
  observability contract).

---

## 9. Validation

Validate at startup and again at run start, with clear errors (decision S10).

| When | Checks |
|---|---|
| Startup | Schema validity; provider packages present; secret references resolvable; Temporal reachable; **expected retention matches (warn on mismatch)**; floor definitions internally consistent; every agent runtime instance has `modelAccess` (DR1); `endpoint` set only on a runtime that supports a custom endpoint |
| Run start and plan validation (including after a re-plan) | Capability negotiation against the plan's steps (decision S3: every step's required capabilities are offered by some provider); policy floors satisfied; **floors only tightened down the layers**; limits within the ceiling; **F1: no merge step into the default branch has `engine_can_merge: true` unless the effective setting allows it**, with the repository value read from its default branch; no lower level relaxes a locked F1; target repositories reachable; **code host rules exist** on each target repository (below) |
| Per step | `feature-flow.yaml` read from the branch and its hash recorded (F1 excepted: always from the default branch); capability negotiation re-run if a needed capability disappeared (`docs/01` section 5.1.2); **attempt ceiling checked on every execution, including loop re-entry** (DR10) |
| Merge time | F1 checked again, reading the base and default branch from the code host (DR2) |

**Code host rules** (DR2, DR3, and the merge design `docs/20`): branch
protection on the default branch requires independent review, and only the
engine's identity may update integration branches. The engine **checks these
rules exist; it never creates or changes them, and never holds a bypass.**
They are configured by an identity the engine does not hold. They are the final
fallback: the engine already enforces F1 at plan validation and merge time, and
agents hold no push credential.

A validation failure is a clear error naming the layer, the key and the reason,
never a silent fallback. A run should fail in its first minute, not at step 40.

---

## 10. Worked profiles

The same code runs in all three; only configuration differs (decision D4).

**Where it runs** (DR5): development on Windows or macOS; the engine and workers
at runtime on Windows, macOS or Linux containers. Docker is used only for
packaging and for sandboxes. Pool `capabilities` such as `os-linux` describe the
sandboxes a pool can provide, not the operating system the worker itself runs
on. The spikes ran on macOS, and Spike 2's Claude Code hook check on Windows;
Linux containers are not yet evidenced.

### 10.1 Solo developer

One developer, one machine (Windows or macOS), one repository. Everything
local; sandboxes are Docker containers.

```yaml
apiVersion: feature-flow/v1
kind: Installation

temporal:
  address: localhost:7233
  namespace: default
  auth: { mode: none }
  expectedRetention: 7d

workerPlatform:
  mode: local
  orchestration: { queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]

providers:
  sandbox:
    default: docker
    instances: { docker: { name: docker } }
  agentRuntime:
    default: claude-code
    instances:
      claude-code:
        name: claude-code
        config: { modelAccess: { mode: proxy, keyRef: env/ANTHROPIC_API_KEY } }
  codeHost:
    default: github
    instances: { github: { name: github, config: { secretRef: env/GITHUB_TOKEN } } }
  ci:
    default: github-actions
    instances: { github-actions: { name: github-actions } }
  tracker:
    default: github-issues
    instances: { github-issues: { name: github-issues } }
  review:
    default: copilot-review
    instances: { copilot-review: { name: copilot-review } }
  artifactStore:
    default: filesystem
    instances: { filesystem: { name: filesystem, config: { path: ~/.feature-flow/artifacts } } }
  recordStore:
    default: sqlite
    instances: { sqlite: { name: sqlite, config: { path: ~/.feature-flow/records.db } } }
  secrets:
    default: env
    instances: { env: { name: env } }
  notifications:
    default: console
    instances: { console: { name: console } }
  credentialBroker:
    default: personal-token
    instances: { personal-token: { name: personal-token } }

api:
  auth: { mode: localToken }

observability:
  otlpEndpoint: http://localhost:4317

defaults:
  attempts: { agent.run: 3 }
```

```yaml
apiVersion: feature-flow/v1
kind: Organisation
name: solo

policy:
  approval:
    ticket: { roles: [approver], minCount: 1 }
    defaultBranch: { roles: [approver], minCount: 1 }
  merge:
    defaultBranch: { requireChecks: false, engineMayMerge: false }   # F1 default; not locked
  attempts:
    ceiling: { agent.run: 5, perRun: 40 }
    hardCeiling: { agent.run: 8, perRun: 60 }
  steering:
    mayAmendStep: [approver]
    mayGrantAttempts: [approver]
    mayAbort: [approver]

roleMapping:
  implementer: { agentRuntime: claude-code, model: claude-haiku }
  fixer:       { agentRuntime: claude-code, model: claude-haiku }
  reviewer:    { agentRuntime: claude-code, model: claude-sonnet }

roles:
  approver: { localUsers: [me] }
```

Notes:

- F1 keeps its safe default here: the engine does not merge into the default
  branch. It is not locked, so a proof-of-concept repository may relax it in
  its own `feature-flow.yaml` on the default branch.
- Docker-dependent tiers may use a privileged container here, which is
  permitted **only** in this profile (decision Q2).
- The personal token is the engine's credential, used by the engine to push
  and merge. It is never passed into an agent's sandbox (DR13), and neither is
  the model key (DR1).

### 10.2 Small company

A team on its own cloud: Kubernetes workers, managed stores, OIDC sign-in.

```yaml
apiVersion: feature-flow/v1
kind: Installation

temporal:
  address: temporal.ff.acme.cloud:7233
  namespace: feature-flow
  auth: { mode: apiKey, secretRef: secrets/temporal-api-key }
  tls: { enabled: true }
  expectedRetention: 30d

workerPlatform:
  mode: kubernetes
  orchestration: { queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]

providers:
  sandbox:
    default: kubernetes-pod
    instances: { kubernetes-pod: { name: kubernetes-pod } }
  agentRuntime:
    default: claude-code
    instances:
      claude-code:
        name: claude-code
        config: { modelAccess: { mode: proxy, keyRef: secrets/anthropic-key } }
  codeHost:
    default: github
    instances: { github: { name: github, config: { appId: 9911, secretRef: secrets/github-app-key } } }
  ci:
    default: github-actions
    instances: { github-actions: { name: github-actions } }
  tracker:
    default: jira
    instances: { jira: { name: jira, config: { baseUrl: https://acme.atlassian.net, secretRef: secrets/jira-token } } }
  review:
    default: copilot-review
    instances: { copilot-review: { name: copilot-review } }
  artifactStore:
    default: s3
    instances: { s3: { name: s3, config: { bucket: acme-ff-artifacts } } }
  recordStore:
    default: postgres
    instances: { postgres: { name: postgres, config: { connectionSecretRef: secrets/ff-db } } }
  secrets:
    default: aws-secrets-manager
    instances: { aws-secrets-manager: { name: aws-secrets-manager } }
  notifications:
    default: slack
    instances: { slack: { name: slack, config: { secretRef: secrets/slack-token } } }
  credentialBroker:
    default: github-app
    instances: { github-app: { name: github-app } }

api:
  baseUrl: https://ff.acme.cloud
  auth:
    mode: oidc
    issuer: https://accounts.google.com
    audience: feature-flow
    roleClaim: groups

observability:
  otlpEndpoint: https://otel.acme.cloud:4317
```

```yaml
apiVersion: feature-flow/v1
kind: Organisation
name: acme

policy:
  approval:
    ticket: { roles: [approver], minCount: 1 }
    epic:   { roles: [approver], minCount: 1 }
    defaultBranch: { roles: [approver], minCount: 1 }
  merge:
    defaultBranch: { requireChecks: true, engineMayMerge: false, locked: true }
    integrationBranch: { requireChecks: true }
  findings:
    mergeBar: noUnresolvedBlocking
    requireDeclineReason: true
  attempts:
    ceiling: { agent.run: 5, perRun: 40 }
    hardCeiling: { agent.run: 8, perRun: 60 }
  steering:
    mayAmendStep: [steerer, admin]
    mayGrantAttempts: [steerer, admin]
    mayAbort: [steerer, admin]
  chat:
    allowedDecisions: [ticket.gate, epic.gate]

roleMapping:
  planner:     { agentRuntime: claude-code, model: claude-haiku }
  implementer: { agentRuntime: claude-code, model: claude-haiku }
  fixer:       { agentRuntime: claude-code, model: claude-haiku }
  reviewer:    { agentRuntime: claude-code, model: claude-sonnet }

roles:
  viewer:   { idpGroups: [acme-eng] }
  approver: { idpGroups: [acme-leads] }
  steerer:  { idpGroups: [acme-leads] }
  admin:    { idpGroups: [acme-platform] }

notifications:
  routing:
    decisionNeeded: { channel: "#ff-approvals" }
    runStuck:       { channel: "#ff-ops" }
```

### 10.3 Enterprise

The first enterprise adopter (`brief.md` section 9): Temporal Cloud,
organisation Kubernetes, several CI systems, GitHub App identity, a model
router, and Windows sandboxes deferred.

```yaml
apiVersion: feature-flow/v1
kind: Installation

temporal:
  address: acme-prod.tmprl.cloud:7233
  namespace: feature-flow-prod          # one namespace per environment, immutable
  auth: { mode: apiKey, secretRef: secrets/temporal-api-key }
  tls: { enabled: true }
  payloadCodec: { name: aes-gcm, keyRef: secrets/payload-key }
  expectedRetention: 90d

workerPlatform:
  mode: kubernetes
  orchestration: { queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]
      - queue: feature-flow-agent:docker
        capabilities: [os-linux, docker]
      # feature-flow-agent:os-windows is declared but has no provider in v1 (docs/01 section 4.7)

providers:
  sandbox:
    default: kubernetes-pod
    instances: { kubernetes-pod: { name: kubernetes-pod } }
  agentRuntime:
    default: claude-code
    instances:
      claude-code:
        name: claude-code
        config: { modelAccess: { mode: proxy, keyRef: secrets/anthropic-key } }
      copilot-cli:
        name: copilot-cli
        config:
          endpoint: https://llm-router.acme.internal/v1
          modelAccess: { mode: proxy, keyRef: secrets/router-key }
  codeHost:
    default: github
    instances: { github: { name: github, config: { appId: 12345, secretRef: secrets/github-app-key } } }
  ci:
    default: github-actions
    instances:
      github-actions: { name: github-actions }
      teamcity:       { name: teamcity, config: { baseUrl: https://tc.acme.internal, secretRef: secrets/teamcity-token } }
  tracker:
    default: jira
    instances: { jira: { name: jira, config: { baseUrl: https://acme.atlassian.net, secretRef: secrets/jira-token } } }
  review:
    default: copilot-review
    instances:
      copilot-review:  { name: copilot-review }
      engine-reviewer: { name: engine-reviewer }
  artifactStore:
    default: azure-blob
    instances: { azure-blob: { name: azure-blob, config: { container: ff-artifacts } } }
  recordStore:
    default: postgres
    instances: { postgres: { name: postgres, config: { connectionSecretRef: secrets/ff-db } } }
  secrets:
    default: internal-platform
    instances: { internal-platform: { name: internal-platform, config: { mode: oidc-planned } } }
  notifications:
    default: slack
    instances: { slack: { name: slack, config: { secretRef: secrets/slack-token } } }
  credentialBroker:
    default: github-app
    instances: { github-app: { name: github-app } }

api:
  baseUrl: https://feature-flow.acme.internal
  auth:
    mode: oidc
    issuer: https://idp.acme.internal
    audience: feature-flow
    roleClaim: groups

observability:
  otlpEndpoint: https://otel.acme.internal:4317
  logLevel: info

defaults:
  attempts: { agent.run: 3, plan.replan: 2, findings.triage: 2, scm.merge: 3 }
  timeouts: { agent.run: 4h }
```

```yaml
apiVersion: feature-flow/v1
kind: Organisation
name: acme

policy:
  approval:
    ticket: { roles: [approver], minCount: 1, agentCounts: false }
    epic:   { roles: [approver], minCount: 1, agentCounts: false }
    defaultBranch: { roles: [approver], minCount: 2, agentCounts: false }
  merge:
    defaultBranch: { requireChecks: true, engineMayMerge: false, locked: true }
    integrationBranch: { requireChecks: true }
  findings:
    mergeBar: noUnresolvedBlocking
    requireDeclineReason: true
  replan:
    allowedRoles: [planner, implementer, fixer]
  attempts:
    ceiling: { agent.run: 5, perRun: 40 }
    hardCeiling: { agent.run: 8, perRun: 60 }
  steering:
    mayAmendStep: [steerer, admin]
    mayOverrideContent: [admin]
    mayGrantAttempts: [steerer, admin]
    mayAbort: [steerer, admin]
  chat:
    allowedDecisions: [ticket.gate, epic.gate]

roleMapping:
  planner:     { agentRuntime: copilot-cli, model: router-default }
  implementer: { agentRuntime: claude-code, model: claude-haiku }
  fixer:       { agentRuntime: claude-code, model: claude-haiku }
  reviewer:    { agentRuntime: claude-code, model: claude-sonnet }

roles:
  viewer:   { idpGroups: [acme-engineering] }
  approver: { idpGroups: [acme-leads] }
  steerer:  { idpGroups: [acme-leads] }
  admin:    { idpGroups: [acme-platform] }

notifications:
  routing:
    decisionNeeded: { channel: "#ff-approvals" }
    runStuck:       { channel: "#ff-ops" }
    runFinished:    { channel: "#ff-runs" }
```

Notes:

- **Two CI systems are named instances;** the repository's `ci.mapping` picks
  one per tier. The same mechanism gives two review providers and two agent
  runtimes.
- **Two agent runtimes:** `copilot-cli` reaches models through the
  organisation's OpenAI-compatible router; `claude-code` cannot use the router
  and talks to Anthropic directly. Which role runs where is a choice; the
  mapping shown is an example.
- **The `os-windows` pool is declared but has no provider in version 1.** A run
  needing a Windows **sandbox** fails at run-start capability negotiation with a
  clear message. A Windows tier delegated to **CI** works in version 1.
- **The payload codec** is configured because the organisation requires
  encrypted Temporal payloads. It is a configuration choice, not a code change
  (`docs/01` section 3.5, Temporal constraints).

---

## 11. Open questions

The first-pass questions are all decided and applied above:

| # | Question | Decision |
|---|---|---|
| 1 | Separate team layer? | No. Team-scoped overrides in the organisation file (section 4) |
| 2 | May a repository tighten a floor? | Yes. Any layer may tighten; none may relax (section 2) |
| 3 | Choosing between two CI providers | Every provider type may have several named instances with one default (section 3) |
| 4 | Where `defaults.attempts` belongs | Installation provides defaults; the organisation may override within its ceilings. Defaults are not floors (section 3) |
| 5 | Budget configuration shape | Deferred with cost budgets (decision Q16). When added: per run and per step kind at the organisation layer; a run may only lower them; exceeding one always gates |
| 6 | Per-step dry-run support | Not needed. Dry run uses fake or recording providers (section 6) |

### Still open

| # | Question |
|---|---|
| 1 | **Model access (DR1).** Which `modelAccess` mode is the default for each profile, and how is the proxy itself deployed and configured? |
| 2 | **Pool sizing headroom.** Spike 1 has two repetitions at 20 steps on 10 slots, not the three asked for. Run a third before setting a headroom figure? |
| 3 | **Copilot CLI hooks.** Re-test before- and after-tool-call hooks with a first-party model. Until then, deny-before-execution is unavailable on `copilot-cli` and coverage comes from its streamed tool calls |
| 4 | **`policy.approval.defaultBranch` under F1.** With F1 at its default a human merges on the code host, under its branch protection. What does this approval policy govern then: the engine's decision request before that merge, or an engine merge where F1 is relaxed? |
