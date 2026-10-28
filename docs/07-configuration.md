# Configuration schema, precedence and profiles

Status: first pass, revised for review 07. Date: 2026-10-26. Deliverable 3 of
`brief.md` section 11. Companion to `docs/01-architecture-first-pass.md` section 5,
which holds the decisions this document turns into a schema.

**Scope.** The four layers, what each may set, the precedence rules and the safety
floors, the policy model, secrets, validation, and three worked profiles.

**Review 07 applied.** Every provider type may have **several named instances**
with one default, replacing `ciSecondary` (answer 3). Any layer may tighten a
floor; none may relax one (answer 2). Team overrides live in the organisation file,
not a fifth layer (answer 1). Defaults may be set at installation and overridden at
organisation (answer 4). Dry run uses **fake or recording providers**, not per-step
declarations (answer 6). Merge settings split into what the organisation *allows*
and what the repository *chooses* (a); `os` applies to the sandbox target only (b);
retention is expected-and-checked, not applied (c); scaling is removed from engine
configuration (d).

---

## 1. Layers

| Layer | Where | Set by | Applies to |
|---|---|---|---|
| Installation | Deployment config | Operator | One installation |
| Organisation / team | Config service or file | Organisation | One organisation, or a team within it |
| Repository | Root `feature-flow.yaml`, in the repository | Repository maintainers | One repository |
| Run | API at run start | Requester | One run |

Precedence: **most specific layer wins, except the safety floors in section 2,
which no lower layer may relax** (DP6).

"Most specific" means: run beats repository beats organisation beats installation.
A layer may only set what section 1.1 allows it to set; anything else is a
validation error, not a silent override.

**There is no fifth layer** (review 07, answer 1). Where teams need different
policy, the organisation file contains **team-scoped overrides** selected by team:
same layer, same file, no extra precedence step.

### 1.1 What each layer may set

| Layer | May set | May not set |
|---|---|---|
| Installation | Temporal connection, worker platform, provider selection and instances, credential references, expected retention, payload codec, observability, API auth, defaults | Anything repository-specific |
| Organisation / team | Policy floors, provider bindings, notification routing, role mapping, budgets, who may approve and steer, defaults overrides | Repository build and test detail |
| Repository | Test tiers and how to run them, setup commands, static analysis, environment requirements, CI mapping, branch conventions, **merge choices within what policy allows** | Policy floors, credentials |
| Run | Scope, target repositories, plan overrides, attempt budgets within ceiling, dry-run | Policy floors, provider selection |

The repository layer is the one that lives in the repository and travels with the
code. Everything else is deployment or organisation configuration.

---

## 2. Safety floors

Decided (DP6). The prototype's "settings conflicted" failure was exactly this:
separate settings for merge gates, review mode, mandatory steps and auto-merge
interacted and stopped the run.

A floor is a value that **no lower layer may relax**. **Any layer may tighten one**
(review 07, answer 2) — including a repository tightening a floor for itself.
Validation enforces that floors only move in the stricter direction down the
layers. The list is deliberately short and explicit.

| # | Floor | Meaning |
|---|---|---|
| F1 | Human approval for default-branch merges | A merge into a repository's default branch requires a human. No layer may set auto-merge for it |
| F2 | Secrets by reference | No layer may inline a secret value. Only a reference to the secrets provider |
| F3 | Protected steps | A step marked `protected` in the plan cannot be removed or weakened by a re-plan or by steering |
| F4 | Attempt ceilings | A run may set attempt budgets **within** the configured ceiling, never above it. Steering may grant more up to a hard ceiling; beyond that needs the admin role |

Two consequences:

- `policy.auto_approve` cannot approve a default-branch merge (F1).
- A repository cannot grant itself unlimited attempts (F4), and neither can a run.

Floors are enforced by the engine, not by workflow content (section 1.5 of the
architecture document). That is the exception to the mechanism-versus-policy split:
the engine enforces floors because no workflow may relax them.

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
  expectedRetention: 30d      # checked at startup, warned on mismatch; not applied (review 07, c)

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
      claude-code:    { name: claude-code }
      copilot-cli:    { name: copilot-cli }
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
  attempts: { agent.run: 3, plan.replan: 2, findings.triage: 2 }
  timeouts: { agent.run: 4h }
```

Notes:

- **Every provider type may have several named instances, with one marked
  `default`** (review 07, answer 3). This generalises the two-CI-providers case and
  covers several review providers (an AI review on the PR plus the engine's own
  reviewer agent) and several agent runtimes selected by role. A repository's
  `ci.mapping` selects the instance per tier; other selections work the same way.
- Providers are selected **by name**; configuration selects them, code does not
  (section 4.5). A host project references the provider packages it ships.
- Every credential is a `secretRef`. No inline secrets (F2).
- `expectedRetention` is **checked, not applied** (review 07, c). Retention is a
  namespace setting owned by the platform team on managed Temporal. The engine
  warns on mismatch at startup.
- **Scaling is not engine configuration** (review 07, d). Replica counts belong to
  the deployment: Helm values, a Kubernetes autoscaler, or compose. The engine
  names the task queues and the capabilities each pool serves.
- `defaults` are the starting values for attempt allowances and timeouts. Review 06
  accepted these as starting values, to be tuned from recorded data. **Defaults are
  not floors** (review 07, answer 4): the installation provides them and the
  organisation may override them within its ceilings.

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
    defaultBranch: { roles: [approver], minCount: 2, agentCounts: false }   # floor F1
  merge:
    defaultBranch: { methods: [squash], requireChecks: true, autoMerge: false }  # floor F1
    integrationBranch: { methods: [merge], requireChecks: true, autoMerge: true }
  findings:
    mergeBar: noUnresolvedBlocking
    requireDeclineReason: true
  attempts:
    ceiling: { agent.run: 5, perRun: 40 }
    hardCeiling: { agent.run: 8, perRun: 60 }     # steering may not exceed without admin
  timeouts:
    heartbeat: 30s                                # how long a step may go silent
    stalledAfter: 5m                              # time with no successful contact before the run is marked stalled
  steering:
    mayAmendStep: [steerer, admin]
    mayOverrideContent: [admin]
    mayGrantAttempts: [steerer, admin]
    mayAbort: [steerer, admin]
  chat:
    allowedDecisions: [ticket.gate, epic.gate]    # default-branch merge is console only

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
        defaultBranch: { minCount: 3 }        # tightens F1; never relaxes it
      attempts:
        ceiling: { agent.run: 4 }
    roles:
      approver: { idpGroups: [acme-payments-leads] }
```

Notes:

- Roles come from the policy model and are mapped to identity-provider groups
  (section 5.4). Scoping by team or repository is a role-mapping detail.
- `chat.allowedDecisions` states which decisions may be made from chat. Ticket and
  epic gates yes; a default-branch merge only in the console.
- `attempts.ceiling` is what a run may set within (F4). `hardCeiling` is what
  steering may grant up to; beyond it needs the admin role.
- **`timeouts.heartbeat` is an operational setting, not a plan value** (review 12
  B2). It is how long a step may go silent before the engine treats it as
  interrupted, and it is the dominant term in crash recovery cost: Spike 1
  measured 87.4s from worker kill to the step making progress again, almost all
  of it this timeout plus worker restart. Lowering it detects crashes faster but
  risks false positives on a slow step; raising it delays detection. It is set
  per installation, not per step, because it describes the platform rather than
  the work.
- **`timeouts.stalledAfter`** is how long a run may go without successful
  contact with a provider before it is marked stalled on infrastructure (D24).
  It is **time-based, not a count of retries** (review 13 B1): a count took about
  8 minutes to reach, because each retry costs a heartbeat timeout plus backoff,
  and that coupling made the threshold depend on two other settings. A stalled
  run alerts and waits; it never fails on its own. A human may abort it. Any
  successful contact resets the clock and clears the state.
- **Team overrides are not a fifth layer** (review 07, answer 1). They live in the
  organisation file and are selected by team, so precedence is unchanged.
- **`policy.merge` states what is allowed; the repository chooses within it**
  (review 07, a). See section 5. Two places for one setting was the prototype's
  "settings conflicted" failure, so the split is: organisation says which methods
  are permitted and whether checks are required; the repository states its
  preferred method. A repository choice outside the allowed set is a **validation
  error at run start**, never a silent override.

---

## 5. `feature-flow.yaml` (repository schema)

Decided (review 03, Q7). Root-level, authoritative, applies to every execution
target. Read per step, never copied into the plan (section 5.1.2).

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
    target: ci                 # Docker-dependent: CI delegation is the default (Q2)
    ci: { instance: github-actions, workflow: integration.yml }
  - name: windows
    commands: [ "msbuild /t:Test" ]
    target: ci                 # CI delegation: works in v1 (review 03, Q9)
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

# The repository chooses within what organisation policy allows (review 07, a).
merge:
  method: squash               # must be in policy.merge.defaultBranch.methods
```

Notes:

- `target` is the **execution target**: `sandbox`, `local`, or `ci` (section 5.1.1).
  A Docker-dependent tier defaults to CI delegation (Q2).
- **`os` applies to the `sandbox` execution target only** (review 07, b). A Windows
  tier delegated to CI **must work in version 1** — that is what review 03 decided
  for Q9. Only a Windows **sandbox** requirement fails capability negotiation,
  because version 1 ships no Windows sandbox provider (section 4.7).
- `ci.mapping` selects a **named provider instance** per tier (review 07, answer 3),
  which is how GitHub Actions and TeamCity coexist without special cases.
- **`merge.method` is a choice, not a policy** (review 07, a). The organisation
  states which methods are allowed; the repository picks one. A choice outside the
  allowed set is a validation error at run start, never a silent override.
  `requireChecks` is **not** repeated here — it is policy, and only the organisation
  sets it.
- Other files (Dockerfiles, compose files) are referenced by relative path.
- `.feature-flow/` is reserved for later use and is not used in version 1.

---

## 6. Run schema (API at start)

```yaml
scope: epic                       # ticket | epic | delivery
repositories: [ acme/api, acme/web ]
plan:
  ref: artifact://plans/epic-42.yaml
  overrides:
    - step: implement
      limits: { attempts: 4 }     # within the ceiling (F4)
dryRun: false
```

Notes:

- A run may set attempt budgets **within** the configured ceiling (F4), and may not
  set policy floors or provider selection.
- **`dryRun` selects fake or recording providers** (review 07, answer 6). The same
  plan runs against providers that record instead of act, chosen by configuration.
  No step declares dry-run support, and no step needs a dry-run branch. This is the
  same mechanism workflow testing will need, so one feature serves both.

---

## 7. Policy model

One model, one evaluation function, used by gates, merges and steering (section
5.2). The engine evaluates; workflow content supplies the plan.

| Object | Contents |
|---|---|
| `approval` | Scope (ticket, epic, delivery into default branch), required approvers by role, minimum count, whether an agent counts |
| `merge` | Target branch pattern, allowed methods, required checks, whether auto-merge is permitted |
| `findings` | Merge bar per D8: no unresolved blocking finding, and every finding fixed or declined with a recorded reason |
| `attempts` | Per step kind and per run ceilings; steering may grant more within a hard ceiling. Counting follows section 3.7 |
| `steering` | Who may amend a step, override content, grant attempts, abort |
| `cost` | Not in version 1. When added: per-run and per-step budgets, with the action configurable (warn, gate, abort). Exceeding one always stops at a gate, never a hard stop |

### 7.1 Evaluation

```
evaluate(policy, request, context) -> allow | deny(reason)
```

- The **API** evaluates policy for decisions and steering, and writes the audit
  record before sending the update (DP8). The workflow trusts the API.
- The **workflow** evaluates policy for gates and merges, using the same function
  against the same model.
- A gate decision is evaluated against the **`approval`** policy. The `steering`
  policy is for amendments, overrides, extra attempts and abort (review 06, S6).

### 7.2 Roles

| Role | May |
|---|---|
| viewer | Read runs, findings, artifacts |
| approver | Decide at gates within their scope |
| steerer | Amend a step, grant attempts, abort |
| admin | Override content, exceed the hard attempt ceiling, administer |

Roles are mapped to identity-provider groups and scoped by team or repository
(section 5.4).

---

## 8. Secrets

- **By reference only** (F2). No layer may inline a secret value.
- A reference names the secrets provider and a key: `secrets/github-app-key`.
- Providers never read credentials from configuration. All go through the
  credential broker (section 4.6), which issues short-lived credentials.
- Values are scrubbed at the logging layer (section 4.8).

---

## 9. Validation

Decided (S10): validate at startup and again at run start, with clear errors.

| When | Checks |
|---|---|
| Startup | Schema validity, provider packages present, secrets references resolvable, Temporal reachable, **expected retention matches (warn on mismatch)**, floor definitions internally consistent |
| Run start | Capability negotiation against the plan's steps (S3), policy floors satisfied, **floors only tightened down the layers**, attempt budgets within ceiling, **repository merge choice within the allowed set**, target repositories reachable |
| Per step | `feature-flow.yaml` read from the branch and its hash recorded; capability negotiation re-run if a needed capability disappeared (section 5.1.2) |

A validation failure is a clear error naming the layer, the key and the reason. It
is never a silent fallback. The intent is that a run fails in the first minute, not
at step 40.

---

## 10. Worked profiles

Same code in all three. Only configuration differs (D4).

### 10.1 Solo developer

One developer, one machine, one repository. Everything local.

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
    instances: { claude-code: { name: claude-code } }
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
    defaultBranch: { roles: [approver], minCount: 1 }    # floor F1 still applies
  merge:
    defaultBranch: { methods: [squash], requireChecks: false, autoMerge: false }
  attempts:
    ceiling: { agent.run: 5, perRun: 40 }
    hardCeiling: { agent.run: 8, perRun: 60 }
  steering:
    mayAmendStep: [approver]
    mayGrantAttempts: [approver]
    mayAbort: [approver]

roles:
  approver: { localUsers: [me] }
```

Notes: the solo profile still cannot auto-merge into a default branch (F1). The
floor is not a deployment-profile convenience; it is invariant. Docker-dependent
tiers may use a privileged pod here, which is permitted **only** in this profile
(Q2).

### 10.2 Small company

A team on its own cloud. Kubernetes workers, managed stores, OIDC.

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
    instances: { claude-code: { name: claude-code } }
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
    defaultBranch: { methods: [squash], requireChecks: true, autoMerge: false }
    integrationBranch: { methods: [merge], requireChecks: true, autoMerge: true }
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

The first enterprise adopter (brief section 9). Temporal Cloud, organisation
Kubernetes, several CI systems, GitHub App identity, Windows-only repositories
deferred.

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
      # feature-flow-agent:os-windows is declared but has no provider in v1 (section 4.7)

providers:
  sandbox:
    default: kubernetes-pod
    instances: { kubernetes-pod: { name: kubernetes-pod } }
  agentRuntime:
    default: claude-code
    instances:
      claude-code: { name: claude-code }
      copilot-cli: { name: copilot-cli }
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
  attempts: { agent.run: 3, plan.replan: 2, findings.triage: 2 }
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
    defaultBranch: { methods: [squash], requireChecks: true, autoMerge: false }
    integrationBranch: { methods: [merge], requireChecks: true, autoMerge: true }
  findings:
    mergeBar: noUnresolvedBlocking
    requireDeclineReason: true
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

- Two CI providers are configured as **named instances**; the repository's
  `ci.mapping` selects one per tier, so GitHub Actions and TeamCity coexist without
  special cases. The same mechanism gives two review providers and two agent
  runtimes.
- The `os-windows` pool is declared but has no provider in version 1. A run needing
  a Windows **sandbox** fails at run-start capability negotiation with a clear
  message. A Windows tier delegated to **CI** works in version 1 (review 07, b).
- The payload codec is configured because the organisation requires encrypted
  payloads. It is a configuration choice, not a code change (section 3.5).

---

## 11. Open questions

All six questions from the first pass were answered in review 07 and are applied
above:

| # | Question | Answer |
|---|---|---|
| 1 | Split organisation and team | **No fifth layer.** Team-scoped overrides live in the organisation file, selected by team (section 4) |
| 2 | May a repository tighten a floor | **Yes.** Any layer may tighten; none may relax. Validation enforces that floors only move in the stricter direction (section 2) |
| 3 | Selecting between two CI providers | **Generalised:** every provider type may have several named instances with one default. `ci.mapping` selects per tier (section 3) |
| 4 | Where `defaults.attempts` belongs | **Both.** Installation provides defaults; organisation may override within its ceilings. Defaults are not floors (section 3) |
| 5 | Budget configuration shape | Deferred with Q16. When added: per run and per step kind at the organisation layer; a run may only lower them; exceeding one always gates |
| 6 | Per-step dry-run support | **Not needed.** A dry run runs the same plan against fake or recording providers, selected by configuration (section 6) |

### Still open

None carried forward.
