# Following the files — publication package

Prepared 2026-10-14; VS-1980 / #673, release preparation PR #736. Draft only.

- [Canonical website article](../2026-10-14-vaultsync-190-backups-origin-and-size.md).
- [Reddit text](../2026-10-14-vaultsync-190-backups-origin-and-size-reddit.md), prepared for r/VaultSync.
- [First-party entry JSON](site-entry.json), using the site's current data schema.
- [Gallery, captions, dimensions and SHA-256](gallery.json).
- Proposed route: `https://fglabs.dev/devlog/vaultsync-190-following-the-files`.
  The route is not published; do not add it as a live Reddit link yet.

## Images and provenance

Gallery order: imported projects, Unity preset, native storage chart. The PNGs
are unmodified outputs from actual production Avalonia controls with the native
macOS renderer, English resources, an isolated configuration/database and
synthetic project data. They are not illustrations, mockups or OS screen grabs.
The desktop capture connector could not start. Full OS interaction and installed
build qualification are not established by these images.

The fixture contains two real local ZIP backups created through BackupService.
The Unity ZIP contains `Assets/Player.cs`, `Assets/Player.cs.meta` and
`Assets/Prototype.unity`; its Library cache and `.pdata` fixture are absent.
The other ZIP contains `src/Program.cs`. Stored sizes were 10,369 and 4,374 bytes.
The chart displays logical payload totals of 110,918 and 49,918 bytes, rounded
by the app to 108.3 KB and 48.7 KB. It is not a stored-byte or compression chart.
Free-space values belong to the capture filesystem; no user hostname, real
project, credential, share or personal file appears in the selected captures.

Native rendering ran with filesystem writes restricted to the temporary demo
root and networking denied. Application startup integration, auto backups,
metadata import and update checks were not used to operate the normal profile.
The harness uses application XAML/resources and production view models; it
captures MainWindow content, the actual ProjectsView and the current storage card.
No labels, controls or screenshot pixels were painted over or substituted.

## Website handoff

The FG Labs working copy was behind its current remote main. A separate worktree
was created from `ac7116b` on `docs/vaultsync-190-backups-origin-and-size`, at
`/tmp/fglabs-vaultsync-devlog-20261014`. The normal site checkout is preserved.
The new entry, image copies and a landscape-hero width correction are prepared
there. No site branch push, deployment, merge or Reddit submission is performed.

Keep the two compression comparisons distinct: ZIP tuning versus its previous
levels, and Huffman versus our first isolated prototype. Synthetic fixtures do
not establish savings on actual projects or server transfer speed. The first
prototype remains research and does not replace the app's archive format.

Before publication, inspect the article/archive/project page in desktop and
mobile browsers. The browser connection is unavailable in this session; rendered
webpage review remains pending. Build/lint and local HTTP/resource checks are
recorded in the site's accompanying draft review. After publishing and checking
the route, add its real link to the Reddit copy. After Reddit submission, record
the actual Reddit URL and run the existing devlog sync.
