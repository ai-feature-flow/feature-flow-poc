// The minimal console (M7): runs, a run's readable form and timeline, and its open decisions to answer. Everything is
// read and answered through the API with the person's own token, so the API authenticates and authorises every call.
// Run data is only ever placed with textContent, never as markup.
"use strict";

const main = document.getElementById("main");
const tokenKey = "ff-token";

function token() { return sessionStorage.getItem(tokenKey) || ""; }

async function api(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: Object.assign({ Authorization: "Bearer " + token() }, body ? { "Content-Type": "application/json" } : {}),
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* the readable form is text */ }
  if (!response.ok) throw new Error((json && json.error) || `${response.status} ${response.statusText}`);
  return json ?? text;
}

function el(tag, props, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (k === "text") node.textContent = v;
    else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v);
  }
  for (const child of children.flat()) if (child != null) node.append(child);
  return node;
}

function table(headings, rows) {
  return el("table", {}, el("thead", {}, el("tr", {}, headings.map(h => el("th", { text: h })))),
    el("tbody", {}, rows.map(cells => el("tr", {}, cells.map(c => el("td", {}, c))))));
}

// replaceChildren takes nodes only: nested lists are flattened and absent parts dropped, as el() does.
function show(...parts) { main.replaceChildren(...parts.flat(3).filter(p => p != null)); }

function runLink(id) { return el("a", { href: "#/runs/" + encodeURIComponent(id), text: id }); }

function status(s) { return el("span", { class: "status-" + s, text: s }); }

async function showRuns() {
  const { runs } = await api("GET", "/runs");
  show(el("h1", { text: "Runs" }),
    runs.length === 0 ? el("p", { class: "note", text: "No runs yet." }) :
      table(["Run", "Template", "Status", "Requested by", "Repositories", "Parent", "Updated"], runs.map(r => [
        runLink(r.runId), r.template, status(r.status), r.requester, (r.repositories || []).join(", "),
        r.parentRunId ? runLink(r.parentRunId) : "", new Date(r.updatedAt).toLocaleString()])));
}

// One open wait and how to answer it: an outcome for a decision, a patch approval or a question; a recovery action for an intervention.
function waitForm(runId, wait) {
  const result = el("div", { class: "note" });
  async function answer(body) {
    result.className = "note";
    result.textContent = "Sending…";
    try {
      const reply = await api("POST", `/runs/${encodeURIComponent(runId)}/answers`, Object.assign({ wait_id: wait.waitId }, body));
      result.textContent = "Answer " + reply.result;
      setTimeout(route, 800);
    } catch (e) {
      result.className = "error";
      result.textContent = e.message;
    }
  }

  const controls = [];
  if (wait.kind === "intervention") {
    const action = el("select", { "aria-label": "recovery" }, ["retry", "go_to", "replan", "grant_attempts", "end"].map(a => el("option", { value: a, text: a })));
    const step = el("input", { placeholder: "step id (go_to, replan)", "aria-label": "step id" });
    const attempts = el("input", { type: "number", min: "1", placeholder: "attempts", "aria-label": "attempts" });
    const end = el("select", { "aria-label": "end status" }, ["failed", "completed", "partial", "aborted", "incomplete"].map(s => el("option", { value: s, text: s })));
    const cleanUp = el("input", { type: "checkbox", "aria-label": "clean up" });
    controls.push(action, step, attempts, end, el("label", {}, cleanUp, " clean_up"),
      el("button", { text: "Recover", onclick: () => answer({
        action: action.value,
        step_id: step.value || undefined,
        attempts: attempts.value ? Number(attempts.value) : undefined,
        status: action.value === "end" ? end.value : undefined,
        clean_up: cleanUp.checked || undefined,
      }) }));
  } else {
    for (const choice of wait.choices || []) controls.push(el("button", { text: choice, onclick: () => answer({ outcome: choice }) }));
    if (wait.kind === "question") {
      const text = el("input", { placeholder: "your answer", "aria-label": "answer" });
      controls.push(text, el("button", { text: "Answer", onclick: () => text.value && answer({ outcome: text.value }) }));
    }
  }

  return el("div", { class: "wait" },
    el("strong", { text: `${wait.kind}: ${wait.decisionType}` }), el("span", { class: "note", text: ` at ${wait.stepId} (${wait.waitId})` }),
    el("p", { text: wait.reason }), el("div", {}, controls), result);
}

async function showRun(id) {
  const [view, plan] = await Promise.all([
    api("GET", "/runs/" + encodeURIComponent(id)),
    api("GET", `/runs/${encodeURIComponent(id)}/plan`).catch(e => "The readable form is not available: " + e.message),
  ]);
  const r = view.run;
  show(
    el("p", {}, el("a", { href: "#/runs", text: "← Runs" })),
    el("h1", { text: r.runId }),
    el("dl", {},
      el("dt", { text: "Template" }), el("dd", { text: r.template }),
      el("dt", { text: "Status" }), el("dd", {}, status(r.status)),
      el("dt", { text: "Requested by" }), el("dd", { text: r.requester }),
      el("dt", { text: "Repositories" }), el("dd", { text: (r.repositories || []).join(", ") || "none" }),
      r.parentRunId ? [el("dt", { text: "Parent" }), el("dd", {}, runLink(r.parentRunId))] : null,
      r.detail ? [el("dt", { text: "Detail" }), el("dd", { text: r.detail })] : null),
    el("h2", { text: "Decisions" }),
    view.waits.length === 0 ? el("p", { class: "note", text: "Nothing waits for a person." }) : view.waits.map(w => waitForm(r.runId, w)),
    view.children.length === 0 ? null : [el("h2", { text: "Child runs" }),
      table(["Run", "Template", "Status"], view.children.map(c => [runLink(c.runId), c.template, status(c.status)]))],
    el("h2", { text: "Plan" }),
    el("p", { class: "note", text: "As the run started; changes made while it runs are in the timeline." }),
    el("pre", { text: plan }),
    el("h2", { text: "Timeline" }),
    table(["#", "At", "Kind", "Step", "Detail"], view.timeline.map(t => [String(t.sequence), new Date(t.at).toLocaleTimeString(), t.kind, t.stepId, t.detail || ""])));
}

async function route() {
  if (!token()) return;
  const match = location.hash.match(/^#\/runs\/(.+)$/);
  try {
    if (match) await showRun(decodeURIComponent(match[1]));
    else await showRuns();
  } catch (e) {
    show(el("p", { class: "error", text: e.message }));
  }
}

document.getElementById("sign-in").addEventListener("submit", e => {
  e.preventDefault();
  const input = document.getElementById("token");
  sessionStorage.setItem(tokenKey, input.value);
  input.value = "";
  route();
});
window.addEventListener("hashchange", route);
route();
