#!/usr/bin/env python3
"""Compare shipped localization files against the English baseline."""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path
import xml.etree.ElementTree as ET


PLACEHOLDER_PATTERN = re.compile(r"\{\d+(?::[^}]*)?\}")
FORMAT_ONLY_PATTERN = re.compile(r"^[\s\d{}:Nn.,/|*#()<>+\-=→·]+$")
INTENTIONAL_VALUES = {
    "Fjord",
    "VaultSync",
    "VaultSync Midnight",
    "VS",
    "x",
}


def read_locale(path: Path) -> tuple[dict[str, str], list[str]]:
    with path.open(encoding="utf-8-sig") as stream:
        pairs = json.load(stream, object_pairs_hook=tuple)

    if not isinstance(pairs, tuple):
        raise ValueError(f"{path} must contain a JSON object.")

    keys = [key for key, _ in pairs]
    duplicate_keys = sorted(
        key for key, count in Counter(keys).items() if count > 1
    )
    return dict(pairs), duplicate_keys


def placeholders(value: str) -> list[str]:
    return sorted(PLACEHOLDER_PATTERN.findall(value))


def is_intentional_exact_match(value: str) -> bool:
    if value in INTENTIONAL_VALUES:
        return True

    without_placeholders = PLACEHOLDER_PATTERN.sub("", value)
    normalized_format = without_placeholders.strip().casefold().removeprefix(":").strip()
    if normalized_format == "ms":
        return True

    return not without_placeholders.strip() or bool(
        FORMAT_ONLY_PATTERN.fullmatch(without_placeholders)
    )


UI_PROPERTIES = {"Text", "Content", "Title", "Header", "Watermark", "PlaceholderText",
                 "ToolTip.Tip", "AutomationProperties.Name", "AutomationProperties.HelpText"}
# Format tokens, brand names and file-format badges are deliberately invariant.
UI_INVARIANTS = {"HH:mm", "HH:mm:ss", "VaultSync", "TXT"}
REFERENCE_PATTERN = re.compile(r'\b(?:L|LF|Lf|LStatic|GetString)\(\s*"([A-Za-z][\w.-]*)"')
MARKUP_REFERENCE_PATTERN = re.compile(r'LocalizedString\s+Key=([\w.-]+)')
VISIBLE_ASSIGNMENT_PATTERN = re.compile(
    r'\b(?:Text|Content|Title|Header|Watermark|PlaceholderText|StatusText|BusyMessage|'
    r'_headline|_detail|_progressSummary)\s*=\s*\$?"((?:[^"\\]|\\.)*)"')


def source_audit(source_dir: Path, english: dict[str, str]) -> dict[str, object]:
    if not source_dir.is_dir():
        raise ValueError(f"Missing UI source directory: {source_dir}")
    missing = []
    hardcoded = []
    supported = set()
    for path in sorted(source_dir.rglob("*")):
        if path.suffix not in {".cs", ".axaml"} or {"obj", "bin"}.intersection(path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        matches = REFERENCE_PATTERN.finditer(text) if path.suffix == ".cs" else MARKUP_REFERENCE_PATTERN.finditer(text)
        for match in matches:
            if match[1] not in english:
                missing.append({"file": str(path), "line": text.count("\n", 0, match.start()) + 1, "key": match[1]})
        if path.name == "LocalizationService.cs":
            supported.update(re.findall(r'Code\s*=\s*"([\w-]+)"', text))
        if path.suffix == ".axaml":
            root = ET.fromstring(text)
            for element in root.iter():
                if element.tag.rsplit("}", 1)[-1] == "Setter" and element.attrib.get("Property") in UI_PROPERTIES:
                    value = element.attrib.get("Value", "")
                    if not value.startswith("{") and value not in UI_INVARIANTS and any(c.isalpha() for c in value):
                        hardcoded.append({"file": str(path), "property": element.attrib["Property"], "value": value})
                for prop, value in element.attrib.items():
                    if prop in UI_PROPERTIES and not value.startswith("{") and value not in UI_INVARIANTS and any(c.isalpha() for c in value):
                        hardcoded.append({"file": str(path), "property": prop, "value": value})
        else:
            for match in VISIBLE_ASSIGNMENT_PATTERN.finditer(text):
                value = match[1]
                visible = re.sub(r"\{[^}]*\}", "", value)
                if value not in UI_INVARIANTS and any(c.isalpha() for c in visible):
                    hardcoded.append({"file": str(path), "line": text.count("\n", 0, match.start()) + 1, "value": value})
    return {"missing_references": missing, "hardcoded_ui_text": hardcoded,
            "supported_locales": sorted(supported)}


def validation_errors(report: dict[str, object]) -> list[str]:
    errors = []
    for field in ("baseline_duplicate_keys", "baseline_blank_or_non_string_values"):
        if report.get(field):
            errors.append(f"English baseline: {field}: {report[field]}")
    for locale, result in report["locales"].items():
        for field in ("missing_keys", "extra_keys", "duplicate_keys", "blank_or_non_string_values", "placeholder_mismatches"):
            if result[field]:
                errors.append(f"{locale}: {field}: {len(result[field])}")
    source = report.get("source", {})
    for field in ("missing_references", "hardcoded_ui_text", "missing_locale_files"):
        if source.get(field):
            errors.append(f"UI: {field}: {source[field]}")
    return errors


def audit(localization_dir: Path, source_dir: Path | None = None) -> dict[str, object]:
    english_path = localization_dir / "strings.en.json"
    english, english_duplicate_keys = read_locale(english_path)
    locales: dict[str, object] = {}

    for locale_path in sorted(localization_dir.glob("strings.*.json")):
        if locale_path == english_path:
            continue

        locale = locale_path.stem.split(".")[-1]
        translated, duplicate_keys = read_locale(locale_path)
        shared_keys = english.keys() & translated.keys()

        exact_matches = [
            {
                "key": key,
                "english": english[key],
            }
            for key in english
            if key in translated
            and isinstance(english[key], str)
            and translated[key] == english[key]
            and english[key].strip()
            and not is_intentional_exact_match(english[key])
        ]
        intentional_matches = [
            {
                "key": key,
                "english": english[key],
            }
            for key in english
            if key in translated
            and isinstance(english[key], str)
            and translated[key] == english[key]
            and english[key].strip()
            and is_intentional_exact_match(english[key])
        ]
        placeholder_mismatches = [
            {
                "key": key,
                "english": english[key],
                "translation": translated[key],
                "english_placeholders": placeholders(english[key]),
                "translation_placeholders": placeholders(translated[key]),
            }
            for key in sorted(shared_keys)
            if isinstance(english[key], str)
            and isinstance(translated[key], str)
            and placeholders(english[key]) != placeholders(translated[key])
        ]

        locales[locale] = {
            "file": str(locale_path),
            "duplicate_keys": duplicate_keys,
            "blank_or_non_string_values": sorted(
                key
                for key, value in translated.items()
                if not isinstance(value, str) or not value.strip()
            ),
            "missing_keys": sorted(english.keys() - translated.keys()),
            "extra_keys": sorted(translated.keys() - english.keys()),
            "exact_english_candidates": exact_matches,
            "intentional_exact_matches": intentional_matches,
            "placeholder_mismatches": placeholder_mismatches,
        }

    report = {
        "baseline": str(english_path),
        "baseline_key_count": len(english),
        "baseline_duplicate_keys": english_duplicate_keys,
        "baseline_blank_or_non_string_values": sorted(key for key, value in english.items() if not isinstance(value, str) or not value.strip()),
        "locales": locales,
    }
    if source_dir is not None:
        source = source_audit(source_dir, english)
        source["missing_locale_files"] = sorted(set(source["supported_locales"]) - ({"en"} | set(locales)))
        report["source"] = source
    return report


def print_markdown(report: dict[str, object]) -> None:
    print("# Localization audit")
    print()
    print(f"- Baseline: `{report['baseline']}`")
    print(f"- Baseline keys: {report['baseline_key_count']}")
    print("- Exact-English entries are translation candidates, not automatic proof of an error.")
    print("- This audit cannot prove that a non-English value uses the intended language.")
    print("- Product names, technical terms, and identical words may be valid exact matches.")
    print()

    locales = report["locales"]
    assert isinstance(locales, dict)

    print(
        "| Locale | Missing | Extra | Duplicate | Blank | "
        "Exact English candidates | Placeholder mismatches |"
    )
    print("| --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    for locale, result in locales.items():
        assert isinstance(result, dict)
        print(
            f"| `{locale}` | {len(result['missing_keys'])} | "
            f"{len(result['extra_keys'])} | "
            f"{len(result['duplicate_keys'])} | "
            f"{len(result['blank_or_non_string_values'])} | "
            f"{len(result['exact_english_candidates'])} | "
            f"{len(result['placeholder_mismatches'])} |"
        )

    for locale, result in locales.items():
        assert isinstance(result, dict)
        print()
        print(f"## {locale}")
        print()
        print("### Missing keys")
        print()
        if result["missing_keys"]:
            for key in result["missing_keys"]:
                print(f"- `{key}`")
        else:
            print("- None")

        print()
        print("### Exact-English translation candidates")
        print()
        for item in result["exact_english_candidates"]:
            print(f"- `{item['key']}` — {item['english']}")

        print()
        print("### Placeholder mismatches")
        print()
        if not result["placeholder_mismatches"]:
            print("- None")
        for item in result["placeholder_mismatches"]:
            print(f"- `{item['key']}`")
            print(f"  - English: {item['english']}")
            print(f"  - Translation: {item['translation']}")
            print(
                "  - Placeholders: "
                f"`{item['english_placeholders']}` → "
                f"`{item['translation_placeholders']}`"
            )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--localization-dir",
        type=Path,
        default=Path("Localization"),
    )
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--check", action="store_true", help="Fail on structural errors, missing source keys and fixed UI text")
    parser.add_argument("--source-dir", type=Path, default=Path("src/VaultSync.UI"))
    args = parser.parse_args()

    report = audit(args.localization_dir, args.source_dir)
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    elif args.check:
        print(f"Localization: {report['baseline_key_count']} keys, {len(report['locales']) + 1} locale files checked")
    else:
        print_markdown(report)
    errors = validation_errors(report)
    if args.check:
        for error in errors:
            print(error, file=sys.stderr)
        return int(bool(errors))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
