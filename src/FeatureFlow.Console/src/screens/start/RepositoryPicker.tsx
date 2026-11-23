import { useState } from "preact/hooks";
import { api, read } from "../../api/client";
import { invalidate, useLive } from "../../api/live";
import { Busy } from "../../ui/Busy";
import { ErrorBox } from "../../ui/ErrorBox";

/**
 * The repositories a run works in, chosen from those registered (GET /repositories; those only runs name are offered too,
 * marked), with a repository registered in place by owner/name or GitHub URL (POST /repositories; design-v2 s4.7, A16).
 * A registered repository is chosen at once; a refusal shows the API's reason.
 */
export function RepositoryPicker({ chosen, onChange }: { chosen: readonly string[]; onChange: (chosen: string[]) => void }) {
  const list = useLive(["repositories"], () => read(api.GET("/repositories")));
  const [entry, setEntry] = useState("");
  const [busy, setBusy] = useState(false);
  const [refused, setRefused] = useState<Error>();

  async function register() {
    setBusy(true);
    setRefused(undefined);
    try {
      const added = await read(api.POST("/repositories", { body: { github: entry.trim() } }));
      setEntry("");
      if (!chosen.includes(added.name)) onChange([...chosen, added.name]);
      await invalidate(["repositories"]);
    } catch (error) {
      setRefused(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setBusy(false);
    }
  }

  const toggle = (name: string, on: boolean) => onChange(on ? [...chosen, name] : chosen.filter(n => n !== name));
  const repositories = list.data?.repositories ?? [];

  return (
    <fieldset class="ff-start-repositories">
      <legend>Repositories</legend>
      {list.data === undefined && (list.error ? <ErrorBox error={list.error} /> : <Busy />)}
      {list.data !== undefined && repositories.length === 0 && <p>No repositories are registered yet.</p>}
      {repositories.map(r => (
        <label key={r.name} class="ff-start-repository" data-repository={r.name}>
          <input type="checkbox" checked={chosen.includes(r.name)} onChange={e => toggle(r.name, e.currentTarget.checked)} /> {r.name}
          <span class="ff-start-about">
            {r.registered ? (r.default_branch ? ` default branch ${r.default_branch}` : "") : " named by a run, not registered"}
          </span>
        </label>
      ))}
      <div class="ff-start-register">
        <input
          type="text"
          aria-label="Register a repository"
          placeholder="owner/name or https://github.com/owner/name"
          value={entry}
          onInput={e => setEntry(e.currentTarget.value)}
          onKeyDown={e => {
            if (e.key === "Enter") {
              e.preventDefault();
              if (entry.trim() !== "") void register();
            }
          }}
        />
        <button type="button" disabled={busy || entry.trim() === ""} onClick={() => void register()}>
          Register
        </button>
      </div>
      {refused && <ErrorBox error={refused} />}
    </fieldset>
  );
}
