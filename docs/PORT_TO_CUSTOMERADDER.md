# Port to ΔΙΑΧΕΙΡΙΣΗ (CustomerAdder): order checks + cancel in both, and till orders → PointToOrder

**Written for:** a Claude session working in `D:\Claude\CustomerAdder` (repo `mavridis9/LIGHTHOUSE_TOOL`).
**Source of truth:** Γέφυρα Καταλόγου (CatalogBridge), `D:\Claude\CatalogBridge`, branch `sizes`,
commit `a360926` (29/09/2026). Everything below was built and verified there first.
**Author:** the CatalogBridge session, 30/09/2026, at the owner's request.

---

## 0. Read before anything else

1. `D:\Claude\CustomerAdder\CLAUDE.md` — all of it. Its §5 hard rules override convenience;
   its §9 row **#2** («Cancelling an order in Orexsys also cancels it in PointToOrder») is
   exactly Feature A of this document and is still marked *Open* there.
2. `D:\Claude\CatalogBridge\CLAUDE.md` — §2 rules and the §5 bullets from *«E-mail order
   correlation (21/09)»* down to *«A price list per category»*. They record every trap below
   as it was found live.
3. The CatalogBridge files listed in §3 and §4 of this document — **read the code, don't
   re-derive it**. The comments name the date and the evidence for every non-obvious line.

**Do not change CatalogBridge** from the CustomerAdder session. If something there is wrong,
say so to the owner; it is fixed in its own repo.

---

## 1. What to build

| | Feature | What the user sees |
|---|---|---|
| **A** | **Order checks + cancel in both** | On Αρχική, a card «Ασυμφωνίες παραγγελιών», refreshed every 5′: orders cancelled on one side only (with a button that cancels the other side), same order with a different amount, online orders that never reached Orexsys, and per-customer totals that differ between the two systems. In the customer's order history, a button «Ακύρωση παραγγελίας» that cancels in Orexsys and/or PointToOrder — only the side still active. |
| **B** | **Till orders → PointToOrder** | A till order of a customer who has a PointToOrder account appears in that customer's PointToOrder history, identical in total, exactly once — by a button in the customer's order history, or automatically (switch on Αρχική, every 2′). |

Build **A first**. It is read-mostly, it answers the customer's request #2, and B's safety
checks reuse A's code. B needs a trigger change and writes orders to the live store; it
comes second, after the owner's decisions in §6.

---

## 2. Ground rules for this port

- **Branch:** create `feature/order-sync` in CustomerAdder. Merge to `master` only on the
  owner's explicit approval (CustomerAdder §5.5).
- **Test data:** `C:\Orexsys\Stavros.FDB` + PointToOrder **demo store 1110** (EFIC code
  `PO4897`) — the same pair CatalogBridge was verified on. **Never write to store 1046
  (LIGHTHOUSE, live) or LIGHTHOUSE2.FDB while testing.** Reads from 1046 are fine.
- **Harnesses:** compile the production files into the harness (CustomerAdder §5.10). The
  CatalogBridge verification harnesses read `settings.ini` and decrypt with DPAPI entropy —
  CustomerAdder has its own entropy; reuse CustomerAdder's `Settings.Load()` instead.
- **Every destructive action asks first, default No** (CustomerAdder §5.9): cancel, send,
  and the first creation of the placeholder products.
- **The app never inserts ORDM/ORDD** (CustomerAdder §5.2). Nothing in this port does; if a
  design ever seems to need it, stop and ask.
- **Copy, then adapt.** Namespaces change from `CatalogBridge` to CustomerAdder's; keep the
  comments (with their dates) so the next reader knows why a line exists. Put a one-line
  header in every ported file: `// Ported from CatalogBridge <file> @ a360926 (30/09/2026).`
- **Tests:** port the matching CatalogBridge tests into `tests/CustomerAdder.Tests` and keep
  them green; `dotnet test` must pass before any exe is published.
- **Release:** CHANGELOG entry, `<Version>` bump, `scripts/release.ps1`, previous exe kept
  beside the new one in `deploy/` (CustomerAdder §5.6).

---

## 3. Feature A — order checks + cancel in both

### 3.1 Source files (CatalogBridge)

| File | Lines | Take | Notes |
|---|---:|---|---|
| `OrderCancel.cs` | 63 | `Decide` + `CancelSide` enum | **Do not port `CancelInOrexsys`** — CustomerAdder already has the same call in `DayClose.Cancel` (it is where CatalogBridge copied it from). Port only `FitRemarks`/`RemarksMaxLength` into `DayClose` (see trap T6). |
| `OrderMismatch.cs` | 328 | all | The pure `Compare` / `CompareCustomers` and the three loaders. |
| `OrderMismatchService.cs` | 73 | all | DispatcherTimer, 5′, raises `Checked(report, status)`. |
| `TillOrderSender.cs` | 435 | only: `PlatformOrderSummary`, `FindOrderByTokenAsync`, `CancelOnPlatformAsync`, `TagToExternalId`, `LoadSentRegisterWithTokens`, `SentFile` | The rest belongs to Feature B. Put these in a new `PlatformOrders.cs` for now; B moves them where they belong. |
| `CustomerOrdersWindow.xaml(.cs)` | 251 | the cancel part: `PlatformTokenFor`, `CancelDecisionFor`, `CancelOrderButton_Click`, the history/`byToken` loading in `LoadAsync` | Goes into CustomerAdder's **`OrderHistoryWindow`**. |
| `CustomerDirectory.cs` | — | only `LoadOrdersAsync`'s query shape and `CustomerOrders` totals | CustomerAdder's `OrderHistoryWindow` has its own query; add to it (see 3.4), don't replace it. |
| `SyncView.xaml(.cs)` | — | the card «Ασυμφωνίες παραγγελιών» (markup + `ShowMismatches`, `MismatchFix_Click`, `CustomerTotalsOpen_Click`, `MismatchCheckButton_Click`) | Goes into CustomerAdder's **`DashboardView`**. |
| `tests/.../OrderCancelTests.cs`, `OrderMismatchTests.cs` | — | all | |

### 3.2 API client additions (CustomerAdder `PointToOrderApiClient`)

CustomerAdder's client has no raw calls. Add, copied from CatalogBridge's client
(`ProbePostAsync` l.261, `ProbeGetAsync` l.269, `ProbePutAsync` l.448, `ProbeDeleteAsync`
l.456, all going through the existing `SendAuthorizedAsync` so the JWT refresh stays in one
place):

```csharp
public string StoreId => storeId;
public Task<(int Status, string Body)> ProbePostAsync(string path, object payload);
public Task<(int Status, string Body)> ProbeGetAsync(string path);
public Task<(int Status, string Body)> ProbePutAsync(string path, object payload);
public Task<(int Status, string Body)> ProbeDeleteAsync(string path);   // Feature B only
```

The endpoints Feature A uses — all verified on store 1110:

| Purpose | Call | Notes |
|---|---|---|
| Every order since a date, **any status** | `POST /api/v1/stores/{id}/orders/search?page=N&size=100` body `{"createdAtFrom":"yyyy-MM-ddTHH:mm:ssZ"}` | Response root: `data[]`, `totalPages`. Fields used: `uuid`, `orderToken`, `status`, `comment`, `customerUserId`, `customerName`, `totalDiscountedPrice`, `createdAt`. Drafts have no `orderToken`. |
| One order by token, any status | same endpoint, body `{"orderToken":"AB-12"}`, `size=1` | |
| Cancel an order | `PUT /api/v2/user/stores/{id}/orders/{uuid}` body `{"status":"CANCELLED","keysToUpdate":["status"]}` | Admin call. 200 → gone from the customer's history. |
| All customers (linking) | CustomerAdder already has what it needs — see 3.5 | |

### 3.3 The rules the code implements (keep them exactly)

**Decide** (`OrderCancel.Decide(cancelledInOrexsys, platformStatus)`), where
`platformStatus` is `null` when no PointToOrder order is known for this Orexsys order:

| Orexsys | PointToOrder | Result |
|---|---|---|
| active | active | `Both` |
| cancelled | active | `PlatformOnly` |
| active | CANCELLED / DELETED | `OrexsysOnly` |
| active | none known | `OrexsysOnly` |
| cancelled | cancelled / none | `None` (button disabled) |
| any | `""` (token known, status unreadable) | treat as **active** — never assume cancelled |

*Cancelled in Orexsys* = `CANCELED = 1 OR TOTAL = 0` (the rule every Orexsys reader in both
apps uses). Pressing the button twice does nothing the second time; a failure half-way
reports what *did* happen and the next press finishes the rest.

**Linking an Orexsys order to its PointToOrder order**, in order of trust:
1. an order that **arrived from** PointToOrder: its token is **`EFOM.ORDERID`** of the EFOM
   row with that `ORDMID`;
2. a till order **sent** to PointToOrder (Feature B): the local registry
   `sent_till_orders.txt` (`ORDMID<TAB>token<TAB>date`);
3. failing both: the platform order's comment tag `[OREXSYS#<ORDMID>]`.

**Mismatch kinds** (last 30 days, `OrderMismatch.Window`):

| Kind | Condition | Button |
|---|---|---|
| `CancelledInOrexsysActiveOnPlatform` | linked, Orexsys cancelled, platform not | «Ακύρωση στο PointToOrder» |
| `CancelledOnPlatformActiveInOrexsys` | linked, platform CANCELLED/DELETED, Orexsys not | «Ακύρωση στο Orexsys» |
| `DifferentAmount` | linked, both active, `ORDM.TOTAL ≠ totalDiscountedPrice` | none |
| `PlatformOrderMissingInOrexsys` | platform order not draft/cancelled, **older than 15′** (`ConnectorSlack`), no Orexsys order claims it, and either **no EFOM row** with that `ORDERID` or an EFOM row **without ORDMID** (e.g. `STATUS -1`). Tagged copies (`[OREXSYS#…]`) are excluded. | none — **the app must never invent an ORDM**; this is the connector's problem |

**Per-customer totals** (`CompareCustomers`): for every customer that exists on **both**
sides, count and sum of *active* orders in the window, Orexsys vs PointToOrder; report only
customers where they differ, and name the orders that explain it
(«#20 12,00 μόνο στο Orexsys», «AB-12 9,50 μόνο στο PointToOrder», «#10/AB-12: 14,56 ≠ 14,00»).
Customers that exist only in Orexsys are never checked.

### 3.4 Where it goes in CustomerAdder

- **`DashboardView`** (Αρχική): a card «Ασυμφωνίες παραγγελιών» with a status dot, the
  status line, «Έλεγχος τώρα», and two `DataGrid`s (orders; per-customer totals) that hide
  when empty. `ShellWindow` owns the `OrderMismatchService` (so it runs on every screen) the
  way CatalogBridge's `MainWindow` owns it, and starts it only when the database and the API
  are configured and `Settings.ApiEnabled` is on.
  Mind CustomerAdder's traps: `MinWidth` on fixed DataGrid columns; highlight triggers on
  `DataGridCell`; ownerless `MessageBox.Show` from a `UserControl`.
- **`OrderHistoryWindow`**: add a column «PointToOrder» (`από P2O` / `✓ AB-12` /
  `(ακυρωμένη)` / empty) and the button «Ακύρωση παραγγελίας...». Show cancelled Orexsys
  orders in the list (status «Ακυρωμένη») but keep them out of count/total/average — the way
  CatalogBridge's `CustomerOrders` does it. Check first how `OrderHistoryWindow` builds its
  list today and extend that query with `(SELECT FIRST 1 e.ORDERID FROM EFOM e WHERE e.ORDMID = m.ORDMID) AS TOKEN`.
- **Orexsys side of the cancel:** call the existing `DayClose.Cancel(connection, transaction,
  ordmId, DayClose.AdministratorUsrsId, remarks)` inside one transaction, the way
  `DayCloseWindow.RunOnOrphan` already does, and log it the same way.
- **Confirmation text** — keep CatalogBridge's: it names the order, the amount, the items,
  *which side(s)* will be cancelled, and for the Orexsys side says «Γίνεται όπως στο ταμείο:
  αντιστρέφονται τα είδη και οι πληρωμές και εκδίδεται ακυρωτικό παραστατικό. ΔΕΝ αναιρείται.»

### 3.5 Linking customers — use CustomerAdder's rule, not CatalogBridge's

CatalogBridge links a customer to a PointToOrder account **by e-mail only** (upper + trim).
CustomerAdder links by **e-mail or card (`memberCode`)** — see `FindExistingAsync(email,
memberCode)`. For `CompareCustomers`' `LinkedCustomer(Orexsys, PlatformId)` use
**CustomerAdder's own pairing**, the one `CustomersWindow` shows, so the two screens never
disagree about who is linked. You will need a "list all platform customers" call:
`POST /api/v1/stores/{id}/customers/search-all?page=N&size=100` body
`{"customerType":"ALL"}` (plain `/customers/search` answers 500 — see CustomerAdder §7);
copy `GetCustomersAsync` from CatalogBridge's client (l.290). Customers with no `id`
(unidentified guests) are skipped.

### 3.6 Tests to port

`OrderCancelTests` (all 8 rows of the table in 3.3 + the 50-char cap) and
`OrderMismatchTests` (21 cases: the four kinds, the three link sources, drafts, the 15′
slack, tagged copies, per-customer totals, other customers not leaking in). Adapt the
`PlatformOrderSide`/`OrexsysOrderSide` constructors only; keep the assertions.

### 3.7 Verification (demo store 1110 + Stavros.FDB only)

Stavros.FDB/1110 already contains real cases from 22–23/09 (customer «Stavros Mavridis»,
CUSTID 2202 ↔ P2O 1003; «Stratos», CUSTID 2200 ↔ 1597):
1. Read-only run of `FindAsync` — expect: 14 platform orders of 18–21/09 «never brought»
   (they predate the fresh database), 2 rejected (M6-2E, 32-5P), and Stratos's totals off by
   −104,00 (duplicates H9-P2 and 5H-KZ from CatalogBridge's tests). If CatalogBridge's owner
   has cleaned those up meanwhile, the numbers are lower — that is fine.
2. Place a new order on 1110 as Stavros, let it reach Orexsys, cancel it at the till →
   within 5′ the card shows «Ακυρωμένη στο Orexsys · ενεργή στο PointToOrder» → the button
   cancels it on 1110 → the row disappears at the next check.
3. The reverse: a new order cancelled in the PointToOrder admin → «Ακύρωση στο Orexsys» →
   `CANCELORDER` runs → gone.
4. «Ακύρωση παραγγελίας» from `OrderHistoryWindow` on an order active on both sides.

---

## 4. Feature B — till orders → PointToOrder

### 4.1 Source files (CatalogBridge)

| File | Take |
|---|---|
| `TillOrderSender.cs` (435) | all: `Line`, `PlatformLine`, `Plan`, `BuildPlatformLines`, `LoadOrderAsync`, `LoadOffersAsync`, `EnsurePlaceholdersAsync`, `SendAsync`/`SendCoreAsync`, the register, `LoadSentAsync`, `IsSentTillOrder`, `AlreadySentException` |
| `TillOrderSyncService.cs` (192) | all |
| `EfomTriggerGuard.cs` (740) | **only the loop guard**: `MarkerLoopGuard`, `SentOrderTag`, `AnchorInsertBlock`/`ReplInsertBlock`, `AnchorUpdateBlock`/`ReplUpdateBlock`, `TryAddLoopGuard`, `TryRemoveLoopGuard`, and a `ReadState` (see 4.4) |
| `SyncView.xaml(.cs)` | the card «Παραγγελίες ταμείου → PointToOrder» (switch, dot, status) → `DashboardView` |
| `CustomerOrdersWindow.xaml.cs` | `SendButton_Click` → `OrderHistoryWindow` |
| `EficRepository.ResolveStoreCode` | the EFIC store-code rule (settings override, else `EFST WHERE APPTYPE = 60 OR NAME CONTAINING 'P2O'`, never Wolt/efood). CustomerAdder never reads EFIC today; `LoadOffersAsync` needs it. |
| tests: `TillOrderSenderTests` (9), `TillOrderOnceTests` (5), the loop-guard cases of `EfomTriggerGuardTests` + the fixture `Fixtures/EFOM_BIUD_TRG_vendor.sql` | Skip the e-mail-only (V2) cases — see 4.4. |

### 4.2 API client additions

Besides 3.2: `CreateCategoryAsync(catalogId, name)` and `CreateOfferAsync(catalogId,
categoryId, name, price)` from CatalogBridge's client (l.240, l.466 — `hierarchyLevel:
"PARENT"` is required, missing fields answer 500), and `CatalogSnapshot.Categories`
(CustomerAdder's snapshot has offers only). `SetOfferStatusAsync` already exists.

### 4.3 The send, step by step (verified on 1110, 22/09/2026)

| # | Call | Body |
|---|---|---|
| 1 | `POST /api/v1/stores/{id}/orders` | `{storeId, deliveryMethod:"NO_LOCATION", uiLanguageLocale:"el", catalogLanguageLocale:"el", orderItems:[]}` → `uuid` (a DRAFT) |
| 2 | `PUT /api/v1/stores/{id}/orders/{uuid}/orderItems` per line | `{offerId, quantity, storeId, uuid:<new guid>}` |
| 3 | `PUT /api/v1/stores/{id}/orders/{uuid}` | `{customerUserId, customerName, externalOrderId:"OREXSYS-<ORDMID>", deliveryMethod:"NO_LOCATION", comment:"[OREXSYS#<ORDMID>] Αγορά στο κατάστημα dd/MM/yyyy HH:mm: <items>"}` |
| 4 | `PUT /api/v2/user/stores/{id}/orders/{uuid}` | `{status:"SUBMITTED", keysToUpdate:["status"]}` — **status only** |
| 5 | `GET /api/v1/stores/{id}/orders/{uuid}?locale=el&simplified=false` | read back `orderToken`, `status`, `totalDiscountedPrice`; must equal the till total |

The store auto-accepts → CLOSED, in the customer's history.

**Identical or nothing** (`BuildPlatformLines`): a product that is on PointToOrder (EFIC
link) goes as itself, and must have the **same price** and an **integer quantity** — else
the whole order is refused and logged with the reason. Products not on PointToOrder ride on
two hidden placeholders: «Αγορά στο κατάστημα» 1,00 € × euros and «Αγορά στο κατάστημα
(λεπτά)» 0,01 € × cents, in the hidden category «ΚΑΤΑΣΤΗΜΑ», created on first need,
`isSellable=false` + `itemType=Inactive` (still orderable by the app, invisible to
customers). The comment carries the real item names.

**Exactly once** — three fences, all *inside* `SendAsync`, never in callers:
1. one send at a time in the process (`SemaphoreSlim`);
2. right before creating anything: the local register, then the platform (by comment tag —
   see T2); either one → `AlreadySentException`, nothing created;
3. any failure after the draft exists and before submit **deletes the draft**
   (`DELETE /api/v1/stores/{id}/orders/{uuid}`); a failure after submit (e.g. the total
   read-back) is **recorded as sent**, because it is on the platform.

**Automatic** (`TillOrderSyncService`): every 2′, closed (`STS 4`), not cancelled, `TOTAL >
0` till orders since the day the switch was turned on, for customers with a PointToOrder
account, not from the platform (no EFOM row). Refused orders are logged once per session.
It refuses to run while the loop guard is not in the trigger.

### 4.4 The trigger: loop guard — and what NOT to change

A till order sent to PointToOrder **comes straight back** through the Orexsys connector as a
new online order. Seen live on 22/09: it became till order #6 plus a nameless customer
(admin-created orders carry no e-mail). The loop guard, two edits in `EFOM_BIUD_TRG`, marks
any order whose JSON contains `[OREXSYS#` as `STATUS -1` on insert and whenever the
connector tries to accept it (the connector copies the comment into `orderGeneralNotes`).

**Compatibility with CatalogBridge — keep these strings byte-identical**, so each app
recognises the other's patch on the same database:

```csharp
public const string MarkerLoopGuard = "OREXSYS-LOOP-GUARD-V1";
public const string SentOrderTag    = "[OREXSYS#";
```

**Do NOT port CatalogBridge's e-mail-only upgrade** (`OREXSYS-EMAIL-ONLY-V2`,
`TryUpgradeToEmailOnly`). CatalogBridge's owner made his store e-mail-only; LIGHTHOUSE runs
CustomerAdder's V1 (e-mail, then phone fallback) and CustomerAdder's CLAUDE.md §7 still
relies on it. CustomerAdder's `ReadState` therefore means: **V1 present + loop guard present
= Patched**. Add the loop guard with its own backup and exactly-once anchored edits, the way
`EfomTriggerGuard.Run` already works; `Remove` stays an explicit button.

> **Know this:** if CatalogBridge's «Αντιστοίχιση παραγγελιών με e-mail» is ever switched on
> against the LIGHTHOUSE database, CatalogBridge will upgrade V1 to e-mail-only on its own
> (that is its design). Tell the owner; it is his decision, not something to code around.

### 4.5 Verification (1110 + Stavros.FDB)

1. Guard: run the loop-guard transform against the vendor fixture and against a **gbak copy**
   of Stavros.FDB — never the real file first. Then the real Stavros.FDB with Orexsys closed.
2. Send one till order by the button → token in the customer's history on 1110 → within a
   minute the connector brings it back as EFOM `STATUS -1`, **no new ORDM, no new CUST**.
3. Press the button again → «έχει ήδη σταλεί … (AB-12)».
4. A till order with a product priced differently online → refused, reason logged.
5. Automatic switch on → a new closed till order is sent within 2′, once.
6. Feature A's card stays clean for these orders (they are linked through the register).

---

## 5. Traps already paid for — do not rediscover them

| # | Trap | Consequence / fix |
|---|---|---|
| T1 | `POST /api/v1/stores/{id}/customers/orders` (customer history) **silently drops CANCELLED/DELETED orders** | It cannot tell «cancelled there» from «never existed». Use `orders/search` for anything status-related. |
| T2 | PointToOrder **overwrites `externalOrderId` with the order token at submit** | `OREXSYS-<id>` is gone after step 4. The only key that survives is the comment tag `[OREXSYS#<id>]` (`TagToExternalId`). Missing this sent one order three times on 22/09. |
| T3 | `CAST(EFOM.JSON AS VARCHAR(n))` **throws** «string right truncation» when the JSON is longer — it does not truncate | Use `EFOM.ORDERID` for the token. Never cast the JSON BLOB. |
| T4 | Adding `paymentStatus` to the admin status PUT → **500** | Send `status` only. |
| T5 | The public `PUT …/orders/{uuid}/status` → «not the owner»; admin `POST /api/v2/user/stores/{id}/orders` → **500 in every shape** | Only the flow in 4.3 works (it is what the admin «Order Capture» page does). |
| T6 | `CANCELORDER`'s remarks parameter is **VARCHAR(50)** (on Stavros.FDB — confirm on the target DB via `RDB$PROCEDURE_PARAMETERS`) | Longer → «string right truncation» at procedure line 51 and nothing happens. Cap at 50 inside `DayClose.Cancel` itself (`FitRemarks`) and declare the parameter with its size. **CustomerAdder's current remark «Ακύρωση από ΔΙΑΧΕΙΡΙΣΗ - χωρίς πελάτη (<user>)» is 40 characters + the Windows user name — any user name longer than 10 characters already breaks today's orphan cancel in Κλείσιμο Ημέρας.** |
| T7 | The platform's security filter rejects `@example.com` addresses (400 `SECURITY_REQUEST_BLOCKED`) | Use real-looking domains in tests. |
| T8 | A platform order younger than a few minutes has no EFOM row yet | `ConnectorSlack` = 15′ before calling it missing. |
| T9 | Orders sent by the app come back through the connector | The loop guard (4.4) — without it every send creates a duplicate till order and a nameless customer. |
| T10 | Hidden (`isSellable=false`) + `Inactive` offers **can** still be put into an order by the app | That is what makes the placeholders possible; customers never see them. |

---

## 6. Decisions to take from the owner BEFORE coding

Ask these first; write the answers into CustomerAdder's `CLAUDE.md` §2.

1. **Cancel on store 1046.** The admin status PUT was verified on 1110 only. CustomerAdder's
   §9 says `ALLOW_ORDER_CHANGE` is false on 1046. Before any cancel on 1046: read both
   stores' `settings` (`GET /api/v1/stores/{id}` → `settings`, read-only) and compare; if
   they differ in anything order-related, set 1110 the same way (with the owner's OK) and
   re-test there. First real cancel on 1046 only with the owner watching.
2. **Is Feature B wanted at LIGHTHOUSE?** Nobody pays at the till there and every order
   carries a customer; till sales would appear in the employee's PointToOrder history. That
   is close to the customer's request #3 («employees see their own orders on the web»), but
   the owner decides.
3. **Placeholders on 1046.** Feature B creates two hidden products and a hidden category in
   the live store the first time it needs them. Owner's OK required before the first send.
4. **Customer linking for the totals:** e-mail or card as CustomerAdder's `CustomersWindow`
   does (recommended), or e-mail only as CatalogBridge does.
5. **Which PC runs it.** The 5′ check and the 2′ sender run while the app is open. One PC
   per store; two PCs running the sender would each have their own register (the platform
   check still prevents doubles, but say it).

---

## 7. Order of work, with checkpoints

1. Branch; API client additions (3.2); port `OrderCancel.Decide`, `OrderMismatch` +
   tests → green. Fix T6 in `DayClose.Cancel` with its test. **Checkpoint: tests.**
2. `OrderMismatchService` + the Αρχική card, read-only. Run against Stavros/1110 (3.7 step 1).
   **Checkpoint: screenshot of the card by window handle, shown to the owner.**
3. The mismatch buttons and «Ακύρωση παραγγελίας» in `OrderHistoryWindow`. 3.7 steps 2–4 on
   1110. **Checkpoint: owner decision 6.1 before 1046.** Release as a minor version.
4. Loop guard in `EfomTriggerGuard` + `ReadState` + tests; gbak copy; then Stavros.FDB.
   **Checkpoint: trigger diff shown to the owner.**
5. `TillOrderSender` (button in `OrderHistoryWindow`) → 4.5 steps 2–4 on 1110.
6. `TillOrderSyncService` + the switch on Αρχική → 4.5 step 5.
   **Checkpoint: owner decisions 6.2 and 6.3.** Release.
7. Update CustomerAdder `CLAUDE.md` (§3 layout, §7 API facts T1–T5, §9 request #2 → Done),
   CHANGELOG, the user guide (`docs/guide-src`, `scripts/build-guides.ps1`).

## 8. Definition of done

- Every rule in 3.3 and 4.3 holds and is covered by a ported test; `dotnet test` green.
- Verified on 1110 + Stavros.FDB, each step in 3.7 and 4.5, with evidence (tokens, EFOM rows).
- Nothing written to 1046 or LIGHTHOUSE2.FDB without the owner's explicit go.
- CatalogBridge untouched; the marker strings in 4.4 identical in both repos.
- CustomerAdder `CLAUDE.md`, CHANGELOG and guide updated; exe released the usual way.
