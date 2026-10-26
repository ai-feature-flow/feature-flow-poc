# JSON Schema draft for plans and patches

Status: draft. This is a working schema for the workflow language design in `docs/09-workflow-language.md`.

## 1. Schema goals

The schema must:

- model one schema for templates and plans
- support versioned plan revisions
- make route logic explicit
- support parallel branch definitions
- support patch operations for re-plan and steering
- keep validation logic machine-checkable

## 2. Plan schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://feature-flow.dev/schemas/plan.schema.json",
  "title": "Feature Flow Plan",
  "type": "object",
  "required": ["apiVersion", "kind", "metadata", "steps"],
  "properties": {
    "apiVersion": {
      "type": "string",
      "const": "feature-flow/v1"
    },
    "kind": {
      "type": "string",
      "const": "Plan"
    },
    "metadata": {
      "$ref": "#/$defs/Metadata"
    },
    "parameters": {
      "type": "object",
      "additionalProperties": {
        "$ref": "#/$defs/ParameterDef"
      }
    },
    "steps": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/Step"
      },
      "minItems": 1
    },
    "routes": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/Route"
      }
    },
    "patches": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/Patch"
      }
    },
    "policy": {
      "$ref": "#/$defs/PolicyRef"
    }
  },
  "$defs": {
    "Metadata": {
      "type": "object",
      "required": ["name", "scope"],
      "properties": {
        "name": { "type": "string" },
        "scope": {
          "type": "string",
          "enum": ["ticket", "epic", "delivery", "any"]
        },
        "version": { "type": "integer", "minimum": 1 },
        "description": { "type": "string" },
        "author": { "type": "string" }
      },
      "additionalProperties": false
    },
    "ParameterDef": {
      "type": "object",
      "properties": {
        "type": {
          "type": "string",
          "enum": ["string", "number", "boolean", "object", "array"]
        },
        "required": { "type": "boolean" },
        "default": {},
        "description": { "type": "string" }
      },
      "required": ["type"],
      "additionalProperties": false
    },
    "Step": {
      "type": "object",
      "required": ["id", "kind"],
      "properties": {
        "id": { "type": "string" },
        "kind": {
          "type": "string",
          "enum": [
            "agent.run",
            "human.gate",
            "human.session",
            "scm.checkout",
            "scm.commit",
            "scm.merge",
            "test.run_tiers",
            "fix.verify",
            "delivery.deploy",
            "notify.send",
            "tracker.comment",
            "parallel",
            "plan.replan"
          ]
        },
        "scope": {
          "type": "string",
          "enum": ["ticket", "epic", "delivery", "any"]
        },
        "role": { "type": "string" },
        "protected": { "type": "boolean" },
        "inputs": { "type": "object", "additionalProperties": true },
        "outcomes": {
          "type": "object",
          "additionalProperties": { "type": "string" }
        },
        "when": { "$ref": "#/$defs/Expression" },
        "attempts": { "type": "integer", "minimum": 1 },
        "timeout": { "type": "string" },
        "requires": {
          "type": "array",
          "items": { "type": "string" }
        }
      },
      "additionalProperties": false
    },
    "Route": {
      "type": "object",
      "required": ["from", "on", "to"],
      "properties": {
        "from": { "type": "string" },
        "on": {
          "type": "string",
          "enum": [
            "completed",
            "failed",
            "blocked",
            "attempts_exhausted",
            "partial",
            "approved",
            "rejected",
            "ready",
            "findings_open",
            "replan_requested",
            "replan_parent"
          ]
        },
        "to": { "type": "string" },
        "when": { "$ref": "#/$defs/Expression" }
      },
      "additionalProperties": false
    },
    "Expression": {
      "type": "string",
      "description": "Deterministic, side-effect-free expression language"
    },
    "PolicyRef": {
      "type": "object",
      "required": ["ref"],
      "properties": {
        "ref": { "type": "string" },
        "version": { "type": "string" }
      },
      "additionalProperties": false
    },
    "Patch": {
      "type": "object",
      "required": ["op"],
      "properties": {
        "op": {
          "type": "string",
          "enum": [
            "insert_before",
            "insert_after",
            "replace_step",
            "remove_step",
            "replace_inputs",
            "add_parallel_branch",
            "remove_parallel_branch",
            "change_route",
            "set_parameter",
            "override_library_reference",
            "insert_policy_guard"
          ]
        },
        "target": { "type": "string" },
        "step": { "$ref": "#/$defs/Step" },
        "values": { "type": "object", "additionalProperties": true },
        "branch": { "type": "object", "additionalProperties": true },
        "from": { "type": "string" },
        "to": { "type": "string" }
      },
      "additionalProperties": false
    }
  }
}
```

## 3. Example plan JSON

```json
{
  "apiVersion": "feature-flow/v1",
  "kind": "Plan",
  "metadata": {
    "name": "ticket-delivery",
    "scope": "ticket",
    "version": 1
  },
  "parameters": {
    "ticketId": { "type": "string", "required": true },
    "repo": { "type": "string", "default": "service" }
  },
  "steps": [
    {
      "id": "plan",
      "kind": "agent.run",
      "role": "planner",
      "inputs": {
        "taskRef": "lib/tasks/plan-ticket",
        "ticketId": "${params.ticketId}"
      },
      "outcomes": {
        "completed": "design",
        "failed": "human.gate"
      }
    },
    {
      "id": "design",
      "kind": "agent.run",
      "role": "designer",
      "protected": true,
      "inputs": {
        "taskRef": "lib/tasks/design"
      },
      "outcomes": {
        "completed": "implement",
        "blocked": "human.gate"
      }
    }
  ],
  "routes": [
    { "from": "plan", "on": "completed", "to": "design" }
  ]
}
```

## 4. Patch schema

```json
{
  "apiVersion": "feature-flow/v1",
  "kind": "Patch",
  "metadata": { "runId": "abc123", "reason": "mid-run replan" },
  "planVersion": 2,
  "changes": [
    {
      "op": "insert_after",
      "target": "implement",
      "step": {
        "id": "review",
        "kind": "agent.run",
        "role": "reviewer",
        "inputs": { "taskRef": "lib/tasks/review" },
        "outcomes": { "completed": "merge" }
      }
    },
    {
      "op": "change_route",
      "target": "test.run_tiers",
      "from": "findings_open",
      "to": "fix.verify"
    }
  ]
}
```

## 5. Design notes

- The schema intentionally does not allow arbitrary untyped JSON blobs in step definitions. Step structure is strict and engine-known.
- `outcomes` are mandatory at the step level to keep routing explicit and machine validated.
- `patch` is a separate schema rather than embedding patch operations inside the plan. This keeps re-plan validation separate from execution.
- The engine may add new step kinds only in a new plan schema version; workflow authors cannot define a new runtime step type ad hoc.

## 6. Open questions

- Should route control also allow `when` guards on a branch or child plan, or only on the route itself?
- Should step inputs allow a richer type system (`oneOf`, `enum`, `pattern`) for stronger validation, or keep them as general JSON?
- Should library references be a strict `pkg:version:path` format or a simpler namespace/path string?
