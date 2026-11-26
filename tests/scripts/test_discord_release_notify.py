import json
import os
import subprocess
import sys
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
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

    def test_rich_notes_become_short_scannable_sections(self):
        body = (
            "Security, reliability and dependency updates for everyday backups.\n\n"
            "## Highlights\n"
            "### Safer backup destinations\n"
            "Destination path boundaries now reject unsafe links and aliases. More details follow.\n\n"
            "### Improved exclusions\n"
            "Ignore rules refresh even when timestamps remain unchanged.\n\n"
            "## Update qualification\n"
            "The **1.8.9 → 1.8.10** upgrade was tested.\n\n"
            "- Windows x64\n- Linux ARM64\n- macOS Apple Silicon\n"
            "## Next steps\n" + ("Very long information. " * 400)
        )
        release = {
            "tag": "v1.8.10",
            "name": "VaultSync 1.8.10 — Security & Reliability",
            "body": body,
            "url": "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10",
            "prerelease": False,
        }
        payload = mod.notification(release)
        embed = payload["embeds"][0]
        self.assertIn("Safer backup destinations", embed["description"])
        self.assertIn("Improved exclusions", embed["description"])
        self.assertNotIn("Very long information", embed["description"])
        self.assertLess(len(embed["description"]), 1200)
        self.assertIn("1.8.9 → 1.8.10", embed["fields"][0]["value"])
        self.assertIn("Windows x64", embed["fields"][0]["value"])
        self.assertIn("Full release notes", embed["fields"][-1]["value"])
        self.assertEqual(payload["allowed_mentions"], {"parse": [], "roles": list(mod.RELEASE_ROLE_IDS)})

    def test_beta_descriptive_sections_and_version_suffixes(self):
        body = (
            "# VaultSync 1.9.0 Beta 2\n\n"
            "Offline recovery is easier to access.\n\n"
            "## Bootable recovery USB\n"
            "Prepare and verify a recovery USB.\n\n"
            "## System recovery progress\n"
            "Tested on specific profiles.\n\n"
            "## Desktop improvements\n"
            "- Improved setup.\n\n"
            "## Update qualification\n"
            "1.9.0-beta.1 → 1.9.0-beta.2\n"
            "- Windows x64\n"
            "## Community\n"
            "Unrelated community content."
        )
        payload = mod.notification({
            "tag": "v1.9.0-beta.2",
            "name": "Beta 2", "body": body,
            "url": "https://example.test", "prerelease": True,
        })
        embed = payload["embeds"][0]
        self.assertTrue(embed["description"].startswith("Offline recovery"))
        self.assertIn("Bootable recovery USB", embed["description"])
        self.assertIn("System recovery progress", embed["description"])
        self.assertIn("Improved setup", embed["description"])
        self.assertNotIn("Unrelated community content", embed["description"])
        self.assertIn("1.9.0-beta.1 → 1.9.0-beta.2", embed["fields"][0]["value"])
        self.assertIn("stable release", embed["fields"][1]["value"])
        self.assertEqual(payload["allowed_mentions"], {"parse": [], "roles": list(mod.RELEASE_ROLE_IDS)})

    def test_replacing_old_post_checks_identity_before_resending(self):
        release = {
            "tag": "v1.8.10", "name": "VaultSync v1.8.10", "body": "Details",
            "url": "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.8.10",
            "prerelease": False,
        }
        old = "1571589463170224189"
        good = json.dumps({"id": old, "embeds": [{"url": release["url"]}]}).encode()
        webhook = "https://discord.com/api/webhooks/123456789012345678/token"
        with (
            patch.object(mod, "_webhook_message_request", side_effect=[(200, good), (204, b"")]) as request,
            patch.object(mod, "post_discord", return_value="1571589463170224190") as sender,
            redirect_stdout(StringIO()),
        ):
            new_id = mod.replace_previous(webhook, release, old)
        self.assertEqual(new_id, "1571589463170224190")
        self.assertEqual(sender.call_count, 1)
        self.assertEqual(request.call_args_list[0].args[2], "GET")
        self.assertEqual(request.call_args_list[1].args[2], "DELETE")

        wrong = json.dumps({"id": old, "embeds": [{"url": "https://example.invalid/other"}]}).encode()
        with (
            patch.object(mod, "_webhook_message_request", return_value=(200, wrong)),
            patch.object(mod, "post_discord") as sender,
        ):
            with self.assertRaisesRegex(ValueError, "not the intended"):
                mod.replace_previous(webhook, release, old)
        sender.assert_not_called()

    def test_cli_entrypoint_imports_without_module_path_configuration(self):
        script = Path(__file__).resolve().parents[2] / "scripts" / "discord_release_notify.py"
        env = os.environ.copy()
        env.pop("DISCORD_RELEASE_WEBHOOK", None)
        proc = subprocess.run(
            [sys.executable, str(script)],
            cwd=script.parents[1], env=env, capture_output=True, text=True, check=False,
        )
        self.assertNotEqual(proc.returncode, 0)
        self.assertIn("Missing DISCORD_RELEASE_WEBHOOK", proc.stderr)
        self.assertNotIn("ModuleNotFoundError", proc.stderr)

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
