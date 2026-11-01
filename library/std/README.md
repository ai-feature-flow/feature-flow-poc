# std: the standard library

The workflow content that ships with Feature Flow: the delivery process Feature
Factory proved, with the first design's review and merge improvements. It is
policy, not engine: any of it may be replaced (`docs/08` section 6).

Status: written 2026-11-01 against the design, before any engine exists.
Nothing here has been loaded by a validator; a scratch structural check found
no unresolved references, unknown route targets, unrouted outcomes or unbound
task variables. The formats the design left open are LOG 62 to 65, approved 2026-11-01.

| Folder | Holds |
|---|---|
| `templates/` | `ticket_delivery`, `epic_delivery`, `project_delivery` |
| `fragments/` | `baseline`, `plan_and_validate`, `standard_review`, `fix_loop`, `verify_and_review`, `merge_into_parent`, `plan_children`, `trace`, `deliver_in_order` |
| `tasks/` | One per agent step |
| `prompts/` | The reporting preamble every agent step gets; decision and pull request text |
| `skills/` | `verification`, `review`, `solution_design` |
| `roles/` | `planner`, `designer`, `implementer`, `fixer`, `reviewer`, `triager`, `conflict_resolver` |
| `artifact_types/` | Seven document types and six engine-only evidence types |
| `decision_types/` | The gates std uses, and the engine-raised types by pattern |
| `status_map.yaml` | The abstract tracker statuses templates use |

Not yet written: workflow test files (`docs/09` section 4.3), which need the
validator, and tracker or pull request skills, which depend on the toolsets an
organisation configures.
