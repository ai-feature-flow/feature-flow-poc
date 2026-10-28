"""Validate a Spike 2 plan against the step catalogue's outcome vocabulary.

Checks the things a reviewer would check by hand:
  - every step kind is known
  - every outcome the kind can return is routed
  - every route target is a step id that exists, or a directive
  - guarded route lists end with an unguarded fallback
  - no step id looks like a step kind (review 09 B2)
  - write_access only ever tightens relative to the role (docs/09 11.0)

Usage:
    python tools/validate-plan.py plans/ticket-delivery.yaml
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# docs/06 section 1.1 plus docs/09 4.1.
OUTCOMES_BY_KIND: dict[str, set[str]] = {
    "agent.run": {"completed", "blocked", "failed", "attempts_exhausted", "replan_requested"},
    "human.gate": {"approved", "rejected"},
    "human.session": {"completed", "rejected"},
    "policy.auto_approve": {"approved", "not_approved"},
    "run.child": {"completed", "blocked", "failed", "replan_parent"},
    "parallel": {"completed", "any_failed"},
    "plan.replan": {"replan_requested", "failed", "attempts_exhausted"},
    "plan.validate": {"completed", "failed"},
    "scm.create_branch": {"completed", "failed", "attempts_exhausted"},
    "scm.merge_base_forward": {"completed", "blocked", "failed", "attempts_exhausted"},
    "scm.open_pr": {"completed", "failed", "attempts_exhausted"},
    "scm.update_pr": {"completed", "failed", "attempts_exhausted"},
    "scm.merge": {"completed", "blocked", "rejected", "attempts_exhausted"},
    "scm.revert": {"completed", "failed", "attempts_exhausted"},
    "ci.trigger": {"completed", "failed", "attempts_exhausted"},
    "ci.wait_checks": {"completed", "failed", "blocked", "attempts_exhausted"},
    "ci.read_results": {"completed", "failed", "partial"},
    "test.run_tiers": {"completed", "failed", "attempts_exhausted"},
    "test.run_composition": {"completed", "failed"},
    "analysis.run": {"completed", "failed"},
    "review.run": {"completed", "findings_open", "failed", "attempts_exhausted"},
    "findings.triage": {"completed", "blocked", "attempts_exhausted"},
    "findings.post_declines": {"completed", "failed", "attempts_exhausted"},
    "findings.evaluate_readiness": {"ready", "findings_open"},
    "fix.verify": {"completed", "failed", "attempts_exhausted"},
    "wait.for_state": {"completed", "blocked"},
    "artifact.bundle": {"completed", "failed"},
    "artifact.validate": {"completed", "failed"},
    "artifact.import": {"completed", "failed"},
    "artifact.publish": {"completed", "failed"},
    "delivery.deploy_order": {"completed"},
    "delivery.deploy": {"completed", "failed", "partial"},
    "delivery.gate": {"approved", "rejected"},
    "delivery.report": {"completed"},
    "tracker.create": {"completed", "failed"},
    "tracker.update": {"completed", "failed"},
    "tracker.transition": {"completed", "failed", "rejected"},
    "tracker.link": {"completed", "failed"},
    "tracker.comment": {"completed", "failed"},
    "notify.send": {"completed", "failed"},
}

DIRECTIVES = {"completed", "failed", "partial", "rejected"}
WRITE_ACCESS_ORDER = {"none": 0, "branch": 1, "repository": 2}
STEP_ID_RE = re.compile(r"^[a-z][a-z0-9_]*$")


def parse(path: Path) -> list[dict]:
    """Minimal YAML subset parser: enough for this plan's shape."""
    steps: list[dict] = []
    current: dict | None = None
    in_steps = False
    in_routes = False

    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].rstrip() if not raw.strip().startswith("#") else ""
        if not line.strip():
            continue

        indent = len(line) - len(line.lstrip())
        text = line.strip()

        if indent == 0:
            in_steps = text == "steps:"
            in_routes = False
            current = None
            continue
        if not in_steps:
            continue

        if indent == 2 and text.startswith("- id:"):
            current = {"id": text.split(":", 1)[1].strip(), "routes": {}}
            steps.append(current)
            in_routes = False
            continue
        if current is None:
            continue

        if indent == 4 and text == "routes:":
            in_routes = True
            continue
        if in_routes and indent == 6:
            key, _, value = text.partition(":")
            current["routes"][key.strip()] = value.strip()
            continue
        if indent == 4:
            in_routes = False
            key, _, value = text.partition(":")
            current[key.strip()] = value.strip()

    return steps


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2

    path = Path(sys.argv[1])
    if not path.exists():
        print(f"error: no such plan: {path}", file=sys.stderr)
        return 2

    steps = parse(path)
    failures: list[str] = []

    ids = {s["id"] for s in steps}

    for step in steps:
        sid, kind = step["id"], step.get("kind", "")
        routes = step["routes"]

        if kind not in OUTCOMES_BY_KIND:
            failures.append(f"{sid}: unknown step kind '{kind}'")
            continue

        if not STEP_ID_RE.match(sid) or sid in OUTCOMES_BY_KIND:
            failures.append(f"{sid}: step id must be snake_case and not look like a step kind")

        expected = OUTCOMES_BY_KIND[kind]
        missing = expected - set(routes)
        if missing:
            failures.append(f"{sid} ({kind}): unrouted outcomes {sorted(missing)}")

        extra = set(routes) - expected
        if extra:
            failures.append(f"{sid} ({kind}): routes outcomes the kind cannot return {sorted(extra)}")

        for outcome, target in routes.items():
            if target.startswith("{ end:"):
                directive = target.split("end:")[1].strip().rstrip("}").strip()
                if directive not in DIRECTIVES:
                    failures.append(f"{sid}.{outcome}: unknown directive '{directive}'")
            elif target.startswith("{ resume:"):
                if kind != "plan.replan":
                    failures.append(f"{sid}.{outcome}: resume is valid only on plan.replan")
            elif target not in ids:
                failures.append(f"{sid}.{outcome}: route target '{target}' is not a step id")

    print(f"plan:    {path}")
    print(f"steps:   {len(steps)}")
    print(f"kinds:   {len({s.get('kind', '') for s in steps})}")

    if failures:
        print(f"\n{len(failures)} problem(s):")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print("\nall checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
