#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.parse
import urllib.request
from collections import defaultdict
from datetime import datetime, timezone
from html import escape
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1
KEEP_RECENT_HISTORY = 90
UNNAMED_RELEASE = "Unnamed release"


def is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
    except ValueError:
        return False
    return path != root


def ensure_existing_workspace() -> Path:
    return Path.cwd().resolve(strict=True)


def resolve_workspace_path(raw_path: str) -> Path:
    workspace = ensure_existing_workspace()
    raw_candidate = Path(raw_path)
    lexical = (workspace / raw_candidate if not raw_candidate.is_absolute() else raw_candidate).resolve(strict=False)
    if not is_within(lexical, workspace):
        raise ValueError(f"Output directory must stay inside the workspace: {raw_path}")
    candidate = lexical.resolve(strict=False)
    if not is_within(candidate, workspace):
        raise ValueError(f"Resolved output directory must stay inside the workspace: {raw_path}")
    return candidate


def child_path(root: Path, relative_name: str) -> Path:
    normalized_root = root.resolve(strict=False)
    lexical = (normalized_root / relative_name).resolve(strict=False)
    if not is_within(lexical, normalized_root):
        raise ValueError(f"Generated path escapes output directory: {relative_name}")
    candidate = lexical.resolve(strict=False)
    if not is_within(candidate, normalized_root):
        raise ValueError(f"Resolved generated path escapes output directory: {relative_name}")
    return candidate


def output_file_path(root: Path, relative_name: str) -> Path:
    normalized_root = root.resolve(strict=False)
    safe_path = child_path(root, relative_name)
    safe_parent = safe_path.parent.resolve(strict=False)
    if safe_parent != normalized_root and not is_within(safe_parent, normalized_root):
        raise ValueError(f"Generated parent path escapes output directory: {relative_name}")
    safe_parent.mkdir(parents=True, exist_ok=True)
    return safe_path


def github_get_json(url: str, token: str) -> Any:
    headers = {
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {token}",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "VaultSync-download-stats",
    }
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request) as response:
        return json.loads(response.read().decode("utf-8"))


def fetch_all_releases(owner: str, repo: str, token: str) -> list[dict[str, Any]]:
    releases: list[dict[str, Any]] = []
    page = 1
    while True:
        query = urllib.parse.urlencode({"per_page": 100, "page": page})
        url = f"https://api.github.com/repos/{owner}/{repo}/releases?{query}"
        chunk = github_get_json(url, token)
        if not chunk:
            break
        releases.extend(chunk)
        if len(chunk) < 100:
            break
        page += 1
    return releases


def iso_now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def read_previous_snapshot(latest_path: Path) -> dict[str, Any] | None:
    if not latest_path.exists():
        return None
    try:
        return json.loads(latest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def previous_download_totals(
    previous: dict[str, Any] | None,
) -> tuple[int, dict[str, int], dict[tuple[str, str], int]]:
    previous_release_totals: dict[str, int] = {}
    previous_asset_totals: dict[tuple[str, str], int] = {}
    previous_total_downloads = 0

    if not previous:
        return previous_total_downloads, previous_release_totals, previous_asset_totals

    previous_total_downloads = int(previous.get("totals", {}).get("all_assets_downloads", 0))
    for prev_release in previous.get("releases", []):
        tag = prev_release.get("tag_name") or ""
        previous_release_totals[tag] = int(prev_release.get("total_downloads", 0))
        for asset in prev_release.get("assets", []):
            previous_asset_totals[(tag, asset.get("name") or "")] = int(asset.get("download_count", 0))
    return previous_total_downloads, previous_release_totals, previous_asset_totals


def empty_snapshot() -> dict[str, Any]:
    return {
        "schema_version": SCHEMA_VERSION,
        "captured_at": iso_now(),
        "repository": None,
        "releases": [],
        "totals": {
            "all_assets_downloads": 0,
            "all_assets_delta": 0,
            "release_count": 0,
            "asset_count": 0,
        },
        "highlights": {
            "latest_stable": None,
            "latest_prerelease": None,
            "top_assets": [],
        },
    }


def normalize_release(
    release: dict[str, Any],
    previous_release_totals: dict[str, int],
    previous_asset_totals: dict[tuple[str, str], int],
) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    release_item = {
        "id": release.get("id"),
        "tag_name": release.get("tag_name"),
        "name": release.get("name"),
        "draft": bool(release.get("draft")),
        "prerelease": bool(release.get("prerelease")),
        "created_at": release.get("created_at"),
        "published_at": release.get("published_at"),
        "html_url": release.get("html_url"),
        "assets": [],
        "total_downloads": 0,
        "downloads_delta": 0,
    }
    asset_rollup: list[dict[str, Any]] = []
    tag_name = release_item["tag_name"] or ""
    for asset in release.get("assets", []):
        asset_name = asset.get("name") or ""
        downloads = int(asset.get("download_count", 0))
        asset_item = {
            "id": asset.get("id"),
            "name": asset_name,
            "size": asset.get("size"),
            "content_type": asset.get("content_type"),
            "download_count": downloads,
            "downloads_delta": downloads - previous_asset_totals.get((tag_name, asset_name), 0),
            "created_at": asset.get("created_at"),
            "updated_at": asset.get("updated_at"),
            "browser_download_url": asset.get("browser_download_url"),
        }
        release_item["assets"].append(asset_item)
        release_item["total_downloads"] += downloads
        asset_rollup.append(
            {
                "release_tag": tag_name,
                "release_name": release_item["name"] or tag_name or UNNAMED_RELEASE,
                **asset_item,
            }
        )
    release_item["downloads_delta"] = release_item["total_downloads"] - previous_release_totals.get(tag_name, 0)
    return release_item, asset_rollup


def release_highlight(release: dict[str, Any]) -> dict[str, Any]:
    return {
        "tag_name": release["tag_name"],
        "name": release["name"],
        "total_downloads": release["total_downloads"],
        "downloads_delta": release["downloads_delta"],
        "html_url": release["html_url"],
    }


def set_latest_release_highlights(snapshot: dict[str, Any]) -> None:
    for release in snapshot["releases"]:
        if release["draft"]:
            continue
        key = "latest_prerelease" if release["prerelease"] else "latest_stable"
        if snapshot["highlights"][key] is None:
            snapshot["highlights"][key] = release_highlight(release)
        if snapshot["highlights"]["latest_prerelease"] and snapshot["highlights"]["latest_stable"]:
            return


def normalize_releases(raw_releases: list[dict[str, Any]], previous: dict[str, Any] | None) -> dict[str, Any]:
    previous_total, previous_releases, previous_assets = previous_download_totals(previous)
    snapshot = empty_snapshot()
    asset_rollup: list[dict[str, Any]] = []

    for release in raw_releases:
        release_item, release_assets = normalize_release(release, previous_releases, previous_assets)
        snapshot["releases"].append(release_item)
        asset_rollup.extend(release_assets)
        snapshot["totals"]["all_assets_downloads"] += release_item["total_downloads"]
        snapshot["totals"]["asset_count"] += len(release_item["assets"])

    snapshot["releases"].sort(key=lambda rel: rel.get("published_at") or rel.get("created_at") or "", reverse=True)
    snapshot["totals"]["release_count"] = len(snapshot["releases"])
    snapshot["totals"]["all_assets_delta"] = snapshot["totals"]["all_assets_downloads"] - previous_total
    set_latest_release_highlights(snapshot)

    asset_rollup.sort(key=lambda asset: (asset["download_count"], asset["downloads_delta"]), reverse=True)
    snapshot["highlights"]["top_assets"] = asset_rollup[:10]
    return snapshot


def write_text_file(root: Path, relative_name: str, content: str) -> None:
    normalized_root = root.resolve(strict=False)
    safe_path = (normalized_root / relative_name).resolve(strict=False)
    if safe_path == normalized_root or not safe_path.is_relative_to(normalized_root):
        raise ValueError(f"Generated path escapes output directory: {relative_name}")
    safe_parent = safe_path.parent.resolve(strict=False)
    if safe_parent != normalized_root and not safe_parent.is_relative_to(normalized_root):
        raise ValueError(f"Generated parent path escapes output directory: {relative_name}")
    safe_parent.mkdir(parents=True, exist_ok=True)
    safe_path.write_text(content, encoding="utf-8")


def write_json(root: Path, relative_name: str, data: Any) -> None:
    write_text_file(root, relative_name, json.dumps(data, indent=2, ensure_ascii=False) + "\n")


def append_markdown_highlights(lines: list[str], snapshot: dict[str, Any]) -> None:
    latest_stable = snapshot["highlights"].get("latest_stable")
    latest_prerelease = snapshot["highlights"].get("latest_prerelease")
    if not latest_stable and not latest_prerelease:
        return
    lines.extend(["## Highlights", ""])
    for label, item in (("Latest stable", latest_stable), ("Latest prerelease", latest_prerelease)):
        if item:
            lines.append(
                f"- {label}: **{item['name'] or item['tag_name']}** with "
                f"**{item['total_downloads']}** downloads ({format_signed(item['downloads_delta'])})"
            )
    lines.append("")


def append_markdown_releases(lines: list[str], releases: list[dict[str, Any]]) -> None:
    lines.extend(["## By release", ""])
    for release in releases:
        title = release["name"] or release["tag_name"] or UNNAMED_RELEASE
        if release["draft"]:
            label = " (draft)"
        elif release["prerelease"]:
            label = " (prerelease)"
        else:
            label = ""
        lines.extend(
            [
                f"### {title}{label}",
                "",
                f"- Tag: `{release['tag_name']}`",
                f"- Published: `{release['published_at']}`",
                f"- Total downloads: **{release['total_downloads']}**",
                f"- Delta: **{format_signed(release['downloads_delta'])}**",
                "",
            ]
        )
        if not release["assets"]:
            lines.extend(["_No assets attached to this release._", ""])
            continue
        lines.extend(["| Asset | Downloads | Delta | Size (bytes) |", "|---|---:|---:|---:|"])
        for asset in sorted(release["assets"], key=lambda item: item["download_count"], reverse=True):
            lines.append(
                f"| {asset['name']} | {asset['download_count']} | "
                f"{format_signed(asset['downloads_delta'])} | {asset['size']} |"
            )
        lines.append("")


def build_markdown(snapshot: dict[str, Any]) -> str:
    lines: list[str] = []
    owner_repo = snapshot["repository"]
    captured_at = snapshot["captured_at"]
    totals = snapshot["totals"]

    lines.append(f"# Download stats for {owner_repo}")
    lines.append("")
    lines.append(f"Captured at: `{captured_at}`")
    lines.append("")
    lines.append(f"- Releases: **{totals['release_count']}**")
    lines.append(f"- Assets: **{totals['asset_count']}**")
    lines.append(f"- Total asset downloads: **{totals['all_assets_downloads']}**")
    lines.append(f"- Change since previous snapshot: **{format_signed(totals['all_assets_delta'])}**")
    lines.append("")

    append_markdown_highlights(lines, snapshot)

    top_assets = snapshot["highlights"].get("top_assets") or []
    if top_assets:
        lines.append("## Top assets")
        lines.append("")
        lines.append("| Asset | Release | Downloads | Delta |")
        lines.append("|---|---|---:|---:|")
        for asset in top_assets:
            lines.append(
                f"| {asset['name']} | `{asset['release_tag']}` | {asset['download_count']} | {format_signed(asset['downloads_delta'])} |"
            )
        lines.append("")

    append_markdown_releases(lines, snapshot["releases"])
    return "\n".join(lines) + "\n"


def format_signed(value: int) -> str:
    return f"+{value}" if value > 0 else str(value)


def render_badge(value: str, tone: str) -> str:
    return f'<span class="badge badge-{tone}">{escape(value)}</span>'


def release_badge(release: dict[str, Any]) -> str:
    if release["draft"]:
        return render_badge("Draft", "muted")
    if release["prerelease"]:
        return render_badge("Prerelease", "warning")
    return render_badge("Stable", "success")


def render_asset_table(assets: list[dict[str, Any]]) -> str:
    if not assets:
        return '<p class="muted">No assets attached to this release.</p>'
    rows = []
    for asset in sorted(assets, key=lambda item: item["download_count"], reverse=True):
        rows.append(
            "<tr>"
            f"<td><a href=\"{escape(asset['browser_download_url'])}\">{escape(asset['name'])}</a></td>"
            f"<td>{asset['download_count']}</td>"
            f"<td>{escape(format_signed(asset['downloads_delta']))}</td>"
            f"<td>{asset['size']}</td>"
            "</tr>"
        )
    return (
        "<table><thead><tr><th>Asset</th><th>Downloads</th><th>Delta</th><th>Size (bytes)</th></tr></thead>"
        f"<tbody>{''.join(rows)}</tbody></table>"
    )


def render_release_card(release: dict[str, Any]) -> str:
    title = escape(release["name"] or release["tag_name"] or UNNAMED_RELEASE)
    return (
        '<section class="release-card">'
        f"<div class=\"release-head\"><div><h3>{title}</h3><p class=\"muted\">{escape(release['tag_name'] or '')}</p></div>"
        f"<div class=\"release-badges\">{release_badge(release)}</div></div>"
        '<div class="stats-row">'
        f"<div><span class=\"label\">Published</span><strong>{escape(release['published_at'] or 'n/a')}</strong></div>"
        f"<div><span class=\"label\">Downloads</span><strong>{release['total_downloads']}</strong></div>"
        f"<div><span class=\"label\">Delta</span><strong>{escape(format_signed(release['downloads_delta']))}</strong></div>"
        f"<div><span class=\"label\">Release</span><strong><a href=\"{escape(release['html_url'] or '#')}\">Open</a></strong></div>"
        "</div>"
        f"{render_asset_table(release['assets'])}"
        "</section>"
    )


def render_highlight_card(title: str, item: dict[str, Any] | None, tone: str) -> str:
    if item is None:
        return (
            f"<article class=\"metric-card {tone}\"><h3>{escape(title)}</h3>"
            '<p class="muted">No release available yet.</p></article>'
        )
    release_name = escape(item["name"] or item["tag_name"] or title)
    return (
        f"<article class=\"metric-card {tone}\"><h3>{escape(title)}</h3>"
        f"<strong>{release_name}</strong>"
        f"<p>{item['total_downloads']} downloads</p>"
        f"<p class=\"muted\">{escape(format_signed(item['downloads_delta']))} since previous snapshot</p>"
        f"<a href=\"{escape(item['html_url'] or '#')}\">Open release</a></article>"
    )


def build_html(snapshot: dict[str, Any]) -> str:
    totals = snapshot["totals"]
    latest_stable = snapshot["highlights"].get("latest_stable")
    latest_prerelease = snapshot["highlights"].get("latest_prerelease")
    top_assets = snapshot["highlights"].get("top_assets") or []

    releases_html = [render_release_card(release) for release in snapshot["releases"]]

    top_asset_items = "".join(
        "<tr>"
        f"<td>{escape(asset['name'])}</td>"
        f"<td>{escape(asset['release_tag'])}</td>"
        f"<td>{asset['download_count']}</td>"
        f"<td>{escape(format_signed(asset['downloads_delta']))}</td>"
        "</tr>"
        for asset in top_assets
    )

    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>VaultSync Download Stats</title>
  <style>
    :root {{
      color-scheme: dark;
      --bg: #0b1220;
      --panel: #152238;
      --panel-2: #1b2b45;
      --border: #32476b;
      --text: #ecf3ff;
      --muted: #9eb3d6;
      --accent: #5ca3ff;
      --success: #2fbf71;
      --warning: #f0b24f;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      font-family: "Segoe UI", Inter, Arial, sans-serif;
      background:
        radial-gradient(circle at top right, rgba(92,163,255,.18), transparent 30%),
        linear-gradient(180deg, #0b1220, #08101c 60%);
      color: var(--text);
    }}
    main {{ max-width: 1200px; margin: 0 auto; padding: 32px 20px 64px; }}
    h1 {{ margin: 0 0 8px; font-size: 2.4rem; }}
    h2 {{ margin-top: 36px; }}
    a {{ color: var(--accent); text-decoration: none; }}
    a:hover {{ text-decoration: underline; }}
    .lede {{ color: var(--muted); max-width: 70ch; line-height: 1.5; }}
    .hero {{
      background: linear-gradient(160deg, rgba(92,163,255,.16), rgba(47,191,113,.08));
      border: 1px solid var(--border);
      border-radius: 24px;
      padding: 28px;
      box-shadow: 0 18px 60px rgba(0,0,0,.24);
    }}
    .hero-grid {{
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 16px;
      margin-top: 24px;
    }}
    .metric-card, .release-card {{
      background: rgba(21,34,56,.9);
      border: 1px solid var(--border);
      border-radius: 20px;
      padding: 20px;
    }}
    .metric-card strong {{ display: block; margin: 8px 0 4px; font-size: 1.35rem; }}
    .metric-card p {{ margin: 0 0 6px; }}
    .metric-card.primary {{ background: linear-gradient(165deg, rgba(92,163,255,.18), rgba(21,34,56,.95)); }}
    .metric-card.success {{ background: linear-gradient(165deg, rgba(47,191,113,.18), rgba(21,34,56,.95)); }}
    .metric-card.warning {{ background: linear-gradient(165deg, rgba(240,178,79,.14), rgba(21,34,56,.95)); }}
    .split {{
      display: grid;
      grid-template-columns: minmax(0, 2fr) minmax(320px, 1fr);
      gap: 20px;
      align-items: start;
      margin-top: 28px;
    }}
    .panel {{
      background: rgba(21,34,56,.9);
      border: 1px solid var(--border);
      border-radius: 20px;
      padding: 20px;
    }}
    table {{
      width: 100%;
      border-collapse: collapse;
      margin-top: 12px;
    }}
    th, td {{
      text-align: left;
      padding: 10px 12px;
      border-bottom: 1px solid rgba(158,179,214,.14);
      vertical-align: top;
    }}
    th {{ color: var(--muted); font-weight: 600; }}
    .release-card + .release-card {{ margin-top: 20px; }}
    .release-head {{
      display: flex;
      justify-content: space-between;
      gap: 12px;
      align-items: start;
    }}
    .release-badges {{ display: flex; gap: 8px; flex-wrap: wrap; }}
    .badge {{
      display: inline-flex;
      align-items: center;
      border-radius: 999px;
      padding: 6px 10px;
      font-size: .82rem;
      font-weight: 600;
      border: 1px solid transparent;
    }}
    .badge-success {{ background: rgba(47,191,113,.12); color: #aff1c8; border-color: rgba(47,191,113,.35); }}
    .badge-warning {{ background: rgba(240,178,79,.12); color: #ffd89c; border-color: rgba(240,178,79,.35); }}
    .badge-muted {{ background: rgba(158,179,214,.12); color: #d7e5fb; border-color: rgba(158,179,214,.25); }}
    .stats-row {{
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: 12px;
      margin: 18px 0 8px;
    }}
    .label {{ display: block; color: var(--muted); font-size: .82rem; margin-bottom: 4px; }}
    .muted {{ color: var(--muted); }}
    .footer {{
      margin-top: 28px;
      color: var(--muted);
      font-size: .92rem;
    }}
    @media (max-width: 860px) {{
      .split {{ grid-template-columns: 1fr; }}
      .release-head {{ flex-direction: column; }}
    }}
  </style>
</head>
<body>
  <main>
    <section class="hero">
      <h1>VaultSync download stats</h1>
      <p class="lede">Daily snapshots of GitHub release asset downloads, grouped into a public summary and backed by raw JSON history. This tracks release-asset downloads only, not installs from mirrors or package managers.</p>
      <div class="hero-grid">
        <article class="metric-card primary">
          <h3>Total asset downloads</h3>
          <strong>{totals['all_assets_downloads']}</strong>
          <p>{escape(format_signed(totals['all_assets_delta']))} since previous snapshot</p>
        </article>
        <article class="metric-card">
          <h3>Releases tracked</h3>
          <strong>{totals['release_count']}</strong>
          <p>{totals['asset_count']} attached assets</p>
        </article>
        {render_highlight_card("Latest stable", latest_stable, "success")}
        {render_highlight_card("Latest prerelease", latest_prerelease, "warning")}
      </div>
      <p class="footer">Captured at <code>{escape(snapshot['captured_at'])}</code> for <strong>{escape(snapshot['repository'])}</strong>.</p>
    </section>

    <section class="split">
      <div class="panel">
        <h2>Releases</h2>
        {''.join(releases_html)}
      </div>
      <aside class="panel">
        <h2>Top assets</h2>
        <table>
          <thead><tr><th>Asset</th><th>Release</th><th>Downloads</th><th>Delta</th></tr></thead>
          <tbody>{top_asset_items}</tbody>
        </table>
        <h2>Raw files</h2>
        <ul>
          <li><a href="./latest.json">latest.json</a></li>
          <li><a href="./README.md">README.md</a></li>
          <li><a href="./history/">history/</a></li>
        </ul>
      </aside>
    </section>
  </main>
</body>
</html>
"""


def ensure_history_index(history_dir: Path) -> None:
    items = sorted((path.name for path in history_dir.glob("*.json")), reverse=True)
    index_html = [
        "<!DOCTYPE html>",
        "<html lang=\"en\"><head><meta charset=\"utf-8\"><title>VaultSync download stats history</title></head><body>",
        "<h1>VaultSync download stats history</h1>",
        "<ul>",
    ]
    for item in items:
        index_html.append(f"<li><a href=\"./{escape(item)}\">{escape(item)}</a></li>")
    index_html.extend(["</ul>", "</body></html>"])
    child_path(history_dir, "index.html").write_text("\n".join(index_html) + "\n", encoding="utf-8")


def prune_history(history_dir: Path, keep_recent: int = KEEP_RECENT_HISTORY) -> list[str]:
    snapshots = sorted(history_dir.glob("*.json"), key=lambda path: path.name, reverse=True)
    if len(snapshots) <= keep_recent:
        return []

    retained: set[Path] = set(snapshots[:keep_recent])
    monthly_kept: set[str] = set()

    for path in snapshots[keep_recent:]:
        month_key = path.name[:7]
        if month_key not in monthly_kept:
            retained.add(path)
            monthly_kept.add(month_key)

    removed: list[str] = []
    for path in snapshots:
        if path in retained:
            continue
        path.unlink(missing_ok=True)
        removed.append(path.name)

    return removed


def main() -> int:
    parser = argparse.ArgumentParser(description="Snapshot GitHub release download stats and generate a public summary.")
    parser.add_argument("--owner", default=os.environ.get("REPO_OWNER"))
    parser.add_argument("--repo", default=os.environ.get("REPO_NAME"))
    parser.add_argument("--token", default=os.environ.get("GITHUB_TOKEN"))
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()

    if not args.owner or not args.repo or not args.token:
        parser.error("--owner, --repo, and --token are required (or set REPO_OWNER/REPO_NAME/GITHUB_TOKEN).")

    output_dir = resolve_workspace_path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    latest_path = child_path(output_dir, "latest.json")
    history_dir = child_path(output_dir, "history")
    history_dir.mkdir(parents=True, exist_ok=True)

    previous = read_previous_snapshot(latest_path)
    releases = fetch_all_releases(args.owner, args.repo, args.token)
    snapshot = normalize_releases(releases, previous)
    snapshot["repository"] = f"{args.owner}/{args.repo}"

    timestamp_slug = snapshot["captured_at"].replace(":", "-")
    write_json(output_dir, "latest.json", snapshot)
    write_json(history_dir, f"{timestamp_slug}.json", snapshot)
    prune_history(history_dir)
    write_text_file(output_dir, "README.md", build_markdown(snapshot))
    write_text_file(output_dir, "index.html", build_html(snapshot))
    write_text_file(output_dir, ".nojekyll", "")
    ensure_history_index(history_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
