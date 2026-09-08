# CatalogBridge — Γέφυρα Καταλόγου (Orexsys ↔ PointToOrder) — project brief for AI assistants

Read this before touching anything, then `README.md` (the detailed design notes — but see the
status note at its top: it predates the 01/09 price decision) and, for everything the two
tools share — Orexsys schema facts, the PointToOrder API and its traps, the customer's
business rules, safety rules — **`CLAUDE.md` in the sibling repo
`mavridis9/LIGHTHOUSE_TOOL`** (`D:\Claude\CustomerAdder\CLAUDE.md` on the owner's
machine). That file is authoritative for shared facts; this one is only what is specific
to the catalog bridge.

---

## 1. What this is

A standalone Windows app (WPF, .NET 6, Greek UI, title **Γέφυρα Καταλόγου**) that keeps the
**Orexsys product master** (Firebird: `ITMS`, `ITMC`, `PRLD`, `EFIC`, `SCRB`) and the
**PointToOrder online catalog** in step. Separate from ΔΙΑΧΕΙΡΙΣΗ/CustomerAdder on purpose:
own folder, own `settings.ini`, own DPAPI entropy — neither app can read the other's
credentials.

**What one press of Συγχρονισμός does (SyncView, current as of 01/09/2026 15:44):**
1. reads both catalogs and the EFIC correlations;
2. anything new in Orexsys (real products — not extras — active, priced) goes to a preview
   where category/product names are editable; **nothing is sent until the user confirms**;
3. **prices are never touched** — PointToOrder prices flow into Orexsys with each order;
4. products switched off at the POS but still selling online are switched off online;
5. everything is reported in plain language.

Only ID-matched (EFIC) pairs are ever auto-fixed; name-guessed pairs are shown, never
written from the one-button flow.

Other screens: **Πίνακας ελέγχου**; **Σύγκριση καταλόγων** (the diff: active-state
mismatches, hidden-online, one-side-only; both prices are *displayed* as information but do
not produce a diff status; CSV export for Greek Excel); **Αντιστοιχίσεις** (the EFIC editor:
ID-based health ⚠ orphan / ❓ in-extra, delete, re-point, map-unmatched via picker).
`StockSyncService` pushes `SCRB.REMAIN` as `stockLevel` every 5 minutes via `changeStatus`
(only EFIC-mapped items; stock checking switched off once when a counter disappears;
unchanged values skipped) — verified not to touch itemType, sellable or price. Ρυθμίσεις is
PIN-gated (`PinPromptWindow`).

Ships as one self-contained exe, built the same way as CustomerAdder. Owner: Stavros
Mavridis (`mavridis9`).

---

## 2. Rules this tool is built on (do not "fix" them)

1. **The IDs are the truth** (owner decision 01/09/2026). `ITMS.ITMSID` ↔ platform
   `offerId`, welded by `EFIC.CODE` (the offerId as a string). Names on the two sides are
   *expected* to differ — PointToOrder carries the customer-facing name chosen at push time —
   and never affect matching or health. A name-based "suspect" check was tried and removed.
2. **Prices are not compared and not synchronised** (owner decision 01/09/2026). When an
   order is placed, the platform's prices travel with it into Orexsys, so the platform price
   *is* the online price; drift is a pricing choice, not an error. `DiffStatus` has no price
   member. `PointToOrderApiClient.UpdateOfferPriceAsync` and its `TooRich` gate still exist
   in the client but **no screen calls them** — if price sync is ever re-enabled, keep that
   gate: a partial `SaveOfferRequest` blanks every omitted field (verified live), and the
   read cannot return photo / variants / translations / extra categories.
3. **Only ID-matched pairs are auto-fixed.** Name matches are suggestions («όνομα (?)»),
   and an ambiguous name (two platform products spelled the same) is left unmatched.
4. **Extras never participate** (`ITMC.ISEXTRA = 1`) — except an extra that somehow carries
   an EFIC code, which is a data anomaly and is shown rather than hidden.
5. **Write surface is closed.** PointToOrder: create category, create offer, `changeStatus`
   PATCH (itemType / sellable / stockLevel / isStockCheckEnabled), delete offer/category.
   Orexsys: `EFIC` insert/update/delete only (`EFICID` from the `efic_sq` trigger, deletes
   via Orexsys's `logdelete` audit). **Nothing else in the database is ever written.**
6. **`hierarchyLevel: "PARENT"` is required** on create; missing fields surface as `500`.
7. **New offers are born hidden** (`isSellable=false`); the flow flips them visible when
   asked. "Switch off" = `itemType=Inactive` **and** `sellable=false`. An item off at the
   POS but Orderable online is the one mismatch that loses money — it outranks everything.
8. **Prices for a push come from `PRLD.DELIVERYPRICE`** (`ITMS` has no price; `ITML` is
   empty; `0.00` = not set). `ITMS.USEINWEBORDER` is always 1 — never filter on it.
9. **Store ≠ database.** LIGHTHOUSE2.FDB pairs with store **1046**; OLLA.FDB (the sandbox)
   with **1007**. `EFIC.STOREID` is the platform code from `EFST.EFSTID` (`PO11521`,
   `PO1007`), *not* the numeric API store id; on a fresh install `EFST` is empty and the code
   is entered once in Ρυθμίσεις («Κωδ. EFIC»).
10. **Never test creates/deletes on 1046.** Everything destructive was verified on 1007 and
    the store left exactly as found.
11. If a push creates the platform offer but the EFIC insert fails, the two systems are out
    of step — reported louder than a plain failure, with the orphaned `offerId` named.

Shared safety rules (verify live; never insert synthetic `ORDM`; never write `PREP`; ask
before backfills; redact PII in screenshots; capture windows by handle) apply here too.

---

## 3. Build, test, release

```bash
dotnet build
dotnet test tests/CatalogBridge.Tests      # pure logic; must pass before a release
dotnet publish CatalogBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Release with `scripts/release.ps1 -Version X.Y.Z` (same gate as CustomerAdder: clean tree
on `master`, `<Version>` in csproj, `## [X.Y.Z]` in `CHANGELOG.md`, tests pass → publish →
`deploy/` → tag `vX.Y.Z` → push → GitHub Release with the exe). `settings.ini` is
git-ignored and excluded from publish. The sidebar shows the version.

---

## 4. Test environment

- `C:\Twinsoft\OLLA.FDB` + store 1007 (39 offers) — the sandbox this tool is configured
  for in `publish/settings.ini` (`PriceListId=1`, `EficStoreCode=PO1007`).
- `C:\Users\TwinSoft\Desktop\LIGHTHOUSE\LIGHTHOUSE2.FDB` + store 1046 — the customer's
  data; read-only for testing.
- PointToOrder credentials are in `settings.ini` only. Auth is username/password → JWT;
  API keys don't work.

---

## 5. Known state (08/09/2026)

- Last run with price sync still wired (01/09 15:20, store 1007): 39 = 39 products, no
  price changed, 3 offers refused as "rich" (photo/extras). Price sync was then removed by
  decision the same afternoon; those three are no longer the tool's concern.
- `README.md` sections on price differences describe the pre-01/09 behaviour.
- Tests (added 08/09) cover `CatalogDiff` matching, classification and `Normalise` only;
  `OrexsysCatalog`, `EficRepository`, the API client and `StockSyncService` are untested.
