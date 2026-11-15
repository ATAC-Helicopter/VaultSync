import json
import unittest
from unittest.mock import patch

from scripts import discord_release_notify as mod


class FakeResponse:
    def __init__(self, body):
        self.body = body

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def read(self, _n):
        return self.body


class DiscordReleaseTests(unittest.TestCase):
    def test_fetch_release_uses_published_release_metadata(self):
        body = json.dumps({
            "tag_name": "v1.8.10",
            "name": "VaultSync 1.8.10",
            "body": "Security and reliability updates.",
            "draft": False,
            "prerelease": False,
        }).encode()
        with patch.object(mod, "urlopen", return_value=FakeResponse(body)) as opener:
            release = mod.fetch_release("v1.8.10")
        self.assertEqual(release["url"], "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10")
        self.assertEqual(release["body"], "Security and reliability updates.")
        opener.assert_called_once()

    def test_rejects_unpublished_or_mismatched_release(self):
        for metadata in (
            {"tag_name": "v1.8.10", "draft": True},
            {"tag_name": "v1.8.9", "draft": False},
        ):
            with self.subTest(metadata=metadata), patch.object(
                    mod, "urlopen", return_value=FakeResponse(json.dumps(metadata).encode())):
                with self.assertRaisesRegex(ValueError, "Release is missing"):
                    mod.fetch_release("v1.8.10")

    def test_rejects_non_release_tags_before_request(self):
        with patch.object(mod, "urlopen") as opener:
            with self.assertRaisesRegex(ValueError, "Invalid VaultSync release tag"):
                mod.fetch_release("../../latest")
        opener.assert_not_called()

    def test_notification_disables_mentions_and_includes_release_link(self):
        payload = mod.notification({
            "tag": "v1.8.10",
            "name": "VaultSync 1.8.10",
            "body": "Release notes",
            "url": "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10",
            "prerelease": False,
        })
        self.assertEqual(payload["allowed_mentions"], {"parse": []})
        self.assertIn("v1.8.10", payload["content"])
        self.assertEqual(payload["embeds"][0]["url"],
                         "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10")


if __name__ == "__main__":
    unittest.main()
