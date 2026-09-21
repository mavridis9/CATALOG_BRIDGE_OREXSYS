using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // The 21/09 incident on Stavros: the connector sent orders as «PO4897», the app wrote its
    // EFIC links under «1», and the first real order arrived with no product on any line.
    [TestClass]
    public class OrderFeedCheckTests
    {
        private static OrderFeedCode Arrived(string code, int unmatched = 3) =>
            new(code, 1, unmatched, new DateTime(2026, 9, 21, 13, 52, 0));

        [TestMethod]
        public void OrdersUnderAnotherCodeAreAMismatch()
        {
            var r = OrderFeedCheck.Evaluate("1", new[] { Arrived("PO4897") });

            Assert.IsTrue(r.Mismatch);
            StringAssert.Contains(r.Message, "«PO4897»");
            StringAssert.Contains(r.Message, "«1»");
            StringAssert.Contains(r.Message, "3 γραμμές χωρίς προϊόν");
        }

        [TestMethod]
        public void OrdersUnderOurOwnCodeAreFine()
        {
            var r = OrderFeedCheck.Evaluate("PO4897", new[] { Arrived("PO4897", 2) });

            Assert.IsFalse(r.Mismatch);
            Assert.AreEqual("", r.Message);
        }

        // STOA: only Wolt/efood orders, which the loader already leaves out - nothing arrives.
        [TestMethod]
        public void NoP2OOrdersIsNoAlarm()
        {
            Assert.IsFalse(OrderFeedCheck.Evaluate("1", Array.Empty<OrderFeedCode>()).Mismatch);
            Assert.IsFalse(OrderFeedCheck.Evaluate("", Array.Empty<OrderFeedCode>()).Mismatch);
        }

        [TestMethod]
        public void NoConnectionChosenButOrdersArriving()
        {
            var r = OrderFeedCheck.Evaluate("", new[] { Arrived("PO4897") });

            Assert.IsTrue(r.Mismatch);
            StringAssert.Contains(r.Message, "δεν έχει επιλεγεί");
        }

        // The fix differs: an unknown code needs EFST corrected in Orexsys; a code EFST already
        // has only needs choosing in Ρυθμίσεις.
        [TestMethod]
        public void MessageSaysWhatToFix()
        {
            var unknown = OrderFeedCheck.Evaluate("1", new[] { Arrived("PO4897") });
            var known = OrderFeedCheck.Evaluate("1", new[] { new OrderFeedCode("PO4897", 1, 3, null, InEfst: true) });

            StringAssert.Contains(unknown.Message, "Διορθώστε στο Orexsys τη γραμμή PointToOrder του EFST");
            StringAssert.Contains(known.Message, "Επιλέξτε στις Ρυθμίσεις τη «Σύνδεση P2O» «PO4897»");
            Assert.IsFalse(known.Message.Contains("Διορθώστε στο Orexsys"));
        }

        [TestMethod]
        public void CodesAreComparedTrimmed()
        {
            Assert.IsFalse(OrderFeedCheck.Evaluate(" PO4897 ", new[] { Arrived("PO4897 ") }).Mismatch);
        }
    }
}
