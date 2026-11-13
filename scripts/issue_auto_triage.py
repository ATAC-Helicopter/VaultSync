#!/usr/bin/env python3
"""Conservative GitHub Issue triage, reusing the canonical VaultSync allocator.

Runs against the repository checkout, never against contributor-supplied code.
It does not add Issues to Projects or set Project fields: enable Project #7's
native Auto-add and status workflows (see docs/WORK_TRACKING.md).
"""
from __future__ import annotations

import json
import os
import re
import sys

from work_tracking import (
    TrackingError, allocate_id, collect_used_ids, github_repo, labels_available,
    load_config, repo_root, run,
)

OWNING_ID = re.compile(r"^\s*(BUG-\d{5,}|VS-\d{4,})(?=\s|:|$)", re.I)
EXACT_RELEASE = re.compile(r"^release:(\d+)\.(\d+)\.(\d+)$", re.I)
FORM_PREFIX = re.compile(
    r"^\[(?:Bug|Crash|Feature|Backup/Restore|Update/Install)\]:?\s*", re.I
)
TYPES = {
    "bug": ("kind:bug", "bug"),
    "feature": ("kind:feature", "enhancement"),
    "change": ("kind:change",),
    "security": ("kind:security",),
    "maintenance": ("kind:maintenance",),
}
STATUS_LABELS = {"status:todo", "status:in-progress", "status:done"}


def classify_kind(labels: set[str]) -> str | None:
    """Only explicit Issue Form/maintainer labels, never arbitrary body text."""
    matches = [kind for kind, markers in TYPES.items() if any(m in labels for m in markers)]
    if len(matches) > 1:
        raise TrackingError("Conflicting work types; leave Issue for maintainer triage.")
    return matches[0] if matches else None


def release_family(labels: set[str]) -> tuple[int, int] | None:
    """An exact, sole target-release label is required for an owning work ID."""
    tagged = sorted(label for label in labels if label.startswith("release:"))
    if len(tagged) != 1:
        return None
    match = EXACT_RELEASE.fullmatch(tagged[0])
    if not match:
        return None
    return (int(match.group(1)), int(match.group(2)))


def owning_id(title: str) -> str | None:
    match = OWNING_ID.match(title)
    return match.group(1).upper() if match else None


def target_status(labels: set[str], state: str) -> str:
    current = labels & STATUS_LABELS
    if len(current) > 1:
        raise TrackingError("Conflicting status labels; maintainer reconciliation required.")
    if state.upper() == "CLOSED":
        return "status:done"
    if not current or current == {"status:done"}:
        return "status:todo"
    return next(iter(current))


def clean_title(title: str) -> str:
    return FORM_PREFIX.sub("", title).strip()


def run_triage(issue_number: int) -> None:
    root = repo_root()
    repository = github_repo(root)
    config = load_config(root)
    response = run(
        ["gh", "issue", "view", str(issue_number), "-R", repository,
         "--json", "number,title,labels,state"], cwd=root,
    )
    issue = json.loads(response.stdout)
    title = issue["title"]
    labels = {entry["name"].lower() for entry in issue["labels"]}
    state = issue["state"]
    kind = classify_kind(labels)
    if kind is None:
        print("Unclassified issue: no ID or speculative work type assigned.")
        return

    status = target_status(labels, state)
    available = {label.lower(): label for label in labels_available(root)}
    edits = ["gh", "issue", "edit", str(issue_number), "-R", repository]
    to_add = [label for label in TYPES[kind] if label not in labels and label in available]
    if status not in labels and status in available:
        to_add.append(status)
    to_remove = sorted((labels & STATUS_LABELS) - {status})
    for label in to_add:
        edits += ["--add-label", available[label]]
    for label in to_remove:
        edits += ["--remove-label", available.get(label, label)]

    current_id = owning_id(title)
    if current_id:
        prefix = "BUG-" if kind == "bug" else "VS-"
        if not current_id.startswith(prefix):
            raise TrackingError("Canonical title ID conflicts with issue type.")
    elif state.upper() != "CLOSED":
        release = release_family(labels)
        if release is not None:
            type_config = config["types"][kind]
            used = collect_used_ids(root, config, type_config["id_prefix"])
            new_id = allocate_id(
                used,
                prefix=type_config["id_prefix"],
                major=release[0], minor=release[1],
                family_digits=int(type_config["family_digits"]),
            )
            edits += ["--title", f"{new_id}: {clean_title(title)}"]
            print(f"Allocating {new_id} for issue #{issue_number}.")
        else:
            print("ID deferred: set exactly one precise release:X.Y.Z label after triage.")
    if len(edits) > 6:
        run(edits, cwd=root)
        print(f"Issue #{issue_number} metadata updated.")
    else:
        print(f"Issue #{issue_number} already consistent.")


def main() -> int:
    raw = os.environ.get("ISSUE_NUMBER", "")
    if not raw.isdecimal() or int(raw) < 1:
        raise TrackingError("ISSUE_NUMBER must be a positive issue number.")
    run_triage(int(raw))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (TrackingError, KeyError, ValueError, TypeError) as exc:
        print(f"Issue auto-triage stopped safely: {exc}", file=sys.stderr)
        raise SystemExit(1)
