# CatalogBridge — Γέφυρα Καταλόγου (Orexsys ↔ PointToOrder)

> **Status 08/09/2026.** Start with [CLAUDE.md](CLAUDE.md). This README's design notes are
> still right about matching, EFIC and where prices live in Orexsys, but **prices are no
> longer compared or synchronised** (owner decision 01/09/2026) — the «price differences»
> and «3 genuine price differences» below describe the earlier behaviour.

Standalone WPF tool (.NET 6, win-x64) that manages the link between the **Orexsys** product
master and the **PointToOrder** online catalog: compares them, pushes new products up, and
edits the `EFIC` correlations that tie the two together.

Separate from CustomerAdder: its own folder, its own `settings.ini`, its own DPAPI entropy.
Neither app can read the other's credentials.

## The three tabs

**Σύγκριση** — the diff. Price differences, per-side active state, and products present on
only one side. Bulk activate/deactivate via the verified `changeStatus` PATCH; CSV export
(UTF-8 BOM, `;` separated, opens cleanly in Greek Excel). Every row is labelled with how it
was matched: **κωδικό** (EFIC code — authoritative) or **όνομα (?)** (normalised name — only
a suggestion, used solely for products that have no EFIC link yet).

**The IDs are the truth.** `ITMSID` ↔ `offerId`, welded together by `EFIC.CODE`. Names on
the two sides are *expected* to differ (PointToOrder carries the customer-facing name chosen
at push time) and never affect matching or health. Renames on either side change nothing.

**Αποστολή** — the new-installation flow. Lists Orexsys products with no EFIC correlation;
selected products are pushed to PointToOrder: the platform category is created if missing
(matched to `ITMC` by normalised name), the offer is created with the `PRLD` price, and the
EFIC row linking `ITMSID` ↔ the new `offerId` is written — so external orders for the new
products correlate automatically from then on. **New offers are born hidden**
(`isSellable: false` is the platform's own default); the «Να γίνουν αμέσως ορατά» checkbox
flips them visible right after creation, otherwise you review first and activate from Σύγκριση.

**Αντιστοιχίσεις** — the EFIC editor. Every correlation with its live platform name and a
health flag (✓ / ❓ ύποπτη / ⚠ ορφανή — the platform product no longer exists). Three
operations: delete wrong rows, re-point a row at a different Orexsys product, and map a
still-unmatched platform product via a searchable picker. All three lean on Orexsys's own
`EFIC_AIUD_TRG`, which backfills unmapped `EFOD` order lines whenever a correlation is
inserted or fixed — repairing a mapping retroactively repairs pending external order lines.

## Write surface

| System | Writes |
|---|---|
| PointToOrder | create category, create offer, `changeStatus` PATCH, delete offer — all verified live 28/08/2026 on sandbox store 1007 (created, read back, deleted; store left exactly as found) |
| Orexsys | `EFIC` insert / update / delete only — `EFICID` comes from the `efic_sq` trigger, deletes go through Orexsys's own `logdelete` audit. **Nothing else in the database is ever written.** |

Create-endpoint facts hard-won from the live run: `hierarchyLevel: "PARENT"` is required in
practice (without it the server answers 500 INTERNAL_ERROR — nothing in this API is marked
required in the spec; missing fields surface as 500s, not validation errors), and both creates
return their new id directly (`categoryId` / `offerId`), which the catalog re-read confirms.

## EFIC facts the editor is built on

- `EFIC.STOREID` comes from `EFST.EFSTID` (e.g. `PO11521`). **On a fresh installation EFST is
  empty too** (verified on Stavros.FDB) — the code exists nowhere in the database, so the
  Ρυθμίσεις screen has an «Κωδ. EFIC» field for the one-time entry; when EFST has its row, the
  field can stay blank and the value is read automatically.
- New rows mirror the 435 live ones: `ITMVID = 0`, `CORRELATE = 1`, `CODE` = the platform
  `offerId` as a string, `NAME` = the platform product name (capped to its VARCHAR(200)).
- If a push creates the platform offer but the EFIC insert then fails, the two systems are out
  of step — that case is tracked separately and reported louder than a plain failure, with the
  orphaned `offerId` named so it can be mapped by hand in Αντιστοιχίσεις.

## Verified live, 28/08/2026 (store 1046, LIGHTHOUSE2)

**`EFIC.CODE` is the platform's `offerId`.** Confirmed against the real catalog: 431 of the
store's 450 offers have an `offerId` that appears as an `EFIC.CODE`, and 425 Orexsys products
matched by code (only 1 needed the name fallback).

**LIGHTHOUSE2 pairs with store `1046`, not `1007`.** CustomerAdder's `settings.ini` pairs that
database with `1007`, which is a different store carrying 35 offers. Copying that pairing here
would match almost nothing.

The offer JSON carries **no `code`, `externalId` or `sku` field at all** — `offerId` is the only
identifier the catalog returns, which is the concrete reason the `externalId` update path below
cannot work.

First real run against store 1046: 352 identical, **3 genuine price differences**,
**3 suspect mappings**, 68 matched products the POS prices at 0.00, 277 Orexsys-only,
24 platform-only (all of them already inactive and hidden).

### Why there is no name-based "suspect" check any more

An earlier version flagged code-matched pairs whose names shared no word («Ύποπτη
αντιστοίχιση») — it caught three genuinely stale rows in LIGHTHOUSE2's historical data
(`ΚΡΙΤΣΙ ΝΤΟΜΑΤ ΘΥΜ` → «Γαλοπούλα τυρί wrap»…), rows written by other software. It was
removed by explicit decision: this app records the correct `offerId` at the moment it
creates each offer, so its own rows are ID-correct by construction, and PointToOrder names
are now *deliberately* different from the POS names — a name heuristic would false-alarm on
every legitimately renamed product. Health in Αντιστοιχίσεις is ID-based only:
**⚠ Ορφανή** (the offerId no longer exists on the platform) and **❓ Σε extra** (the ITMSID
sits in an `ISEXTRA=1` category).

## Where the prices actually come from

`ITMS` has **no price column**. Prices live in **`PRLD`** (price-list detail), keyed by
`PRLSID` + `ITMSID`, with three separate columns:

- `RESTPRICE` — dine-in. Populated on only **5 of 703** rows on the live databases.
- `DELIVERYPRICE` — populated on **408**. **The default this tool compares against.**
- `TAKEAWAYPRICE` — populated on 408, and **equal to `DELIVERYPRICE` on all but 2 rows**.

`ITML` looks like the natural price table and is **empty on all three databases**
(LIGHTHOUSE2, LIGHTHOUSE, Stavros). There is exactly one price list: `PRLSID 1`
«Βασικός Τιμοκατάλογος».

A `PRLD` price of `0.00` is treated as **"no price set"**, not as "free" — 295 of the 703 rows
are extras and modifiers Orexsys leaves at zero, and comparing those against a real online
price would report 295 false price differences.

## Traps this tool already works around

- **`ITMS.USEINWEBORDER` is 1 on every row** on all three databases. It is a default, not a
  "should this be on the web menu" flag. Do not filter on it.
- **`EFIC.EXTID` and `EFIC.EXTCODE` are null on every row.** `EFIC.CODE` (e.g. `25348` for
  `ITMSID 224`) is the only usable mapping key.
- **A few items carry two EFIC codes** (7 on LIGHTHOUSE2) — size variants sold as separate
  platform products, so the mapping is one-to-many and the matcher never assumes one.
- **Ambiguous names are left unmatched** rather than paired arbitrarily. A wrong pairing would
  invent a price difference and invite someone to "fix" a price that was never wrong.
- **Activate/deactivate move `itemType` and `sellable` together.** `Inactive` alone leaves the
  product still listed (just unselectable), which is not what staff mean by "switch it off".
- The published OpenAPI spec's **`visible` field is ignored by the server**; the admin UI sends
  **`sellable`**. This tool sends `sellable`.

## Not built, and why

**Updating an existing offer's price is deliberately absent.** Both candidate endpoints remain
unsafe or dead:

- `PATCH /api/v1/stores/{storeId}/externalId/{externalId}` — the natural per-item update. Taken
  from the **test** host's spec (`admin.testgodev.nl`); never confirmed to exist on
  `admin.point2order.gr`. Known to be a dead end regardless: `EFIC.EXTID` is null on every
  row **and** the live catalog response carries no external-id field, so nothing on either side
  holds the id this endpoint addresses items by.
- `POST …/catalog/{catalogId}/offer/{offerId}` (`SaveOfferRequest`) — **known dangerous for
  partial updates**: no read endpoint returns all 28 write fields, so reconstructing the payload
  silently blanks option groups, schedules and external ids.

So a price change still goes through the PointToOrder admin panel by hand; Σύγκριση tells you
which products need it. Creating *new* offers via the same request shape is fine — a new offer
has no option groups or schedules to blank.

**Inserting products into Orexsys** (ITMS/PRLD) is out of scope for now by design: catalog
entry into Orexsys stays manual, this tool takes over from there.

## Build & publish

```
dotnet publish CatalogBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

`settings.ini` is excluded from the publish output (`CopyToPublishDirectory=Never`) so a fresh
deploy never inherits another site's database or API credentials — configure it once per
installation via the in-app Ρυθμίσεις screen.

## First run

1. **Ρυθμίσεις** → fill in the Firebird connection → **Έλεγχος βάσης** (this also populates the
   price-list dropdown).
2. Fill in the PointToOrder credentials → **Έλεγχος API**. It warns if the store id is not one
   the account can actually see — the most likely misconfiguration, and one that otherwise shows
   up later as "nothing matched" rather than as an error.
3. **Αποθήκευση**, then **Σύγκριση**.

`publish/settings.ini` already carries the verified configuration (LIGHTHOUSE2 + store **1046**,
price list 1, delivery prices). Only the two passwords are blank — enter them once in Ρυθμίσεις
and they are stored DPAPI-encrypted.

The status bar reports the match rate every run. **If nothing matched by code**, the two
catalogs were compared on names alone and every difference on screen should be checked by hand.
