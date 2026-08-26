#!/usr/bin/env python3
"""Audit and repair required date fields on the canonical GitHub Project."""

from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from datetime import date
from typing import Any, Iterable

from roadmap_sync import (
    ITEM_ID_PATTERN,
    PROJECT_ID_PATTERN,
    run_gh,
    validate_owner,
    validate_project_number,
)


REPOSITORY_NAME = "VaultSync"
START_DATE_FIELD = "Start date"
TARGET_DATE_FIELD = "Target date"
COMPLETED_ON_FIELD = "Completed on"
REQUIRED_DATE_FIELDS = (START_DATE_FIELD, TARGET_DATE_FIELD, COMPLETED_ON_FIELD)

ITEMS_QUERY = """query($endCursor: String, $owner: String!, $projectNumber: Int!) {
  user(login: $owner) {
    projectV2(number: $projectNumber) {
      items(first: 100, after: $endCursor) {
        pageInfo { hasNextPage endCursor }
        nodes {
          id
          fieldValues(first: 30) {
            nodes {
              ... on ProjectV2ItemFieldDateValue {
                date
                field { ... on ProjectV2FieldCommon { name } }
              }
              ... on ProjectV2ItemFieldSingleSelectValue {
                name
                field { ... on ProjectV2FieldCommon { name } }
              }
            }
          }
          content {
            ... on Issue {
              title createdAt closedAt
              milestone { title dueOn }
            }
            ... on PullRequest {
              title createdAt closedAt mergedAt
              milestone { title dueOn }
            }
            ... on DraftIssue { title createdAt updatedAt }
          }
        }
      }
    }
  }
}"""

REPOSITORY_DATES_QUERY = """query($owner: String!, $repository: String!) {
  repository(owner: $owner, name: $repository) {
    milestones(first: 100, states: [OPEN, CLOSED]) {
      nodes { title dueOn }
    }
    releases(first: 100) {
      nodes { tagName publishedAt }
    }
  }
}"""


@dataclass(frozen=True)
class DateRepair:
    item_id: str
    title: str
    field_name: str
    value: str
    source: str


@dataclass(frozen=True)
class UnresolvedDate:
    item_id: str
    title: str
    field_name: str
    reason: str


def _iso_date(value: str | None) -> str | None:
    if not value:
        return None
    candidate = value[:10]
    date.fromisoformat(candidate)
    return candidate


def normalize_items(pages: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    normalized: list[dict[str, Any]] = []
    for page in pages:
        nodes = page["data"]["user"]["projectV2"]["items"]["nodes"]
        for node in nodes:
            fields: dict[str, str] = {}
            for field_value in node.get("fieldValues", {}).get("nodes", []):
                field = field_value.get("field") or {}
                name = field.get("name")
                value = field_value.get("date") or field_value.get("name")
                if name and value:
                    fields[str(name)] = str(value)
            normalized.append(
                {
                    "id": node.get("id"),
                    "content": node.get("content") or {},
                    "fields": fields,
                }
            )
    return normalized


def normalize_repository_dates(
    response: dict[str, Any],
) -> tuple[dict[str, str], dict[str, str]]:
    repository = response["data"]["repository"]
    milestones = {
        str(node["title"]): _iso_date(node.get("dueOn"))
        for node in repository["milestones"]["nodes"]
        if node.get("title") and node.get("dueOn")
    }
    releases = {
        str(node["tagName"]).removeprefix("v"): _iso_date(node.get("publishedAt"))
        for node in repository["releases"]["nodes"]
        if node.get("tagName") and node.get("publishedAt")
    }
    return milestones, releases


def plan_repairs(
    items: Iterable[dict[str, Any]],
    milestone_dates: dict[str, str],
    release_dates: dict[str, str],
) -> tuple[list[DateRepair], list[UnresolvedDate]]:
    repairs: list[DateRepair] = []
    unresolved: list[UnresolvedDate] = []

    for item in items:
        _plan_item_repairs(
            item, milestone_dates, release_dates, repairs, unresolved
        )

    return repairs, unresolved


def _plan_item_repairs(
    item: dict[str, Any],
    milestone_dates: dict[str, str],
    release_dates: dict[str, str],
    repairs: list[DateRepair],
    unresolved: list[UnresolvedDate],
) -> None:
    item_id = str(item.get("id") or "")
    content = item.get("content") or {}
    fields = item.get("fields") or {}
    title = str(content.get("title") or fields.get("Title") or item_id)
    start = _iso_date(fields.get(START_DATE_FIELD))
    target = _iso_date(fields.get(TARGET_DATE_FIELD))

    if start is None:
        start = _iso_date(content.get("createdAt"))
        _record_value_or_problem(
            repairs, unresolved, item_id, title, START_DATE_FIELD, start,
            "issue or pull-request creation date",
            "content has no creation timestamp",
        )

    if target is None:
        target, source = _infer_target(content, fields, milestone_dates, release_dates)
        if target is not None and start is not None and target < start:
            target = None
            source = "inferred target precedes the start date"
        _record_value_or_problem(
            repairs, unresolved, item_id, title, TARGET_DATE_FIELD, target,
            source,
            source,
        )

    if fields.get("Status") == "Done" and not fields.get(COMPLETED_ON_FIELD):
        completed = _iso_date(
            content.get("mergedAt")
            or content.get("closedAt")
            or content.get("updatedAt")
        )
        _record_value_or_problem(
            repairs, unresolved, item_id, title, COMPLETED_ON_FIELD, completed,
            "merge, close, or final draft update date",
            "completed item has no completion timestamp",
        )


def _infer_target(
    content: dict[str, Any],
    fields: dict[str, str],
    milestone_dates: dict[str, str],
    release_dates: dict[str, str],
) -> tuple[str | None, str]:
    milestone = content.get("milestone") or {}
    milestone_title = str(milestone.get("title") or "")
    due_on = _iso_date(milestone.get("dueOn"))
    if due_on:
        return due_on, f"milestone {milestone_title} due date"
    if milestone_title in release_dates:
        return release_dates[milestone_title], f"published {milestone_title} release date"

    release = str(fields.get("Release") or "")
    if release in milestone_dates:
        return milestone_dates[release], f"{release} release-horizon milestone due date"
    if release in release_dates:
        return release_dates[release], f"published {release} release date"

    completed = _iso_date(content.get("mergedAt") or content.get("closedAt"))
    if fields.get("Status") == "Done" and completed:
        return completed, "merge or close date for completed unscheduled work"
    return None, "no milestone, release horizon, or completed-work date is available"


def _record_value_or_problem(
    repairs: list[DateRepair],
    unresolved: list[UnresolvedDate],
    item_id: str,
    title: str,
    field_name: str,
    value: str | None,
    source: str,
    reason: str,
) -> None:
    if ITEM_ID_PATTERN.fullmatch(item_id) is None:
        unresolved.append(UnresolvedDate(item_id, title, field_name, "invalid item identifier"))
    elif value:
        repairs.append(DateRepair(item_id, title, field_name, value, source))
    else:
        unresolved.append(UnresolvedDate(item_id, title, field_name, reason))


def load_live_state(
    owner: str, project_number: int
) -> tuple[str, dict[str, str], list[dict[str, Any]], dict[str, str], dict[str, str]]:
    validated_owner = validate_owner(owner)
    validated_number = validate_project_number(project_number)
    project = json.loads(
        run_gh(
            [
                "project", "view", validated_number,
                "--owner", validated_owner,
                "--format", "json",
            ]
        )
    )
    project_id = str(project.get("id") or "")
    if PROJECT_ID_PATTERN.fullmatch(project_id) is None:
        raise ValueError("GitHub returned an invalid project identifier")

    field_data = json.loads(
        run_gh(
            [
                "project", "field-list", validated_number,
                "--owner", validated_owner,
                "--format", "json",
            ]
        )
    )
    field_ids = {
        str(field["name"]): str(field["id"])
        for field in field_data.get("fields") or []
        if field.get("name") in REQUIRED_DATE_FIELDS
    }
    missing_fields = set(REQUIRED_DATE_FIELDS) - set(field_ids)
    if missing_fields:
        raise ValueError(f"Project is missing required date fields: {sorted(missing_fields)}")

    pages = json.loads(
        run_gh(
            [
                "api", "graphql", "--paginate", "--slurp",
                "-f", f"query={ITEMS_QUERY}",
                "-f", f"owner={validated_owner}",
                "-F", f"projectNumber={validated_number}",
            ]
        )
    )
    repository_dates = json.loads(
        run_gh(
            [
                "api", "graphql",
                "-f", f"query={REPOSITORY_DATES_QUERY}",
                "-f", f"owner={validated_owner}",
                "-f", f"repository={REPOSITORY_NAME}",
            ]
        )
    )
    milestone_dates, release_dates = normalize_repository_dates(repository_dates)
    return project_id, field_ids, normalize_items(pages), milestone_dates, release_dates


def apply_repairs(
    repairs: Iterable[DateRepair], project_id: str, field_ids: dict[str, str]
) -> None:
    if PROJECT_ID_PATTERN.fullmatch(project_id) is None:
        raise ValueError("Project identifier is invalid")
    for repair in repairs:
        field_id = field_ids[repair.field_name]
        run_gh(
            [
                "project", "item-edit",
                "--id", repair.item_id,
                "--project-id", project_id,
                "--field-id", field_id,
                "--date", repair.value,
            ]
        )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--owner", default="ATAC-Helicopter")
    parser.add_argument("--project-number", type=int, default=7)
    parser.add_argument("--apply", action="store_true")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    project_id, field_ids, items, milestones, releases = load_live_state(
        args.owner, args.project_number
    )
    repairs, unresolved = plan_repairs(items, milestones, releases)
    report = {
        "projectItems": len(items),
        "repairs": [asdict(repair) for repair in repairs],
        "unresolved": [asdict(problem) for problem in unresolved],
        "writeMode": bool(args.apply),
    }
    print(json.dumps(report, indent=2, sort_keys=True))
    if unresolved:
        raise SystemExit("Project date integrity failed; no changes were applied.")
    if args.apply:
        apply_repairs(repairs, project_id, field_ids)


if __name__ == "__main__":
    main()
