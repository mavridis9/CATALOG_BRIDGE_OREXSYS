# Αποδοχή παραγγελιών από το ταμείο — how it works, what was tested, what is still open

Owner request (01/10/2026): *«send an order from P2O, see it in this program and accept or
decline it»*. This file is the record of everything found and done that day, so the next
person (or chat) does not have to rediscover it. Store **1110** (demo) only; nothing was
touched on 1046 / 1111.

---

## 1. The three ways PointToOrder can hold an order for the shop

Read from `D:\Claude\p2o-api-docs\PointToOrder_SecuredAPIs_openapi.json` and the admin UI.

| Mechanism | Setting(s) | What the shop does | Needs a public URL? |
|---|---|---|---|
| **A. Manual accept** (built) | `AUTO_ACCEPT_ORDERS=false` | order stays `SUBMITTED`; shop sets `CLOSED` (accept) or `REJECTED` + `rejectReason` | no — the app polls |
| B. Realtime confirmation | `REQUIRES_REALTIME_CONFIRMATION=true` | order goes `PENDING_CONFIRMATION`; shop calls `POST …/orders/{uuid}/confirm-pending {etaMinutes, price}` | no — untested |
| C. Validation webhook | «Endpoint Validation Link» (`EXTERNAL_ORDER_VALIDATION` + `VALIDATION_ENDPOINT_LINK`, or `POST …/orders/events/configs {endpoint, validationEndpoint}`) | PointToOrder calls **our** URL synchronously *before* submission and we answer yes/no | **yes** — and the payload is undocumented |

The owner's screenshot of «Custom Integration» shows two fields:
* **Endpoint Submission Link** — *«Publish all POINT2ORDER orders to an integration endpoint»*:
  a push webhook of every order (an alternative to polling, not an acceptance step).
* **Endpoint Validation Link** — *«Validate orders synchronously with an external system
  before submission»*: mechanism C.

Both need an HTTPS address reachable from the internet. The till PC sits behind the shop's
router, so they need hosting or a tunnel — the same decision deferred for the licence server.
And a synchronous validation cannot wait for a person to press a button (it has to answer in
seconds), so C is a *machine* check (stock, hours, zone), not «the till accepts».

**Decision: A.** It uses only calls this app already makes, works from behind NAT, and gives
the customer a reject reason. B and C stay available if hosting arrives later.

## 2. What the platform actually does (verified live, 1110, 01/10/2026)

Store 1110 as found: `AUTO_ACCEPT_ORDERS=True`, `REQUIRES_REALTIME_CONFIRMATION=False`,
no validation endpoint, `events/configs = {}`, `TWINSOFT_ENABLED=True`,
`TWINSOFT_STORE_ID=PO4897` (= `EFST.EFSTID` of the P2O line in Stavros.FDB).

| Step | Call | Result |
|---|---|---|
| switch auto-accept off | `PUT /api/v1/stores/1110/settings` body `[{"key":"AUTO_ACCEPT_ORDERS","value":"false"}]` | 200; the answer is the whole store, `settings.AUTO_ACCEPT_ORDERS` reads back `false` |
| place order **96-54** (draft → item → name → `PUT v2 … status SUBMITTED`) | as `TillOrderSender.SendCoreAsync` | stays **`SUBMITTED`**, `validated=false`, `isCompleted=false` |
| what is waiting | `POST …/orders/search?page=0&size=50` body `{"status":["SUBMITTED"],"createdAtFrom":…}` | returns 96-54 with `orderItems`, `customerName`, `totalDiscountedPrice`, `submittedAt`, `wishTime`, `comment` |
| cheap «anything changed?» | `GET …/orders/last-updates/ORDER` | `{"lastUpdatedAt":"2026-10-01T11:24:39.835503Z"}` — moves on submit, accept, reject. The admin screen itself polls this every ~2 s. |
| accept with ETA | `PUT /api/v2/user/stores/1110/orders/{uuid}` `{status:"RECEIVED", estimatedTime:"…Z", keysToUpdate:["status","estimatedTime"]}` | 200 → `RECEIVED`, `estimatedTime` set. **But** the admin screen still lists it under «Open» with «Order Expected 14:40» — RECEIVED is *seen*, not *accepted*. |
| reject | same PUT, `{status:"REJECTED", rejectReason:"Δοκιμή: το προϊόν τελείωσε", keysToUpdate:["status","rejectReason"]}` (order **M3-93**) | 200 → `REJECTED`, reason stored; the order moves to the «Rejected» tab |
| accept the way the admin screen does | admin «Confirm» button on **97-TN** → it sent `PATCH /api/v2/user/stores/1110/orders/{uuid}` | status became **`CLOSED`** — the same status every auto-accepted order has (all 30/09 orders are CLOSED, `isCompleted=false`) |
| accept via our PUT | `{status:"CLOSED", keysToUpdate:["status"]}` on 96-54 (RECEIVED → CLOSED) | 200 → `CLOSED` |
| switch auto-accept back on | same settings PUT with `"true"` | 200, reads back `true`; 1110 left as found |

So in PointToOrder terms: **Open = SUBMITTED / RECEIVED; Confirmed = CLOSED; Ready = isReady;
Completed = isCompleted; Rejected = REJECTED.** «Accept» for us is `CLOSED`.

`paymentStatus` in the admin PUT still answers 500 (as on 22/09) — not sent.

## 3. How an accepted order reaches Orexsys — and the open question

The connector is **not** `osmsrv`. It is
`C:\Orexsys\extOrderClientAPIapp\extOrderClientAPIapp\extOrderClientAPIapp.exe` (a window,
not a service), which every 10 s asks Twinsoft's relay for `<token>.json` files for store
`PO4897` and writes `EFOM` (`STATUS` 2 → the vendor trigger raises the till order). The JSON it
receives is PointToOrder's push to Twinsoft (it carries the relay's credentials — never log or
copy it unmasked). Yesterday's orders arrived 9–20 s after submission: with auto-accept on,
PointToOrder pushes at submit time.

**Not yet known: does PointToOrder push to the Twinsoft relay when the shop accepts manually
(SUBMITTED → CLOSED)?** It could not be answered on 01/10 because the connector was closed
since 12:39 and, when started, said `Cannot connect to DB`: its ini points to
`Server=192.168.2.18` while this PC is now on `192.168.1.37` (the database is local — this
app uses `LOCALHOST`). The relay keeps orders while the connector is closed (ΔΙΑΧΕΙΡΙΣΗ's
notes, 30/09), so **the answer is waiting in the relay**:

1. Point the connector at the database (its `extOrderClientAPIapp.ini`, `[DB] Server=`), start it.
2. Watch `…\logs\Logging011026.txt` and `EFOM` for tokens **96-54** (accepted via API, CLOSED),
   **97-TN** (accepted from the admin screen, CLOSED), **34-TX** (accepted via API, CLOSED).
   **M3-93** was rejected and must *not* arrive.
3. If they arrive → the feature is complete as built.
   If they do not → PointToOrder pushes to Twinsoft only when it auto-accepts, and the
   acceptance has to live elsewhere (ask PointToOrder support whether the Twinsoft integration
   fires on manual acceptance; or ΔΙΑΧΕΙΡΙΣΗ's `EfomReplay` path, which writes EFOM itself from
   the API order — a decision for the owner, not this app).

Until this is settled the switch should be used **only on 1110**.

## 4. What was built (1.1.3, branch `sizes`)

* `OrderAcceptance.cs` — pure: `ParsePending` (orders/search page → `PendingOrder`, SUBMITTED
  only, CHILD option lines folded into the line above as «ΠΙΤΑ ΓΥΡΟΣ (ΠΑΚΕΤΟ|ΧΛ)», oldest first),
  `ParseLastUpdate`, `NewSince`, `FitRejectReason` (default sentence, cap 1024),
  `PlatformValueFor`, `ReadAutoAccept`; and the calls `LastUpdateAsync`, `LoadPendingAsync`
  (last 2 days, paged), `AcceptAsync` (CLOSED), `RejectAsync` (REJECTED + reason),
  `SetPlatformAutoAcceptAsync` (settings PUT, read-back check), `ReadPlatformAutoAcceptAsync`.
* `OrderAcceptanceService.cs` — every 10 s `last-updates/ORDER`; a search only when the stamp
  moved (or every 2′ as a net); runs only while the switch is on, the API is configured and the
  `orders` module is licensed and not expired.
* Αρχική (`SyncView`): a new top card «Αποδοχή παραγγελιών από το ταμείο». The switch flips
  **the platform first** (`AUTO_ACCEPT_ORDERS`), then saves `OrderAcceptanceEnabled`; if the
  platform refuses, nothing changes. Each waiting order is a row: token · customer · total ·
  Παραλαβή/Διανομή/Στο κατάστημα · «το συντομότερο» or «για dd/MM HH:mm» (wishTime more than
  20′ after submit), the items, the comment, the time; buttons **Αποδοχή** (CLOSED) and
  **Απόρριψη** (`RejectReasonWindow`: editable list of reasons, the customer reads it). A new
  order plays the system sound and brings the window up. Everything is logged in `customers.log`
  (`acceptance:` lines). Licence: `License.Blocked(Module.Orders)` on the switch and both buttons.
* `Settings.OrderAcceptanceEnabled` (`settings.ini`), default off.
* Tests: `tests/CatalogBridge.Tests/OrderAcceptanceTests.cs` (10) — 134 in all.
* Smoke run of the real window against 1110 with the switch on: order **34-TX** appeared with
  both buttons within 10 s of submission (screenshot taken by window handle, not kept in the
  repo). Then accepted via the API, auto-accept restored to `true`.

## 5. Operating rules the shop must know

* **While the app is closed, nobody accepts.** Orders pile up as SUBMITTED on PointToOrder and
  the customer waits. The confirmation dialog and the card say so; switch it off before closing
  the shop PC for long. (What PointToOrder does with an order nobody answers — timeout, e-mail —
  is not documented; `ORDER_NOTIFICATION_EMAIL_STATUS_CHANGE` is `false` on 1110.)
* The switch changes a **store setting on PointToOrder**; the admin screen's own «Confirm /
  Reject» buttons keep working in parallel and the app sees the result on the next poll.
* An expired `orders` licence stops the watcher but does **not** flip the platform back by
  itself: the card says ΣΤΑΜΑΤΗΜΕΝΟ. **Switching off always works, licence or not** (only
  switching *on* and the two buttons go through `License.Blocked`), so the shop can always put
  PointToOrder back to accepting by itself. The admin screen's «Settings → Orders» does the same.
* Rejected orders never reach the till; accepted ones reach it through the connector exactly as
  before — **if §3 is answered yes**.

## 6. Test harness

`scratchpad/AcceptTest` (not in the repo; the same calls are in `OrderAcceptance.cs`): modes
`settings`, `off|on`, `order [name]`, `status <uuid>`, `setstatus <uuid> <STATUS>`,
`accept <uuid> [min]`, `reject <uuid> <why>`, `pending`, `list`, `last`, `efom [baseline]`,
`json <efomid>`, `get <path>`. It refuses any `ApiStoreId` other than 1110.
Orders created on 1110 that day: 96-54, M3-93, 97-TN, 34-TX (4,00 € each, ΠΙΤΑ ΓΥΡΟΣ).
