# JSON Schema draft: plans and patches

Status: second draft. Date: 2026-10-27. Deliverable 2 of
`docs/briefs/brief-02-workflow-language.md`.

**Review 09 applied.** Two separate documents: a plan schema and a patch schema
(E4). The step kind enum is generated from `docs/06` (A1). Inputs are typed per
step kind (answer 5). `patches` is removed from the plan (E4). `base_revision`
replaces `planVersion` (E3). Terminal directives are distinct from step ids (B2).

Companion to `docs/09-workflow-language.md`, which owns the semantics. This
document owns the machine-checkable shape.

---

## 1. Two documents

| Document | `kind` | When it exists |
|---|---|---|
| Plan | `Plan` | One per plan revision. Immutable once written |
| Plan patch | `PlanPatch` | One per re-plan or steering action |

A patch is never embedded in a plan. The first draft had both a `patches` array
inside the plan and a separate patch form, and its own design note contradicted
the array.

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
    "artifactTypes": {
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
        "scope": { "enum": ["ticket", "epic", "delivery", "any"] },
        "revision": { "type": "integer", "minimum": 1 },
        "schemaVersion": { "const": 1 },
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
      "required": ["id", "kind", "routes"],
      "properties": {
        "id": {
          "type": "string",
          "pattern": "^[a-z][a-z0-9_]*$",
          "not": { "enum": ["$stepKinds"] },
          "description": "snake_case. Must not look like a step kind (review 09 B2)"
        },
        "kind": { "$ref": "#/$defs/StepKind" },
        "scope": { "enum": ["ticket", "epic", "delivery", "any"] },
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
        "routes": { "$ref": "#/$defs/Routes" },
        "gate": { "$ref": "#/$defs/Gate" },
        "produces": { "type": "array", "items": { "$ref": "#/$defs/ArtifactDecl" } },
        "consumes": { "type": "array", "items": { "$ref": "#/$defs/ArtifactDecl" } }
      },
      "additionalProperties": false
    },
    "StepKind": {
      "enum": [
        "agent.run",
        "human.gate", "human.session", "policy.auto_approve",
        "run.child", "run.wait", "parallel",
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
                  { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" }
                ]
              }
            },
            "additionalProperties": false
          }
        }
      ]
    },
    "Directive": {
      "type": "string",
      "pattern": "^end: (completed|failed|partial|rejected)$"
    },
    "Expression": {
      "type": "string",
      "maxLength": 1000,
      "description": "CEL. Namespaces: params, steps, run, findings"
    },
    "Gate": {
      "type": "object",
      "required": ["decision_type"],
      "properties": {
        "decision_type": {
          "type": "string",
          "pattern": "^(ticket|epic|delivery)\\.[a-z_]+$"
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

- **`Step.id` must not equal a step kind.** The `not: { enum: [...] }` clause is
  written as `$stepKinds` above; in the real schema it is the same list as
  `StepKind`. This is what makes `end: completed` unambiguous (B2).
- **`routes` is required on every step.** An unrouted outcome is a blocking error,
  and the schema cannot express "every outcome this kind can return" on its own,
  so that check is in the validator, not the schema (`docs/12` section 2).
- **`inputs` is `type: object` with no per-kind typing in this draft.** Review 09
  answer 5 asks for typed inputs per kind. Section 4 gives the mechanism; the
  full per-kind input schemas are generated from `docs/06` and are not written out
  here, because they would duplicate that document and then drift from it.
- **`LibraryRef` is one format** (D8). The first draft had three.

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
            "type": { "enum": ["agent", "human", "child_run"] },
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
            "add_branch", "remove_branch"
          ]
        },
        "target": { "type": "string" },
        "step": { "$ref": "plan/v1#/$defs/Step" },
        "inputs": { "type": "object" },
        "limits": { "$ref": "plan/v1#/$defs/Limits" },
        "role": { "type": "string" },
        "branch": { "type": "string" },
        "outcome": { "type": "string" },
        "to": {
          "oneOf": [
            { "type": "string", "pattern": "^end: (completed|failed|partial|rejected)$" },
            { "type": "string", "pattern": "^[a-z][a-z0-9_]*$" }
          ]
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

---

## 4. Typed inputs per step kind

Review 09 answer 5: inputs are typed per step kind, from the catalogue.

The mechanism is a schema per kind, selected by `kind`:

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
            "required": ["role", "task_ref", "write_access"],
            "properties": {
              "role": { "type": "string" },
              "task_ref": { "$ref": "#/$defs/LibraryRef" },
              "write_access": { "enum": ["none", "branch", "repository"] },
              "tools": { "type": "array", "items": { "type": "string" } },
              "bindings": { "type": "object" },
              "resume_strategy": { "type": "string" },
              "capabilities": { "type": "array", "items": { "type": "string" } }
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
written out in this document for the reason given in section 2.1: it would
duplicate `docs/06` and drift from it. **The generator, not the document, is the
deliverable.**

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
    "schemaVersion": 1,
    "source": "feature-flow-default:templates/ticket-delivery"
  },
  "parameters": {
    "ticket_id": { "type": "string", "required": true }
  },
  "artifactTypes": [
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
        "role": "roles/planner",
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
        "role": "roles/designer",
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
      "routes": { "approved": "design", "rejected": "end: rejected" }
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
          "repositories": { "expr": "params.second_repository" }
        },
        "routes": {
          "completed": "end: completed",
          "failed": "escalate",
          "replan_parent": "apply_child_patch",
          "attempts_exhausted": "escalate"
        }
      }
    }
  ]
}
```

---

## 7. Open questions

- **Per-kind input schemas: generated or hand-written?** Section 4 argues for a
  generator over `docs/06`. That needs `docs/06` to be machine-readable, which it
  is not today. Is making it so in scope for this brief, or does the first
  implementation hand-write the forty-one schemas?
- **`$ref` across documents.** The patch schema references `plan/v1#/$defs/Step`.
  Is a bundled single-file schema preferred for distribution, or is cross-document
  `$ref` acceptable?
- **`schemaVersion` versus `apiVersion`.** Both appear in `PlanMetadata`. Is one
  redundant?
