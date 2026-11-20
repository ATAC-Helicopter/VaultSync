# Screenshot library

These are the canonical product screenshots used by the repository README,
website, wiki, release material, and promotional pages.

Refreshed on **2026-11-16**. Public guides use the released **1.8.10** source,
tag `v1.8.10`, commit `3582268768bb35477203a6c2ef30e3e5a7df5267`.
Only development documents use the separate `development/` images from 1.9.0
preparation commit `c7f76c862b3f580955406763087a61eb0dd34679`.
Image hashes, dimensions and source versions are recorded in [manifest.json](manifest.json).

| File | Primary use |
| --- | --- |
| `Dashboard.png` | README hero, website hero, daily overview |
| `Projects_Page.png` | Project discovery, optional folders, folder actions, assignment, presets, and tags |
| `Schedule_Page.png` | Operational readiness, upcoming opportunities, project coverage, shared policy, and runtime constraints |
| `Guide_Page.png` | In-app workflow guide and recovery terminology |
| `Backup_Page.png` | Backup health, storage, and restore points |
| `Compare_View.png` | Snapshot comparison and text diff |
| `History_Timeline.png` | Searchable history and restore-point details |
| `Recovery_Readiness.png` | Recovery score, coverage, and 3-2-1 guidance |
| `Recovery_Drill.png` | Successful read-only recovery-drill evidence |
| `Settings_General.png` | General, scheduling, retention, and transfer |
| `Settings_Destinations.png` | Advanced destinations, retry, resume, and quota |
| `Settings_Encryption_Performance.png` | Encryption, format, and performance |
| `Settings_Maintenance.png` | Maintenance, repair planning, conflicts, and updates |
| `Theme_Gallery_Dark.png` | Dark-theme promotional gallery |
| `Theme_Gallery_Light.png` | Light-theme promotional gallery |
| `tour/` | Stable wiki-specific aliases and onboarding coverage |

The feature screenshots use an isolated demonstration profile with `Client Portal`,
`Design System` and `Release Tools`. Source files and destinations are synthetic;
credentials are absent. Real backup, snapshot, comparison and read-only integrity
drill services produce the displayed data. The first two projects have verified
demo checkpoints; the third deliberately shows missing protection. All storage
is local test storage, so the interface continues to report missing offsite copies.

Images are offscreen renders of the unmodified Avalonia views through Skia on
Linux, at 96 DPI with English localization. Full-page images are 1600 × 1050;
the recovery proof and theme galleries show the corresponding actual controls.
They are interface illustrations, not native desktop, package, system-disk,
boot-media or release-qualification evidence. No generated or retouched UI is used.
Theme changes affect only the disposable profile, never the installed application.

Dated images under `docs/release-evidence/images/` retain their original sources,
hashes and measured conditions. They are historical evidence and are not replaced
with current illustrations.
