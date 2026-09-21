# Changelog

All notable changes to CatalogBridge (Γέφυρα Καταλόγου). Versions are semantic; each
released version is a git tag `vX.Y.Z` on `master` with the exe attached to the GitHub Release.

## [1.1.1] — 2026-09-15

- **EFIC rows now carry `SALESNAME`** (= `NAME`, the platform name), matching a row Orexsys
  itself creates for PointToOrder (owner's reference: `SALESNAME = NAME`, `ITMVID 0`,
  `CORRELATE 1`, everything else NULL). Order matching by `CODE` was never affected;
  `SALESNAME` is what Orexsys's triggers fall back on when an order line arrives without a
  usable code. Rows written by 1.0.x/1.1.0 have `SALESNAME` NULL — not backfilled.
  Proof harness on the STOA copy: 26/26, the inserted row checked field by field, Wolt/efood
  fingerprint unchanged.
- **Sizes as separate items (`SizeMode=SeparateItems`, the default).** A product with sizes
  goes out as one PointToOrder product per size — «ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ» 1,00 and «ΤΟΣΤ ΖΑΜΠΟΝ
  ΚΑΣΕΡΙ XL» 4,00 (names editable in the preview) — each with its own offerId and its own
  EFIC row carrying the size (`ITMVID`), which is what Orexsys's `EFOD_BIUD_TRG` copies onto
  the incoming order line: the till receives the size. The same pattern Wolt/efood sizes
  already use in customers' EFIC. Only unlinked sizes are pending, so a size added later is
  sent alone and what is online is left alone; a size with no price in the chosen list is
  skipped. Σύγκριση pairs every size offer with its product (one row per size, «ΤΟΣΤ (XL)»),
  so switching a product off at the till switches off all its sizes online; Αντιστοιχίσεις
  shows a «Μέγεθος» column and re-pointing keeps the size; Πίνακας ελέγχου counts per size.
  Ρυθμίσεις → «Προϊόντα με μεγέθη» chooses the mode. Verified on the scratch copy (11/11
  checks), with the EFIC proof harness (26/26) and live on demo 1110 (ΤΟΣΤ + ΤΟΣΤ XL, EFIC
  rows with ITMVID 0 and 17).
- **Sizes as one product with an option group (`SizeMode=OptionGroup`).** The product goes out
  once, at its cheapest price, with a single-choice group «ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ» whose choices are
  the sizes with the difference as surcharge (Κανονικό +0,00 · XL +3,00 — shown in the preview).
  One EFIC row, `ITMVID 0`: the price reaches the till right, the size only as text in
  `EFOD.OPTIONS`, not in the size field (owner's accepted trade-off, 11/09). Built on what the
  admin UI itself creates (read live on 1110): the group is a `CHILD` category with
  `min`/`max`/`parentOfferId`, each choice an offer inside it, all made visible with the parent
  (`PointToOrderApiClient.CreateOptionGroupAsync`). A product already online is left alone even
  if sizes are added later. Verified end to end on demo 1110, created and deleted again.
- **Fix: products with no price are no longer skipped in silence.** Seen 18/09 on Stavros: the
  four products of the chosen categories (ΣΑΛΑΤΑ, ΚΥΡΙΩΣ) had restaurant and take-away prices but
  Delivery 0,00; with «Τιμή» = Delivery they were skipped and Συγχρονισμός said «όλα
  συγχρονισμένα». Now `PushRules.IsUnpriced` finds every active, not-yet-sent product in a chosen
  category with no price in the configured list/column (own or any size): Συγχρονισμός names
  them («⚠ N ενεργά προϊόντα … ΔΕΝ στάλθηκαν γιατί δεν έχουν τιμή «Delivery» …») and, when
  nothing else is to do, says «Τίποτα δεν στάλθηκε» in orange instead of «όλα συγχρονισμένα»;
  Πίνακας ελέγχου adds the same warning. Prices are still never guessed from another column.
- **Fix: a link to a product deleted on PointToOrder no longer counts as "sent".** Seen on
  demo 1110: every product deleted in the P2O admin, and Συγχρονισμός still reported «όλα
  συγχρονισμένα» because the EFIC rows were still there. Now an EFIC code whose offer is not
  in the catalog is an orphan (`PushRules.DropOrphanLinks`): the product is offered again in
  Συγχρονισμός, Αποστολή and Πίνακας ελέγχου, and when it is re-sent its dead rows are removed
  first (`EficRepository.DeleteOrphanLinksAsync` — own store code, `STOREID` + `ITMSID` in the
  WHERE, exactly one row per code or rollback). The report says «♻ N προϊόντα είχαν διαγραφεί
  από το PointToOrder και στάλθηκαν ξανά — M παλιές αντιστοιχίσεις καθαρίστηκαν».
- **Sizes, step 1 — read them, hold them back.** Fix: `PRLD` is unique on
  `PRLSID+ITMSID+ITMVID`, and the catalog and category-picker joins ignored `ITMVID`, so a
  product with sizes was read once per size — pushed twice or three times, counted double.
  Both joins now take the product's own row (`ITMVID = 0`). Each product also carries its
  sizes (`OrexsysProduct.Sizes`: every active `PRLD` row with `ITMVID <> 0` in any price list,
  as Orexsys's `GETITMSIZES`; price from the configured list). Until size support is built,
  products with sizes are **not sent** (`PushRules.IsHeldForSizes`): Συγχρονισμός names them
  («⏸ … δεν στέλνονται ακόμα»), Αποστολή shows them with a «Μεγέθη» column and refuses them,
  Πίνακας ελέγχου does not count them. No database seen so far has sizes, so nothing changes
  for today's customers. Checked on a scratch copy of Stavros.FDB with test sizes: old query
  205 rows for 202 products, new 202; 8/8 checks; the Αποστολή screen refused ΠΡΟΙΟΝ 2.

## [1.1.0] — 2026-09-11

- **Επιλογή κατηγοριών.** Ρυθμίσεις → «Κατηγορίες προς συγχρονισμό»: the technician picks
  which Orexsys categories the sync sends (`SyncAllCategories` / `SyncCategoryIds`; "all"
  also covers categories added later, «Καμία» sends nothing new). The picker shows, per
  category, how many active products have a price in the chosen list — so a wrong price
  list is visible at once (STOA.FDB serves two businesses: ΣΤΟΑ is priced only in list 2).
  Governs what is *sent*; linked products switched off at the till are still switched off
  online in every category. Applied to Συγχρονισμός, Αποστολή and Πίνακας ελέγχου.
- **Fix (safety): the EFST fallback only picks the PointToOrder row** (`APPTYPE 60`). EFIC is
  shared by every platform: STOA.FDB has live Wolt (`APPTYPE 28`, 211 rows) and efood (`6`,
  233 rows) correlations and no P2O row, and the old fallback took the first EFST row —
  Wolt. With no P2O code configured, no EFIC row now counts as ours.
- **EFIC write fences — existing rows (Wolt, efood, other stores) cannot be touched.** All
  enforced inside `EficRepository`, not trusted to callers: writes only under the resolved
  P2O code (never empty, never another platform's `EFSTID` — also when typed into «Κωδ.
  EFIC»); every UPDATE/DELETE is constrained by `STOREID` as well as `EFICID` and must hit
  exactly the rows asked for, else it is rolled back (`EficSafetyException`, Greek message);
  the whole EFIC table is saved to `backups\EFIC_<database>_<time>.csv` before the first
  write of a session, and nothing is written if that fails. Proven on the STOA copy: 18
  attempts on Wolt/efood rows refused, normal insert/repair/delete worked, whole-table
  SHA-256 identical before and after.
- **«Σύνδεση P2O» dropdown replaces the free-text «Κωδ. EFIC».** One EFST line = one
  platform connection; the dropdown lists only the PointToOrder lines (`APPTYPE 60`) — never
  Wolt or efood — plus «— καμία —» (nothing written to EFIC) and, if one is already saved, a
  code with no EFST line, flagged «μόνο για δοκιμές» (the sandbox stores). Filled from the
  database when Ρυθμίσεις opens and on «Έλεγχος βάσης»; with exactly one P2O line it is
  pre-selected. The hint under it follows the selection and names the connections the
  database does have (STOA: «υπάρχουν μόνο: WOLT, EFOOD»).
- **Fix: EFIC links are now scoped to the configured store.** Every EFIC read (the
  Συγχρονισμός candidate list, Πίνακας ελέγχου, Αντιστοιχίσεις and the stock sync) filters by
  `EFIC.STOREID` = «Κωδ. EFIC» (or `EFST` when blank). Before, a link to *another* platform
  store made a product look already sent: Stavros.FDB, moved from the 1007 sandbox to store
  1110, kept its `PO1007` row for ΠΡΟΙΟΝ, so the first 1110 sync offered only ΠΡΟΙΟΝ 2.
  When pointing a database at a new store, set «Κωδ. EFIC» to that store's code too.

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
