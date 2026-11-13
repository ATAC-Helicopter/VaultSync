#!/usr/bin/env python3
"""Send one Discord announcement when a GitHub release is published.

The release event is the only input. No GitHub API credentials or third-party
Python packages are needed. The workflow explicitly excludes rerun attempts to
avoid unintentionally reposting an announcement.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit
from urllib.request import Request, urlopen


def build_message(event: dict) -> dict | None:
    """Create a bounded, no-pings Discord payload for a published release."""
    if event.get("action") != "published":
        return None
    release = event.get("release") or {}
    if release.get("draft"):
        return None
    tag = release.get("tag_name")
    link = release.get("html_url")
    if not isinstance(tag, str) or not tag.strip():
        raise ValueError("Published release is missing a tag")
    if not isinstance(link, str) or not link.startswith("https://github.com/"):
        raise ValueError("Published release is missing a GitHub URL")

    prerelease = bool(release.get("prerelease"))
    kind = "Prerelease / beta" if prerelease else "Stable release"
    body = release.get("body") or ""
    if not isinstance(body, str):
        body = ""
    summary = body.replace("\r\n", "\n").strip()
    if len(summary) > 900:
        summary = summary[:897].rstrip() + "..."
    if not summary:
        summary = "See the release page for details and supported downloads."

    return {
        "username": "VaultSync Releases",
        "allowed_mentions": {"parse": []},
        "embeds": [
            {
                "title": f"VaultSync {tag} | {kind}"[:256],
                "url": link,
                "description": summary,
                "color": 0xE6A23C if prerelease else 0x3478F6,
                "fields": [
                    {
                        "name": "Release notes and downloads",
                        "value": f"[Open GitHub release]({link})",
                    }
                ],
                "footer": {"text": "VaultSync · Official GitHub Release"},
            }
        ],
    }


def validated_webhook_url(value: str) -> str:
    """Reject non-Discord URLs so the secret cannot be sent to another host."""
    parts = urlsplit(value)
    if (
        parts.scheme != "https"
        or parts.hostname not in {"discord.com", "discordapp.com"}
        or parts.port not in {None, 443}
        or parts.username is not None
        or parts.password is not None
        or parts.query
        or parts.fragment
        or not re.fullmatch(r"/api(?:/v\d+)?/webhooks/\d+/[A-Za-z0-9_.-]+", parts.path)
    ):
        raise ValueError("DISCORD_RELEASE_WEBHOOK must be a Discord HTTPS webhook URL")
    return urlunsplit((parts.scheme, parts.netloc, parts.path, "wait=true", ""))


def post_message(payload: dict, webhook_url: str, *, opener=urlopen, sleeper=time.sleep) -> str:
    """Retry only explicit 429 rejections, never ambiguous delivery failures."""
    url = validated_webhook_url(webhook_url)
    encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = Request(url, data=encoded, headers={"Content-Type": "application/json", "User-Agent": "VaultSync-Release-Notifier"}, method="POST")
    for attempt in range(3):
        try:
            with opener(request, timeout=15) as response:
                if response.status != 200:
                    raise RuntimeError(f"Unexpected Discord response status: {response.status}")
                message = json.loads(response.read())
                message_id = message.get("id")
                if not message_id:
                    raise RuntimeError("Discord did not confirm a message ID; do not automatically resend")
                return str(message_id)
        except HTTPError as error:
            if error.code == 429 and attempt < 2:
                try:
                    delay = float(error.headers.get("Retry-After", "1"))
                except (ValueError, TypeError):
                    delay = 1.0
                sleeper(min(max(delay, 0.5), 20.0))
                continue
            raise RuntimeError(f"Discord rejected announcement with HTTP {error.code}") from None
        except (URLError, TimeoutError):
            raise RuntimeError("Discord delivery status unknown; check the channel before any manual retry") from None
    raise RuntimeError("Discord rate limit still active; announcement not confirmed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event-path", type=Path, default=os.environ.get("GITHUB_EVENT_PATH"))
    parser.add_argument("--dry-run", action="store_true", help="Print the payload; do not contact Discord")
    args = parser.parse_args()

    if not args.event_path:
        parser.error("--event-path or GITHUB_EVENT_PATH is required")

    if os.environ.get("GITHUB_RUN_ATTEMPT", "1") != "1" and not args.dry_run:
        print("Skipping rerun attempt: release announcements must not be automatically reposted.")
        return 0

    event = json.loads(Path(args.event_path).read_text(encoding="utf-8"))
    payload = build_message(event)
    if payload is None:
        print("Not a published public release; no announcement sent.")
        return 0
    if args.dry_run:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        return 0

    webhook = os.environ.get("DISCORD_RELEASE_WEBHOOK")
    if not webhook:
        raise RuntimeError("Repository secret DISCORD_RELEASE_WEBHOOK is missing")
    message_id = post_message(payload, webhook)
    print(f"Discord confirmed release announcement (message ID: {message_id}).")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, RuntimeError, OSError, json.JSONDecodeError) as error:
        # Do not include the secret URL or underlying HTTP exception in logs.
        print(f"Release notifier error: {error}", file=sys.stderr)
        sys.exit(1)
