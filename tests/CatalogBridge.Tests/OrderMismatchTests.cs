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
            new(id, D, "Stavros", total, cancelled, platformOrderNumber);

        private static PlatformOrderSide P(string token, string status, string comment = "", string uuid = "u") =>
            new(uuid + token, token, status, comment, "Stavros", 10m);

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
                new[] { new PlatformOrderSide("u", "", "DRAFT", "[OREXSYS#5]", "", 0m) });

            Assert.AreEqual(0, found.Count);
        }

        [TestMethod]
        public void ACancelledOrexsysOrderWithZeroTotal_ShowsThePlatformTotal()
        {
            var found = OrderMismatch.Compare(new[] { O(11, cancelled: true, "2K-9A", total: 0m) }, NoRegistry,
                new[] { new PlatformOrderSide("u", "2K-9A", "CLOSED", "", "Stavros", 6m) });

            Assert.AreEqual(6m, found.Single().Total);
            StringAssert.Contains(found.Single().Title, "6,00 €");
        }
    }
}
