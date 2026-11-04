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

## Steering core

What W1 built on `m7-steering`: a person's run-time overrides in the interpreter and the run workflow (docs/05 section 5),
breakpoints, mid-run library overrides, the gaps in agent proposals (F3, F10), and the two recoveries M6's live runs
needed. Everything that old histories could reach is behind the patch marker `m7-steering`.

**What was built**

| Area | What |
|---|---|
| The `override` update | `RunWorkflow.Overrides.cs` carries out `breakpoint`, `skip`, `amend`, `change_role`, `grant_attempts`, `override_library` and `abort` through the core, with the actor the API authenticated (F8). It refuses protected content or a grant beyond the hard ceiling unless the caller holds the variant (`skip_protected`, `amend_protected`, `override_library_protected`, `grant_attempts_beyond`), names the protected steps in its reply, is idempotent by override id, and records each on the timeline. Answers and recoveries stay on the `answer` update (LOG 203) |
| Breakpoints | The run pauses before the step and opens a `<level>.breakpoint` wait, answered with `continue` or any recovery; it holds until cleared (LOG 206) |
| Skips, grants, role changes, amends | A skip ends the step with a chosen outcome, using no attempt (LOG 207). One grant mechanism for the override and the recovery (LOG 208). `change_role` is a person's patch; `amend` may omit `resume_at` and never changes a role (LOG 209) |
| Mid-run library override | Content stored by hash, the run's lock rewritten (its last lock record is the run's lock), future steps resolve the new content while steps in flight keep theirs, running children are told by signal, later children inherit the lock; a lock entry with actor, reason, old and new hash, steps in flight and protected steps affected (LOG 212) |
| Agent proposals | An agent patch that routes around a protected step, or falls under the organisation's `planning.require_review`, awaits a person (LOG 210, 211). An agent's library override is always a patch approval, applied as the approver's (LOG 213) |
| Recoveries | Going to a failed branch of a decided `parallel` or chain reopens its join, so the run can complete (LOG 214). An abort may close the run tree's open pull requests (LOG 215) |
| Re-drive tests | The workflow test format gains `overrides`, `answers` and `expect.skipped` (docs/09 section 4.3); std gains `ticket_breakpoint_before_merge`, `ticket_skip_protected_analysis` and `epic_child_rerun` |

**Checks**

- `SteeringTests` (21, interpreter core), `RequireReviewTests` (1), the three std re-drive tests (`ff test library/std/tests`, 25).
- `SteeringRunTests` (7, Temporal): a breakpoint and a protected skip; amend, role change and grant within their variants; a
  library override reaching a later step and a running child while a step in flight keeps its task; an agent's library
  override approved by a person; re-running a failed child of a join; an abort closing the run tree's pull requests (and
  one leaving them); an override sent while the run drains for a hand-over, carried into the next run.
- Two recorded histories, `steering_line` and `steering_rerun_branch`, replay; every older history still replays.

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| No pause at a breakpoint; a skip never applied; a decided join not reopened | `SteeringTests` and the matching std re-drive test |
| A protected skip, or a person's protected amend, without the variant; an amend changing a role; a grant above the hard ceiling | `SteeringTests` |
| Routing around a protected step allowed; the organisation's `require_review` ignored; breakpoints not carried in a snapshot | `SteeringTests` |
| Reopening a join with the marker's path off | `Replay.Tests` (`steering_rerun_branch` does not replay) |
| Every variant treated as held | `SteeringRunTests`: the protected skip is not refused |
| The core not resolving from the new libraries | `SteeringRunTests`: the review still gets `REVIEW-V1` |
| Pull requests never closed; children not told to close theirs | `SteeringRunTests`: the abort test |
| Breakpoints not carried across a hand-over | `SteeringRunTests`: the hand-over test's run never pauses |

**Deviations and judgement calls:** LOG 206 to 215. Additive changes outside the per-area files: `RunPolicy.RequireReview`
(`Wire.cs`, not in the frozen list), `StepReport.LibraryOverride` and the scripted runtime's `library_override`.

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | W2's API: pass the caller's variant keys in `OverrideArgs.Variants`, and `close_pull_requests` from the abort request into `AbortArgs.ClosePullRequests` (today's endpoint does not) |
| 2 | An MCP tool for agents to propose a library override (and a patch), so real runtimes can use LOG 213's path |
| 3 | `run.child`'s `run_gate: manual` (a breakpoint before a child's first step) is not built |
| 4 | F1 at patch time: a patch adding an engine merge into a default branch is caught only at merge time (`scm.merge` re-checks), not when proposed |
| 5 | An abort's hold on an action that sends no heartbeat waits for the action to end before the run closes its pull requests (M3 follow-up 9) |
| 6 | From R1, low: a replaced or inserted container whose inner agent steps change role is not caught by the role checks (top-level `agent.role` only, pre-existing); branches of a `protected: true` parallel are not themselves protected (std protects fragments, which propagate the flag); a protected step inside one branch of a parallel is checked through its parallel, not path by path (LOG 210); an `override_library` sent twice with one id while the first awaits its activity records two entries (harmless; W2 can use the override id as the update id) |

**Review fixes (R1, changes-required)**

| Finding | Fix | Control (mutation that made the check fail) |
|---|---|---|
| F1: an agent patch could leave a protected step unrun by a never-true guard, a bypass one step earlier, `resume_at`, or a fake guard | One reachability check replaces route-edge matching (LOG 210); the four probes are tests | No protected step tracked; guarded routes followed by their first entry only |
| F2: a breakpoint plus go-to left a protected step unrun without `skip_protected` | A go-to or re-plan that leaves one unrun is refused; skip it first (LOG 206) | The refusal off |
| F3: an amend's `resume_at` forked a running run | Refused at the override update (LOG 209) | The refusal off |
| F4: a library override reached children's protected steps | Needs `override_library_protected` in a run that starts children (LOG 212) | Children not counted |
| F5: a steerer approving an agent's proposal reached what only an administrator may change | std's `*.patch_approval` defaults to `admin` (LOG 213) | std back to `steerer` |
| F6, F7: ended children's pull requests left open; any `pull_request` output counted | Children answer a `pull_requests` query; only `scm.pull_request` and `artifact.publish` count (LOG 215) | Children not read; the action filter off |
| F8: a skip on a pre-M7 run was accepted, then ignored | Refused (LOG 207) | The refusal off |
| Untested: the lock across a hand-over; a carried `PastBreakpoint` entry and breakpoint `clean_up`; a skip during a patch approval; a reopened branch's status | Tests added | Each removed in turn |
