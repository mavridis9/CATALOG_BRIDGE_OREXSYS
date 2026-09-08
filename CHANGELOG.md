# Changelog

All notable changes to CatalogBridge (Γέφυρα Καταλόγου). Versions are semantic; each
released version is a git tag `vX.Y.Z` on `master` with the exe attached to the GitHub Release.

## [1.0.0] — 2026-09-08

First versioned release; the tool has been in use since 28/08/2026 without a version.

- **Συγχρονισμός** (one button) — pushes new Orexsys products to the platform after an
  editable preview (category created if missing, offer created with the PRLD price, EFIC
  row written, made visible on request) and switches off online whatever is inactive at
  the till. **Prices are never touched** — decision of 01/09/2026: the platform price travels
  into Orexsys with each order, so drift is a pricing choice, not an error.
- **Σύγκριση** — diff of Orexsys vs PointToOrder by EFIC code (authoritative) or normalised
  name (suggestion only): active-state mismatches, hidden-online, one-side-only; both prices
  shown for information; bulk activate/deactivate; CSV export for Greek Excel.
- **Αντιστοιχίσεις** — EFIC editor with ID-based health (orphan / in-extra), delete,
  re-point, map-unmatched via picker; leans on Orexsys's `EFIC_AIUD_TRG` backfill.
- **Stock sync** — `SCRB.REMAIN` → `stockLevel` via `changeStatus` every 5 minutes for
  EFIC-mapped items.
- PIN-gated settings; per-app DPAPI entropy.
- Retained but unused: `UpdateOfferPriceAsync` with its `TooRich` gate (see `CLAUDE.md` §2).
- Project: `CLAUDE.md`, `.gitignore`, MSTest tests for `CatalogDiff`, `scripts/release.ps1`,
  version shown in the sidebar.
