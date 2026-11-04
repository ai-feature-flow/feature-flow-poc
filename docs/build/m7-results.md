# M7 results: people, policy and steering

## Summary

M7 is done. People now steer runs, answer through Slack, a console or their own machine, and policy says who may do what.

- **Done-when 1:** every run-time override key (docs/05 section 5) has a test showing a role that may and one that may
  not use it, and each protected or beyond-ceiling variant has one too. A test reads those rows and compares them with
  the catalogue (`Model.Overrides`), so a new key without a test fails.
- **Done-when 2 (live):** a person ran `project_delivery`'s requirements session on their machine and submitted
  (`run-5a6000bc7f32`, `feature-flow-example`). The person was a second identity on its own API host. The run went on
  past the session with the person recorded as the actor; the run's starter was refused.
- **Verified-by:**
  - a request naming another actor in its body is recorded with the authenticated one;
  - a pre-submit check and the later step receive the same task, tools, model and commit (equal digests, live too).
- **The three items M6 asked M7 to take first** are built: report-time artifact checks with correction turns (LOG 246);
  a person triaging findings through a decision (LOG 248); re-running a failed branch of a finished parallel, and an
  abort closing the run's pull requests (LOG 214, 215).

How it ran: shared interfaces first (PR #16), five builders in parallel (PRs #17 to #21), then integration (`M7 integration: seams and live done-when`).
Each builder's pull request had an independent review that broke the code to show its tests catch it, and a re-check.

Tests on `m7-integration` at the end: 1,249 default (replay 29 of them), 100 integration, 29 std re-drive.

The sections below are each worker's own, in build order, then integration. They are followed by the coordinator's
decisions, the list for the product owner, and the consolidated follow-ups.

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

**Review fixes (R2, changes-required)**

| Finding | Fix | Control (mutation that failed the test) |
|---|---|---|
| F1 a team rule could relax by its roles or a broader pattern | A team rule sets only `min_count` and only raises it | The team rule governing whole; a team rule allowed `roles` (`OrganisationTests`) |
| F2 a run over two teams' repositories took the looser team | Refused at start (API, and run start for children with its own message, re-check N3) | The check off (`OverrideAuthorisationTests.Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams`); run start skipping it (`OneTeamRunStartTests`) |
| F3 the local token beside OIDC counted as a second person | A group-less credential may not answer a multi-person wait under OIDC | The refusal off (`OidcTests.The_local_token_is_not_a_second_person_beside_oidc`) |
| F4 approving a patch skipped `amend_protected` | Coordinator decision C10: W1 makes std's `*.patch_approval` default `admin` (W1's LOG entry); no API change | W1's |
| F5 a team's roles replaced the organisation's | A team adds holders in its own scope (coordinator's decision) | Team replacing the organisation's holders (`A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away`) |
| F6 the refresh on an unknown key did not take effect | Re-check N1: one `RequestRefresh` on an unknown key, no immediate retry; the next requests see the rotated key | No refresh (`OidcTests.A_token_signed_by_a_rotated_key_is_accepted_once_the_keys_are_read_again`) |
| F7 min_count race | Follow-up, LOG 221 | — |
| F8 / C7 a grant at an intervention needed only `grant_attempts` | Needs `recover` and `grant_attempts` | Either check off (`Granting_attempts_at_an_intervention_needs_recover_and_grant_attempts`) |
| M10, M11 passed by construction | Tests: a viewer may not start a run; the token's issuer is the configured one even when the metadata names another (the equality check is gone, `ValidIssuer` is the configured issuer) | Start needing only viewer; the issuer taken from the metadata |

The round-1 mutation script restored files with an older timestamp, so a later run could keep an earlier mutation's build.
After the fix, the round-1 controls were run again; each still failed its own test.

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

## Policy and identity

W2, branch `m7-policy`: the organisation file in full, people roles, approval rules, authorisation of every run-time
override, OIDC, and the effective policy view.

**What was built**

| Area | What | Where |
|---|---|---|
| Organisation file (docs/07 sections 3 and 7) | Every key read into the frozen model, unknown keys refused, caps as a value or `{ value, locked }`. People roles are the four of docs/05 section 3.3. A team's approval rule sets only `min_count`, and only raises it. Startup checks across the two layers name the layer, key and reason (LOG 223) | `Model/Configuration.cs`, `Hosting/StartupChecks.cs` |
| People roles | From identity-provider groups; in a team's scope its groups add holders. `admin` > `steerer` > `viewer`, `approver` apart. A run spanning two teams is refused at start (LOG 216, 217) | `Model/Approval.cs` (`PeopleRoles`), `Api/ApiAuthentication.cs` (`OrganisationRoles`) |
| Approval | One resolver for the API, run start and the view: the organisation's rule, the library's `default_approval`, the engine's; a team only raises `min_count`. `min_count` counts distinct people answering the same way, each recorded; `from_chat` against the caller's channel; no caller may act as `policy` (LOG 221, 222) | `Model.Approval`, `ApiApp.Decisions.cs` (`SubmitAnswerAsync`) |
| Override authorisation | `POST /runs/{id}/overrides` for the keys the `override` update carries; default roles, `policy.overrides`, and the variants the caller holds passed to the run. A recovery at an intervention is the `recover` override (LOG 218, 225). Reading needs `viewer`, starting a run `steerer` (LOG 219) | `ApiApp.Overrides.cs`, `ApiApp.Authorisation.cs`, `ApiApp.Runs.cs` |
| OIDC | Discovery, keys, issuer, audience, expiry and signature; actor and groups from configured claims; beside the local token (LOG 220) | `Api/ApiAuthentication.cs`, `api.auth` in the installation |
| Effective policy view | Per merge step (who merges, checks) and decision step (who answers, auto): request, cap, result, deciding setting. In `ff show --organisation <file> [--repo <r>]` and in the run start record as `policy` timeline entries, patch `m7-effective-policy` (LOG 224) | `Validation/EffectivePolicy.cs`, `Engine/Activities.cs` |
| In-run values | A run's auto-resolution rules follow the same order, fixed at start; check 22 uses the governing rule and warns where an unlocked rule makes `auto` have no effect (LOG 222) | `Engine/EngineServices.cs`, `Validation/RunStartChecks.cs` |
| CLI | `ff run override <run> <key> --reason <text> [--step <id>] [--change name=value]...`; `ff run abort --close-pull-requests` | `Cli/RunCommands.cs` |

**Checks**

- Done-when 1, the API side: `OverrideAuthorisationTests` gives every key of docs/05 section 5 a role that may and one
  that may not, the four variants (a steerer holds none, an administrator each), `policy.overrides` changing a key and a
  variant, and roles scoped by the run's team. A fake Temporal client keeps the delivered arguments, so "may" is shown by
  the update the run receives.
- Verified-by: a body asserting `mallory` is recorded and delivered as the authenticated caller, for answers, recoveries,
  aborts and every override (`OverrideAuthorisationTests`; through a real run in `PolicyRunTests`, Temporal :8333).
- `ApprovalTests` (min_count, chat, rule precedence, team rule, `policy`), `OidcTests` (in-process issuer: valid, expired,
  wrong audience, wrong issuer, foreign key, unsigned, no subject, `policy`, local token beside), `OrganisationTests`,
  `StartupCheckTests`, `EffectivePolicyTests`, `RunPolicyTests`, `ShowPolicyTests`, `PolicyRunTests` (the run start record;
  a team's rule deciding auto-resolution in a real run; the CLI's override and abort).
- Default suite 1,077 passed (replay 19); integration 81 passed (Temporal :8333).

**Controls** (each mutation made its check fail, then was restored)

| Mutation | Failed |
|---|---|
| A team rule allowed to relax `min_count`; team roles added to, not replacing, the organisation's; `admin` implying `approver`; organisation auto rules before the team's; an unknown `policy.overrides` key accepted | `OrganisationTests` |
| A `role_mapping` runtime unchecked at startup | `StartupCheckTests` |
| The actor taken from the body: answers (in both places it is set), overrides, aborts | `OverrideAuthorisationTests`, `OidcTests` |
| No role check; every variant passed; recovery by approval rule instead of `recover` | `OverrideAuthorisationTests` (19, 4 and 1 tests) |
| `min_count` counting a person twice, or an answer given another way; `from_chat` unchecked; `policy` allowed; roles not scoped by team | `ApprovalTests`, `OverrideAuthorisationTests` |
| OIDC lifetime, audience or issuer unchecked; an invalid token accepted; groups ignored | `OidcTests` |
| Run auto rules without library defaults or the team; check 22 silent on an unlocked refusal; the view resolving auto whatever the rule or ignoring `engine_may_merge`; `ff show` ignoring the team | `RunPolicyTests`, `RunStartCheckTests`, `EffectivePolicyTests`, `ShowPolicyTests` |
| The view not written to the run start record; in-run auto ignoring the team; the CLI dropping the step, splitting a change at its last `=`, never asking to close pull requests | `PolicyRunTests` |

**Review fixes (R2, changes-required)**

| Finding | Fix | Control (mutation that failed the test) |
|---|---|---|
| F1 a team rule could relax by its roles or a broader pattern | A team rule sets only `min_count` and only raises it | The team rule governing whole; a team rule allowed `roles` (`OrganisationTests`) |
| F2 a run over two teams' repositories took the looser team | Refused at start (API, and run start for children with its own message, re-check N3) | The check off (`OverrideAuthorisationTests.Starting_a_run_needs_a_steerer_and_a_run_may_not_span_two_teams`); run start skipping it (`OneTeamRunStartTests`) |
| F3 the local token beside OIDC counted as a second person | A group-less credential may not answer a multi-person wait under OIDC | The refusal off (`OidcTests.The_local_token_is_not_a_second_person_beside_oidc`) |
| F4 approving a patch skipped `amend_protected` | Coordinator decision C10: W1 makes std's `*.patch_approval` default `admin` (W1's LOG entry); no API change | W1's |
| F5 a team's roles replaced the organisation's | A team adds holders in its own scope (coordinator's decision) | Team replacing the organisation's holders (`A_team_adds_holders_for_its_own_runs_and_never_takes_the_organisations_away`) |
| F6 the refresh on an unknown key did not take effect | Re-check N1: one `RequestRefresh` on an unknown key, no immediate retry; the next requests see the rotated key | No refresh (`OidcTests.A_token_signed_by_a_rotated_key_is_accepted_once_the_keys_are_read_again`) |
| F7 min_count race | Follow-up, LOG 221 | — |
| F8 / C7 a grant at an intervention needed only `grant_attempts` | Needs `recover` and `grant_attempts` | Either check off (`Granting_attempts_at_an_intervention_needs_recover_and_grant_attempts`) |
| M10, M11 passed by construction | Tests: a viewer may not start a run; the token's issuer is the configured one even when the metadata names another (the equality check is gone, `ValidIssuer` is the configured issuer) | Start needing only viewer; the issuer taken from the metadata |

The round-1 mutation script restored files with an older timestamp, so a later run could keep an earlier mutation's build.
After the fix, the round-1 controls were run again; each still failed its own test.

**Deviations and judgement calls:** LOG 216 (role nesting, approver apart), 217 (team scope and overlays), 218 (an
intervention is answered as `recover`), 219 (viewer to read, steerer to start), 220 (OIDC), 221 (min_count recording),
222 (library default in the run's auto rules; unlocked warns; `policy` reserved), 223 (file read in full; keys read but not
yet enforced), 224 (effective policy view), 225 (override wire and CLI).

**Follow-ups**

| # | Follow-up |
|---|---|
| 1 | W1: the run refuses protected content or a beyond-ceiling grant without the variant in `OverrideArgs.Variants`; until its handler lands, Temporal rejects the `override` update (the API records the refusal). Auto-resolution's timeline entry gives the outcome but not the actor `policy` (docs/04 section 4.2) |
| 2 | W3, W4, W5: answers from the chat gateway, a session's submission and a person's triage should go through `SubmitAnswerAsync`, so approval, `from_chat` and `min_count` apply; the gateway's `Caller` needs the person's groups (or roles) for team scoping |
| 3 | Read but not enforced on this branch (LOG 223): `role_mapping`, `timeouts`, `library_overrides`, `library_inputs`, `toolsets.allowed`; check at integration which of the others W1 and W3 consumed |
| 4 | `merge.require_checks` locked on refuses only a literal `require_checks: false` at run start; an expression giving false at merge time is not checked against the cap (`scm.merge` does not read it). Older than M7 |
| 5 | Startup does not yet resolve the API's `token_ref` or other secret references (docs/07 section 7); a missing one shows as every local-token request refused |
| 6 | min_count: two answers at the same moment can leave the count short until another answer; re-read after recording (review R2 F7) |

## Interactive sessions

W4, on `m7-sessions`: a decision's interactive session (docs/04 section 4.4) with local hand-off (RW18), pre-submit checks
with parity (docs/09 section 4.2), and submission. Done-when 2, "a person runs a project's requirements session on
their machine and submits", is prepared for the live run (how-to below).

**What was built**

| Piece | What |
|---|---|
| Opening | When a decision with `session` opens (patch `m7-sessions`), the run makes branch `ff/session/<run>/<wait>` in the session's repository at the run's work branch, else the parent branch; prepares it through the session surface (new kind `local`, the hand-off); stores the workspace document (prompt, files, drafts, context, checks, instructions); records the session `prepared`. Drafts come from consumed artifacts, else the step's last submission (LOG 237) |
| Reaching it | API `/runs/{id}/sessions`, `/sessions/{sid}` (fetch), `/sessions/{sid}/checks`, `/submit`, `/abandon`, as the authenticated person. CLI `ff session list | fetch [--out] | check <step> | submit | abandon`, and `ff session mcp`, a stdio MCP server with `session_fetch`, `session_check`, `session_submit` for the person's agent tool (LOG 243) |
| Pre-submit checks | Update `session_check`. The step's call is built by the one function the step itself starts with (`CallOf`), with the session's files imported at the commit standing in for the decision's artifacts, and runs through the same activity. The activity returns a digest of exactly what it received (`CheckInputs`), which the check and the step record. `artifact.validate` steps only (LOG 238) |
| Submission | An answer carrying the commit (`Revision`): the run imports the files with `artifact.import` and the decision takes the submitting outcome with them as its artifacts, the person as actor (F8). A plain answer cannot take that outcome; abandoning, or any other answer, closes the session (LOG 239, 240) |
| Core | `DecisionAnswered` carries optional artifacts, exactly those the outcome promises; the re-drive runner answers session decisions with them |

**Checks**

- `SessionRunTests` (integration, memory world): `project_delivery` reaches `define`; round 1 fails its pre-submit check and
  is submitted anyway, `check_requirements` finds it invalid and a second session opens seeded with it; round 2 is done as a
  person would (`ff session fetch --out`, the check through `ff session mcp`, `ff session submit`), passes, and the run
  reaches `approve`. In both rounds the check's digest equals the step's; at different commits they differ. The submitter
  is the authenticated caller although the body names another. A plain `submitted` answer is refused; abandon ends the run
  `rejected` and closes the session.
- After the review: a session decision is a session only if the run opened one behind `m7-sessions`; `project_session`
  (with the marker) and `project_session_pre_m7` (recorded on the code before sessions, answering `define` with a plain
  `submitted`) replay. A dry run's decision takes a plain answer. A check of a step other than `artifact.validate` is
  refused though `checks_from` names it (`m7:templates/session_checks`). The run's own refusals of an update sent
  directly (artifacts given; not a full commit; abandon with a revision). The CLI refuses an id that is not a session's.
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
| Review F1 undone (any decision whose plan declares `session` is one) | Replay of `project_session_pre_m7` (nondeterminism); the dry-run test |
| A session path changed (its closing record not written) | Replay of `project_session` |
| The step's activity, or the check's, given another call or scope than built (timeout; contract; form; work branch) | `SessionRunTests`: digests differ |
| Any action step may be checked | `SessionRunTests` (`session_checks`) |
| The run accepts artifacts given on a submission; `artifact.import` accepts a revision that is not a commit | `SessionRunTests` (abandon test) |
| The CLI sends any session id | `CliAppTests` |

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
| 3 | Done at the merge with W2: reading a session needs `viewer` in the run's scope; acting on one needs the approval rule for its decision type (roles in the run's scope, `from_chat`), and a submission or abandonment goes through the one answer path (`SubmitAnswerAsync`, with `min_count`) |
| 4 | Session branches are not deleted when a session ends; a run's clean-up could remove them |
| 5 | A load check refusing `checks_from` that names a step other than `artifact.validate`; today the check is refused when asked (LOG 238) |
| 6 | Checks are not cancelled when the run ends, and are not capped per session; with `artifact.validate` only they are short (review F6) |
| 7 | The guard refusing a second answer while a submission imports (`submitting`) has no test (review F8, mutation H) |
| 8 | A session that fails to open can only be abandoned; a re-open action would be gentler (review design note) |

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
| 4 | Done after the merge with W2: `InboundTests` shows a chat answer to a `from_chat: false` type is refused. Still open: the surfaces tests set `notifications` through the harness hook, not the organisation file W2 now reads |
| 5 | The console needs an OIDC sign-in once W2's OIDC lands; today a person pastes a token |
| 6 | A woken run is chosen among the 200 most recently updated runs; a query of running runs by repository would remove the limit |
| 7 | The Slack message could be updated (`chat.update`) when the wait is answered, so a stale button is visibly closed |

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

## Integration

Worker W6, branch `m7-integration`, from `m7` with PRs #17 to #21 merged. Temporal on port 8733. Judgement calls LOG 256
to 261, unreviewed.

### Seams fixed

Each fix is the simplest that closes the seam, has a test, and the test a control (a mutation that made it fail, then
restored).

| # | Seam | Fix | Test (control) |
|---|---|---|---|
| 0 | CI flake: a pre-submit check sent while the run hands over to its next run is refused (409, by design), and the session test sent one | `ff session check` and `ff session mcp` try that one refusal again for up to 30 s; the test helper does too (LOG 261) | `SessionClientTests` (no retry fails one; retrying every refusal fails the other) |
| 1 | A decision's wait recorded the word `decision`, so Slack and the console showed no question (W3 follow-up 1) | The wait records the rendered prompt (patch `m7-decision-prompt`, LOG 257); history `decision_prompt` | `SurfacesTests`: the Slack message and the wait record show the prompt (the core's reason fails both) |
| 2 | The surfaces test set `notifications` through a harness hook, not the organisation file (W3 Q4) | The test writes `notifications` in the organisation file; the hook is gone (the harness takes organisation lines and `from_chat`). The `from_chat: false` refusal through the Slack gateway is W3's `InboundTests` test | `SurfacesTests` (the reader dropping `notifications`: no post) |
| 3 | Organisation keys read but not enforced (W2 Q4, LOG 223) | `library_overrides`, `library_inputs`, `policy.toolsets.allowed`, `role_mapping` and `timeouts` refused "not enforced before M8" (LOG 256). Consumed, so kept: `notifications`, `planning.require_review`, `roles`, `teams` | `OrganisationTests` (one key dropped from the refusal fails two) |
| 4 | `auto_resolved` did not name its actor (W2 Q5) | `<outcome> by policy` (LOG 258) | `PolicyRunTests.An_automatic_answer_names_policy_as_its_actor` (the old detail fails it) |
| 5 | A session's submission was checked only for parsing (W4 Q1, W5 Q1) | One type check, `ArtifactTypeCheck`, for agents and `artifact.import`; a submission of files that break their types is refused with every error and the session stays open (patch `m7-import-check`, LOG 259) | `SessionRunTests.A_submission_whose_files_break_their_types_...` (import ignoring the errors; the scope without the lock; the helper skipping validators, which also fails two of W5's `ArtifactCheckTests`) |
| 6 | A person's triage artifact was not in the run's artifact index (W5 Q2) | A decision answer's artifacts are indexed (LOG 260) | `FindingsLoopTests.A_person_triages_...` (not indexed fails it) |

### Done-when 1: who may and may not use each override

`OverrideAuthorisationTests.Every_key_of_the_override_catalogue_has_a_may_and_a_may_not_test` reads the rows of the two
theories below and checks they cover `Model.Overrides` exactly (10 keys, 4 variants), and that each row's roles agree
with the catalogue's defaults. Control: a key added to the catalogue with no row fails it.

| Key | May (by default) | May not | Where |
|---|---|---|---|
| `answer`, `answer_question` | approver (by the approval rule) | steerer; admin | API |
| `recover` | steerer; admin | approver; viewer | API |
| `breakpoint`, `amend`, `grant_attempts`, `abort` | steerer | approver | API |
| `skip`, `override_library` | steerer | viewer | API |
| `change_role` | admin | steerer | API |
| `skip_protected`, `amend_protected`, `override_library_protected`, `grant_attempts_beyond` | admin (the API passes the variant) | steerer (passes none; the run refuses protected content or a grant beyond the ceiling without it) | API, and W1's `SteeringTests`, `SteeringRunTests` |

`policy.overrides` changing a key and a variant, a team adding holders for its own runs, and a grant at an
intervention needing both `recover` and `grant_attempts` each have their own test (W2).

### Verified-by

Both exist and pass; I broke each again to confirm.

| Check | Tests | Control |
|---|---|---|
| A body naming `mallory` is recorded and delivered as the authenticated actor | `OverrideAuthorisationTests` (every key), `PolicyRunTests.An_actor_asserted_...` (a real run), `SessionRunTests` (submission) | The answer's actor from the body at both places it is set: 4 API cases and the run test fail |
| A pre-submit check and the later step receive the same task, tools, model and commit | `SessionRunTests` (digests equal; differ across commits), `CheckInputsTests` | The check given one extra input: the session test fails (digests differ) |

### Done-when 2, live: a person's requirements session

As the product owner decided (2026-11-04): the coordinator acted as the person, a separate identity. One attempt.

- **Setup:** three hosts on Temporal :8733 sharing one record store and artifact store: the orchestration worker, the
  starter's API (`m7-starter`, roles viewer, steerer, admin) and the person's API (its own installation, `m7-person`,
  role approver, its own token). Organisation: std from this branch, one rule `*` for approvers. No agent worker: no
  agent step runs before `approve`. Model `haiku` configured, unused.
- **Repository:** `ai-feature-flow/feature-flow-example` (the run's planning repository by default).

| Step | What happened |
|---|---|
| Start | `ff run start std:templates/project_delivery --repo ai-feature-flow/feature-flow-example --param project_name=m7-live` as the starter: `run-5a6000bc7f32`. The run opened the session at `define` on `ff/session/run-5a6000bc7f32/decision-1` from `main` (`d033998`); the wait's reason was the rendered prompt (seam 1) |
| Fetch | As the person: `ff session list`, `ff session fetch --out` (prompt and instructions) |
| Write | In the person's clone: switched to the session branch, wrote `projects/m7-live/requirements.yaml` (2 requirements) and `architecture.md`, committed `5b2782b`, pushed |
| Refused | The starter's `ff session submit`: 403, "m7-starter may not act on session ... it needs one of the roles approver" |
| Check | The person's `ff session check ... check_requirements`: passed (`requirement_support` valid), recorded "by m7-person" |
| Submit | The person's `ff session submit`: recorded. Timeline: `answered decision-1 submitted by m7-person`; `session_submitted ... at 5b2782b... by m7-person: contract, requirements`; `check_requirements` valid; the run opened `approve` |
| Parity | The check's digest and the step's `check_inputs` were the same, `sha256:129e6b69...` |
| Records | The session `submitted`, person `m7-person`; the answer records' actor `m7-person` |
| End | Aborted at `approve` with `--close-pull-requests` (none had opened). The session branch deleted. Both fixtures have only `main`, no `refs/feature-flow/*`, no open pull request |

Seen live but not changed: session branches are not deleted when a session ends (W4 follow-up 4; deleted by hand). The
person's push needed only a credential for the fixture, as a person would have. No Slack post was made: the live
installation configured no notifications provider.

### Tests run

On `m7-integration` at the end (Debug, Temporal :8733, Docker): default suite **1,249 passed**, 0 failed (22 projects;
replay 29, every history including the new `decision_prompt`); integration **100 passed**; `ff test library/std/tests`
**29 passed**. Build: 0 warnings.

### Deviations and judgement calls

LOG 256 to 261. Outside the seams: none. The only interface change is additive: `ActionScope.LockRef` (an optional last
parameter), `SessionNames.HandingOver`, `ArtifactTypeCheck` and `LibraryLock.ArtifactTypeAsync`. W3's LOG 233 (the
harness `configure` hook) is superseded: the hook is gone.

### Follow-ups

| # | Follow-up |
|---|---|
| 1 | M8 lifts each "not enforced before M8" refusal together with the key's consumer (LOG 256) |
| 2 | A run's clean-up could delete its session branches (W4 follow-up 4, seen live) |
| 3 | `artifact.import` in a plan stops for a person on a type error; a work-failure outcome might suit a plan that can retry |

## Coordinator decisions

Decisions the coordinator took during M7; those marked PO are the product owner's.

| # | When | Decision | Why |
|---|---|---|---|
| C1 | Start | Split: W0 interfaces; W1 steering core; W2 policy and identity; W3 surfaces; W4 sessions; W5 report checks and triage; W6 integration. LOG ranges 201-205 (W0), 206-215, 216-225, 226-235, 236-245, 246-255, 256-265 (W6). Ports 8133..8733; reviewers 8833+ | One worker per area with separate files |
| C2 | PO, 2026-11-04 | Slack: Socket Mode will be enabled later; build the inbound gateway without it now (signed-request tests); keep a Socket Mode transport easy to add | PO answer in chat |
| C3 | PO, 2026-11-04 | Done-when 2: the coordinator acts as the person, authenticated as a separate person identity; optional demo with the PO later | PO answer in chat |
| C4 | W0 review | PR #16 approved and merged; interfaces frozen. Wake with no step wakes every wait; W2 owns the API override endpoint and OverrideRecord, W1 only the workflow handler; LOG 204's teams.<name>.repositories accepted | W0 open questions 1-3 |
| C5 | Builder reports | Accepted builders' recommendations: W1 Q1-4 (breakpoint holds until cleared; skip gives no outputs; making a protected step unreachable is rejected; F1 for patched merges left to merge-time re-check). W2 Q1-3 (approver apart from admin; interventions authorised by `recover`; starting a run needs steerer); Q4-5 to integration. W3 Q3 (Slack users mapped by configuration); Q1, Q4 to integration; Q2 reminders deferred (follow-up). W4 Q1 yes at integration; Q2 use OIDC for the person; Q3 no agent-step pre-submit checks in M7. W5 Q1-2 at integration; Q3 approver default for triage | Each is the simpler shape and keeps floors |
| C6 | R1 F5 / R2 F4 | Approving an agent's patch or library override needs the roles the equivalent direct override needs. Contract change authorised: `WaitRecord` gains an optional last parameter `IReadOnlyList<string>? Requires = null` (override keys). W1 adds and persists it and fills it on patch-approval waits; W2's API requires `RolesForKey` of each key in addition to the approval rule | Second route to the same effect must not skip the role |
| C7 | R2 F8 | An intervention's recovery needs `recover`; `grant_attempts` (and `_beyond`) are required in addition when that is the action | One route, both roles |
| C8 | R5 F7 | Merge order changed to #20, #18, #17, #21, #19: W5 resolves its RunWorkflow conflict against W4 after #17 merges | W5 carries W4's commit already |
| C9 | PO, 2026-11-05 | "We are trying not to overcomplicate anything": added to reviewer rules and steered to all builders (simplest fix that closes the real hole; low findings needing real code become follow-ups) | PO instruction |
| C10 | Replaces C6 | No `WaitRecord.Requires`. Std's `*.patch_approval` default becomes `admin` (W1, LOG entry); who approves agent proposals stays the organisation's approval-rule call. No interface change | Simpler; same effect for std |

## For the product owner

LOG 201 to 261 are unreviewed (201 to 205 shared interfaces, 206 to 215 steering, 216 to 225 policy, 226 to 233
surfaces, 236 to 245 sessions, 246 to 250 reports and triage, 256 to 261 integration). One line each, with a
recommendation.

| LOG | What it decided | Recommendation |
|---|---|---|
| 201 | Notifications are de-duplicated by the engine (run, step, attempt, event), not the provider | Approve |
| 202 | A session's shapes; what a person produces (a triage, a session's files) arrives as artifacts on the one answer | Approve: one answer path, so approval rules apply to all |
| 203 | One override catalogue and one `override` update for every key | Approve |
| 204 | Authentication is a list of authenticators; roles resolve per scope; teams gain `repositories` | Approve, and add `teams.<name>.repositories` to docs/07 |
| 205 | The `wake` signal (a hint, the wait reads again) and the file splits | Approve |
| 206 | A breakpoint holds until cleared and pauses every entry to the step | Approve |
| 207 | A skip applies when the run next reaches the step, with a chosen outcome and no outputs | Approve: the person routing on the outputs gives a second action, which is rare |
| 208 | One grant mechanism for the override and the recovery | Approve |
| 209 | `change_role` is a person's patch; `amend` may omit `resume_at` and never changes a role | Approve |
| 210 | A patch whose route no longer reaches a protected step "touches" it (one reachability check) | Approve: it closed four bypasses the review found |
| 211 | `planning.require_review` is fixed in the run's policy at start | Approve |
| 212 | A mid-run library override: content by hash, the run's lock rewritten, steps in flight keep theirs | Approve |
| 213 | An agent's library override is always a patch approval, applied as the approver's | Approve; the agent-facing MCP tool is a follow-up |
| 214 | Going to a step inside a decided join reopens the join, so the run can complete | Approve: M6's live run needed it |
| 215 | Aborting closes the run tree's pull requests only when asked | Approve |
| 216 | `admin` > `steerer` > `viewer`; `approver` stands apart | Approve: an admin who should approve also needs the approver group |
| 217 | A team adds role holders for its own runs and may only raise `min_count`; a run cannot span two teams | Approve |
| 218 | An intervention is answered as the `recover` override, not by an approval rule | Approve: one rule for one act |
| 219 | Reading needs `viewer`, starting a run `steerer` | Approve |
| 220 | OIDC (issuer, audience, keys, groups claim) beside the local token | Approve |
| 221 | `min_count` counts distinct people; each answer recorded; the delivered actor can be several people | Approve; the race (two answers at once) is a follow-up |
| 222 | Auto-resolution counts the library's default; an unlocked "not allowed" warns; `policy` is reserved | Approve |
| 223 | The organisation file is read in full; startup checks span both layers | Approve, with LOG 256 (keys with no consumer are refused) |
| 224 | The effective policy view: one row per topic with request, cap, result and deciding setting | Approve |
| 225 | The override endpoint's wire and the CLI | Approve |
| 226 | The engine notifies from what a run records; a failed send never affects the run | Approve |
| 227 | A Slack user maps to a person by configuration, not email | Approve for now; an identity-provider lookup later |
| 228 | The chat gateway makes the ordinary answer call as the mapped person | Approve |
| 229 | A wake reuses the hand-over's interruption, so no attempt is used | Approve |
| 230 | Webhooks verified by their provider, de-duplicated in memory | Approve; the 200-run limit is a follow-up |
| 231 | The console is one static page reading through the API | Approve; OIDC sign-in is a follow-up |
| 232 | Reminders are not built | Approve the deferral |
| 233 | A harness `configure` hook | Superseded at integration: the hook is gone |
| 236 | A session's person is whoever takes it | Approve |
| 237 | The workspace is a branch and a document fetched through the API; no commit by the engine | Approve |
| 238 | Pre-submit checks run the later step's own code, `artifact.validate` steps only | Approve; agent-step checks later (coordinator C5) |
| 239 | The run imports a submission, not the API | Approve; the type check it waited for is LOG 259 |
| 240 | Only a submission takes the outcome that imports files | Approve |
| 241 | A check and a submission name a full commit | Approve |
| 242 | The check update and the workspace document are new wire types in a new file | Approve |
| 243 | `ff session mcp` is the engine's MCP server for the person's agent | Approve |
| 244 | A session's activities try five times, then tell the person | Approve |
| 245 | A dry run opens no session | Approve |
| 246 | Report-time artifact checks with correction turns; an in-house JSON Schema subset | Approve: it was M6's most costly gap |
| 247 | std's type rules become the `document_rules` validator | Approve |
| 248 | A person triages findings through a decision, recorded as that person; std's fix loop routes to it | Approve |
| 249 | `scm.pull_request` posts the declines the ledger holds | Approve |
| 250 | A fixed rule vocabulary for std's reviewers | Approve |
| 256 | Organisation keys nothing enforces are refused "not enforced before M8" | Approve: each returns with its consumer |
| 257 | A decision's wait records its rendered prompt | Approve |
| 258 | `auto_resolved` names `policy` as its actor | Approve |
| 259 | One artifact type check for agents, people and imports | Approve |
| 260 | A decision answer's artifacts are indexed | Approve |
| 261 | A check refused during the hand-over is tried again by the client | Approve |

## Consolidated follow-ups

"From" names the follow-ups table of a section of this note and its row: Interfaces, Steering, Policy, Sessions,
Surfaces, Reports, Integration; M6 is `m6-results.md`'s consolidated follow-ups. Closed during M7 and not listed:
Interfaces 1, Steering 1, Policy 1 to 3, Sessions 1 and 3, Surfaces 1 and 4, Reports 1 and 2, and M6's 1 to 3, 5, 6
and 8 (schema checks on import).

| # | Follow-up | From |
|---|---|---|
| 1 | **M4 follow-up 12, still open:** the sandbox's network is unrestricted and reaches the Temporal frontend. Fix before any shared deployment (M8) | M6 4 |
| 2 | M8 lifts each "not enforced before M8" organisation key with its consumer | Integration 1 |
| 3 | `min_count`: two answers at the same moment can leave the count short; re-read after recording | Policy 6 |
| 4 | Startup does not resolve the API's `token_ref` or other secret references | Policy 5 |
| 5 | `merge.require_checks` locked on refuses only a literal `false` at run start | Policy 4 |
| 6 | An MCP tool for agents to propose a library override (and a patch) | Steering 2 |
| 7 | `run.child`'s `run_gate: manual` is not built | Steering 3 |
| 8 | F1 at patch time: a patch adding an engine merge into a default branch is caught only at merge time | Steering 4 |
| 9 | An abort waits for an action that sends no heartbeat before closing pull requests | Steering 5 |
| 10 | Role checks on containers' inner agent steps; protected branches of a parallel; a twice-sent library override records twice | Steering 6 |
| 11 | Pre-submit checks of agent steps | Sessions 2 |
| 12 | A run's clean-up deletes its session branches | Sessions 4, Integration 2 |
| 13 | A load check refusing `checks_from` naming a step other than `artifact.validate`; checks capped and cancelled at run end; a test of the `submitting` guard; a gentler re-open of a session that failed to open | Sessions 5 to 8 |
| 14 | Reminders on open waits; `stalled` and `stuck` notifications once the engine has those states | Surfaces 2, 3 |
| 15 | The console's OIDC sign-in; the ledger and a triage in the console | Surfaces 5, Reports 3 |
| 16 | A woken run is chosen among the 200 most recently updated runs; a Slack message updated when answered | Surfaces 6, 7 |
| 17 | A decline from a triage with `post_declines: false` is still posted when the pull request later opens | Reports 4 |
| 18 | `docs/07` lists `teams.<name>.repositories` once LOG 204 is approved | Interfaces 2 |
| 19 | A plan's `artifact.import` stops for a person on a type error; a work-failure outcome might suit a plan that retries | Integration 3 |
| 20 | Still open from M6: `verification_satisfaction` and the trace checks; the work-item budget's overshoot; `EpicRunTests`' per-child durations; a live hand-over during a CI wait; detached hosts ignoring SIGTERM; the Copilot findings source unseen; M5 follow-ups 3 and 7; watch activities per run; parent counts lag a child's; `wait.condition` on a tracker issue | M6 7, 9 to 18 |
