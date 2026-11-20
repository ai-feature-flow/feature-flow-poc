# Phase 2 build plan: alpha and first full release

Status: drafted and reviewed 2026-11-21; the product owner answered its open questions the same day (section 7).

This document owns how the next phase of Feature Flow is built: the release
stages, the order of the work, the milestones and what each must show. Version 1
(M0 to M8 of `docs/11`) is built and merged. The inputs are the design review of
2026-11-21 (findings F1 to F21, low-severity findings L1 to L11), the console
design of the same date (Design A, API gaps G1 to G14) and the product owner's
decisions on both (`docs/PLAN.md` section 3). What the engine does stays owned by
`docs/00` to `docs/10`; this document cites them and does not restate them.
Judgement calls made here are LOG 328 to 346; LOG 330, 333, 341 and 342 were
decided by the product owner on 2026-11-21, the rest are unreviewed.

---

## 1. Scope

### 1.1 Release stages

| Stage | Who uses it | What it must include |
|---|---|---|
| **Alpha** | The product owner alone, on a laptop: local Temporal, SQLite, Docker, GitHub, Claude Code | The control gaps closed, the engine simplified, oversight readable through the API, the first console slice with no sign-in or a local token, sandboxes built from each repository's image, design documents that match the code |
| **First full release** | A team without the authors, in a corporate cloud install (Xero's: AWS, Okta) | Everything in alpha, plus the enterprise profile (Temporal Cloud, TeamCity, SonarCloud; F7), browser sign-in with OIDC, pluggable authentication, provider distribution without a fork, the extension points opened, the operational leftovers, a user guide |

Alpha milestones (A1 to A6) come first. First-full-release milestones (R1 to R5)
follow. Nothing in alpha depends on an R milestone.

### 1.2 Decisions this plan follows

The product owner's decisions of 2026-11-21 are recorded in `docs/PLAN.md`
section 3 (NP1 to NP9, and NP10 to NP12 for the answers in section 7). In short:

- **Deployment range.** The open-source base serves one developer locally, with a
  console that needs no sign-in, and a corporate cloud install. Authentication is
  pluggable or configurable; a fork with bespoke handlers is acceptable
  (`docs/00` P13).
- **Console.** Design A: TypeScript with a Node build, embedded in
  `FeatureFlow.Api` and served at `/console`, keeping the hash routes
  `#/runs` and `#/runs/{id}` that Slack links use (`SlackNotifications.cs:104`).
  The first slice includes steering (G10, G11) and server-sent events (G8). Any
  viewer of a run may read its transcripts.
- **F2** is built: `<level>.merge_action` becomes a decision. **F7** is first full
  release, not alpha. **F19**: toolsets are not in version 1, and the settings that
  are accepted but unused are refused.
- Every other review recommendation is accepted as direction.

### 1.3 Not in this plan

Version 1's exclusions stand (`docs/06` section 5; cost budgets, `docs/02`
section 8). External toolsets stay out (F19). Template authoring in the console
stays out: libraries are files in Git, reviewed by pull request.

---

## 2. Ways of working

These replace `docs/11` section 4.1 for this phase where they differ.

| Rule | Detail |
|---|---|
| Tests on a pull request | Only the default category runs. The integration and cluster categories run on `main`, or on a pull request labelled `full-ci` |
| Tests a builder runs | The tests that target the change, plus the default suite |
| Review | One independent review per pull request |
| Live runs | Only when a milestone's done-when needs one. Each such milestone says so |
| Results note | Each milestone ends with a short note in `docs/build/`, in the shape of the M-notes: what was built, the checks, what they showed, design changes with their LOG entries |
| Verified | As `CONTRIBUTING.md`, "Verify, do not assert": each named check has been seen to fail when the thing it guards is broken |
| Documents | A milestone that changes a mechanism updates its owning document in the same pull request, and removes any "planned" marker this plan added |

---

## 3. Build order

| # | Milestone | Stage | Why it comes here |
|---|---|---|---|
| A1 | Control gaps | Alpha | Correctness and safety faults, each small (review step 1). Refusing unused settings early stops new configuration depending on them |
| A2 | Engine simplification | Alpha | Every new action, wait or record kind added before it copies the current pattern. The review put oversight (its step 2: F5, F9, F4, F21) before simplification (its step 3); this plan reverses the two, because F5, F9, F21 and the G routes add waits, records and actions that would otherwise copy the current pattern. As a result the console arrives one large milestone later, and F4 moves to A5 (LOG 330; section 7, question 3) |
| A3 | Oversight API | Alpha | The console's reads and writes. Built on A2's single wait routing and action registry |
| A4 | Console first slice | Alpha | Needs A3's routes and contracts |
| A5 | Sandboxes and runtimes | Alpha | Independent of A3 and A4; may run alongside them after A2 |
| A6 | Documents match the code | Alpha | Last alpha step, so it describes what alpha built |
| R1 | Extension points | First full release | Opens the seams a corporate install and a fork need. After A2, which reshapes the code they touch |
| R2 | Pluggable authentication and console sign-in | First full release | Needs R1's distribution mechanism and A4's console |
| R3 | Enterprise profile | First full release | Needs R1 to ship providers outside the engine repository |
| R4 | Operations | First full release | Needed by a shared install, not by one person |
| R5 | User guide | First full release | Written last, against the released behaviour |

A5 may run in parallel with A3 and A4. R3 and R4 may run in parallel after R1.
Effort uses the review's scale: S is up to a few days, M about one to two weeks,
L several weeks.

---

## 4. Milestones

### A1. Control gaps

| | |
|---|---|
| Builds | **F1**, a locked cap cannot be bypassed: caps held in `RunPolicy`, one cap function used at run start and on patches, so an expression or a patch that loosens a locked `require_checks` is refused (today only a literal `false` is, `RunStartChecks.cs:232-234`; `SourceControl.cs:143`; `Patches.cs:249-262`); a `merge.require_tested` policy key, applied by the same function (LOG 342). **F1, races**: `limits.races` shares the per-step attempts ceiling on every path; today a patch is checked against it (`Patches.cs:260-261`) and run start is not (`RunStartChecks.cs:222-223`) (LOG 340). **F2**, the merge action request becomes a `<level>.merge_action` decision, raised by `SourceControl.ActionRequestAsync` (`SourceControl.cs:152-175`, which today polls the pull request and opens no wait), so approval rules on it take effect (`docs/04` section 4.5; LOG 123 superseded). A pull request merged on the code host before the decision is answered is accepted and the run continues: the engine closes the decision as answered on the code host and records, on the run timeline, the code host login of whoever merged and that the decision was not given through Feature Flow; no intervention is raised. Branch protection on the code host governs merges made there; Feature Flow's approval rules govern merges requested through it (LOG 341, product owner 2026-11-21). **F3, small part**: `notify.send` removed from `docs/04` and from the action contracts (`Actions.cs:71`; it is absent from `EngineActions.Built`, `EngineActions.cs:22-29`; LOG 342), and an action no provider can serve raises an intervention instead of retrying. **F19**: the `toolset` provider type (in the type list at `Configuration.cs:177`), a role's `toolsets` (`LibraryItems.cs:185-197`) and a library item's `requires.toolsets` (`LibraryItems.cs:95-100`) refused at load, beside the existing development-only refusals (`Configuration.cs:331-340`), as `policy.toolsets` already is (LOG 273). **L4, part**: the `scripted` agent runtime refused outside `development: true`. **L9**: evidence types refused on `artifact.import` at load |
| Done when | A negative test per path for `require_checks`: a locked `require_checks` loosened by an expression, and by a patch, is each refused; the same for a locked `require_tested`. Separate negative tests for races: a `limits.races` above the per-step ceiling is refused at run start, and by a patch. A run whose policy requires approval on `ticket.merge_action` opens that decision and, once it is answered, asks the person to merge; a pull request merged on the code host before the answer closes the decision as answered on the code host, records the merger's code host login on the run timeline, raises no intervention and lets the run continue, shown by a test. A plan using an unservable action stops at an intervention. Each refused setting has a load test naming its key |
| Depends on | None |
| Effort | M |
| Stage | Alpha |

### A2. Engine simplification

The review's step 3, done together because the changes touch the same code in
`RunWorkflow`, `Activities` and `EngineActions`.

| | |
|---|---|
| Builds | **F3, rest**: delete the M3 action stand-in (`action_stand_in`, `IActionStandIn`, `Built`, `BuiltActions`, `HasSourceControl`, `WithoutSandboxAsync`) and the `wait.condition` tracker stub (`Waiting.cs:100`); tests move to the memory providers; a dry run is kept, served by the memory and recording providers, and `docs/07` section 5 follows (LOG 342). **F10**: one action descriptor registry replaces the nine tables keyed by action name, and every action gets an input schema in it, `run.child` included, so unknown keys such as `run_gate` are refused (F14, code part; `StepStartChecks.cs:28-51` has no `run.child` entry; LOG 346). **F8**: logic and state move out of the Temporal shell (`RunWorkflow`, 2,239 lines in four files) into the interpreter core, and child runs and questions into `RunState`. **F11**: one human-wait mechanism: `WaitKind.Question` added, `Decision` renamed `Gate`, one routing function for every wait; LOG 93 amended. **L1**: shared rules in one place (classification, step lookup, duration parsing, short references). **L2**: one recovery-name table. **L6**: compatibility leftovers removed (no backward compatibility, LOG 263). **L7**: one repository-root test helper |
| Done when | The default and integration suites pass with the stand-in deleted. The architecture test fails if the shell holds a routing decision the core can make. Adding a test action needs one registry entry and nothing else, shown by a test. A plan with `run_gate` on `run.child` is refused at load. Agent questions, gates and interventions open, answer and time out through one routing function, each with a re-drive test |
| Depends on | A1 (F2 and F3-small settle what the registry must hold) |
| Effort | L |
| Stage | Alpha |

### A3. Oversight API

The reads and writes the console's first slice uses, built on A2.

| | |
|---|---|
| Builds | **F5 / G3**: a required `reason` on answers and aborts, recorded on `AnswerRecord` (`AnswerArgs` has none, `Wire.cs:210-221`), passed to the next agent in a run-context artifact with `context: full` and the retry cause. **F9 / G5**: transcripts indexed, `GET /runs/{id}/artifacts`, and each gate wait linked to the artifacts its step consumed. This amends LOG 325, which decided no listing (LOG 343). **G1**: `GET /attention`, open waits across runs that the caller may answer, with a store query in SQLite and Postgres. **G2**: `GET /me`, and `can_answer` per wait on `GET /runs/{id}`. **G4**: closed waits, answers with `min_count` progress, and overrides readable. **G8**: `GET /events`, server-sent events carrying change notifications, fed by one poller per API replica over new "updated after" store queries. **G10**: templates read, entrypoints and parameters from the configured libraries. **G11 / F18, engine part**: each accepted plan revision recorded as an artifact, the current revision served as data; `ff` steering accepts `--change name=@file`. **F21 / G14**: reminders on open waits, interval in organisation configuration (M8 follow-up 10; LOG 232). **L3 / G7**: request and response records for the routes above, and an OpenAPI document |
| Done when | API tests for each route, on SQLite and Postgres where a store query is new. A rejection on a gate with a reason produces a fix attempt whose prompt carries the reason, shown by a re-drive test. A reminder fires once per interval on a test clock and stops when the wait closes. The OpenAPI document is generated in the build and checked against the routes |
| Depends on | A2 (F21 needs F11's routing; F5 and G11 add records through F10's registry) |
| Effort | L (several M items, mostly independent) |
| Stage | Alpha |

### A4. Console first slice

| | |
|---|---|
| Builds | **Design A**: a TypeScript single-page application at `/console`, built with Node and Vite (LOG 344; the UI library is chosen in this milestone), embedded in `FeatureFlow.Api` as hashed assets; Node in CI and in `deploy/images/host.Dockerfile`; a client generated from A3's OpenAPI document; the text-only rule of the M7 console kept (`console.js:1-3`). **Sign-in for alpha**: no sign-in when the API is bound only to loopback (a planned `api.auth.mode: none`, which checks the bound address at start and refuses to start otherwise, since `FF_API_LISTEN` may name any address, `Composition.cs:256`; LOG 333), or the pasted local token. **Screens**: "Needs me" (G1, G2); the run page with open and closed waits, child runs, timeline and current plan (G4, G11); the decision form with a required reason, consumed artifacts as links, all recovery actions including `continue`, a step picker for `go_to` and `replan`, and `min_count` progress; artifacts and transcripts, readable by any viewer of the run (G5); abort; start a run from a template (G10); steering with breakpoint, skip, amend, retry and attempt grants against the current plan (**F18**, G11); live updates over `/events` (G8). **Console defect**: `stalled` and `stuck` shown as status, not as decisions, and `continue` in the recovery list (`console.js:74, 116-117`; M8 follow-up 11) |
| Done when | Playwright tests in the default CI job, against an API on the in-memory providers, cover: find a wait in "Needs me", answer it with a reason, see the closed wait; start a run; set a breakpoint and continue; an update arrives over `/events` without a reload. The Slack link `#/runs/{id}` opens the run. A test fails if markup is inserted from API data. One live run by the product owner on the local profile, driven from the console only. A host with `api.auth.mode: none` and `FF_API_LISTEN` set to a non-loopback address refuses to start, shown by a test |
| Depends on | A3 |
| Effort | M (console) plus S (build chain and asset serving) |
| Stage | Alpha |

### A5. Sandboxes and runtimes

| | |
|---|---|
| Builds | **F4**: the sandbox built from the repository's `environment.image` (`Manifest.cs:83`), the base sandbox image's contract documented in `docs/06`, and M4 follow-up 1 restored to the follow-up list, which the m8 consolidation dropped. **F12** (in alpha rather than R1, LOG 345): an agent runtime base class with hook normalisation, a runtime contract suite, and `model_access` per runtime instance (M8 follow-ups 14 and 15). **L5**: the `engine_tools` switch and the unused `api.base_url` key removed. **L10**: the provider supplies its own fault hint instead of the GitHub-specific text in `ProviderError.DescribeFault` |
| Done when | A run on a repository that declares an image runs its tests in that image (integration test with a local image). Both built-in runtimes pass the runtime contract suite; a deliberately broken hook mapping fails it. Configuration naming `engine_tools` or `api.base_url` is refused. `docs/06` states the base sandbox image's contract, and M4 follow-up 1 is back on the follow-up list |
| Depends on | A2 |
| Effort | M |
| Stage | Alpha |

### A6. Documents match the code

| | |
|---|---|
| Builds | **F14**: the design documents brought into line with the code: `run_gate` (`docs/04` section 5.1), the merge queue (`docs/04` section 7), reminders (`docs/04`, `docs/10`), capability task queues (`docs/02` section 1.1, `docs/10`), unlocked caps (`docs/05` section 3), unbuilt configuration in the `docs/07` installation example. Each "planned" marker this plan added is removed or confirmed |
| Done when | A reviewer reading each owning document against the code finds no described mechanism that does not exist, other than ones marked planned with a milestone. Findings listed in the results note |
| Depends on | A1 to A5 |
| Effort | S |
| Stage | Alpha |

### R1. Extension points

The review's step 4.

| | |
|---|---|
| Builds | **F6**: an organisation adds a provider without forking the engine: a host project composed from a new `StandardProviders.Factories` (the standard packages' `Factories` lists, such as `GitHubProviders` and `ClaudeCodeRuntime`, combined) plus its own, a sample host, and the engine published as packages (`docs/06` section 4). A fork with bespoke handlers remains acceptable (`docs/00` P13). **F15**: std templates as fragment wrappers, so changing one std task or role does not mean copying the template chain. **F16**: capability flags either read by the engine or removed, and capabilities checked on the instance a step's role maps to, not the default instance of its type (`ProviderSet.Offered`, `ProviderSet.cs:33-40`). **F17**: record kinds declared once; each store reads the declaration instead of re-encoding queries and write rules; the separate memory store deleted. **F13**: `findings.triage`'s classes that need a person moved from the engine (`Findings.cs:247-253`) to workflow policy. **L4, rest**: test doubles moved to `Providers.Testing`. **L8**: tracker status identity as a provider default |
| Done when | The sample host adds a provider of its own and runs a workflow test with it, built against the published packages only. Each store passes the record contract suite from the declarations. An organisation library that wraps one std task changes it in every std template that uses it without copying those templates, shown by a load test. A step whose role maps to an instance lacking a required capability is refused at run start although the type's default instance has it |
| Depends on | A2 |
| Effort | L |
| Stage | First full release |

### R2. Pluggable authentication and console sign-in

| | |
|---|---|
| Builds | **Pluggable authentication**: the API's authenticators (`IAuthenticator`, today chosen by `ApiAuthentication.For`) selected by `api.auth.mode` from a set the host registers, as provider factories are, so a distribution or fork adds a bespoke handler. **G6**: a public console client in `api.auth`, an unauthenticated `GET /console/config` (issuer, client id, audience), the content security policy's `connect-src` for the issuer, and authorisation code with PKCE in the console. Okta is the reference identity provider |
| Done when | A test host registers a custom authenticator and authenticates with it. The console signs in against a test OIDC issuer in Playwright, and a token for the wrong audience is refused. A live sign-in against the corporate Okta tenant |
| Depends on | R1 (F6), A4 |
| Effort | M |
| Stage | First full release |

### R3. Enterprise profile

| | |
|---|---|
| Builds | **F7**: Temporal Cloud connection with TLS and API-key or mTLS authentication, and the retention check (`TemporalConnection.cs` reads only the payload codec today; M8 follow-up 1); a TeamCity CI provider; a SonarCloud findings analyser (only `sarif` runs, `Testing.cs:214`) |
| Done when | Contract suites pass for the TeamCity and SonarCloud providers on recorded responses. One live run on the enterprise profile: Temporal Cloud, Jira, GitHub, TeamCity and SonarCloud |
| Depends on | R1 |
| Effort | M (Temporal Cloud S, TeamCity M, SonarCloud S to M) |
| Stage | First full release |

### R4. Operations

| | |
|---|---|
| Builds | **L11**: hosts retry at startup until Temporal and the stores are ready; published images; a sandbox sweeper; work branches and `refs/feature-flow/wip/*` cleaned up after a run (M8 follow-ups 2, 3, 16, 18). **G13**: a health and platform status route, shown in the console. **G9**: run list paging and filters (status, template, requester, repository, cursor). **G12**: agent step detail, `GET /runs/{id}/steps/{step}`, with tool-level activity in the console |
| Done when | A host started before Temporal comes up and serves once it does. A sandbox left by a killed worker is removed by the sweeper. Closed runs leave no work branch or wip reference. More than 200 runs are reachable by paging. An agent step's tool calls appear in the console while it runs |
| Depends on | R1; A4 for the console parts |
| Effort | M |
| Stage | First full release |

### R5. User guide

| | |
|---|---|
| Builds | **F20**: a user and operator guide: install each profile, configure an organisation, start, watch, answer and steer a run, write a library, recover a stuck run |
| Done when | A person outside the authors installs the small-company profile and runs a ticket from the guide alone, with the gaps they hit fixed |
| Depends on | R1 to R4 |
| Effort | M |
| Stage | First full release |

---

## 5. Coverage

Every review finding and console gap, and the milestone that carries it.

| Id | What | Milestone |
|---|---|---|
| F1 | Locked `require_checks` bypassed by expression or patch; races not checked at run start | A1 |
| F2 | Merge action request is not a decision | A1 (with a code-host merge before the answer failing the step) |
| F3 | Action stand-in on the production path | A1 (`notify.send` removed, unservable actions); A2 (stand-in and dry-run branches removed) |
| F4 | Sandbox not built from the repository's environment | A5 |
| F5 | Reasons and retry causes do not reach the next agent | A3 |
| F6 | Providers cannot be added without a fork | R1 |
| F7 | Enterprise profile cannot be deployed | R3 |
| F8 | Temporal shell holds logic and state | A2 |
| F9 | Artifacts not listed, transcripts not readable | A3 |
| F10 | Action facts spread over nine tables | A2 |
| F11 | Three mechanisms for human waits | A2 |
| F12 | A new agent runtime needs engine changes | A5 |
| F13 | `findings.triage` fixes classes in the engine | R1 |
| F14 | Design documents describe dropped or unbuilt mechanisms | A2 (`run.child` input schema refuses `run_gate`); A6 (documents) |
| F15 | Changing one std task means copying the template chain | R1 |
| F16 | Capability flags never read | R1 |
| F17 | Record kinds re-encoded in each store | R1 |
| F18 | Steering CLI-only, against an unseen plan | A3 (plan revisions, `--change @file`); A4 (console steering) |
| F19 | Toolset settings accepted but unused | A1 |
| F20 | No user or operator guide | R5 |
| F21 | No reminders on open waits | A3 |
| L1 | Shared rules duplicated | A2 |
| L2 | Recovery names in several tables | A2 |
| L3 | API bodies read by key | A3 (as G7) |
| L4 | Test doubles in production assemblies; `scripted` runtime allowed | A1 (refuse `scripted`); R1 (move doubles) |
| L5 | `engine_tools` switch; unused `api.base_url` | A5 |
| L6 | Compatibility leftovers | A2 |
| L7 | Repository-root test helper duplicated | A2 |
| L8 | Tracker status identity default | R1 |
| L9 | Evidence types allowed on `artifact.import` | A1 |
| L10 | GitHub-specific fault hint in the abstraction | A5 |
| L11 | Startup retry, images, sandbox sweeper, branch clean-up | R4 |
| G1 | `GET /attention` | A3 |
| G2 | `GET /me`, `can_answer` | A3 |
| G3 | Answer reason (F5) | A3 |
| G4 | History reads | A3 |
| G5 | Artifacts and transcripts (F9) | A3; console in A4 |
| G6 | Browser sign-in with PKCE, `/console/config` | R2 |
| G7 | Typed contracts and OpenAPI (L3) | A3 |
| G8 | Server-sent events | A3; console in A4 |
| G9 | Run list paging and filters | R4 |
| G10 | Templates read for a start form | A3; console in A4 |
| G11 | Current plan revision as data (F18) | A3; console in A4 |
| G12 | Agent step detail | R4 |
| G13 | Health and platform status | R4 |
| G14 | Reminders (F21) | A3 |
| Console defect | `stalled`/`stuck` shown as decisions; `continue` missing | A4 |

Nothing is deferred beyond first full release.

---

## 6. Judgement calls

| LOG | Call |
|---|---|
| 328 | File name `12-build-plan-phase-2.md` |
| 329 | Milestones named A and R by release stage |
| 330 | Engine simplification (A2) before the oversight API and console (accepted by the product owner, 2026-11-21) |
| 331 | F2 and F3-small before A2, though F2 touches a wait |
| 332 | Reminders (F21) after F11, in A3 |
| 333 | A loopback-only `none` authentication mode for the alpha console (accepted by the product owner, 2026-11-21) |
| 334 | Pluggable authentication built on the existing `IAuthenticator` list, in R2 after F6 |
| 335 | Extension points (R1) placed in first full release |
| 336 | G9, G12 and G13 placed in first full release |
| 337 | F14 as alpha's last milestone |
| 338 | Planned items marked in the owning documents with this plan's milestone |
| 339 | F19 enforced by refusal at load, not by removing the toolset sections from the design |
| 340 | Races share the per-step attempts ceiling on every path, run start included |
| 341 | A pull request merged on the code host before the `<level>.merge_action` answer is accepted and the run continues (the product owner's decision, 2026-11-21) |
| 342 | Dry run kept, `require_tested` added, `notify.send` removed (accepted by the product owner, 2026-11-21) |
| 343 | LOG 325 amended: artifacts listed and transcripts indexed in A3 |
| 344 | Vite as the console's build tool |
| 345 | F12 in alpha (A5), not with the extension points in R1 |
| 346 | F14's code part (`run.child` input schema) placed in A2 under F10 |

---

## 7. Product owner answers

The product owner answered the plan's eight open questions on 2026-11-21
(`docs/PLAN.md` section 3, NP10 to NP12). Questions 1, 2, 3 and 5 to 8 were
accepted as proposed; question 4 was overridden.

1. **`api.auth.mode: none`** (LOG 333). Accepted: alpha's console may run with
   no sign-in when the host has checked at start that the API is bound only to
   loopback; the pasted local token remains the other mode. A4.
2. **Version numbering.** Backward compatibility, dropped for version 1's build
   (LOG 263), returns at the first full release, not alpha.
3. **Milestone order** (LOG 330). Accepted: engine simplification (A2) before
   the oversight API (A3) and the console (A4), reversing the review's order.
4. **Merge on the code host before the merge decision is answered** (F2,
   LOG 341). **Overridden.** The proposed default (fail the step as a policy
   breach) is not taken. A pull request merged on the code host before the
   `<level>.merge_action` decision is answered is accepted and the run
   continues: the engine closes the decision as answered on the code host,
   records the code host login of whoever merged and that the decision was not
   given through Feature Flow on the run timeline, and raises no intervention.
   Branch protection on the code host governs merges made there; Feature Flow's
   approval rules govern merges requested through it. In the product owner's
   words: "if an engineer merges in github then there is not much that anyone can
   do so there is no point stopping - often an engineer will review and just
   click merge by habit in github, so the workflow should accept and continue".
   A1.
5. **Dry run** (F3, LOG 342). Accepted: keep `dry_run`, served by the memory and
   recording providers. A2.
6. **`require_tested`** (F1, LOG 342). Accepted: add a lockable
   `merge.require_tested` policy key, applied by F1's cap function. A1.
7. **`notify.send`** (F3, LOG 342). Accepted: remove it from `docs/04` sections 2
   and 12 and from the action contracts. A1.
8. **Okta for R2.** Assume the corporate tenant can register a public browser
   client for the console (OIDC with PKCE); verify this before R2 starts. If it
   cannot, R2 needs a server-side session instead (about S to M more).
