using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    [TestClass]
    public class SettingsCategoryTests
    {
        [TestMethod]
        public void DefaultSendsEveryCategory()
        {
            Assert.IsTrue(new Settings().IncludesCategory(46));
        }

        [TestMethod]
        public void ChosenSubsetSendsOnlyThoseCategories()
        {
            var settings = new Settings { SyncAllCategories = false, SyncCategoryIds = "9, 10,26" };

            Assert.IsTrue(settings.IncludesCategory(10));
            Assert.IsTrue(settings.IncludesCategory(26));
            Assert.IsFalse(settings.IncludesCategory(46));
        }

        // «Καμία» must mean none - not fall back to "all" because the list is empty.
        [TestMethod]
        public void EmptySubsetSendsNothing()
        {
            var settings = new Settings { SyncAllCategories = false, SyncCategoryIds = "" };

            Assert.IsFalse(settings.IncludesCategory(9));
        }

        [TestMethod]
        public void ParseIgnoresJunkAndNegatives()
        {
            CollectionAssert.AreEquivalent(
                new long[] { 1, 2 }, Settings.ParseIdSet(" 1,x,,2,-5").ToList());
        }
    }
}
