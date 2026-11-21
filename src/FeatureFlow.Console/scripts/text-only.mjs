// The text-only rule (LOG 455, 460): API data reaches the page only as text. This walks the TypeScript syntax tree of every
// source under the folders given (default: src) and prints each breach as file:line: what; it exits 1 if there is any.
// `npm run build` runs it, and ConsoleTests.The_console_sources_never_build_markup_or_links_from_data runs it on the sources
// and on cases it must refuse. A syntax tree, not line patterns: JSX allows spaces around `=` and attributes over lines.
//
// Refused:
// - a markup sink or text run as code: innerHTML, outerHTML, insertAdjacentHTML, dangerouslySetInnerHTML, srcdoc,
//   createContextualFragment, DOMParser, setHTMLUnsafe, parseHTMLUnsafe, document.write, eval, Function;
// - a URL attribute (href, src, action, formAction, poster, srcSet, xlinkHref, in any case) whose value is not a call into
//   app/links.ts: in JSX, assigned to a property, through setAttribute, or in the props of h(), createElement() and the like;
// - props spread onto an element ({...props}, or a spread in those calls' props), and setAttribute with a computed name.
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, sep } from "node:path";
import ts from "typescript";

const sinks = new Set([
  "innerHTML",
  "outerHTML",
  "insertAdjacentHTML",
  "dangerouslySetInnerHTML",
  "srcdoc",
  "createContextualFragment",
  "DOMParser",
  "setHTMLUnsafe",
  "parseHTMLUnsafe",
  "eval",
]);
const urlNames = new Set(["href", "src", "action", "formaction", "poster", "srcset", "xlinkhref"]);
const elementCalls = new Set(["h", "createElement", "cloneElement", "jsx", "jsxs", "jsxDEV"]);

/** Whether an attribute or property name is a URL's, whatever its case or namespace (xlink:href). */
const isUrl = name => name !== undefined && urlNames.has(name.replace(/[:-]/g, "").toLowerCase());

/** A name as written: an identifier, a string or number key, or a namespaced JSX name. */
const nameOf = node =>
  ts.isIdentifier(node) || ts.isPrivateIdentifier(node) || ts.isStringLiteralLike(node) || ts.isNumericLiteral(node)
    ? node.text
    : ts.isJsxNamespacedName(node)
      ? `${node.namespace.text}:${node.name.text}`
      : undefined;

const sources = folder =>
  readdirSync(folder, { withFileTypes: true, recursive: true })
    .filter(e => e.isFile() && /\.tsx?$/.test(e.name) && !e.name.endsWith(".gen.d.ts"))
    .map(e => join(e.parentPath, e.name));

function check(root, file, found) {
  const name = relative(root, file).split(sep).join("/");
  const source = ts.createSourceFile(file, readFileSync(file, "utf8"), ts.ScriptTarget.Latest, true, file.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
  const report = (node, what) => found.push(`${name}:${source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1}: ${what}`);
  const isLinks = name === "app/links.ts";

  // What this file imports from app/links.ts: a namespace (links.run(...)) or its functions (run(...)).
  const namespaces = new Set();
  const functions = new Set();
  for (const statement of source.statements)
    if (ts.isImportDeclaration(statement) && /(^|\/)links$/.test(statement.moduleSpecifier.text)) {
      const bindings = statement.importClause?.namedBindings;
      if (bindings && ts.isNamespaceImport(bindings)) namespaces.add(bindings.name.text);
      else if (bindings) for (const element of bindings.elements) functions.add(element.name.text);
    }

  /** Whether a value is built by app/links.ts: one call of its functions, and nothing more. */
  const fromLinks = node => {
    while (node && (ts.isParenthesizedExpression(node) || ts.isJsxExpression(node))) node = node.expression;
    if (!node || !ts.isCallExpression(node)) return false;
    const callee = node.expression;
    return (
      (ts.isIdentifier(callee) && functions.has(callee.text)) ||
      (ts.isPropertyAccessExpression(callee) && ts.isIdentifier(callee.expression) && namespaces.has(callee.expression.text))
    );
  };
  const link = (node, value, what) => {
    if (!isLinks && !fromLinks(value)) report(node, `${what} (a link not built by app/links.ts)`);
  };

  /** h(type, props, ...) and the like: props must be absent or an object whose URL props come from app/links.ts. */
  const elementProps = (node, called, props) => {
    if (!props || props.kind === ts.SyntaxKind.NullKeyword || (ts.isIdentifier(props) && props.text === "undefined")) return;
    if (!ts.isObjectLiteralExpression(props)) return report(node, `${called}() with props it cannot read`);
    for (const property of props.properties)
      if (ts.isSpreadAssignment(property)) report(property, `props spread into ${called}()`);
      else if (property.name && ts.isComputedPropertyName(property.name)) report(property, `a computed prop in ${called}()`);
      else if (isUrl(nameOf(property.name)))
        link(property, ts.isPropertyAssignment(property) ? property.initializer : undefined, `${nameOf(property.name)} in ${called}()`);
  };

  const visit = node => {
    if ((ts.isIdentifier(node) || ts.isPrivateIdentifier(node) || ts.isStringLiteralLike(node)) && sinks.has(node.text)) report(node, node.text);

    if (ts.isJsxSpreadAttribute(node)) report(node, "props spread onto an element");
    else if (ts.isJsxAttribute(node) && isUrl(nameOf(node.name)) && node.initializer) link(node, node.initializer, nameOf(node.name));
    else if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.EqualsToken) {
      const target = node.left;
      const property = ts.isPropertyAccessExpression(target)
        ? target.name.text
        : ts.isElementAccessExpression(target) && ts.isStringLiteralLike(target.argumentExpression)
          ? target.argumentExpression.text
          : undefined;
      if (isUrl(property)) link(node, node.right, `.${property} =`);
    } else if (ts.isCallExpression(node) || ts.isNewExpression(node)) {
      const callee = node.expression;
      const called = ts.isIdentifier(callee) ? callee.text : ts.isPropertyAccessExpression(callee) ? callee.name.text : undefined;
      const args = node.arguments ?? [];
      if (called === "Function") report(node, "Function");
      else if ((called === "write" || called === "writeln") && ts.isPropertyAccessExpression(callee) && callee.expression.getText(source) === "document")
        report(node, `document.${called}`);
      else if (called === "setAttribute" || called === "setAttributeNS") {
        const at = called === "setAttribute" ? 0 : 1;
        const attribute = args[at];
        if (!attribute || !ts.isStringLiteralLike(attribute)) report(node, `${called} with a computed name`);
        else if (isUrl(attribute.text)) link(node, args[at + 1], `${called}("${attribute.text}")`);
      } else if (elementCalls.has(called)) elementProps(node, called, args[1]);
    }

    ts.forEachChild(node, visit);
  };
  visit(source);
}

const folders = process.argv.slice(2);
const found = [];
for (const folder of folders.length > 0 ? folders : ["src"]) {
  const files = sources(folder);
  if (files.length === 0) found.push(`${folder}: no sources to check`);
  for (const file of files) check(folder, file, found);
}
for (const line of found) console.log(line);
process.exit(found.length > 0 ? 1 : 0);
