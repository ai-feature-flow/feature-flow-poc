import { useState } from "preact/hooks";
import { api, ApiError, read } from "../../api/client";
import { runKey, useLive } from "../../api/live";
import * as links from "../../app/links";
import { useViewer } from "../../app/viewer";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";

/** How much of an artifact the raw view shows; the rest is downloaded. */
const shown = 1024 * 1024;

/** The artifact's bytes, read with the token, and the first part of them as text. */
async function rawOf(runId: string, reference: string): Promise<{ blob: Blob; text: string }> {
  const blob = await read(api.GET("/runs/{id}/artifacts/{reference}", { params: { path: { id: runId, reference } }, parseAs: "blob" }));
  // Decoded as a stream, so a character the cut splits is left out rather than shown as a replacement character.
  return { blob, text: new TextDecoder().decode(await blob.slice(0, shown).arrayBuffer(), { stream: true }) };
}

/** The artifact as a document (A7), or null when it is not text (415): then only its raw form and download are offered. */
async function documentOf(runId: string, reference: string) {
  try {
    return await read(api.GET("/runs/{id}/artifacts/{reference}/document", { params: { path: { id: runId, reference } } }));
  } catch (e) {
    if (e instanceof ApiError && e.status === 415) return null;
    throw e;
  }
}

/** Saves `blob` as `name` through a link to an object URL: a plain link to the API could not send the token. */
function save(blob: Blob, name: string) {
  const link = document.createElement("a");
  link.href = links.download(blob);
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

/**
 * One artifact, in the viewer or full size (design-v2 s4.4; A7): YAML and JSON as tables and lists, Markdown as its text
 * (with its front matter as a table), anything else as text; its raw form, a download and its previous revision one click
 * away. Every value is text.
 */
export function Document({ runId, reference }: { runId: string; reference: string }) {
  const [raw, setRaw] = useState(false);
  const viewer = useViewer();
  const index = useLive([runKey(runId)], () => read(api.GET("/runs/{id}/artifacts", { params: { path: { id: runId } } })));
  const doc = useLive([`document:${runId}\n${reference}`], () => documentOf(runId, reference));
  const showRaw = raw || doc.data === null;
  const bytes = useLive([`artifact:${runId}\n${reference}\n${showRaw}`], () => (showRaw ? rawOf(runId, reference) : Promise.resolve(undefined)));
  const entry = index.data?.artifacts.find(a => a.ref === reference);
  const name = entry?.name ?? reference;
  const previous = entry
    ? index.data!.artifacts.filter(a => a.step_id === entry.step_id && a.name === entry.name && a.execution < entry.execution).sort((a, b) => b.execution - a.execution)[0]
    : undefined;

  return (
    <article class="ff-viewer">
      <h3 class="ff-viewer-name">{name}</h3>
      <p class="ff-viewer-about">
        {entry ? `${entry.kind.replace("_", " ")} from ${entry.step_id || "the run"}, execution ${entry.execution}${entry.type ? `, type ${entry.type}` : ""}` : ""}
      </p>
      <p class="ff-viewer-actions">
        {doc.data !== null && (
          <button type="button" class="ff-linkbtn" aria-pressed={raw} onClick={() => setRaw(r => !r)}>
            {raw ? "Formatted" : "Source"}
          </button>
        )}
        {previous && (
          <button
            type="button"
            class="ff-linkbtn"
            onClick={() => viewer.openArtifact({ runId, reference: previous.ref, name: `${previous.name} · exec ${previous.execution}` })}
          >
            Previous revision
          </button>
        )}
        {bytes.data && (
          <button type="button" class="ff-linkbtn" onClick={() => save(bytes.data!.blob, name)}>
            Download
          </button>
        )}
      </p>
      {doc.error && <ErrorBox error={doc.error} />}
      {showRaw ? (
        bytes.error ? (
          <ErrorBox error={bytes.error} />
        ) : bytes.data === undefined ? (
          <Busy />
        ) : (
          <>
            {bytes.data.blob.size > shown && <p>This shows the first 1 MB of {bytes.data.blob.size} bytes. Download the rest.</p>}
            <pre class="ff-viewer-text">{bytes.data.text}</pre>
          </>
        )
      ) : doc.data === undefined ? (
        !doc.error && <Busy />
      ) : (
        <Formatted format={doc.data!.format} data={doc.data!.data} body={doc.data!.body} />
      )}
    </article>
  );
}

function Formatted({ format, data, body }: { format: string; data: string | null; body: string | null }) {
  let value: unknown = undefined;
  if (data !== null) {
    try {
      value = JSON.parse(data);
    } catch {
      value = data;
    }
  }
  return (
    <div class="ff-doc" data-format={format}>
      {value !== undefined && <Data value={value} />}
      {body !== null && body !== "" && <pre class="ff-viewer-text">{body}</pre>}
    </div>
  );
}

const isScalar = (v: unknown) => v === null || typeof v !== "object";
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);

/** A JSON value as tables and lists: an object as a key/value table, a list of objects as a table, a list of scalars as a list. */
export function Data({ value }: { value: unknown }) {
  if (value === null) return <span class="ff-muted">none</span>;
  if (Array.isArray(value)) {
    if (value.length === 0) return <span class="ff-muted">none</span>;
    if (value.every(isScalar))
      return (
        <ul class="ff-doc-list">
          {value.map((v, i) => (
            <li key={i} class="ff-text">
              {String(v)}
            </li>
          ))}
        </ul>
      );
    if (value.every(isObject)) {
      const columns = [...new Set(value.flatMap(v => Object.keys(v)))];
      return (
        <table class="ff-doc-table">
          <thead>
            <tr>
              {columns.map(c => (
                <th key={c}>{c}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {value.map((row, i) => (
              <tr key={i}>
                {columns.map(c => (
                  <td key={c}>{c in row ? <Data value={row[c]} /> : ""}</td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      );
    }
    return (
      <ol class="ff-doc-list">
        {value.map((v, i) => (
          <li key={i}>
            <Data value={v} />
          </li>
        ))}
      </ol>
    );
  }
  if (isObject(value))
    return (
      <table class="ff-doc-kv">
        <tbody>
          {Object.entries(value).map(([k, v]) => (
            <tr key={k}>
              <th>{k}</th>
              <td>
                <Data value={v} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    );
  return <span class="ff-text">{String(value)}</span>;
}
