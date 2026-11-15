
#!/usr/bin/env python3
"""Build GitHub Wiki pages from docs/wiki without modifying the source."""

import os
import posixpath
import re
import sys
from pathlib import Path
from urllib.parse import quote, urlsplit, urlunsplit

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "docs" / "wiki"
REPO = os.environ.get("GITHUB_REPOSITORY", "ATAC-Helicopter/VaultSync")
BRANCH = "Stable"

MD_LINK = re.compile(r'(!?\[[^\]\n]*\]\()([^)\s]+)([^)]*\))')
HTML_LINK = re.compile(r'(\b(?:src|href)\s*=\s*["\'])([^"\']+)(["\'])')


def convert_url(value: str, filename: str) -> str:
    if value.startswith(("#", "//")):
        return value

    # urlsplit interprets Windows drive prefixes as URL schemes.
    if re.match(r"^[A-Za-z]:[\\/]", value):
        raise ValueError(f"{filename}: absolute or unsafe linked path: {value}")

    parsed = urlsplit(value)
    if parsed.scheme or parsed.netloc or not parsed.path:
        return value

    # GitHub Wiki links must never access arbitrary absolute filesystem paths.
    if posixpath.isabs(parsed.path) or "\\" in parsed.path:
        raise ValueError(f"{filename}: absolute or unsafe linked path: {value}")

    relative = posixpath.normpath(
        posixpath.join("docs/wiki", parsed.path)
    )
    if relative == ".." or relative.startswith("../") or posixpath.isabs(relative):
        raise ValueError(f"{filename}: path escapes repository: {value}")

    source_file = ROOT / relative
    if not source_file.is_file():
        raise ValueError(f"{filename}: missing linked file: {relative}")

    # Do not follow a checked-in symlink outside the repository.
    if not source_file.resolve(strict=True).is_relative_to(ROOT.resolve(strict=True)):
        raise ValueError(f"{filename}: linked file escapes repository: {value}")

    if relative.startswith("docs/wiki/") and relative.endswith(".md"):
        page = quote(source_file.stem)
        destination = f"https://github.com/{REPO}/wiki/{page}"
    elif relative.lower().endswith((".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp")):
        destination = (
            f"https://raw.githubusercontent.com/{REPO}/{BRANCH}/"
            f"{quote(relative, safe='/')}"
        )
    else:
        destination = (
            f"https://github.com/{REPO}/blob/{BRANCH}/"
            f"{quote(relative, safe='/')}"
        )

    return urlunsplit((
        urlsplit(destination).scheme,
        urlsplit(destination).netloc,
        urlsplit(destination).path,
        parsed.query,
        parsed.fragment,
    ))


def convert_document(contents: str, filename: str) -> str:
    result = []
    in_code_fence = False

    for line in contents.splitlines(keepends=True):
        if line.lstrip().startswith(("```", "~~~")):
            in_code_fence = not in_code_fence
            result.append(line)
            continue

        if in_code_fence:
            result.append(line)
            continue

        line = MD_LINK.sub(
            lambda match: (
                match.group(1)
                + convert_url(match.group(2), filename)
                + match.group(3)
            ),
            line,
        )

        line = HTML_LINK.sub(
            lambda match: (
                match.group(1)
                + convert_url(match.group(2), filename)
                + match.group(3)
            ),
            line,
        )

        result.append(line)

    return "".join(result)


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: build_wiki.py OUTPUT_DIRECTORY")

    output = Path(sys.argv[1])
    output.mkdir(parents=True, exist_ok=True)

    pages = sorted(SOURCE.glob("*.md"))
    if not pages:
        raise SystemExit("No wiki pages found")

    for page in pages:
        contents = page.read_text(encoding="utf-8")
        rendered = convert_document(contents, page.name)
        (output / page.name).write_text(rendered, encoding="utf-8")

    print(f"Prepared {len(pages)} GitHub Wiki pages")


if __name__ == "__main__":
    main()
