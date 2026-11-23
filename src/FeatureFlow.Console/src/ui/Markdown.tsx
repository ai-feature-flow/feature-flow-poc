import * as links from "../app/links";
import { type Block, type Inline, parse } from "./markdownTree";

// Markdown as elements (LOG 542): ui/markdownTree.ts reads it into a tree and this builds each element from it, every value
// as text; a link only for http, https and mailto (app/links.ts), opened in a new tab without an opener or a referrer.

/** A Markdown document, formatted. */
export function Markdown({ source }: { source: string }) {
  return <div class="ff-md">{parse(source, links.isWeb).map(block)}</div>;
}

function block(node: Block, key: number) {
  switch (node.type) {
    case "heading": {
      const Heading = `h${Math.min(node.level, 6)}` as "h1";
      return <Heading key={key}>{node.children.map(inline)}</Heading>;
    }
    case "paragraph":
      return <p key={key}>{node.children.map(inline)}</p>;
    case "code":
      return (
        <pre key={key} class="ff-md-code" data-language={node.language || undefined}>
          <code>{node.text}</code>
        </pre>
      );
    case "quote":
      return <blockquote key={key}>{node.children.map(block)}</blockquote>;
    case "list": {
      const items = node.items.map((item, i) => (
        // A tight item, a single paragraph, shows its text without the paragraph's margins.
        <li key={i}>{item.length === 1 && item[0].type === "paragraph" ? item[0].children.map(inline) : item.map(block)}</li>
      ));
      return node.ordered ? (
        <ol key={key} start={node.start === 1 ? undefined : node.start}>
          {items}
        </ol>
      ) : (
        <ul key={key}>{items}</ul>
      );
    }
    case "table":
      return (
        <table key={key} class="ff-doc-table">
          <thead>
            <tr>
              {node.head.map((cell, c) => (
                <th key={c} style={node.align[c] ? { textAlign: node.align[c]! } : undefined}>
                  {cell.map(inline)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {node.rows.map((row, r) => (
              <tr key={r}>
                {row.map((cell, c) => (
                  <td key={c} style={node.align[c] ? { textAlign: node.align[c]! } : undefined}>
                    {cell.map(inline)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      );
    case "rule":
      return <hr key={key} />;
  }
}

function inline(node: Inline, key: number) {
  switch (node.type) {
    case "text":
      return node.text;
    case "strong":
      return <strong key={key}>{node.children.map(inline)}</strong>;
    case "em":
      return <em key={key}>{node.children.map(inline)}</em>;
    case "del":
      return <del key={key}>{node.children.map(inline)}</del>;
    case "code":
      return <code key={key}>{node.text}</code>;
    case "link":
      return (
        <a key={key} href={links.web(node.url)} target="_blank" rel="noopener noreferrer">
          {node.children.map(inline)}
        </a>
      );
    case "break":
      return <br key={key} />;
  }
}
