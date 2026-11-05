# Configuration

Status: draft, 2026-10-31.

This document owns the configuration files: their layers, their keys and three
deployment profiles. It lists settings only. What a policy setting means is
owned by `docs/05`; what a manifest setting does is owned by the step that reads
it (`docs/04`). Keys are snake_case (RW12).

---

## 1. Layers

| Layer | Where | Set by | Holds |
|---|---|---|---|
| Installation | Deployment configuration | Operator | Temporal, workers, provider instances, API authentication, observability, engine defaults for limits |
| Organisation | A configuration file or service, with team overrides | Organisation | Policy and caps (`docs/05`), people roles, agent role mapping, libraries, notifications routing |
| Repository | `feature-flow.yaml` at the repository root | Repository maintainers | How to set up, test, analyse and run the repository; library additions |
| Run | The run request, through the API | Requester | Template, parameters, repositories, limit overrides |

Precedence follows `docs/00` section 3: the most specific wins, within
organisation caps and engine floors. A key set in a layer that may not set it is
a validation error, never ignored.

Teams are not a fifth layer: team overrides live in the organisation file and
may tighten organisation values, never relax them. A team may also add holders
to a people role for its own runs (LOG 217). Teams are optional and frozen for
version 1 (LOG 262): a solo or single-team organisation leaves `teams` out.

---

## 2. Installation

```yaml
api_version: feature-flow/v1
kind: installation

temporal:
  address: temporal.internal:7233
  namespace: feature-flow-prod
  auth: { mode: api_key, secret_ref: secrets/temporal_api_key }
  tls: { enabled: true }
  payload_codec: { name: aes_gcm, key_ref: secrets/payload_key }   # optional
  expected_retention: 30d        # checked at startup; a mismatch warns

providers:                       # every type: named instances, one default
  sandbox:        { default: kubernetes, instances: { kubernetes: { image: "registry.internal/feature-flow-sandbox:1.0", namespace: feature-flow-sandboxes } } }
  agent_runtime:
    default: claude_code
    instances:
      claude_code: { model_access: { mode: proxy, key_ref: secrets/anthropic_key } }
      copilot_cli: { model: claude-haiku-4-5, model_access: { mode: proxy } }
  vcs:            { default: git, instances: { git: {} } }
  code_host:      { default: github, instances: { github: { app_id: 12345, secret_ref: secrets/github_app_key, webhook_secret_ref: secrets/github_webhook } } }
  ci:             { default: github_actions, instances: { github_actions: {}, teamcity: { base_url: https://tc.internal, secret_ref: secrets/teamcity } } }
  tracker:        { default: jira, instances: { jira: { base_url: https://acme.atlassian.net, secret_ref: secrets/jira, project: ACME, status_map: { in_progress: "In Progress", code_review: "Code Review", done: "Done" } } } }
  findings_source: { instances: { copilot_review: {}, sonarcloud: { secret_ref: secrets/sonar } } }
  validator:      { instances: { std: {} } }
  toolset:        { instances: { jira: { url: https://mcp.internal/jira }, confluence: { url: https://mcp.internal/confluence } } }
  notifications:
    default: slack
    instances:
      slack:
        token_ref: secrets/slack_bot
        signing_secret_ref: secrets/slack_signing
        console_url: https://feature-flow.internal/console
        users: { U0123ABC: { actor: alice, groups: [acme-leads] } }
  session_surface: { default: local_handoff, instances: { local_handoff: {} } }
  artifact_store: { default: s3, instances: { s3: { bucket: acme-ff-artifacts, region: eu-west-1 } } }
  record_store:   { default: postgres, instances: { postgres: { connection_ref: secrets/ff_db } } }
  secrets:        { default: azure_key_vault, instances: { azure_key_vault: { vault: acme-kv } } }
  credential_broker: { default: github_app, instances: { github_app: {} } }

api:
  base_url: https://feature-flow.internal
  auth: { mode: oidc, issuer: https://idp.internal, audience: feature-flow, role_claim: groups }

observability:
  otlp_endpoint: https://otel.internal:4317

defaults:                        # engine defaults for limits; not floors
  limits:
    agent: { attempts: 3, timeout: 4h, correction_turns: 2 }
    decision: {}
    action: { attempts: 3 }
    scm.merge: { attempts: 3 }
```

| Key | Notes |
|---|---|
| `providers.<type>.instances` | Several instances of one type may coexist (two CI systems, two agent runtimes). Other settings select one by name |
| `agent_runtime.*.model_access` | `proxy` (a proxy adds the key to model requests) or `per_run_key` (a key with a spend limit and expiry). Required for every runtime (F6). With `proxy`, `upstream` is the model API (default the Anthropic API) and `auth` how the key is sent: `api_key` (default) or `oauth` (a subscription token). LOG 91 |
| `agent_runtime.claude_code` | `model`, `executable`, and `engine_tools`: `false` offers the agent no engine tools, for SP3's file fall-back test, and is refused unless `development: true`. LOG 91 |
| `agent_runtime.copilot_cli` | The same four settings as `claude_code`. Copilot reaches the model through the same model proxy, as a custom Anthropic provider with the step's token; one proxy, configured by the default runtime's `model_access`, serves every runtime instance (LOG 293) |
| `sandbox.docker` | `image` (the sandbox image, `feature-flow-sandbox`), `user` (the agent's, default `node`), `docker` (the executable). Its network reaches only `agent_worker`'s `urls` and `model_base_url`, and the internet (LOG 291) |
| `sandbox.kubernetes` | `image`, `user`, `namespace` (default `default`), `cpu` (default 2) and `memory` (default 4Gi) limits per pod, and `context` (a kubeconfig context; omitted, the cluster the worker runs in). Its network is `sandbox-network-policy.yaml`, which must be applied in `namespace` first; the agent worker's pods carry `app.kubernetes.io/name: feature-flow-agent-worker` (LOG 294) |
| `agent_worker` | LOG 91: `listen` (the agent worker's endpoint), `urls` (its replicas as sandboxes reach them), `model_base_url` (the model proxy as sandboxes reach it; default the first of `urls` with `/model`), `token_key_ref` (the secret every replica signs step tokens with) and `sandbox_client` (the executable copied into each sandbox). How often commits are pushed (30 seconds), Claude Code's own model retries, the hook's hold (120 seconds) and the sandbox client's retry (10 minutes) are fixed defaults, not settings |
| `secrets.file.from_environment` | Secret names the `file` provider reads from the process environment, such as the solo profile's subscription token. LOG 91 |
| `endpoint` | Not in version 1: every runtime reaches its model through the one model proxy; a per-runtime upstream is a follow-up (LOG 293) |
| `code_host` identity | Must not be able to change branch protection (F5); checked at startup |
| `tracker.*.status_map` | Maps the library's abstract statuses to the tracker's (`docs/08` section 1). For `github_issues` (settings `repository`, `status_map`) a status maps to a label or to `closed` (LOG 184). For `jira` (settings `base_url`, `secret_ref`, `project`, `issue_type`, default Task, and `status_map`) a status maps to a Jira status name, default `in_progress` to "In Progress", `code_review` to "In Review" and `done` to "Done"; the secret holds `<account email>:<API token>` (LOG 301) |
| `observability.otlp_endpoint` | Where traces are exported with OTLP over gRPC: an http or https URL. Without it nothing is exported. Version 1 exports traces only; metrics and logs are later (LOG 305) |
| `notifications.slack` | `token_ref` (the bot token) and `signing_secret_ref` (verifies its buttons at `/inbound/chat/slack`) are secret references. `users` maps a Slack user id to the person its button presses answer as: `actor`, and `groups` or `roles` for the people roles (`docs/05` section 3.3). An unmapped user cannot answer. `console_url` is linked from each message. LOG 227 |
| `code_host.github.webhook_secret_ref` | The secret GitHub signs webhook deliveries with; deliveries go to `/inbound/events/github` and only wake waits (`docs/02` section 2.3). LOG 228 |
| `defaults.limits` | Engine defaults per step form and per action. The organisation, workflow and step may override them within ceilings |
| `temporal.continue_as_new_events` | LOG 87: the history length at which a run hands over (`docs/02` section 3); default 5,120, at least 100 |
| `temporal.payload_codec` | Encrypts every payload, and failure messages, the engine sends Temporal. `name: aes_gcm` (the only codec); `key_ref` names a secret of 32 random bytes in base64 (`openssl rand -base64 32`). Replaying an encrypted history needs the same key. LOG 284 |
| `record_store.postgres` | `connection_ref` names the secret holding the connection string (`Host=...;Database=...;Username=...;Password=...`). The schema is created or migrated at first use. LOG 281 |
| `artifact_store.s3` | `bucket` (required, created by the deployment), `region` (default `us-east-1`), `endpoint` for a store other than AWS such as MinIO (path-style), and `access_key_ref` with `secret_key_ref`, set together; without them the AWS SDK's credential chain applies, such as an EKS service account's role. LOG 283 |
| `api.auth` | `local_token` (`token_ref`, `actor`, `roles`) or `oidc` (`issuer`, `audience`, `role_claim`, default `groups`, and `actor_claim`, default `sub`); with `oidc`, a `token_ref` keeps the local token working beside it. The actor `policy` is reserved for automatic answers (LOG 220, 222) |

Scaling (replica counts) is deployment configuration, not engine configuration. `workers` (a worker mode and agent
pools by sandbox capability) is not in version 1 and is refused at startup; every agent worker serves the one agent
queue (LOG 318). Each orchestration worker runs the stuck-run
check (`docs/02` section 8): with more than one replica, a `stalled`, `stuck` or `resumed` message may be sent once per
replica; one orchestration replica avoids that (LOG 307).

---

## 3. Organisation

```yaml
api_version: feature-flow/v1
kind: organisation
name: acme

libraries:                                  # docs/08 sections 3 and 4
  - { name: std, source: directory, path: /opt/feature-flow/library/std }
  - { name: acme_delivery, source: git, repository: "https://github.com/acme/ff-library.git", ref: main }

policy:                                     # meanings in docs/05
  approval:
    - { match: "*.merge_action", roles: [approver], min_count: 2, from_chat: false, auto: false }
    - { match: "*", roles: [approver], min_count: 1, from_chat: true, auto: true }
  merge:
    default_branch: { engine_may_merge: ["acme/poc-*"] }
    require_checks: { value: true, locked: true }
  attempts:
    ceiling: { per_step: 5, per_run: 40 }
    hard_ceiling: { per_step: 8, per_run: 60 }
  budgets:
    max_work_items: { ceiling: 30 }
  planning:
    allowed_roles: [planner, implementer, fixer]
    require_review: [{ match: "*:templates/project_*", value: true }]
  overrides:                                # default roles for run-time overrides (docs/05 section 5)
    skip_protected: [admin]

roles:                                      # people roles to identity-provider groups
  viewer:   { idp_groups: [acme-engineering] }
  approver: { idp_groups: [acme-leads] }
  steerer:  { idp_groups: [acme-leads] }
  admin:    { idp_groups: [acme-platform] }

role_mapping:                               # library agent roles to runtime and model
  planner:     { agent_runtime: claude_code, model: claude-sonnet }
  implementer: { agent_runtime: claude_code, model: claude-haiku }
  fixer:       { agent_runtime: claude_code, model: claude-haiku }
  reviewer:    { agent_runtime: claude_code, model: claude-opus }
  default:     { agent_runtime: claude_code, model: claude-sonnet }

notifications:
  routing:
    decision_needed: { channel: "#ff-decisions" }
    stuck:           { channel: "#ff-ops" }
    stalled:         { channel: "#ff-ops" }
    completed:       { channel: "#ff-runs" }
    failed:          { channel: "#ff-runs" }
  events: [decision_needed, failed, completed]

teams:
  payments:
    policy:
      approval: [{ match: "*.merge_action", min_count: 3 }]   # a team's rule only raises min_count (LOG 217)
    roles:
      approver: { idp_groups: [acme-payments-leads] }
```

| Key | Notes |
|---|---|
| `libraries` | Each a `name` and a `source`: `directory` with `path` (relative to this file), or `git` with `repository` (anything Git can clone, with the host's own Git credentials; a URL holding a credential is refused, LOG 289) and `ref` (a branch or tag; default the repository's default branch), fetched at each read (LOG 286). `package` is not in version 1 (LOG 287). The first library is the base for references without a library name |
| `policy.*` | Meanings in `docs/05`. A cap may be `{ value, locked }`; a bare value is an unlocked default. Which settings reach running runs is in `docs/05` section 8 |
| `policy.budgets.max_work_items.ceiling` | F11's ceiling, always locked: a root template asking more does not start; a root that sets no budget gets the ceiling (LOG 187) |
| `roles`, `teams` | People roles to groups. A run is in the scope of the team whose `repositories` match its repositories; a run spanning two teams is refused. In its scope a team's groups add holders to a role, and its approval rules may only raise `min_count` (LOG 204, 217) |
| `role_mapping` | Each library agent role's `agent_runtime` (an instance of the installation's) and `model` (default: that instance's own). `default` serves roles with no entry; with neither, the installation's default runtime runs the step with its own model. Fixed for a run when it starts (LOG 267, 295) |

`library_overrides`, `library_inputs`, `policy.toolsets` (whose only key was `allowed`) and `timeouts` are not in
version 1 and are refused at startup, whatever they hold (LOG 268 to 270, 273).

---

## 4. Repository: `feature-flow.yaml`

One file at the repository root, applying to every place the repository runs:
sandbox, local machine and CI. Read fresh from the branch at every step, and
recorded by hash (RW11). Other files (Dockerfiles, compose files) are referenced
by relative path.

```yaml
api_version: feature-flow/v1
kind: repository

environment:
  from: .devcontainer/devcontainer.json    # or inline: image, os, docker
  os: linux

setup:
  commands: ["git config core.hooksPath .githooks", "dotnet tool restore"]

after_changes:                             # run by the engine after an agent commits (docs/04 section 3.1)
  commands: ["dotnet format"]

test_tiers:
  - name: unit
    tags: [unit, fast]
    command: dotnet test tests/Unit --logger "trx;LogFileName=unit.trx"
    result: { format: trx, path: tests/Unit/TestResults/unit.trx }
    targets: [sandbox, local, ci]
  - name: integration
    tags: [integration]
    command: dotnet test tests/Integration
    result: { format: trx, path: tests/Integration/TestResults/*.trx }
    targets: [ci]
    ci: { instance: github_actions, job: integration.yml }
    prerequisites: { env: [APP_BASE_URL] }

static_analysis:
  analyser: sarif                          # sarif | sonarcloud | none
  command: dotnet build -warnaserror -p:ErrorLog=analysis.sarif
  sarif_path: analysis.sarif

integration_environment:                   # for test.integration
  image: mcr.microsoft.com/dotnet/aspnet:9.0
  start: dotnet run --project src/Api
  health: curl -f http://localhost:8080/health
  port: 8080
  ready_timeout: 10m

library:                                   # docs/08 section 4; read from the default branch only
  default_template: std:templates/ticket_delivery
  adds:
    skills: [{ name: acme_api_conventions, path: .feature-flow/skills/acme_api_conventions }]
```

| Key | Notes |
|---|---|
| `environment` | Inline, or a referenced `devcontainer.json`. The engine merges the two into the **resolved manifest**, which providers receive |
| `test_tiers[].tags` | What templates select by (`test.run` `tier_tags`), so a template never lists a repository's tiers |
| `test_tiers[].targets` | Where the tier may run. A tier that needs Docker or Windows normally lists only `ci` |
| `test_tiers[].prerequisites` | `env`: environment variable names the tier needs. `test.run` raises an intervention if one is missing where the tier runs, rather than reporting the tests as failed |
| `test_tiers[].result` | Parsed format and path. Without it the tier is judged by exit code only, and its tests cannot be claimed as evidence |
| `test_tiers[].ci` | The CI provider instance and the job (for GitHub Actions, a workflow file). The job runs from the default branch and must check out the revision it is given: the workflow contract is in `docs/06` section 3.5 |
| `library` | The only section read from the default branch rather than the working branch |
| `.feature-flow/` | Holds only the content files `library.adds` references |

Unknown keys are refused, not ignored, so a misspelt section stops the step
that reads it (from FF).

The first design's `merge.default_branch` and `branches` sections are removed:
F1 is organisation configuration (`docs/05` section 2), and branch naming is in
the template (`docs/03` section 1).

---

## 5. Run request

```yaml
template: std:templates/epic_delivery     # default: the repository's default template
parameters: { epic_id: ACME-42 }
repositories: [acme/api, acme/web]
parent_branch: ff/epic/acme-42            # optional; default each repository's default branch
limits:
  - { step: implement, attempts: 4 }        # within the organisation's ceiling
dry_run: false
```

`parent_branch` names the branch the run's work branch starts from and merges into, in every
repository of the run; it must exist. F1 still governs it when it is the default branch (LOG 137).

`tracker_issue` names the run's tracker issue (`run.tracker_issue`): agent steps read it as their ticket, and
declarative `tracker` updates move it (LOG 185, 186).

`dry_run: true` selects recording providers that record instead of act, chosen
by configuration. Workflow tests use the same mechanism (`docs/09`).

LOG 87: `continue_as_new_events` sets the hand-over threshold
for the run tree, so tests can hand over often. Only an administrator may set it, and
not below 100.

---

## 6. Profiles

The same code runs in every profile; only configuration differs.

| | Solo developer | Small company | Enterprise |
|---|---|---|---|
| Temporal | `temporal server start-dev` | Docker Compose, Helm, or Temporal Cloud | Temporal Cloud, one namespace per environment |
| Workers | Local processes | Kubernetes | Organisation Kubernetes, pools by capability |
| Sandbox | Local Docker | Kubernetes pod | Kubernetes pod; Docker and Windows tiers delegated to CI |
| Tiers that need Docker | A privileged container, permitted only in this profile | CI delegation, or a rootless container runtime where supported | CI delegation |
| Tracker, code host, CI | GitHub Issues, GitHub, GitHub Actions | Jira, GitHub, GitHub Actions | Jira, GitHub, GitHub Actions and TeamCity |
| Stores | Local filesystem, SQLite | S3 (or an S3 API store such as MinIO), Postgres | Organisation object store and database |
| Secrets | Environment or file | Cloud secret manager | Internal platform, short-lived |
| Credentials | The developer's token, used only by the engine | GitHub App | GitHub App |
| Library source | `directory` | `git` | `git` |
| API authentication | Local token | OIDC | OIDC |
| Default-branch merges | A person, unless the solo organisation file lists the repository | A person | A person; two approvers |

The solo organisation file can be four lines: one approver (the developer), one
library, and default caps. Every profile still keeps agents without push
credentials (F5) and model keys (F6).

---

## 7. Validation

| When | Checks |
|---|---|
| Startup | Schemas; provider packages present; secret references resolve (every `*_ref` setting, naming the key, never a value; LOG 308); Temporal reachable; expected retention (warn); every agent runtime has `model_access`; the code host identity cannot change protection; library sources reachable |
| Run start | The template validates (`docs/09`); every capability the plan needs is offered; caps respected; F1 for every merge step into a default branch, reading the default branch from the code host; branch protection exists on default branches; decision types known; library lock written |
| Each step | The manifest read from the branch and its hash recorded; capabilities checked again if the manifest changed |

A validation failure names the layer, the key and the reason. A run fails in
its first minute, not at step 40.

---

## 8. Open questions

None that block the next documents. Judgement calls made in this document are
in `docs/LOG.md`, entries 44 to 46.
