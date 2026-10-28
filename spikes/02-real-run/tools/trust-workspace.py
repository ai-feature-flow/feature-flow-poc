"""Mark the spike working directories as trusted in ~/.claude.json.

`claude --bg` (the start-and-poll surface) refuses to start in a directory the
user has not accepted the workspace-trust dialog for, and the dialog needs a
TTY. `claude -p` skips it, but `--bg` does not. This writes the same flag the
dialog would write.

Run once per directory. Idempotent.
"""

import json
import os
import sys

CONFIG = os.path.expanduser("~/.claude.json")


def main(paths):
    data = {}
    if os.path.exists(CONFIG):
        with open(CONFIG, encoding="utf-8") as handle:
            try:
                data = json.load(handle)
            except ValueError:
                data = {}

    projects = data.setdefault("projects", {})
    for raw in paths:
        key = os.path.abspath(raw)
        entry = projects.setdefault(key, {})
        entry["hasTrustDialogAccepted"] = True
        entry["hasCompletedProjectOnboarding"] = True
        entry.setdefault("allowedTools", [])
        print("trusted: %s" % key)

    with open(CONFIG, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2)


if __name__ == "__main__":
    main(sys.argv[1:] or [os.getcwd()])
