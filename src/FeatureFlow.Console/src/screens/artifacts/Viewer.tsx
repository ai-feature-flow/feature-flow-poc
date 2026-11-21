import { api, read } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import * as links from "../../app/links";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import "./Viewer.css";

export interface ArtifactViewerProps {
  runId: string;
  /** The artifact's reference, URL-decoded from #/runs/{id}/artifacts/{ref}. */
  reference: string;
}

/** How much of an artifact the page shows; the rest is downloaded. */
const shown = 1024 * 1024;

/** The artifact's content, read with the token, and the first part of it as text. */
async function contentOf(runId: string, reference: string): Promise<{ blob: Blob; text: string }> {
  const blob = await read(api.GET("/runs/{id}/artifacts/{reference}", { params: { path: { id: runId, reference } }, parseAs: "blob" }));
  return { blob, text: await blob.slice(0, shown).text() };
}

/** Saves `blob` as `name` through a link to an object URL: a plain link to the API could not send the token. */
function save(blob: Blob, name: string) {
  const link = document.createElement("a");
  link.href = links.download(blob);
  link.download = name;
  link.click();
  // The browser has started the download by then; until revoked, the URL holds the blob.
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

/**
 * One artifact or transcript (#/runs/{id}/artifacts/{ref}; GET /runs/{id}/artifacts/{ref}). Every kind is shown as plain
 * text, Markdown and JSON included (LOG 460): the first 1 MB, and the whole by download.
 */
export function ArtifactViewer({ runId, reference }: ArtifactViewerProps) {
  const content = useLive([`artifact:${runId}\n${reference}`], () => contentOf(runId, reference));
  // What the run's index says of it; a consumed artifact from elsewhere (a parent's contract) is not in it.
  const index = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/artifacts", { params: { path: { id: runId } } })));
  const entry = index.data?.artifacts.find(a => a.ref === reference);
  const name = entry?.name ?? reference;

  return (
    <article class="ff-viewer">
      <p>
        <a href={links.run(runId)}>Back to the run</a>
      </p>
      <h1 class="ff-viewer-name">{name}</h1>
      {entry && (
        <p class="ff-viewer-about">
          {entry.kind.replace("_", " ")} from {entry.step_id || "the run"}, execution {entry.execution}
          {entry.type && `, type ${entry.type}`}
        </p>
      )}
      {content.error && <ErrorBox error={content.error} />}
      {content.data === undefined ? (
        !content.error && <Busy />
      ) : (
        <>
          <p>
            <button type="button" onClick={() => save(content.data!.blob, name)}>
              Download
            </button>
          </p>
          {content.data.blob.size > shown && (
            <p>
              This shows the first 1 MB of {content.data.blob.size} bytes. Download the rest.
            </p>
          )}
          <pre class="ff-viewer-text">{content.data.text}</pre>
        </>
      )}
    </article>
  );
}
