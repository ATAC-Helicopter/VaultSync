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
    TrackingError, allocate_id, collect_used_ids, fetch_remote_work_items,
    github_repo, labels_available, load_config, repo_root, run,
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
    exact = [EXACT_RELEASE.fullmatch(label) for label in tagged]
    matches = [m for m in exact if m]
    if len(matches) != 1:
        return None
    release = (int(matches[0].group(1)), int(matches[0].group(2)))
    family = f"release:{release[0]}.{release[1]}.x"
    if any(label != family and not EXACT_RELEASE.fullmatch(label) for label in tagged):
        return None
    return release


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


def classify_form_title(title: str) -> str | None:
    """Explicit form prefixes are reliable fallbacks when labels were removed."""
    match = FORM_PREFIX.match(title)
    if not match:
        return None
    prefix = match.group(0).lower()
    if prefix.startswith("[feature]"):
        return "feature"
    return "bug"


def required_assignee(assignees: list[dict[str, str]], fallback: str) -> str | None:
    """Preserve existing ownership; assign a maintainer only for unowned work."""
    return fallback if not assignees else None


def run_triage(issue_number: int) -> None:
    root = repo_root()
    repository = github_repo(root)
    config = load_config(root)
    response = run(
        ["gh", "issue", "view", str(issue_number), "-R", repository,
         "--json", "number,title,labels,state,assignees,milestone"], cwd=root,
    )
    issue = json.loads(response.stdout)
    title = issue["title"]
    labels = {entry["name"].lower() for entry in issue["labels"]}
    state = issue["state"]
    kind = classify_kind(labels)
    title_kind = classify_form_title(title)
    if kind and title_kind and kind != title_kind:
        raise TrackingError("Issue Form title and labels disagree on work type.")
    kind = kind or title_kind

    status = target_status(labels, state)
    available = {label.lower(): label for label in labels_available(root)}
    edits = ["gh", "issue", "edit", str(issue_number), "-R", repository]
    to_add = ([label for label in TYPES[kind] if label not in labels and label in available]
              if kind else [])
    if status not in labels and status in available:
        to_add.append(status)
    to_remove = sorted((labels & STATUS_LABELS) - {status})
    priorities = sorted(label for label in labels if label.startswith("priority:"))
    if len(priorities) > 1:
        raise TrackingError("Conflicting priority labels; maintainer reconciliation required.")
    if not priorities and release_family(labels) is not None and "priority:p2" in available:
        to_add.append(available["priority:p2"])
    for label in to_add:
        edits += ["--add-label", available[label]]
    for label in to_remove:
        edits += ["--remove-label", available.get(label, label)]
    if required_assignee(issue.get("assignees", []), config.get("project_owner", "ATAC-Helicopter")):
        edits += ["--add-assignee", config.get("project_owner", "ATAC-Helicopter")]

    # Set the correct release milestone without silently overwriting a conflicting one.
    release = release_family(labels)
    if release:
        matches = [label for label in labels if EXACT_RELEASE.fullmatch(label)]
        target = matches[0].split(":", 1)[1]
        milestone = issue.get("milestone")
        if milestone and milestone.get("title") != target:
            raise TrackingError(f"Existing milestone conflicts with {target}.")
        if not milestone:
            edits += ["--milestone", target]

    if kind is None:
        print("Awaiting explicit Bug/Feature type; status and assignee only.")
        if len(edits) > 6:
            run(edits, cwd=root)
        return

    current_id = owning_id(title)
    if current_id:
        prefix = "BUG-" if kind == "bug" else "VS-"
        if not current_id.startswith(prefix):
            raise TrackingError("Canonical title ID conflicts with issue type.")
    elif state.upper() != "CLOSED":
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


def reconcile_pending(limit: int = 5) -> None:
    """Catch pending issue events GitHub Actions concurrency may have replaced."""
    root = repo_root()
    repository = github_repo(root)
    all_items = fetch_remote_work_items(root, repository)
    candidates = []
    for item in all_items:
        if item["state"] != "open" or owning_id(item["title"]):
            continue
        labels = {entry["name"].lower() for entry in item["labels"]}
        try:
            kind = classify_kind(labels)
        except TrackingError:
            print(f"Skipping ambiguous issue #{item['number']} during reconciliation.")
            continue
        if kind and release_family(labels) is not None:
            candidates.append(item["number"])
    for number in sorted(candidates)[:limit]:
        run_triage(number)
    print(f"Reconciled up to {limit} accepted unnumbered issues ({len(candidates)} pending).")


def main() -> int:
    raw = os.environ.get("ISSUE_NUMBER", "")
    if not raw.isdecimal():
        raise TrackingError("ISSUE_NUMBER must be a nonnegative issue number.")
    if int(raw) == 0:
        reconcile_pending()
    else:
        run_triage(int(raw))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (TrackingError, KeyError, ValueError, TypeError) as exc:
        print(f"Issue auto-triage stopped safely: {exc}", file=sys.stderr)
        raise SystemExit(1)
