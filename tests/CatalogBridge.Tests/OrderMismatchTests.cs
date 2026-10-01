using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // «Ασυμφωνίες παραγγελιών» (owner, 23/09/2026): cancelled on one side, active on the other.
    [TestClass]
    public class OrderMismatchTests
    {
        private static readonly DateTime D = new(2026, 9, 23, 10, 0, 0);
        private static readonly Dictionary<long, string> NoRegistry = new();

        private static OrexsysOrderSide O(long id, bool cancelled, string platformOrderNumber = "", decimal total = 10m) =>
            new(id, D, 2202, "Stavros", total, cancelled, platformOrderNumber);

        private static readonly DateTime Now = D.AddHours(2);
        private static readonly EfomEntry[] NoEfom = Array.Empty<EfomEntry>();

        private static PlatformOrderSide P(string token, string status, string comment = "", string uuid = "u", decimal total = 10m, DateTime? created = null) =>
            new(uuid + token, token, status, comment, 1003, "Stavros", total, created ?? D.ToUniversalTime());

        private static List<Mismatch> Cmp(IEnumerable<OrexsysOrderSide> o, IReadOnlyDictionary<long, string> reg, IEnumerable<PlatformOrderSide> p, IEnumerable<EfomEntry>? efom = null) =>
            OrderMismatch.Compare(o, reg, p, efom ?? EfomFor(p), Now);

        // By default every platform order is «in Orexsys» (an EFOM row with an ORDMID), so the
        // older tests stay about the cancelled/active question only.
        private static IEnumerable<EfomEntry> EfomFor(IEnumerable<PlatformOrderSide> p) =>
            p.Where(x => x.Token.Length > 0).Select(x => new EfomEntry(x.Token, 1, 2));

        // The owner's case: an online order cancelled at the till, still CLOSED on the platform.
        [TestMethod]
        public void OnlineOrderCancelledAtTheTill_IsAMismatch_ToCancelOnPlatform()
        {
            var found = Cmp(new[] { O(11, cancelled: true, "2K-9A") }, NoRegistry, new[] { P("2K-9A", "CLOSED") });

            var m = found.Single();
            Assert.AreEqual(MismatchKind.CancelledInOrexsysActiveOnPlatform, m.Kind);
            Assert.AreEqual(11, m.OrdmId);
            Assert.AreEqual("2K-9A", m.Token);
            Assert.AreEqual("u2K-9A", m.Uuid);
            StringAssert.Contains(m.Action, "PointToOrder");
        }

        [TestMethod]
        public void CancelledOnPlatform_ActiveInOrexsys_IsTheOtherKind()
        {
            var found = Cmp(new[] { O(8, cancelled: false, "48-PY") }, NoRegistry, new[] { P("48-PY", "CANCELLED") });

            Assert.AreEqual(MismatchKind.CancelledOnPlatformActiveInOrexsys, found.Single().Kind);
            StringAssert.Contains(found.Single().Action, "Orexsys");
        }

        [TestMethod]
        public void BothActive_OrBothCancelled_IsFine()
        {
            var platform = new[] { P("A", "CLOSED"), P("B", "CANCELLED"), P("C", "DELETED") };
            var found = Cmp(
                new[] { O(1, false, "A"), O(2, true, "B"), O(3, true, "C") }, NoRegistry, platform);

            Assert.AreEqual(0, found.Count);
        }

        // A till order sent to the platform is known through the local registry, not EFOM.
        [TestMethod]
        public void TillOrderSent_FoundThroughTheRegistry()
        {
            var registry = new Dictionary<long, string> { [10] = "TP-5Z" };
            var found = Cmp(new[] { O(10, cancelled: true) }, registry, new[] { P("TP-5Z", "CLOSED") });

            Assert.AreEqual(10, found.Single().OrdmId);
        }

        // Sent from another PC: no registry here, but the platform order's comment carries the tag.
        [TestMethod]
        public void TillOrderSentElsewhere_FoundThroughTheCommentTag()
        {
            var found = Cmp(new[] { O(9, cancelled: true) }, NoRegistry,
                new[] { P("Z7-9Z", "CLOSED", "[OREXSYS#9] Αγορά στο κατάστημα") });

            Assert.AreEqual("Z7-9Z", found.Single().Token);
        }

        [TestMethod]
        public void OrdersWithoutACounterpart_AreNotMismatches()
        {
            var found = Cmp(
                new[] { O(1, cancelled: true), O(2, cancelled: true, "XX-XX") },   // plain till order; token not on the platform
                NoRegistry,
                new[] { P("YY-YY", "CLOSED"), P("", "DRAFT") });                   // platform orders that are not ours

            Assert.AreEqual(0, found.Count);
        }

        [TestMethod]
        public void DraftsNeverCount()
        {
            var found = Cmp(new[] { O(5, cancelled: true) }, new Dictionary<long, string> { [5] = "" },
                new[] { new PlatformOrderSide("u", "", "DRAFT", "[OREXSYS#5]", 0, "", 0m, D) });

            Assert.AreEqual(0, found.Count);
        }

        // ---- per-customer totals (owner, 23/09: «calculate the total of Orexsys and P2O on every
        // customer and compare it», linked customers only) ----------------------------------

        private static readonly LinkedCustomer Stavros = new(new OrexsysCustomer(2202, "Stavros", "505060", "stavros@orexsys.gr", ""), 1003);

        [TestMethod]
        public void SameOrdersOnBothSides_NoReport()
        {
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { O(11, false, "2K-9A", 6m), O(12, true, "", 150m) },            // cancelled one does not count
                NoRegistry,
                new[] { P("2K-9A", "CLOSED", uuid: "u1", total: 6m),
                        P("X7-N8", "CANCELLED", "[OREXSYS#12]", "u2", 150m) });

            Assert.AreEqual(0, found.Count);
        }

        [TestMethod]
        public void OnlineOrderThatNeverReachedOrexsys_ShowsInTheDifference()
        {
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { O(11, false, "2K-9A", 6m) }, NoRegistry,
                new[] { P("2K-9A", "CLOSED", uuid: "u1", total: 6m),
                        P("AB-12", "CLOSED", uuid: "u2", total: 9.5m) });

            var c = found.Single();
            Assert.AreEqual(1, c.OrexsysCount); Assert.AreEqual(6m, c.OrexsysTotal);
            Assert.AreEqual(2, c.PlatformCount); Assert.AreEqual(15.5m, c.PlatformTotal);
            Assert.AreEqual(-9.5m, c.Difference);
            StringAssert.Contains(c.Details, "AB-12 9,50 μόνο στο PointToOrder");
        }

        [TestMethod]
        public void TillOrderNotSent_ShowsInTheDifference()
        {
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { O(20, false, "", 12m) }, NoRegistry, Array.Empty<PlatformOrderSide>());

            StringAssert.Contains(found.Single().Details, "#20 12,00 μόνο στο Orexsys");
            Assert.AreEqual(12m, found.Single().Difference);
        }

        [TestMethod]
        public void SameOrderDifferentAmount_IsNamed()
        {
            var registry = new Dictionary<long, string> { [10] = "TP-5Z" };
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { O(10, false, "", 14.56m) }, registry,
                new[] { P("TP-5Z", "CLOSED", total: 14m) });

            StringAssert.Contains(found.Single().Details, "#10/TP-5Z: 14,56 ≠ 14,00");
        }

        [TestMethod]
        public void OtherCustomersOrders_DoNotLeakIn()
        {
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { new OrexsysOrderSide(30, D, 2200, "Stratos", 100m, false, "") },      // Stratos, not linked here
                NoRegistry,
                new[] { new PlatformOrderSide("u", "NN-3K", "CLOSED", "", 1597, "Stratos", 100m, D) });

            Assert.AreEqual(0, found.Count);
        }

        // ---- same order, different amount / never reached Orexsys (owner, 23/09) ----------

        [TestMethod]
        public void SameOrderDifferentAmount_IsReported_WithoutAButton()
        {
            var found = Cmp(new[] { O(10, false, "TP-5Z", 14.56m) }, NoRegistry, new[] { P("TP-5Z", "CLOSED", total: 14m) });

            var m = found.Single();
            Assert.AreEqual(MismatchKind.DifferentAmount, m.Kind);
            StringAssert.Contains(m.Short, "14,56");
            StringAssert.Contains(m.Short, "14,00");
            Assert.IsFalse(m.HasAction);
        }

        [TestMethod]
        public void AmountDifference_OnACancelledOrder_IsNotReported() =>
            Assert.AreEqual(0, Cmp(new[] { O(1, true, "A", 0m) }, NoRegistry, new[] { P("A", "CANCELLED", total: 5m) }).Count);

        [TestMethod]
        public void PlatformOrderTheConnectorNeverBrought_IsReported()
        {
            var old = D.AddHours(-3).ToUniversalTime();
            var found = Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("M6-2E", "CLOSED", total: 3m, created: old) }, NoEfom);

            var m = found.Single();
            Assert.AreEqual(MismatchKind.PlatformOrderMissingInOrexsys, m.Kind);
            Assert.AreEqual(0, m.OrdmId);
            Assert.AreEqual("M6-2E", m.Token);
            StringAssert.Contains(m.Short, "καμία γραμμή EFOM");
            Assert.IsFalse(m.HasAction);
        }

        [TestMethod]
        public void PlatformOrderOrexsysRejected_IsReported_WithTheStatus()
        {
            var found = Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("32-5P", "CLOSED", total: 10m, created: D.AddHours(-3).ToUniversalTime()) },
                new[] { new EfomEntry("32-5P", null, -1) });

            StringAssert.Contains(found.Single().Short, "STATUS -1");
        }

        [TestMethod]
        public void PlatformOrderThatIsInOrexsys_IsNotMissing_EvenOutsideTheWindow() =>
            Assert.AreEqual(0, Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("62-87", "CLOSED", created: D.AddDays(-2).ToUniversalTime()) },
                new[] { new EfomEntry("62-87", 1, 2) }).Count);

        [TestMethod]
        public void FreshPlatformOrder_GetsTheConnectorItsTime() =>
            Assert.AreEqual(0, Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("NEW-1", "CLOSED", created: Now.AddMinutes(-5)) }, NoEfom).Count);

        [TestMethod]
        public void OurOwnCopyRejectedByTheLoopGuard_IsNotMissing() =>
            Assert.AreEqual(0, Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("NN-3K", "CLOSED", "[OREXSYS#5] Αγορά στο κατάστημα", created: D.AddHours(-3).ToUniversalTime()) },
                new[] { new EfomEntry("NN-3K", null, -1) }).Count);

        [TestMethod]
        public void CancelledAndDraftPlatformOrders_AreNeverMissing() =>
            Assert.AreEqual(0, Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("C1", "CANCELLED", created: D.AddHours(-3)), new PlatformOrderSide("u", "", "DRAFT", "", 0, "", 4m, D.AddHours(-3)) },
                NoEfom).Count);

        // Since the shop decides (01/10): a platform order the shop REJECTED is not «missing» in
        // Orexsys - it was never meant to be there; and one still SUBMITTED is waiting for the
        // shop, which the «Online παραγγελίες» board shows - not a mismatch either.
        [TestMethod]
        public void RejectedPlatformOrder_IsNeverMissing() =>
            Assert.AreEqual(0, Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("R1-11", "REJECTED", created: D.AddHours(-3).ToUniversalTime()) },
                new[] { new EfomEntry("R1-11", null, -1) }).Count);

        [TestMethod]
        public void PlatformOrderStillWaitingForTheShop_IsNotMissing() =>
            Assert.AreEqual(0, Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("W1-11", "SUBMITTED", created: D.AddHours(-3).ToUniversalTime()), P("W2-22", "SUBMITTED", created: D.AddHours(-3).ToUniversalTime()) },
                new[] { new EfomEntry("W1-11", null, 1) }).Count);

        [TestMethod]
        public void AcceptedOnThePlatformButStillWaitingAtTheTill_SaysSo()
        {
            var found = Cmp(Array.Empty<OrexsysOrderSide>(), NoRegistry,
                new[] { P("S1-11", "CLOSED", created: D.AddHours(-3).ToUniversalTime()) },
                new[] { new EfomEntry("S1-11", null, 1) });
            StringAssert.Contains(found.Single().Short, "περιμένει ακόμα στο ταμείο");
        }

        [TestMethod]
        public void ACancelledOrexsysOrderWithZeroTotal_ShowsThePlatformTotal()
        {
            var found = Cmp(new[] { O(11, cancelled: true, "2K-9A", total: 0m) }, NoRegistry,
                new[] { P("2K-9A", "CLOSED", total: 6m) });

            Assert.AreEqual(6m, found.Single().Total);
            StringAssert.Contains(found.Single().Title, "6,00 €");
        }
    }
}
