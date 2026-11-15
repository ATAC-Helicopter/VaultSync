#!/usr/bin/env python3
"""Announce a published VaultSync release through its Discord webhook."""
import json
import os
import re
from urllib.parse import quote
from urllib.request import Request, urlopen

from scripts.discord_devlog_notify import post_discord

RELEASE_API = "https://api.github.com/repos/ATAC-Helicopter/VaultSync/releases/tags/"
TAG = re.compile(r"^v[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$")


def fetch_release(tag):
    if not TAG.fullmatch(tag):
        raise ValueError("Invalid VaultSync release tag")
    request = Request(
        RELEASE_API + quote(tag, safe=""),
        headers={"Accept": "application/vnd.github+json", "User-Agent": "VaultSyncReleaseNotifier/1.0"},
    )
    with urlopen(request, timeout=20) as response:
        raw = response.read(500_001)
    if len(raw) > 500_000:
        raise ValueError("Release metadata is larger than expected")
    release = json.loads(raw)
    if release.get("tag_name") != tag or release.get("draft"):
        raise ValueError("Release is missing, unpublished, or does not match the requested tag")
    return {
        "tag": tag,
        "name": str(release.get("name") or tag)[:256],
        "body": str(release.get("body") or "").strip()[:3900],
        "url": f"https://github.com/ATAC-Helicopter/VaultSync/releases/tag/{quote(tag, safe='')}",
        "prerelease": bool(release.get("prerelease")),
    }


def notification(release):
    kind = "pre-release" if release["prerelease"] else "release"
    description = release["body"] or f"VaultSync {release['tag']} is now available."
    return {
        "content": f"📦 **VaultSync {release['tag']} {kind}**",
        "embeds": [{
            "title": release["name"],
            "url": release["url"],
            "description": description,
            "color": 0x4388DB,
        }],
        "allowed_mentions": {"parse": []},
    }


def main():
    tag = os.getenv("RELEASE_TAG", "").strip()
    webhook = os.getenv("DISCORD_RELEASE_WEBHOOK", "").strip()
    if not webhook:
        raise ValueError("Missing DISCORD_RELEASE_WEBHOOK")
    release = fetch_release(tag)
    message_id = post_discord(webhook, notification(release))
    print(f"Posted release {tag} as {message_id}")


if __name__ == "__main__":
    main()
