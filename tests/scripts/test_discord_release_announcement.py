"""Offline tests: do not contact Discord or GitHub."""

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError, URLError

from scripts.discord_release_announcement import build_message, main, post_message, validated_webhook_url


EVENT = {
    "action": "published",
    "release": {
        "id": 123,
        "tag_name": "v1.9.0-beta.1",
        "html_url": "https://github.com/ATAC-Helicopter/VaultSync/releases/tag/v1.9.0-beta.1",
        "body": "Changes and notes. @everyone <@&12345>",
        "prerelease": True,
        "draft": False,
    },
}


class Response:
    status = 200

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False

    def read(self):
        return b'{"id":"998877"}'


class DiscordReleaseTests(unittest.TestCase):
    def test_beta_and_safe_mentions(self):
        payload = build_message(EVENT)
        self.assertIn("Prerelease / beta", payload["embeds"][0]["title"])
        self.assertEqual(payload["allowed_mentions"], {"parse": []})
        self.assertIn("@everyone", payload["embeds"][0]["description"])

    def test_stable_and_trimmed_notes(self):
        event = json.loads(json.dumps(EVENT))
        event["release"].update(prerelease=False, body="x" * 5000)
        payload = build_message(event)
        self.assertIn("Stable release", payload["embeds"][0]["title"])
        self.assertLessEqual(len(payload["embeds"][0]["description"]), 900)

    def test_draft_or_unrelated_event_skipped(self):
        event = json.loads(json.dumps(EVENT))
        event["release"]["draft"] = True
        self.assertIsNone(build_message(event))
        event["release"]["draft"] = False
        event["action"] = "edited"
        self.assertIsNone(build_message(event))

    def test_bad_or_missing_release_link_rejected(self):
        event = json.loads(json.dumps(EVENT))
        event["release"]["html_url"] = "http://example.org"
        with self.assertRaises(ValueError):
            build_message(event)

    def test_webhook_domain_and_scheme_validation(self):
        good = "https://discord.com/api/webhooks/123/abc.XYZ"
        self.assertEqual(validated_webhook_url(good), good + "?wait=true")
        for bad in ("http://discord.com/api/webhooks/123/abc", "https://evil.example/api/webhooks/123/abc", "https://discord.com.evil.example/api/webhooks/123/abc", good + "?wait=false"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                validated_webhook_url(bad)

    def test_delivery_receipt(self):
        calls = []
        def open_response(request, timeout):
            calls.append((request, timeout))
            return Response()
        message_id = post_message(build_message(EVENT), "https://discord.com/api/webhooks/123/token", opener=open_response)
        self.assertEqual(message_id, "998877")
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][1], 15)
        self.assertEqual(json.loads(calls[0][0].data)["allowed_mentions"], {"parse": []})

    def test_explicit_rate_limit_retried(self):
        tries = []
        waits = []
        def open_response(request, timeout):
            tries.append(True)
            if len(tries) == 1:
                raise HTTPError("secret-url", 429, "Too Many Requests", {"Retry-After": "1"}, None)
            return Response()
        post_message(build_message(EVENT), "https://discord.com/api/webhooks/123/token", opener=open_response, sleeper=waits.append)
        self.assertEqual(len(tries), 2)
        self.assertEqual(waits, [1.0])

    def test_ambiguous_failure_is_not_retried(self):
        calls = []
        def fail(request, timeout):
            calls.append(True)
            raise URLError("network failure")
        with self.assertRaisesRegex(RuntimeError, "status unknown"):
            post_message(build_message(EVENT), "https://discord.com/api/webhooks/123/token", opener=fail)
        self.assertEqual(len(calls), 1)

    def test_rerun_does_not_send(self):
        with tempfile.TemporaryDirectory() as tmp:
            event_path = Path(tmp) / "event.json"
            event_path.write_text(json.dumps(EVENT), encoding="utf-8")
            with patch("sys.argv", ["notifier", "--event-path", str(event_path)]), patch.dict("os.environ", {"GITHUB_RUN_ATTEMPT": "2", "DISCORD_RELEASE_WEBHOOK": "https://discord.com/api/webhooks/123/token"}), patch("scripts.discord_release_announcement.post_message") as sent, contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main(), 0)
                sent.assert_not_called()

    def test_dry_run_never_sends(self):
        with tempfile.TemporaryDirectory() as tmp:
            event_path = Path(tmp) / "event.json"
            event_path.write_text(json.dumps(EVENT), encoding="utf-8")
            output = io.StringIO()
            with patch("sys.argv", ["notifier", "--event-path", str(event_path), "--dry-run"]), patch.dict("os.environ", {"GITHUB_RUN_ATTEMPT": "1"}), patch("scripts.discord_release_announcement.post_message") as sent, contextlib.redirect_stdout(output):
                self.assertEqual(main(), 0)
                sent.assert_not_called()
            self.assertIn("v1.9.0-beta.1", output.getvalue())


if __name__ == "__main__":
    unittest.main()
