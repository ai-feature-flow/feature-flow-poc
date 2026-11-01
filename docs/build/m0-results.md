# M0: skeleton

Status: built 2026-11-02 on branch `claude/m0-skeleton-70c559`. Every local
check holds. The CI run is not yet seen: the pull request could not be opened
from this machine (section 5).

## 1. What was built

| Part | Detail |
|---|---|
| Solution | `FeatureFlow.slnx` with every project of `docs/11` section 2.3, empty but compiling: 25 under `src/` (11 of them in `src/Providers/`), 3 under `hosts/` |
| Tests | One test project per `src` project, mirroring its folder; `Replay.Tests`, `Integration.Tests`, `Live.Tests`; and `Architecture.Tests` for the reference rules |
| Build settings | `global.json` pins SDK 10.0.401. `Directory.Build.props`: `net10.0`, nullable, warnings as errors, deterministic, `ContinuousIntegrationBuild` on GitHub Actions, version 0.1.0. `Directory.Packages.props`: central versions. `tests/Directory.Build.props` makes every test project an xUnit v3 executable |
| Reference-rule test | `tests/FeatureFlow.Architecture.Tests` reads each project file under `src/` and `hosts/` and applies the table of section 2.3. It also fails when a project is in neither the table nor the solution, and when a shared build file outside `tests/` adds a reference the project files would not show |
| CLI | `ff --version` prints `ff <informational version>` and exits 0; anything else prints usage and exits 2 |
| CI | `.github/workflows/ci.yml`, on every pull request and on pushes to `main`. Job 1 builds and runs every test not tagged `Integration` or `Live`. Job 2 installs the Temporal CLI, starts a dev server, runs `docker version`, and runs the `Integration` category. `Live` runs nowhere in CI |

Test categories are xUnit traits: `[Trait("Category", "Integration")]` and
`[Trait("Category", "Live")]`. Untagged tests are the default category.

## 2. Checks

Each check was seen to fail when the thing it guards was broken, then to pass
once the change was reverted (`CONTRIBUTING.md`, "Verify, do not assert").

| # | Check | Broken by | Seen |
|---|---|---|---|
| C1 | Reference rules hold on the real project files | `Interpreter` given a `PackageReference` to `Temporalio` | Fails: `FeatureFlow.Interpreter references package Temporalio: the core has no Temporal reference`. Passes after revert |
| C2 | Same test, project references | `Interpreter` given a `ProjectReference` to `Engine` | Fails: `the core may reference only core projects before it` |
| C3 | Every project is classified and in the solution | An unlisted `src/FeatureFlow.Stray` project | Both layout tests fail |
| C4 | Shared build files add no references | `Temporalio` added to the root `Directory.Build.props` | Fails |
| C5 | `ff --version` runs | | `ff 0.1.0+4dbeaa3…`, exit 0; no arguments gives usage, exit 2. Also a unit test in `Cli.Tests` |
| C6 | The integration job needs Temporal and Docker | No dev server running | Both environment tests fail. With a dev server on port 7733 the Temporal test passes. Docker was not running on this Mac, so the Docker test's pass is left to CI |
| C7 | The default filter excludes `Live` | A failing test tagged `Live` | Not run by the default filter; run and failing without it |
| C8 | Spikes still build with their own settings | | `spikes/04-continue-as-new` builds with 0 warnings |
| C9 | CI passes on the pull request | | **Not yet seen** (section 5) |

The default category has 21 tests: 17 in `Architecture.Tests` (the real-file
checks plus allowed and forbidden cases of each rule) and 4 in `Cli.Tests`.

## 3. Implementation choices

None changes the design, so none has a LOG entry.

| Choice | Reason |
|---|---|
| `Architecture.Tests` is a test project section 2.3 does not list | The reference rules belong to no single `src` project |
| No object store provider project | Which one is open question 4, deferred by the product owner |
| The table is applied literally: `Engine`, `Engine.Agent` and `Api` may not reference each other or `Hosting`; hosts reference only `Hosting` for now | The table says so. If M3 needs, say, `Api` to reference `Engine`, that is a change to section 2.3 and gets a LOG entry |
| `Testing.Workflows`, `SandboxClient`, `Hosting` and `Cli` are listed as unconstrained | Section 2.3 gives them no rule. Listing them keeps "a project not in the table fails" |
| Provider rule applies to `Providers.Abstractions` and `Providers.Contracts` too | Both are `Providers.*`; Abstractions needs at most `Model`, Contracts at most Abstractions and `Model` |
| Two environment tests in `Integration.Tests` (Temporal answers, Docker answers) | M0 has no integration tests, so the integration job would otherwise pass on a runner without Temporal or Docker |
| `spikes/Directory.Build.props` and `spikes/Directory.Packages.props`, both empty | They stop the root files applying to spike projects, which pin their own package versions. No spike code changed |
| Temporalio 1.21.0 (the spikes' version); xUnit v3 3.2.2 with the VSTest runner | Known to work here; the plan allows either Temporalio version |

## 4. Out of scope

Code in the projects (M1 onward); `ff validate` and `ff show` (M1); branch
protection making the CI jobs required on `main`, which is a repository
setting.

## 5. Open questions

| # | Question |
|---|---|
| 1 | The pull request could not be opened: `gh` and the GitHub connector on this machine are signed in only as `stuart-parkinson_xero`, an Enterprise Managed User, and GitHub refuses it (`As an Enterprise Managed User, you cannot access this content`). The branch was pushed over SSH. Who opens the pull request, or which account should `gh` use? |
| 2 | Branch protection on `main` should require the two CI jobs, *Build and default tests* and *Integration tests*. That needs repository admin rights |
