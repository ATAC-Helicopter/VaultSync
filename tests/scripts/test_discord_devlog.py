import importlib.util
import json
import os
import sys
import tempfile
from contextlib import contextmanager, redirect_stdout
import unittest
from pathlib import Path
from io import StringIO
from unittest.mock import patch, MagicMock

path = Path(__file__).resolve().parents[2] / "scripts" / "discord_devlog_notify.py"
spec = importlib.util.spec_from_file_location("discord_devlog_notify", path)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


def fixture(*items):
    out = []
    for slug, project in items:
        out.append(
            f"<item><guid>https://fglabs.dev/devlog/{slug}</guid>"
            f"<title>{slug}</title><link>https://fglabs.dev/devlog/{slug}</link>"
            f"<description>Useful update about recovery.</description>"
            f"<category>{project}</category>"
            f"<pubDate>Tue, 10 Nov 2026 20:00:00 GMT</pubDate></item>"
        )
    return ("<rss><channel>" + "".join(out) + "</channel></rss>").encode()


@contextmanager
def working_directory(path):
    previous = os.getcwd()
    os.chdir(path)
    try:
        yield
    finally:
        os.chdir(previous)


class FakeResponse:
    def __init__(self, body):
        self.body = body

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def read(self, _n):
        return self.body


class DiscordDevlogTests(unittest.TestCase):
    def test_filter(self):
        items = mod.read_feed(fixture(("new", "VaultSync"), ("other", "InputShare")))
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]["title"], "new")

    def test_initializes_without_reposting_archive(self):
        items = mod.read_feed(fixture(("new", "VaultSync"), ("old", "VaultSync")))
        sent, cursor = mod.pending_posts(items, "")
        self.assertEqual(sent, [])
        self.assertEqual(cursor, "https://fglabs.dev/devlog/new")

    def test_new_articles_oldest_first(self):
        items = mod.read_feed(fixture(("third", "VaultSync"), ("second", "VaultSync"), ("first", "VaultSync")))
        sent, cursor = mod.pending_posts(items, "https://fglabs.dev/devlog/first")
        self.assertEqual([i["title"] for i in sent], ["second", "third"])
        self.assertEqual(cursor, "https://fglabs.dev/devlog/third")

    def test_dont_repeat_if_cursor_not_found(self):
        with self.assertRaises(ValueError):
            mod.pending_posts(mod.read_feed(fixture(("new", "VaultSync"))), "missing")

    def test_reject_external_url(self):
        xml = b"<rss><channel><item><guid>x</guid><title>x</title><link>https://evil.com/devlog/x</link><category>VaultSync</category></item></channel></rss>"
        self.assertEqual(mod.read_feed(xml), [])

    def test_feed_guid_must_match_validated_article_url(self):
        xml = b"<rss><channel><item><guid>https://evil.test/path</guid><title>x</title><link>https://fglabs.dev/devlog/x</link><category>VaultSync</category></item></channel></rss>"
        self.assertEqual(mod.read_feed(xml), [])

    def test_state_cursors_must_be_valid_article_urls(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "state.json"
            path.write_text(json.dumps({"last_guid": "../../outside"}))
            with self.assertRaisesRegex(ValueError, "Invalid delivery state"):
                mod.load_state(path)

    def test_embed_no_image_and_mentions_disabled(self):
        item = mod.read_feed(fixture(("update", "VaultSync")))[0]
        payload = mod.message(item)
        self.assertEqual(payload["allowed_mentions"], {"parse": [], "roles": []})
        self.assertEqual(payload["embeds"][0]["url"], "https://fglabs.dev/devlog/update")
        self.assertNotIn("image", payload["embeds"][0])
        self.assertEqual(payload["username"], "VaultSync Dev Updates")
        self.assertEqual(payload["embeds"][0]["footer"]["text"], "VaultSync • FG Labs development journal")

    def test_devlog_embed_preserves_paragraphs_and_links(self):
        item = mod.read_feed(fixture(("update", "VaultSync")))[0]
        item["summary"] = "First paragraph.\n\nSecond paragraph."
        embed = mod.message(item)["embeds"][0]
        self.assertEqual(embed["description"], "First paragraph.\n\nSecond paragraph.")
        self.assertEqual(embed["thumbnail"]["url"], mod.AVATAR)
        self.assertIn(item["url"], embed["fields"][0]["value"])
        item["summary"] = "A longer development update. " * 100
        description = mod.message(item)["embeds"][0]["description"]
        self.assertLessEqual(len(description), 600)
        self.assertTrue(description.endswith("…"))

    def test_opt_in_role_only(self):
        item = mod.read_feed(fixture(("update", "VaultSync")))[0]
        role = "123456789012345678"
        payload = mod.message(item, role)
        self.assertIn("<@&" + role + ">", payload["content"])
        self.assertEqual(payload["allowed_mentions"], {"parse": [], "roles": [role]})

    def test_invalid_role(self):
        item = mod.read_feed(fixture(("update", "VaultSync")))[0]
        with self.assertRaises(ValueError):
            mod.message(item, "@everyone")

    def test_manual_latest_announces_baselined_article_once(self):
        items = mod.read_feed(fixture(("latest", "VaultSync"), ("older", "VaultSync")))
        previous = {"last_guid": items[0]["guid"]}
        sent, cursor = mod.choose_notifications(items, previous, publish_latest=True)
        self.assertEqual([item["title"] for item in sent], ["latest"])
        self.assertEqual(cursor, items[0]["guid"])
        previous["last_sent_guid"] = sent[-1]["guid"]
        repeated, repeated_cursor = mod.choose_notifications(items, previous, publish_latest=True)
        self.assertEqual(repeated, [])
        self.assertEqual(repeated_cursor, cursor)

    def test_manual_latest_does_not_change_normal_scheduled_behavior(self):
        items = mod.read_feed(fixture(("latest", "VaultSync"), ("older", "VaultSync")))
        sent, cursor = mod.choose_notifications(items, {"last_guid": items[0]["guid"]})
        self.assertEqual(sent, [])
        self.assertEqual(cursor, items[0]["guid"])

    def test_manual_latest_on_first_run_sends_only_one_current_article(self):
        items = mod.read_feed(fixture(("latest", "VaultSync"), ("older", "VaultSync")))
        sent, cursor = mod.choose_notifications(items, {}, publish_latest=True)
        self.assertEqual([item["title"] for item in sent], ["latest"])
        self.assertEqual(cursor, items[0]["guid"])

    def test_manual_latest_does_not_bypass_pending_automatic_posts(self):
        items = mod.read_feed(fixture(("latest", "VaultSync"), ("older", "VaultSync")))
        sent, cursor = mod.choose_notifications(items, {"last_guid": items[1]["guid"]}, publish_latest=True)
        self.assertEqual([item["title"] for item in sent], ["latest"])
        self.assertEqual(cursor, items[0]["guid"])

    def test_last_sent_guid_prevents_reannouncing_auto_delivered_article(self):
        items = mod.read_feed(fixture(("latest", "VaultSync"), ("older", "VaultSync")))
        state = {"last_guid": items[0]["guid"], "last_sent_guid": items[0]["guid"]}
        sent, cursor = mod.choose_notifications(items, state, publish_latest=True)
        self.assertEqual(sent, [])
        self.assertEqual(cursor, items[0]["guid"])

    def test_discord_post_sends_compliant_user_agent(self):
        url = "https://discord.com/api/webhooks/123456789012345678/fixture"
        connection = MagicMock()
        response = MagicMock()
        response.status = 403
        response.getheader.return_value = "text/html"
        response.read.return_value = b"<html>blocked</html>"
        connection.getresponse.return_value = response
        with patch.object(mod, "HTTPSConnection", return_value=connection) as ctor:
            with self.assertRaisesRegex(RuntimeError, "HTTP 403.*non-JSON") as failure:
                mod.post_discord(url, {"content": "test"})
        ctor.assert_called_once_with("discord.com", timeout=20)
        args, kwargs = connection.request.call_args
        self.assertEqual(args[0], "POST")
        self.assertEqual(args[1], "/api/webhooks/123456789012345678/fixture?wait=true")
        self.assertEqual(kwargs["headers"]["User-Agent"], mod.DISCORD_USER_AGENT)
        self.assertEqual(kwargs["headers"]["Content-Type"], "application/json")
        self.assertNotIn("fixture", str(failure.exception))
        connection.close.assert_called_once()


    def test_discord_api_403_exposes_only_error_code(self):
        url = "https://discord.com/api/webhooks/123456789012345678/fixture"
        with patch.object(mod, "_send_discord_request", return_value=(
                403, "application/json; charset=utf-8",
                b'{"message":"Missing Permissions","code":50013}')):
            with self.assertRaisesRegex(RuntimeError, "HTTP 403: Discord API error code 50013") as failure:
                mod.post_discord(url, {"content": "test"})
        self.assertNotIn("fixture", str(failure.exception))


    def test_discord_post_keeps_confirmed_success_requirement(self):
        url = "https://discord.com/api/webhooks/123456789012345678/fixture"
        with patch.object(mod, "_send_discord_request", return_value=(
                200, "application/json", b'{"id":"123456789098765432"}')) as sender:
            result = mod.post_discord(url, {"content": "test"})
        self.assertEqual(result, "123456789098765432")
        self.assertEqual(sender.call_count, 1)


    def test_main_posts_pending_article_and_saves_cursor(self):
        with tempfile.TemporaryDirectory() as root:
            state = Path(root) / "discord-devlog-state.json"
            output = Path(root) / "discord-devlog-new-state.json"
            old = "https://fglabs.dev/devlog/old"
            new = "https://fglabs.dev/devlog/new"
            state.write_text(json.dumps({"last_guid": old, "last_sent_guid": old}))
            webhook = "https://discord.com/api/webhooks/123456789012345678/fixture"
            with (
                patch.object(mod, "urlopen", return_value=FakeResponse(
                    fixture(("new", "VaultSync"), ("old", "VaultSync")))) as feed,
                patch.object(mod, "_send_discord_request", return_value=(
                    200, "application/json", b'{"id":"123456789012345678"}')) as sender,
                patch.object(sys, "argv", ["notifier"]),
                patch.dict(os.environ, {"DISCORD_DEVLOG_WEBHOOK": webhook,
                                        "DISCORD_DEVLOG_ROLE_ID": "123456789012345678"}, clear=True),
                redirect_stdout(StringIO()),
            ):
                with working_directory(root):
                    mod.main()
            result = json.loads(output.read_text())
            self.assertEqual(result["last_guid"], new)
            self.assertEqual(result["last_sent_guid"], new)
            self.assertEqual(feed.call_count, 1)
            self.assertEqual(sender.call_count, 1)


    def test_main_dry_run_never_posts_or_writes(self):
        with tempfile.TemporaryDirectory() as root:
            state = Path(root) / "discord-devlog-state.json"
            output = Path(root) / "discord-devlog-new-state.json"
            state.write_text('{"last_guid":"https://fglabs.dev/devlog/old"}')
            with (
                patch.object(mod, "urlopen",
                             return_value=FakeResponse(fixture(("new", "VaultSync"), ("old", "VaultSync")))) as opener,
                patch.object(sys, "argv", ["notifier", "--dry-run"]),
                patch.dict(os.environ, {}, clear=True),
                redirect_stdout(StringIO()) as messages,
            ):
                with working_directory(root):
                    mod.main()
            self.assertIn("https://fglabs.dev/devlog/new", messages.getvalue())
            self.assertEqual(opener.call_count, 1)
            self.assertFalse(output.exists())

    def test_main_initial_run_seeds_without_sending_history(self):
        with tempfile.TemporaryDirectory() as root:
            state = Path(root) / "discord-devlog-state.json"
            output = Path(root) / "discord-devlog-new-state.json"
            with (
                patch.object(mod, "urlopen",
                             return_value=FakeResponse(fixture(("new", "VaultSync"), ("old", "VaultSync")))) as opener,
                patch.object(sys, "argv", ["notifier"]),
                patch.dict(os.environ, {"DISCORD_DEVLOG_WEBHOOK":
                           "https://discord.com/api/webhooks/123456789012345678/token"}, clear=True),
                redirect_stdout(StringIO()),
            ):
                with working_directory(root):
                    mod.main()
            self.assertEqual(json.loads(output.read_text())["last_guid"],
                             "https://fglabs.dev/devlog/new")
            self.assertEqual(opener.call_count, 1)

    def test_main_refuses_missing_webhook_and_keeps_state(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / "discord-devlog-new-state.json"
            with (
                patch.object(mod, "urlopen",
                             return_value=FakeResponse(fixture(("new", "VaultSync")))),
                patch.object(sys, "argv", ["notifier"]),
                patch.dict(os.environ, {}, clear=True),
            ):
                with working_directory(root):
                    with self.assertRaisesRegex(ValueError, "Missing DISCORD_DEVLOG_WEBHOOK"):
                        mod.main()
            self.assertFalse(output.exists())

    def test_main_refuses_pending_post_without_role_and_keeps_cursor(self):
        with tempfile.TemporaryDirectory() as root:
            state = Path(root) / "discord-devlog-state.json"
            output = Path(root) / "discord-devlog-new-state.json"
            state.write_text(json.dumps({"last_guid": "https://fglabs.dev/devlog/old"}))
            with (
                patch.object(mod, "urlopen", return_value=FakeResponse(
                    fixture(("new", "VaultSync"), ("old", "VaultSync")))),
                patch.object(sys, "argv", ["notifier"]),
                patch.dict(os.environ, {"DISCORD_DEVLOG_WEBHOOK":
                           "https://discord.com/api/webhooks/123456789012345678/token"}, clear=True),
            ):
                with working_directory(root):
                    with self.assertRaisesRegex(ValueError, "Missing DISCORD_DEVLOG_ROLE_ID"):
                        mod.main()
            self.assertFalse(output.exists())
            self.assertEqual(json.loads(state.read_text())["last_guid"],
                             "https://fglabs.dev/devlog/old")

    def test_main_does_not_advance_cursor_after_post_failure(self):
        with tempfile.TemporaryDirectory() as root:
            state = Path(root) / "discord-devlog-state.json"
            output = Path(root) / "discord-devlog-new-state.json"
            state.write_text('{"last_guid":"https://fglabs.dev/devlog/old"}')
            url = "https://discord.com/api/webhooks/123456789012345678/fixture"
            with (
                patch.object(mod, "urlopen", return_value=FakeResponse(
                    fixture(("new", "VaultSync"), ("old", "VaultSync")))),
                patch.object(mod, "_send_discord_request", return_value=(
                    403, "application/json", b'{"code":50013}')),
                patch.object(sys, "argv", ["notifier"]),
                patch.dict(os.environ, {"DISCORD_DEVLOG_WEBHOOK": url,
                                        "DISCORD_DEVLOG_ROLE_ID": "123456789012345678"}, clear=True),
            ):
                with working_directory(root):
                    with self.assertRaisesRegex(RuntimeError, "Discord API error code 50013"):
                        mod.main()
            self.assertFalse(output.exists())
            self.assertEqual(json.loads(state.read_text())["last_guid"],
                             "https://fglabs.dev/devlog/old")


    def test_main_rejects_oversize_feed_before_writing_cursor(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root) / "discord-devlog-new-state.json"
            with (
                patch.object(mod, "urlopen",
                             return_value=FakeResponse(b"x" * 2_000_001)),
                patch.object(sys, "argv", ["notifier", "--dry-run"]),
                patch.dict(os.environ, {}, clear=True),
            ):
                with working_directory(root):
                    with self.assertRaisesRegex(ValueError, "RSS larger than expected"):
                        mod.main()
            self.assertFalse(output.exists())

    def test_discord_retries_rate_limits_and_preserves_confirmed_id(self):
        url = "https://discord.com/api/webhooks/123456789012345678/fixture"
        with (
            patch.object(mod, "_send_discord_request", side_effect=[
                (429, "application/json", b'{"retry_after":0.2}'),
                (200, "application/json", b'{"id":"123456789012345678"}'),
            ]) as sender,
            patch.object(mod.time, "sleep") as sleep,
        ):
            self.assertEqual(mod.post_discord(url, {"content": "test"}),
                             "123456789012345678")
        self.assertEqual(sender.call_count, 2)
        sleep.assert_called_once()


    def test_reject_unrecognized_webhook_targets(self):
        for value in [
            "http://discord.com/api/webhooks/123456789012345678/secret",
            "https://example.net/api/webhooks/123456789012345678/secret",
            "https://discord.com/api/webhooks/123456789012345678/secret?x=1",
            "https://discord.com/api/webhooks/123456789012345678/secret#part",
            "https://discord.com/api/webhooks/xyz/secret",
        ]:
            with self.subTest(value=value):
                with self.assertRaisesRegex(ValueError, "Invalid webhook URL"):
                    mod.post_discord(value, {"content": "test"})

    def test_pending_limit_advances_in_fixed_batches(self):
        items = mod.read_feed(fixture(*[(f"post-{i}", "VaultSync")
                                       for i in range(8, 0, -1)]))
        sent, cursor = mod.pending_posts(items, "https://fglabs.dev/devlog/post-1")
        self.assertEqual(len(sent), 5)
        self.assertEqual(cursor, "https://fglabs.dev/devlog/post-6")
        next_sent, new_cursor = mod.pending_posts(items, cursor)
        self.assertEqual(len(next_sent), 2)
        self.assertEqual(new_cursor, "https://fglabs.dev/devlog/post-8")

    def test_empty_feed_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "No valid VaultSync"):
            mod.pending_posts([], "https://fglabs.dev/devlog/old")

    def test_rejects_bad_rss_root(self):
        with self.assertRaisesRegex(ValueError, "Expected RSS"):
            mod.read_feed(b"<html><p>not an RSS feed</p></html>")

    def test_discord_canonical_origin_for_legacy_webhook(self):
        url = "https://discordapp.com/api/webhooks/123456789012345678/fixture"
        with patch.object(mod, "_send_discord_request", return_value=(
                200, "application/json", b'{"id":"123456789012345678"}')) as sender:
            self.assertEqual(mod.post_discord(url, {"content": "test"}),
                             "123456789012345678")
        self.assertEqual(sender.call_args.args[0],
                         "/api/webhooks/123456789012345678/fixture")


    def test_webhook_transport_errors_do_not_log_token(self):
        url = "https://discord.com/api/webhooks/123456789012345678/privatefixture"
        connection = MagicMock()
        connection.request.side_effect = OSError("transport failed for " + url)
        with patch.object(mod, "HTTPSConnection", return_value=connection):
            with self.assertRaisesRegex(RuntimeError, "network failure") as failure:
                mod.post_discord(url, {"content": "test"})
        self.assertNotIn("privatefixture", str(failure.exception))
        connection.close.assert_called_once()


    def test_feed_rejects_doctype_and_entity_declarations(self):
        for xml in [
            b'<!DOCTYPE rss [<!ENTITY a "bad">]><rss><channel/></rss>',
            b'<!doctype rss><rss><channel/></rss>',
        ]:
            with self.subTest(xml=xml):
                with self.assertRaisesRegex(ValueError, "declarations are not allowed"):
                    mod.read_feed(xml)
