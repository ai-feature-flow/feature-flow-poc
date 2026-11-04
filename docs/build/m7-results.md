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

## Interactive sessions

W4, on `m7-sessions`: a decision's interactive session (docs/04 section 4.4) with local hand-off (RW18), pre-submit checks
with parity (docs/09 section 4.2), and submission. Done-when 2, "a person runs a project's requirements session on
their machine and submits", is prepared for the live run (how-to below).

**What was built**

| Piece | What |
|---|---|
| Opening | When a decision with `session` opens (patch `m7-sessions`), the run makes branch `ff/session/<run>/<wait>` in the session's repository at the run's work branch, else the parent branch; prepares it through the session surface (new kind `local`, the hand-off); stores the workspace document (prompt, files, drafts, context, checks, instructions); records the session `prepared`. Drafts come from consumed artifacts, else the step's last submission (LOG 237) |
| Reaching it | API `/runs/{id}/sessions`, `/sessions/{sid}` (fetch), `/sessions/{sid}/checks`, `/submit`, `/abandon`, as the authenticated person. CLI `ff session list | fetch [--out] | check <step> | submit | abandon`, and `ff session mcp`, a stdio MCP server with `session_fetch`, `session_check`, `session_submit` for the person's agent tool (LOG 243) |
| Pre-submit checks | Update `session_check`. The step's call is built by the one function the step itself starts with (`CallOf`), with the session's files imported at the commit standing in for the decision's artifacts, and runs through the same activity. Both the check and the step record a digest of what they were given (`CheckInputs`). Engine-action steps only (LOG 238) |
| Submission | An answer carrying the commit (`Revision`): the run imports the files with `artifact.import` and the decision takes the submitting outcome with them as its artifacts, the person as actor (F8). A plain answer cannot take that outcome; abandoning, or any other answer, closes the session (LOG 239, 240) |
| Core | `DecisionAnswered` carries optional artifacts, exactly those the outcome promises; the re-drive runner answers session decisions with them |

**Checks**

- `SessionRunTests` (integration, memory world): `project_delivery` reaches `define`; round 1 fails its pre-submit check and
  is submitted anyway, `check_requirements` finds it invalid and a second session opens seeded with it; round 2 is done as a
  person would (`ff session fetch --out`, the check through `ff session mcp`, `ff session submit`), passes, and the run
  reaches `approve`. In both rounds the check's digest equals the step's; at different commits they differ. The submitter
  is the authenticated caller although the body names another. A plain `submitted` answer is refused; abandon ends the run
  `rejected` and closes the session.
- `CheckInputsTests`: the digest ignores who the step is, and changes with the task, tools, role (model) or files.
- `SessionAnswerTests` (core), `SessionApiTests` (who may act; full commits; closed sessions), the local surface against
  `SessionSurfaceContract`, CLI usage.

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| The check keeps the later step's own artifacts from an earlier execution | `SessionRunTests`: round 2's digests differ |
| The check is given another contract, or another form | `SessionRunTests`: digests differ |
| The check is not given the session's files | `SessionRunTests`: the check fails |
| The digest leaves out the scope (the files, so the commit) | `SessionRunTests`: two commits' checks are equal |
| The digest includes the step's identity | `CheckInputsTests` |
| The core accepts any artifact set on a decision | `SessionAnswerTests` |
| A plain answer may take `submitted` | `SessionRunTests` (abandon test) |
| The submitter is taken from the request body | `SessionRunTests` |
| The CLI and MCP check `HEAD` instead of the commit named | `SessionRunTests` |
| Any authenticated caller may act on a session; a closed decision's session may be acted on | `SessionApiTests` |

**Deviations and judgement calls:** LOG 236 (the person is whoever takes the session), 237 (branch plus fetched document;
no commit), 238 (checks run the step's own builder; engine-action steps only), 239 (the run imports a submission;
`AnswerArgs.Artifacts` stays null), 240 (only a submission takes the importing outcome), 241 (full commits), 242 (new wire
file), 243 (`ff session mcp`), 244 (bounded retries), 245 (no session in a dry run).

**Done-when 2: the live run**

1. **Where**: `project_delivery`'s session works in `params.planning_repository`, by default the run's first repository.
   Start with `--repo ai-feature-flow/feature-flow-example --param project_name=m7-live`: the person writes
   `projects/m7-live/requirements.yaml` and `projects/m7-live/architecture.md` on `ff/session/<run>/<wait>`.
2. **The person**: any caller holding a role that may answer `project.requirements_session` (`approver` by default). With
   the local token, run a second API host on the same Temporal, record store and artifact store, with its own
   installation file whose `api.auth` names the person (for example `actor: m7-person, roles: [approver]`) and its own
   token; with OIDC, the person's own login. The session records whoever submits.
3. **Steps**: start the run as the starter; `ff run wait <run> --for wait`; then as the person
   `ff session list <run>`, `git fetch origin <branch> && git switch <branch>` in a clone of the fixture,
   `ff session fetch <sid> --out ../ff-session`, write and commit both files, `git push origin <branch>`,
   `ff session check <sid> check_requirements`, `ff session submit <sid>`. The run reaches `approve`. Or point the
   person's Claude Code at `ff session mcp` (with `FF_API_URL` and `FF_API_TOKEN` set) and let it do the same.
4. **Leave nothing behind**: abort the run at `approve` (later steps plan epics and open pull requests); delete every
   `ff/session/<run>/*` branch the run made (`git push origin --delete ...`). Nothing else is written to the fixture
   before `approve`.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | Submission checks only what `artifact.import` checks; call the artifact type check (W5's report-time check, LOG 176) on submitted files once it lands (LOG 239) |
| 2 | Pre-submit checks of agent steps (a reviewer of a spec): their own hidden reference, questions and step token (LOG 238) |
| 3 | The session endpoints authorise with the decision's roles as `answers` does today; when W2's approval rules (team scope, `from_chat`) land, apply the same to `ActableAsync` |
| 4 | Session branches are not deleted when a session ends; a run's clean-up could remove them |
