import { api, read, type Schema } from "../../api/client";
import { invalidate, runKey } from "../../api/live";

/** Applies an override (POST /runs/{id}/overrides) and reads the run again; resolves to the run's reply, throws its refusal. */
export async function override(runId: string, body: Schema<"OverrideRequest">): Promise<string> {
  const reply = await read(api.POST("/runs/{id}/overrides", { params: { path: { id: runId } }, body }));
  void invalidate([runKey(runId)]);
  return reply.result;
}
