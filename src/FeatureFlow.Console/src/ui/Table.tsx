import type { ComponentChildren } from "preact";
import "./Table.css";

/** A table with a header row of `head`; the rows are the children. */
export function Table({ head, children }: { head: readonly string[]; children: ComponentChildren }) {
  return (
    <table class="ff-table">
      <thead>
        <tr>
          {head.map(h => (
            <th key={h}>{h}</th>
          ))}
        </tr>
      </thead>
      <tbody>{children}</tbody>
    </table>
  );
}
