import { useState } from "preact/hooks";
import { api, read, type Schema } from "../../api/client";
import { useLive } from "../../api/live";
import * as links from "../../app/links";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";
import "./StartRun.css";

type Parameter = Schema<"TemplateParameter">;

/** A parameter's field before any edit: its literal default as the field holds it (a string unquoted), else empty. */
function initial(p: Parameter): string {
  if (p.type === "boolean") return p.default ?? "false";
  if (p.type === "string" && p.default !== null) {
    const value: unknown = JSON.parse(p.default);
    return typeof value === "string" ? value : p.default;
  }
  return p.default ?? "";
}

/** A field's value as the JSON text StartRunRequest.parameters takes, or undefined for an empty field (the template's default applies). */
function json(p: Parameter, field: string): string | undefined {
  if (p.type === "boolean") return field;
  if (field.trim() === "") return undefined;
  return p.type === "string" ? JSON.stringify(field) : field;
}

/** One input for a parameter, by its type: text, a number, a checkbox, or JSON text for any other type. */
function ParameterField({ p, value, onInput }: { p: Parameter; value: string; onInput: (value: string) => void }) {
  const field =
    p.type === "boolean" ? (
      <input type="checkbox" checked={value === "true"} onChange={e => onInput(String(e.currentTarget.checked))} />
    ) : p.type === "string" || p.type === "number" ? (
      <input
        type="text"
        inputMode={p.type === "number" ? "decimal" : undefined}
        value={value}
        required={p.required && p.default_expression === null}
        placeholder={p.default_expression === null ? undefined : "computed at start"}
        onInput={e => onInput(e.currentTarget.value)}
      />
    ) : (
      <textarea value={value} rows={3} required={p.required && p.default_expression === null} onInput={e => onInput(e.currentTarget.value)} />
    );
  return (
    <div class="ff-start-parameter" data-parameter={p.name}>
      <label>
        {p.name} {field}
      </label>
      <p class="ff-start-about">
        {p.type}
        {p.required && ", required"}
        {p.default_expression !== null && `; computed at start from ${p.default_expression} unless given`}
        {p.description && `. ${p.description}`}
      </p>
    </div>
  );
}

/** Start a run from a template (#/start; GET /templates, POST /runs); once started, its page opens. */
export function StartRun() {
  const list = useLive(["templates"], () => read(api.GET("/templates")));
  const [chosen, choose] = useState<string>();
  const [values, setValues] = useState<Record<string, string>>({});
  const [repositories, setRepositories] = useState("");
  const [trackerIssue, setTrackerIssue] = useState("");
  const [dryRun, setDryRun] = useState(false);
  const [sending, setSending] = useState(false);
  const [refused, setRefused] = useState<Error>();

  if (list.data === undefined) return list.error ? <ErrorBox error={list.error} /> : <Busy />;
  const { templates, unreadable } = list.data;
  const template = templates.find(t => t.reference === chosen) ?? templates[0];

  async function start(e: Event) {
    e.preventDefault();
    if (template === undefined) return;
    const parameters: Record<string, string> = {};
    for (const p of template.parameters) {
      // A boolean the person left alone with no literal default is the template's to decide: its default may be an expression.
      if (p.type === "boolean" && values[p.name] === undefined && p.default === null) continue;
      const value = json(p, values[p.name] ?? initial(p));
      if (value !== undefined) parameters[p.name] = value;
    }
    setSending(true);
    setRefused(undefined);
    try {
      const started = await read(
        api.POST("/runs", {
          body: {
            template: template.reference,
            repositories: repositories.split(",").map(r => r.trim()).filter(r => r !== ""),
            parameters,
            dry_run: dryRun,
            tracker_issue: trackerIssue.trim() === "" ? null : trackerIssue.trim(),
          },
        }),
      );
      location.hash = links.run(started.run_id);
    } catch (error) {
      setRefused(error instanceof Error ? error : new Error(String(error)));
      setSending(false);
    }
  }

  return (
    <section class="ff-start">
      <h1>Start a run</h1>
      {list.error && <ErrorBox error={list.error} />}
      {unreadable.map(u => (
        <p key={u.library} class="ff-start-unreadable">
          Library {u.library} could not be read: {u.error}
        </p>
      ))}
      {template === undefined ? (
        <p>There are no templates to start.</p>
      ) : (
        <form onSubmit={start}>
          <label>
            Template{" "}
            <select
              value={template.reference}
              onChange={e => {
                choose(e.currentTarget.value);
                setValues({});
              }}
            >
              {templates.map(t => (
                <option key={t.reference} value={t.reference}>
                  {t.reference}
                </option>
              ))}
            </select>
          </label>
          {template.when && <p class="ff-start-when">{template.when}</p>}
          <fieldset>
            <legend>Parameters</legend>
            {template.parameters.length === 0 && <p>This template has no parameters.</p>}
            {template.parameters.map(p => (
              <ParameterField key={`${template.reference}\n${p.name}`} p={p} value={values[p.name] ?? initial(p)} onInput={v => setValues({ ...values, [p.name]: v })} />
            ))}
          </fieldset>
          <label>
            Repositories (comma-separated) <input type="text" value={repositories} onInput={e => setRepositories(e.currentTarget.value)} />
          </label>
          <label>
            Tracker issue <input type="text" value={trackerIssue} onInput={e => setTrackerIssue(e.currentTarget.value)} />
          </label>
          <label>
            <input type="checkbox" checked={dryRun} onChange={e => setDryRun(e.currentTarget.checked)} /> Dry run
          </label>
          {refused && <ErrorBox error={refused} />}
          <p>
            <button type="submit" disabled={sending}>
              Start
            </button>
          </p>
        </form>
      )}
    </section>
  );
}
