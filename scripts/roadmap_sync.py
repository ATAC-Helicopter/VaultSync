#!/usr/bin/env python3
"""Safely synchronize roadmap ticket contracts into GitHub Project items."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Iterable


TICKET_PATTERN = re.compile(
    r"^\s*-\s+\[[xX ]\]\s+`?(?P<id>(?:VS|ISS|BUG|REL)-\d+)`?\s*[:\-]?\s*(?P<rest>.+?)\s*$"
)
PRIORITY_PATTERN = re.compile(r"^(?:`?P[0-2]`?\s+)")
HEADER_PATTERN = re.compile(r"^\s*#+\s+(.+?)\s*$")
MANAGED_BODY_PREFIX = "Synced from ROADMAP.md"


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


def normalize_title(title: str | None) -> str:
    normalized = re.sub(r"\s+", " ", title or "").strip()
    return normalized[:-1] if normalized.endswith(".") else normalized


def parse_roadmap(text: str) -> dict[str, RoadmapEntry]:
    entries: dict[str, RoadmapEntry] = {}
    current_section = ""
    current_id: str | None = None
    title_parts: list[str] = []
    body_lines: list[str] = []
    completed = False
    collecting_title = False

    def flush() -> None:
        nonlocal current_id, title_parts, body_lines, completed, collecting_title
        if current_id is None:
            return
        title_text = normalize_title(" ".join(title_parts))
        full_title = normalize_title(f"{current_id}: {title_text}")
        description = "\n".join(line for line in body_lines if line.strip()).strip()
        entries[current_id] = RoadmapEntry(
            ticket_id=current_id,
            title=full_title,
            section=current_section,
            description=description,
            completed=completed,
        )
        current_id = None
        title_parts = []
        body_lines = []
        completed = False
        collecting_title = False

    for raw_line in text.splitlines():
        header = HEADER_PATTERN.match(raw_line)
        if header:
            flush()
            current_section = header.group(1).strip()
            continue

        ticket = TICKET_PATTERN.match(raw_line)
        if ticket:
            flush()
            current_id = ticket.group("id")
            rest = PRIORITY_PATTERN.sub("", ticket.group("rest")).strip()
            title_parts = [rest]
            completed = "[x]" in raw_line.lower()
            collecting_title = True
            continue

        if current_id is None:
            continue

        trimmed = raw_line.strip()
        if not trimmed:
            collecting_title = False
            continue

        is_nested_item = bool(re.match(r"^(?:[-*+]\s+|\d+[.)]\s+)", trimmed))
        if collecting_title and raw_line[:1].isspace() and not is_nested_item:
            title_parts.append(trimmed)
            continue

        collecting_title = False
        if raw_line[:1].isspace():
            body_lines.append(_remove_ticket_indent(raw_line))

    flush()
    return entries


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
    changes: list[PlannedChange] = []
    for item in items:
        content = item.get("content") or {}
        content_type = content.get("type") or ""
        if content_type not in {"Issue", "DraftIssue"}:
            continue

        old_title = str(item.get("title") or content.get("title") or "")
        ticket_match = re.search(r"(?:VS|ISS|BUG|REL)-\d+", old_title)
        if ticket_match is None:
            continue
        entry = index.get(ticket_match.group(0))
        if entry is None:
            continue

        old_body = str(content.get("body") or "")
        generated_body = build_managed_body(entry, item)
        body_is_managed = not old_body.strip() or old_body.startswith(MANAGED_BODY_PREFIX)
        generated_is_shorter = bool(old_body) and len(generated_body) < len(old_body)
        if not body_is_managed:
            new_body = old_body
            body_action = "preserve-manual"
        elif generated_is_shorter:
            new_body = old_body
            body_action = "preserve-longer"
        else:
            new_body = generated_body
            body_action = "update" if old_body != generated_body else "unchanged"
        # Project titles can intentionally be shorter than roadmap prose. The
        # reconstructed title is the matching contract, not authorization for a
        # bulk rename of manually curated GitHub issues.
        title_changed = False
        if body_action in {"unchanged", "preserve-manual", "preserve-longer"}:
            continue

        changes.append(
            PlannedChange(
                item_id=str(item.get("id") or content.get("id") or ""),
                content_type=content_type,
                issue_number=content.get("number"),
                old_title=old_title,
                new_title=old_title,
                title_changed=title_changed,
                body_action=body_action,
                old_body=old_body,
                new_body=new_body,
            )
        )
    return changes


def run_gh(arguments: list[str]) -> str:
    completed = subprocess.run(
        ["gh", *arguments],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return completed.stdout


def load_items(args: argparse.Namespace) -> tuple[str, list[dict[str, Any]]]:
    if args.items_snapshot_path:
        if not args.dry_run:
            raise ValueError("--items-snapshot-path is allowed only with --dry-run")
        snapshot = json.loads(Path(args.items_snapshot_path).read_text(encoding="utf-8-sig"))
        return "snapshot", list(snapshot.get("items") or [])

    project = json.loads(
        run_gh(["project", "view", str(args.project_number), "--owner", args.owner, "--format", "json"])
    )
    item_data = json.loads(
        run_gh(
            [
                "project",
                "item-list",
                str(args.project_number),
                "--owner",
                args.owner,
                "--limit",
                "1000",
                "--format",
                "json",
            ]
        )
    )
    return str(project["id"]), list(item_data.get("items") or [])


def apply_change(change: PlannedChange, owner: str, project_id: str) -> None:
    if change.content_type == "Issue" and change.issue_number:
        arguments = ["issue", "edit", str(change.issue_number), "--repo", f"{owner}/VaultSync"]
        if change.title_changed:
            arguments.extend(["--title", change.new_title])
        if change.body_action == "update":
            arguments.extend(["--body", change.new_body])
        run_gh(arguments)
        return

    if change.content_type == "DraftIssue" and change.item_id:
        arguments = ["project", "item-edit", "--id", change.item_id, "--project-id", project_id]
        if change.title_changed:
            arguments.extend(["--title", change.new_title])
        if change.body_action == "update":
            arguments.extend(["--body", change.new_body])
        run_gh(arguments)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--owner", default="ATAC-Helicopter")
    parser.add_argument("--project-number", type=int, default=1)
    parser.add_argument("--roadmap-path", default="ROADMAP.md")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--items-snapshot-path", default="")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    roadmap_text = Path(args.roadmap_path).read_text(encoding="utf-8-sig")
    index = parse_roadmap(roadmap_text)
    project_id, items = load_items(args)
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
        return 0

    for change in changes:
        apply_change(change, args.owner, project_id)
    print(f"Descriptions sync complete. updated={len(changes)} indexed={len(index)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
