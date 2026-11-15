#!/usr/bin/env python3
"""Send new VaultSync devlog articles as compact Discord webhook embeds."""
import argparse
import json
import os
import re
import time
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlparse
from urllib.request import Request, urlopen
from xml.etree import ElementTree as ET
from email.utils import parsedate_to_datetime
from datetime import timezone

FEED = "https://fglabs.dev/devlog/feed.xml?project=VaultSync"
AVATAR = "https://raw.githubusercontent.com/ATAC-Helicopter/VaultSync/Stable/docs/branding/vaultsync-reddit-icon.png"
ROLE = re.compile(r"^[0-9]{17,20}$")
# Discord/Cloudflare can reject the default Python-urllib User-Agent (HTTP 403).
DISCORD_USER_AGENT = "DiscordBot (https://github.com/ATAC-Helicopter/VaultSync, 1.0)"
PATH = re.compile(r"^/devlog/[a-z0-9][a-z0-9-]*$")


def read_feed(xml):
    root = ET.fromstring(xml)
    if root.tag != "rss":
        raise ValueError("Expected RSS")
    posts = []
    seen = set()
    for item in root.findall("./channel/item"):
        value = lambda key: (item.findtext(key) or "").strip()
        link, guid, title = value("link"), value("guid"), value("title")
        url = urlparse(link)
        if (not guid or not title or guid in seen
                or url.scheme != "https" or url.netloc != "fglabs.dev"
                or not PATH.fullmatch(url.path) or url.query or url.fragment
                or value("category").casefold() != "vaultsync"):
            continue
        seen.add(guid)
        try:
            date = parsedate_to_datetime(value("pubDate")).astimezone(timezone.utc).isoformat()
        except (TypeError, ValueError, OverflowError):
            date = None
        posts.append({"guid": guid, "url": link, "title": title,
                      "summary": value("description"), "date": date})
    return posts


def pending_posts(posts, cursor):
    if not posts:
        raise ValueError("No valid VaultSync items: refusing to advance cursor")
    if not cursor:
        return [], posts[0]["guid"]  # Never replay old articles at first startup.
    for i, post in enumerate(posts):
        if post["guid"] == cursor:
            pending = list(reversed(posts[:i]))[:5]
            return pending, pending[-1]["guid"] if pending else cursor
    raise ValueError("Cursor no longer in feed; refusing to replay history")


def choose_notifications(posts, state, publish_latest=False):
    """Choose new posts or manually announce an already-baselined latest post once."""
    pending, cursor = pending_posts(posts, state.get("last_guid", ""))
    if (publish_latest and not pending and posts[0]["guid"] == cursor
            and state.get("last_sent_guid") != cursor):
        return [posts[0]], cursor
    return pending, cursor

def message(post, role_id=""):
    if role_id and not ROLE.fullmatch(role_id):
        raise ValueError("Invalid Discord role ID")
    summary = " ".join(post["summary"].split())
    if len(summary) > 380:
        summary = summary[:379].rstrip() + "…"
    embed = {"title": post["title"][:256], "url": post["url"],
             "description": summary or "Read the new development article on FG Labs.",
             "color": 0x4388DB,
             "footer": {"text": "VaultSync • FG Labs development journal"}}
    if post["date"]:
        embed["timestamp"] = post["date"]
    return {
        "username": "VaultSync Dev Updates", "avatar_url": AVATAR,
        "content": (f"<@&{role_id}>\n" if role_id else "") + "🛠️ **New development update**",
        "embeds": [embed],
        "allowed_mentions": {"parse": [], "roles": [role_id] if role_id else []},
    }


def post_discord(webhook, payload):
    url = urlparse(webhook)
    if (url.scheme != "https" or url.netloc not in ("discord.com", "discordapp.com")
            or not re.fullmatch(r"/api/webhooks/[0-9]+/[A-Za-z0-9._-]+", url.path)
            or url.query or url.fragment):
        raise ValueError("Invalid webhook URL")

    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    for attempt in range(3):
        request = Request(webhook + "?wait=true", data=data, method="POST",
                          headers={"Content-Type": "application/json",
                                   "Accept": "application/json",
                                   "User-Agent": DISCORD_USER_AGENT})
        try:
            with urlopen(request, timeout=20) as result:
                response = json.loads(result.read(16384))
                if not response.get("id"):
                    raise ValueError("Discord did not confirm delivery")
                return response["id"]
        except HTTPError as error:
            # Never log the webhook URL/token or raw HTML error pages.
            error_body = error.read(4096)
            content_type = (error.headers.get("Content-Type", "") if error.headers else "")
            discord_error = None
            if "application/json" in content_type.lower():
                try:
                    discord_error = json.loads(error_body)
                except (UnicodeDecodeError, ValueError):
                    pass
            if error.code == 429 and attempt < 2:
                try:
                    retry = float(discord_error.get("retry_after", 2)) if isinstance(discord_error, dict) else 2
                except (TypeError, ValueError):
                    retry = 2
                time.sleep(max(0.5, min(10, retry)))
                continue

            if isinstance(discord_error, dict) and isinstance(discord_error.get("code"), int):
                detail = f"Discord API error code {discord_error['code']}"
            else:
                detail = "non-JSON response (possible network/Cloudflare block)"
            raise RuntimeError(
                f"Discord webhook rejected HTTP {error.code}: {detail}; "
                "webhook URL and response body intentionally omitted"
            ) from None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    parser.add_argument("--output-state", required=True)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--publish-latest", action="store_true",
                        help="Publish the latest already-baselined article once")
    args = parser.parse_args()
    prior = Path(args.state)
    state = json.loads(prior.read_text()) if prior.is_file() and prior.stat().st_size else {}
    request = Request(FEED, headers={"User-Agent": "VaultSyncDiscordNotifier/1.0"})
    with urlopen(request, timeout=25) as response:
        xml = response.read(2_000_001)
    if len(xml) > 2_000_000:
        raise ValueError("RSS larger than expected")
    posts = read_feed(xml)
    pending, cursor = choose_notifications(posts, state, args.publish_latest)
    role_id = os.getenv("DISCORD_DEVLOG_ROLE_ID", "").strip()
    messages = [message(post, role_id) for post in pending]
    if args.dry_run:
        print(json.dumps({"messages": messages, "next_guid": cursor}, indent=2))
        return
    webhook = os.getenv("DISCORD_DEVLOG_WEBHOOK", "").strip()
    if not webhook:
        raise ValueError("Missing DISCORD_DEVLOG_WEBHOOK")
    if not state.get("last_guid") and not args.publish_latest:
        print("Seeded latest article without historical backfill")
    for item, payload in zip(pending, messages):
        msgid = post_discord(webhook, payload)
        print("Posted", item["guid"], "as", msgid)
    output = Path(args.output_state)
    output.parent.mkdir(parents=True, exist_ok=True)
    state["last_guid"] = cursor
    if pending:
        state["last_sent_guid"] = pending[-1]["guid"]
    output.write_text(json.dumps(state, indent=2) + "\n")
    print("Cursor prepared after", len(pending), "notification(s)")


if __name__ == "__main__":
    main()
