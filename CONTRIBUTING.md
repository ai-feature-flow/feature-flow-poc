# Contributing

How work on this design repository is done. Written 2026-10-31 from the Spike 3
handover, which it replaces.

## Where things are

| What | Where |
|---|---|
| The rewrite plan and its status | `docs/PLAN.md` (start here) |
| Design documents | `docs/00` (principles) to `docs/10` (decisions, risks, spike plan) |
| The version 1 build plan | `docs/11-build-plan.md` |
| The alpha and first full release plan | `docs/12-build-plan-phase-2.md` |
| Milestone results notes and handovers | `docs/build/` |
| Judgement calls made without the product owner; approvals are recorded there | `docs/LOG.md` |
| The first design | `archive/docs/`, read-only. Spike results there are the evidence the new design cites: `archive/docs/15` (Spike 1), `16` (Spike 2 and the Claude Code proof), `17` and `18` (Spike 3) |
| Reviews from the product owner | `archive/docs/reviews/` for the first design; later reviews arrive in pull requests or in `docs/build/` |
| Spike results for the new design (SP1 to SP5) | `docs/spikes/` |
| Spike code | `spikes/01-interpreter` to `spikes/08-model-proxy` |
| Engine code | `src/`, the hosts in `hosts/`, tests in `tests/`, the standard library in `library/std/` |
| Fixture repositories | `ai-feature-flow/feature-flow-example`, a small .NET API the spikes run against, and `ai-feature-flow/feature-flow-example-client`, its client library (from M6, for runs over two repositories) |

## Writing

- **Keep documents short**, and write for a reader who has not just read
  everything else.
- **Explain every reference where it is used, in a few words.** Not "E3 is met
  (R10, section 1.3)", but "E3, no ticket blocked by another's failure, is
  met". The reader should never have to jump to another section to follow a
  sentence.
- **One fact, one place** (`docs/00`, P12). Explaining a reference means naming
  the rule and its effect in one sentence, not restating it. The first design
  restated rules in up to 13 documents, and the copies came to disagree.
- **End each deliverable with an open questions section.** The product owner
  answers in the next review. When all are answered, say "None open" and keep
  the answers.
- **Flag judgement calls.** Anything decided without being asked is listed as
  such, so it can be reviewed rather than looking settled.
- **Check cross-references after editing.** A stale section number is worse
  than none.

## Development and decision guidelines

From the product owner, 2026-11-03. Apply them when designing code structure,
implementing features and making design decisions.

- **Prefer simple designs and repeated patterns.** Solve a new problem the way a
  similar one is already solved in the code, unless that clearly doesn't fit.
- **Nothing is set in stone.** If the design calls for something that adds a lot
  of complexity, and a simpler solution would work as well, challenge the
  design: propose the simpler shape and record the change as a LOG entry.
- **Prefer a general solution to special cases.** Avoid complex code for special
  or edge cases when a more general mechanism covers them.
- **Keep it pluggable and extensible.** External systems sit behind provider
  interfaces with contract tests, and new behaviour should arrive as a new
  provider, action or library content rather than as engine special cases.
- **Keep the safety floors** (`docs/05` section 1), **but be pragmatic about
  them.** Close the major loopholes and the cheats an agent is likely to try.
  Don't build complex defences against every unlikely way an agent might game a
  check: requirements testing and traceability ("does the product meet its
  requirements?") catch what slips through. Weigh a defence's complexity against
  how likely and how harmful the cheat is.

## The review cycle

Design and code work (`src/`, `hosts/`, `tests/`, `docs/`) go through pull
requests, which merge only when CI passes (`docs/11` section 4.1; while main development goes on, a pull
request runs only the default tests unless labelled `full-ci`).

## Commits

- Stage files explicitly. Never `git add -A`: it has swept up scratch files
  before.
- Say in the message what was verified and how.
- End every message with:
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`

## Verify, do not assert

This is the convention that has caught the most mistakes.

- **A check that passes by construction proves nothing.** Before trusting a
  result, ask what would make it come out differently. If nothing could, the
  check is circular.
- **Check the method on an obvious case first.** For example, run the merge
  check with the lock bypassed and confirm it does see overlaps.
- **Never substitute canned content for the thing under test.** If a
  credential is missing, stop and ask. Stopping to ask is the agreed working
  model, not a failure.
- **Verify what helper agents claim.** Check the decisive evidence yourself (the
  commit on the remote, the session transcript, the record) before repeating it.
- **Fix the method, then pre-register.** If a measurement turns out flawed, fix
  it, write down the thresholds before re-running, and keep the flawed result as
  history (Spike 3 did this for its review measurement).

## Spikes

Spikes prove a design concept is feasible. A spike review asks only two
questions: does the evidence support the feasibility claim, and did the spike
teach something that changes the design? Production concerns found along the
way are recorded as design requirements (`docs/10` section 2), not fixed in
spike code. Keep spike code as cheap as the evidence allows.

## Working with helper agents

One agent coordinates and integrates; helper sessions each own one piece of
work. Give each helper its own Temporal port, task queue and directory where it
touches shared infrastructure: in Spike 1, two agents sharing a worker corrupted
a run.
How a milestone is split, briefed, reviewed and merged, and how the coordinator
decides, is in the latest handover (`docs/build/handover-2026-11-05-v1.md`,
sections 3 and 4).

## Local setup (macOS)

To run the engine from a clone, on macOS or Windows, see `docs/getting-started.md`. The table below is what
development needs.

| Need | Detail |
|---|---|
| .NET | SDK 10. All spike code targets `net10.0`. The fixture targets `net9.0`; run its tests with `DOTNET_ROLL_FORWARD=Major` |
| Node (A4) | Node 26 and npm on `PATH`. `dotnet build` builds the console (`src/FeatureFlow.Console`) with them, and fails with an install hint without them. To build without Node, pass `-p:ConsoleBuild=false`: npm is skipped, and `/console` then serves whatever `src/FeatureFlow.Console/dist` already holds, or answers 404 |
| Console development | In `src/FeatureFlow.Console`, `npm run dev` serves the sources at `http://localhost:5173/console/` with hot reload and passes the API's routes to a local API at `FF_API` (default `http://127.0.0.1:8480`). The client types are generated from `src/FeatureFlow.Api/openapi.json` by every build and are not committed. Add packages at exact versions; commit `package-lock.json`. `npm test` runs the console's unit tests (`tests/*.test.ts`, Node's own runner); `npm run build` runs them when the console is rebuilt, and `ConsoleUnitTests` in `tests/FeatureFlow.Console.Tests` runs them in the default suite every time (skipped, saying why, only where npm is not installed) |
| Console browser tests (A4) | `tests/FeatureFlow.Console.Tests` drives the console in Chromium with Playwright, against the API in process and a scripted engine (no Temporal), and runs the console's unit tests (`npm test`, `ConsoleUnitTests`). They are in the default category, so `dotnet test` runs them. The first run downloads Chromium into `~/Library/Caches/ms-playwright`; later runs reuse it. Only these tests: `dotnet test tests/FeatureFlow.Console.Tests`. To watch one: `HEADED=1 dotnet test tests/FeatureFlow.Console.Tests --filter "FullyQualifiedName~LiveTests"` (a visible, slowed browser) |
| Temporal | CLI at `~/.temporalio/bin/temporal`. Dev server ports used so far: 7233 (Spike 1), 7333 (Spike 2), 7533 (Spike 3) |
| Agent runtime | `claude` on `PATH`, signed in to the Anthropic subscription. The spikes use model `haiku` |
| Secrets | `~/keys/feature-flow-secrets.json`, outside the repository, passed as `FF_SECRETS_FILE`. It holds the GitHub App id (5507595) and installation id (176108462), and the path to the App's private key. Never commit a credential, and never print one |
| Pushing | `origin` uses an SSH host alias that can push to both the design and the fixture repository |
| Sandbox image (M8) | `feature-flow-sandbox:m8`, from `src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile`. The integration tests build it if it is absent, and publish the sandbox client for the Docker host's architecture |
| Live tests (M4) | `dotnet test tests/FeatureFlow.Live.Tests` with `FF_TEMPORAL_ADDRESS`, `FF_SECRETS_FILE` and `CLAUDE_CODE_OAUTH_TOKEN` set. They spend model tokens and push a branch and a hidden reference to the fixture repository, which each test removes. The Copilot tests (`LiveCopilotTests`) spend Copilot quota instead and need, in place of `CLAUDE_CODE_OAUTH_TOKEN`, `FF_COPILOT_GITHUB_TOKEN` (a fine-grained personal access token with only Copilot Requests, kept in a secrets file outside the repository; never `COPILOT_GITHUB_TOKEN`, which a local `copilot` would pick up) and `FF_COPILOT_API_URL` (the account's Copilot API). Set them from the file without printing them, and run C1 alone: `FF_COPILOT_GITHUB_TOKEN=$(jq -r .copilot_github_token ~/ff-live/secrets.json) FF_COPILOT_API_URL=<upstream> dotnet test tests/FeatureFlow.Live.Tests --filter "FullyQualifiedName~A_copilot_implementer_step"` (`docs/build/a4-results.md`, "Live run with Copilot CLI"; LOG 498) |

## Design principles worth not losing

Each has bitten the project or its prototype at least once.

- **An interruption must become a continuation, never a wrong outcome.** A run
  that ends without reaching an `end:` route must not report `completed`
  (floor F9, `docs/05` section 1).
- **Infrastructure failures never use up a ticket's attempts.** A long outage
  stalls and alerts; it never fails on its own.
- **Events wake, reads confirm.** An event is a hint that something may have
  changed, never the source of truth.
- **Repository facts are read at each step, never copied into the plan.** The
  prototype went stale exactly this way.
- **Privilege comes from the role, never the step.** An agent cannot grant
  itself write access or tools by editing its own plan.
- **A safety rule must not depend on a file the governed party can edit.**
  Spike 2's merge check read the default branch from a file the agent could
  change.
- **Who made a decision comes from the authenticated identity, never the
  request body.** That is what makes "only a human merges into `main`" (the default of
  floor F1, `docs/05` section 2) enforceable.
- **Agents never hold a credential that can push,** and model credentials stay
  out of the agent's environment (floors F5 and F6, `docs/05` section 1).
