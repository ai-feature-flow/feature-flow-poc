# Review 14 A3, A7, A8: the agent authors the change

Captured 2026-10-29. Repository `ai-feature-flow/feature-flow-example`.
Temporal dev server on port **7433**, queue **`ff-a3-queue`** — both chosen so
this work does not collide with Spike 1 (`spike-1` / 7233) or Spike 2's earlier
run (`spike-2-real` / 7333).

## Summary

| Item | Result |
|---|---|
| **A3** — the agent authors the change | **EVIDENCED** for Copilot CLI. **NOT EVIDENCED** for Claude Code (reason below) |
| **A7** — R4 with a real reviewer model | **EVIDENCED, and the verdicts do not agree.** The reviewer model is unstable |
| **A8** — `insert_after` with a planner model | **MEASURED.** `insert_after` 20% validation failure vs two-op 25% |

The decisive A3 evidence is below: a file in the fixture repository whose
content the model wrote, with the diff and the commit.

---

## A3 — the agent authors the change

### What changed in the code

`StepWork` is **removed from the agent path**. `RealActivities.RunAgentAsync`
no longer calls it. The step's work is now whatever the agent committed:

- `AgentRunActivity` captures the branch head **before** the agent runs
  (`headBefore`), then reads it again afterwards and compares. A step whose
  agent made no commit is reported as `failed` with that reason, rather than
  silently adopting whatever HEAD happens to be.
- The engine **pushes** the agent's commit, using the credential already minted
  for that step. The prompt tells the agent not to push, because the push
  credential is the engine's to hold.
- `StepWork.ChangeFor` now returns `null` for every step id, so canned content
  cannot come back silently. `StepWork` remains only for genuinely
  non-agent (orchestration) steps.
- `Inputs.WorkIsScripted` is now `false`.

### Per-runtime: which model ran, and whether structured tool use worked

| Runtime instance | Model | Structured tool use through the router | Result |
|---|---|---|---|
| `copilot-cli` | `z-ai/glm-5.3-flash` | **Yes** — `finish_reason: tool_calls`, 1 call | **EVIDENCED** |
| `claude-code` | `z-ai/glm-5.3-flash` | **No** — rejected before any call | **NOT EVIDENCED** |

**Copilot CLI.** Verified directly against the router, and then through the
real CLI in the fixture checkout:

```text
finish_reason: tool_calls
tool_calls:    1
  name=write_file args={"content": "hello from the agent\n", "path": "notes/hello.txt"}
```

```text
copilot --prompt "Create a file at notes/probe.txt containing exactly the text: hello from copilot" \
  --model z-ai/glm-5.3-flash --allow-all-tools --output-format json
exit=0
notes/probe.txt -> "hello from copilot"
```

**Claude Code — NOT EVIDENCED, and why.** Claude Code validates model names
against Anthropic's own list and speaks the Anthropic Messages API, not the
OpenAI-compatible shape this router serves. It rejects the model before doing
any work:

```text
[claude-code:unrecognized_model] {"model":"z-ai/glm-5.3-flash","query_source":"sdk"}
"result":"There's an issue with the selected model (z-ai/glm-5.3-flash).
 It may not exist or you may not have access to it."
"terminal_reason":"api_error","api_error_status":404
```

The product owner decided on 2026-10-29 to report Claude Code as not evidenced
and proceed with Copilot CLI only, rather than build a proxy to work around it.
Accordingly `organisation.yaml`'s `roleMapping` now maps **every** role to
`copilot-cli`. The `claude-code` instance is still registered and still probed;
it is simply not mapped to a role, because a runtime that cannot reach the model
cannot evidence anything.

### The Ollama finding: small local models fail at structured tool use

Review 14 says this is still useful, so it is recorded here. Each model was
asked the same request with a `write_file` tool declared:

| Model | `finish_reason` | `tool_calls` | How it failed |
|---|---|---|---|
| `qwen2.5-coder:7b` | `stop` | 0 | Emitted the tool call as **prose JSON in `content`** |
| `qwen2.5-coder:32b` | `stop` | 0 | Same — prose JSON in `content` |
| `qwen2.5-coder:3b` | `stop` | 0 | Same, wrapped in a ```json fence |
| `llama3.1:8b` | `tool_calls` | 1 | **Worked** — structured call emitted |
| `llama3:latest` | — | — | HTTP 400: `does not support tools` |

So the earlier report's claim that "Ollama returns a tool call as prose text"
was **too broad**. It is true of the Qwen coder models at 3B/7B/32B, but
`llama3.1:8b` emits a correct structured `tool_calls` block. The accurate
statement is: **structured tool use through Ollama is model-dependent, and the
Qwen coder models tried here do not do it.**

### The decisive evidence: a file whose content the model wrote

The ticket's `implement` step asked for `WidgetStore.IsEmpty()`. The model wrote
it. Commit `2c04e04`:

```text
commit 2c04e0447acaae8df39ae2a8f329206bb9c23d98
Author: sparkz-nz <stuart.parkinson.nz@gmail.com>
Date:   Thu Oct 29 20:40:52 2026 +1300

    TICKET-SPIKE2-1: implement

 src/FeatureFlow.Example.Api/WidgetStore.cs | 9 +++++++++
 1 file changed, 9 insertions(+)
```

```diff
@@ -44,4 +44,13 @@ public sealed class WidgetStore
             return _widgets.Count;
         }
     }
+
+    /// <summary>Returns true when the store holds no widgets.</summary>
+    public bool IsEmpty()
+    {
+        lock (_gate)
+        {
+            return _widgets.Count == 0;
+        }
+    }
 }
```

The `fix_findings` step then authored the regression test, commit `1b248db`:

```diff
+    [Fact]
+    public void IsEmpty_is_true_for_a_new_store()
+    {
+        var store = new WidgetStore();
+
+        Assert.True(store.IsEmpty());
+
+        store.Add("alpha");
+
+        Assert.False(store.IsEmpty());
+    }
```

Neither string appears anywhere in the spike source. `StepWork.ChangeFor`
returns `null` for every step id, so there is no code path that could have
supplied them. The content came from the model through the router.

---

## A7 — R4 with a real reviewer model

### What changed

`review.run` no longer derives its verdict from the diff hash. It calls a real
reviewer model through the router, passing the diff text and the prior
findings, including which findings were declined. `Findings.Record` now
persists the verdict's findings into the store, which it previously did not.

### The verdicts, and whether prior findings stabilised them

Four passes on the same branch, each a real model call
(`model=z-ai/glm-5.3-flash` on every pass):

| pass | outcome | findings | prior findings passed |
|---|---|---|---|
| 1 | `findings_open` | 1 | 0 |
| 2 | `findings_open` | 1 | 0 |
| 3 | `findings_open` | 1 | 1 |
| 4 | `findings_open` | 2 | 1 |

**The verdicts do not agree, and passing prior findings did not stabilise
them.** Two distinct instabilities, both real:

1. **A fixed finding is re-raised.** `f-review-1` (`plan-implementation-mismatch`)
   is recorded `fixed=true` after the fixer's commit, yet the next review pass
   raises it again. The model is told the finding exists and is fixed; it
   re-raises it anyway.
2. **A new finding appears on unchanged input.** Pass 4 raises
   `f-review-2` (`missing-eof-newline`) — a finding about a missing trailing
   newline — which passes 1-3 did not raise, on a diff that had not changed in
   the relevant respect.

This is the finding R4 exists to detect, and it is reported rather than
reconciled. The previous R4 passed because the verdict was a pure function of
the diff hash; with a model in the loop, the verdict is not stable across
passes on unchanged input.

The control still holds: declining a finding changes the verdict, and a
declined finding is not re-raised. That is enforced by the engine (K4), not
left to the model.

---

## A8 — the `insert_after` measurement with a planner model

### What changed

The patches are no longer generated in code by cycling targets. A planner model
reads the **readable form** (`docs/12` section 4, review 09 D7) and authors
each patch itself, choosing its own target. New code: `ReadableForm.cs` (the
renderer) and `PlannerModel.cs` (the planner).

The readable form the planner actually read:

```text
Plan ticket-delivery rev 1
────────────────────────────────────────────────────────────────────────────────
 #  id                 kind             role         access   status     routes
 1  plan_spec          agent.run        planner      none     pending    attempts_exhausted->gate_spec blocked->gate_spec completed->design failed->gate_spec replan_requested->replan
 2  design             agent.run        designer     none     pending    attempts_exhausted->gate_design blocked->gate_design completed->implement failed->gate_design replan_requested->replan
                                                                         [protected]
 3  implement          agent.run        implementer  branch   pending    attempts_exhausted->gate_escalate blocked->replan completed->run_tests failed->replan replan_requested->replan
 4  run_tests          test.run_tiers   -            branch   pending    attempts_exhausted->gate_escalate completed->review failed->triage_findings
 5  review             review.run       -            branch   pending    attempts_exhausted->gate_escalate completed->evaluate_readiness failed->gate_escalate findings_open->triage_findings
 6  triage_findings    findings.triage  -            branch   pending    attempts_exhausted->gate_escalate blocked->gate_escalate completed->fix_findings
 7  fix_findings       agent.run        fixer        branch   pending    attempts_exhausted->gate_escalate blocked->gate_escalate completed->verify_fix
 8  verify_fix         fix.verify       -            branch   pending    attempts_exhausted->gate_escalate completed->run_tests failed->triage_findings
 9  evaluate_readiness findings.evaluate_readiness -            branch   pending    findings_open->triage_findings ready->open_pr
 10 open_pr            scm.open_pr      -            branch   pending    attempts_exhausted->gate_escalate completed->wait_checks failed->gate_escalate
 11 wait_checks        ci.wait_checks   -            branch   pending    attempts_exhausted->gate_escalate blocked->gate_escalate completed->gate_merge failed->triage_findings
 12 gate_merge         human.gate       -            branch   pending    approved->merge_pr rejected->end:rejected
 13 merge_pr           scm.merge        -            branch   pending    attempts_exhausted->gate_escalate blocked->gate_escalate completed->end:completed rejected->gate_merge
 14 gate_spec          human.gate       -            branch   pending    approved->design rejected->end:rejected
 15 gate_design        human.gate       -            branch   pending    approved->implement rejected->end:rejected
 16 gate_escalate      human.gate       -            branch   pending    approved->replan rejected->end:failed
 17 replan             plan.replan      -            branch   pending    attempts_exhausted->gate_escalate failed->gate_escalate replan_requested->(unresolved)
Protected: design
```

### The measurement

20 trials per form, planner-authored, against the real plan with `design`
protected. Planner failures (unparseable, unknown target, duplicate id) are
counted separately from validation failures (applyPatch refused or held).

| form | authored | planner failures | validation failures | applied |
|---|---|---|---|---|
| `insert_after` | 20/20 | 0 (0%) | 4 (20%) | 16 (80%) |
| `add_step` + `change_route` | 20/20 | 0 (0%) | 5 (25%) | 15 (75%) |

Every validation failure had the same cause:

```text
4x protected step cannot be changed (floor F3)     # insert_after
5x protected step cannot be changed (floor F3)     # add_step + change_route
```

### What this says, and what it does not

The planner authored **every** patch successfully — 0% planner failure rate on
both forms. That is the number review 11 C2 asked for, and it is a real
measurement rather than a floor-F3 artefact.

The remaining 20%/25% is still floor F3, but now for a different and more
informative reason: the planner **chose** `plan_spec` as its target, and
inserting after `plan_spec` makes the protected `design` step unreachable. The
earlier measurement forced that target by cycling; this one shows the planner
picking it on its own. So the finding is not "the two-op form is worse" — it is
that **a planner asked to insert a notification step will sometimes pick a
target whose rewiring strands a protected step**, and the two-op form hits that
one extra time because it states the rewiring explicitly.

The gap between the two forms (20% vs 25%, one trial) is **not** large enough to
conclude that `insert_after` is safer as a mechanism. With 20 trials, one trial
is the smallest difference the measurement can express. Reporting it as a
reliability difference would repeat the mistake A8 is about.

---

## Bugs found and fixed

1. **The "agent made no commit" check could never fire.** `HeadShaAsync()`
   reads HEAD, which always resolves — so comparing a post-run HEAD against
   nothing always looked like success. Fixed by capturing the head *before*
   the agent runs and comparing. This is why `design` and `implement` reported
   the same SHA as `plan_spec` in the first re-run.
2. **The agent's commit was never pushed.** `StepWork` used to push; removing
   it removed the push, so CI could not check out the SHA and
   `actions/checkout` failed with a non-fast-forward rejection. Fixed by
   having the engine push the agent's commit with the step's credential.
3. **`Findings.Record` did not persist findings.** It stored the verdict but
   never wrote its findings into `store.Findings`, so triage saw zero open
   findings, selected nothing, and `fix.verify` failed forever — a loop that
   looks like a planner problem but is a persistence bug.
4. **`verify_fix` could never pass on the real path.** It required
   `Findings.HasRegressionTest`, which only the K4 check ever set via
   `AttachRegressionTest`. Nothing on the real fixer path called it. Fixed by
   also detecting the test in the diff the fixer produced.

## What could not be evidenced, and why

- **Claude Code on A3: not evidenced.** It rejects the router's model with
  `[claude-code:unrecognized_model]` (HTTP 404) because it validates model names
  against Anthropic's own list and speaks the Anthropic Messages API rather than the
  OpenAI-compatible shape. Per the product owner's decision, no proxy was built to
  work around this.
- **A stable R4 verdict: not evidenced.** The reviewer model's verdicts differ
  between passes on unchanged input, as recorded above. This is the finding, not
  a gap in the measurement.
- **K4-decline: not evidenced.** Opening a pull request returned
  `Infrastructure/server_error`, so the decline reason could not be posted on a
  real PR. This is a pre-existing check, unrelated to A3/A7/A8.
