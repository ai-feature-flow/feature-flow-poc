import { api, read, type Schema } from "../../api/client";
import { runKey, useLive } from "../../api/live";

/** One artifact read as a document (A7): YAML and JSON as data, Markdown as its body with front matter as data. */
export interface Document {
  format: string;
  data: unknown;
  body: string | null;
}

/** The document at `reference`, read again when the run changes; null until read or when it has none. */
export function useDocument(runId: string, reference: string | null | undefined): { doc: Document | null; error?: Error } {
  const live = useLive([runKey(runId), `doc:${reference ?? ""}`], async (): Promise<Document | null> => {
    if (!reference) return null;
    const d = await read(api.GET("/runs/{id}/artifacts/{reference}/document", { params: { path: { id: runId, reference } } }));
    return { format: d.format, data: d.data === null ? null : (JSON.parse(d.data) as unknown), body: d.body };
  });
  return { doc: live.data ?? null, error: live.error };
}

/** The consumed artifact whose type or name is `kind` (`requirements`, `delivery_plan`). */
export const consumedOf = (wait: Schema<"WaitView">, kind: string): Schema<"ConsumedArtifactView"> | undefined =>
  wait.consumed.find(c => c.type?.endsWith(`/${kind}`) || c.type === kind) ?? wait.consumed.find(c => c.name === kind || c.key === kind);

/** A field of a parsed document as a list of objects; anything else is none. */
export const listOf = (data: unknown, key: string): Record<string, unknown>[] => {
  const value = data !== null && typeof data === "object" ? (data as Record<string, unknown>)[key] : undefined;
  return Array.isArray(value) ? value.filter((v): v is Record<string, unknown> => v !== null && typeof v === "object") : [];
};

/** A field as text: a string, a number or a boolean; anything else is empty. */
export const textOf = (value: unknown): string =>
  typeof value === "string" ? value : typeof value === "number" || typeof value === "boolean" ? String(value) : "";
