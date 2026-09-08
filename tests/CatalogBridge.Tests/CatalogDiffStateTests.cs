using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests;

// How a code-matched pair is classified. Two decisions from 2026-09-01 are pinned here:
//  - an item the POS switched off but the platform still sells outranks everything (it
//    loses money, not just tidiness);
//  - prices are NOT compared - the platform price travels with the order into Orexsys, so
//    drift is a pricing choice, never a diff.
[TestClass]
public class CatalogDiffStateTests
{
    private static DiffStatus StatusOf(bool posActive, string itemType, bool sellable,
        decimal posPrice = 5m, decimal platformPrice = 5m)
    {
        var product = new OrexsysProduct
        {
            ItmsId = 1, Code = "1", Name = "X", CategoryName = "C", IsActive = posActive, Price = posPrice,
        };
        product.ExternalCodes.Add("100");

        var offer = new CatalogOffer
        {
            OfferId = 100, CategoryId = 1, CategoryName = "C", Name = "Y",
            Price = platformPrice, ItemType = itemType, Sellable = sellable,
        };
        offer.CodeCandidates.Add("100");

        var orexsys = new OrexsysCatalogSnapshot();
        orexsys.Products.Add(product);
        var platform = new CatalogSnapshot { CatalogId = 1 };
        platform.Offers.Add(offer);

        var row = CatalogDiff.Build(orexsys, platform, new Settings()).Rows.Single();
        Assert.AreEqual(MatchKind.ByCode, row.MatchedBy, "precondition: the pair must be code-matched");
        return row.Status;
    }

    [TestMethod]
    public void OffAtThePos_ButSellingOnline_IsActiveMismatch() =>
        Assert.AreEqual(DiffStatus.ActiveMismatch, StatusOf(posActive: false, OfferItemType.Orderable, sellable: true));

    [TestMethod]
    public void ActiveAtThePos_ButInactiveOnline_IsHiddenOnline() =>
        Assert.AreEqual(DiffStatus.HiddenOnline, StatusOf(posActive: true, OfferItemType.Inactive, sellable: true));

    [TestMethod]
    public void ActiveAtThePos_OrderableButNotSellable_IsHiddenOnline() =>
        Assert.AreEqual(DiffStatus.HiddenOnline, StatusOf(posActive: true, OfferItemType.Orderable, sellable: false));

    [TestMethod]
    public void ActiveBothSides_IsIdentical() =>
        Assert.AreEqual(DiffStatus.Identical, StatusOf(posActive: true, OfferItemType.Orderable, sellable: true));

    [TestMethod]
    public void OffBothSides_IsIdentical() =>
        Assert.AreEqual(DiffStatus.Identical, StatusOf(posActive: false, OfferItemType.Inactive, sellable: false));

    [TestMethod]
    public void PriceDrift_IsNotADifference()
    {
        // 2026-09-01 decision. If this fails, someone re-introduced price comparison - have
        // the conversation about the rule before shipping it.
        Assert.AreEqual(DiffStatus.Identical,
            StatusOf(posActive: true, OfferItemType.Orderable, sellable: true, posPrice: 29.90m, platformPrice: 35.60m));
    }

    [TestMethod]
    public void ActiveMismatch_OutranksHiddenOnline()
    {
        // Off at the POS and hidden-but-orderable online: the money-losing state wins.
        Assert.AreEqual(DiffStatus.ActiveMismatch,
            StatusOf(posActive: false, OfferItemType.Orderable, sellable: false));
    }
}
