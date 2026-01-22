# What's New

### Performance and polish
- Faster dashboard and history refreshes with fewer redundant database reads.
- Smoother projects list refresh with cached discovery results.
- Startup tasks now stagger to keep launch responsive. {performance}

### Backup and upload reliability
- Archive uploads now auto-tune buffer sizes based on link speed.
- Parallel archive uploads can kick in for SMB-mapped destinations.
- Finalizing stage is clearer and cancel is disabled once uploads are done. {stability}

### Metadata sync
- Import previews are faster with lightweight store queries.
- Tombstoned backups no longer flip-flop between add/delete on repeated imports. {sync}

### UI refresh
- Projects detail panel and preset dropdown feel more modern and consistent.
- Backup storage card and totals reflect actual stored data.
- Dashboard KPIs use heavier weights for better readability.

### Updates
- Release notes are available in the app. [Release notes](https://github.com/ATAC-Helicopter/VaultSync/releases)
