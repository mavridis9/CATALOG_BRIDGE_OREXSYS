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
   Orexsys: `EFIC` insert/update/delete (`EFICID` from the `efic_sq` trigger, deletes
   via Orexsys's `logdelete` audit), and — only with «Αντιστοίχιση παραγγελιών με e-mail» on —
   `ALTER TRIGGER EFOM_BIUD_TRG` through `EfomTriggerGuard` (owner decision 21/09/2026), and —
   from the «Πελάτες» screen — `CUST` + `CUSP` INSERT for a new customer (never update/delete).
   And — «Ακύρωση παραγγελίας» in the customer's orders window (owner decision 22/09/2026) —
   `EXECUTE PROCEDURE CANCELORDER`, Orexsys's own cancellation, the same call ΔΙΑΧΕΙΡΙΣΗ makes.
   PointToOrder additionally: create customer, send invitation, create order (till orders →
   history), cancel order (admin `PUT status=CANCELLED`).
   **Nothing else in the database is ever written.**
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
- **1.0.1 (11/09/2026): EFIC reads are store-scoped.** `EficRepository.ResolveStoreCode`
  (Settings «Κωδ. EFIC», else `EFST`) filters every EFIC read — `OrexsysCatalog` links and
  `EficRepository.LoadAsync` (Αντιστοιχίσεις, stock sync). Found when Stavros.FDB was pointed
  at new store **1110** with `EficStoreCode` still `PO1007`: the leftover 01/09 `PO1007` row
  for ΠΡΟΙΟΝ (ITMSID 200) hid it from the push. Switching a database to another store means
  changing «Κωδ. EFIC» as well; `publish/settings.ini` now has `PO1110`.
- **1.1.0 (11/09/2026): category choice + P2O-only EFST fallback.** `Settings.IncludesCategory`
  (`SyncAllCategories`/`SyncCategoryIds`, picker `CategoryPickerWindow` over
  `OrexsysCategories`) filters what Συγχρονισμός/Αποστολή/Πίνακας send; deactivations stay
  category-blind. `ResolveStoreCode` falls back only to `EFST WHERE APPTYPE = 60 OR NAME
  CONTAINING 'P2O'`; an empty code matches no EFIC row (never Wolt 28 / efood 6). STOA.FDB
  (real customer, two businesses): price list 1 ΚΑΛΑΜΑΚΙ → 8 categories, list 2 ΣΤΟΑ → the rest.
- **EFIC write fences (1.1.0).** Existing EFIC rows that are not ours — Wolt, efood, other P2O
  stores — must never change. `EficRepository` enforces it (do not move these checks into
  callers): `RequireOwnStoreCode` (never empty, never a non-P2O `EFSTID`), `STOREID`-constrained
  UPDATE/DELETE with exact-row-count checks and rollback, per-database CSV backup of the whole
  table before the first write. Proof harness result 11/09: 25/25, fingerprint unchanged.
- **EFIC row shape (1.1.1).** An inserted row must look like one Orexsys makes for P2O (owner's
  reference, 11/09): `STOREID` = EFST code, `CODE` = offerId, `NAME` = `SALESNAME` = platform
  name, `ITMSID`, `ITMVID 0`, `CORRELATE 1`, everything else NULL, `EFICID`/`RECVER` from the
  trigger. Earlier rows have `SALESNAME` NULL (harmless: triggers use it only for code-less
  order lines); backfill only on the owner's say-so.
- **Sizes (1.1.1).** ITMV = one global size list; a product's sizes = its active `PRLD` rows
  with `ITMVID <> 0` (any list, as `GETITMSIZES`); `ITMVID 0` = its own price — every PRLD
  join must pin it (`PRLD_UNQ_IDX` = PRLSID+ITMSID+ITMVID). Orders get their size only from
  `EFIC.ITMVID` (`EFOD_BIUD_TRG`, `USEOREXSYSID = 0`). Two modes, owner decision 11/09
  (`Settings.SizeMode`): **SeparateItems** (built) — `PushRules.PendingUnits` yields one
  `PushUnit` per unlinked priced size (+ the product itself), each created as its own offer
  and EFIC row with its ITMVID; `OrexsysProduct.LinkSizes` (code → ITMVID) makes the diff
  pair every size offer. **OptionGroup** (built) — one offer at the cheapest price + a
  single-choice CHILD category «ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ» (`min 1`, `max 1`, `parentOfferId`) whose
  choices are offers with the surcharge as price (`CreateOptionGroupAsync`, verified live on
  1110 — nesting happens on create, no `associate` call needed); one EFIC row, ITMVID 0; the
  owner accepts the till gets the size only as `EFOD.OPTIONS` text. Nested option offers do
  not appear in the catalog listing, so the diff never sees them. A linked product is never
  given a group afterwards. Rule 5's write surface now includes CHILD category + CHILD offer
  creates. Demo/test store for P2O writes = 1110 only (owner, 11/09). Test DB with sizes:
  scratch gbak copy of Stavros.
- **«Σύνδεση P2O» dropdown (1.1.0).** Ρυθμίσεις no longer takes a typed EFIC code: it lists
  `EficRepository.LoadConnectionsAsync` rows flagged P2O (one EFST line = one connection),
  plus «καμία» and a flagged test code if one is saved. The path where a database *has* a P2O
  line is untested live — no available DB has one (LIGHTHOUSE2 copy is gone; EFST must never
  be written by this tool).
- **Reset (1.1.1, 21/09).** Ρυθμίσεις → «Επαναφορά από την αρχή» (`ResetWindow`, `ResetService`,
  `EficRepository.DeleteAllUnderCodesAsync`) deletes the WHOLE PointToOrder store (products,
  option groups, categories) and the P2O rows of EFIC, after a JSON backup of the catalogue and the
  usual EFIC CSV. Store 1046 is hard-refused in `ResetService.ProtectedStores`; Wolt/efood codes can
  never be selected or reached. Owner-requested so a demo can start clean.
- **E-mail order correlation (21/09).** Owner made customer login mandatory on P2O, so every
  order's JSON has `customerDetails.customerEmail`. E-MAIL ONLY (owner, 21/09): with an e-mail the
  phone is never used (marker `OREXSYS-EMAIL-ONLY-V2` + V1 kept for ΔΙΑΧΕΙΡΙΣΗ; V1 upgraded in place).
  `EfomTriggerGuard` (ported from
  ΔΙΑΧΕΙΡΙΣΗ, same marker/edits; plus `ReadState`, `Remove`/`TryRevert`) patches
  `EFOM_BIUD_TRG` when `Settings.EmailOrderCorrelation` is on (applied on save, re-checked at
  start). «Off» never removes — ΔΙΑΧΕΙΡΙΣΗ keeps the same patch on at LIGHTHOUSE and the two must
  not fight; removal is an explicit button. Needs Orexsys closed everywhere for the ALTER. Test
  fixture = the real vendor trigger (`tests/.../Fixtures/EFOM_BIUD_TRG_vendor.sql`).
- **«Πελάτες» (21/09).** Owner decision: ONLY the shop creates customers, once, for both systems
  (`CustomersView`, `NewCustomerWindow`, `CustomerDirectory`; `PointToOrderPhone`/`EmailValidator`
  ported from ΔΙΑΧΕΙΡΙΣΗ). Card → `CUST.CARDNO` and P2O `memberCode`; e-mail is the link (same
  upper+trim match as the trigger). Invitation only by button. The shop cannot edit a
  self-registered P2O account (403 «only he can change their details») — that is why accounts are
  created by the shop. Still open: showing TILL orders in the customer's P2O history —
  `external-order` is loyalty-only/invisible, admin `POST /api/v2/user/stores/{id}/orders`
  answers 500 in every shape tried; next idea = capture what the admin «Order Capture» screen sends.
- **Till orders → P2O history (22/09).** `TillOrderSender`: draft (public API) → items → customer +
  `externalOrderId OREXSYS-<ORDMID>` + comment «[OREXSYS#<ORDMID>]» → admin v2 PUT status. Only if
  IDENTICAL (EFIC-mapped products, same price, integer qty) else refused. `EfomTriggerGuard` gained the
  loop guard (`OREXSYS-LOOP-GUARD-V1`, edits D1/D2: tagged order → STATUS -1 on insert and on accept)
  because the first live send came back as till order #6 + a nameless customer. `ReadState.Patched`
  now requires e-mail-only AND loop guard; older = `PatchedOlder`, completed at next Run.
- **Once only (22/09).** PointToOrder overwrites `externalOrderId` with the token at submit; the
  comment tag «[OREXSYS#<id>]» is the only key that survives (`TillOrderSender.IsSentTillOrder`
  / `TagToExternalId`). `SendAsync` = single-flight + register (`SentFile`, shared by button and
  service) + full-history re-check + draft DELETE on failure before submit. Keep all three inside
  `SendAsync`, not in callers.
- **«Ασυμφωνίες παραγγελιών» (23/09).** `OrderMismatch.Compare` (pure) + `OrderMismatchService`
  (10′) + card on Αρχική: last 30 days, Orexsys side (`ORDM` + customer + EFOM.JSON token) vs
  platform side (`orders/search {createdAtFrom}` — every status, one paged call). Link order of
  trust: EFOM token → local registry → comment tag. Two kinds, one button each, user-confirmed,
  reusing `TillOrderSender.CancelOnPlatformAsync` / `OrderCancel.CancelInOrexsysAsync`. Read-only
  otherwise; needs the `orders` module present (expired may look, not act).
- **Licensing, phase 1 (22/09).** Two modules — `catalogue` (§1-A: sync, diff, EFIC, sizes, stock,
  reset) and `orders` (B + everything riding on it: trigger patch, Πελάτες, till orders → history,
  cancel). Design: `docs/LICENSING_AND_UPDATES.md`. Token = signed JSON (ECDSA P-256, public key
  embedded in `License.PublicKeys`, private key OUTSIDE the repo: `D:\Claude\CatalogBridge.keys\` on
  the owner's PC, issued with `tools/LicenceSigner`), bound to `MachineFingerprint` (MachineGuid +
  system-volume serial → `XXXX-XXXX-XXXX-XXXX-XXXX`), stored plain in `licence.dat` next to the exe,
  verified offline at every start. Rule: every write path calls `License.Blocked(module, out why)`
  first (Συγχρονισμός, push, EFIC repair/delete/map, reset, stock sync, trigger apply, till-order
  switch + service, customer create/send/card/invite, order send/cancel); expired = read-only +
  services stop; module absent = its nav hidden; **licence state never removes anything** (trigger
  patch stays for ΔΙΑΧΕΙΡΙΣΗ). The app does not call the server yet: renewal = a new token pasted
  in Ρυθμίσεις → «Άδεια χρήσης». `LicenseToken.cs` is pure and shared by app, tool and tests -
  never copy it inside this repo (the server repo keeps the one sanctioned copy, see next bullet).
- **Licensing, phase 2: the server (22/09).** Built in its own repo, `D:\Claude\Catalogue_Portal`
  (ASP.NET Core minimal API, .NET 8, SQLite; read its `CLAUDE.md`). Endpoints as design §5:
  `POST /v1/activate|heartbeat|deactivate`, `GET /v1/updates/{product}/{channel}` (404 until phase 3),
  `/admin/*` behind a bearer key, plus the `licence-admin` CLI (create, extend, modules, suspend,
  bind store, fleet, deactivate). It signs with the SAME key (`D:\Claude\CatalogBridge.keys\`, read
  from a configured path) and refuses to start unless it matches `License.PublicKeys[0]`, so its
  tokens verify here unchanged. It compiles a byte-identical copy of `LicenseToken.cs` guarded by a
  drift test: **a change to `LicenseToken.cs` here must be copied there in the same step.** Server
  rules the client must match when phase-2 client work starts: `valid_until = min(now + 30 d,
  licence_until)` (§4, owner 22/09); one active installation per key, second fingerprint → 409
  with the other activation's date; error codes in the JSON `error` field (`bad-key`,
  `unknown-licence`, `suspended`, `store-mismatch`, `licence-expired`, `not-activated`,
  `already-activated-elsewhere`, `rate-limited`) with the Greek text in `message`; rate limits
  activate 5/h per key, heartbeat 1/h per installation (429 + `Retry-After`); the fingerprint is
  sent as the app formats it, the token carries it dash-less (`Verify` already normalises). Not
  yet done here: the activate/heartbeat calls, the 20 h heartbeat schedule, «Αποδέσμευση».
- **Cancel in both platforms with one click (22/09).** «Ακύρωση παραγγελίας» in the customer's
  orders window: `OrderCancel.Decide(cancelledInOrexsys, platformStatus)` → None / OrexsysOnly /
  PlatformOnly / Both — only the side(s) still active are touched, so a second press does nothing.
  Orexsys side = `CANCELORDER` (ported from ΔΙΑΧΕΙΡΙΣΗ's DayClose.Cancel); platform side = admin
  `PUT status=CANCELLED` on the uuid. Two traps found live: (1) the platform's customer-history
  endpoint (`customers/orders`) silently DROPS cancelled orders, so it cannot tell «already
  cancelled there» from «never existed» — `orders/search` with `orderToken` finds every status;
  (2) the till-sent token therefore comes from the local registry (`LoadSentRegisterWithTokens`),
  never from the platform's history; the platform-native token comes from `EFOM.JSON
  orderDetails.orderNumber` (substring scan — the SQL CAST can truncate the JSON). Cancelled
  Orexsys orders are now SHOWN in the list (status «Ακυρωμένη») but still excluded from the totals.
