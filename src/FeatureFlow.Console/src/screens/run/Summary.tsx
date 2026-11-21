import type { Schema } from "../../api/client";
import * as links from "../../app/links";
import { Age } from "../../ui/Age";

/** Who started the run, where it works, its parent and when it last changed. */
export function Summary({ run }: { run: Schema<"RunView"> }) {
  return (
    <dl class="ff-run-summary">
      <dt>Started by</dt>
      <dd>{run.requester}</dd>
      <dt>Repositories</dt>
      <dd>{run.repositories.length === 0 ? "none" : run.repositories.join(", ")}</dd>
      {run.parent_run_id !== null && (
        <>
          <dt>Parent run</dt>
          <dd>
            <a href={links.run(run.parent_run_id)}>{run.parent_run_id}</a>
          </dd>
        </>
      )}
      {run.dry_run && (
        <>
          <dt>Dry run</dt>
          <dd>yes</dd>
        </>
      )}
      <dt>Updated</dt>
      <dd>
        <Age at={run.updated_at} />
      </dd>
      {run.detail !== null && (
        <>
          <dt>Detail</dt>
          <dd class="ff-run-text">{run.detail}</dd>
        </>
      )}
    </dl>
  );
}
