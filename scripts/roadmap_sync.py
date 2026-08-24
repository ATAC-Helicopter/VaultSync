#!/usr/bin/env python3
"""Safely synchronize roadmap ticket contracts into GitHub Project items."""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable


TICKET_ID_PATTERN = re.compile(r"(?:VS|ISS|BUG|REL)-\d+")
OWNER_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9-]{0,38}")
PROJECT_ID_PATTERN = re.compile(r"PVT[A-Za-z0-9_-]+")
ITEM_ID_PATTERN = re.compile(r"PVTI_[A-Za-z0-9_-]+")
MANAGED_BODY_PREFIX = "Synced from ROADMAP.md"
REPOSITORY_NAME = "VaultSync"
REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class RoadmapEntry:
    ticket_id: str
    title: str
    section: str
    description: str
    completed: bool


@dataclass(frozen=True)
class PlannedChange:
    item_id: str
    content_type: str
    issue_number: int | None
    old_title: str
    new_title: str
    title_changed: bool
    body_action: str
    old_body: str
    new_body: str


@dataclass
class _ParserState:
    section: str = ""
    ticket_id: str | None = None
    title_parts: list[str] = field(default_factory=list)
    body_lines: list[str] = field(default_factory=list)
    completed: bool = False
    collecting_title: bool = False


def normalize_title(title: str | None) -> str:
    normalized = re.sub(r"\s+", " ", title or "").strip()
    return normalized[:-1] if normalized.endswith(".") else normalized


def parse_roadmap(text: str) -> dict[str, RoadmapEntry]:
    entries: dict[str, RoadmapEntry] = {}
    state = _ParserState()

    for raw_line in text.splitlines():
        header = _parse_header(raw_line)
        if header is not None:
            _flush_entry(entries, state)
            state.section = header
            continue

        ticket = _parse_ticket(raw_line)
        if ticket is not None:
            _flush_entry(entries, state)
            state.ticket_id, title, state.completed = ticket
            state.title_parts = [title]
            state.collecting_title = True
            continue

        if state.ticket_id is None:
            continue
        _append_continuation(state, raw_line)

    _flush_entry(entries, state)
    return entries


def _parse_header(raw_line: str) -> str | None:
    stripped = raw_line.strip()
    if not stripped.startswith("#"):
        return None
    header = stripped.lstrip("#").strip()
    return header or None


def _parse_ticket(raw_line: str) -> tuple[str, str, bool] | None:
    stripped = raw_line.strip()
    if len(stripped) < 7 or not stripped.startswith("- [") or stripped[4:6] != "] ":
        return None
    marker = stripped[3]
    if marker not in "xX ":
        return None

    identifier, separator, remainder = stripped[6:].partition(" ")
    identifier = identifier.rstrip(":-").strip("`")
    if not separator or TICKET_ID_PATTERN.fullmatch(identifier) is None:
        return None
    title = _remove_priority(remainder.strip())
    if not title:
        return None
    return identifier, title, marker.lower() == "x"


def _remove_priority(text: str) -> str:
    first, separator, remainder = text.partition(" ")
    if first.strip("`") in {"P0", "P1", "P2"} and separator:
        return remainder.strip()
    return text


def _flush_entry(entries: dict[str, RoadmapEntry], state: _ParserState) -> None:
    if state.ticket_id is None:
        return
    title_text = normalize_title(" ".join(state.title_parts))
    description = "\n".join(line for line in state.body_lines if line.strip()).strip()
    entries[state.ticket_id] = RoadmapEntry(
        ticket_id=state.ticket_id,
        title=normalize_title(f"{state.ticket_id}: {title_text}"),
        section=state.section,
        description=description,
        completed=state.completed,
    )
    state.ticket_id = None
    state.title_parts = []
    state.body_lines = []
    state.completed = False
    state.collecting_title = False


def _append_continuation(state: _ParserState, raw_line: str) -> None:
    trimmed = raw_line.strip()
    if not trimmed:
        state.collecting_title = False
        return
    indented = raw_line[:1].isspace()
    if state.collecting_title and indented and not _is_nested_item(trimmed):
        state.title_parts.append(trimmed)
        return
    state.collecting_title = False
    if indented:
        state.body_lines.append(_remove_ticket_indent(raw_line))


def _is_nested_item(text: str) -> bool:
    if text.startswith(("- ", "* ", "+ ")):
        return True
    prefix, separator, _ = text.partition(" ")
    return bool(separator and prefix[:-1].isdigit() and prefix[-1:] in {".", ")"})


def _remove_ticket_indent(line: str) -> str:
    if line.startswith("  "):
        return line[2:].rstrip()
    if line.startswith("\t"):
        return line[1:].rstrip()
    return line.rstrip()


def build_managed_body(entry: RoadmapEntry, item: dict[str, Any]) -> str:
    values = {
        "status": item.get("status") or "Todo",
        "priority": item.get("priority") or "N/A",
        "release": item.get("release") or "1.9.x",
        "area": item.get("area") or "Core",
    }
    lines = [
        MANAGED_BODY_PREFIX,
        f"Section: {entry.section}",
        f"Status: {values['status']}",
        f"Priority: {values['priority']}",
        f"Release: {values['release']}",
        f"Area: {values['area']}",
        "",
        "Description:",
        entry.description,
    ]
    return "\n".join(lines).rstrip()


def plan_changes(items: Iterable[dict[str, Any]], index: dict[str, RoadmapEntry]) -> list[PlannedChange]:
    planned = (_plan_item_change(item, index) for item in items)
    return [change for change in planned if change is not None]


def find_classification_violations(items: Iterable[dict[str, Any]]) -> list[str]:
    violations: list[str] = []
    for item in items:
        content = item.get("content") or {}
        if (content.get("type") or "") not in {"Issue", "DraftIssue"}:
            continue
        # The Project Title field can lag after an issue rename. Classification
        # follows the linked issue title, which is the canonical identity.
        title = str(content.get("title") or item.get("title") or "")
        labels = {str(label).lower() for label in item.get("labels") or []}
        if "bug" in labels and TICKET_ID_PATTERN.search(title):
            identifiers = TICKET_ID_PATTERN.findall(title)
            if any(identifier.startswith("VS-") for identifier in identifiers):
                violations.append(
                    f"{title}: bug-labelled items cannot carry VS identifiers"
                )
        if "kind:vs" in labels and not title.startswith("VS-"):
            violations.append(
                f"{title}: kind:vs items must start with their VS identifier"
            )
        if "bug" in labels and "kind:vs" in labels:
            violations.append(f"{title}: bug and kind:vs labels are mutually exclusive")
    return violations


def _plan_item_change(
    item: dict[str, Any], index: dict[str, RoadmapEntry]
) -> PlannedChange | None:
    content = item.get("content") or {}
    content_type = content.get("type") or ""
    if content_type not in {"Issue", "DraftIssue"}:
        return None

    old_title = str(item.get("title") or content.get("title") or "")
    ticket_match = TICKET_ID_PATTERN.search(old_title)
    entry = index.get(ticket_match.group(0)) if ticket_match else None
    if entry is None:
        return None

    old_body = str(content.get("body") or "")
    generated_body = build_managed_body(entry, item)
    if old_body.strip() and not old_body.startswith(MANAGED_BODY_PREFIX):
        return None
    if old_body and len(generated_body) < len(old_body):
        return None
    if old_body == generated_body:
        return None

    # GitHub titles can intentionally be shorter than roadmap prose. A roadmap
    # match authorizes managed-body repair, never a bulk title rewrite.
    return PlannedChange(
        item_id=str(item.get("id") or content.get("id") or ""),
        content_type=content_type,
        issue_number=content.get("number"),
        old_title=old_title,
        new_title=old_title,
        title_changed=False,
        body_action="update",
        old_body=old_body,
        new_body=generated_body,
    )


def run_gh(arguments: list[str], stdin_text: str | None = None) -> str:
    executable = shutil.which("gh")
    if executable is None:
        raise RuntimeError("GitHub CLI (gh) is required")
    # The executable is resolved locally, shell expansion is disabled, remote
    # identifiers are validated, and document content travels only via stdin.
    completed = subprocess.run(
        [executable, *arguments],  # NOSONAR
        check=True,
        shell=False,
        text=True,
        input=stdin_text,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return completed.stdout


def load_items(args: argparse.Namespace) -> tuple[str, list[dict[str, Any]]]:
    if args.items_snapshot_path:
        if not args.dry_run:
            raise ValueError("--items-snapshot-path is allowed only with --dry-run")
        snapshot_path = resolve_input_path(args.items_snapshot_path)
        snapshot = json.loads(snapshot_path.read_text(encoding="utf-8-sig"))
        return "snapshot", list(snapshot.get("items") or [])

    owner = validate_owner(args.owner)
    project_number = validate_project_number(args.project_number)
    project = json.loads(
        run_gh(["project", "view", project_number, "--owner", owner, "--format", "json"])
    )
    item_data = json.loads(
        run_gh(
            [
                "project",
                "item-list",
                project_number,
                "--owner",
                owner,
                "--limit",
                "1000",
                "--format",
                "json",
            ]
        )
    )
    project_id = str(project["id"])
    if PROJECT_ID_PATTERN.fullmatch(project_id) is None:
        raise ValueError("GitHub returned an invalid project identifier")
    return project_id, list(item_data.get("items") or [])


def validate_owner(owner: str) -> str:
    if OWNER_PATTERN.fullmatch(owner) is None or owner != "ATAC-Helicopter":
        raise ValueError("--owner must identify the VaultSync repository owner")
    return "ATAC-Helicopter"


def validate_project_number(project_number: int) -> str:
    if project_number <= 0:
        raise ValueError("--project-number must be positive")
    return str(project_number)


def resolve_input_path(raw_path: str) -> Path:
    candidate = Path(raw_path)
    if not candidate.is_absolute():
        candidate = REPOSITORY_ROOT / candidate
    resolved = candidate.resolve(strict=True)
    if not resolved.is_relative_to(REPOSITORY_ROOT) or not resolved.is_file():
        raise ValueError("Input files must be regular files inside the repository")
    return resolved


def apply_change(change: PlannedChange, owner: str, project_id: str) -> None:
    if change.content_type == "Issue" and change.issue_number:
        _apply_issue_change(change, owner)
        return

    if change.content_type == "DraftIssue" and change.item_id:
        _apply_draft_change(change, project_id)


def _apply_issue_change(change: PlannedChange, owner: str) -> None:
    validated_owner = validate_owner(owner)
    if change.issue_number is None or change.issue_number <= 0:
        raise ValueError("Issue number must be positive")
    arguments = [
        "issue",
        "edit",
        str(change.issue_number),
        "--repo",
        f"{validated_owner}/{REPOSITORY_NAME}",
        "--body-file",
        "-",
    ]
    run_gh(arguments, change.new_body)


def _apply_draft_change(change: PlannedChange, project_id: str) -> None:
    if ITEM_ID_PATTERN.fullmatch(change.item_id) is None:
        raise ValueError("Draft item identifier is invalid")
    if PROJECT_ID_PATTERN.fullmatch(project_id) is None:
        raise ValueError("Project identifier is invalid")
    query = """mutation($projectId: ID!, $itemId: ID!, $body: String!) {
  updateProjectV2DraftIssue(input: {
    projectId: $projectId,
    draftIssueId: $itemId,
    body: $body
  }) { projectV2DraftIssue { id } }
}"""
    request = json.dumps(
        {
            "query": query,
            "variables": {
                "projectId": project_id,
                "itemId": change.item_id,
                "body": change.new_body,
            },
        }
    )
    run_gh(["api", "graphql", "--input", "-"], request)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--owner", default="ATAC-Helicopter")
    parser.add_argument("--project-number", type=int, default=7)
    parser.add_argument("--roadmap-path", default="ROADMAP.md")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--items-snapshot-path", default="")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    roadmap_path = resolve_input_path(args.roadmap_path)
    roadmap_text = roadmap_path.read_text(encoding="utf-8-sig")
    index = parse_roadmap(roadmap_text)
    project_id, items = load_items(args)
    classification_violations = find_classification_violations(items)
    if classification_violations:
        raise SystemExit(
            "Project work-item classification failed:\n- "
            + "\n- ".join(classification_violations)
        )
    changes = plan_changes(items, index)

    if args.dry_run:
        print(
            json.dumps(
                {
                    "dryRun": True,
                    "indexed": len(index),
                    "changes": [asdict(change) for change in changes],
                    "unchanged": len(items) - len(changes),
                },
                indent=2,
                sort_keys=True,
            )
        )
        return

    for change in changes:
        apply_change(change, args.owner, project_id)
    print(f"Descriptions sync complete. updated={len(changes)} indexed={len(index)}")


if __name__ == "__main__":
    main()
