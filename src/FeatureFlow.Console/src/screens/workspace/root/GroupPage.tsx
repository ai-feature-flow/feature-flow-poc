import type { GroupNode, Node } from "../tree/model";
import { pathOf } from "../tree/model";

const words: Record<Node["state"], string> = {
  done: "done",
  running: "running",
  needs: "needs you",
  waits: "waiting",
  problem: "in trouble",
  pending: "not started",
  ghost: "not planned yet",
};

/** A group selected in the tree (a fragment's use or a branch): its path, what it holds, and each row in it with its state. */
export function GroupPage({ node }: { node: GroupNode }) {
  return (
    <div class="ff-stack">
      <div class="ff-crumbs">{pathOf(node)}</div>
      <div class="ff-runhdr">
        <h1>{node.label}</h1>
      </div>
      <section class="ff-box ff-detail" aria-label="In this group">
        <header class="ff-pane-head">
          <h2>{node.note || "Nothing in it yet"}</h2>
        </header>
        <table>
          <tbody>
            {node.children.map(c => (
              <tr key={c.key}>
                <td>{c.label}</td>
                <td class={`ff-state-${c.state}`}>{words[c.state]}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
