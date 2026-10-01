# SDD ledger — plan: docs/superpowers/plans/2026-10-01-accept-in-tool.md
Pre-flight: Task 1 produces TillRow.EfomId/CanAcceptHere/ShouldVoidAtTill consumed by 2–4 — names fixed in the plan, no conflict.
Ruling: EFOM.STATUS/ACCEPTMIN update on the connector row is the one Orexsys write this feature adds — it is the till screen's own path (owner asked to accept inside the tool) — cost if wrong: an order raised at the till that the shop did not want (guarded by STATUS=1 + row count).
Task 1: complete (commits e90bc02..4f9069f, tests: dotnet test tests/CatalogBridge.Tests → 144/144 pass)
Task 2: Ruling: AcceptAtTillAsync/RejectAtTillAsync are Firebird writes with no unit test - verified live on Stavros.FDB/1110 in task 5 (the guard STATUS=1 + row count is the test) - cost if wrong: a wrong EFOM status on the demo DB
Task 2: complete (commits 4f9069f..fb769b8, tests: dotnet test tests/CatalogBridge.Tests → 145/145 pass; DB writes verified live in task 5)
Task 3: complete (same commit; void rule runs only for waiting rows the SUBMITTED list does not explain)
Task 4: complete (same commit; Ruling: minutes picker is a per-row combo seeded from Settings.AcceptMinutes, saved on each accept — cost if wrong: a wrong default minute count)
Final: fixed mismatch card mislabels rejected/waiting platform orders as «missing in Orexsys» — RejectedPlatformOrder_IsNeverMissing, PlatformOrderStillWaitingForTheShop_IsNotMissing, AcceptedOnThePlatformButStillWaitingAtTheTill_SaysSo RED→GREEN (3 failed before the change)
Task 5: complete (commits fb769b8..a152d23, tests: dotnet test tests/CatalogBridge.Tests → 148/148 pass; live 1110: XE-9K accepted 15′ → EFOM 48=2, ORDM 59, platform CLOSED+ETA; N2-ZY rejected → EFOM 47=-1, REJECTED; owner rejected T6-E8 from the real window → EFOM 49=-1, REJECTED «Το κατάστημα είναι κλειστό.»)
Final review: fresh reviewer (Agent, fable) on review-e90bc02..a152d23.diff - 0 Critical, 4 Important, 8 Minor.
Final: fixed #1 reject not gated on the till row (customer told «rejected» for a raised order / a till copy) — CanRejectHere_NeverForARaisedOrderOrATillCopy RED→GREEN, button bound to CanReject, handler re-checks
Final: fixed #2 in-tool decisions not registered (tally + leaving verdict wrong) — Register_DoesNotCountATokenTwice RED→GREEN (RememberOnce), buttons register at the Orexsys step
Final: fixed #3 till copies pile up SUBMITTED on the board while the switch is on — TillCopies_NeverReachTheBoard RED→GREEN (IsTillCopy, Pair skips them; service closes them) — live check on 1110 below
Final: fixed #4 full platform search every 5 s while an unexplained waiting row exists — RowsToVoid_OnlyWaitingUntaggedRowsThePlatformGaveUpOn RED→GREEN (RowsToVoid + explained cache cleared when the stamp moves)
Final: fixed #9 stale comments / CLAUDE.md rule 5 «nothing else is ever written» (re-graded Important: a false safety claim for the next reader) — doc only
Final: suite 152/152
Final: minor (deferred): #5 retry after a failed platform reject uses the fixed sentence, not the dialog's reason
Final: minor (deferred): #6 pill says «Δεν έφτασε στο ταμείο» for a row the till answered but the platform was not told yet
Final: minor (deferred): #7 service voids (platform CANCELLED/DELETED) are counted as the shop's rejections in the tally
Final: minor (deferred): #8 EFOM UPDATEs could also constrain on ORDERID + STOREID (defence in depth)
Final: minor (deferred): #10 AcceptMinutes outside the six picker options shows a blank picker
Final: minor (deferred): #11 AwaitingShop skip in OrderMismatch also applies with the switch off
Final: minor (deferred): #12 void selection now pure (RowsToVoid) - the service glue itself is untested
Final: Ruling: reviewer's "Declined to judge" items (DSTS question, no unit tests for the two DB writes, RECEIVED orders not on the board, double PUT CLOSED race, Settings.Save per accept, dialog text path, motion/PDF not reviewed, 4-char token collision) — all stand as-is: owner's question / ledgered ruling / pre-existing design / harmless by §2 measurements — cost if wrong: a RECEIVED order (admin screen «seen») would not appear on the board until someone closes or rejects it there
Final: live check #3 on 1110 — tagged copy KE-XY (EFOM 51 = -1 by the loop guard) closed on the platform by the service within 25 s, never on the board; HM-3E (real) shown with both buttons
Final review clean after the fix pass; workspace deleted
