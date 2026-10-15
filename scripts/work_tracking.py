#!/usr/bin/env python3
"""Repository-native work tracking for VaultSync.

The script intentionally uses only Python's standard library plus the existing
`git` and GitHub CLI (`gh`) executables. It keeps GitHub Issues as the source of
truth and stores the current branch association in local git config only.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import subprocess
import sys
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence

CANONICAL_ID_RE = re.compile(r"\b(?:VS-\d{4,}|BUG-\d{5,}|ISS-\d{4,}|REL-\d{4,})\b", re.I)
ISSUE_LINK_RE = re.compile(
    r"\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?|refs?)\s+#(\d+)\b", re.I
)
RELEASE_BRANCH_RE = re.compile(r"(?:^|/)release/(\d+)\.(\d+)(?:\.\d+)?(?:[-/].*)?$", re.I)
UNRELEASED_RE = re.compile(r"^##\s+\[(\d+)\.(\d+)(?:\.\d+)?[^\]]*\]\s+-\s+Unreleased\s*$", re.M | re.I)


class TrackingError(RuntimeError):
    pass


@dataclass(frozen=True)
class CommandResult:
    stdout: str
    stderr: str
    returncode: int


def run(
    args: Sequence[str],
    *,
    cwd: Path | None = None,
    check: bool = True,
    input_text: str | None = None,
    env: dict[str, str] | None = None,
) -> CommandResult:
    proc = subprocess.run(
        list(args),
        cwd=str(cwd) if cwd else None,
        input=input_text,
        text=True,
        capture_output=True,
        env=env,
        check=False,
    )
    result = CommandResult(proc.stdout.strip(), proc.stderr.strip(), proc.returncode)
    if check and proc.returncode != 0:
        command = " ".join(shlex.quote(part) for part in args)
        detail = result.stderr or result.stdout or f"exit {proc.returncode}"
        raise TrackingError(f"Command failed: {command}\n{detail}")
    return result


def repo_root() -> Path:
    result = run(["git", "rev-parse", "--show-toplevel"])
    return Path(result.stdout).resolve()


def load_config(root: Path) -> dict[str, Any]:
    path = root / ".github" / "work-tracking.json"
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise TrackingError(f"Missing tracking config: {path}") from exc
    except json.JSONDecodeError as exc:
        raise TrackingError(f"Invalid tracking config: {path}: {exc}") from exc


def current_branch(root: Path) -> str:
    branch = run(["git", "branch", "--show-current"], cwd=root).stdout
    if not branch:
        raise TrackingError("Detached HEAD is not supported for tracked work.")
    return branch


def github_repo(root: Path) -> str:
    env_repo = os.getenv("GITHUB_REPOSITORY")
    if env_repo:
        return env_repo
    result = run(["gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], cwd=root)
    if not result.stdout or "/" not in result.stdout:
        raise TrackingError("Could not determine GitHub repository name.")
    return result.stdout


def slugify(value: str, max_length: int = 52) -> str:
    value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode("ascii")
    value = re.sub(r"[^A-Za-z0-9]+", "-", value).strip("-").lower()
    value = re.sub(r"-+", "-", value)
    return (value[:max_length].rstrip("-") or "work")


def family_code(major: int, minor: int) -> str:
    return f"{major}{minor}"


def parse_release_family(value: str) -> tuple[int, int]:
    match = re.search(r"(?<!\d)(\d+)\.(\d+)(?:\.\d+)?", value)
    if not match:
        raise TrackingError(f"Cannot parse release family from {value!r}; expected e.g. 1.9 or 1.9.5.")
    return int(match.group(1)), int(match.group(2))


def infer_release_family(root: Path, branch: str, explicit: str | None) -> tuple[int, int]:
    if explicit:
        return parse_release_family(explicit)
    match = RELEASE_BRANCH_RE.search(branch)
    if match:
        return int(match.group(1)), int(match.group(2))
    metadata = root / "release" / "release-metadata.json"
    if metadata.exists():
        try:
            active = json.loads(metadata.read_text(encoding="utf-8-sig"))["activeRelease"]["version"]
            if not isinstance(active, str):
                raise ValueError("active release must be a version string")
            return parse_release_family(active)
        except (ValueError, KeyError, TypeError) as error:
            raise TrackingError("Invalid active release metadata; fix it or pass --release explicitly.") from error
    changelog = root / "CHANGELOG.md"
    if changelog.exists():
        text = changelog.read_text(encoding="utf-8-sig")
        section = re.search(r"^##\s+\[Unreleased\]\s*$([\s\S]*?)(?=^##\s|\Z)", text, re.M | re.I)
        if section:
            target = re.search(r"\*\*Target version:\*\*\s*`(\d+\.\d+(?:\.\d+)?)`", section.group(1))
            if target:
                return parse_release_family(target.group(1))
        match = UNRELEASED_RE.search(text)
        if match:
            return int(match.group(1)), int(match.group(2))
    raise TrackingError("Cannot infer release family. Pass --release <major.minor>, for example --release 1.9.")


def extract_ids(text: str, prefix: str) -> set[int]:
    pattern = re.compile(rf"\b{re.escape(prefix)}-(\d+)\b", re.I)
    return {int(match.group(1)) for match in pattern.finditer(text or "")}


def collect_used_ids(root: Path, config: dict[str, Any], prefix: str) -> set[int]:
    used: set[int] = set()
    for relative in config.get("planning_files", []):
        path = root / relative
        if path.exists():
            used.update(extract_ids(path.read_text(encoding="utf-8-sig"), prefix))

    repository = github_repo(root)
    result = run(
        ["gh", "api", "--paginate", "--slurp", f"repos/{repository}/issues?state=all&per_page=100"],
        cwd=root,
        check=False,
    )
    if result.returncode != 0:
        raise TrackingError("Cannot audit remote work IDs; no ID was allocated. Check GitHub access and retry.")
    try:
        pages = json.loads(result.stdout)
        if not isinstance(pages, list) or any(not isinstance(page, list) for page in pages):
            raise ValueError("Expected paginated issue lists")
        for page in pages:
            for issue in page:
                if not isinstance(issue, dict):
                    raise ValueError("Invalid issue record")
                used.update(extract_ids(issue.get("title") or "", prefix))
                used.update(extract_ids(issue.get("body") or "", prefix))
    except (ValueError, TypeError) as error:
        raise TrackingError("Cannot parse the remote ID audit; no ID was allocated.") from error
    return used


def allocate_id(
    used: Iterable[int],
    *,
    prefix: str,
    major: int,
    minor: int,
    family_digits: int,
) -> str:
    family = family_code(major, minor)
    base = int(family) * (10**family_digits)
    ceiling = base + (10**family_digits) - 1
    family_used = sorted(value for value in set(used) if base <= value <= ceiling)
    candidate = (family_used[-1] + 1) if family_used else base + 1
    if candidate > ceiling:
        raise TrackingError(f"No IDs remain in {prefix}-{family} family ({base}-{ceiling}).")
    return f"{prefix}-{candidate}"


def labels_available(root: Path) -> set[str]:
    result = run(["gh", "label", "list", "--limit", "200", "--json", "name"], cwd=root, check=False)
    if result.returncode != 0 or not result.stdout:
        return set()
    try:
        return {item["name"] for item in json.loads(result.stdout) if isinstance(item, dict) and item.get("name")}
    except json.JSONDecodeError:
        return set()


def select_labels(root: Path, type_config: dict[str, Any], priority: str | None, release: tuple[int, int]) -> list[str]:
    available = labels_available(root)
    chosen: list[str] = []
    for candidate in type_config.get("labels", []):
        if candidate in available:
            chosen.append(candidate)
            break
    if priority:
        for candidate in (f"priority:{priority.upper()}", f"priority:{priority.lower()}"):
            if candidate in available:
                chosen.append(candidate)
                break
    major, minor = release
    for candidate in (f"release:{major}.{minor}.x", f"release:{major}.{minor}"):
        if candidate in available:
            chosen.append(candidate)
            break
    return chosen


def issue_body(
    *,
    work_id: str,
    work_type: str,
    title: str,
    priority: str,
    area: str | None,
    release: tuple[int, int],
    branch_mode: str,
    source_branch: str,
    extra: str | None,
) -> str:
    major, minor = release
    lines = [
        "## Tracking",
        "",
        f"- Canonical ID: `{work_id}`",
        f"- Type: `{work_type}`",
        f"- Priority: `{priority.upper()}`",
        f"- Release family: `{major}.{minor}.x`",
        f"- Branch mode: `{branch_mode}`",
        f"- Source branch: `{source_branch}`",
    ]
    if area:
        lines.append(f"- Area: `{area}`")
    lines += [
        "",
        "## Scope",
        "",
        title,
        "",
        "## Acceptance criteria",
        "",
        "- [ ] Intended behavior is implemented or the defect is reproduced and fixed.",
        "- [ ] Relevant automated/manual validation is recorded.",
        "- [ ] User-facing release notes or documentation are updated when applicable.",
    ]
    if extra:
        lines += ["", "## Context", "", extra.strip()]
    return "\n".join(lines).rstrip() + "\n"


def create_issue(root: Path, title: str, body: str, labels: list[str]) -> dict[str, Any]:
    args = ["gh", "issue", "create", "--title", title, "--body-file", "-"]
    for label in labels:
        args += ["--label", label]
    result = run(args, cwd=root, input_text=body)
    url = result.stdout.splitlines()[-1].strip()
    view = run(["gh", "issue", "view", url, "--json", "number,title,url"], cwd=root)
    return json.loads(view.stdout)


def update_issue_title(root: Path, issue_number: int, title: str) -> None:
    run(["gh", "issue", "edit", str(issue_number), "--title", title], cwd=root)


def add_to_project(root: Path, config: dict[str, Any], issue_url: str) -> None:
    number = config.get("project_number")
    owner = config.get("project_owner")
    if not number or not owner:
        return
    result = run(
        ["gh", "project", "item-add", str(number), "--owner", str(owner), "--url", issue_url],
        cwd=root,
        check=False,
    )
    if result.returncode != 0:
        print(f"warning: project auto-add skipped: {result.stderr or result.stdout}", file=sys.stderr)


def set_branch_tracking(root: Path, *, branch: str, issue_number: int, work_id: str, work_type: str, title: str) -> None:
    values = {
        "fgIssue": str(issue_number),
        "fgWorkId": work_id,
        "fgWorkType": work_type,
        "fgWorkTitle": title,
    }
    for key, value in values.items():
        run(["git", "config", f"branch.{branch}.{key}", value], cwd=root)


def branch_tracking(root: Path, branch: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for key in ("fgIssue", "fgWorkId", "fgWorkType", "fgWorkTitle"):
        value = run(["git", "config", "--get", f"branch.{branch}.{key}"], cwd=root, check=False)
        if value.returncode == 0 and value.stdout:
            result[key] = value.stdout
    return result


def maybe_create_branch(root: Path, *, mode: str, work_id: str, title: str, branch_prefix: str) -> str:
    source = current_branch(root)
    if mode == "current":
        return source
    branch = f"{branch_prefix}/{work_id}-{slugify(title)}"
    exists = run(["git", "show-ref", "--verify", "--quiet", f"refs/heads/{branch}"], cwd=root, check=False)
    if exists.returncode == 0:
        raise TrackingError(f"Branch already exists: {branch}")
    run(["git", "switch", "-c", branch], cwd=root)
    return branch


def canonical_id_from_text(*parts: str | None) -> str | None:
    for part in parts:
        if not part:
            continue
        match = CANONICAL_ID_RE.search(part)
        if match:
            return match.group(0).upper()
    return None


def issue_number_from_url(url: str) -> int:
    match = re.search(r"/issues/(\d+)(?:$|[?#])", url)
    if not match:
        raise TrackingError(f"Could not parse issue number from {url}")
    return int(match.group(1))


def type_for_id(work_id: str, requested: str | None = None) -> str:
    if requested:
        return requested
    return "bug" if work_id.upper().startswith("BUG-") else "change"


def cmd_issue(args: argparse.Namespace) -> int:
    root = repo_root()
    config = load_config(root)
    work_type = args.type.lower()
    type_config = config["types"].get(work_type)
    if not type_config:
        raise TrackingError(f"Unknown type {work_type!r}. Valid: {', '.join(config['types'])}")

    source_branch = current_branch(root)
    release = infer_release_family(root, source_branch, args.release)
    used = collect_used_ids(root, config, type_config["id_prefix"])
    work_id = allocate_id(
        used,
        prefix=type_config["id_prefix"],
        major=release[0],
        minor=release[1],
        family_digits=int(type_config["family_digits"]),
    )
    priority = args.priority.upper()
    body = issue_body(
        work_id=work_id,
        work_type=work_type,
        title=args.title,
        priority=priority,
        area=args.area,
        release=release,
        branch_mode=args.branch,
        source_branch=source_branch,
        extra=args.context,
    )
    labels = select_labels(root, type_config, priority, release)
    issue = create_issue(root, f"{work_id}: {args.title}", body, labels)
    add_to_project(root, config, issue["url"])
    branch = maybe_create_branch(
        root,
        mode=args.branch,
        work_id=work_id,
        title=args.title,
        branch_prefix=type_config["branch_prefix"],
    )
    set_branch_tracking(
        root,
        branch=branch,
        issue_number=int(issue["number"]),
        work_id=work_id,
        work_type=work_type,
        title=args.title,
    )
    print(f"Created {work_id} -> #{issue['number']}")
    print(issue["url"])
    print(f"Branch: {branch} ({'kept current' if args.branch == 'current' else 'created'})")
    return 0


def cmd_adopt(args: argparse.Namespace) -> int:
    root = repo_root()
    config = load_config(root)
    raw = run(["gh", "issue", "view", str(args.issue), "--json", "number,title,body,url"], cwd=root)
    issue = json.loads(raw.stdout)
    existing = canonical_id_from_text(issue.get("title"), issue.get("body"))
    if existing is None and args.type is None:
        raise TrackingError("Existing issue has no canonical ID; pass --type so an ID can be allocated.")
    work_type = args.type.lower() if args.type else type_for_id(existing or "", None)
    type_config = config["types"].get(work_type)
    if not type_config:
        raise TrackingError(f"Unknown type {work_type!r}.")

    source_branch = current_branch(root)
    release = infer_release_family(root, source_branch, args.release)
    work_id = existing
    title = issue["title"]
    if work_id is None:
        used = collect_used_ids(root, config, type_config["id_prefix"])
        work_id = allocate_id(
            used,
            prefix=type_config["id_prefix"],
            major=release[0],
            minor=release[1],
            family_digits=int(type_config["family_digits"]),
        )
        clean_title = re.sub(r"^\[[^\]]+\]:\s*", "", title).strip()
        update_issue_title(root, int(issue["number"]), f"{work_id}: {clean_title}")
        title = clean_title
    else:
        title = re.sub(rf"^\s*{re.escape(work_id)}\s*:\s*", "", title, flags=re.I).strip()

    labels = select_labels(root, type_config, args.priority, release)
    for label in labels:
        run(["gh", "issue", "edit", str(issue["number"]), "--add-label", label], cwd=root, check=False)
    add_to_project(root, config, issue["url"])
    branch = maybe_create_branch(
        root,
        mode=args.branch,
        work_id=work_id,
        title=title,
        branch_prefix=type_config["branch_prefix"],
    )
    set_branch_tracking(
        root,
        branch=branch,
        issue_number=int(issue["number"]),
        work_id=work_id,
        work_type=work_type,
        title=title,
    )
    print(f"Adopted #{issue['number']} as {work_id}")
    print(issue["url"])
    print(f"Branch: {branch} ({'kept current' if args.branch == 'current' else 'created'})")
    return 0


def cmd_status(_: argparse.Namespace) -> int:
    root = repo_root()
    branch = current_branch(root)
    tracking = branch_tracking(root, branch)
    print(f"Branch: {branch}")
    if not tracking:
        print("Tracking: none")
        return 1
    print(f"ID: {tracking.get('fgWorkId', '?')}")
    print(f"Issue: #{tracking.get('fgIssue', '?')}")
    print(f"Type: {tracking.get('fgWorkType', '?')}")
    print(f"Title: {tracking.get('fgWorkTitle', '?')}")
    issue_number = tracking.get("fgIssue")
    if issue_number:
        view = run(["gh", "issue", "view", issue_number, "--json", "state,url", "--jq", '.state + " " + .url'], cwd=root, check=False)
        if view.returncode == 0 and view.stdout:
            print(f"GitHub: {view.stdout}")
    return 0


def cmd_pr(args: argparse.Namespace) -> int:
    root = repo_root()
    config = load_config(root)
    branch = current_branch(root)
    tracking = branch_tracking(root, branch)
    missing = [key for key in ("fgIssue", "fgWorkId", "fgWorkTitle", "fgWorkType") if key not in tracking]
    if missing:
        raise TrackingError(f"Current branch is not tracked ({', '.join(missing)} missing). Use `./dev issue` or `./dev adopt` first.")

    issue_number = tracking["fgIssue"]
    work_id = tracking["fgWorkId"]
    title = tracking["fgWorkTitle"]
    work_type = tracking["fgWorkType"]
    close_word = "Refs" if args.keep_open else "Closes"
    body = "\n".join(
        [
            "## Summary",
            "",
            f"Implements {work_id}: {title}",
            "",
            "## Tracking",
            "",
            f"- Canonical ID: `{work_id}`",
            f"- Type: `{work_type}`",
            f"- {close_word} #{issue_number}",
            "",
            "## Validation",
            "",
            "- [ ] Relevant automated tests pass.",
            "- [ ] Manual validation completed where applicable.",
            "- [ ] Release/docs impact reviewed.",
        ]
    )
    base = args.base or config.get("default_pr_base") or "Dev"
    cmd = ["gh", "pr", "create", "--base", base, "--title", f"[{work_id}] {title}", "--body-file", "-"]
    if args.draft:
        cmd.append("--draft")
    result = run(cmd, cwd=root, input_text=body)
    url = result.stdout.splitlines()[-1].strip()

    type_config = config.get("types", {}).get(work_type, {})
    available = labels_available(root)
    for label in type_config.get("labels", []):
        if label in available:
            run(["gh", "pr", "edit", url, "--add-label", label], cwd=root, check=False)
            break
    print(url)
    return 0


def validate_pr_payload(payload: dict[str, Any]) -> list[str]:
    pr = payload.get("pull_request") or {}
    title = pr.get("title") or ""
    body = pr.get("body") or ""
    author = ((pr.get("user") or {}).get("login") or "").lower()
    combined = f"{title}\n{body}"

    if author in {"dependabot[bot]", "github-actions[bot]"}:
        return []
    if re.search(r"(?im)^Tracking:\s*bootstrap\s*$", body):
        return []
    if re.search(r"(?im)^Tracking:\s*skip\s*[-:]\s*\S.+$", body):
        return []

    errors: list[str] = []
    if not CANONICAL_ID_RE.search(combined):
        errors.append("PR must reference a canonical work ID (VS-xxxx, BUG-xxxxx, ISS-xxxx, or REL-xxxx).")
    if not ISSUE_LINK_RE.search(body):
        errors.append("PR body must link an issue with `Closes #N`, `Fixes #N`, `Resolves #N`, or `Refs #N`.")
    return errors


def audit_push_payload(payload: dict[str, Any]) -> list[str]:
    warnings: list[str] = []
    for commit in payload.get("commits") or []:
        message = commit.get("message") or ""
        first = message.splitlines()[0] if message else "(no message)"
        if CANONICAL_ID_RE.search(message):
            continue
        if re.match(r"(?i)^(merge|revert)\b", first):
            continue
        warnings.append(f"Untracked commit {str(commit.get('id') or '')[:8]}: {first}")
    return warnings


def cmd_check_event(args: argparse.Namespace) -> int:
    path = Path(args.event or os.getenv("GITHUB_EVENT_PATH") or "")
    if not path.is_file():
        raise TrackingError("GitHub event JSON not found.")
    payload = json.loads(path.read_text(encoding="utf-8"))
    event = args.event_name or os.getenv("GITHUB_EVENT_NAME") or ""

    if event == "pull_request":
        errors = validate_pr_payload(payload)
        if errors:
            for error in errors:
                print(f"::error::{error}")
            return 1
        print("Tracking check passed.")
        return 0
    if event == "push":
        warnings = audit_push_payload(payload)
        for warning in warnings:
            print(f"::warning::{warning}")
        print(f"Push tracking audit complete: {len(warnings)} warning(s).")
        return 0
    print(f"No tracking policy for event {event!r}; nothing to do.")
    return 0


def cmd_notes(args: argparse.Namespace) -> int:
    root = repo_root()
    repo = github_repo(root)
    api_args = [
        "gh",
        "api",
        "--method",
        "POST",
        f"repos/{repo}/releases/generate-notes",
        "-f",
        f"tag_name={args.tag}",
        "-f",
        f"target_commitish={args.target}",
    ]
    if args.previous_tag:
        api_args += ["-f", f"previous_tag_name={args.previous_tag}"]
    result = run(api_args, cwd=root)
    payload = json.loads(result.stdout)
    body = payload.get("body") or ""
    if args.output:
        output = Path(args.output)
        if not output.is_absolute():
            output = root / output
        output.write_text(body.rstrip() + "\n", encoding="utf-8")
        print(output)
    else:
        print(body)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="work_tracking.py", description="VaultSync work tracking helper")
    sub = parser.add_subparsers(dest="command", required=True)

    issue = sub.add_parser("issue", help="Create a tracked issue and optionally a dedicated branch")
    issue.add_argument("type", choices=["bug", "feature", "change", "security", "maintenance"])
    issue.add_argument("title")
    issue.add_argument("--branch", choices=["current", "new"], default="current")
    issue.add_argument("--release", help="Release family, e.g. 1.9. Inferred from release branch or first Unreleased changelog entry.")
    issue.add_argument("--priority", default="P2")
    issue.add_argument("--area")
    issue.add_argument("--context")
    issue.set_defaults(func=cmd_issue)

    adopt = sub.add_parser("adopt", help="Adopt an existing GitHub issue into tracked development")
    adopt.add_argument("issue", type=int)
    adopt.add_argument("--type", choices=["bug", "feature", "change", "security", "maintenance"])
    adopt.add_argument("--branch", choices=["current", "new"], default="current")
    adopt.add_argument("--release")
    adopt.add_argument("--priority", default="P2")
    adopt.set_defaults(func=cmd_adopt)

    status = sub.add_parser("status", help="Show tracking associated with the active branch")
    status.set_defaults(func=cmd_status)

    pr = sub.add_parser("pr", help="Create a PR linked to the tracked issue")
    pr.add_argument("--base")
    pr.add_argument("--draft", action="store_true")
    pr.add_argument("--keep-open", action="store_true", help="Use Refs instead of Closes")
    pr.set_defaults(func=cmd_pr)

    check = sub.add_parser("check-event", help="Validate a GitHub Actions PR/push event")
    check.add_argument("--event")
    check.add_argument("--event-name")
    check.set_defaults(func=cmd_check_event)

    notes = sub.add_parser("notes", help="Generate GitHub-native release notes using .github/release.yml")
    notes.add_argument("--tag", required=True)
    notes.add_argument("--previous-tag")
    notes.add_argument("--target", default="Dev")
    notes.add_argument("--output")
    notes.set_defaults(func=cmd_notes)

    return parser


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        return int(args.func(args))
    except TrackingError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
