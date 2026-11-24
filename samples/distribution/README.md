# Sample distribution: Acme

An organisation's distribution of Feature Flow, built from the engine's packages alone (`docs/06` section 4). Acme adds
a provider of its own, a validator, without forking the engine, and tests a workflow that uses it.

| Folder | What it is |
|---|---|
| `Acme.Providers/` | Acme's provider: a `validator` of kind `acme`, offering `acme_line_limit` (no subject longer than `max_lines` lines). References `FeatureFlow.Providers.Abstractions` only |
| `Acme.Host/` | Acme's host: one process running the orchestration worker, the agent worker and the API, composing `StandardProviders.Factories`, `TestingProviders.Factories` (refused outside `development: true`) and `AcmeProviders.Factories` |
| `Acme.Host.Tests/` | Acme's workflow test: the host in process against a Temporal dev server, with a development installation, runs `acme:templates/checked_notes` twice; notes within the limit complete, notes over it are rejected, and the validator ran both times |
| `library/` | Acme's library: `checked_notes` imports `notes.md` from a repository at a commit, then checks it with `acme_line_limit`. It needs nothing of std |
| `Directory.Build.props`, `Directory.Packages.props`, `NuGet.config` | The sample's own build settings, so it never inherits the engine repository's: `FeatureFlow.*` from the local feed only, everything else from nuget.org |

## Build and test it

From the engine repository's root, pack the engine to the local feed the sample's `NuGet.config` names (nothing is
published anywhere), then test the sample against a Temporal dev server:

```sh
dotnet pack FeatureFlow.slnx -c Release -o artifacts/packages     # add -p:ConsoleBuild=false without Node
~/.temporalio/bin/temporal server start-dev --port 7233 --headless &
cd samples/distribution
NUGET_PACKAGES=$PWD/.packages FF_TEMPORAL_ADDRESS=localhost:7233 dotnet test Acme.Host.Tests/Acme.Host.Tests.csproj
```

Every pack has the same version (0.1.0), so use a package cache of your own (`NUGET_PACKAGES`) or clear
`FeatureFlow.*` from yours after packing again; otherwise NuGet serves the earlier pack. To build against another
version, pack with `-p:Version=<v>` and build with `-p:FeatureFlowVersion=<v>`.

The engine's `DistributionTests` (Integration category) does all of this in a temporary folder outside the repository,
and checks that the sample's assets resolve every `FeatureFlow.*` as a package and no project but the sample's own.

## Running the host

```sh
dotnet run --project Acme.Host -- --installation <installation.yaml> --organisation <organisation.yaml>
```

The installation names the acme validator as an instance, as any provider: `validator: { default: acme, instances:
{ acme: {} } }`, or beside the standard validators as `{ default: std, instances: { std: {}, acme: {} } }`; a step's
`artifact.validate` finds the instance that offers the validator it names.
