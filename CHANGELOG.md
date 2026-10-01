# Changelog

All notable changes to CatalogBridge (Γέφυρα Καταλόγου). Versions are semantic; each
released version is a git tag `vX.Y.Z` on `master` with the exe attached to the GitHub Release.

## [1.1.3] — 2026-10-01 (branch `sizes`, not tagged)

- **New look, and the order arrives as a ticket** (owner, 01/10 evening; `docs/design/VISUAL_REFRESH.md`).
  A deep petrol rail with icons, a cool mineral background, one petrol accent, status colours
  that mean one thing each, numbers in Bahnschrift. A new online order now pops up over
  everything as a printed ticket (`OrderTicketWindow`): token, customer, when, items with their
  options, the comment, the total, minute chips and one green button that says what it does
  («Αποδοχή, έτοιμη σε 20′»); rejecting unfolds the reasons inside the ticket and shows the
  sentence the customer will read. Several at once queue («1 από 3»); «Αργότερα» leaves it on
  Αρχική, where a click reopens it. The search for waiting orders leaves the lines out, so each
  waiting order is read in full once (cached). **Everything about the e-mail correlation moved
  to its own screen, «Αντιστοίχιση παραγγελιών»** (`OrdersView`): the correlation switch (PIN),
  the live trigger state, till orders → PointToOrder, the mismatch check; the rail shows the
  mismatch count. Ρυθμίσεις keeps a pointer. **Fixed:** saving Ρυθμίσεις silently switched
  «Online παραγγελίες» off and reset the minutes (seen live 16:27 — the platform stayed on manual
  accept while the app stopped watching). 156 tests.

- **Online παραγγελίες: decided in the tool, applied to both sides** (owner, 01/10 evening,
  final). Every waiting order has a minutes picker, **Αποδοχή** and **Απόρριψη**. Αποδοχή does
  what the till's accept screen does — the connector's EFOM row goes to status 2 with the minutes
  and Orexsys raises the till order — then tells PointToOrder (`CLOSED` + ready time). Απόρριψη
  voids the row (−1) so it is never raised, then tells PointToOrder (`REJECTED` + the reason).
  Orexsys first, platform second, each guarded by `STATUS = 1`. The service also voids any waiting
  row whose platform order is already rejected or cancelled (the «not accepted, yet placed»
  fix), and the mirror of the till's own screen stays. Live on 1110: XE-9K → ORDM 59 + CLOSED
  15′; N2-ZY → −1 + REJECTED; the owner rejected T6-E8 from the window. After review: Απόρριψη
  is refused for a row the till already raised (the customer would read «rejected» while the
  kitchen cooks) or a till copy; decisions made here count in today's tally; till copies the app
  itself sent (comment tag) never reach the board and are closed on the platform by the service;
  the platform's full list is read only once per change, not every 5 s. `docs/ORDER_ACCEPTANCE.md`
  §10. 152 tests.
- *(superseded the same evening)* **Online παραγγελίες: the till decides, the app mirrors** (owner, 01/10 afternoon, replacing
  the buttons below the same day). The till's own «online orders» screen accepts (with minutes)
  or rejects; `OrderMirror` reads the connector's EFOM rows and tells PointToOrder — `CLOSED` +
  `estimatedTime`, or `REJECTED` with a fixed sentence — only while the order is still
  `SUBMITTED` there. The home card is a status board: one row per waiting order with a pill
  «Στο ταμείο · n′» / «Δεν έφτασε στο ταμείο · n′» that turns amber at 3′ and red at 8′ (2′ when
  the connector has not brought it), the verdict shown for a moment when the row leaves, and
  today's tally. No accept/reject buttons, no sound (the till rings). `Motion.cs`: 200 ms
  ease-out entrances and colour crossfades, 0.97 press feedback on every button, all off when
  Windows animations are off. `docs/ORDER_ACCEPTANCE.md` §6–§9. 141 tests.
- *(superseded the same day)* **Αποδοχή παραγγελιών από το ταμείο** (owner, 01/10). A new top card on Αρχική with a switch:
  on, PointToOrder stops accepting online orders by itself (`AUTO_ACCEPT_ORDERS=false`, set on
  the platform by the app, read back as proof) and every submitted order appears here within
  10 s — token, customer, total, pickup/delivery and when, the items with their options, the
  comment — with **Αποδοχή** (status `CLOSED`, what auto-accept and the admin «Confirm» produce)
  and **Απόρριψη** (status `REJECTED` + a reason the customer reads, from an editable list). A
  new order plays the system sound and raises the window. Off restores the platform setting.
  Polling = `GET orders/last-updates/ORDER` every 10 s, `orders/search {status:[SUBMITTED]}`
  only when it moved. `OrderAcceptance` (pure + calls), `OrderAcceptanceService`,
  `RejectReasonWindow`, `Settings.OrderAcceptanceEnabled`; 10 tests (134). Verified live on
  1110 (orders 96-54, M3-93, 97-TN, 34-TX), store left as found. **Open:** whether PointToOrder
  pushes a manually accepted order to the Twinsoft relay (the connector could not reach its
  database that day) — `docs/ORDER_ACCEPTANCE.md` §3 says exactly what to check. Use on 1110
  only until then.
- **Same afternoon, measured (doc §6–§8):** the relay gets every order at submission, so P2O
  acceptance only informs the customer; the hold is on the Orexsys side (EFOM STATUS 1). The
  validation link's request and answer rules were captured through a temporary tunnel: only
  `200 {"valid":false,"message"}` refuses; timeout ≈ 5 s and errors are fail-open. No code change.

## [1.1.2] — 2026-09-29

- **A price list per category** (owner, 29/09, at STOA — one Orexsys, two businesses: ΚΑΛΑΜΑΚΙ
  priced in list 1, ΣΤΟΑ in list 2). Ρυθμίσεις → «Κατηγορίες προς συγχρονισμό»: next to every
  category a «Τιμοκατάλογος» choice, each option saying how many of the category's products are
  priced there («ΣΤΟΑ · 12 με τιμή»). A category opens on the list saved for it, else on the
  default list if it has prices there, else on the list it IS priced in — so nothing has to be
  guessed. Saved as `CategoryPriceLists=ITMCID=PRLSID,…` (only where it differs from the default
  «Τιμοκατάλογος», which stays as the fallback); the price COLUMN remains one for the
  installation. `OrexsysCatalog` reads every list in use once and takes each product's (and each
  size's) price from its category's list, so Συγχρονισμός, Αποστολή, Πίνακας ελέγχου and the
  unpriced warning all follow. Verified read-only on a copy of STOA.FDB: 88 priced products
  with list 1 alone, 212 with a list per category; 15 categories open on «ΣΤΟΑ» by themselves.
- Built from branch `sizes`, not tagged: handed to the client as `deploy\Gefyra_Katalogou_1.1.2`.

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
- **Products not on PointToOrder ride on two hidden placeholders, so every till order can go
  with the exact total** (owner decision 22/09: never visible when ordering). «Αγορά στο
  κατάστημα» 1,00 € × euros and «Αγορά στο κατάστημα (λεπτά)» 0,01 € × cents, in a category
  «ΚΑΤΑΣΤΗΜΑ», created by the app the first time they are needed and kept hidden
  (`isSellable` false) **and** Inactive — verified on 1110 that a hidden, inactive product can
  still be put into an order by the app. Products that are online go as themselves; one whose
  price differs from the till still refuses the whole order (a catalogue to fix). The order's
  comment lists the real items («ΠΡΟΙΟΝ 13% ×1 100,00€»). Verified live: Stratos's till order
  #5 (100,00, «ΠΡΟΙΟΝ 13%», not online) went as 100 × «Αγορά στο κατάστημα» → NN-3K CLOSED
  100,00 in his history; the connector brought it back as EFOM 8 and the loop guard left it at
  STATUS -1 — no till order, no customer. Also found: the admin submit call answers 500 when
  `paymentStatus` is sent with it — status only now.
- **A till order goes to PointToOrder ONCE** (owner, 22/09). Found while closing this: the
  platform overwrites `externalOrderId` with the order token at submit, so the «already there»
  check never matched — that is how #5 ended up as NN-3K *and* 5H-KZ. The key is now the comment
  tag «[OREXSYS#<ORDMID>]» (the same one the loop guard reads), which the platform keeps.
  `TillOrderSender.SendAsync` now owns three fences shared by the button and the automatic
  run: one send at a time in the process; a re-check of the register (`sent_till_orders.txt`,
  now written by the button too) and of EVERY page of the customer's history right before
  anything is created (`AlreadySentException`, shown as «έχει ήδη σταλεί (token)»); and a
  failure after the draft exists but before submit DELETES the draft, so a retry never doubles
  it — while a failure after submit (e.g. the total check) is recorded as sent. Verified on
  1110: refused by the platform check, then by the register; a bogus line left no draft behind.
  The duplicate my check created before the fix (N3-38) is cancelled.
- **«Ακύρωση παραγγελίας» — one click, both platforms** (owner, 22/09). In the customer's orders
  window: cancels the selected order in Orexsys (`CANCELORDER`, the till's own procedure — items
  and payments reversed, cancellation document issued, not undoable) AND on PointToOrder (admin
  status → CANCELLED), touching only the side(s) still active: already cancelled in Orexsys →
  only the platform; already cancelled on the platform → only Orexsys; both → nothing. The
  confirmation says exactly which. A failure half-way reports what DID happen, and the next press
  finishes the rest. Cancelled Orexsys orders now appear in the list («Ακυρωμένη», not counted).
  Found while building it: PointToOrder's customer-history endpoint silently drops cancelled
  orders — `orders/search` by token is used instead, and the till-sent token is read from the
  local registry, never from the platform. Decision logic unit-tested; the platform side was
  verified live on 1110; the Orexsys side runs the same call ΔΙΑΧΕΙΡΙΣΗ has used since 1.7.
- **Licensing, phase 1** (owner, 22/09; design in `docs/LICENSING_AND_UPDATES.md`). Ρυθμίσεις →
  «Άδεια χρήσης»: the PC's fingerprint (read it to the technician), a box to paste the licence,
  «Ενεργοποίηση». The licence is a signed text issued per PC for one or both modules —
  Συγχρονισμός Καταλόγου, Αντιστοίχιση Παραγγελιών & Πελατών — until a date; verified offline, no
  internet needed. Without it, or after it expires (14 days' grace with an amber warning first),
  the app only shows: no sync, no stock, no push, no EFIC edits, no reset, no trigger patch, no
  customer creation, no order send/cancel, automatic services off — and nothing already done in
  Orexsys or PointToOrder is touched. A module not in the licence has its screens hidden. Sidebar
  shows «Άδεια: <πελάτης> · έως <ημερομηνία>»; the home page shows a red/amber banner when it is
  not fine. Issued with `tools/LicenceSigner` (owner's PC only; the signing key is not in the repo).
- **«Ασυμφωνίες παραγγελιών» on Αρχική** (owner, 23/09). An order that exists on both sides but is
  cancelled on one and still active on the other — e.g. an online order cancelled at the till that
  still sits CLOSED in the customer's PointToOrder history. Checked at start and every 10 minutes
  («Έλεγχος τώρα» too) over the last 30 days: Orexsys orders with a PointToOrder counterpart (token
  from EFOM.JSON, the local send registry, or the «[OREXSYS#…]» comment tag) against one paged
  `orders/search` on the platform. Each row names the order, the customer, the amount and the
  problem, with one button — «Ακύρωση στο PointToOrder» or «Ακύρωση στο Orexsys» — that acts only
  after confirmation, the same way the «Ακύρωση παραγγελίας» button does. Nothing is automatic.
  First live run found #3 (87-5Z): cancelled on the platform on 22/09, still active in Orexsys.
- **Πελάτες loads itself** the first time the tab is opened (owner, 23/09) — a list loaded hours
  earlier had shown a customer as «μόνο στο Orexsys» after his PointToOrder account changed.
  «Φόρτωση» remains the refresh. Home screen re-laid out the same day: the till-order switch on
  top, the sync button in the middle, mismatches as a grid.
- **Per-customer totals, every 5 minutes** (owner, 23/09). The «Ασυμφωνίες παραγγελιών» check now
  runs every 5′ (was 10′) and, for every customer that exists on BOTH sides (same e-mail — the
  link the trigger uses; plain Orexsys customers are left alone), compares the last 30 days:
  count and total of active orders in Orexsys against count and total on PointToOrder. Only the
  customers that differ are listed — Orexsys · PointToOrder · Διαφορά — with the reason spelled
  out per order: «#20 12,00 μόνο στο Orexsys» (a till order never sent), «AB-12 9,50 μόνο στο
  PointToOrder» (an online order that never reached Orexsys, or landed on another customer),
  «#10/TP-5Z: 14,56 ≠ 14,00» (same order, different amount). «Παραγγελίες...» opens the customer's
  orders window to fix it. First live run: Stratos, Orexsys 3 · 604,00 vs PointToOrder 5 · 708,00 —
  the two duplicate copies from the 22/09 tests (H9-P2, 5H-KZ) found by the check itself.
- **Two more order checks** (owner, 23/09): **same order, different amount** on the two sides
  («Διαφορετικό ποσό: Orexsys 14,56 € · PointToOrder (TP-5Z) 14,00 €»), and **an online order
  that never became an Orexsys order** — either the connector never brought it (no EFOM row) or
  Orexsys rejected it (EFOM STATUS −1, no ORDMID). Both are shown without a button: the first is
  a catalogue/price question, the second is the connector's — nothing the app should invent an
  ORDM for. A platform order younger than 15 minutes is given the connector its time; copies of
  till orders the loop guard rejected on purpose (tagged) are never reported. First live run on
  the demo: 14 orders of 18–21/09 «never brought» — they predate the fresh STAVROS.FDB of 21/09 —
  and 2 rejected (M6-2E, 32-5P) from before the EFST line existed.
- **EFOM.ORDERID instead of the JSON** for the PointToOrder token of an order that arrived from
  the platform. Found while building the above: `CAST(JSON AS VARCHAR(8000))` does not truncate
  in Firebird — it raises «string right truncation» the moment an order's JSON is longer, which
  would have broken the customer's orders window and the check for any shop with a big online
  order. The connector fills `ORDERID` with the token, so no BLOB is read at all now.
- **User guide rewritten** (29/09): `docs/guide/guide.html` → PDF through headless Edge (the
  ΔΙΑΧΕΙΡΙΣΗ pipeline). 11 pages, 13 sections: licence, the home screen, catalogue sync, sizes,
  customers, till orders ↔ PointToOrder, cancel in both, mismatches, settings, technician
  screens, rules, files/backups/moving PC, troubleshooting. The technician PIN is no longer
  printed in it. The 01/09 `.docx` is superseded and kept only as history.
- **Automatic: «Παραγγελίες ταμείου → PointToOrder» switch on the home page.** Owner request
  22/09. `TillOrderSyncService` runs every 2 minutes while the app is open: every closed
  (STS 4), not cancelled till order — not one that came from the platform — placed since the
  day the switch was turned on, for a customer whose e-mail has a PointToOrder account, is sent
  with `TillOrderSender` under the same identical-only rule; refused orders are logged once per
  session with the reason (`backups\customers.log`); sent ones are remembered in
  `sent_till_orders.txt` and checked against the platform's `externalOrderId`, so nothing is
  sent twice. Turning it on asks for confirmation and records the date
  (`TillOrderSyncEnabled` / `TillOrderSyncSince`; kept as they are when Ρυθμίσεις is saved).
  It refuses to run while the loop guard is not in the trigger, and cannot be switched on
  while «Αντιστοίχιση παραγγελιών με e-mail» is off.
- **Till orders into the customer's PointToOrder history («Αποστολή στο PointToOrder» in the
  customer's orders window).** Owner goal 21-22/09: the same orders on both sides. The admin
  «create order» API answers 500, but the admin's own «Order Capture» page (watched live in the
  owner's Chrome) uses the customer-app calls: draft `POST /api/v1/stores/{id}/orders` →
  `PUT …/orderItems` per product → `PUT …/orders/{uuid}` with `customerUserId`,
  `externalOrderId = OREXSYS-<ORDMID>` and a comment → admin
  `PUT /api/v2/user/stores/{id}/orders/{uuid}` status SUBMITTED + PAID_OFFLINE (auto-accept
  closes it). Verified live for Stratos on 1110: the order appeared in his history. Rule
  (owner): **sent only if identical** — every line maps through EFIC (this store, that size)
  to a PointToOrder product whose price equals the till price, integer quantities; otherwise
  refused with the reason and nothing is sent (`TillOrderSender.CheckIdentical`). The
  window's «PointToOrder» column shows «από P2O» for orders that came from the platform and
  the token of the copy for till orders already sent; those cannot be sent again.
- **Loop guard in `EFOM_BIUD_TRG` (`OREXSYS-LOOP-GUARD-V1`).** The first live send came
  straight back through the connector as a duplicate till order #6, and — admin-created
  orders carry no e-mail — as a new nameless customer. Every order the app sends carries
  «[OREXSYS#<ORDMID>]» in its comment (→ `orderGeneralNotes` in `EFOM.JSON`); two more
  anchored edits in the trigger mark such an order STATUS -1 on insert and again whenever the
  connector tries to accept it, so it never becomes a till order and never touches `CUST`.
  Part of the same patch as the e-mail correlation (added in place to a database already on
  e-mail-only; removed together). «Αποστολή» refuses when the guard is not active. Proven on
  a copy of STAVROS.FDB (8/8: ordinary order still accepted, tagged order stays -1 with no
  ORDMID, no customer created, removal restores the vendor trigger); applied to the real
  STAVROS.FDB at app start 22/09 10:31 (backup in `publish\backups`).
- **«Πελάτες» → double-click: the customer's orders.** Every real order of the customer in
  Orexsys — online and at the till (customer link = CARDCUSTID when set, else CUSTID, as every
  Orexsys reader does; cancelled = CANCELED or TOTAL 0, counted apart and left out of the
  totals) — with date, number, kind (online / take away / table), state, main items and amount,
  plus the count, the total and the average per order. Read-only. An account that exists only
  on PointToOrder has no Orexsys orders yet — the message points to «Κάρτα...».
- **«Πελάτες» → «Κάρτα...»: a card for a customer that has none.** For an Orexsys customer
  without a card it fills `CUST.CARDNO` (refused if the card is on another customer or the
  customer already has one — exactly one row changes or nothing). For an account that exists
  only on PointToOrder it creates the Orexsys customer with that card and the account's name,
  e-mail and (real) mobile, so the account's orders match from then on. Then it tries to put
  the card on the PointToOrder account as `memberCode`; a self-registered account refuses
  (403 «only he can change their details») and the message says so — orders still match by
  e-mail, so nothing is lost. Verified on the scratch copy (11/11).
- **Νέα οθόνη «Πελάτες»: ο πελάτης δημιουργείται μία φορά, και στα δύο συστήματα.** Owner
  decision 21/09/2026: only the shop creates customers. «Νέος πελάτης» takes name, card,
  e-mail and (optional) mobile and creates the customer in Orexsys (`CUST`: name in
  FNAME+LNAME, card in `CARDNO`, e-mail, mobile; a `CUSP` row for the mobile — one transaction,
  CUSTID from Orexsys's trigger) and then on PointToOrder (card = `memberCode`). Because the
  e-mail is on both sides from the start, the customer's first online order already finds them
  through the e-mail-only trigger — verified on a copy of STAVROS.FDB. A card or e-mail already
  on another Orexsys customer is refused (a duplicate e-mail would make the trigger pick one at
  random). Logic and traps ported from ΔΙΑΧΕΙΡΙΣΗ: `customers/search-all` (plain `search` is
  broken server-side), Created vs already-there vs *rejected as duplicate* (e-mail is unique
  across the whole platform) reported separately, names split so the platform does not double
  them, and the phone sent is a Greek mobile or a stand-in built from the CUSTID (anything else
  passes creation and then makes P2O refuse the customer's orders). A platform failure never
  undoes the Orexsys customer: «Αποστολή στο PointToOrder» retries from the list. **The
  invitation e-mail is never automatic** — «Αποστολή πρόσκλησης...» sends it on request
  (confirmation defaulting to No; the date is remembered in `customer_invites.txt`). The list
  shows the Orexsys customers that carry an e-mail next to the P2O accounts, matched on e-mail
  exactly as the trigger matches (upper + trim): ✓ και στα δύο / ← μόνο στο Orexsys / → μόνο στο
  PointToOrder / ⛔ same e-mail on 2+ Orexsys customers, plus a note when the card differs.
  Every action is logged to `backups\customers.log`. Enabled with Ρυθμίσεις → Οθόνες τεχνικού →
  «Πελάτες». Found on the way: the shop cannot edit a self-registered P2O account (403), admin
  order-create answers 500, and P2O's security filter blocks `@example.com` addresses (explained
  in Greek). 8 unit tests; 12/12 on the scratch copy with the P2O side read-only.
- **Αντιστοίχιση παραγγελιών μόνο με e-mail (Ρυθμίσεις → «Παραγγελίες PointToOrder»).** Customer
  login is mandatory on the platform (owner, 21/09), so every order carries
  `customerDetails.customerEmail` in `EFOM.JSON`. Ticking the option patches Orexsys's
  `EFOM_BIUD_TRG` (the trigger that picks the customer of an incoming order): an order with an
  e-mail is matched against `CUST.EMAIL` **only** — the phone is never consulted; no match means
  Orexsys creates a new customer carrying that e-mail, which the person's next order finds. Only
  an order without an e-mail keeps the vendor's phone lookup. `EfomTriggerGuard` is ported from
  ΔΙΑΧΕΙΡΙΣΗ (same five anchored edits, applied to whatever source is live, all-or-nothing,
  backup to `backups\EFOM_BIUD_TRG_*.sql` first, `backups\efom_guard.log`) with the lookup block
  made e-mail-only (marker `OREXSYS-EMAIL-ONLY-V2`, alongside ΔΙΑΧΕΙΡΙΣΗ's
  `OREXSYS-EMAIL-CORRELATION-V1` so that program still sees the trigger as patched); a trigger
  carrying ΔΙΑΧΕΙΡΙΣΗ's e-mail-then-phone version is upgraded in place. Applied on «Αποθήκευση»
  and re-checked at every start (an Orexsys upgrade restores the vendor trigger). Off = the
  trigger is left alone; «Αφαίρεση από το Orexsys...» reverses the edits (either version) on
  request. Ρυθμίσεις shows the live state. Tested: 11 unit tests on the real vendor trigger text
  (Stavros and STOA run the same version); end to end on a copy of STAVROS.FDB, 12/12.
- **The chosen categories now scope every technician screen** (owner request 21/09): Σύγκριση,
  Πίνακας ελέγχου and Αντιστοιχίσεις show only the products of the categories ticked in
  Ρυθμίσεις («Όλες» = no filter), and say so. On the PointToOrder side an unlinked product
  counts when its P2O category holds a product linked to a chosen category (a category renamed
  in the preview still counts) or has the same name as a chosen Orexsys category
  (`CategoryScope`). Αντιστοιχίσεις keeps a row pointing at no product visible (it is a fault).
  Συγχρονισμός is unchanged: switching off online what is off at the till still covers every
  category. Also: a product sent once with a size option group no longer shows «(Κανονικό)»
  in Σύγκριση — the label appears only when each size is its own P2O product.
- **«Επαναφορά από την αρχή» (Ρυθμίσεις → Επαναφορά).** Empties the PointToOrder store and the
  PointToOrder rows of EFIC so everything can be sent again. The dialog first reads and shows
  exactly what goes: every product, every category and every size option group of the store in
  «Κωδικός καταστ.» (not only what the app sent), and the EFIC rows per code — the own «Σύνδεση
  P2O» ticked, other P2O / leftover test codes offered unticked, Wolt / efood listed greyed as
  «δεν αγγίζεται ποτέ». It needs «ΔΙΑΓΡΑΦΗ» typed plus a Yes. Before deleting, the whole
  catalogue (listing + every product detail) is saved to `backups\P2O_<store>_<time>_before_reset.json`
  and EFIC to its usual CSV. Order: option choices → option groups → products → categories;
  the store is re-read afterwards and what is left is reported. Store **1046** is refused
  outright. EFIC side (`EficRepository.DeleteAllUnderCodesAsync`): own code must resolve, no
  code may be empty or another platform's, rows deleted must equal rows counted in the same
  transaction. Tested: STOA copy — Wolt/efood codes refused, only the 2 test rows removed, other
  rows byte-for-byte unchanged; demo 1110 — a throwaway category, 2 products and a size group
  deleted cleanly, the real products untouched; read-only plan of the real store correct.
- **Order-feed check: orders arriving under another code than «Σύνδεση P2O» are a stop.** Seen
  21/09 on Stavros: the connector sends store 1110 as `PO4897`, EFST said `PO1110` and the app
  wrote its EFIC links under `1` — the first real order arrived with no product on any line.
  `EficRepository.LoadOrderFeedAsync` (read-only) lists the codes P2O orders arrived under in the
  last 30 days (`EFOM.STOREID`; Wolt / efood / any non-P2O EFST line left out) and
  `OrderFeedCheck` flags every one that is not ours. Συγχρονισμός stops before creating anything
  with a red message naming the arriving code and the fix (choose it in Ρυθμίσεις, or first
  correct the EFST line in Orexsys if it has no such line); Αποστολή refuses to send; Πίνακας
  ελέγχου shows it in red; the «Σύνδεση P2O» hint in Ρυθμίσεις says which code to choose.
  Checked read-only: Stavros with `PO4897` quiet, with `1` alarms naming `PO4897`, STOA copy
  (Wolt/efood only) quiet.
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
