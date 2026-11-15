#!/usr/bin/env python3
"""Publish concise VaultSync release announcements via Discord webhooks.

An explicit one-off replacement can retire a previous message only after a
corrected announcement was successfully published.
"""
import json
import os
import re
from http.client import HTTPSConnection, HTTPException
from urllib.parse import quote, urlparse
from urllib.request import Request, urlopen

if __package__:
    from .discord_devlog_notify import AVATAR, DISCORD_USER_AGENT, post_discord
else:
    from discord_devlog_notify import AVATAR, DISCORD_USER_AGENT, post_discord

RELEASE_API = "https://api.github.com/repos/ATAC-Helicopter/VaultSync/releases/tags/"
TAG = re.compile(r"^v[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$")
SNOWFLAKE = re.compile(r"^[0-9]{17,20}$")

# The server's explicitly opt-in notification roles. Never use @everyone.
RELEASE_ROLE_IDS = (
    "1569729675079589889",  # Dev Updates
    "1569729619500863609",  # Beta Updates
    "1569838025285050538",  # Release Updates
)


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
        "body": str(release.get("body") or "").strip()[:30000],
        "url": f"https://github.com/ATAC-Helicopter/VaultSync/releases/tag/{quote(tag, safe='')}",
        "prerelease": bool(release.get("prerelease")),
    }


def _compact(value, limit):
    """Turn Markdown release prose into short, readable Discord-safe lines."""
    value = re.sub(r"!\[[^\]]*\]\([^)]+\)", "", value)
    value = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", value)
    value = re.sub(r"[*_" + chr(96) + r"]+", "", value)
    value = " ".join(value.split())
    return value if len(value) <= limit else value[:limit - 1].rstrip() + "…"


def _section(body, name):
    # Only second-level Markdown headings delimit release note sections.
    lines = body.replace("\r", "").splitlines()
    section = []
    active = False
    for line in lines:
        if line.startswith("## "):
            heading = line[3:].strip().casefold()
            if active:
                break
            active = heading == name.casefold()
        elif active:
            section.append(line)
    return section


def _opening(body):
    lines = body.replace("\r", "").splitlines()
    paragraph = []
    for line in lines:
        if line.startswith("#"):
            break
        if not line.strip() and paragraph:
            break
        if line.strip():
            paragraph.append(line)
    return _compact(" ".join(paragraph), 270)


def _highlights(body):
    """Use the author's Highlights headings instead of dumping entire notes."""
    results = []
    heading, detail = None, []
    for line in _section(body, "Highlights") + ["### END"]:
        if line.startswith("### "):
            if heading:
                sentence = re.split(r"(?<=[.!?])\s+", " ".join(detail).strip(), maxsplit=1)[0]
                value = _compact(sentence, 132)
                results.append(f"• **{_compact(heading, 64)}** — {value}" if value
                               else f"• **{_compact(heading, 64)}**")
            heading, detail = line[4:].strip(), []
        elif heading and line.strip() and not line.strip().startswith("- "):
            detail.append(line.strip())
    return results[:5]


def _qualification(body):
    lines = _section(body, "Update qualification")
    if not lines:
        return ""
    text = "\n".join(lines)
    # The release itself states the validated update path and platforms.
    version = re.search(r"(\d+\.\d+\.\d+)\s*→\s*(\d+\.\d+\.\d+)", text)
    platforms = [_compact(line.lstrip("- ").strip(), 45)
                 for line in lines if line.strip().startswith("- ")]
    prefix = (f"**{version.group(1)} → {version.group(2)}**" if version else "Upgrade validation")
    value = prefix + (" · tested on " + ", ".join(platforms[:5]) if platforms else "")
    return _compact(value, 360)


def notification(release):
    tag = release["tag"]
    kind = "Pre-release" if release["prerelease"] else "Stable release"
    intro = _opening(release["body"]) or f"VaultSync {tag} is now available."
    highlights = _highlights(release["body"])
    if highlights:
        description = intro + "\n\n**What's improved**\n" + "\n".join(highlights)
    else:
        description = intro
    fields = []
    qualification = _qualification(release["body"])
    if qualification:
        fields.append({"name": "✅ Tested update", "value": qualification, "inline": False})
    fields.append({
        "name": "⬇️ Downloads & details",
        "value": (
            f"[Get VaultSync](https://fglabs.dev/vaultsync)"
            f"  ·  [Full release notes]({release['url']})"
        ),
        "inline": False,
    })
    payload = {
        "username": "VaultSync Releases",
        "avatar_url": AVATAR,
        "content": (
            " ".join(f"<@&{role_id}>" for role_id in RELEASE_ROLE_IDS)
            + f"\n📦 **VaultSync {tag} is now available**"
            + (" *(pre-release)*" if release["prerelease"] else "")
        ),
        "embeds": [{
            "title": _compact(release["name"], 256),
            "url": release["url"],
            "description": description[:4096],
            "color": 0x4388DB if not release["prerelease"] else 0xDC9959,
            "thumbnail": {"url": AVATAR},
            "fields": fields,
            "footer": {"text": f"VaultSync • {kind} • Windows, macOS & Linux"},
        }],
        # Only the three known server roles can trigger a mention.
        "allowed_mentions": {"parse": [], "roles": list(RELEASE_ROLE_IDS)},
    }
    return payload


def _webhook_message_request(webhook, message_id, method):
    """Get/delete a message from our webhook; never log the credential URL."""
    if method not in ("GET", "DELETE") or not SNOWFLAKE.fullmatch(message_id):
        raise ValueError("Invalid Discord message operation")
    url = urlparse(webhook)
    if (url.scheme != "https" or url.netloc not in ("discord.com", "discordapp.com")
            or not re.fullmatch(r"/api/webhooks/[0-9]+/[A-Za-z0-9._-]+", url.path)
            or url.query or url.fragment):
        raise ValueError("Invalid Discord webhook URL")
    connection = HTTPSConnection("discord.com", timeout=20)
    try:
        connection.request(method, url.path + "/messages/" + message_id,
                           headers={"Accept": "application/json", "User-Agent": DISCORD_USER_AGENT})
        response = connection.getresponse()
        return response.status, response.read(100_000)
    except (OSError, HTTPException):
        raise RuntimeError("Discord message operation failed; webhook details omitted") from None
    finally:
        connection.close()


def replace_previous(webhook, release, old_message_id):
    """Replace one verified earlier announcement, creating the new ping only once.

    If sending fails, the original post stays. If deletion fails, an operator must
    remove the original separately; do not automatically resend the new post.
    """
    status, body = _webhook_message_request(webhook, old_message_id, "GET")
    if status != 200:
        raise RuntimeError(f"Original Discord message unavailable (HTTP {status})")
    try:
        existing = json.loads(body)
    except (ValueError, UnicodeDecodeError):
        raise ValueError("Original Discord message returned invalid JSON") from None
    if (existing.get("id") != old_message_id or
            not any(e.get("url") == release["url"] for e in existing.get("embeds", []))):
        raise ValueError("Original message is not the intended VaultSync release announcement")
    new_message_id = post_discord(webhook, notification(release))
    print(f"Posted corrected release {release['tag']} as {new_message_id}")
    status, _ = _webhook_message_request(webhook, old_message_id, "DELETE")
    if status != 204:
        raise RuntimeError(
            f"Corrected message {new_message_id} was posted, but deleting old post failed "
            f"(HTTP {status}); do not re-send, remove the old post manually"
        )
    print(f"Retired previous release announcement {old_message_id}")
    return new_message_id


def main():
    tag = os.getenv("RELEASE_TAG", "").strip()
    webhook = os.getenv("DISCORD_RELEASE_WEBHOOK", "").strip()
    if not webhook:
        raise ValueError("Missing DISCORD_RELEASE_WEBHOOK")
    release = fetch_release(tag)
    previous = os.getenv("DISCORD_REPLACE_MESSAGE_ID", "").strip()
    if previous:
        replace_previous(webhook, release, previous)
        return
    message_id = post_discord(webhook, notification(release))
    print(f"Posted release {tag} as {message_id}")


if __name__ == "__main__":
    main()
