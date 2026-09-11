using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    [TestClass]
    public class PushRulesTests
    {
        private static readonly Settings Separate = new();
        private static readonly Settings Options = new() { SizeMode = SizeMode.OptionGroup };

        private static OrexsysProduct Product(params OrexsysSize[] sizes)
        {
            var product = new OrexsysProduct
            {
                ItmsId = 203, Name = "ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ", CategoryId = 8, IsActive = true, Price = 1.00m,
            };
            product.Sizes.AddRange(sizes);
            return product;
        }

        private static OrexsysSize Xl => new(17, "XL", 4.00m);

        [TestMethod]
        public void ProductWithoutSizesIsOneUnit()
        {
            var units = PushRules.SendableUnits(Product(), Separate);

            Assert.AreEqual(1, units.Count);
            Assert.AreEqual(0, units[0].ItmvId);
            Assert.AreEqual("ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ", units[0].DefaultName);
            Assert.AreEqual(1.00m, units[0].Price);
        }

        // Separate items: «ΤΟΣΤ» 1,00 (ITMVID 0) and «ΤΟΣΤ XL» 4,00 (ITMVID 17), each its own
        // offer and EFIC row - the way Wolt/efood sizes already live in EFIC at customers.
        [TestMethod]
        public void SizesBecomeSeparateItemsWithTheirOwnPriceAndItmvId()
        {
            var units = PushRules.SendableUnits(Product(Xl), Separate);

            CollectionAssert.AreEqual(new long[] { 0, 17 }, units.Select(u => u.ItmvId).ToArray());
            CollectionAssert.AreEqual(new[] { "ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ", "ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ XL" },
                units.Select(u => u.DefaultName).ToArray());
            CollectionAssert.AreEqual(new[] { 1.00m, 4.00m }, units.Select(u => u.Price).ToArray());
            Assert.AreEqual(0, units.Sum(u => u.Options.Count), "no option group in this mode");
        }

        // A size added after the product went online is sent alone; what is online stays.
        [TestMethod]
        public void OnlyUnlinkedSizesArePending()
        {
            var product = Product(Xl, new OrexsysSize(11, "ΜΙΣΗ ΜΕΡΙΔΑ", 0.60m));
            product.ExternalCodes.Add("26745");          // pre-size row: ITMVID 0
            product.ExternalCodes.Add("26990");
            product.LinkSizes["26990"] = 17;

            var units = PushRules.PendingUnits(product, Separate);

            CollectionAssert.AreEqual(new long[] { 11 }, units.Select(u => u.ItmvId).ToArray());
        }

        // A size without a price in the configured list, or inactive there, is not a unit.
        [TestMethod]
        public void UnpricedSizeIsSkipped()
        {
            var units = PushRules.SendableUnits(Product(Xl, new OrexsysSize(1, "ΔΙΠΛΟ", null)), Separate);

            CollectionAssert.AreEqual(new long[] { 0, 17 }, units.Select(u => u.ItmvId).ToArray());
        }

        // Option-group mode: ONE product at the cheapest price, the sizes as choices with the
        // difference as surcharge - «ΤΟΣΤ» 1,00 with Κανονικό +0,00 / XL +3,00. Written to
        // EFIC once, ITMVID 0 (the till gets the size only as text - owner's choice).
        [TestMethod]
        public void OptionGroupModeSendsOneProductWithSizeChoices()
        {
            var units = PushRules.SendableUnits(Product(Xl), Options);

            Assert.AreEqual(1, units.Count);
            Assert.AreEqual(0, units[0].ItmvId);
            Assert.AreEqual(1.00m, units[0].Price);
            Assert.AreEqual("ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ", units[0].DefaultName);
            CollectionAssert.AreEqual(new[] { "Κανονικό", "XL" }, units[0].Options.Select(o => o.Name).ToArray());
            CollectionAssert.AreEqual(new[] { 0.00m, 3.00m }, units[0].Options.Select(o => o.Surcharge).ToArray());
        }

        // No size may carry a negative surcharge: the base is the cheapest price, and the
        // product's own price becomes «Κανονικό +x» when a size is cheaper than it.
        [TestMethod]
        public void OptionGroupBaseIsTheCheapestPrice()
        {
            var product = Product(new OrexsysSize(11, "ΜΙΣΗ ΜΕΡΙΔΑ", 0.60m), Xl);

            var unit = PushRules.SendableUnits(product, Options).Single();

            Assert.AreEqual(0.60m, unit.Price);
            CollectionAssert.AreEqual(new[] { 0.40m, 0.00m, 3.40m }, unit.Options.Select(o => o.Surcharge).ToArray());
        }

        // Only priced sizes become choices; a product priced only through its sizes still goes.
        [TestMethod]
        public void OptionGroupSkipsUnpricedSizesAndWorksWithoutABasePrice()
        {
            var product = Product(Xl, new OrexsysSize(1, "ΔΙΠΛΟ", null));
            product.Price = 0m;

            var unit = PushRules.SendableUnits(product, Options).Single();

            Assert.AreEqual(4.00m, unit.Price);
            CollectionAssert.AreEqual(new[] { "XL" }, unit.Options.Select(o => o.Name).ToArray());
        }

        // A product already online is left alone, even if sizes were added afterwards.
        [TestMethod]
        public void OptionGroupModeDoesNotTouchLinkedProducts()
        {
            var linked = Product(Xl);
            linked.ExternalCodes.Add("26889");

            Assert.AreEqual(0, PushRules.PendingUnits(linked, Options).Count);
        }

        [TestMethod]
        public void NonEligibleProductsHaveNoUnitsInEitherMode()
        {
            var extra = Product(Xl); extra.IsExtra = true;
            var onlyCategory9 = new Settings { SizeMode = SizeMode.OptionGroup, SyncAllCategories = false, SyncCategoryIds = "9" };

            Assert.AreEqual(0, PushRules.PendingUnits(extra, Options).Count);
            Assert.AreEqual(0, PushRules.PendingUnits(extra, Separate).Count);
            Assert.AreEqual(0, PushRules.PendingUnits(Product(Xl), onlyCategory9).Count);
        }

        [TestMethod]
        public void InactiveOrUnpricedProductIsNotSentBySync()
        {
            var inactive = Product(Xl); inactive.IsActive = false;
            var unpriced = Product(Xl); unpriced.Price = 0m;

            Assert.AreEqual(0, PushRules.SendableUnits(inactive, Separate).Count);
            // Unpriced itself but with a priced size: the size still goes (as its own item).
            Assert.AreEqual(1, PushRules.SendableUnits(unpriced, Separate).Count);
            // ...and Αποστολή still lists inactive ones, so a technician can send on purpose.
            Assert.AreEqual(2, PushRules.PendingUnits(inactive, Separate).Count);
        }

        [TestMethod]
        public void SizesTextShowsEachSizeWithItsPriceOrADash()
        {
            var product = Product(new OrexsysSize(5, "0,5L", 3.00m), new OrexsysSize(7, "1L", null),
                                  new OrexsysSize(8, "3L", 0m));

            Assert.AreEqual("0,5L 3,00 · 1L — · 3L —", product.SizesText);
        }
    }
}
