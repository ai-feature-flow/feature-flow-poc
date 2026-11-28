# Getting started

How to run Feature Flow on your own machine from a fresh clone, and start a first run on the fixture repository
`ai-feature-flow/feature-flow-example`. It covers macOS and Windows; Linux follows the macOS steps.

The engine runs as four local processes. The agents run in Docker containers (sandboxes). The engine writes to GitHub
as a GitHub App you create.

Status of this guide: the macOS steps and `deploy/local/ff.sh` were run on an Apple silicon Mac (section 10). The
Windows steps and `deploy/local/ff.ps1` were not run on Windows. Linux was not run.

## 1. What runs

| Process | Listens on | What it does |
|---|---|---|
| Temporal dev server | `127.0.0.1:7233`, UI on `127.0.0.1:8233` | Keeps each run's durable state |
| Orchestration worker | nothing | Runs each run's plan |
| Agent worker | `0.0.0.0:8481` | Starts an agent sandbox for each agent step and serves it the engine's endpoint |
| API and console | `127.0.0.1:8480` | The REST API and the web console |

Data is kept in your install folder (section 5): SQLite databases, run artifacts, logs. Runs survive a restart.

## 2. Prerequisites

| Need | Version | Install |
|---|---|---|
| git | any recent | macOS: `xcode-select --install`. Windows: <https://git-scm.com/download/win> |
| .NET SDK | 10.0.401 or a later 10.0 feature band (`global.json`) | <https://dotnet.microsoft.com/download/dotnet/10.0>. Check with `dotnet --version` |
| Node and npm | Node 22.12 or later | <https://nodejs.org>. `dotnet build` builds the web console with them |
| Docker | Docker Desktop, running, with Linux containers | <https://www.docker.com/products/docker-desktop>. On Windows, use the WSL 2 backend and do not switch to Windows containers |
| Temporal CLI | current | macOS: `brew install temporal`. Linux: `curl -sSf https://temporal.download/cli.sh \| sh`. Windows: download the zip from <https://temporal.io/download> and put `temporal.exe` on `PATH` |
| PowerShell 7 | 7 or later; Windows only | <https://aka.ms/powershell>. Windows PowerShell 5.1 does not run `ff.ps1` |
| Claude Code | current; Claude subscription only | `npm install -g @anthropic-ai/claude-code`. Needed once, to create a token (section 4.2) |

The machine's architecture decides the sandbox client built in section 3: `linux-arm64` for Apple silicon, `linux-x64`
for Intel Macs and x64 Windows. `ff.sh` and `ff.ps1` read it from Docker; you do not choose it.

## 3. Clone and build

```sh
git clone https://github.com/ai-feature-flow/feature-flow-poc.git
cd feature-flow-poc
```

Build everything the processes need:

```sh
./deploy/local/ff.sh build
```

On Windows:

```powershell
./deploy/local/ff.ps1 build
```

The command does three things:

1. Builds the solution in Release (`dotnet build FeatureFlow.slnx -c Release`), including the web console.
2. Publishes the sandbox client, a single Linux file `ff-sandbox` for Docker's architecture, into `sandbox-client/` in
   the install folder. Each sandbox gets a copy.
3. Builds the sandbox image `feature-flow-sandbox:local`: Node, git, Claude Code and Copilot CLI, with an unprivileged
   user. The first build downloads the .NET and npm packages and the base image, so it is slow.

## 4. Credentials

You need two credentials: a GitHub App for the engine's writes to GitHub, and model access for the agents.

### 4.1 The GitHub App

The engine reads and writes the fixture repository through a GitHub App: it commits to branches, opens pull requests,
reads and updates issues, and reads checks. The only supported credential for this is a GitHub App
(`credential_broker.github_app`). A fine-grained personal access token cannot be used here. The agents get no GitHub
credential beyond read access to the repository.

1. Create the App under your account or organisation: Settings, Developer settings, GitHub Apps, New GitHub App. Give
   it any name. Turn webhooks off.
2. Set these repository permissions, the most the engine ever asks for, and leave all others at no access:

   | Permission | Access |
   |---|---|
   | Contents | Read and write |
   | Pull requests | Read and write |
   | Issues | Read and write |
   | Actions | Read and write |
   | Checks | Read-only |
   | Metadata | Read-only (always on) |

3. On the App's page, note the **App ID**, then **Generate a private key**. GitHub downloads a `.pem` file. Keep it
   outside the repository, for example `~/keys/feature-flow-app.pem`.
4. Install the App on the repository the runs work on. The installation page's address ends in the **installation ID**:
   `https://github.com/settings/installations/<installation ID>` (for an organisation,
   `https://github.com/organizations/<organisation>/settings/installations/<installation ID>`).

The fixture repository is in the `ai-feature-flow` organisation. A run commits to it and opens pull requests. To run
without writing there, fork it, install the App on the fork, and set `repositories` and `repository` in the
installation file (section 5) to the fork.

### 4.2 Model access

Choose one.

**Claude Code, on a Claude subscription or an Anthropic API key.**

- Subscription: run `claude setup-token` and sign in when asked. It prints a long-lived token. Keep it as
  `claude_model_token` in `secrets.json` (section 5). Do not paste it on a command line.
- API key: create the key in the Anthropic console, keep it as `claude_model_token`, and change `auth: oauth` to
  `auth: api_key` in the installation file.

Use `deploy/local/installation.claude.yaml`. Agents run Claude Code with model `sonnet`.

**Copilot CLI, on a Copilot quota.**

1. At <https://github.com/settings/personal-access-tokens>, create a fine-grained token owned by the account whose
   Copilot seat pays. Repository access: Public repositories. Account permission: **Copilot Requests**, and no other.
   Give it a short expiry. Copilot refuses a classic token (`ghp_...`). Keep it as `copilot_github_token` in
   `secrets.json`.
2. Find your account's Copilot API address, reading the token from the file so that it is not printed. macOS or Linux,
   with `jq`:

   ```sh
   jq -r '"Authorization: Bearer " + .copilot_github_token' "$FF_HOME/secrets.json" \
     | curl -sf -H @- https://api.github.com/copilot_internal/user | jq -r .endpoints.api
   ```

   Windows:

   ```powershell
   $t = (Get-Content "$env:FF_HOME/secrets.json" | ConvertFrom-Json).copilot_github_token
   (Invoke-RestMethod https://api.github.com/copilot_internal/user -Headers @{ Authorization = "Bearer $t" }).endpoints.api
   ```

   The answer is an address such as `https://api.business.githubcopilot.com`. Put it in `upstream` in the installation
   file. The command spends no quota.
3. Set `model` to a name in your Copilot catalogue that supports `/v1/messages` (`docs/07` section 6.1 explains the
   choice). `claude-sonnet-5.5` is set.

Use `deploy/local/installation.copilot.yaml`.

## 5. Configure

The install folder holds your configuration, secrets, data and logs. It is not in the repository. The default is
`~/feature-flow-local`; set `FF_HOME` to use another. `ff.sh` and `ff.ps1` read `FF_HOME`, so set it in every terminal
you use them from, or leave it unset for the default.

Create the folder and copy the examples in. macOS or Linux, for Claude Code (use `installation.copilot.yaml` for
Copilot):

```sh
export FF_HOME=~/feature-flow-local
mkdir -p "$FF_HOME"
cp deploy/local/installation.claude.yaml "$FF_HOME/installation.yaml"
cp deploy/local/organisation.yaml "$FF_HOME/organisation.yaml"
cp deploy/local/secrets.example.json "$FF_HOME/secrets.json"
chmod 600 "$FF_HOME/secrets.json"
openssl rand -hex 32   # a value for step_token_key
```

Windows:

```powershell
$env:FF_HOME = "$HOME\feature-flow-local"
New-Item -ItemType Directory -Force $env:FF_HOME | Out-Null
Copy-Item deploy/local/installation.claude.yaml "$env:FF_HOME/installation.yaml"
Copy-Item deploy/local/organisation.yaml "$env:FF_HOME/organisation.yaml"
Copy-Item deploy/local/secrets.example.json "$env:FF_HOME/secrets.json"
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))   # a value for step_token_key
```

Then edit the three files in the install folder. Replace every `<ANGLE BRACKET>` value; the files hold no others.

| File | Edit |
|---|---|
| `installation.yaml` | `app_id`, `installation_id` and `key_path` from section 4.1 (on Windows write the path with forward slashes: `C:/Users/you/keys/app.pem`). `actor`: your name, which the console shows. For Copilot, `upstream` from section 4.2. If you use a fork, `repositories` and `repository` |
| `organisation.yaml` | `<CHECKOUT>`: the absolute path of your clone, with forward slashes. It names the standard library, `library/std` |
| `secrets.json` | Replace the descriptive text with values: `step_token_key` (the random value above) and either `claude_model_token` or `copilot_github_token`. Delete the other entries. Never commit this file, and keep it outside any repository |

The files are plain YAML and JSON. Relative paths in `installation.yaml` (`data/`, `clones`, `sandbox-client/`,
`secrets.json`) are relative to the install folder. Each host checks the configuration when it starts and names any
fault in its log (`logs/`). The settings are described in `docs/07`.

`api.auth.mode: none` means the console asks nobody to sign in. The API host refuses to start on any address except
the loopback one, which is what `ff.sh` sets, so the console is not reachable from other machines.

The agent worker listens on all interfaces, port 8481, because the sandboxes reach it as `host.docker.internal`. On
Windows, allow `dotnet.exe` through the firewall for private networks when Windows asks.

## 6. Start

```sh
./deploy/local/ff.sh start
```

On Windows:

```powershell
./deploy/local/ff.ps1 start
```

It starts Temporal and the three hosts in the background, waits a few seconds, and prints their status. All four must
say `running`. If one says `stopped`, read its log (section 9).

The console is at <http://127.0.0.1:8480/console>. Temporal's own UI is at <http://127.0.0.1:8233>.

## 7. First run

The template `std:templates/ticket_delivery` takes one ticket from plan to merge into the default branch. It stops for
your decision wherever the organisation file requires an approval, and the console shows each stop.

1. In the fixture repository, open an issue that describes a small change, for example "Add a `/health` endpoint
   returning 200". Note its number.
2. In the console, choose **Start a run**.
3. Fill the form:

   | Field | Value |
   |---|---|
   | Template | `std:templates/ticket_delivery` |
   | `ticket_id` | the issue number, for example `12`, or `ai-feature-flow/feature-flow-example#12` |
   | `engine_can_merge` | off. A person merges |
   | Repositories | `ai-feature-flow/feature-flow-example` (or your fork) |

4. Choose **Start**. The run page opens with the plan and its timeline.
5. When the run needs you, **Needs me** at the top shows a count. Open it, read the prompt and the artifacts it
   refers to, give a reason, and choose an outcome. The plan, the pull request and the review all come back to you
   this way.

A run spends model quota. To end one early, use **Abort** on its page and give a reason.

## 8. Stop, restart, update

| Command | Effect |
|---|---|
| `ff.sh status` | Shows the commit, each process's state and the addresses |
| `ff.sh stop` | Stops the three hosts. Temporal keeps running |
| `ff.sh stop --all` | Stops the hosts and Temporal |
| `ff.sh restart` | Stops and starts the three hosts. Runs resume |
| `ff.sh update` | Fast-forwards the clone to `origin/main`, stops, builds, starts. Does nothing if already current; `--force` rebuilds anyway |
| `ff.sh build` | Rebuilds without updating |
| `ff.sh logs [api\|agent\|orchestration\|temporal]` | Follows a log; default `api` |

On Windows, the same commands are `ff.ps1 status`, `ff.ps1 stop -All`, `ff.ps1 update -Force`, and so on. The only
differences from `ff.sh` are the switch spellings (`-All`, `-Force`) and that each log has a second file,
`<name>.err.log`, for its error output.

Both scripts take their settings from environment variables: `FF_HOME` (install folder), `FF_SRC` (the clone, default
the one the script is in), `FF_TEMPORAL_PORT` (default 7233), `FF_API_PORT` (default 8480), `FF_IMAGE` (the sandbox image
tag), `FF_TEMPORAL` (the Temporal CLI), `FF_INSTALLATION` and `FF_ORGANISATION` (file names). The script header lists
them. If you change `FF_TEMPORAL_PORT`, change `temporal.address` in `installation.yaml` to match. If you change the
agent worker's port, change `listen` and `urls` in the same file.

To remove everything: `ff.sh stop --all`, then delete the install folder. To keep the install but clear cloned
repositories, delete `clones/` while the hosts are stopped.

## 9. If something fails

| Symptom | Cause and fix |
|---|---|
| A host says `stopped` after `start` | It stopped itself at its start-up checks. `ff.sh logs api` (or `agent`, `orchestration`) shows the reason, in plain words |
| `the sandbox image ... is not present` | Run `ff.sh build`, or start Docker Desktop and then run it |
| `GitHub has no such App installation` or `the GitHub App key does not read` | `app_id`, `installation_id` or `key_path` is wrong, or the App is not installed on the repository |
| A host names a secret it cannot read | A name in `installation.yaml` (`secrets/<name>`) is missing from `secrets.json` |
| `dotnet build` fails in the console step | Node or npm is missing or older than 22.12. Install it, or build without the console with `dotnet build FeatureFlow.slnx -c Release -p:ConsoleBuild=false` (`/console` then answers 404) |
| An agent step fails to reach the engine | The agent worker is not reachable as `host.docker.internal:8481`: a firewall, or a changed port in only one of `listen` and `urls` |
| Model requests answer 502 and name `UntrustedRoot` | The network re-signs TLS. See "Behind a TLS-inspecting proxy" in `deploy/README.md` |
| Copilot refuses the model | `model` is not in your catalogue. List the catalogue with `GET <upstream>/models`, sending the token as `Authorization: Bearer` |

## 10. What was tested

| Part | Result |
|---|---|
| `installation.claude.yaml`, `installation.copilot.yaml`, `organisation.yaml` | Load and compose with every host's providers in the default test suite (`DeploymentConfigurationTests`). The tests also check that `secrets.example.json` has every secret the installations name |
| `ff.sh` build, start, status, restart, stop, stop `--all` | Run on macOS (Apple silicon, Docker Desktop, `linux-arm64`), against ports 7733, 8780 and 8781, with a configuration that used the in-memory providers in place of GitHub, and no model call. The console answered 200 |
| A run on the fixture repository | Not run from this guide or a clean clone. The same configuration shape, in a folder outside the repository, is the one live runs used on macOS (`docs/build/a4-results.md`, `docs/build/copilot-results.md`). The GitHub App settings and the Claude subscription path of this guide were not exercised here |
| `ff.ps1` | Run with `pwsh` 7.7 on macOS: it read its settings and the git state, then stopped at `Get-CimInstance`, a cmdlet that exists only on Windows. Its process detection, start, stop, build and logs were not run. Nothing of it was run on Windows |
| Intel Mac, x64 Windows, Linux | Not run. The only difference from the tested path is the sandbox client's `linux-x64` target, chosen from Docker |

## Open questions

None open.
