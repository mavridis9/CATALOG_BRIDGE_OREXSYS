using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // A price list per category (owner, 29/09/2026): STOA prices ΚΑΛΑΜΑΚΙ in list 1 and ΣΤΟΑ in
    // list 2, from one database.
    [TestClass]
    public class CategoryPriceListTests
    {
        [TestMethod]
        public void UnlistedCategoryUsesTheDefaultList()
        {
            var s = new Settings { PriceListId = 1, CategoryPriceLists = "21=2,22=2" };

            Assert.AreEqual(1, s.PriceListFor(8));
            Assert.AreEqual(2, s.PriceListFor(21));
            Assert.AreEqual(2, s.PriceListFor(22));
        }

        [TestMethod]
        public void EveryListInUseIsRead_DefaultIncluded()
        {
            var s = new Settings { PriceListId = 1, CategoryPriceLists = "21=2,22=2,30=5" };

            CollectionAssert.AreEquivalent(new long[] { 1, 2, 5 }, s.PriceListsInUse().ToList());
            CollectionAssert.AreEquivalent(new long[] { 3 }, new Settings { PriceListId = 3 }.PriceListsInUse().ToList());
        }

        [TestMethod]
        public void MapSurvivesGarbage_AndRoundTrips()
        {
            var map = Settings.ParsePriceListMap(" 21 = 2 , x=1, 5=, =3, 0=2, 7=0, 22=2,, 21=4 ");

            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(4, map[21], "the last one wins");
            Assert.AreEqual(2, map[22]);
            Assert.AreEqual("21=4,22=2", Settings.FormatPriceListMap(map));
            Assert.AreEqual(0, Settings.ParsePriceListMap("").Count);
            Assert.AreEqual(0, Settings.ParsePriceListMap(null!).Count);
        }

        private static OrexsysCategory Category(long id, params (long List, int Priced)[] priced) =>
            new() { Id = id, Name = "C" + id, Total = 12, PricedByList = priced.ToDictionary(p => p.List, p => p.Priced) };

        private static readonly Dictionary<long, long> NothingSaved = new();

        // The picker opens each category on the list that actually holds its prices.
        [TestMethod]
        public void CategoryPricedOnlyInAnotherList_OpensOnThatList()
        {
            var stoa = Category(21, (2, 12));

            Assert.AreEqual(2, CategoryPickerWindow.InitialPriceList(stoa, NothingSaved, defaultList: 1));
            Assert.AreEqual(2, stoa.BestPriceList);
            Assert.AreEqual(0, stoa.PricedIn(1));
        }

        [TestMethod]
        public void CategoryPricedInTheDefaultList_StaysThere_EvenIfAnotherHasMore()
        {
            var both = Category(8, (1, 4), (2, 9));

            Assert.AreEqual(1, CategoryPickerWindow.InitialPriceList(both, NothingSaved, defaultList: 1));
        }

        [TestMethod]
        public void SavedChoiceWins()
        {
            var both = Category(8, (1, 4), (2, 9));

            Assert.AreEqual(2, CategoryPickerWindow.InitialPriceList(both, new Dictionary<long, long> { [8] = 2 }, 1));
        }

        [TestMethod]
        public void CategoryWithoutAnyPrice_StaysOnTheDefault()
        {
            var none = Category(30);

            Assert.AreEqual(1, CategoryPickerWindow.InitialPriceList(none, NothingSaved, 1));
            Assert.AreEqual(0, none.BestPriceList);
        }
    }
}
