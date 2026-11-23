// Markdown read into a tree (LOG 542), which ui/Markdown.tsx turns into elements: the text-only rule (LOG 455, 460) holds,
// because nothing here or there is ever HTML. It reads the common subset of CommonMark and GitHub's tables: ATX and setext
// headings, paragraphs, hard breaks, bullet and ordered lists, block quotes, fenced and indented code, tables, rules,
// emphasis, strong, strikethrough, code spans, links, images (as links), autolinks and bare http(s) URLs. Raw HTML is not
// read: it stays text. A link is a link only where `linkable` allows its URL; otherwise its text is text.
// No imports, so `npm test` can load it in Node (tests/markdownTree.test.ts).

export type Inline =
  | { type: "text"; text: string }
  | { type: "strong" | "em" | "del"; children: Inline[] }
  | { type: "code"; text: string }
  | { type: "link"; url: string; children: Inline[] }
  | { type: "break" };

export type Align = "left" | "center" | "right" | null;

export type Block =
  | { type: "heading"; level: number; children: Inline[] }
  | { type: "paragraph"; children: Inline[] }
  | { type: "code"; language: string; text: string }
  | { type: "quote"; children: Block[] }
  | { type: "list"; ordered: boolean; start: number; items: Block[][] }
  | { type: "table"; align: Align[]; head: Inline[][]; rows: Inline[][][] }
  | { type: "rule" };

/** Whether a URL may be a link. */
export type Linkable = (url: string) => boolean;

const fenceOpen = /^( {0,3})(`{3,}|~{3,})(.*)$/;
const atx = /^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/;
const rule = /^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$/;
const quoted = /^ {0,3}> ?/;
const item = /^( {0,3})([-+*]|(\d{1,9})([.)]))( +|$)/;
const setext = /^ {0,3}(=+|-+)[ \t]*$/;
const delimiterRow = /^ {0,3}\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$/;

const blank = (line: string) => line.trim() === "";
const indentOf = (line: string) => /^ */.exec(line)![0].length;

/** Whether a line starts a block that ends a paragraph. */
function interrupts(line: string): boolean {
  const listed = item.exec(line);
  return (
    fenceOpen.test(line) ||
    atx.test(line) ||
    rule.test(line) ||
    quoted.test(line) ||
    (listed !== null && line.slice(listed[0].length).trim() !== "" && (listed[3] === undefined || listed[3] === "1"))
  );
}

/** `source` as Markdown blocks. */
export function parse(source: string, linkable: Linkable): Block[] {
  const lines = source.replace(/\r\n?/g, "\n").split("\n").map(l => l.replace(/^\t+/, t => "    ".repeat(t.length)));
  return blocks(lines, linkable);
}

function blocks(lines: string[], linkable: Linkable): Block[] {
  const out: Block[] = [];
  const text = (s: string) => inlines(s, linkable);
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    let m: RegExpExecArray | null;
    if (blank(line)) {
      i++;
    } else if ((m = fenceOpen.exec(line)) && !(m[2][0] === "`" && m[3].includes("`"))) {
      const [, indent, marker, info] = m;
      const close = new RegExp(`^ {0,3}${marker[0] === "`" ? "`" : "~"}{${marker.length},}[ \\t]*$`);
      const body: string[] = [];
      for (i++; i < lines.length && !close.test(lines[i]); i++) body.push(lines[i].slice(Math.min(indent.length, indentOf(lines[i]))));
      i++;
      out.push({ type: "code", language: info.trim().split(/\s+/)[0] ?? "", text: body.join("\n") });
    } else if ((m = atx.exec(line))) {
      out.push({ type: "heading", level: m[1].length, children: text(m[2] ?? "") });
      i++;
    } else if (rule.test(line)) {
      out.push({ type: "rule" });
      i++;
    } else if (quoted.test(line)) {
      const inner: string[] = [];
      for (; i < lines.length && !blank(lines[i]); i++) {
        if (quoted.test(lines[i])) inner.push(lines[i].replace(quoted, ""));
        else if (!interrupts(lines[i]) && !blank(inner[inner.length - 1] ?? "")) inner.push(lines[i]);
        else break;
      }
      out.push({ type: "quote", children: blocks(inner, linkable) });
    } else if ((m = item.exec(line))) {
      const ordered = m[3] !== undefined;
      const delimiter = ordered ? m[4] : m[2];
      const items: Block[][] = [];
      while (i < lines.length) {
        const at = item.exec(lines[i]);
        if (!at || (at[3] !== undefined) !== ordered || (ordered ? at[4] : at[2]) !== delimiter) break;
        const spaces = at[5].length;
        const content = at[1].length + at[2].length + (spaces >= 1 && spaces <= 4 ? spaces : 1);
        const body = [lines[i].slice(content)];
        for (i++; i < lines.length; i++) {
          const next = lines[i];
          if (blank(next)) body.push("");
          else if (indentOf(next) >= content) body.push(next.slice(content));
          // A lazy continuation of the item's last paragraph.
          else if (!blank(body[body.length - 1]) && !interrupts(next) && !item.test(next)) body.push(next);
          else break;
        }
        items.push(blocks(body, linkable));
      }
      out.push({ type: "list", ordered, start: ordered ? Number(m[3]) : 1, items });
    } else if (indentOf(line) >= 4) {
      const body: string[] = [];
      for (; i < lines.length && (blank(lines[i]) || indentOf(lines[i]) >= 4); i++) body.push(lines[i].slice(4));
      while (body.length > 0 && blank(body[body.length - 1])) body.pop();
      out.push({ type: "code", language: "", text: body.join("\n") });
    } else if (line.includes("|") && i + 1 < lines.length && delimiterRow.test(lines[i + 1]) && cells(line).length === cells(lines[i + 1]).length) {
      const head = cells(line);
      const align = cells(lines[i + 1]).map((c): Align => (c.startsWith(":") ? (c.endsWith(":") ? "center" : "left") : c.endsWith(":") ? "right" : null));
      const rows: Inline[][][] = [];
      for (i += 2; i < lines.length && !blank(lines[i]) && !interrupts(lines[i]); i++) {
        const row = cells(lines[i]);
        rows.push(head.map((_, c) => text(row[c] ?? "")));
      }
      out.push({ type: "table", align, head: head.map(text), rows });
    } else {
      const para = [line];
      let level = 0;
      for (i++; i < lines.length && !blank(lines[i]); i++) {
        const under = setext.exec(lines[i]);
        if (under) {
          level = under[1][0] === "=" ? 1 : 2;
          i++;
          break;
        }
        if (interrupts(lines[i])) break;
        para.push(lines[i]);
      }
      const children = text(para.map(l => l.replace(/^[ \t]+/, "")).join("\n").replace(/[ \t]+$/, ""));
      out.push(level > 0 ? { type: "heading", level, children } : { type: "paragraph", children });
    }
  }
  return out;
}

/** A table row's cells: split on pipes that are not escaped, the row's outer pipes dropped, `\|` read as a pipe. */
function cells(row: string): string[] {
  const trimmed = row.trim().replace(/^\|/, "").replace(/(?<!\\)\|$/, "");
  return trimmed.split(/(?<!\\)\|/).map(c => c.trim().replace(/\\\|/g, "|"));
}

const punctuation = /[!-/:-@[-`{-~]/;
const word = /[\p{L}\p{N}]/u;
const space = (c: string | undefined) => c === undefined || /\s/.test(c);

/** Inline Markdown as a tree. */
export function inlines(source: string, linkable: Linkable): Inline[] {
  const out: Inline[] = [];
  let buffer = "";
  const flush = () => {
    if (buffer !== "") out.push({ type: "text", text: buffer });
    buffer = "";
  };
  const push = (node: Inline) => {
    flush();
    out.push(node);
  };

  let i = 0;
  while (i < source.length) {
    const c = source[i];
    const rest = source.slice(i);

    if (c === "\\" && i + 1 < source.length) {
      if (source[i + 1] === "\n") {
        push({ type: "break" });
        i += 2;
        continue;
      }
      if (punctuation.test(source[i + 1])) {
        buffer += source[i + 1];
        i += 2;
        continue;
      }
    }

    if (c === "`") {
      const run = /^`+/.exec(rest)![0];
      const close = closingTicks(source, i + run.length, run.length);
      if (close >= 0) {
        let code = source.slice(i + run.length, close).replace(/\n/g, " ");
        if (code.length > 2 && code.startsWith(" ") && code.endsWith(" ") && code.trim() !== "") code = code.slice(1, -1);
        push({ type: "code", text: code });
        i = close + run.length;
      } else {
        buffer += run;
        i += run.length;
      }
      continue;
    }

    if (c === "\n") {
      if (/ {2,}$/.test(buffer)) {
        buffer = buffer.replace(/ +$/, "");
        push({ type: "break" });
      } else buffer += "\n";
      i++;
      continue;
    }

    if (c === "<") {
      const auto = /^<([a-zA-Z][a-zA-Z0-9+.-]{1,31}:[^\s<>]*)>/.exec(rest);
      const email = /^<([^\s@<>\\]+@[^\s@<>\\]+)>/.exec(rest);
      const url = auto ? auto[1] : email ? `mailto:${email[1]}` : null;
      if (url !== null && linkable(url)) {
        push({ type: "link", url, children: [{ type: "text", text: auto ? auto[1] : email![1] }] });
        i += (auto ?? email)![0].length;
        continue;
      }
    }

    if (c === "[" || (c === "!" && source[i + 1] === "[")) {
      const link = linkAt(source, c === "!" ? i + 1 : i);
      if (link) {
        const children = inlines(link.label, linkable);
        if (linkable(link.url)) push({ type: "link", url: link.url, children });
        else {
          flush();
          out.push(...children);
        }
        i = link.end;
        continue;
      }
    }

    if (c === "*" || c === "_" || c === "~") {
      const span = emphasisAt(source, i);
      if (span) {
        push({ type: span.type, children: inlines(span.inner, linkable) });
        i = span.end;
        continue;
      }
    }

    if ((c === "h" || c === "H") && (i === 0 || /[\s(*_~]/.test(source[i - 1]))) {
      const bare = /^https?:\/\/[^\s<]*[^\s<?!.,:;*_~'")\]]/i.exec(rest);
      if (bare && linkable(bare[0])) {
        push({ type: "link", url: bare[0], children: [{ type: "text", text: bare[0] }] });
        i += bare[0].length;
        continue;
      }
    }

    buffer += c;
    i++;
  }
  flush();
  return out;
}

/** Where a run of exactly `length` backticks starts at or after `from`; -1 if none. */
function closingTicks(source: string, from: number, length: number): number {
  for (let j = from; j < source.length; ) {
    if (source[j] !== "`") {
      j++;
      continue;
    }
    const run = /^`+/.exec(source.slice(j))![0].length;
    if (run === length) return j;
    j += run;
  }
  return -1;
}

/** `[label](url "title")` at `start` (its `[`): the label, the URL and where it ends; null if it is not one. */
function linkAt(source: string, start: number): { label: string; url: string; end: number } | null {
  let depth = 0;
  let j = start;
  for (; j < source.length; j++) {
    const c = source[j];
    if (c === "\\") j++;
    else if (c === "`") {
      const run = /^`+/.exec(source.slice(j))![0].length;
      const close = closingTicks(source, j + run, run);
      if (close >= 0) j = close + run - 1;
    } else if (c === "[") depth++;
    else if (c === "]" && --depth === 0) break;
  }
  if (j >= source.length || source[j + 1] !== "(") return null;
  const label = source.slice(start + 1, j);
  let k = j + 2;
  while (source[k] === " " || source[k] === "\n") k++;
  let url = "";
  if (source[k] === "<") {
    const close = source.indexOf(">", k);
    if (close < 0 || source.slice(k + 1, close).includes("\n")) return null;
    url = source.slice(k + 1, close);
    k = close + 1;
  } else {
    let parens = 0;
    for (; k < source.length && !/\s/.test(source[k]); k++) {
      if (source[k] === "\\" && punctuation.test(source[k + 1] ?? "")) {
        url += source[++k];
        continue;
      }
      if (source[k] === "(") parens++;
      else if (source[k] === ")" && parens-- === 0) break;
      url += source[k];
    }
  }
  while (source[k] === " " || source[k] === "\n") k++;
  const title = /^("[^"]*"|'[^']*'|\([^)]*\))[ \n]*/.exec(source.slice(k));
  if (title) k += title[0].length;
  return source[k] === ")" ? { label, url, end: k + 1 } : null;
}

/** Emphasis (`*x*`, `_x_`), strong (`**x**`, `__x__`) or strikethrough (`~~x~~`) opening at `start`; null if none closes. */
function emphasisAt(source: string, start: number): { type: "em" | "strong" | "del"; inner: string; end: number } | null {
  const ch = source[start];
  const run = new RegExp(`^\\${ch}+`).exec(source.slice(start))![0].length;
  const size = ch === "~" ? (run === 2 ? 2 : 0) : Math.min(run, 2);
  if (size === 0 || space(source[start + size])) return null;
  if (ch === "_" && word.test(source[start - 1] ?? "")) return null;
  for (let j = start + size + 1; j < source.length; ) {
    const c = source[j];
    if (c === "\\") {
      j += 2;
      continue;
    }
    if (c === "`") {
      const ticks = /^`+/.exec(source.slice(j))![0].length;
      const close = closingTicks(source, j + ticks, ticks);
      j = close >= 0 ? close + ticks : j + ticks;
      continue;
    }
    if (c !== ch) {
      j++;
      continue;
    }
    const length = new RegExp(`^\\${ch}+`).exec(source.slice(j))![0].length;
    const fits = size === 1 ? length === 1 : length >= 2;
    if (fits && !space(source[j - 1]) && !(ch === "_" && word.test(source[j + size] ?? "")))
      return { type: size === 2 ? (ch === "~" ? "del" : "strong") : "em", inner: source.slice(start + size, j), end: j + size };
    j += length;
  }
  return null;
}
