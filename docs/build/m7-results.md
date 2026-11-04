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
