using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // A till order goes to PointToOrder with the SAME total (owner, 22/09/2026): products on
    // the platform go as themselves (same price, or refused); the rest ride on two hidden
    // placeholders, «Αγορά στο κατάστημα» × euros and «(λεπτά)» × cents.
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
        private static readonly (long, long) Placeholders = (901, 902);
        private static readonly (long, long) NoPlaceholders = (0, 0);

        [TestMethod]
        public void ProductsOnThePlatformGoAsThemselves()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 2, 4m), L(2, 203, 17, "ΤΟΣΤ XL", 1, 4m) };

            Assert.IsNull(TillOrderSender.BuildPlatformLines(lines, Offers, Prices, Placeholders, out var pl));
            CollectionAssert.AreEqual(new[] { (27121L, 2), (26890L, 1) }, pl.Select(x => (x.OfferId, x.Quantity)).ToArray());
        }

        // Stratos's real order #5: «ΠΡΟΙΟΝ 13%» 100,00 is not online → 100 × 1,00 placeholder.
        [TestMethod]
        public void ProductNotOnThePlatformRidesOnThePlaceholders()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 1, 4m), L(2, 200, 0, "ΠΡΟΙΟΝ 13%", 1, 100m) };

            Assert.IsNull(TillOrderSender.BuildPlatformLines(lines, Offers, Prices, Placeholders, out var pl));
            CollectionAssert.AreEqual(new[] { (27121L, 1), (901L, 100) }, pl.Select(x => (x.OfferId, x.Quantity)).ToArray());
            StringAssert.Contains(pl[1].Label, "ΠΡΟΙΟΝ 13%");
        }

        [TestMethod]
        public void CentsGoOnTheCentPlaceholder()
        {
            var lines = new[] { L(1, 999, 0, "ΚΑΦΕΣ", 1, 2.35m), L(2, 998, 0, "ΝΕΡΟ", 2, 0.5m) };   // 3,35

            Assert.IsNull(TillOrderSender.BuildPlatformLines(lines, Offers, Prices, Placeholders, out var pl));
            CollectionAssert.AreEqual(new[] { (901L, 3), (902L, 35) }, pl.Select(x => (x.OfferId, x.Quantity)).ToArray());
        }

        [TestMethod]
        public void WholeEurosNeedNoCentLine_AndUnder1EuroNeedsNoEuroLine()
        {
            TillOrderSender.BuildPlatformLines(new[] { L(1, 999, 0, "Χ", 1, 12m) }, Offers, Prices, Placeholders, out var a);
            TillOrderSender.BuildPlatformLines(new[] { L(1, 999, 0, "Χ", 1, 0.6m) }, Offers, Prices, Placeholders, out var b);

            CollectionAssert.AreEqual(new[] { (901L, 12) }, a.Select(x => (x.OfferId, x.Quantity)).ToArray());
            CollectionAssert.AreEqual(new[] { (902L, 60) }, b.Select(x => (x.OfferId, x.Quantity)).ToArray());
        }

        // A product that IS online but at another price is a catalogue to fix - refused, not
        // hidden in the placeholder.
        [TestMethod]
        public void DifferentPriceRefusesTheWholeOrder()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 1, 4.50m), L(2, 200, 0, "ΠΡΟΙΟΝ 13%", 1, 100m) };

            string? p = TillOrderSender.BuildPlatformLines(lines, Offers, Prices, Placeholders, out var pl);
            StringAssert.Contains(p!, "τιμή ταμείου 4,50, τιμή PointToOrder 4,00");
            Assert.AreEqual(0, pl.Count);
        }

        [TestMethod]
        public void WithoutPlaceholdersAMissingProductRefuses()
        {
            var lines = new[] { L(1, 200, 0, "ΠΡΟΙΟΝ 13%", 1, 100m) };

            StringAssert.Contains(TillOrderSender.BuildPlatformLines(lines, Offers, Prices, NoPlaceholders, out _)!, "δεν υπάρχει στο PointToOrder");
        }

        // A size without its own offer (option-group mode) is simply "not online": placeholder.
        [TestMethod]
        public void SizeWithoutItsOwnOfferRidesOnThePlaceholder()
        {
            var lines = new[] { L(1, 203, 11, "ΤΟΣΤ ΜΙΣΗ", 1, 0.6m) };

            Assert.IsNull(TillOrderSender.BuildPlatformLines(lines, Offers, Prices, Placeholders, out var pl));
            Assert.AreEqual((902L, 60), (pl.Single().OfferId, pl.Single().Quantity));
        }

        [TestMethod]
        public void FractionalQuantityOfAPlatformProductRefuses()
        {
            var lines = new[] { L(1, 205, 0, "ΠΙΤΑ ΓΥΡΟΣ", 1.5m, 4m) };

            StringAssert.Contains(TillOrderSender.BuildPlatformLines(lines, Offers, Prices, Placeholders, out _)!, "ακέραιες");
        }

        [TestMethod]
        public void ExternalIdAndTagAreTiedToTheOrderNumber()
        {
            Assert.AreEqual("OREXSYS-5", TillOrderSender.Plan.ExternalIdFor(5));
            Assert.AreEqual("[OREXSYS#", EfomTriggerGuard.SentOrderTag);
        }
    }
}
