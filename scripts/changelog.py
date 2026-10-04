#!/usr/bin/env python3
"""Validate VaultSync's Keep a Changelog contract and extract reviewable release notes."""

from __future__ import annotations

import argparse
import json
import re
from datetime import date
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
CATEGORIES = ("Added", "Changed", "Deprecated", "Removed", "Fixed", "Security")
HEADER = re.compile(r"^## \[([^]]+)\](?: - (\d{4}-\d{2}-\d{2})(?: \[YANKED\])?)?$")
TARGET = re.compile(r"^\*\*Target version:\*\* `([^`]+)`", re.MULTILINE)


def sections(text: str) -> list[tuple[str, str | None, list[str]]]:
    result: list[tuple[str, str | None, list[str]]] = []
    for line in text.splitlines():
        match = HEADER.fullmatch(line)
        if match:
            result.append((match[1], match[2], []))
        elif result:
            result[-1][2].append(line)
    return result


def active_version(text: str) -> str | None:
    """Resolve the documented target without guessing from a historical release."""
    parsed = sections(text)
    if not parsed:
        return None
    version, _, body = parsed[0]
    if version == "Unreleased":
        target = TARGET.search("\n".join(body))
        return target[1] if target else None
    return version


def validate(text: str, expected_version: str | None = None) -> list[str]:
    errors: list[str] = []
    if "https://keepachangelog.com/en/1.1.0/" not in text:
        errors.append("Missing Keep a Changelog 1.1.0 reference.")
    parsed = sections(text)
    raw_headers = [line for line in text.splitlines() if line.startswith("## ")]
    if len(raw_headers) != len(parsed):
        errors.append("Release headings must use [Unreleased] or [version] - YYYY-MM-DD.")
    if not parsed or parsed[0][0] != "Unreleased":
        errors.append("The first section must be [Unreleased].")
    if sum(version == "Unreleased" for version, _, _ in parsed) != 1:
        errors.append("Exactly one Unreleased section is required.")
    versions = [version for version, _, _ in parsed]
    if len(versions) != len(set(versions)):
        errors.append("Duplicate release sections.")
    current = active_version(text)
    if not current or expected_version is not None and current != expected_version:
        errors.append(f"Unreleased target {current!r} does not match active version {expected_version!r}.")
    links = set(re.findall(r"^\[([^]]+)\]: https?://\S+$", text, re.MULTILINE))
    previous: tuple[int, ...] | None = None
    for version, released, body in parsed:
        if version not in links:
            errors.append(f"{version}: missing version reference link.")
        if version == "Unreleased":
            if released is not None:
                errors.append("Unreleased cannot have a publication date.")
            limit_words = True
        else:
            try:
                date.fromisoformat(released or "")
            except ValueError:
                errors.append(f"{version}: invalid or missing publication date.")
            # Retain four-part historical versions; do not rename published history.
            numeric = re.fullmatch(r"(\d+(?:\.\d+)+)(?:-[0-9A-Za-z.-]+)?", version)
            if not numeric:
                errors.append(f"{version}: invalid historical version.")
                continue
            key = tuple(int(v) for v in numeric[1].split("."))
            if previous is not None and key > previous:
                errors.append(f"{version}: releases must be newest first.")
            previous = key
            limit_words = True
        seen: list[str] = []
        category: str | None = None
        entries = 0
        for line in body:
            if line.startswith("### "):
                if category is not None and not entries:
                    errors.append(f"{version}: empty category {category}.")
                category = line[4:]
                entries = 0
                if category not in CATEGORIES:
                    errors.append(f"{version}: unsupported category {category!r}.")
                seen.append(category)
            elif line.startswith("- "):
                entries += 1
                if category is None:
                    errors.append(f"{version}: entry has no change category.")
                summary = re.sub(r"^\[[^]]+\]\s*", "", line[2:])
                if limit_words and len(summary.split()) > 22:
                    errors.append(f"{version}: entry exceeds 22 words: {line}")
            elif limit_words and line.startswith("  ") and line.strip():
                errors.append(f"{version}: use one concise line per entry; move detail into documentation.")
        if category is not None and not entries:
            errors.append(f"{version}: empty category {category}.")
        if len(seen) != len(set(seen)):
            errors.append(f"{version}: duplicate change categories.")
        known = [CATEGORIES.index(c) for c in seen if c in CATEGORIES]
        if known != sorted(known):
            errors.append(f"{version}: change categories are out of order.")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "notes"))
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    parser.add_argument("--version")
    args = parser.parse_args()
    text = (args.repo_root / "CHANGELOG.md").read_text(encoding="utf-8-sig")
    metadata = json.loads((args.repo_root / "release/release-metadata.json").read_text())
    version = str(metadata["activeRelease"]["version"])
    errors = validate(text, version)
    if errors:
        print("\n".join(errors))
        return 1
    if args.command == "check":
        print(f"Keep a Changelog 1.1.0: Unreleased targets {version}; {len(sections(text))-1} historical releases validated.")
        return 0
    wanted = args.version or version
    for name, _, body in sections(text):
        if name == wanted or name == "Unreleased" and wanted == version:
            print("\n".join(body).strip())
            return 0
    print(f"No changelog section for {wanted}.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
