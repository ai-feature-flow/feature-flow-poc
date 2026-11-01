# Write the requirements and architecture for {{ params.project_name }}

This session prepares a workspace for you to work in your own agent tool or
editor. Write two files and submit them:

| File | Artifact | Type |
|---|---|---|
| `requirements.yaml` | `requirements` | `std:artifact_types/requirements`, scope `project`, id `{{ params.project_name }}` |
| `architecture.md` | `contract` | `std:artifact_types/contract` |

The requirements are the project's top-level behaviour: what must be true when
the project is done, stated so that tests can show it. They link to nothing
above. Mark a requirement `end_to_end: true` where you want it proven across the
running repositories.

The architecture names the participating repositories and components, their
responsibilities and contracts, the data and control flow, and the material
decisions. The epic designers work within it.

Before submitting you may run the checks the epic planning step will run, so a
problem shows here rather than after submission.

| Answer | Effect |
|---|---|
| `submitted` | Your files are imported at the submitted commit, validated, and sent for approval |
| `abandoned` | The run ends |
