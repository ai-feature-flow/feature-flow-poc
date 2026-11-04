# M7 results: people, policy and steering

## Shared interfaces

What M7's five builders share, built first on `m7-interfaces` (W0), with fakes, contract suites and file splits, and no
feature implementations. The builders: W1 steering core, W2 policy and identity, W3 surfaces, W4 interactive sessions,
W5 reports and triage.

**What was built**

| Area | Types | Used by |
|---|---|---|
| Notifications (docs/06 section 3.10) | `INotificationsProvider` (send, post a decision; neither waits), `MessageRef`, `Notification`, `DecisionRequest`, `NotificationEvents` (the six event names and the de-duplication key: run, step, attempt, event). Fake `InMemoryNotifications`, kind `memory` | W3 |
| Session surface (docs/06 section 3.11) | `ISessionSurfaceProvider.PrepareAsync`, idempotent by session id; `SessionWorkspace`, `SessionFile`, `SessionHandle`. Fake `InMemorySessionSurface`, kind `memory` | W4 |
| Authentication | `IAuthenticator` (the first to accept a request decides its caller); `LocalTokenAuthenticator` (today's behaviour); `Caller` gains `Groups` and `Channel` (`api` or `chat`: the chat gateway acts as the mapped person, never itself); `IPeopleRoles.RolesFor(caller, RoleScope)` with `CredentialRoles` as the default; `ApiServices.Authenticators` and `PeopleRoles` | W2 (OIDC, roles), W3 (chat gateway) |
| Organisation configuration (docs/07 section 3) | `OrganisationConfig` gains roles, role mapping, notifications, timeouts, teams, library overrides and inputs; `PolicyConfig` gains `Overrides`, `RequireReview`, `AllowedToolsets`; `TeamConfig` (with `repositories`, LOG 204); `DecisionTypeItem.DefaultApproval`. The reader still refuses these keys | W2 fills; W1, W3 read |
| Override catalogue and wire | `Model.Overrides`: every key of docs/05 section 5, default roles, variants (`*_protected`, `grant_attempts_beyond`), `RolesFor` with `policy.overrides`. Update `override` with `OverrideArgs`; `AnswerArgs` gains `Artifacts` and `Revision`; `AbortArgs` gains `ClosePullRequests` | W1, W2, W4, W5 |
| Record store | `OverrideRecord` (appended), `NotificationRecord` (once per key, first write wins), `SessionRecord` and `SessionCheckRecord` (last write wins); queries `overrides`, `notification`, `notifications`, `session`, `sessions`, `session_checks`; SQLite and in-memory | W1, W2, W3, W4 |
| Wake signal | Signal `wake` with `WakeSignal(source, event, step?)`: a hint; the wait reads again | W3 |
| File splits | `ApiApp` split into runs, decisions, overrides, sessions, inbound, console and authentication files, each mapping its own routes; `RunWorkflow` partial with empty overrides, sessions and inbound files. Pure moves | All |

**Checks**

- Contract suites: `NotificationsContract` (4 tests), `SessionSurfaceContract` (2), `SteeringRecordsContract` (4), each
  against the fakes; the records suite also against SQLite.
- `OverridesTests` (11), the API's authenticator order and refusal (1), the fakes composing by world (1).

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| Fake notifications: a reply posted at the top level; an unknown channel accepted; a decision post without its wait id | `NotificationsContract` |
| Fake session surface: a new handle on every prepare; instructions not naming the session | `SessionSurfaceContract` |
| In-memory store: a notification's last write wins; an override replaced by id; sessions of any run | `SteeringRecordsContract` |
| SQLite store: notifications replaced; sessions first-write-wins; checks of any session; overrides of any run | `SteeringRecordsContract` (SQLite) |
| The de-duplication key without the attempt | `SteeringRecordsContract` |
| The catalogue without admin implied, ignoring the organisation, missing a variant | `OverridesTests` |
| The API's last authenticator winning | `ApiTests` |

**Deviations and judgement calls:** LOG 201 (de-duplication in the engine), 202 (sessions; one answer payload for what a
person produced, triage included), 203 (one catalogue, one override update), 204 (authenticators; scoped roles; teams
gain repositories), 205 (wake signal; file splits).

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | The override update, wake signal and their handlers have no implementation yet: W1 (`override`), W3 (`wake`). Until then Temporal rejects an `override` update and holds a `wake` signal unhandled |
| 2 | `docs/07` should list `teams.<name>.repositories` once LOG 204 is approved |

## Report-time checks and triage

Branch `m7-reports` (worker W5), from `m7@10caae7` (the shared interfaces, PR #16).

### What was built

| Part | Where | What |
|---|---|---|
| Schema and structure checks | `Model/ArtifactSchema.cs`, `Model/ArtifactContent.cs`, `Model/LibraryItems.cs` | An artifact type's schema is read and checked by an in-house evaluator of a JSON Schema subset; a library using another keyword is refused when it loads. Format (YAML, strict JSON, Markdown), schema, and Markdown front matter, headings and identifiers. Errors name field, line and rule (LOG 246) |
| Report-time checks with correction turns | `Engine.Agent/AgentActivities.Artifacts.cs`, `AgentActivities.cs` | After a report is accepted, each artifact is copied out once and its stored bytes checked against its declared type, including the type's validators through the validator providers. A failure is a rejected report: the same session is continued with the errors, from the one allowance of `correction_turns`. The last report counts. `ArtifactSpec.Type` carries the declared type (LOG 246) |
| `document_rules` | `Providers.StdValidators/DocumentRules.cs`; std's `requirements`, `delivery_plan`, `work_list`, `triage` | The rules std's types state in prose: unique ids, a work list's dependencies known and acyclic, a finding triaged once (LOG 247) |
| A person's triage | `Api/ApiApp.Decisions.cs`, `Engine/RunWorkflow.cs` (`ToInput`, the answer validator), `Engine/Actions/Findings.cs`, `Cli/RunCommands.cs` | An answer carries artifacts (stored by the API, sent by reference); the validator refuses an answer missing what its outcome promises; `findings.triage` records the person whose recorded answer gave the triage as actor (F8). `GET /runs/{id}/findings`, `ff run findings`, `ff run answer ... --artifact` (LOG 248). The core's half is W4's commit `cbb9c6e`, cherry-picked unchanged |
| std routes to the person | `fragments/fix_loop.yaml`, `decision_types/standard.yaml`, `prompts/findings_triage.md` | An escalation or a declined blocking or demonstrated finding goes to `person_triage` (type `findings.triage`), applied by `apply_person_triage` (LOG 248) |
| M6 follow-up 5 | `Engine/Actions/SourceControl.cs`, `Findings.cs` | `scm.pull_request` posts the declines the ledger holds, under the declining triage's comment key; `post_declines` input (LOG 249) |
| M6 follow-up 6 | std's `findings_report`, review skill and task | A fixed vocabulary of 17 rules, enforced by the report-time check (LOG 250) |
| Fix | `Model/Yaml/YamlText.cs` | YamlDotNet's `InvalidOperationException` on some malformed flow collections is a parse error, not a crash: agent-written text reaches the parser now |

### Checks

| What | Test | Result |
|---|---|---|
| Every std type loads within the keyword subset; M6's malformed shapes (owner as text; a work list keyed by `id`) named by field and rule; each keyword; Markdown structure; the rule vocabulary | `Model.Tests/ArtifactContentTests` (29) | Pass |
| `document_rules`: one passing, six failing fixtures | `StdValidatorsContract` | Pass |
| **M6's malformed plan corrected in place in one session**, with std's real library and validators; a validator failure; turns exhausted; an acceptance not taken after its artifact was rejected; missing validator or type stops the step for a person; untyped artifact unchecked | `Engine.Agent.Tests/ArtifactCheckTests` (7) | Pass |
| The same through Claude Code in Docker with a scripted model: the engine's message is the continued session's next turn | `Integration.Tests/AgentContractTests.An_artifact_that_breaks_its_type_...` | Pass |
| The person as actor; an agent's or another decision's artifact with the same content; a refused answer; ledger declines posted at the pull request's opening, once; `post_declines: false` | `Engine.Tests/FindingsActionTests` (+6) | Pass |
| Answer artifacts stored and sent by reference as the authenticated actor; malformed ones refused before anything is recorded; the ledger read | `Api.Tests/ApiTests` (+5) | Pass |
| std re-drive: person declines, reopens, triages invalidly, escalates (from the triager and the verifier) | `library/std/tests/ticket_person_*.yaml`, `ticket_triage_to_person`, `ticket_verifier_to_person` | Pass |
| End to end: a refused answer without the triage, the ledger read and a decline through the CLI recorded as `tester`, the ticket merging without a fix | `Integration.Tests/FindingsLoopTests.A_person_triages_...` | Pass |
| **Live, haiku on the fixture**: told to write notes without their required heading, it reports; the engine rejects the stored file and continues the session; haiku fixes it and reports again | `Live.Tests/LiveAgentTests.An_artifact_that_breaks_its_type_...` | Pass, 1 m 27 s: reports accepted, rejected, accepted; 2 sessions; 5 model requests. Nothing left on the fixture (GitHub API). A first attempt stalled before any session started (0 model requests) and timed out; not diagnosed |

Counts on `m7-reports` before the PR: default suite **1045 passed, 0 failed** (21 projects); integration
**80 passed** (Temporal on port 8633, Docker); Release build 0 warnings. std's re-drive tests 33.

### Controls

Each mutation made the named tests fail; each was restored and the tests passed again.

| Mutation | Failed |
|---|---|
| Content check off (twice: in-memory and through Claude Code) | `ArtifactCheckTests` M6 plan; `AgentContractTests` artifact test |
| Type validators off | `ArtifactCheckTests` validator and no-provider tests |
| First accepted report wins (the old rule) | `ArtifactCheckTests` stale acceptance |
| `additionalProperties`, `type` or the keyword check ignored; the new YAML catch removed | `ArtifactContentTests` (the matching cases) |
| `document_rules` cycle detection or triage duplicates off | `StdValidatorsContract` |
| Triage actor always the step; from any decision's wait; refused answers counted | `FindingsActionTests` (the matching cases) |
| std's escalation routed back to the exit | 4 std re-drive scenarios |
| The answer validator off; `ToInput` dropping the answer's artifacts | `FindingsLoopTests` end to end |
| The API dropping the stored references | `ApiTests` |
| Ledger declines not posted at opening; posted under the pull request step's key; `post_declines` ignored | `FindingsActionTests` (the matching cases) |
| The rule enum made an annotation | `ArtifactContentTests` vocabulary |

### Deviations and judgement calls

- LOG 246: the check runs on the agent worker after the report is accepted, in a continued session, not inside the
  `report` tool call: the endpoint cannot see the sandbox, and the sandbox client is on the agent's side. The schema
  evaluator is in-house (a ~15-keyword subset), refusing other keywords at load. `ArtifactSpec.Type` is activity input
  only, so no patch marker. M3's scripted runtimes without a sandbox are not checked.
- LOG 247: std's planners, triagers and verifiers now need a validator provider on the agent worker (as LOG 177).
- LOG 248: the person's answer is not schema-checked at the API (it does not hold the plan); `findings.triage` reads it
  and sends a bad one back. Answers are refused without the promised artifacts in the update validator only, which
  replay does not run.
- LOG 249, 250: M6 follow-ups 5 and 6.
- W4's commit `cbb9c6e` (a decision answer carries artifacts) is cherry-picked unchanged, so the two branches merge
  cleanly. Shared files touched with small additions: `RunWorkflow.cs` (`ToInput`, the validator, `Contract`),
  `ApiApp.Decisions.cs`, `RunCommands.cs`, `Steps.cs` (`ArtifactSpec.Type`), `Validator.cs` (`ValidatorProviders.Find`),
  `Validation.cs`, `StepStartChecks.cs`, `FindingsLoopTests.cs`, `ApiTests.cs` (an artifact store in its world).

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | `artifact.import` (and W4's session submission) can use `ArtifactContent.Check` and the type's validators the same way; M6 follow-up 8 |
| 2 | A person's triage artifact is not written to the record store's artifact index (the core holds it, so `findings.triage` reads it) |
| 3 | The web console (W3) could show the ledger and take a triage; today it is the API or the CLI |
| 4 | A decline from a triage with `post_declines: false` is still posted when the pull request later opens, unless `scm.pull_request` says false too (LOG 249) |
