# Review 14 A2: code-host protection attempt

Captured 2026-10-29. Repository `ai-feature-flow/feature-flow-example`.

## Result

**Blocked: the ruleset was not created.** The GitHub App installation token does
not have the repository `administration` permission required by GitHub's create
repository ruleset endpoint.

The intended active branch ruleset targeted only `refs/heads/main`, had no bypass
actors, required all changes through a pull request, required one approving review,
required approval after the last push, dismissed stale approvals, and required
review-thread resolution. It did not target `ff/ticket/*`.

## Real output

Before the attempt, the installation token reported these permissions:

```text
permissions:
  actions: write
  checks: write
  contents: write
  metadata: read
  pull_requests: write
  workflows: write
```

The repository had no rulesets:

```text
HTTP 200
[]
```

Creating `POST /repos/ai-feature-flow/feature-flow-example/rulesets` with the
installation token was refused by GitHub:

```text
HTTP 403
{"message":"Resource not accessible by integration","documentation_url":"https://docs.github.com/rest/repos/rules#create-a-repository-ruleset","status":"403"}
```

A follow-up listing confirmed that the failed request changed nothing:

```text
HTTP 200
[]
```

## Required verification

The three behavioural checks were deliberately not run because their prerequisite
was absent:

1. App-token push or merge to `main`: **not run**. With no ruleset, the operation
   could succeed and change `main`.
2. Agent-token push or merge to `main`: **not run** for the same reason.
3. Agent-token push to `ff/ticket/*` followed by an app-token PR: **not run**. A
   successful result without the ruleset would not prove that the protection leaves
   ticket branches usable.

No test branch was created, so the fixture repository still has only `main`. PRs
#1-#4 were not changed. The final branch listing was:

```text
HTTP 200
[{"name":"main","commit":{"sha":"5dd0108fc9e9a3e4af18995fc68eed6f0a9231ac","url":"https://api.github.com/repos/ai-feature-flow/feature-flow-example/commits/5dd0108fc9e9a3e4af18995fc68eed6f0a9231ac"},"protected":false}]
```

## Permission needed

Grant the GitHub App **Repository permissions → Administration: Read and write**:

`https://github.com/organizations/ai-feature-flow/settings/apps/ai-feature-flow-agent-access/permissions`

After saving the permission change, an organisation owner must approve the updated
installation permissions here:

`https://github.com/organizations/ai-feature-flow/settings/installations/176108462`

Then repeat the ruleset creation and all three live checks. `contents:write` and
`pull_requests:write` are already present; neither grants ruleset administration.
