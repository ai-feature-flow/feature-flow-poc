import { useState } from "preact/hooks";
import { api, type Schema } from "../../api/client";
import { invalidate, runKey } from "../../api/live";

export type Body = Omit<Schema<"AnswerRequest">, "wait_id">;

export interface Answering {
  /** Sends the answer; resolves true when the run took it (or recorded it, 202), false when it was refused. */
  send: (body: Body) => Promise<boolean>;
  busy: boolean;
  error?: Error;
  /** The run's reply to an answer that waits for more people (202 `recorded: …`). */
  pending?: string;
}

/**
 * Sends answers to one wait (POST /runs/{id}/answers). After the run takes one, the run and Needs me read again at once;
 * the change stream would deliver it too. A 202 means the answer is recorded and waits for more people (min_count).
 */
export function useAnswer(runId: string, waitId: string): Answering {
  const [state, setState] = useState<Omit<Answering, "send">>({ busy: false });
  const send = async (body: Body) => {
    setState(s => ({ ...s, busy: true, error: undefined }));
    try {
      const { data, response } = await api.POST("/runs/{id}/answers", { params: { path: { id: runId } }, body: { wait_id: waitId, ...body } });
      setState({ busy: false, pending: response.status === 202 ? data?.result.replace(/^recorded: /, "") : undefined });
      invalidate([runKey(runId), "attention"]);
      return true;
    } catch (e) {
      setState(s => ({ ...s, busy: false, error: e instanceof Error ? e : new Error(String(e)) }));
      return false;
    }
  };
  return { ...state, send };
}
