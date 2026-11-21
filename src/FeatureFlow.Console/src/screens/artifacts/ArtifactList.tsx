import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import * as links from "../../app/links";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import { Table } from "../../ui/Table";

export interface ArtifactListProps {
  runId: string;
}

/** The kinds in the order the list shows them, each with its heading. */
const kinds: readonly [Schema<"ArtifactKind">, string][] = [
  ["artifact", "Artifacts"],
  ["evidence", "Evidence"],
  ["transcript", "Transcripts"],
  ["prompt", "Prompts"],
  ["plan_revision", "Plan revisions"],
];

/** The run's artifacts, evidence, transcripts, prompts and plan revisions (GET /runs/{id}/artifacts), each opening in the viewer. */
export function ArtifactList({ runId }: ArtifactListProps) {
  const list = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/artifacts", { params: { path: { id: runId } } })));
  const artifacts = list.data?.artifacts;

  return (
    <section class="ff-artifacts" aria-label="Artifacts">
      <h2>Artifacts</h2>
      {list.error && <ErrorBox error={list.error} />}
      {artifacts === undefined && !list.error && <Busy />}
      {artifacts?.length === 0 && <p>The run has no artifacts yet.</p>}
      {artifacts &&
        kinds.map(([kind, heading]) => {
          const ofKind = artifacts.filter(a => a.kind === kind);
          return (
            ofKind.length > 0 && (
              <section key={kind} aria-label={heading} data-kind={kind}>
                <h3>{heading}</h3>
                <Table head={["Step", "Execution", "Name", "Type"]}>
                  {ofKind.map(a => (
                    <tr key={`${a.ref}\n${a.step_id}\n${a.execution}\n${a.name}`}>
                      <td>{a.step_id}</td>
                      <td>{a.execution}</td>
                      <td>
                        <a href={links.artifact(runId, a.ref)}>{a.name}</a>
                      </td>
                      <td>{a.type}</td>
                    </tr>
                  ))}
                </Table>
              </section>
            )
          );
        })}
    </section>
  );
}
