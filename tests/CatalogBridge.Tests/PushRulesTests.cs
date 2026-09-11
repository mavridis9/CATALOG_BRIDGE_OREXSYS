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
            Assert.IsFalse(PushRules.IsHeldForSizes(Product(Xl), Separate));
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

        // Option-group mode is not built yet: sized products wait and are reported as held.
        [TestMethod]
        public void OptionGroupModeHoldsSizedProductsBack()
        {
            var product = Product(Xl);

            Assert.AreEqual(0, PushRules.SendableUnits(product, Options).Count);
            Assert.IsTrue(PushRules.IsHeldForSizes(product, Options));
            Assert.IsFalse(PushRules.IsHeldForSizes(Product(), Options), "no sizes: nothing to hold");
        }

        [TestMethod]
        public void HeldIsOnlyReportedForWhatWouldOtherwiseBeSent()
        {
            var inactive = Product(Xl); inactive.IsActive = false;
            var extra = Product(Xl); extra.IsExtra = true;
            var allLinked = Product(Xl);
            allLinked.ExternalCodes.Add("1"); allLinked.ExternalCodes.Add("2"); allLinked.LinkSizes["2"] = 17;
            var otherCategory = Product(Xl);
            var onlyCategory9 = new Settings { SizeMode = SizeMode.OptionGroup, SyncAllCategories = false, SyncCategoryIds = "9" };

            Assert.IsFalse(PushRules.IsHeldForSizes(inactive, Options));
            Assert.IsFalse(PushRules.IsHeldForSizes(extra, Options));
            Assert.IsFalse(PushRules.IsHeldForSizes(allLinked, Options));
            Assert.IsFalse(PushRules.IsHeldForSizes(otherCategory, onlyCategory9));
        }

        [TestMethod]
        public void InactiveOrUnpricedProductIsNotSentBySync()
        {
            var inactive = Product(Xl); inactive.IsActive = false;
            var unpriced = Product(Xl); unpriced.Price = 0m;

            Assert.AreEqual(0, PushRules.SendableUnits(inactive, Separate).Count);
            Assert.AreEqual(0, PushRules.SendableUnits(unpriced, Separate).Count);
            // ...but Αποστολή still lists them, as before, so a technician can send on purpose.
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
