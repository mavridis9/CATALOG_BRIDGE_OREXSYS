using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests;

// CatalogDiff.Build and CatalogDiff.Normalise against hand-built snapshots.
//
// The rule these pin down: THE IDS ARE THE TRUTH. A pair tied by EFIC code is authoritative
// however different the names are; a pair tied by name is only a suggestion; extras never
// take part; a product on one side only is reported, never guessed onto something.
[TestClass]
public class CatalogDiffTests
{
    private static OrexsysProduct Product(long itmsId, string name, decimal? price = 5m,
        bool active = true, bool extra = false, params string[] codes)
    {
        var p = new OrexsysProduct
        {
            ItmsId = itmsId, Code = itmsId.ToString(), Name = name,
            CategoryName = "ΠΙΑΤΑ", IsActive = active, IsExtra = extra, Price = price,
        };
        foreach (string c in codes) p.ExternalCodes.Add(c);
        return p;
    }

    private static CatalogOffer Offer(long offerId, string name, decimal price = 5m,
        string itemType = OfferItemType.Orderable, bool sellable = true)
    {
        var o = new CatalogOffer
        {
            OfferId = offerId, CategoryId = 1, CategoryName = "ΠΙΑΤΑ", Name = name,
            Price = price, ItemType = itemType, Sellable = sellable,
        };
        // Mirrors what the API client does: the offer's own id is a code candidate.
        o.CodeCandidates.Add(offerId.ToString());
        return o;
    }

    private static DiffResult Diff(IEnumerable<OrexsysProduct> products, IEnumerable<CatalogOffer> offers)
    {
        var orexsys = new OrexsysCatalogSnapshot();
        foreach (var p in products) orexsys.Products.Add(p);
        var platform = new CatalogSnapshot { CatalogId = 907 };
        foreach (var o in offers) platform.Offers.Add(o);
        return CatalogDiff.Build(orexsys, platform, new Settings());
    }

    // ---- matching ------------------------------------------------------------------------

    // A product sent as separate items per size has one code per size: every size online is
    // its own row (so a product switched off at the till switches off ALL its sizes), named
    // with the size, and none of them is reported as "only on PointToOrder".
    [TestMethod]
    public void ProductWithSizes_MatchesEverySizeOffer_OneRowEach()
    {
        var tost = Product(203, "ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ", active: false, codes: new[] { "26745", "26990" });
        tost.Sizes.Add(new OrexsysSize(17, "XL", 4m));
        tost.LinkSizes["26990"] = 17;

        var r = Diff(new[] { tost }, new[] { Offer(26745, "ΤΟΣΤ"), Offer(26990, "ΤΟΣΤ XL") });

        Assert.AreEqual(2, r.Rows.Count);
        Assert.IsTrue(r.Rows.All(x => x.MatchedBy == MatchKind.ByCode && x.Status == DiffStatus.ActiveMismatch));
        Assert.AreEqual(0, r.CountOf(DiffStatus.PlatformOnly));
        CollectionAssert.AreEquivalent(new[] { "ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ (Κανονικό)", "ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ (XL)" },
            r.Rows.Select(x => x.OrexsysName).ToArray());
    }

    [TestMethod]
    public void CodeMatch_IsAuthoritative_EvenWhenNamesShareNothing()
    {
        // Seen live: EFIC mapped «ΚΡΙΤΣΙ ΝΤΟΜΑΤ ΘΥΜ» onto «Γαλοπούλα τυρί wrap». The pairing
        // stands; names never decide.
        var r = Diff(new[] { Product(224, "ΚΡΙΤΣΙ ΝΤΟΜΑΤ ΘΥΜ", codes: "25348") },
                     new[] { Offer(25348, "Γαλοπούλα τυρί wrap") });

        var row = r.Rows.Single();
        Assert.AreEqual(MatchKind.ByCode, row.MatchedBy);
        Assert.AreSame(row.Offer, r.Rows[0].Offer);
        Assert.AreEqual(25348, row.Offer!.OfferId);
    }

    [TestMethod]
    public void NameMatch_IsOnlyASuggestion()
    {
        var r = Diff(new[] { Product(10, "Espresso μονός") },
                     new[] { Offer(9001, "ESPRESSO  ΜΟΝΟΣ") });

        var row = r.Rows.Single();
        Assert.AreEqual(MatchKind.ByName, row.MatchedBy);
        Assert.AreEqual("όνομα (?)", row.MatchedByText, "a name match must be visibly marked as a guess");
    }

    [TestMethod]
    public void OneSideOnly_IsReported_NotGuessed()
    {
        var r = Diff(new[] { Product(11, "ΜΟΥΣΑΚΑΣ") },
                     new[] { Offer(9002, "Cheeseburger") });

        Assert.AreEqual(1, r.CountOf(DiffStatus.OrexsysOnly));
        Assert.AreEqual(1, r.CountOf(DiffStatus.PlatformOnly));
        Assert.AreEqual(0, r.MatchedByName, "unrelated names must not be paired");
    }

    [TestMethod]
    public void Extras_NeverParticipate()
    {
        var r = Diff(new[] { Product(12, "DECAF", price: 0m, extra: true) },
                     new[] { Offer(9003, "DECAF") });

        Assert.IsFalse(r.Rows.Any(x => x.Orexsys?.ItmsId == 12), "an extra must not appear at all");
        Assert.AreEqual(1, r.CountOf(DiffStatus.PlatformOnly), "the offer is left unmatched, not paired to the extra");
    }

    [TestMethod]
    public void ExtraWithACode_IsShown_BecauseItIsAnAnomaly()
    {
        var r = Diff(new[] { Product(13, "ΣΑΛΤΣΑ", extra: true, codes: "777") },
                     new[] { Offer(777, "Σάλτσα") });

        Assert.IsTrue(r.Rows.Any(x => x.Orexsys?.ItmsId == 13));
    }

    [TestMethod]
    public void CodeMatchWins_OverNameMatch_ForTheSameOffer()
    {
        // Two products could claim the same offer: one by code, one by name. Code wins and
        // the name-claimant is left Orexsys-only rather than stealing the offer.
        var byCode = Product(20, "ΤΥΡΟΠΙΤΑ", codes: "5000");
        var byName = Product(21, "Cheese pie");
        var r = Diff(new[] { byName, byCode }, new[] { Offer(5000, "Cheese pie") });

        var codeRow = r.Rows.Single(x => x.Orexsys?.ItmsId == 20);
        var nameRow = r.Rows.Single(x => x.Orexsys?.ItmsId == 21);
        Assert.AreEqual(MatchKind.ByCode, codeRow.MatchedBy);
        Assert.AreEqual(DiffStatus.OrexsysOnly, nameRow.Status);
    }

    // ---- Normalise -----------------------------------------------------------------------

    [TestMethod]
    public void Normalise_StripsAccents_FoldsFinalSigma_CollapsesSpaces_Uppercases()
    {
        Assert.AreEqual("ESPRESSO ΜΟΝΟΣ", CatalogDiff.Normalise("  Espresso   μονός "));
        Assert.AreEqual(CatalogDiff.Normalise("Γαλοπούλα τυρί"), CatalogDiff.Normalise("ΓΑΛΟΠΟΥΛΑ ΤΥΡΙ"));
        Assert.AreEqual("", CatalogDiff.Normalise("   "));
        Assert.AreEqual("", CatalogDiff.Normalise(null!));
    }

    [TestMethod]
    public void Normalise_TreatsFinalAndMedialSigmaAlike()
    {
        Assert.AreEqual(CatalogDiff.Normalise("μονός"), CatalogDiff.Normalise("μονόσ"));
    }
}
