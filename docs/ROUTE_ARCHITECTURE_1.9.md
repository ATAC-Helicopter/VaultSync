# 1.9 desktop route architecture — VS-1910 review draft

Status: proposed contract for [VS-1910 / #501](https://github.com/ATAC-Helicopter/VaultSync/issues/501). This document records a migration design; no new route or deep link is implemented by this document. `ROADMAP.md` remains the delivery authority.

## Current shell and the migration boundary

The current shell exposes Dashboard, Projects, Backups, Schedule, History, Recovery, Guide, and Settings. `AppViewModel.SetCurrentView(string)` switches cached view models, updates `CurrentViewName` and `CurrentViewKey`, and persists only the last page key. Unknown keys fall back to Dashboard. Individual pages own their selected project and filters. Tray actions and onboarding call the page commands directly. These behaviors are visible in `AppViewModel.NavigationOps.cs`, `AppViewModel.StartupOps.cs`, `AppViewModel.TrayOps.cs`, and the page view models.

This model retains page state during many in-process switches, but a page name cannot identify a backup, a recovery point, a tab, or a pending operation. It also provides no single place to validate navigation requests. The migration must wrap the existing pages before replacing them; existing operations and saved `LastView` values continue to work.

## User intent and workflow ownership

| 1.9 intent | User question | Current entry points | First migration boundary |
| --- | --- | --- | --- |
| Protect | What should be backed up, where, and when? | Dashboard, Projects, Backups, Schedule | Project selection, destination, backup creation, schedule, and status |
| History | What was captured, changed, or verified? | History, Backups | Snapshot and recorded-backup inspection, diff, verification evidence |
| Recover | What can I restore, and what will change? | Recovery, Backups, Guide | Recovery point selection, preview, restore, then future image/portable recovery |
| Manage | How is VaultSync configured and diagnosed? | Settings, Guide, Projects | Destinations, credentials, updates, diagnostics, documentation |

The four intents are navigation groups, not new storage types. A live mirror belongs to Protect and must remain distinguishable from a recorded backup. A backup record in History does not prove its payload is available or intact; verification evidence is shown separately. Disk images and recovery media enter Recover only after their own format, platform, and restore contracts are approved.

## Proposed route and state contract

Use one typed route value as the authority for the active location. The first implementation can map it to existing page view models without replacing their command or data services.

```text
Route = { kind, resourceId?, subview?, schemaVersion = 1 }
kind = protect | history | recover | manage
resourceId = stable project, backup, snapshot, or image identity as allowed by kind
subview = a documented detail or task within the kind
```

- Route parameters identify resources; they never contain filesystem paths, credentials, encryption material, or user-entered search text. A route must be validated against the local repository before opening a resource.
- Keep transient page state separately: selected project, filters, sort, scroll position, and an unfinished form or preview. Persist only safe, versioned state with an explicit lifetime. An unfinished destructive operation cannot be resumed as an authorized action after restart.
- Navigation is a request/result operation: `Open(route, origin)` resolves a valid target, returns an explicit unavailable/unsupported result when necessary, and commits the active route only after resolution. Cancelled navigation leaves the current task and state intact.
- A fresh process restores a supported last location or falls back to Protect overview. Old `LastView` keys map to the table below; unknown or newer versions fall back safely without changing repository data.
- Back and forward restore the prior route and its local view state. A refresh retains identity-based selection when the resource still exists, and gives a clear missing-resource state when it does not.
- Background work owns its own cancellation and completion state. Merely leaving a page never silently cancels a backup, restore, or verification. A task that would be interrupted prompts or exposes an explicit safe cancellation action.

## Legacy route map

| Saved 1.8/early 1.9 key | Proposed destination | Compatibility requirement |
| --- | --- | --- |
| Dashboard | Protect overview | Preserve status and next actions |
| Projects | Protect projects | Preserve project selection and management actions |
| Backups | Protect backups; History and Recover links for inspection/restore | Keep all existing backup actions reachable during migration |
| Schedule | Protect schedule | Preserve schedule editing and status |
| History | History overview | Preserve filters and timeline semantics |
| Recovery | Recover overview | Preserve readiness and coverage wording; avoid unqualified disk claims |
| Guide | Manage guide, with context links from each intent | Preserve offline guidance and onboarding |
| Settings | Manage settings | Preserve destination, credential, update, and diagnostic access |

Legacy shell commands, tray entry points, onboarding steps, and saved `LastView` values use a compatibility adapter until their replacements are qualified. The adapter maps known keys explicitly; it must not use reflection or silently route an unknown detail to an unrelated page. Existing app or OS entry points need an inventory before any external deep-link scheme is added. No URL scheme is approved by this draft.

## Migration and removal gates

1. Add the typed route and adapter while rendering the current pages. Test every old key, startup fallback, tray/onboarding entry, stale resource, and repeated navigation.
2. Move one workflow family at a time behind the route coordinator. Keep existing actions available from their old page until the new path has the same underlying operation, error behavior, and recovery evidence.
3. Review each family on Windows, macOS, and Linux for keyboard order, focus restoration, screen-reader names, localization, dark/light/custom themes, 100/150/200 percent scaling, and narrow layouts. Test navigation during loading, refresh, deletion, cancellation, and an unfinished form.
4. Remove a legacy entry only after its parity checklist and compatibility window are recorded in its owning issue and release notes. Redirect supported saved routes; never remove the only way to perform an operation.

## Decisions still required for approval

- Choose the precise route type/API and state persistence boundary after a small adapter prototype.
- Inventory every app/OS deep-link and notification activation entry point, including packaged and unpackaged Windows behavior.
- Confirm which view state may survive a process restart and which must be discarded for privacy or safety.
- Record a family-by-family parity matrix and usability evidence before replacing the legacy shell.

VS-1910 remains In progress until these decisions are reviewed and the acceptance criteria in #501 have evidence. Destructive disk work remains governed by VS-1917–VS-1919 and the supported-system gate.

## Adapter prototype — 2026-10-07

`Infrastructure/DesktopRoute.cs` proposes a value with a version, typed intent and
typed page. `LegacyDesktopRouteAdapter` round-trips the eight exact existing page
keys through the mapping above. Unsupported versions, enum values and intent/page
pairs fail explicitly. A default zero-initialized route is invalid. Only
`RestoreLegacyLocation` permits a saved-location fallback to Protect overview;
request mapping never silently redirects unsupported input. Matching remains
ordinal and case-sensitive, as in the current page switch.

The prototype is pure and has no repository, device, credential or dispatcher
access. It intentionally has no resource fields, serializer, deep-link parsing,
back stack or operation-resume state. Resource resolution, typed identity schema,
versioned persistence and coordinator API still require joint review. Existing
`SetCurrentView`, tray/onboarding entry points, cached view models and `LastView`
persistence are unchanged. The adapter is not wired into live navigation yet.

Focused round-trip and refusal tests exercise compatibility and unsupported input;
they do not establish visual parity or the readiness of the four-intent UI.
This is the small adapter prototype requested by the decision gate above, not
architecture approval or a supported new navigation flow.
