// Unit tests of the Markdown reader (src/ui/markdownTree.ts; LOG 542), run by `npm test` and so by `npm run build`.
import assert from "node:assert/strict";
import { test } from "node:test";
import { isWeb } from "../src/app/links.ts";
import { type Block, type Inline, parse } from "../src/ui/markdownTree.ts";

const md = (source: string) => parse(source, isWeb);
const text = (value: string): Inline => ({ type: "text", text: value });
const para = (...children: Inline[]): Block => ({ type: "paragraph", children });

/** Every inline node of a tree, depth first. */
function* inlinesOf(nodes: readonly (Block | Inline)[]): Generator<Inline> {
  for (const node of nodes) {
    switch (node.type) {
      case "heading":
      case "paragraph":
      case "quote":
        yield* inlinesOf(node.children);
        break;
      case "list":
        for (const item of node.items) yield* inlinesOf(item);
        break;
      case "table":
        for (const cell of [...node.head, ...node.rows.flat()]) yield* inlinesOf(cell);
        break;
      case "strong":
      case "em":
      case "del":
      case "link":
        yield node;
        yield* inlinesOf(node.children);
        break;
      case "text":
      case "code":
      case "break":
        yield node;
        break;
    }
  }
}

test("headings, paragraphs and inline formatting", () => {
  assert.deepEqual(md("# Title #\n\nSome *em*, **strong**, ~~gone~~ and `code`.\nNext line  \nafter a break\n\nSetext\n---"), [
    { type: "heading", level: 1, children: [text("Title")] },
    para(
      text("Some "),
      { type: "em", children: [text("em")] },
      text(", "),
      { type: "strong", children: [text("strong")] },
      text(", "),
      { type: "del", children: [text("gone")] },
      text(" and "),
      { type: "code", text: "code" },
      text(".\nNext line"),
      { type: "break" },
      text("after a break"),
    ),
    { type: "heading", level: 2, children: [text("Setext")] },
  ]);
});

test("underscores inside words and lone asterisks stay text", () => {
  assert.deepEqual(md("std:artifact_types/test_results and 2 * 3 * 4 and _real_"), [
    para(text("std:artifact_types/test_results and 2 * 3 * 4 and "), { type: "em", children: [text("real")] }),
  ]);
});

test("lists nest, keep their start and take lazy lines", () => {
  assert.deepEqual(md("- one\n  - inner\n- two\nlazy\n\n3. three\n4. four"), [
    {
      type: "list",
      ordered: false,
      start: 1,
      items: [[para(text("one")), { type: "list", ordered: false, start: 1, items: [[para(text("inner"))]] }], [para(text("two\nlazy"))]],
    },
    { type: "list", ordered: true, start: 3, items: [[para(text("three"))], [para(text("four"))]] },
  ]);
});

test("code blocks keep their text as written, Markdown and markup included", () => {
  assert.deepEqual(md("```yaml\na: *b*\n<b>x</b>\n```\n\n    indented\n      more"), [
    { type: "code", language: "yaml", text: "a: *b*\n<b>x</b>" },
    { type: "code", language: "", text: "indented\n  more" },
  ]);
});

test("tables, with alignment and escaped pipes", () => {
  assert.deepEqual(md("| a | b |\n|:--|--:|\n| `x` | y \\| z |\n| only |"), [
    {
      type: "table",
      align: ["left", "right"],
      head: [[text("a")], [text("b")]],
      rows: [
        [[{ type: "code", text: "x" }], [text("y | z")]],
        [[text("only")], []],
      ],
    },
  ]);
});

test("block quotes and rules", () => {
  assert.deepEqual(md("> quoted\ncontinued\n\n***"), [{ type: "quote", children: [para(text("quoted\ncontinued"))] }, { type: "rule" }]);
});

test("links: http, https and mailto", () => {
  assert.deepEqual(md('[site](https://example.com/a_(b) "t") <http://x.org> <me@example.com> see https://y.org/p.'), [
    para(
      { type: "link", url: "https://example.com/a_(b)", children: [text("site")] },
      text(" "),
      { type: "link", url: "http://x.org", children: [text("http://x.org")] },
      text(" "),
      { type: "link", url: "mailto:me@example.com", children: [text("me@example.com")] },
      text(" see "),
      { type: "link", url: "https://y.org/p", children: [text("https://y.org/p")] },
      text("."),
    ),
  ]);
});

test("a hostile document reads as text: no markup, and no link but http, https and mailto", () => {
  const hostile = [
    "<script>window.ffx = 1</script>",
    "",
    '<img src=x onerror="window.ffx=1"> <b class="ffx">bold</b>',
    "",
    "[click](javascript:alert(1)) [CLICK]( JAVASCRIPT:alert(1)) [data](data:text/html,<b>x</b>) [rel](docs/x.md)",
    "",
    "<javascript:alert(1)> ![img](javascript:alert(1)) [ok](mailto:a@b.c)",
    "",
    '<div onclick="x()">',
    "**<i>still text</i>**",
    "</div>",
  ].join("\n");
  const tree = md(hostile);

  assert.deepEqual(
    [...inlinesOf(tree)].flatMap(n => (n.type === "link" ? [n.url] : [])),
    ["mailto:a@b.c"],
  );
  assert.deepEqual(tree[0], para(text("<script>window.ffx = 1</script>")));
  assert.deepEqual(tree[1], para(text('<img src=x onerror="window.ffx=1"> <b class="ffx">bold</b>')));
  // A refused link keeps its text, and nothing else.
  assert.deepEqual(tree[2], para(text("click"), text(" "), text("CLICK"), text(" "), text("data"), text(" "), text("rel")));
  assert.deepEqual(tree[3], para(text("<javascript:alert(1)> "), text("img"), text(" "), { type: "link", url: "mailto:a@b.c", children: [text("ok")] }));
  assert.deepEqual(tree[4], para(text('<div onclick="x()">\n'), { type: "strong", children: [text("<i>still text</i>")] }, text("\n</div>")));
  assert.equal(tree.length, 5);
});

test("isWeb admits http, https and mailto only", () => {
  for (const url of ["http://a.b", "https://a.b/c?d#e", "mailto:a@b.c", "HTTPS://A.B"]) assert.equal(isWeb(url), true, url);
  for (const url of ["javascript:alert(1)", " javascript:alert(1)", "java\tscript:alert(1)", "data:text/html,x", "vbscript:x", "file:///etc/passwd", "docs/x.md", "#/runs", ""])
    assert.equal(isWeb(url), false, url);
});
