# UI localization contract

Every new or changed application-authored string visible to a user must use a
localization resource in the same change. This covers desktop pages, startup
states, dialogs, confirmations, validation, notifications, tooltips, placeholders,
accessibility names/help, tray/widget UI, installer/update outcomes and labels
constructed in C# or supplied by Core results.

Use `infra:LocalizedString Key=...` in AXAML and the existing localization helpers
in C#. Add the English key and a real translation in every maintained locale:
`ar`, `bn`, `de`, `en`, `es`, `fr`, `hi`, `id`, `it`, `ja`, `ko`, `nl`, `pl`, `pt`,
`ru`, `tr`, `uk`, `vi`, `zh`. The language picker owns the supported inventory.
Do not count an English fallback or a copied paragraph as completed translation.
Missing keys currently render their key name; null-coalescing alone does not
supply a fallback for that condition.

Keep whole sentences in resources with numbered placeholders. Preserve argument
identity, repetitions and numeric/date formats; translations may reorder them.
Avoid constructing a sentence by concatenating translated fragments. Format with
the selected culture. Give distinct outcomes distinct keys: update restart,
completed installation and installer handoff are different user instructions.

Localize at the presentation boundary. Store stable protocol/status identifiers,
paths, project/user-entered names, checksums and original technical evidence
unchanged; translate surrounding labels and explanations. Never rewrite historical
recovery evidence or user data to change its display language. Raw OS/tool errors
and technical evidence payloads retain their recorded wording; they are distinct
from newly authored UI instructions. Product names, glyphs, format tokens such as
`HH:mm`, file-format badges and international units can be invariant when reviewed.

Cached view models must reapply translations when the language changes without
resetting filters, search, user edits or operations. Build displayed messages from
typed results on the UI thread so an older background result cannot reintroduce
strings formatted in the previous language. AXAML bindings to computed localized
properties must notify changes. Use flexible layouts for translated lengths and
inspect long strings, accessibility labels and RTL languages on affected screens.

## Required checks

```bash
python3 scripts/localization_audit.py --check
bash scripts/validate_development.sh release
```

The first check fails on missing/extra/duplicate/blank/non-string locale entries,
invalid English entries, changed placeholder signatures, missing language files
advertised by the picker, unresolved static UI resource references and fixed text
in recognized AXAML/C# UI properties. It runs in local contracts/Release checks,
PR quality, required Linux CI and the release-assets quality gate before packaging. Reusing a qualified source run
for draft assembly also compares `Localization/` inputs; translation-only changes
require rebuilding its packages.
A failed localization check must be repaired before pushing release-ready work.

`python3 scripts/localization_audit.py --json` provides the detailed audit.
Exact-English matches are review candidates: common words and technical/product
names can legitimately match. The static scanner does not prove translation
quality, discover every indirect/dynamic string, interpret all C# expressions or
replace UI review. Review interpolated resource keys, Core-to-UI message paths,
markup setters, errors and accessibility explicitly; never silence a failure
using an English fallback or a broad allowlist. Narrow invariants need a reason.

## 1.9.0 audit — 2026-10-13

BUG-19030/#785 repairs two missing referenced keys, initial Guide/Recovery text,
coverage windows, Recovery headline/count localization and distinct updater
completion outcomes. Nine new keys have translations in all 19 locales. Guide
and Recovery cached presentation reapply on language changes; Recovery formats
coverage and recommendations at application time. The audit retains historical
technical payloads and intentional identical terminology, and does not claim
native-speaker review or complete rendering qualification.
