using System.Collections.Generic;
using System.Linq;

namespace CatalogBridge
{
    // One thing to create on PointToOrder: a product, or one size of a product. A product
    // without sizes is a single unit with ItmvId 0; with sizes (SizeMode.SeparateItems) each
    // size is a unit of its own - «ΤΟΣΤ» (0), «ΤΟΣΤ XL» (17) - carrying that size's price and
    // written to EFIC with its ITMVID, so the till receives the size with the order.
    public sealed record PushUnit(OrexsysProduct Product, long ItmvId, string SizeName, decimal Price)
    {
        public string DefaultName =>
            ItmvId == 0 ? Product.Name : $"{Product.Name} {SizeName}".Trim();
    }

    // Which Orexsys products the one-button sync sends. One definition for Συγχρονισμός,
    // Αποστολή and Πίνακας ελέγχου, so the dashboard number is exactly what one press sends.
    public static class PushRules
    {
        // A real product (not an extra) in a category chosen in Ρυθμίσεις.
        public static bool IsEligible(OrexsysProduct product, Settings settings) =>
            !product.IsExtra && settings.IncludesCategory(product.CategoryId);

        // What is still missing on PointToOrder for this product, whether or not it is active
        // or priced (Αποστολή lets the technician send those on purpose; Συγχρονισμός gates on
        // IsSendable). A product without sizes: itself, once, unless already linked. With
        // sizes: every priced size (and the plain product, if it has a price) that has no
        // EFIC row for that ITMVID yet - so a size added later goes out as a new item while
        // the ones already online are left alone.
        public static List<PushUnit> PendingUnits(OrexsysProduct product, Settings settings)
        {
            var units = new List<PushUnit>();
            if (!IsEligible(product, settings)) return units;

            if (!product.HasSizes)
            {
                if (product.ExternalCodes.Count == 0)
                    units.Add(new PushUnit(product, 0, "", product.Price ?? 0m));
                return units;
            }

            // Option groups are not built yet: such products are held back (IsHeldForSizes).
            if (settings.SizeMode != SizeMode.SeparateItems) return units;

            if (product.HasPrice && !product.IsSizeLinked(0))
                units.Add(new PushUnit(product, 0, product.SizeName(0), product.Price!.Value));

            foreach (var size in product.Sizes)
                if (size.Price is decimal p && p > 0m && !product.IsSizeLinked(size.ItmvId))
                    units.Add(new PushUnit(product, size.ItmvId, size.Name, p));

            return units;
        }

        // What Συγχρονισμός sends without being asked: active, priced, something pending.
        public static bool IsSendable(OrexsysProduct product, Settings settings) =>
            product.IsActive && product.HasPrice && PendingUnits(product, settings).Count > 0;

        public static List<PushUnit> SendableUnits(OrexsysProduct product, Settings settings) =>
            IsSendable(product, settings) ? PendingUnits(product, settings) : new List<PushUnit>();

        // A product Συγχρονισμός would send if its sizes could be: SizeMode.OptionGroup is
        // not implemented yet, so these wait and are named in the report.
        public static bool IsHeldForSizes(OrexsysProduct product, Settings settings) =>
            product.HasSizes && settings.SizeMode != SizeMode.SeparateItems &&
            IsEligible(product, settings) && product.IsActive && product.HasPrice &&
            (!product.IsSizeLinked(0) ||
             product.Sizes.Any(s => s.Price is decimal p && p > 0m && !product.IsSizeLinked(s.ItmvId)));
    }
}
