import json
import os
import unittest
from contextlib import redirect_stdout
from io import StringIO
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

    def test_fetch_release_falls_back_to_tag_and_marks_prerelease(self):
        body = json.dumps({"tag_name": "v1.8.10", "draft": False, "prerelease": True}).encode()
        with patch.object(mod, "urlopen", return_value=FakeResponse(body)):
            release = mod.fetch_release("v1.8.10")
        self.assertEqual(release["name"], "v1.8.10")
        self.assertEqual(release["body"], "")
        self.assertTrue(release["prerelease"])

    def test_rejects_oversize_release_metadata(self):
        with patch.object(mod, "urlopen", return_value=FakeResponse(b"x" * 500_001)):
            with self.assertRaisesRegex(ValueError, "larger than expected"):
                mod.fetch_release("v1.8.10")

    def test_rejects_non_release_tags_before_request(self):
        with patch.object(mod, "urlopen") as opener:
            with self.assertRaisesRegex(ValueError, "Invalid VaultSync release tag"):
                mod.fetch_release("../../latest")
        opener.assert_not_called()

    def test_notification_only_mentions_release_update_roles_and_includes_release_link(self):
        payload = mod.notification({
            "tag": "v1.8.10",
            "name": "VaultSync 1.8.10",
            "body": "Release notes",
            "url": "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10",
            "prerelease": False,
        })
        self.assertEqual(payload["allowed_mentions"], {"parse": [], "roles": list(mod.RELEASE_ROLE_IDS)})
        for role_id in mod.RELEASE_ROLE_IDS:
            self.assertIn(f"<@&{role_id}>", payload["content"])
        self.assertIn("v1.8.10", payload["content"])
        self.assertEqual(payload["embeds"][0]["url"],
                         "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10")

    def test_notification_marks_prerelease_and_uses_fallback_description(self):
        payload = mod.notification({
            "tag": "v1.8.10-rc.1", "name": "Candidate", "body": "", "url": "https://example.test",
            "prerelease": True,
        })
        self.assertIn("pre-release", payload["content"])
        self.assertEqual(payload["embeds"][0]["description"],
                         "VaultSync v1.8.10-rc.1 is now available.")

    def test_main_posts_release_and_requires_webhook(self):
        release = {"tag": "v1.8.10", "name": "VaultSync 1.8.10", "body": "Notes",
                   "url": "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10",
                   "prerelease": False}
        webhook = "https://discord.com/api/webhooks/123456789012345678/token"
        with (
            patch.dict(os.environ, {"RELEASE_TAG": "v1.8.10", "DISCORD_RELEASE_WEBHOOK": webhook}, clear=True),
            patch.object(mod, "fetch_release", return_value=release),
            patch.object(mod, "post_discord", return_value="123456789012345678") as sender,
            redirect_stdout(StringIO()) as output,
        ):
            mod.main()
        sender.assert_called_once_with(webhook, mod.notification(release))
        self.assertIn("Posted release v1.8.10", output.getvalue())

        with patch.dict(os.environ, {"RELEASE_TAG": "v1.8.10"}, clear=True):
            with self.assertRaisesRegex(ValueError, "Missing DISCORD_RELEASE_WEBHOOK"):
                mod.main()


if __name__ == "__main__":
    unittest.main()
