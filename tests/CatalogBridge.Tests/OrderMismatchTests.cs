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

        private static PlatformOrderSide P(string token, string status, string comment = "", string uuid = "u") =>
            new(uuid + token, token, status, comment, 1003, "Stavros", 10m);

        // The owner's case: an online order cancelled at the till, still CLOSED on the platform.
        [TestMethod]
        public void OnlineOrderCancelledAtTheTill_IsAMismatch_ToCancelOnPlatform()
        {
            var found = OrderMismatch.Compare(new[] { O(11, cancelled: true, "2K-9A") }, NoRegistry, new[] { P("2K-9A", "CLOSED") });

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
            var found = OrderMismatch.Compare(new[] { O(8, cancelled: false, "48-PY") }, NoRegistry, new[] { P("48-PY", "CANCELLED") });

            Assert.AreEqual(MismatchKind.CancelledOnPlatformActiveInOrexsys, found.Single().Kind);
            StringAssert.Contains(found.Single().Action, "Orexsys");
        }

        [TestMethod]
        public void BothActive_OrBothCancelled_IsFine()
        {
            var platform = new[] { P("A", "CLOSED"), P("B", "CANCELLED"), P("C", "DELETED") };
            var found = OrderMismatch.Compare(
                new[] { O(1, false, "A"), O(2, true, "B"), O(3, true, "C") }, NoRegistry, platform);

            Assert.AreEqual(0, found.Count);
        }

        // A till order sent to the platform is known through the local registry, not EFOM.
        [TestMethod]
        public void TillOrderSent_FoundThroughTheRegistry()
        {
            var registry = new Dictionary<long, string> { [10] = "TP-5Z" };
            var found = OrderMismatch.Compare(new[] { O(10, cancelled: true) }, registry, new[] { P("TP-5Z", "CLOSED") });

            Assert.AreEqual(10, found.Single().OrdmId);
        }

        // Sent from another PC: no registry here, but the platform order's comment carries the tag.
        [TestMethod]
        public void TillOrderSentElsewhere_FoundThroughTheCommentTag()
        {
            var found = OrderMismatch.Compare(new[] { O(9, cancelled: true) }, NoRegistry,
                new[] { P("Z7-9Z", "CLOSED", "[OREXSYS#9] Αγορά στο κατάστημα") });

            Assert.AreEqual("Z7-9Z", found.Single().Token);
        }

        [TestMethod]
        public void OrdersWithoutACounterpart_AreNotMismatches()
        {
            var found = OrderMismatch.Compare(
                new[] { O(1, cancelled: true), O(2, cancelled: true, "XX-XX") },   // plain till order; token not on the platform
                NoRegistry,
                new[] { P("YY-YY", "CLOSED"), P("", "DRAFT") });                   // platform orders that are not ours

            Assert.AreEqual(0, found.Count);
        }

        [TestMethod]
        public void DraftsNeverCount()
        {
            var found = OrderMismatch.Compare(new[] { O(5, cancelled: true) }, new Dictionary<long, string> { [5] = "" },
                new[] { new PlatformOrderSide("u", "", "DRAFT", "[OREXSYS#5]", 0, "", 0m) });

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
                new[] { new PlatformOrderSide("u1", "2K-9A", "CLOSED", "", 1003, "Stavros", 6m),
                        new PlatformOrderSide("u2", "X7-N8", "CANCELLED", "[OREXSYS#12]", 1003, "Stavros", 150m) });

            Assert.AreEqual(0, found.Count);
        }

        [TestMethod]
        public void OnlineOrderThatNeverReachedOrexsys_ShowsInTheDifference()
        {
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { O(11, false, "2K-9A", 6m) }, NoRegistry,
                new[] { new PlatformOrderSide("u1", "2K-9A", "CLOSED", "", 1003, "Stavros", 6m),
                        new PlatformOrderSide("u2", "AB-12", "CLOSED", "", 1003, "Stavros", 9.5m) });

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
                new[] { new PlatformOrderSide("u", "TP-5Z", "CLOSED", "", 1003, "Stavros", 14m) });

            StringAssert.Contains(found.Single().Details, "#10/TP-5Z: 14,56 ≠ 14,00");
        }

        [TestMethod]
        public void OtherCustomersOrders_DoNotLeakIn()
        {
            var found = OrderMismatch.CompareCustomers(new[] { Stavros },
                new[] { new OrexsysOrderSide(30, D, 2200, "Stratos", 100m, false, "") },      // Stratos, not linked here
                NoRegistry,
                new[] { new PlatformOrderSide("u", "NN-3K", "CLOSED", "", 1597, "Stratos", 100m) });

            Assert.AreEqual(0, found.Count);
        }

        [TestMethod]
        public void ACancelledOrexsysOrderWithZeroTotal_ShowsThePlatformTotal()
        {
            var found = OrderMismatch.Compare(new[] { O(11, cancelled: true, "2K-9A", total: 0m) }, NoRegistry,
                new[] { new PlatformOrderSide("u", "2K-9A", "CLOSED", "", 1003, "Stavros", 6m) });

            Assert.AreEqual(6m, found.Single().Total);
            StringAssert.Contains(found.Single().Title, "6,00 €");
        }
    }
}
