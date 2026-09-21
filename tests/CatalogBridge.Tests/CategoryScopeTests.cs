using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // Owner request 21/09: Σύγκριση, Πίνακας ελέγχου and Αντιστοιχίσεις show only the
    // categories chosen in Ρυθμίσεις.
    [TestClass]
    public class CategoryScopeTests
    {
        private static OrexsysProduct Product(long id, string name, long cat, string catName, params string[] codes)
        {
            var p = new OrexsysProduct { ItmsId = id, Name = name, CategoryId = cat, CategoryName = catName, IsActive = true, Price = 1m };
            foreach (var c in codes) p.ExternalCodes.Add(c);
            return p;
        }

        private static CatalogOffer Offer(long id, string name, long cat, string catName)
        {
            var o = new CatalogOffer { OfferId = id, Name = name, CategoryId = cat, CategoryName = catName, ItemType = OfferItemType.Orderable, Sellable = true };
            o.CodeCandidates.Add(id.ToString());
            return o;
        }

        private static (OrexsysCatalogSnapshot, CatalogSnapshot) Data()
        {
            var orexsys = new OrexsysCatalogSnapshot();
            orexsys.Products.Add(Product(204, "CEASARS SALAD", 9, "ΣΑΛΑΤΑ", "27108"));
            orexsys.Products.Add(Product(206, "ΜΠΡΙΖΟΛΑ", 10, "ΚΥΡΙΩΣ"));
            orexsys.Products.Add(Product(200, "ΠΡΟΙΟΝ", 7, "ΔΙΑΦΟΡΑ", "26743"));

            var platform = new CatalogSnapshot { CatalogId = 1010 };
            platform.Categories.Add(new CatalogCategory { CategoryId = 1, Name = "ΣΑΛΑΤΕΣ ONLINE" });   // renamed in the preview
            platform.Categories.Add(new CatalogCategory { CategoryId = 2, Name = "ΚΥΡΙΩΣ" });
            platform.Categories.Add(new CatalogCategory { CategoryId = 3, Name = "TEST" });
            platform.Categories.Add(new CatalogCategory { CategoryId = 4, Name = "ΔΙΑΦΟΡΑ" });
            platform.Offers.Add(Offer(27108, "Σαλάτα Caesar", 1, "ΣΑΛΑΤΕΣ ONLINE"));
            platform.Offers.Add(Offer(27200, "Σαλάτα εποχής", 1, "ΣΑΛΑΤΕΣ ONLINE"));   // unlinked, same category
            platform.Offers.Add(Offer(27300, "Ψαρονέφρι", 2, "ΚΥΡΙΩΣ"));              // unlinked, name match
            platform.Offers.Add(Offer(26881, "TESTWITHEXTRA", 3, "TEST"));             // unrelated
            platform.Offers.Add(Offer(26743, "ΠΡΟΙΟΝ", 4, "ΔΙΑΦΟΡΑ"));                // other category
            return (orexsys, platform);
        }

        private static readonly Settings Chosen = new() { SyncAllCategories = false, SyncCategoryIds = "9,10" };

        [TestMethod]
        public void OnlyChosenCategoriesRemain()
        {
            var (orexsys, platform) = Data();
            var diff = CategoryScope.Filter(CatalogDiff.Build(orexsys, platform, Chosen), orexsys, platform, Chosen);

            CollectionAssert.AreEquivalent(new[] { "CEASARS SALAD", "ΜΠΡΙΖΟΛΑ" },
                diff.Rows.Where(r => r.Orexsys != null).Select(r => r.Orexsys!.Name).ToArray());
            CollectionAssert.AreEquivalent(new[] { "Σαλάτα εποχής", "Ψαρονέφρι" },
                diff.Rows.Where(r => r.Orexsys == null).Select(r => r.PlatformName).ToArray(),
                "unlinked offers: kept by a linked neighbour or a matching category name; TEST and ΔΙΑΦΟΡΑ dropped");
        }

        [TestMethod]
        public void AllCategoriesMeansNoFilter()
        {
            var (orexsys, platform) = Data();
            var all = new Settings();
            var full = CatalogDiff.Build(orexsys, platform, all);

            Assert.AreSame(full, CategoryScope.Filter(full, orexsys, platform, all));
            Assert.AreEqual("", CategoryScope.Note(all));
        }
    }
}
