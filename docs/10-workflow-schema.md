# JSON Schema draft: plans and patches

Status: third draft. Date: 2026-10-31. Deliverable 2 of
`docs/briefs/brief-02-workflow-language.md` (the workflow language brief).

The machine-checkable shape of a plan and a plan patch, as JSON Schema.
`docs/09` (the workflow language) owns what the fields mean; `docs/06` (the step
catalogue) owns the step kinds and their inputs and outcomes.

| Section | Contents |
|---|---|
| 1 | The two document types |
| 2 | Plan schema |
| 3 | Patch schema |
| 4 | Typed inputs per step kind |
| 5, 6 | Example plan and patch |
| 7 | Open questions |

---

## 1. Two documents

| Document | `kind` | When it exists |
|---|---|---|
| Plan | `Plan` | One per plan revision. Immutable once written |
| Plan patch | `PlanPatch` | One per re-plan or steering action. Applied to a plan to produce the next revision |

A patch is never embedded in a plan (review 09 E4): a plan has no `patches`
array.

---

## 2. Plan schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://feature-flow.dev/schemas/plan/v1",
  "title": "Feature Flow Plan",
  "type": "object",
  "required": ["apiVersion", "kind", "metadata", "steps"],
  "properties": {
    "apiVersion": { "const": "feature-flow/v1" },
    "kind": { "const": "Plan" },
    "metadata": { "$ref": "#/$defs/PlanMetadata" },
    "parameters": {
      "type": "object",
      "additionalProperties": { "$ref": "#/$defs/ParameterDef" }
    },
    "artifact_types": {
      "type": "array",
      "items": { "$ref": "#/$defs/ArtifactType" }
    },
    "steps": {
      "type": "array",
      "minItems": 1,
      "items": { "$ref": "#/$defs/Step" }
    }
  },
  "$defs": {
    "PlanMetadata": {
      "type": "object",
      "required": ["name", "scope", "revision"],
      "properties": {
        "name": { "type": "string" },
        "scope": { "enum": ["ticket", "epic", "project", "delivery", "any"] },
        "revision": { "type": "integer", "minimum": 1 },
        "description": { "type": "string" },
        "source": { "$ref": "#/$defs/LibraryRef" }
      },
      "additionalProperties": false
    },
    "ParameterDef": {
      "type": "object",
      "required": ["type"],
      "properties": {
        "type": { "enum": ["string", "number", "boolean", "object", "array"] },
        "required": { "type": "boolean" },
        "default": {},
        "description": { "type": "string" }
      },
      "additionalProperties": false
    },
    "ArtifactType": {
      "type": "object",
      "required": ["name", "version", "schema_ref"],
      "properties": {
        "name": { "type": "string" },
        "version": { "type": "integer", "minimum": 1 },
        "schema_ref": { "$ref": "#/$defs/LibraryRef" },
        "compatible_with": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Artifact type names this type may be consumed as"
        },
        "description": { "type": "string" }
      },
      "additionalProperties": false
    },
    "Step": {
      "type": "object",
      "required": ["id", "kind"],
      "properties": {
        "id": {
          "type": "string",
          "pattern": "^[a-z][a-z0-9_]*$",
          "not": { "enum": ["$stepKinds"] },
          "description": "snake_case. Must not look like a step kind (review 09 B2)"
        },
        "kind": { "$ref": "#/$defs/StepKind" },
        "scope": { "enum": ["ticket", "epic", "project", "delivery", "any"] },
        "protected": { "type": "boolean" },
        "inputs": { "type": "object" },
        "requires": {
          "type": "object",
          "properties": {
            "capabilities": { "type": "array", "items": { "type": "string" } }
          },
          "additionalProperties": false
        },
        "limits": { "$ref": "#/$defs/Limits" },
        "routes": {
          "$ref": "#/$defs/Routes",
          "description": "Required on every step except a parallel branch and a step added by add_chain. The schema cannot tell those apart, so the validator enforces it (docs/12 check 10)"
        },
        "gate": { "$ref": "#/$defs/Gate" },
        "produces": { "type": "array", "items": { "$ref": "#/$defs/ArtifactDecl" } },
        "consumes": { "type": "array", "items": { "$ref": "#/$defs/ArtifactDecl" } },
        "outputs": { "$ref": "#/$defs/Outputs" }
      },
      "additionalProperties": false
    },
    "StepKind": {
      "enum": [
        "agent.run",
        "human.gate", "human.session", "policy.auto_approve",
        "run.child", "parallel",
        "plan.replan", "plan.validate",
        "scm.create_branch", "scm.merge_base_forward", "scm.open_pr",
        "scm.update_pr", "scm.merge", "scm.revert",
        "ci.trigger", "ci.wait_checks", "ci.read_results",
        "test.run_tiers", "test.run_composition",
        "analysis.run",
        "review.run",
        "findings.triage", "findings.post_declines", "findings.evaluate_readiness",
        "fix.verify",
        "wait.for_state",
        "artifact.bundle", "artifact.validate", "artifact.import", "artifact.publish",
        "delivery.deploy_order", "delivery.deploy", "delivery.gate", "delivery.report",
        "tracker.create", "tracker.update", "tracker.transition",
        "tracker.link", "tracker.comment",
        "notify.send"
      ]
    },
    "Limits": {
      "type": "object",
      "properties": {
        "attempts": { "type": "integer", "minimum": 1 },
        "correction_turns": {
          "type": "integer",
          "minimum": 0,
          "description": "Chances an agent gets to fix an artifact that failed its schema, within one attempt"
        },
        "start_to_close": { "type": "string", "pattern": "^[0-9]+(s|m|h)$" },
        "heartbeat": { "type": "string", "pattern": "^[0-9]+(s|m|h)$" }
      },
      "additionalProperties": false
    },
    "Routes": {
      "type": "object",
      "additionalProperties": { "$ref": "#/$defs/RouteTarget" },
      "description": "Keyed by outcome. Every outcome the kind can return must be present"
    },
    "RouteTarget": {
      "oneOf": [
        { "$ref": "#/$defs/Directive" },
        { "$ref": "#/$defs/Resume" },
        { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" },
        {
          "type": "array",
          "minItems": 1,
          "items": {
            "type": "object",
            "required": ["to"],
            "properties": {
              "when": { "$ref": "#/$defs/Expression" },
              "to": {
                "oneOf": [
                  { "$ref": "#/$defs/Directive" },
                  { "$ref": "#/$defs/Resume" },
                  { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" }
                ]
              }
            },
            "additionalProperties": false
          }
        }
      ]
    },
    "Outputs": {
      "type": "object",
      "maxProperties": 16,
      "additionalProperties": {
        "type": "object",
        "required": ["type"],
        "properties": {
          "type": { "enum": ["string", "number", "boolean", "array"] },
          "max_size": { "type": "integer", "minimum": 1, "maximum": 256 }
        },
        "additionalProperties": false
      },
      "description": "Small typed values published into workflow state. Never artifact content (review 10 A3). The cap is a starting value; Spike 2 measured at most 5 entries and 71 bytes per value (docs/16 section 2)"
    },
    "Directive": {
      "type": "object",
      "required": ["end"],
      "properties": {
        "end": { "enum": ["completed", "failed", "partial", "rejected"] }
      },
      "additionalProperties": false,
      "description": "A mapping, not a string: typed in the schema and needs no YAML quoting (review 10 B9)"
    },
    "Resume": {
      "type": "object",
      "required": ["resume"],
      "properties": {
        "resume": { "const": true }
      },
      "additionalProperties": false,
      "description": "Continue at the patch's resume_at. Not an end value: the run has not ended (review 11 A3). Valid only on plan.replan"
    },
    "Expression": {
      "type": "string",
      "maxLength": 1000,
      "description": "CEL. Namespaces: params, steps, run, findings. steps.<id>.previous holds a re-run step's previous results; steps.<id>.executions its execution count"
    },
    "StringOrExpr": {
      "oneOf": [
        { "type": "string" },
        {
          "type": "object",
          "required": ["expr"],
          "properties": { "expr": { "$ref": "#/$defs/Expression" } },
          "additionalProperties": false
        }
      ],
      "description": "An input value given literally or as a CEL expression"
    },
    "Gate": {
      "type": "object",
      "required": ["decision_type"],
      "properties": {
        "decision_type": {
          "type": "string",
          "pattern": "^(ticket|epic|project|delivery)\\.[a-z_]+$"
        }
      },
      "additionalProperties": false
    },
    "ArtifactDecl": {
      "type": "object",
      "required": ["name", "type"],
      "properties": {
        "name": { "type": "string" },
        "type": { "type": "string" },
        "from": { "type": "string", "description": "Producer step id, for consumes" }
      },
      "additionalProperties": false
    },
    "LibraryRef": {
      "type": "string",
      "pattern": "^[a-z0-9-]+(@[0-9]+\\.[0-9]+\\.[0-9]+)?:(templates|tasks|skills|checks|roles|schemas|prompts)/[a-z0-9-]+$",
      "description": "One format: package[@version]:type/name (docs/11 section 3)"
    }
  }
}
```

### 2.1 Notes on the plan schema

- **A step id must not equal a step kind** (review 09 B2). The
  `not: { enum: [...] }` clause is written as `$stepKinds` above; in the real
  schema it is the same list as `StepKind`. This is what keeps a terminal
  directive such as `{ end: completed }` distinct from a step id.
- **`routes` is optional in the schema, required by the validator.** Every step
  must route every outcome its kind can return (`docs/12` section 2, the
  validation table, check 10), except a parallel branch, which reports to its
  join, and a step added by `add_chain`, whose routes the chain supplies. JSON
  Schema can tell neither which outcomes a kind returns nor which steps are
  branches, so the validator enforces both.
- **`inputs` is an untyped object here.** Inputs are typed per step kind
  (review 09, answer 5). Section 4 gives the mechanism. The per-kind input
  schemas are generated from `docs/06` and are not written out here, so they
  cannot drift from it.
- **`LibraryRef` has one format**, `package[@version]:type/name` (review 09 D8;
  `docs/11` section 3).
- **Scopes.** `ticket`, `epic`, `project`, `delivery` and `any`. A project run
  starts epic runs with `run.child`, the way an epic run starts ticket runs.
- **Run statuses are not all directives.** `end` takes `completed`, `failed`,
  `partial` or `rejected`. A run that stops without reaching an `end:` route has
  the status `incomplete` (DR6), which no route can choose; a parent's
  `run.child` sees such a child as `failed`.
- **Loops re-run steps** (DR12). Re-running a step clears its outcome, outputs
  and artifacts. Expressions can read the previous execution's results as
  `steps.<id>.previous` and the count as `steps.<id>.executions`.
- **No merge-lock fields.** The schema never had any, and the merge design
  (`docs/20`: no lock; catch up, re-test if changed, merge) needs none. The
  catch-up step's `branch_changed` result is an ordinary entry in `outputs`.

---

## 3. Patch schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://feature-flow.dev/schemas/plan-patch/v1",
  "title": "Feature Flow Plan Patch",
  "type": "object",
  "required": ["apiVersion", "kind", "metadata", "base_revision", "changes"],
  "properties": {
    "apiVersion": { "const": "feature-flow/v1" },
    "kind": { "const": "PlanPatch" },
    "metadata": { "$ref": "#/$defs/PatchMetadata" },
    "base_revision": {
      "type": "integer",
      "minimum": 1,
      "description": "Optimistic concurrency. A stale revision is rejected and re-proposed"
    },
    "resume_at": {
      "type": "string",
      "pattern": "^[a-z][a-z0-9_]*$",
      "description": "Where execution continues after the patch. Validated like a route target: must exist after the patch is applied. Defaults to the step that routed to plan.replan (review 10 A6)"
    },
    "changes": {
      "type": "array",
      "minItems": 1,
      "items": { "$ref": "#/$defs/Change" }
    }
  },
  "$defs": {
    "PatchMetadata": {
      "type": "object",
      "required": ["run_id", "proposed_by", "reason"],
      "properties": {
        "run_id": { "type": "string" },
        "proposed_by": {
          "type": "object",
          "required": ["type"],
          "properties": {
            "type": { "enum": ["agent", "human", "child_run", "engine"] },
            "step_id": { "type": "string" },
            "actor": { "type": "string" },
            "child_run_id": { "type": "string" }
          },
          "additionalProperties": false
        },
        "reason": { "type": "string", "minLength": 1 }
      },
      "additionalProperties": false
    },
    "Change": {
      "type": "object",
      "required": ["op", "target"],
      "properties": {
        "op": {
          "enum": [
            "insert_before", "insert_after", "replace_step", "remove_step",
            "replace_inputs", "replace_limits", "replace_role", "change_route",
            "add_branch", "remove_branch", "add_chain"
          ]
        },
        "target": { "type": "string" },
        "step": { "$ref": "https://feature-flow.dev/schemas/plan/v1#/$defs/Step" },
        "inputs": { "type": "object" },
        "limits": { "$ref": "https://feature-flow.dev/schemas/plan/v1#/$defs/Limits" },
        "role": { "type": "string" },
        "branch": { "type": "string" },
        "on_other": { "type": "string" },
        "outcome": { "type": "string" },
        "to": {
          "oneOf": [
            { "$ref": "https://feature-flow.dev/schemas/plan/v1#/$defs/Directive" },
            { "$ref": "https://feature-flow.dev/schemas/plan/v1#/$defs/Resume" },
            { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" }
          ],
          "description": "The same forms as a plan route: a step id, { end: ... } or { resume: true }"
        }
      },
      "additionalProperties": false
    }
  }
}
```

### 3.1 Which fields each operation uses

| Op | `target` | Required | Optional |
|---|---|---|---|
| `insert_before` | Step id | `step` | — |
| `insert_after` | Step id | `step` | — |
| `replace_step` | Step id | `step` | — |
| `remove_step` | Step id | — | — |
| `replace_inputs` | Step id | `inputs` | — |
| `replace_limits` | Step id | `limits` | — |
| `replace_role` | Step id | `role` | — |
| `change_route` | Step id | `outcome`, `to` | — |
| `add_branch` | `parallel` step id | `step` | — |
| `remove_branch` | `parallel` step id | `branch` | — |
| `add_chain` | Step id to chain after | `step`, `on_other` | — |

---

## 4. Typed inputs per step kind

Inputs are typed per step kind, from the step catalogue (review 09, answer 5).
The mechanism is one schema per kind, selected by `kind`. Three kinds are shown:
`agent.run`, `review.run` and `scm.merge`.

```json
{
  "allOf": [
    { "$ref": "#/$defs/Step" },
    {
      "if": { "properties": { "kind": { "const": "agent.run" } }, "required": ["kind"] },
      "then": {
        "properties": {
          "inputs": {
            "type": "object",
            "required": ["role", "task_ref"],
            "properties": {
              "role": { "$ref": "#/$defs/LibraryRef" },
              "task_ref": { "$ref": "#/$defs/LibraryRef" },
              "write_access": {
                "enum": ["none", "branch", "repository"],
                "description": "Optional TIGHTENING only. Defaults to the role's value. A step may never exceed its role (review 10 A1, review 11 A4). Governs what the agent may change in its sandbox; the agent never holds a credential that can push, and the engine pushes for it (DR13, docs/20)"
              },
              "push": {
                "type": "boolean",
                "default": true,
                "description": "Whether the engine pushes the agent's commits to the branch at the end of the step and at work-in-progress points. False for scratch steps. Pushing follows write_access: with none, nothing is pushed"
              },
              "bindings": { "type": "object" },
              "resume_strategy": { "type": "string" }
            },
            "additionalProperties": false
          }
        }
      }
    },
    {
      "if": { "properties": { "kind": { "const": "review.run" } }, "required": ["kind"] },
      "then": {
        "properties": {
          "inputs": {
            "type": "object",
            "required": ["scope", "prior_findings"],
            "properties": {
              "scope": { "enum": ["ticket", "epic", "project", "delivery"] },
              "branch": { "$ref": "#/$defs/StringOrExpr", "description": "The branch to review, on a read-only checkout" },
              "base": { "$ref": "#/$defs/StringOrExpr", "description": "The branch it is compared with" },
              "diff_ref": { "$ref": "#/$defs/StringOrExpr", "description": "Only for a review of a specification document rather than code: the document to review" },
              "spec_refs": { "$ref": "#/$defs/StringOrExpr", "description": "The specification documents to review against: acceptance criteria, the epic contract" },
              "machine_results": { "$ref": "#/$defs/StringOrExpr", "description": "Results of the machine checks that ran first: build, tests, linters, analysers" },
              "checks": { "type": "array", "items": { "$ref": "#/$defs/LibraryRef" } },
              "prior_findings": { "$ref": "#/$defs/StringOrExpr" },
              "require_coverage": {
                "oneOf": [
                  { "enum": ["none", "all"] },
                  { "type": "array", "minItems": 1, "items": { "type": "string" } }
                ],
                "default": "none",
                "description": "Which changed files the reviewer must have read: none, all, or a list of paths. The engine publishes coverage as an output either way and never fails a step for coverage on its own"
              }
            },
            "additionalProperties": false
          }
        }
      }
    },
    {
      "if": { "properties": { "kind": { "const": "scm.merge" } }, "required": ["kind"] },
      "then": {
        "properties": {
          "inputs": {
            "type": "object",
            "required": ["pr_ref", "engine_can_merge", "squash"],
            "properties": {
              "pr_ref": { "$ref": "#/$defs/StringOrExpr" },
              "engine_can_merge": {
                "type": "boolean",
                "description": "True: the engine builds the merge commit from what was tested and pushes it. False: the engine raises a human decision request, a human merges on the code host, and the engine detects the merge. Checked against the default-branch merge setting (floor F1) at validation and at merge time"
              },
              "squash": { "type": "boolean", "description": "True for a squash merge, false for a plain merge" }
            },
            "additionalProperties": false
          }
        }
      }
    }
  ]
}
```

The full set is one `if`/`then` per kind, generated from `docs/06`. It is not
written out here, for the reason in section 2.1. **The generator, not this
document, is the deliverable.**

---

## 5. Example: a plan

```json
{
  "apiVersion": "feature-flow/v1",
  "kind": "Plan",
  "metadata": {
    "name": "ticket-delivery",
    "scope": "ticket",
    "revision": 1,
    "source": "feature-flow-default:templates/ticket-delivery"
  },
  "parameters": {
    "ticket_id": { "type": "string", "required": true }
  },
  "artifact_types": [
    {
      "name": "ticket-plan",
      "version": 1,
      "schema_ref": "feature-flow-default:schemas/ticket-plan"
    }
  ],
  "steps": [
    {
      "id": "plan_spec",
      "kind": "agent.run",
      "scope": "ticket",
      "inputs": {
        "role": "feature-flow-default:roles/planner",
        "task_ref": "feature-flow-default:tasks/plan-ticket",
        "write_access": "none",
        "bindings": { "ticket_id": { "expr": "params.ticket_id" } }
      },
      "requires": { "capabilities": ["sandbox", "agent_runtime"] },
      "limits": { "attempts": 3, "start_to_close": "4h", "heartbeat": "30s" },
      "produces": [{ "name": "plan", "type": "ticket-plan" }],
      "routes": {
        "completed": "design",
        "blocked": "gate_spec",
        "failed": "gate_spec",
        "attempts_exhausted": "gate_spec",
        "replan_requested": "replan"
      }
    },
    {
      "id": "design",
      "kind": "agent.run",
      "protected": true,
      "inputs": {
        "role": "feature-flow-default:roles/designer",
        "task_ref": "feature-flow-default:tasks/design",
        "write_access": "none"
      },
      "consumes": [{ "name": "plan", "type": "ticket-plan", "from": "plan_spec" }],
      "routes": {
        "completed": "implement",
        "blocked": "gate_design",
        "failed": "gate_design",
        "attempts_exhausted": "gate_design",
        "replan_requested": "replan"
      }
    },
    {
      "id": "gate_spec",
      "kind": "human.gate",
      "gate": { "decision_type": "ticket.spec_approval" },
      "inputs": {
        "prompt_ref": "feature-flow-default:prompts/spec-approval",
        "context_refs": { "expr": "[steps.plan_spec.artifacts.plan]" }
      },
      "routes": { "approved": "design", "rejected": { "end": "rejected" } }
    }
  ]
}
```

---

## 6. Example: a patch

```json
{
  "apiVersion": "feature-flow/v1",
  "kind": "PlanPatch",
  "metadata": {
    "run_id": "run-1234",
    "proposed_by": { "type": "agent", "step_id": "replan" },
    "reason": "implementation discovered a change needed in a second repository"
  },
  "base_revision": 7,
  "resume_at": "tickets",
  "changes": [
    {
      "op": "add_branch",
      "target": "tickets",
      "step": {
        "id": "ticket_d",
        "kind": "run.child",
        "inputs": {
          "child_scope": "ticket",
          "plan_ref": "feature-flow-default:templates/ticket-delivery",
          "parameters": {
            "ticket_id": "EPIC-104",
            "repositories": { "expr": "params.repositories" }
          }
        }
      }
    }
  ]
}
```

The added branch has no `routes`: a parallel branch reports its terminal outcome
to the join, and a child's request to re-plan its parent (`replan_parent`) is
handled by the interpreter (review 11 A1).

---

## 7. Open questions

### Answered

| Question | Answer |
|---|---|
| Per-kind input schemas: generated or hand-written | **Generated.** In implementation the catalogue becomes one machine-readable YAML file that `docs/06` and the schemas are both generated from. An implementation task |
| `$ref` across documents | Cross-document `$ref` in source; publish a bundled single file |
| `schemaVersion` versus `apiVersion` | `apiVersion: feature-flow/v1` **is** the plan schema version. `schemaVersion` is dropped, and a package's `requires.plan_schema_version` is expressed as an `apiVersion` |
| Richer input types | Inputs are typed per step kind, from the catalogue (section 4) |
| Reference format | One format, `package[@constraint]:type/name` (`docs/11` section 3) |
| The `outputs` cap (16 entries, 256 bytes per value) | Kept as a starting value. Spike 2 measured at most 5 entries and 71 bytes (`docs/16` section 2) |

### 7.1 Answered in round 2

Found while checking the schema against the worked examples in `docs/13`, and
decided by the product owner on 2026-10-31.

| # | Question | Answer, applied in this document |
|---|---|---|
| 1 | `Step` required `routes`, but parallel branches and `add_chain` steps carry none | `routes` is optional in the schema; the validator requires it on every other step (section 2.1) |
| 2 | `Limits` had no `correction_turns` | Added |
| 3 | A patch's `change_route` typed directives as strings | `to` uses the plan's forms: a step id, `{ end: ... }` or `{ resume: true }` |
| 4 | `review.run` inputs under the review design | `branch`, `base`, `spec_refs`, `machine_results`; `diff_ref` only for reviewing a document; plus `require_coverage` (section 4) |
