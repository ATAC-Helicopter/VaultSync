"""Regression tests for safe GitHub Wiki link publication."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "build_wiki.py"

spec = importlib.util.spec_from_file_location("build_wiki", MODULE_PATH)
assert spec is not None and spec.loader is not None
build_wiki = importlib.util.module_from_spec(spec)
spec.loader.exec_module(build_wiki)


class WikiPublishingTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "repository"
        self.source = self.root / "docs" / "wiki"
        self.source.mkdir(parents=True)
        (self.root / "docs" / "images").mkdir()
        (self.source / "Home.md").write_text("# Home\n", encoding="utf-8")
        (self.root / "DOCUMENTATION.md").write_text("# Docs\n", encoding="utf-8")
        (self.root / "docs" / "images" / "Dashboard.png").write_bytes(b"test")
        patch_root = patch.object(build_wiki, "ROOT", self.root)
        patch_source = patch.object(build_wiki, "SOURCE", self.source)
        patch_root.start()
        patch_source.start()
        self.addCleanup(patch_root.stop)
        self.addCleanup(patch_source.stop)

    def test_wiki_link_preserves_fragment_and_query(self) -> None:
        result = build_wiki.convert_url("Home.md?mode=read#install", "Home.md")
        self.assertEqual(
            "https://github.com/ATAC-Helicopter/VaultSync/wiki/Home?mode=read#install",
            result,
        )

    def test_images_use_raw_stable_files(self) -> None:
        result = build_wiki.convert_url("../images/Dashboard.png", "Home.md")
        self.assertEqual(
            "https://raw.githubusercontent.com/ATAC-Helicopter/VaultSync/Stable/"
            "docs/images/Dashboard.png",
            result,
        )

    def test_repository_document_uses_stable_blob_link(self) -> None:
        result = build_wiki.convert_url("../../DOCUMENTATION.md#intro", "Home.md")
        self.assertEqual(
            "https://github.com/ATAC-Helicopter/VaultSync/blob/Stable/"
            "DOCUMENTATION.md#intro",
            result,
        )

    def test_external_and_local_anchor_links_unchanged(self) -> None:
        for value in ("", "#here", "https://example.org/file", "mailto:test@example.org",
                      "//example.org/page", "?view=1"):
            with self.subTest(value=value):
                self.assertEqual(value, build_wiki.convert_url(value, "Home.md"))

    def test_reject_absolute_and_backslash_paths(self) -> None:
        for value in ("/etc/passwd", r"..\..\etc\passwd", r"C:\windows\system.ini"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ValueError, "unsafe|escapes"):
                    build_wiki.convert_url(value, "Home.md")

    def test_reject_relative_escape_above_repository(self) -> None:
        with self.assertRaisesRegex(ValueError, "escapes repository"):
            build_wiki.convert_url("../../../../etc/passwd", "Home.md")

    def test_reject_missing_repository_file(self) -> None:
        with self.assertRaisesRegex(ValueError, "missing linked file"):
            build_wiki.convert_url("Missing.md", "Home.md")

    def test_reject_symlink_outside_repository(self) -> None:
        outside = Path(self.temp.name) / "outside.txt"
        outside.write_text("private fixture", encoding="utf-8")
        (self.source / "outside.md").symlink_to(outside)
        with self.assertRaisesRegex(ValueError, "escapes repository"):
            build_wiki.convert_url("outside.md", "Home.md")

    def test_document_converts_markdown_and_html_not_fenced_code(self) -> None:
        content = (
            "[Home](Home.md)\n"
            "~~~markdown\n[unresolved](Missing.md)\n~~~\n"
            "<img src=\"../images/Dashboard.png\">\n"
            "~~~html\n<a href=\"Missing.md\">unchanged</a>\n~~~\n"
            "[Docs](../../DOCUMENTATION.md)\n"
        )
        result = build_wiki.convert_document(content, "Home.md")
        self.assertIn("/wiki/Home)", result)
        self.assertIn("[unresolved](Missing.md)", result)
        self.assertIn("src=\"https://raw.githubusercontent.com/", result)
        self.assertIn("<a href=\"Missing.md\">unchanged</a>", result)
        self.assertIn("/blob/Stable/DOCUMENTATION.md)", result)

    def test_document_propagates_missing_link_failure(self) -> None:
        with self.assertRaisesRegex(ValueError, "missing linked file"):
            build_wiki.convert_document("[bad](NoPage.md)\n", "Home.md")

    def test_main_writes_published_pages_without_touching_sources(self) -> None:
        (self.source / "FAQ.md").write_text(
            "[Home](Home.md)\n", encoding="utf-8"
        )
        output = Path(self.temp.name) / "publish"
        capture = StringIO()
        with patch.object(sys, "argv", ["build_wiki.py", str(output)]):
            with redirect_stdout(capture):
                build_wiki.main()
        self.assertIn("Prepared 2 GitHub Wiki pages", capture.getvalue())
        self.assertIn("/wiki/Home", (output / "FAQ.md").read_text(encoding="utf-8"))
        self.assertEqual(
            "[Home](Home.md)\n", (self.source / "FAQ.md").read_text(encoding="utf-8")
        )

    def test_main_rejects_missing_source_pages(self) -> None:
        (self.source / "Home.md").unlink()
        with patch.object(sys, "argv", ["build_wiki.py", str(Path(self.temp.name) / "out")]):
            with self.assertRaisesRegex(SystemExit, "No wiki pages found"):
                build_wiki.main()

    def test_main_requires_output_argument(self) -> None:
        with patch.object(sys, "argv", ["build_wiki.py"]):
            with self.assertRaisesRegex(SystemExit, "Usage: build_wiki.py"):
                build_wiki.main()


if __name__ == "__main__":
    unittest.main()
