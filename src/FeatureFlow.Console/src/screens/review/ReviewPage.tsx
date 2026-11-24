/**
 * A run's review report (A14; design-v2 s4.6), `#/runs/{tree}/review` for the tree's root and `#/runs/{tree}/review/{run}`
 * for a run in it, from `GET /runs/{id}/review`: its passes, each angle's verdict and what it checked, the findings with their
 * states and reasons, the external review and the trace. A stub until build-console-b2 package C builds it: it renders nothing.
 */
export function ReviewPage({ tree, runId }: { tree: string; runId: string }) {
  void tree;
  void runId;
  return null;
}
