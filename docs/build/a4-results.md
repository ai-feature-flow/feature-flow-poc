# A4 results: console first slice

Milestone A4 of `docs/12-build-plan-phase-2.md` section 4, built 2026-11-21 on `stuart-parkinson_xero/feature-flow-a4`
from a design note, three builder tasks in order (build chain and serving with the no-sign-in mode beside it, then the
shell and browser-test harness), four screen tasks in parallel, a check of the console against the real hosts, an
independent review and two fix passes. Every done-when item is built and shown by a test seen to fail when the code it
guards is broken, except the product owner's live run, which is his: its steps are in section "Live run" below and were
checked as far as they go without model tokens or credentials. No recorded replay history changed.

## What was built

| Item | Change |
|---|---|
| Design A, build chain | A TypeScript single-page application in `src/FeatureFlow.Console` (not a .NET project): Preact 11, Vite 8, TypeScript 5.9, `openapi-fetch`; exact versions and a committed lockfile (LOG 452). MSBuild targets in `FeatureFlow.Api.csproj` run `npm ci` and `npm run build` when their inputs change (a stamp of the input list catches a deleted file) and embed `dist/` as hashed assets; Node is required for `dotnet build`, `-p:ConsoleBuild=false` skips it (LOG 453). Client types are generated from the committed `openapi.json` in every build, so API drift fails `tsc` (LOG 456). Node 26 in every CI job, Chromium installed in the default job; a Node stage in `deploy/images/host.Dockerfile`, with the console's exclusions in `host.Dockerfile.dockerignore` (LOG 462) |
| Serving | `ApiApp.Console.cs`: `/console` serves `index.html` with `no-cache`, `/console/assets/*` with `immutable`; a content security policy with Trusted Types and no inline styles (LOG 454, 455). The M7 page and script are removed |
| Sign-in | `api.auth.mode: none`: every request is the configured actor with its roles; the host refuses to start unless the API listens only on loopback, checked on the configured address, at host start before Temporal, and on every bound address (LOG 457). Under `none` a guarded route also refuses a request from a non-loopback connection or `Host`, a cross-site `Sec-Fetch-Site`, or a write without a JSON body, so another page in the browser cannot drive the API (LOG 469). Otherwise the pasted token, checked with `GET /me` (LOG 458) |
| Screens | Routes `#/` Needs me, `#/runs`, `#/runs/{id}` (the Slack form), `#/runs/{id}/artifacts/{ref}`, `#/start` (LOG 463). Needs me (G1, G2), one `/attention` read per page (LOG 471). Run page: summary, open waits with `stalled`/`stuck` as status (M8 follow-up 11), closed waits with who answered, what and why, history, child runs marked when they wait on you, timeline, artifact list, abort with a reason while the run is running (LOG 464, 470). Decision form: gate with consumed artifacts as links and a required reason; question; patch approval; intervention and breakpoint with every recovery the wait offers (so `continue` at a breakpoint), a step picker for `go_to` and `replan`, attempts, end status; `min_count` progress; read-only when `can_answer` is false (LOG 465). Plan panel: the current revision with step status, and steering where the engine accepts it: breakpoint, skip, attempt grants, amend; retry is a recovery in the decision form (LOG 466, 470). Viewer: text only, first 1 MB, download with the token (LOG 467). Start a run from a template's parameters (G10) |
| Live updates | `GET /events` read with `fetch` (the bearer token is sent), reconnecting with `Last-Event-ID`; `reset`, a new connection and a visible page re-read everything; other frames re-read the affected run and lists (LOG 459) |
| Text-only rule | A check over the console's syntax tree in `npm run build` and `ConsoleTests` (markup sinks, URL attributes not built by `app/links.ts`, spreads, computed `setAttribute`); Trusted Types; a browser test feeding markup through API data on every screen but the start form (LOG 460) |
| Browser tests | `tests/FeatureFlow.Console.Tests`, `Microsoft.Playwright`, default category: the API in process on loopback over SQLite and file-system artifacts, and a scripted `FakeEngine` that writes the records the workflow would and refuses what the core refuses (LOG 461). Every test fails on a page error, console error or policy violation |
| Documents | `docs/00` status; `docs/02` section 7 (authentication and console rows; the A4 "planned" marker removed); `docs/07` sections 2 and 6; `docs/09` section 4.2; `docs/11` sections 2.2, 2.3, 4.1; `CONTRIBUTING.md` local setup (Node, console development, browser tests); `ci.yml` header |

## Checks

| Done when | Test |
|---|---|
| Find a wait in "Needs me", answer it with a reason, see the closed wait | `NeedsMeTests.A_wait_found_in_needs_me_is_answered_with_a_reason_and_shown_closed` (the outcome button disabled until a reason; the Closed waits section shows actor, choice and reason; Needs me empty; no reload) |
| Start a run | `StartRunTests.A_run_started_from_a_template_opens_on_its_page`; `Parameters_are_offered_by_type_with_their_defaults`, `A_refused_start_shows_the_reason` |
| Set a breakpoint and continue | `SteeringTests.A_breakpoint_set_from_the_plan_pauses_the_run_and_continue_resumes_it` (on a pending step of `std:templates/ticket_delivery`; the run reaches it and pauses; `continue` through the form; the step starts); `The_engine_refuses_a_breakpoint_on_a_step_that_is_waiting`; `DecisionTests.A_breakpoint_offers_continue` |
| An update arrives over `/events` without a reload | `LiveTests.A_wait_opened_elsewhere_appears_without_a_reload` (waits for the stream's first re-read to settle, then appends a wait; a page marker survives); `After_a_reset_the_page_reads_everything_again`; `SignInTests.With_the_local_token_the_console_asks_for_it_and_receives_events` |
| The Slack link `#/runs/{id}` opens the run | `LinkTests.A_slack_link_opens_the_run` (the URL `SlackNotifications.LinkOf` builds, an id needing encoding) |
| A test fails if markup is inserted from API data | `TextOnlyTests.Markup_in_api_data_is_shown_as_text_and_inserts_no_element` (needs me, run heading, run page, run list, wait form, plan, artifact list, viewer); `ConsoleTests.The_console_sources_never_build_markup_or_links_from_data`, `The_text_only_check_refuses_links_spreads_and_sinks_however_written` |
| A host with `none` and a non-loopback `FF_API_LISTEN` refuses to start | `Hosting.Tests/NoSignInTests.A_host_with_no_sign_in_refuses_to_start_when_FF_API_LISTEN_is_not_loopback` (through `HostArguments.RunAsync` with the variable set), `A_host_with_no_sign_in_refuses_to_start_on_an_address_other_than_loopback` (five addresses), `The_loopback_rule`; `Api.Tests/NoSignInTests` (configured and bound addresses; the cross-site refusals) |
| One live run by the product owner on the local profile, driven from the console only | Not done here: the product owner's. Steps in "Live run" below |

Other console tests: `RunPageTests` (status waits as banners, closed waits, children, timeline and history, abort with a
reason, no abort or steering on an ended run or for a caller without `abort`), `DecisionTests` (recoveries and step
picker, arguments, consumed artifacts, `min_count`, questions, read-only), `SteeringTests` (only the steering the engine
accepts, protected steps, the form in the step's row, skip and grants, amend against the current revision, a reason on
every override), `ArtifactTests`, `RunListTests`, `SignInTests`, `ConsoleTests.The_console_is_served_as_hashed_assets_with_its_policy`.

Suites at the head: build 0 warnings; default suite 25 projects, all passed (Api 171, Console 46, Hosting 58, Replay 44);
Integration category on a local Temporal dev server (Integration.Tests 124, Postgres 30, S3 12, Docker 17, API 2). The console tests take about 10 s; the first run on a machine downloads Chromium (about 2 min). The
host image builds with the console embedded.

## Controls

Each named check was seen to fail with the code it guards broken, then pass restored: about 70 by the builders and fix
passes, recorded in their reports, and six by the reviewer. The review found that two done-when tests did not guard what
they named (the `/events` test passed with change frames ignored; the breakpoint test ran a step the engine refuses and
never checked the resume); both were fixed and re-checked. Mine on the merged branch are in the sub-orchestrator's report.

## Check against the real hosts

The browser tests use a scripted engine, so the console was also driven by hand against the real orchestration worker
and API host (Temporal dev server, SQLite, file-system store, `api.auth.mode: none` on `127.0.0.1`), with
`m7s:templates/decide`, which has no agent step: start from "Start a run", the gate in Needs me and under Open waits, an
answer with a reason, the run completing with no reload, the closed wait, an abort with a reason, the viewer and the run
list; the host on `0.0.0.0` refused to start with its message. What it found: steering was offered on ended runs (fixed,
LOG 470), and three follow-ups below.

## Live run

The product owner's run of `std:templates/ticket_delivery` on `ai-feature-flow/feature-flow-example`, on the local
profile, driven from the console only. Steps marked *(checked)* were run on 2026-11-21 with the m7s template and dummy
credentials; the rest need model tokens or the GitHub App.

1. **Prerequisites.** .NET SDK 10, Node 26 and npm 11 on `PATH`; Docker running; the Temporal CLI at
   `~/.temporalio/bin/temporal`. Build the checkout: `dotnet build FeatureFlow.slnx -c Release` (builds the console).
   The sandbox image: `docker build -t feature-flow-sandbox:m8 -f src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile src/Providers/FeatureFlow.Providers.Docker`.
   The sandbox client for Docker's architecture (Apple silicon):
   `dotnet publish src/FeatureFlow.SandboxClient -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o ~/ff-live/sandbox-client`.
   Claude Code signed in; a subscription token for `CLAUDE_CODE_OAUTH_TOKEN`. The GitHub App id, installation id and
   key path from `~/keys/feature-flow-secrets.json`. An open issue on the fixture for the run's `ticket_id`.
2. **Configuration**, in `~/ff-live` (`<…>` marks your values; the files passed validation and provider composition
   with dummy values *(checked)*):

   `installation.yaml`:

   ```yaml
   api_version: feature-flow/v1
   kind: installation
   temporal: { address: "127.0.0.1:7833", namespace: default, task_queue: ff-a4-live }
   providers:
     artifact_store: { default: filesystem, instances: { filesystem: { root: data/artifacts } } }
     record_store: { default: sqlite, instances: { sqlite: { path: data/records.db } } }
     secrets: { default: file, instances: { file: { path: secrets.json, from_environment: [CLAUDE_CODE_OAUTH_TOKEN] } } }
     sandbox: { default: docker, instances: { docker: { image: "feature-flow-sandbox:m8" } } }
     agent_runtime:
       default: claude_code
       instances:
         claude_code:
           model: haiku
           model_access: { mode: proxy, key_ref: secrets/CLAUDE_CODE_OAUTH_TOKEN, upstream: "https://api.anthropic.com", auth: oauth }
     vcs: { default: git, instances: { git: { cache: clones } } }
     credential_broker:
       default: github_app
       instances:
         github_app:
           app_id: "<github-app-id>"
           installation_id: "<github-installation-id>"
           key_path: "<path to the App's private key>"
           repositories: [ai-feature-flow/feature-flow-example]
     code_host: { default: github, instances: { github: {} } }
     ci: { default: github_actions, instances: { github_actions: {} } }
     tracker: { default: github_issues, instances: { github_issues: { repository: ai-feature-flow/feature-flow-example } } }
     findings_source: { instances: { copilot_review: {} } }
     validator: { instances: { std: {} } }
   agent_worker:
     listen: "http://0.0.0.0:8581"
     urls: ["http://host.docker.internal:8581"]
     token_key_ref: secrets/step_token_key
     sandbox_client: "<home>/ff-live/sandbox-client/ff-sandbox"
   api:
     auth: { mode: none, actor: <your name> }
   ```

   `organisation.yaml`:

   ```yaml
   api_version: feature-flow/v1
   kind: organisation
   name: solo
   libraries:
     - { name: std, source: directory, path: "<checkout>/library/std" }
   policy:
     approval:
       - { match: "*", roles: [approver], min_count: 1, from_chat: false, auto: false }
   role_mapping:
     default: { agent_runtime: claude_code, model: haiku }
   ```

   `secrets.json`: `{ "step_token_key": "<output of: openssl rand -hex 32>" }`.
3. **Temporal** *(checked)*: in `~/ff-live`, `mkdir -p data`, then
   `~/.temporalio/bin/temporal server start-dev --headless --port 7833 --http-port 7834 --metrics-port 7835 --ui-port 8833 --db-filename data/temporal.db`.
4. **The three hosts**, one terminal each in `~/ff-live` (orchestration worker and API *(checked)*; agent worker not):

   ```sh
   dotnet <checkout>/hosts/FeatureFlow.Host.OrchestrationWorker/bin/Release/net10.0/FeatureFlow.Host.OrchestrationWorker.dll --installation installation.yaml --organisation organisation.yaml
   CLAUDE_CODE_OAUTH_TOKEN=<token> dotnet <checkout>/hosts/FeatureFlow.Host.AgentWorker/bin/Release/net10.0/FeatureFlow.Host.AgentWorker.dll --installation installation.yaml --organisation organisation.yaml
   FF_API_LISTEN=http://127.0.0.1:8580 dotnet <checkout>/hosts/FeatureFlow.Host.Api/bin/Release/net10.0/FeatureFlow.Host.Api.dll --installation installation.yaml --organisation organisation.yaml
   ```

   With `FF_API_LISTEN=http://0.0.0.0:8580` the API host refuses to start, naming the address and `none` *(checked)*.
5. **Open** `http://127.0.0.1:8580/console`: no token form; the header shows your name and "Needs me (0)" *(checked)*.
6. **The run, from the console only.**
   1. "Start a run": `std:templates/ticket_delivery`, `ticket_id` the fixture issue, `engine_can_merge` off,
      Repositories `ai-feature-flow/feature-flow-example`; Start. The run page opens *(checked with m7s)*.
   2. Before the agent reaches it, set a breakpoint on a later step from the plan panel, with a reason. When the run
      reaches the step it pauses with a breakpoint wait; choose `continue`, give a reason, Send. (This covers "set a
      breakpoint and continue" on the real engine, which the m7s check could not: its template has one step.)
   3. Watch the plan and timeline move as the agent steps run.
   4. At each wait ("Needs me (1)", or Open waits on the run page): read the prompt, open the consumed artifacts in the
      viewer, type a reason and choose. The answer moves to Closed waits with your name, choice and reason, with no
      reload *(checked with m7s)*. Answer every wait, recoveries included, from the console.
   5. Do not use the CLI, the API or GitHub's pages to move the run; only look.
7. **Evidence to record**: screenshots of the console at start, the start form, each open wait and its answer, Needs
   me, the completed run (Closed waits, History, Timeline, Artifacts), one artifact in the viewer, the run list; the run
   id, its final status, the pull request and merge commit on the fixture; the browser's developer console (expect one
   404 for `/runs/{id}/plan` just after start, follow-up 2); each host's output. Stop each host and Temporal with Ctrl+C.

## Live run with Copilot CLI

The same run on Copilot quota instead of the Anthropic subscription: Copilot CLI in its native mode, behind the model
proxy with `auth: github` (LOG 496 to 498; `docs/07` section 6.1). It shares `~/ff-live`, Temporal and the ports with
the run above; only the configuration and the secret differ. Steps marked *(checked)* were run on 2026-11-22 before
the Copilot changes merged, with a dummy token, the real GitHub App and a copy of the files below (ports 7661, 8680 and
8681); "after merge" marks what needs the merged engine; the rest needs the token. On 2026-11-22, after the merge and
with the product owner's token: steps 1.2 and 1.5 were run, the three hosts of step 4 started with `auth: github` and the
real upstream (a dummy token), and Live test C1 passed (`docs/build/copilot-results.md`), all *(checked after merge)*.
Steps 5 to 7, the console run itself, are the product owner's.

1. **Prerequisites**, as for the run above, except Claude Code and its token, and:
   1. The merged branch built: `dotnet build FeatureFlow.slnx -c Release`.
   2. The sandbox image rebuilt with Copilot CLI 1.0.107 (the Dockerfile pins it):
      `docker build -t feature-flow-sandbox:m8 -f src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile src/Providers/FeatureFlow.Providers.Docker`,
      then `docker run --rm --entrypoint copilot feature-flow-sandbox:m8 --version` says 1.0.107 *(checked after
      merge)*.
   3. The sandbox client published *(checked)*:
      `dotnet publish src/FeatureFlow.SandboxClient -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o ~/ff-live/sandbox-client`.
   4. The token, which the product owner creates and places himself: a fine-grained personal access token owned by
      his Xero GitHub account (an Enterprise Managed User), at github.com/settings/personal-access-tokens: resource
      owner the account itself, repository access "Public repositories", account permission **Copilot Requests**
      only, a short expiry. He adds it to `~/ff-live/secrets.json`, outside the repository, as
      `"copilot_github_token"`. It is never typed on a command line or printed.
   5. The upstream, the account's Copilot API, read with the token from the file and without printing it (no quota
      spent):

      ```sh
      jq -r '"Authorization: Bearer " + .copilot_github_token' ~/ff-live/secrets.json \
        | curl -sf -H @- https://api.github.com/copilot_internal/user | jq -r .endpoints.api
      ```

      Expect `https://api.business.githubcopilot.com` or `https://api.enterprise.githubcopilot.com`; for the product
      owner's Xero account it is `https://api.enterprise.githubcopilot.com` (plan `enterprise`) *(checked after merge)*.
      The same header on `GET <upstream>/models` lists the models; `claude-haiku-4.5` must be among them, with
      `/v1/messages` in its `supported_endpoints` (it is, as are `claude-haiku-5.5`, `claude-sonnet-5.5` and
      `claude-opus-5.5`) *(checked after merge)*. (With a dummy token the first command answers 401 and prints nothing
      of the token *(checked)*.)
2. **Configuration**, in `~/ff-live` beside the Claude Code files (`<…>` marks your values). All three hosts started
   with a copy and dummy values *(checked)*, and again after the merge with `auth: github` and the real upstream
   *(checked after merge)*; that `auth: github` is refused for `claude_code` and sent as `Bearer` is covered by the
   default tests.

   `installation-copilot.yaml`: `installation.yaml` from the run above, with this `secrets` and `agent_runtime`, and
   `task_queue: ff-a4-copilot`:

   ```yaml
     secrets: { default: file, instances: { file: { path: secrets.json } } }
     agent_runtime:
       default: copilot_cli
       instances:
         copilot_cli:
           model: claude-haiku-4.5
           model_access: { mode: proxy, key_ref: secrets/copilot_github_token, upstream: "<the upstream from step 1.5>", auth: github }
   ```

   `organisation-copilot.yaml`: `organisation.yaml` from the run above, with
   `role_mapping: { default: { agent_runtime: copilot_cli, model: claude-haiku-4.5 } }`.

   `secrets.json`: `{ "step_token_key": "<as before>", "copilot_github_token": "<placed by the product owner>" }`.
3. **Temporal**, as in the run above *(checked)*:
   `~/.temporalio/bin/temporal server start-dev --headless --port 7833 --http-port 7834 --metrics-port 7835 --ui-port 8833 --db-filename data/temporal.db`.
4. **The three hosts**, one terminal each in `~/ff-live`; no token in any environment *(checked: each started and
   listened)*:

   ```sh
   dotnet <checkout>/hosts/FeatureFlow.Host.OrchestrationWorker/bin/Release/net10.0/FeatureFlow.Host.OrchestrationWorker.dll --installation installation-copilot.yaml --organisation organisation-copilot.yaml
   dotnet <checkout>/hosts/FeatureFlow.Host.AgentWorker/bin/Release/net10.0/FeatureFlow.Host.AgentWorker.dll --installation installation-copilot.yaml --organisation organisation-copilot.yaml
   FF_API_LISTEN=http://127.0.0.1:8580 dotnet <checkout>/hosts/FeatureFlow.Host.Api/bin/Release/net10.0/FeatureFlow.Host.Api.dll --installation installation-copilot.yaml --organisation organisation-copilot.yaml
   ```

   The API lists `std:templates/ticket_delivery` *(checked)*.
5. **Open** `http://127.0.0.1:8580/console`, as in the run above (checked there; the Copilot check built without the
   console).
6. **The run, from the console only**, as step 6 of the run above: "Start a run", `std:templates/ticket_delivery`,
   `ticket_id` an open fixture issue, `engine_can_merge` off, Repositories `ai-feature-flow/feature-flow-example`;
   Start; answer every wait from the console. The run page's timeline shows `session 1 started on runtime copilot_cli
   with model claude-haiku-4.5`.
7. **Evidence to record**: as for the run above, and the agent worker's model requests (the step's agent events,
   `GET meta`, `GET models`, `POST v1/messages` with status 200) and the account's premium requests for the day
   (github.com, Settings, Billing and licensing, or `/usage` in a local `copilot`), to compare with a normal session
   (spike 0, open question 5).

Before the console run, the Copilot Live test C1 checks the same arrangement on its own: one real step that commits,
with the token's value in none of its sandbox's environment, homes, transcript or commits (`CONTRIBUTING.md`, Live
tests).

## Size

`src` +4,100 / −230 lines against `main` after A3 (70 files), of which 1,334 are the npm lockfile; tests +2,360. The
console's TypeScript (and its check script) is about 2,200 lines, with 300 of CSS.

## Deviations and judgement calls

- The design note's open questions were decided by the sub-orchestrator: Node is required for every `dotnet build`
  (LOG 453, flagged), and the browser tests run against a scripted engine, not a real run loop (LOG 461, flagged).
- The review's R1 (cross-site requests under `none`) was not in the plan; it is closed by LOG 469.
- A breakpoint is offered on a done step, because the engine accepts it (LOG 470, flagged).
- `clean_up` is offered with `go_to` and `replan`, not `end`, following the engine (LOG 465).
- An answer to an agent's question still needs no reason (LOG 392, A3); "a required reason on every answer" is read
  with that exception.
- Clearing a breakpoint stays in the plan panel; the decision form does not offer "continue and clear".

## Follow-ups

Follow-ups 2, 4 and 5 were built in the alpha follow-ups batch (`docs/build/alpha-follow-ups-results.md`).

1. Product owner: the live run above; LOG 453, 461 and 470.
2. `GET /runs/{id}/plan` answers 404 until the first plan revision is recorded, so a new run page logs one browser
   console error (the console handles it). Answer 200 with no plan, or let the run detail say whether a revision exists.
3. The timeline lists `answered` after `ended` when the answer ends the run (engine order).
4. A host's start-up refusal ends in an unhandled exception and exit 134, not a plain message and exit 1
   (`HostArguments.RunAsync`; not specific to A4).
5. `POST /runs` does not check required template parameters; a missing one fails in the engine.
6. `PlanStepView` and `RunView` lack attempt counts, a tracker issue and a start time (LOG 464, 466).
7. `FakeEngine` does not complete a gate's step or route the run after an answer (LOG 461); a real-engine browser test
   (Integration category) would close the gap if wanted.
8. `Two_workers_running_the_same_merge_land_it_once_and_both_succeed` (Git provider, from before A4) failed once under
   full-suite load.
