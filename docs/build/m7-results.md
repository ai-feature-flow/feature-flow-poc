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

## Notifications, inbound and console

W3, branch `m7-surfaces`: Slack notifications, the inbound chat gateway, inbound webhooks and the minimal console.

**What was built**

| Area | What |
|---|---|
| Slack provider (`Providers.Slack`) | `chat.postMessage` for messages and thread replies; decisions with their choices as buttons naming run, wait and choice, and a console link. Every Slack answer maps to docs/06 section 1.1. Its receiving side verifies Slack's request signature and a five-minute age, and maps the Slack user to a person by the installation's `users` setting (LOG 227). Registered in every host |
| Engine notifications (`RunNotifier`) | Sent from the record activity, never workflow code: an opened wait is `decision_needed`, a closed one `resumed`, an ended run `completed` or `failed`. Routed and filtered by `notifications`; once per key (LOG 201); one thread per root run per channel; a failed, throwing or hanging send never affects the run (LOG 226) |
| Chat gateway | `POST /inbound/chat/{source}`: verified by the provider, then the API's own answer call as the mapped person, channel `chat` (LOG 228) |
| Webhooks | `POST /inbound/events/{source}`: GitHub deliveries verified by `X-Hub-Signature-256`; a `wake` to each running run naming the repository; once per delivery (LOG 230). The workflow's `wake` handler restarts the wait's execution so it reads again (patch `m7-wake`, LOG 229) |
| Console | `GET /console`: a static page reading and answering through the API with the person's token; `GET /runs/{id}/plan` gives the readable form with step statuses (LOG 231) |

**Checks**

- Slack: the frozen `NotificationsContract` against the provider and a fake Web API; 20 more tests (buttons, error mapping, signing, mapping, factory). A Live contract is written, not run.
- `RunNotifierTests` (6): routing and filtering, a restart sends nothing again, threads with a child posting first, `resumed` naming the answer, failure isolation, no provider.
- `GitHubWebhookTests` (3), `InboundTests` (4) and `ConsoleTests` (2), on the API test host.
- `SurfacesTests` (2, Temporal): a decision posted with buttons, a signed press answered as the mapped person, the run resuming and completing in one thread; a ten-minute wait that a forged delivery leaves alone, a wrong event wakes to read again with no outcome and no attempt used, and only the true event ends.
- Replay: a new history `wake` holds the `m7-wake` marker.
- The console was looked at once in a browser (Playwright) against a live engine: runs, a run's plan, timeline and decision, answered with its button. The look found nested lists rendered as text, now fixed.

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| Slack: no age check; signature without the body; `thread_ts` dropped; an unmapped user mapped; `not_in_channel` unmapped | Slack provider tests, contract |
| Notifier: de-dup off; no thread; a child's first message not starting the root's thread; `events` ignored; an exception escaping; no `WaitAsync` for a provider ignoring its token; every intervention choice as a button | `RunNotifierTests` |
| Wake: does nothing; the event decides (completes the wait without a read); the patch marker call removed | `SurfacesTests`; replay of `wake` |
| Webhooks: de-dup off; signature unchecked | `InboundTests`, `SurfacesTests`, `GitHubWebhookTests` |
| Chat: the gateway answering as itself; an unmapped user answering | `InboundTests`, `SurfacesTests` |
| Console: open waits ignored; the plan not behind authentication; no CSP; run data placed with `innerHTML` | `ConsoleTests` |

**Deviations and judgement calls:** LOG 226 (what the engine sends and when), 227 (Slack users by configuration), 228 (the gateway makes the ordinary answer call; `Inbound.cs` added to the abstractions), 229 (a wake reuses the hand-over's interruption), 230 (webhooks; in-memory delivery de-dup), 231 (the console), 232 (reminders not built), 233 (test harness hook, test library, history).

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | A decision's wait records the word `decision` as its reason, not its rendered prompt, so Slack and the console show no question text. The workflow should record the prompt (W1's file) |
| 2 | Reminders on open waits (LOG 232): a workflow timer and a configured interval |
| 3 | `stalled` and `stuck` are sent once the engine has those states |
| 4 | Once W2 merges: a test that a chat answer to a `from_chat: false` type is refused, and `notifications` read from the organisation file instead of the harness hook |
| 5 | The console needs an OIDC sign-in once W2's OIDC lands; today a person pastes a token |
| 6 | A woken run is chosen among the 200 most recently updated runs; a query of running runs by repository would remove the limit |
| 7 | The Slack message could be updated (`chat.update`) when the wait is answered, so a stale button is visibly closed |
