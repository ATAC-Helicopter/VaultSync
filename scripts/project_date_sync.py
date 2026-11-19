#!/usr/bin/env python3
"""Conservative date synchronization for the existing personal GitHub Project #7.

Requires explicit Project-write authorization. Never modifies issue state,
status, priority, release, milestones, assignments, or target dates.
"""
from __future__ import annotations

from datetime import date, datetime, timezone
import json
import os
import sys

from work_tracking import TrackingError, run

OWNER = "ATAC-Helicopter"
NUMBER = 7
REPOSITORY = "ATAC-Helicopter/VaultSync"
START_CUTOFF = date(2026, 11, 13)

QUERY = """query($owner:String!,$number:Int!,$cursor:String) {
  user(login:$owner) { projectV2(number:$number) {
    id
    fields(first:100) { pageInfo { hasNextPage } nodes {
      ... on ProjectV2Field { id name }
      ... on ProjectV2SingleSelectField { id name }
    } }
    items(first:100,after:$cursor) {
      totalCount pageInfo { hasNextPage endCursor }
      nodes {
        id
        content { ... on Issue {
          number state createdAt closedAt
          repository { nameWithOwner }
        } }
        fieldValues(first:30) { pageInfo { hasNextPage } nodes {
          ... on ProjectV2ItemFieldDateValue {
            date field { ... on ProjectV2Field { name } }
          }
          ... on ProjectV2ItemFieldSingleSelectValue {
            name field { ... on ProjectV2SingleSelectField { name } }
          }
        } }
      }
    }
  } }
}"""

MUTATE_SET = """mutation($p:ID!,$i:ID!,$f:ID!,$d:Date!) {
  updateProjectV2ItemFieldValue(input:{
    projectId:$p,itemId:$i,fieldId:$f,value:{date:$d}
  }) { projectV2Item { id } }
}"""
MUTATE_CLEAR = """mutation($p:ID!,$i:ID!,$f:ID!) {
  clearProjectV2ItemFieldValue(input:{
    projectId:$p,itemId:$i,fieldId:$f
  }) { projectV2Item { id } }
}"""


def graphql(query: str, variables: dict) -> dict:
    output = run(["gh", "api", "graphql", "--input", "-"],
                 input_text=json.dumps({"query": query, "variables": variables}))
    value = json.loads(output.stdout)
    if value.get("errors"):
        raise TrackingError("Project GraphQL request failed: " + str(value["errors"]))
    return value["data"]


def desired_date_changes(status: str, start: str | None, completed: str | None,
                         created: str, closed: str | None,
                         today: date) -> list[tuple[str, str, str | None]]:
    """Date transitions only; existing historical dates are never fabricated."""
    out = []
    created_date = date.fromisoformat(created[:10])
    if status == "In progress" and not start and created_date >= START_CUTOFF:
        out.append(("set", "Start date", today.isoformat()))
    if status == "Done" and closed and completed != closed[:10]:
        out.append(("set", "Completed on", closed[:10]))
    elif status != "Done" and completed:
        out.append(("clear", "Completed on", None))
    return out


def load_project() -> tuple[str, dict[str, str], list[dict]]:
    cursor = None
    cursors: set[str] = set()
    nodes: list[dict] = []
    total = None
    project_id = ""
    fields = {}
    while True:
        data = graphql(QUERY, {"owner": OWNER, "number": NUMBER, "cursor": cursor})
        project = (data.get("user") or {}).get("projectV2")
        if not project:
            raise TrackingError("Missing authorized personal Project #7")
        if project["fields"]["pageInfo"]["hasNextPage"]:
            raise TrackingError("Project field list incomplete")
        project_id = project["id"]
        fields = {node["name"]: node["id"] for node in project["fields"]["nodes"] if node and "name" in node}
        page = project["items"]
        if total is None:
            total = page["totalCount"]
        if page["totalCount"] != total:
            raise TrackingError("Project item count changed during scan")
        nodes.extend(page["nodes"])
        page_info = page["pageInfo"]
        if not page_info["hasNextPage"]:
            if len(nodes) != total:
                raise TrackingError("Incomplete Project item pagination")
            break
        cursor = page_info["endCursor"]
        if not cursor or cursor in cursors:
            raise TrackingError("Repeated/invalid Project page cursor")
        cursors.add(cursor)
    return project_id, fields, nodes


def main() -> None:
    if not os.environ.get("GH_TOKEN"):
        raise TrackingError("Explicit GH_PROJECT_TOKEN / GH_TOKEN required")
    today = datetime.now(timezone.utc).date()
    project_id, fields, nodes = load_project()
    for field in ("Start date", "Completed on"):
        if field not in fields:
            raise TrackingError(f"Missing Project field: {field}")
    changes = []
    for item in nodes:
        issue = item.get("content") or {}
        if issue.get("repository", {}).get("nameWithOwner") != REPOSITORY or "number" not in issue:
            continue
        conn = item["fieldValues"]
        if conn["pageInfo"]["hasNextPage"]:
            raise TrackingError("Incomplete Project item field values")
        values = {v["field"]["name"]: v.get("date", v.get("name"))
                  for v in conn["nodes"] if v and v.get("field")}
        status = values.get("Status")
        if status not in ("Todo", "In progress", "Done"):
            continue
        if status == "Done" and not issue.get("closedAt"):
            print(f"Issue #{issue['number']}: Project Done but issue still open; no completion date written")
            continue
        changes.extend((item["id"], issue["number"], action, field, value)
                       for action, field, value in desired_date_changes(
                           status, values.get("Start date"), values.get("Completed on"),
                           issue["createdAt"], issue.get("closedAt"), today))
    if len(changes) > 100:
        raise TrackingError(f"Unexpected batch of {len(changes)} changes: review before writing")
    print(f"Project #{NUMBER}: {len(nodes)} items, {len(changes)} safe date changes")
    for item_id, number, action, field, value in changes:
        query = MUTATE_SET if action == "set" else MUTATE_CLEAR
        variables = {"p": project_id, "i": item_id, "f": fields[field]}
        if value is not None:
            variables["d"] = value
        graphql(query, variables)
        print(f"Issue #{number}: {action} {field} {value or ''}")


if __name__ == "__main__":
    try:
        main()
    except (TrackingError, KeyError, ValueError, TypeError) as exc:
        print(f"Project date synchronization stopped: {exc}", file=sys.stderr)
        raise SystemExit(1)
