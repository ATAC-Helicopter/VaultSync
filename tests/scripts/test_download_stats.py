import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "scripts" / "download_stats.py"

spec = importlib.util.spec_from_file_location("download_stats", MODULE_PATH)
download_stats = importlib.util.module_from_spec(spec)
assert spec is not None and spec.loader is not None
spec.loader.exec_module(download_stats)


class DownloadStatsTests(unittest.TestCase):
    def test_normalize_releases_computes_deltas_and_highlights(self) -> None:
        previous = {
            "totals": {
                "all_assets_downloads": 14,
            },
            "releases": [
                {
                    "tag_name": "v1.7.0",
                    "total_downloads": 10,
                    "assets": [
                        {"name": "vaultsync-win.zip", "download_count": 10},
                    ],
                },
                {
                    "tag_name": "v1.8.0-beta.1",
                    "total_downloads": 4,
                    "assets": [
                        {"name": "vaultsync-beta.zip", "download_count": 4},
                    ],
                },
            ],
        }

        raw_releases = [
            {
                "id": 200,
                "tag_name": "v1.8.0-beta.2",
                "name": "1.8.0 Beta 2",
                "draft": False,
                "prerelease": True,
                "created_at": "2026-03-29T10:00:00Z",
                "published_at": "2026-03-29T10:00:00Z",
                "html_url": "https://example.test/beta",
                "assets": [
                    {
                        "id": 21,
                        "name": "vaultsync-beta.zip",
                        "size": 200,
                        "content_type": "application/zip",
                        "download_count": 9,
                        "created_at": "2026-03-29T10:00:00Z",
                        "updated_at": "2026-03-29T10:00:00Z",
                        "browser_download_url": "https://example.test/beta.zip",
                    }
                ],
            },
            {
                "id": 100,
                "tag_name": "v1.7.0",
                "name": "1.7.0",
                "draft": False,
                "prerelease": False,
                "created_at": "2026-03-20T10:00:00Z",
                "published_at": "2026-03-20T10:00:00Z",
                "html_url": "https://example.test/stable",
                "assets": [
                    {
                        "id": 11,
                        "name": "vaultsync-win.zip",
                        "size": 100,
                        "content_type": "application/zip",
                        "download_count": 15,
                        "created_at": "2026-03-20T10:00:00Z",
                        "updated_at": "2026-03-20T10:00:00Z",
                        "browser_download_url": "https://example.test/stable.zip",
                    }
                ],
            },
        ]

        snapshot = download_stats.normalize_releases(raw_releases, previous)

        self.assertEqual(snapshot["totals"]["all_assets_downloads"], 24)
        self.assertEqual(snapshot["totals"]["all_assets_delta"], 10)
        self.assertEqual(snapshot["totals"]["release_count"], 2)
        self.assertEqual(snapshot["totals"]["asset_count"], 2)

        stable = snapshot["highlights"]["latest_stable"]
        prerelease = snapshot["highlights"]["latest_prerelease"]
        self.assertEqual(stable["tag_name"], "v1.7.0")
        self.assertEqual(stable["downloads_delta"], 5)
        self.assertEqual(prerelease["tag_name"], "v1.8.0-beta.2")
        self.assertEqual(prerelease["downloads_delta"], 9)

        top_assets = snapshot["highlights"]["top_assets"]
        self.assertEqual(top_assets[0]["name"], "vaultsync-win.zip")
        self.assertEqual(top_assets[0]["downloads_delta"], 5)

    def test_build_markdown_and_html_include_expected_sections(self) -> None:
        snapshot = {
            "repository": "ATAC-Helicopter/VaultSync",
            "captured_at": "2026-03-30T10:00:00Z",
            "totals": {
                "release_count": 1,
                "asset_count": 1,
                "all_assets_downloads": 15,
                "all_assets_delta": 5,
            },
            "highlights": {
                "latest_stable": {
                    "tag_name": "v1.7.0",
                    "name": "1.7.0",
                    "total_downloads": 15,
                    "downloads_delta": 5,
                    "html_url": "https://example.test/stable",
                },
                "latest_prerelease": None,
                "top_assets": [
                    {
                        "name": "vaultsync-setup.exe",
                        "release_tag": "v1.7.0",
                        "download_count": 15,
                        "downloads_delta": 5,
                    }
                ],
            },
            "releases": [
                {
                    "tag_name": "v1.7.0",
                    "name": "1.7.0",
                    "draft": False,
                    "prerelease": False,
                    "published_at": "2026-03-20T10:00:00Z",
                    "html_url": "https://example.test/stable",
                    "total_downloads": 15,
                    "downloads_delta": 5,
                    "assets": [
                        {
                            "name": "vaultsync-setup.exe",
                            "download_count": 15,
                            "downloads_delta": 5,
                            "size": 123456,
                            "browser_download_url": "https://example.test/stable.exe",
                        }
                    ],
                }
            ],
        }

        markdown = download_stats.build_markdown(snapshot)
        html = download_stats.build_html(snapshot)

        self.assertIn("# Download stats for ATAC-Helicopter/VaultSync", markdown)
        self.assertIn("## Top assets", markdown)
        self.assertIn("| vaultsync-setup.exe | `v1.7.0` | 15 | +5 |", markdown)

        self.assertIn("VaultSync download stats", html)
        self.assertIn("Latest stable", html)
        self.assertIn("vaultsync-setup.exe", html)
        self.assertIn("+5 since previous snapshot", html)

    def test_ensure_history_index_orders_newest_first(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            history_dir = Path(tmp)
            (history_dir / "2026-03-28T10-00-00Z.json").write_text("{}", encoding="utf-8")
            (history_dir / "2026-03-30T10-00-00Z.json").write_text("{}", encoding="utf-8")
            (history_dir / "2026-03-29T10-00-00Z.json").write_text("{}", encoding="utf-8")

            download_stats.ensure_history_index(history_dir)

            index_html = (history_dir / "index.html").read_text(encoding="utf-8")
            first = index_html.index("2026-03-30T10-00-00Z.json")
            second = index_html.index("2026-03-29T10-00-00Z.json")
            third = index_html.index("2026-03-28T10-00-00Z.json")
            self.assertLess(first, second)
            self.assertLess(second, third)

    def test_read_previous_snapshot_handles_invalid_json(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "latest.json"
            path.write_text("{not-json", encoding="utf-8")
            self.assertIsNone(download_stats.read_previous_snapshot(path))

            payload = {"totals": {"all_assets_downloads": 1}}
            path.write_text(json.dumps(payload), encoding="utf-8")
            self.assertEqual(download_stats.read_previous_snapshot(path), payload)

    def test_prune_history_keeps_recent_and_monthly_checkpoints(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            history_dir = Path(tmp)
            names = [
                "2026-03-30T10-00-00Z.json",
                "2026-03-29T10-00-00Z.json",
                "2026-03-28T10-00-00Z.json",
                "2026-02-20T10-00-00Z.json",
                "2026-02-10T10-00-00Z.json",
                "2026-01-15T10-00-00Z.json",
                "2026-01-05T10-00-00Z.json",
            ]
            for name in names:
                (history_dir / name).write_text("{}", encoding="utf-8")

            removed = download_stats.prune_history(history_dir, keep_recent=2)

            self.assertEqual(
                removed,
                [
                    "2026-02-10T10-00-00Z.json",
                    "2026-01-05T10-00-00Z.json",
                ],
            )
            remaining = sorted(path.name for path in history_dir.glob("*.json"))
            self.assertEqual(
                remaining,
                [
                    "2026-01-15T10-00-00Z.json",
                    "2026-02-20T10-00-00Z.json",
                    "2026-03-28T10-00-00Z.json",
                    "2026-03-29T10-00-00Z.json",
                    "2026-03-30T10-00-00Z.json",
                ],
            )


if __name__ == "__main__":
    unittest.main()
