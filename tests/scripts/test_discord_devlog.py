import importlib.util
import unittest
from pathlib import Path
from io import BytesIO
from unittest.mock import patch
from urllib.error import HTTPError

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

    def test_embed_no_image_and_mentions_disabled(self):
        item = mod.read_feed(fixture(("update", "VaultSync")))[0]
        payload = mod.message(item)
        self.assertEqual(payload["allowed_mentions"], {"parse": [], "roles": []})
        self.assertEqual(payload["embeds"][0]["url"], "https://fglabs.dev/devlog/update")
        self.assertNotIn("image", payload["embeds"][0])
        self.assertEqual(payload["username"], "VaultSync Dev Updates")
        self.assertEqual(payload["embeds"][0]["footer"]["text"], "VaultSync • FG Labs development journal")

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
        url = 'https://discord.com/api/webhooks/123456789012345678/notARealWebhookToken'
        problem = HTTPError(url, 403, 'Forbidden',
                            {'Content-Type': 'text/html'}, BytesIO(b'<html>blocked</html>'))
        with patch.object(mod, 'urlopen', side_effect=problem) as opener:
            with self.assertRaisesRegex(RuntimeError, 'HTTP 403.*non-JSON') as failure:
                mod.post_discord(url, {'content': 'test'})
        request = opener.call_args.args[0]
        self.assertEqual(request.get_header('User-agent'), mod.DISCORD_USER_AGENT)
        self.assertTrue(mod.DISCORD_USER_AGENT.startswith('DiscordBot ('))
        self.assertEqual(request.get_header('Content-type'), 'application/json')
        self.assertNotIn('notARealWebhookToken', str(failure.exception))
        self.assertNotIn('blocked', str(failure.exception))

    def test_discord_api_403_exposes_only_error_code(self):
        url = 'https://discord.com/api/webhooks/123456789012345678/secretToken'
        error = HTTPError(url, 403, 'Forbidden',
                          {'Content-Type': 'application/json; charset=utf-8'},
                          BytesIO(b'{"message":"Missing Permissions","code":50013}'))
        with patch.object(mod, 'urlopen', side_effect=error):
            with self.assertRaisesRegex(RuntimeError, 'HTTP 403: Discord API error code 50013') as failure:
                mod.post_discord(url, {'content': 'test'})
        self.assertNotIn('secretToken', str(failure.exception))

    def test_discord_post_keeps_confirmed_success_requirement(self):
        url = 'https://discord.com/api/webhooks/123456789012345678/secretToken'
        class Response:
            def __enter__(self):
                return self
            def __exit__(self, *args):
                return False
            def read(self, _n):
                return b'{"id":"123456789098765432"}'
        with patch.object(mod, 'urlopen', return_value=Response()) as opener:
            result = mod.post_discord(url, {'content': 'test'})
        self.assertEqual(result, '123456789098765432')
        self.assertEqual(opener.call_args.args[0].get_header('User-agent'), mod.DISCORD_USER_AGENT)
