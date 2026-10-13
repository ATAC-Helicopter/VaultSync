# VaultSync 1.9.4 — Unified Recovery Experience

Stage: planned, unscheduled. Target date: Unscheduled.
Branch: `release/1.9.4`; predecessor integration candidate: `1.9.3`.
Current published stable remains 1.8.9. No predecessor compatibility or native
product functionality is qualified by creation of this branch.

Owning work IDs: `VS-1941`, `VS-1942`, `VS-1943`, `VS-1944`.
BSC architecture gate VS-1981/1982 applies to 1.9.6 and later only.

See [family contract and stable gates](RELEASE_FAMILY_1.9.md) and
[canonical scope/status](../ROADMAP.md). Before promotion, integrate the qualified
predecessor, verify actual functionality and all version/packaging consumers,
record a reviewed target date, and complete native release evidence.

## Changelog protocol — 2026-10-04

Own development notes use one explicit-target `Unreleased` section and standard
Keep a Changelog categories. Published history is backfilled without rewriting
tags/assets. Original inherited development notes remain in the evidence ledger;
they do not establish shipped predecessor features. Run scripts/changelog.py check
and scripts/validate_development.sh contracts before updating the owning PR.

Current preparation: `work/1.9.4-changelog-protocol` / #754. Protected release identity stays `release/1.9.4`. See the family preparation-head map.

## Repository tracking — 2026-10-07

VS-1980/#673 distributes the shared helper from #759/#758. Use `./dev adopt` for existing owning issues and `./dev status` before pushes; reuse this release's preparation PR. Allocating IDs requires a complete remote audit. This tooling does not qualify product or release gates.

## Shared compact changelog standard — 2026-10-12

VS-1980/#673 distributes short descriptions (aim 8–14 words; ceiling 22) and adjacent entries without blank separators. AGENTS.md, CONTRIBUTING.md and the automated validator agree; headings retain spacing. No sibling unreleased features or release metadata are imported.

Shared release standard (2026-10-13): VS-1980/#673 adopts mandatory provenance
signing under [the policy](RELEASE_SIGNING.md), including updater manifests and
patches, retained offline proof and exact workflow/ref/commit verification. This
changes release validation only; native publisher signing remains owned by
VS-1902/#113 in 1.9.0. This release retains its own feature scope and metadata.

Shared localization standard (2026-10-13): VS-1980/#673 adopts the mandatory
[UI localization contract](LOCALIZATION.md), source/catalog checks in required CI
and before packaging, plus shared existing-desktop text repairs from
BUG-19030/#785 (owned by 1.9.0). Maintained locales and cached language-change
behavior are covered. This release retains its metadata and feature scope;
static checks do not establish native-speaker or rendered platform qualification.

Reused release packages must also have unchanged `Localization/` source inputs;
translation-only changes require rebuilding the qualified source run.
