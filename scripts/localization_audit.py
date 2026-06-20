#!/usr/bin/env python3
"""Compare shipped localization files against the English baseline."""

from __future__ import annotations

import argparse
import json
import re
from collections import Counter
from pathlib import Path


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
        pairs = json.load(stream, object_pairs_hook=list)

    if not isinstance(pairs, list):
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


def audit(localization_dir: Path) -> dict[str, object]:
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

    return {
        "baseline": str(english_path),
        "baseline_key_count": len(english),
        "baseline_duplicate_keys": english_duplicate_keys,
        "locales": locales,
    }


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
    args = parser.parse_args()

    report = audit(args.localization_dir)
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print_markdown(report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
