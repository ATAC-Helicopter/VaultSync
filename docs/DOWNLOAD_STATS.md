# Download Stats

VaultSync can snapshot GitHub release asset download counts on a schedule and publish a human-readable summary without polluting `Dev` or `Stable`.

## How it works
- Workflow: `.github/workflows/download-stats.yml`
- Generator: `scripts/download_stats.py`
- Persistent output branch: `download-stats`

Each run:
- reads GitHub Releases + asset download counts via the Releases API
- updates `latest.json`
- appends a timestamped `history/*.json` snapshot
- regenerates a Markdown summary (`README.md`)
- regenerates a public-friendly HTML summary (`index.html`)
- commits only to the `download-stats` branch

## Why this branch-only model is used
- `Dev` stays focused on development history
- `Stable` stays focused on releasable history
- daily operational snapshots do not clutter release or code review history

## Output layout
- `README.md`
- `index.html`
- `latest.json`
- `history/<timestamp>.json`
- `history/index.html`

## Public use
If you want a browsable public page, point GitHub Pages at the `download-stats` branch root.

Even without Pages, the branch still provides:
- readable Markdown in GitHub
- raw JSON snapshots
- a static `index.html` that can be downloaded or served later

## Notes
- This tracks GitHub release asset downloads only.
- It does not count installs from mirrors, package managers, or cloned source trees.
- Draft and prerelease data are included in the raw snapshot and labeled in the summaries.
