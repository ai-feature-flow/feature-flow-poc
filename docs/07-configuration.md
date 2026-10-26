# Configuration schema, precedence and profiles

Status: first pass. Date: 2026-10-26. Deliverable 3 of `brief.md` section 11.
Companion to `docs/01-architecture-first-pass.md` section 5, which holds the
decisions this document turns into a schema.

**Scope.** The four layers, what each may set, the precedence rules and the safety
floors, the policy model, secrets, validation, and three worked profiles.

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

### 1.1 What each layer may set

| Layer | May set | May not set |
|---|---|---|
| Installation | Temporal connection, worker platform, provider selection, credential references, retention, payload codec, observability, API auth, defaults | Anything repository-specific |
| Organisation / team | Policy floors, provider bindings, notification routing, role mapping, budgets, who may approve and steer | Repository build and test detail |
| Repository | Test tiers and how to run them, setup commands, static analysis, environment requirements, CI mapping, branch and merge conventions | Policy floors, credentials |
| Run | Scope, target repositories, plan overrides, attempt budgets within ceiling, dry-run | Policy floors, provider selection |

The repository layer is the one that lives in the repository and travels with the
code. Everything else is deployment or organisation configuration.

---

## 2. Safety floors

Decided (DP6). The prototype's "settings conflicted" failure was exactly this:
separate settings for merge gates, review mode, mandatory steps and auto-merge
interacted and stopped the run.

A floor is a value that **no lower layer may relax**. A lower layer may tighten it.
The list is deliberately short and explicit.

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
  retention: 30d

workerPlatform:
  mode: kubernetes
  orchestration: { replicas: 3, queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]
        replicas: 5
      - queue: feature-flow-agent:os-windows
        capabilities: [os-windows]
        replicas: 2

providers:
  sandbox:       { name: kubernetes-pod }
  agentRuntime:  { name: claude-code }
  codeHost:      { name: github, config: { appId: 12345, secretRef: secrets/github-app-key } }
  ci:            { name: github-actions }
  tracker:       { name: jira, config: { baseUrl: https://acme.atlassian.net, secretRef: secrets/jira-token } }
  review:        { name: copilot-review }
  artifactStore: { name: azure-blob, config: { container: ff-artifacts } }
  recordStore:   { name: postgres, config: { connectionSecretRef: secrets/ff-db } }
  secrets:       { name: azure-keyvault, config: { vault: acme-kv } }
  notifications: { name: slack, config: { secretRef: secrets/slack-token } }
  credentialBroker: { name: github-app }

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

- Providers are selected **by name**; configuration selects them, code does not
  (section 4.5). A host project references the provider packages it ships.
- Every credential is a `secretRef`. No inline secrets (F2).
- `defaults` are the starting values for attempt allowances and timeouts. Review 06
  accepted these as starting values, to be tuned from recorded data.

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
```

Notes:

- Roles come from the policy model and are mapped to identity-provider groups
  (section 5.4). Scoping by team or repository is a role-mapping detail.
- `chat.allowedDecisions` states which decisions may be made from chat. Ticket and
  epic gates yes; a default-branch merge only in the console.
- `attempts.ceiling` is what a run may set within (F4). `hardCeiling` is what
  steering may grant up to; beyond it needs the admin role.

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
    ci: { workflow: integration.yml }
  - name: windows
    commands: [ "msbuild /t:Test" ]
    target: ci
    os: windows

staticAnalysis:
  analyser: sonarcloud
  failOn: newIssues

ci:
  mapping:
    unit:        { workflow: unit.yml }
    integration: { workflow: integration.yml }
  queueBuilds: true            # report results for merge_group events

branches:
  default: main
  integrationPrefix: ff/epic/
  ticketPrefix: ff/ticket/

merge:
  method: squash
  requireChecks: true
```

Notes:

- `target` is the **execution target**: `sandbox`, `local`, or `ci` (section 5.1.1).
  A Docker-dependent tier defaults to CI delegation (Q2).
- `os: windows` on a tier routes to `feature-flow-agent:os-windows` (S1). Version 1
  ships no Windows sandbox provider, so such a run fails at run-start capability
  negotiation with a clear message (section 4.7).
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
- `dryRun` runs the plan without side effects where steps support it.

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
| Startup | Schema validity, provider packages present, secrets references resolvable, Temporal reachable, floor definitions internally consistent |
| Run start | Capability negotiation against the plan's steps (S3), policy floors satisfied, attempt budgets within ceiling, target repositories reachable |
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
  retention: 7d

workerPlatform:
  mode: local
  orchestration: { queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]

providers:
  sandbox:       { name: docker }
  agentRuntime:  { name: claude-code }
  codeHost:      { name: github, config: { secretRef: env/GITHUB_TOKEN } }
  ci:            { name: github-actions }
  tracker:       { name: github-issues }
  review:        { name: copilot-review }
  artifactStore: { name: filesystem, config: { path: ~/.feature-flow/artifacts } }
  recordStore:   { name: sqlite, config: { path: ~/.feature-flow/records.db } }
  secrets:       { name: env }
  notifications: { name: console }
  credentialBroker: { name: personal-token }

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
  retention: 30d

workerPlatform:
  mode: kubernetes
  orchestration: { replicas: 2, queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]
        replicas: 3

providers:
  sandbox:       { name: kubernetes-pod }
  agentRuntime:  { name: claude-code }
  codeHost:      { name: github, config: { appId: 9911, secretRef: secrets/github-app-key } }
  ci:            { name: github-actions }
  tracker:       { name: jira, config: { baseUrl: https://acme.atlassian.net, secretRef: secrets/jira-token } }
  review:        { name: copilot-review }
  artifactStore: { name: s3, config: { bucket: acme-ff-artifacts } }
  recordStore:   { name: postgres, config: { connectionSecretRef: secrets/ff-db } }
  secrets:       { name: aws-secrets-manager }
  notifications: { name: slack, config: { secretRef: secrets/slack-token } }
  credentialBroker: { name: github-app }

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
  retention: 90d

workerPlatform:
  mode: kubernetes
  orchestration: { replicas: 5, queue: feature-flow-orchestration }
  agent:
    pools:
      - queue: feature-flow-agent
        capabilities: [os-linux, docker]
        replicas: 10
      - queue: feature-flow-agent:docker
        capabilities: [os-linux, docker]
        replicas: 4
      # feature-flow-agent:os-windows is declared but has no provider in v1 (section 4.7)

providers:
  sandbox:       { name: kubernetes-pod }
  agentRuntime:  { name: claude-code }
  codeHost:      { name: github, config: { appId: 12345, secretRef: secrets/github-app-key } }
  ci:            { name: github-actions }
  ciSecondary:   { name: teamcity, config: { baseUrl: https://tc.acme.internal, secretRef: secrets/teamcity-token } }
  tracker:       { name: jira, config: { baseUrl: https://acme.atlassian.net, secretRef: secrets/jira-token } }
  review:        { name: copilot-review }
  artifactStore: { name: azure-blob, config: { container: ff-artifacts } }
  recordStore:   { name: postgres, config: { connectionSecretRef: secrets/ff-db } }
  secrets:       { name: internal-platform, config: { mode: oidc-planned } }
  notifications: { name: slack, config: { secretRef: secrets/slack-token } }
  credentialBroker: { name: github-app }

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

- Two CI providers are configured. The repository's `ci.mapping` selects which one
  a tier uses, so GitHub Actions and TeamCity coexist without special cases.
- The `os-windows` pool is declared but has no provider in version 1. A run needing
  Windows fails at run-start capability negotiation with a clear message.
- The payload codec is configured because the organisation requires encrypted
  payloads. It is a configuration choice, not a code change (section 3.5).

---

## 11. Open questions

1. **Whether the organisation layer can be split into organisation and team.**
   Today it is one layer with optional team scoping through role mapping. A real
   split would add a fifth layer and another precedence step.
2. **How a repository opts into a stricter floor than its organisation.** Floors
   may be tightened by a lower layer, but it is not settled whether a *repository*
   may tighten a floor for itself, or only an organisation may.
3. **`ciSecondary` is a placeholder.** Two CI providers are configured by name, but
   the selection mechanism — per tier in `ci.mapping`, or per repository — is not
   settled.
4. **Whether `defaults.attempts` belongs at installation or organisation.** Today
   it is installation, but an organisation may reasonably want its own starting
   values.
5. **Budget configuration shape.** `cost` is not in version 1. When it arrives,
   whether budgets are per run, per step kind, or per organisation is open.
6. **Whether `dryRun` needs per-step support declared.** Today it is a run flag and
   steps honour it where they can. Whether a step must declare dry-run support is
   not settled.
