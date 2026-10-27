#!/usr/bin/env python3
"""Watch a git remote for new commits and report them.

Polls the remote for the configured branch. When the local branch is behind,
it fetches and reports what arrived, with the changed files and, for review
files, the full text so a coordinator can act on it without another step.

Does not pull, merge or rebase. Reporting only: the caller decides what to do.

Usage:
    python tools/watch_remote.py                 # one check, then exit
    python tools/watch_remote.py --interval 60   # poll every 60s until stopped
    python tools/watch_remote.py --json          # machine-readable output

Exit codes: 0 no change or change reported, 1 error.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class Commit:
    sha: str
    subject: str
    body: str
    author: str
    files: list[str] = field(default_factory=list)


@dataclass
class Report:
    branch: str
    behind: int
    commits: list[Commit]
    review_files: dict[str, str] = field(default_factory=dict)

    def as_dict(self) -> dict:
        return {
            "branch": self.branch,
            "behind": self.behind,
            "commits": [
                {
                    "sha": c.sha,
                    "subject": c.subject,
                    "body": c.body,
                    "author": c.author,
                    "files": c.files,
                }
                for c in self.commits
            ],
            "review_files": self.review_files,
        }


def run(args: list[str], cwd: Path) -> subprocess.CompletedProcess:
    return subprocess.run(
        args,
        cwd=cwd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def current_branch(repo: Path) -> str:
    result = run(["git", "rev-parse", "--abbrev-ref", "HEAD"], repo)
    if result.returncode != 0:
        raise RuntimeError(f"not a git repository, or no HEAD: {result.stderr.strip()}")
    return result.stdout.strip()


def fetch(repo: Path) -> None:
    result = run(["git", "fetch", "--quiet", "origin"], repo)
    if result.returncode != 0:
        raise RuntimeError(f"fetch failed: {result.stderr.strip()}")


def behind_count(repo: Path, branch: str) -> int:
    result = run(["git", "rev-list", "--count", f"HEAD..origin/{branch}"], repo)
    if result.returncode != 0:
        raise RuntimeError(f"could not count commits: {result.stderr.strip()}")
    return int(result.stdout.strip() or 0)


def files_for_commit(repo: Path, sha: str) -> list[str]:
    """Files changed by one commit, from the fetched remote ref.

    Asked per commit rather than with `git log --name-only`, because name-only
    output follows the record separator and so lands in the next record.
    """
    result = run(["git", "show", "--name-only", "--format=", sha], repo)
    if result.returncode != 0:
        return []
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def collect_commits(repo: Path, branch: str) -> list[Commit]:
    fmt = "%H%x1f%s%x1f%b%x1f%an%x1e"
    result = run(["git", "log", f"--format={fmt}", f"HEAD..origin/{branch}"], repo)
    if result.returncode != 0:
        raise RuntimeError(f"could not read commits: {result.stderr.strip()}")

    commits: list[Commit] = []
    for record in result.stdout.split("\x1e"):
        if not record.strip():
            continue
        header = record.strip().split("\x1f")
        if len(header) < 4:
            continue
        sha, subject, body, author = header[0], header[1], header[2], header[3]
        commits.append(
            Commit(
                sha=sha,
                subject=subject,
                body=body.strip(),
                author=author,
                files=files_for_commit(repo, sha),
            )
        )
    return commits


def read_review_files(repo: Path, commits: list[Commit], pattern: str, branch: str) -> dict[str, str]:
    """Read new review files straight from the fetched remote ref.

    Uses `git show origin/<branch>:<path>`, so nothing needs to be checked out.
    """
    found: dict[str, str] = {}
    for commit in commits:
        for path in commit.files:
            if pattern not in path or not path.endswith(".md"):
                continue
            if path in found:
                continue
            result = run(["git", "show", f"origin/{branch}:{path}"], repo)
            if result.returncode == 0:
                found[path] = result.stdout
    return found


def check(repo: Path, branch: str, review_pattern: str) -> Report | None:
    fetch(repo)
    behind = behind_count(repo, branch)
    if behind == 0:
        return None
    commits = collect_commits(repo, branch)
    reviews = read_review_files(repo, commits, review_pattern, branch)
    return Report(branch=branch, behind=behind, commits=commits, review_files=reviews)


def print_report(report: Report) -> None:
    print(f"{report.behind} new commit(s) on origin/{report.branch}\n")
    for commit in report.commits:
        print(f"  {commit.sha[:8]}  {commit.subject}  ({commit.author})")
        for path in commit.files:
            print(f"      {path}")
        if commit.body.strip():
            print()
            for line in commit.body.strip().splitlines():
                print(f"      {line}")
        print()
    for path, content in report.review_files.items():
        print("=" * 72)
        print(f"REVIEW FILE: {path}")
        print("=" * 72)
        print(content)
        print()


def main() -> int:
    # Review text is UTF-8 and routinely contains characters the Windows console
    # encoding (cp1252) cannot represent. Without this, print_report raises
    # UnicodeEncodeError after the fetch has already moved origin/<branch>, so
    # the tool reports nothing while the remote ref has silently advanced.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    parser = argparse.ArgumentParser(description="Watch a git remote for new commits.")
    parser.add_argument("--repo", default=".", help="repository path (default: cwd)")
    parser.add_argument("--branch", default=None, help="branch to watch (default: current)")
    parser.add_argument(
        "--interval",
        type=int,
        default=0,
        help="poll every N seconds; 0 means check once and exit",
    )
    parser.add_argument(
        "--review-pattern",
        default="docs/reviews/",
        help="path fragment identifying review files (default: docs/reviews/)",
    )
    parser.add_argument("--json", action="store_true", help="emit JSON instead of text")
    args = parser.parse_args()

    repo = Path(args.repo).resolve()
    try:
        branch = args.branch or current_branch(repo)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    try:
        while True:
            report = check(repo, branch, args.review_pattern)
            if report is not None:
                if args.json:
                    print(json.dumps(report.as_dict(), indent=2))
                else:
                    print_report(report)
            elif not args.interval:
                print(f"up to date with origin/{branch}")
            if not args.interval:
                return 0
            time.sleep(args.interval)
    except RuntimeError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\nstopped")
        return 0


if __name__ == "__main__":
    sys.exit(main())
