# Plan: accept / reject online orders inside Γέφυρα Καταλόγου, on both sides

Owner (01/10/2026, after the mirror): «we are going to accept the order inside the tool
program. Then we are going to fix why the order that is not accepted is still placed inside
the Orexsys». Spec = that sentence plus `docs/ORDER_ACCEPTANCE.md` §1–§9 (what the platform
and the connector really do).

## Global constraints
- Store 1110 / Stavros.FDB only for live checks. Never 1046 / 1111.
- Orexsys writes are limited to `EFOM.STATUS` (+ `ACCEPTMIN`) on the connector's own row, guarded
  by `STATUS = 1` and an exact row count. This is the same update the till's accept screen makes;
  the vendor trigger raises the till order. Nothing else in Orexsys is written. No synthetic ORDM.
- PointToOrder is told only after Orexsys accepted the change (customer never hears «accepted»
  for an order the till could not raise).
- TDD for every pure function; the two DB writes are verified live on 1110 (recorded in the doc).

## Tasks
1. **Pure decisions.** `TillRow` gains `EfomId`. `OrderMirror.CanAcceptHere(WaitingOrder)`:
   null when the row is at the till (STATUS 1), else the Greek reason. `OrderMirror.ShouldVoidAtTill
   (TillRow, platformStatus)`: true when the row waits (1) and the platform says
   REJECTED / CANCELLED / DELETED — the fix for «not accepted but still placed»: such a row is
   set to −1 before anyone at the till can raise it. Tests in `OrderMirrorTests`.
2. **Orexsys writes.** `OrderMirror.AcceptAtTillAsync(settings, efomId, minutes)` → `UPDATE EFOM
   SET STATUS = 2, ACCEPTMIN = @min WHERE EFOMID = @id AND STATUS = 1`, exactly one row or
   `InvalidOperationException`; returns the ORDMID read back (may be null).
   `OrderMirror.RejectAtTillAsync(settings, efomId)` → `STATUS = -1`, same guard.
3. **Service.** Each tick, after the mirror: load platform orders of the last 2 days (every
   status, `OrderMismatch.LoadPlatformAsync`) and void every STATUS-1 row whose platform order is
   rejected/cancelled (log + register as Reject). `WaitingRow` exposes `CanAccept`/`EfomId`.
4. **UI.** Buttons on every row: minutes picker («σε 20′», 10/15/20/30/45/60, last choice
   remembered in `Settings.AcceptMinutes`), **Αποδοχή** (enabled only when at the till) and
   **Απόρριψη** (reason dialog restored). Accept = Orexsys then platform; reject = Orexsys (if
   the row is there) then platform. Status line reports each step.
5. **Live check on 1110** with the owner's waiting orders, then docs (`ORDER_ACCEPTANCE.md` §10,
   CHANGELOG, CLAUDE.md, guide), commit, push `sizes`.

## Review focus
- The STATUS = 1 guard: a second click, a row the till already accepted, a tagged copy.
- A platform call that fails after the Orexsys write (customer not told): status line + log, the
  mirror tells the platform on a later tick (row is now 2 / −1).
