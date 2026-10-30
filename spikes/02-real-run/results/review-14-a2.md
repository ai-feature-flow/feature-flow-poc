# Review 14 A2: code-host protection for `main`

Captured 2026-10-29. Repository `ai-feature-flow/feature-flow-example`.

## Result

**Done. The ruleset was created and all three checks pass.**

The product owner granted the GitHub App **Repository permissions →
Administration: Read and write**, which was the blocker recorded in the previous
version of this file. A fresh installation token now carries
`administration: write`, and the ruleset was created with it.

| | |
|---|---|
| Ruleset | `protect-main`, id **26099165** |
| Target | `branch`, `enforcement: active` |
| Applies to | `refs/heads/main` only |
| Bypass actors | **none** (`current_user_can_bypass: never`) |
| Rules | `pull_request` (1 approving review, dismiss stale on push, require last-push approval, require review-thread resolution), `non_fast_forward` |

It does **not** target `ff/ticket/*` or `ff/epic/*`. The engine and the agents
can still push there and open pull requests, which case 3 proves.

## The token

Minted after the permission change, as the task required:

```text
permissions:
  actions: write
  administration: write     <-- the permission that was missing
  checks: write
  contents: write
  metadata: read
  pull_requests: write
  workflows: write
```

## Real output

### Case 1: the app's token cannot push or merge to `main`

A direct push to `main` with the **engine's** installation token:

```text
HTTP 409
{"message":"Repository rule violations found\n\nChanges must be made through a pull request.\n\n","metadata":{},"documentation_url":"https://docs.github.com/rest/repos/contents#create-or-update-file-contents","status":"409"}
```

Merging a pull request into `main` with the **engine's** token:

```text
HTTP 405
{"message":"Repository rule violations found\n\nNew changes require approval from someone other than the last pusher.\n\n","documentation_url":"https://docs.github.com/rest/pulls/pulls#merge-a-pull-request","status":"405"}
```

This is the exact operation PR #3 performed. It is now refused by the code host,
not only by the engine (A1).

### Case 2: an agent's token cannot push or merge to `main`

The agent token was minted through the same App, narrowed to one repository and
the agent grant (`contents:write`, `pull_requests:write`, `metadata:read`) — the
grant `GitHubAppCredentialBroker` issues. Its permissions as the remote reported
them:

```text
agent token permissions: {"contents": "write", "metadata": "read", "pull_requests": "write"}
```

A direct push to `main` with that token:

```text
HTTP 409
{"message":"Repository rule violations found\n\nChanges must be made through a pull request.\n\n","metadata":{},"documentation_url":"https://docs.github.com/rest/repos/contents#create-or-update-file-contents","status":"409"}
```

Merging the same pull request into `main` with that token:

```text
HTTP 405
{"message":"Repository rule violations found\n\nNew changes require approval from someone other than the last pusher.\n\n","documentation_url":"https://docs.github.com/rest/pulls/pulls#merge-a-pull-request","status":"405"}
```

### Case 3: `ff/ticket/*` still works

The agent token created the branch, pushed a commit to it, and the engine's
token opened a pull request from it:

```text
--- agent token creates branch ff/ticket/a2-verify
HTTP 201
{"ref":"refs/heads/ff/ticket/a2-verify", ...}

--- agent token PUT contents on branch=ff/ticket/a2-verify
HTTP 201
{"content":{"name":"a2-probe-ticket.txt","path":"a2-probe-ticket.txt", ...}}

--- app token opens a pull request from the ticket branch
HTTP 201
{"url":"https://api.github.com/repos/ai-feature-flow/feature-flow-example/pulls/5", ... "number":5,"state":"open" ...}
```

The pull request was left open and unmerged, and its state confirms the
protection is what is holding it, not a conflict:

```text
  number:          5
  state:           open
  mergeable:       True
  mergeable_state: blocked
  merged:          False
```

`mergeable: True` with `mergeable_state: blocked` is the useful pair: the diff
applies cleanly, and the only thing stopping the merge is the ruleset.

## Residual risk: the app can edit the ruleset

The permission that let the app create the ruleset also lets it change it. A
`PUT` carrying the ruleset's own definition back unchanged was accepted:

```text
--- PUT /repos/ai-feature-flow/feature-flow-example/rulesets/26099165 (no-op: the same definition)
HTTP 200
```

The probe deliberately sent the definition the remote had just returned, so it
could not weaken the protection it was measuring, and a follow-up `GET`
confirmed the ruleset was unchanged.

So this ruleset is **not** a control the engine cannot reach. It is a backstop
against an engine that is *wrong*, not against an engine that is *hostile*: a
compromised App key could delete it and merge to `main`. Two things narrow that:

- The App key lives outside the repository (floor F2) and is held only by the
  engine's secrets provider.
- The engine's own broker refuses to mint `administration` to an agent: the
  grantable set in `RunContext` is `contents:write`, `contents:read`,
  `pull_requests:write`, `actions:write`, `metadata:read`. An agent cannot
  obtain a credential that could edit the ruleset.

Closing the gap properly needs a control outside the App's reach — an
organisation-level ruleset, or an owner-held credential the engine never sees.
Recorded here rather than fixed, because it is a design decision for the product
owner, not a spike change.

## Note on branch protection

The legacy branch-protection endpoint still reports `main` as unprotected:

```text
--- GET /repos/ai-feature-flow/feature-flow-example/branches/main/protection
HTTP 404
{"message":"Branch not protected", ...}
```

That is expected and not a gap: rulesets and classic branch protection are
separate mechanisms, and the protection here is the ruleset. The 404 is recorded
so a later reader does not mistake it for the ruleset having been lost.

## Reproducing

```powershell
$k = $env:FF_GITHUB_APP_KEY        # path to the App private key, outside the repository

# the three checks, with cleanup of the probe branch and pull request
python tools\gh_ruleset_verify.py --app-id 5507595 --key $k --installation 176108462 --cleanup

# the ruleset as stored, the pull request merge state, and the no-op PUT
python tools\gh_ruleset_state.py --app-id 5507595 --key $k --installation 176108462 --ruleset 26099165 --pr 5
```

`tools/gh_ruleset_verify.py` mints both tokens itself: the full installation
token for the engine, and a repository-scoped token with the agent grant for the
agent. It prints the HTTP status and body of every call and asserts nothing, so
the output is the evidence.

## State afterwards

The probe branch `ff/ticket/a2-verify` was deleted and probe PR #5 was closed,
leaving only `main`:

```text
['main']
```

PRs #1-#4 were not touched; review 14 says to leave them as the example's
history. `main` is still at `5dd0108fc9e9a3e4af18995fc68eed6f0a9231ac`, the
commit PR #3 merged — nothing in this work changed it.
