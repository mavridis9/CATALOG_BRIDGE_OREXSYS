using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // A till order goes to PointToOrder only when it can be IDENTICAL (owner, 22/09/2026):
    // every line a product that exists there, same price; otherwise refused, nothing sent.
    [TestClass]
    public class TillOrderSenderTests
    {
        private static TillOrderSender.Line L(long ordd, long itms, long itmv, string name, decimal qty, decimal price) =>
            new(ordd, itms, itmv, name, qty, price, qty * price);

        private static readonly Dictionary<(long, long), long> Offers = new()
        {
            [(205, 0)] = 27121,   // ΠΙΤΑ ΓΥΡΟΣ
            [(203, 0)] = 26889,   // ΤΟΣΤ
            [(203, 17)] = 26890,  // ΤΟΣΤ XL (separate item)
        };
        private static readonly Dictionary<long, decimal> Prices = new() { [27121] = 4m, [26889] = 1m, [26890] = 4m };

        [TestMethod]
        public void IdenticalOrderIsSendable()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 2, 4m), L(2, 203, 17, "ΤΟΣΤ XL", 1, 4m) };

            Assert.IsNull(TillOrderSender.CheckIdentical(lines, Offers, Prices, out var map));
            Assert.AreEqual(27121, map[1]);
            Assert.AreEqual(26890, map[2]);
        }

        [TestMethod]
        public void ProductNotOnPointToOrderRefusesTheWholeOrder()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 1, 4m), L(2, 200, 0, "ΠΡΟΙΟΝ 13%", 1, 100m) };

            string? p = TillOrderSender.CheckIdentical(lines, Offers, Prices, out var map);
            Assert.IsNotNull(p);
            StringAssert.Contains(p, "«ΠΡΟΙΟΝ 13%» δεν υπάρχει στο PointToOrder");
            Assert.AreEqual(0, map.Count, "nothing is mapped when the order is refused");
        }

        [TestMethod]
        public void DifferentPriceRefuses()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 1, 4.50m) };

            StringAssert.Contains(TillOrderSender.CheckIdentical(lines, Offers, Prices, out _)!, "τιμή ταμείου 4,50, τιμή PointToOrder 4,00");
        }

        [TestMethod]
        public void SizeWithoutItsOwnOfferRefuses()
        {
            var lines = new[] { L(1, 203, 11, "ΤΟΣΤ ΜΙΣΗ", 1, 0.6m) };

            StringAssert.Contains(TillOrderSender.CheckIdentical(lines, Offers, Prices, out _)!, "(αυτό το μέγεθος)");
        }

        [TestMethod]
        public void FractionalQuantityRefuses()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 1.5m, 4m) };

            StringAssert.Contains(TillOrderSender.CheckIdentical(lines, Offers, Prices, out _)!, "ακέραιες");
        }

        [TestMethod]
        public void ExternalIdAndTagAreTiedToTheOrderNumber()
        {
            Assert.AreEqual("OREXSYS-5", TillOrderSender.Plan.ExternalIdFor(5));
            Assert.AreEqual("[OREXSYS#", EfomTriggerGuard.SentOrderTag);
        }
    }
}
