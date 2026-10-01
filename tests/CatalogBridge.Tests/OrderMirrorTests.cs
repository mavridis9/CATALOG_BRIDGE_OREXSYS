using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // The mirror's decisions: the till's EFOM status vs the platform's order status.
    [TestClass]
    public class OrderMirrorTests
    {
        private static TillRow Row(int status, int min = 0, bool tagged = false) =>
            new("96-54", status, min, new DateTime(2026, 10, 1, 14, 54, 35), tagged, status == 2 ? 60 : null);

        private static PendingOrder Order(string token = "96-54", DateTime? submitted = null) =>
            new("u-" + token, token, "ΔΟΚΙΜΗ", 4m, submitted ?? new DateTime(2026, 10, 1, 11, 24, 39, DateTimeKind.Utc), null, "NO_LOCATION", "", new List<PendingLine>());

        [TestMethod]
        public void Decide_FollowsTheTill_OnlyWhileThePlatformStillWaits()
        {
            Assert.AreEqual(MirrorAction.Accept, OrderMirror.Decide("SUBMITTED", Row(2, 20)));
            Assert.AreEqual(MirrorAction.Reject, OrderMirror.Decide("SUBMITTED", Row(-1)));
            Assert.AreEqual(MirrorAction.Accept, OrderMirror.Decide("RECEIVED", Row(2)));
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("SUBMITTED", Row(1)));          // still waiting at the till
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("SUBMITTED", null));            // not at the till yet
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("CLOSED", Row(2)));             // platform already knows
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("CANCELLED", Row(-1)));
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("REJECTED", Row(-1)));
        }

        [TestMethod]
        public void Decide_NeverTouchesATillSentCopy()
        {
            // A till order sent to the platform comes back through the relay and the loop guard
            // marks it -1: that is the guard, not a rejection by the shop.
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("SUBMITTED", Row(-1, tagged: true)));
            Assert.AreEqual(MirrorAction.None, OrderMirror.Decide("SUBMITTED", Row(2, tagged: true)));
        }

        [TestMethod]
        public void EstimatedTime_FromTheTillsMinutes()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(now.AddMinutes(20), OrderMirror.EstimatedTime(Row(2, 20), now));
            Assert.IsNull(OrderMirror.EstimatedTime(Row(2, 0), now));
        }

        [TestMethod]
        public void Index_PrefersTheRealRowOverATaggedCopy()
        {
            var early = Row(1) with { Insdate = new DateTime(2026, 10, 1, 10, 0, 0) };
            var late = Row(2) with { Insdate = new DateTime(2026, 10, 1, 11, 0, 0) };
            var copy = Row(-1, tagged: true) with { Insdate = new DateTime(2026, 10, 1, 12, 0, 0) };
            var map = OrderMirror.Index(new[] { copy, early, late });
            Assert.AreEqual(1, map.Count);
            Assert.AreSame(late, map["96-54"]);   // latest untagged wins
            var onlyCopy = OrderMirror.Index(new[] { copy });
            Assert.IsTrue(onlyCopy["96-54"].TaggedCopy);
        }

        [TestMethod]
        public void Pair_StatesAndLevels()
        {
            var now = new DateTime(2026, 10, 1, 11, 30, 0, DateTimeKind.Utc);
            var rows = new Dictionary<string, TillRow> { ["96-54"] = Row(1) };
            var fresh = Order("AA-11", now.AddMinutes(-1));
            var waiting = OrderMirror.Pair(new[] { Order(), fresh }, rows, now);

            Assert.AreEqual("AA-11", waiting[1].Order.Token);          // oldest first
            var atTill = waiting[0];
            Assert.IsTrue(atTill.AtTill);
            Assert.AreEqual("Στο ταμείο · 5′", atTill.StateText);
            Assert.AreEqual(1, atTill.Level);                           // ≥ 3′: take a look
            StringAssert.Contains(atTill.Hint, "ταμείο");

            var notYet = waiting[1];
            Assert.IsFalse(notYet.AtTill);
            Assert.AreEqual("Δεν έφτασε στο ταμείο · 1′", notYet.StateText);
            Assert.AreEqual(1, notYet.Level);                           // under 2′: amber, not red
            Assert.AreEqual(2, new WaitingOrder(fresh, null, now.AddMinutes(3)).Level);   // 4′ without a row: red
            Assert.AreEqual(0, new WaitingOrder(Order(submitted: now.AddMinutes(-1)), Row(1), now).Level);
            Assert.AreEqual(2, new WaitingOrder(Order(submitted: now.AddMinutes(-9)), Row(1), now).Level);
        }

        // ---- deciding INSIDE the tool (owner, 01/10 evening) ---------------------------------

        [TestMethod]
        public void CanAcceptHere_OnlyWhileTheRowWaitsAtTheTill()
        {
            var now = new DateTime(2026, 10, 1, 11, 30, 0, DateTimeKind.Utc);
            Assert.IsNull(OrderMirror.CanAcceptHere(new WaitingOrder(Order(), Row(1), now)));
            StringAssert.Contains(OrderMirror.CanAcceptHere(new WaitingOrder(Order(), null, now))!, "ταμείο");
            StringAssert.Contains(OrderMirror.CanAcceptHere(new WaitingOrder(Order(), Row(2), now))!, "ήδη");
            StringAssert.Contains(OrderMirror.CanAcceptHere(new WaitingOrder(Order(), Row(-1), now))!, "ήδη");
            Assert.IsNotNull(OrderMirror.CanAcceptHere(new WaitingOrder(Order(), Row(1, tagged: true), now)));
        }

        [TestMethod]
        public void TillRow_CarriesItsEfomId()
        {
            var row = new TillRow("96-54", 1, 0, DateTime.Now, false, null, EfomId: 47);
            Assert.AreEqual(47L, row.EfomId);
        }

        [TestMethod]
        public void ShouldVoidAtTill_WhenThePlatformGaveUpOnAWaitingRow()
        {
            // «Not accepted, yet placed in Orexsys»: a row that still waits at the till while the
            // platform order is already rejected or cancelled must be voided (−1) before anyone
            // at the till can raise it.
            Assert.IsTrue(OrderMirror.ShouldVoidAtTill(Row(1), "REJECTED"));
            Assert.IsTrue(OrderMirror.ShouldVoidAtTill(Row(1), "CANCELLED"));
            Assert.IsTrue(OrderMirror.ShouldVoidAtTill(Row(1), "DELETED"));
            Assert.IsFalse(OrderMirror.ShouldVoidAtTill(Row(1), "SUBMITTED"));
            Assert.IsFalse(OrderMirror.ShouldVoidAtTill(Row(1), "CLOSED"));
            Assert.IsFalse(OrderMirror.ShouldVoidAtTill(Row(2), "REJECTED"));      // already raised: too late, the mismatch check shows it
            Assert.IsFalse(OrderMirror.ShouldVoidAtTill(Row(-1), "REJECTED"));
            Assert.IsFalse(OrderMirror.ShouldVoidAtTill(Row(1, tagged: true), "CANCELLED"));
        }

        [TestMethod]
        public void Register_RoundTripsTodayOnly()
        {
            string file = Path.Combine(Path.GetTempPath(), "mirror-test-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var today = DateTime.Now.Date;
                OrderMirror.Remember(file, new MirrorEvent(DateTime.UtcNow.AddDays(-1), "OLD-1", MirrorAction.Accept, 10));
                OrderMirror.Remember(file, new MirrorEvent(DateTime.UtcNow, "96-54", MirrorAction.Accept, 20));
                OrderMirror.Remember(file, new MirrorEvent(DateTime.UtcNow, "M3-93", MirrorAction.Reject, 0));
                var list = OrderMirror.LoadToday(file, today);
                Assert.AreEqual(2, list.Count);
                var state = new MirrorState { Today = list };
                Assert.AreEqual(1, state.AcceptedToday);
                Assert.AreEqual(1, state.RejectedToday);
                StringAssert.StartsWith(state.TodayText, "Σήμερα: 1 δεκτή · 1 απόρριψη · τελευταία ");
            }
            finally { File.Delete(file); }
            Assert.AreEqual(0, OrderMirror.LoadToday(file + ".missing", DateTime.Today).Count);
            Assert.AreEqual("", new MirrorState().TodayText);
        }

        [TestMethod]
        public void WaitingRow_UpdatesInPlaceAndLeavesWithTheVerdict()
        {
            var now = new DateTime(2026, 10, 1, 11, 30, 0, DateTimeKind.Utc);
            var row = new WaitingRow(new WaitingOrder(Order(), Row(1), now));
            Assert.AreEqual("96-54", row.Token);
            StringAssert.Contains(row.Who, "ΔΟΚΙΜΗ · 4,00 € · Παραλαβή");
            int changes = 0;
            row.PropertyChanged += (s, e) => changes++;
            row.Update(new WaitingOrder(Order(), Row(1), now.AddMinutes(5)));
            Assert.AreEqual("Στο ταμείο · 10′", row.StateText);
            Assert.AreEqual(2, row.Level);
            Assert.IsTrue(row.HasHint);
            Assert.AreEqual(2, changes);   // StateText + Level; the hint text is the same at level 1 and 2

            row.Leave(new MirrorEvent(now, "96-54", MirrorAction.Accept, 20));
            Assert.AreEqual("Δεκτή · 20′", row.StateText);
            Assert.AreEqual(3, row.Level);
            Assert.IsFalse(row.HasHint);
            Assert.IsTrue(row.Leaving);
            row.Update(new WaitingOrder(Order(), Row(1), now));   // ignored once leaving
            Assert.AreEqual("Δεκτή · 20′", row.StateText);

            var rejected = new WaitingRow(new WaitingOrder(Order(), Row(1), now));
            rejected.Leave(new MirrorEvent(now, "96-54", MirrorAction.Reject, 0));
            Assert.AreEqual("Απορρίφθηκε", rejected.StateText);
            Assert.AreEqual(4, rejected.Level);
        }
    }
}
