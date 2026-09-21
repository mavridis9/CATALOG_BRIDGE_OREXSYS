using System;
using System.Collections.Generic;
using System.Linq;

namespace CatalogBridge
{
    // One choice of a size option group («ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ»): the size's name and what it
    // adds to the product's base price (Κανονικό +0,00 · XL +3,00).
    public sealed record PushOption(string Name, decimal Surcharge, long ItmvId);

    // One thing to create on PointToOrder: a product, or one size of a product.
    //  - SizeMode.SeparateItems: each size is a unit of its own - «ΤΟΣΤ» (0), «ΤΟΣΤ XL» (17) -
    //    carrying that size's price and written to EFIC with its ITMVID, so the till receives
    //    the size with the order.
    //  - SizeMode.OptionGroup: one unit at the base price with Options filled in; the till
    //    gets ITMVID 0 and the chosen size only as text in EFOD.OPTIONS (owner's choice).
    public sealed record PushUnit(OrexsysProduct Product, long ItmvId, string SizeName, decimal Price)
    {
        public List<PushOption> Options { get; init; } = new();

        public string DefaultName =>
            ItmvId == 0 ? Product.Name : $"{Product.Name} {SizeName}".Trim();
    }

    // Which Orexsys products the one-button sync sends. One definition for Συγχρονισμός,
    // Αποστολή and Πίνακας ελέγχου, so the dashboard number is exactly what one press sends.
    public static class PushRules
    {
        public const string SizeGroupName = "ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ";

        // An EFIC row whose offerId is no longer on the platform links nothing: the customer
        // deleted the product on PointToOrder (seen 11/09 on demo 1110 — the sync then said
        // «όλα συγχρονισμένα» with an empty shop). Such codes are moved to OrphanCodes so the
        // product becomes pending again; the dead rows are deleted when it is re-sent. Only
        // top-level offers are in the listing - option-group choices are never linked, so
        // nothing is misjudged. Returns how many products were affected.
        public static int DropOrphanLinks(OrexsysCatalogSnapshot orexsys, CatalogSnapshot platform)
        {
            var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var offer in platform.Offers)
                foreach (string candidate in offer.CodeCandidates) online.Add(candidate);

            int affected = 0;
            foreach (var product in orexsys.Products)
            {
                var dead = product.ExternalCodes.Where(c => !online.Contains(c)).ToList();
                if (dead.Count == 0) continue;

                affected++;
                foreach (string code in dead)
                {
                    product.ExternalCodes.Remove(code);
                    product.OrphanCodes.Add(code);
                }
            }
            return affected;
        }

        // A real product (not an extra) in a category chosen in Ρυθμίσεις.
        public static bool IsEligible(OrexsysProduct product, Settings settings) =>
            !product.IsExtra && settings.IncludesCategory(product.CategoryId);

        // What is still missing on PointToOrder for this product, whether or not it is active
        // or priced (Αποστολή lets the technician send those on purpose; Συγχρονισμός gates on
        // IsSendable). A product without sizes: itself, once, unless already linked. With
        // sizes as separate items: every priced size (and the plain product, if it has a
        // price) that has no EFIC row for that ITMVID yet - a size added later goes out as a
        // new item while the ones already online are left alone. With an option group: the
        // product once, with its sizes as options; a product already online is left alone
        // even if sizes were added afterwards (adding a group to a live offer is manual).
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

            if (settings.SizeMode == SizeMode.OptionGroup)
            {
                if (product.ExternalCodes.Count == 0)
                {
                    var unit = OptionGroupUnit(product);
                    if (unit != null) units.Add(unit);
                }
                return units;
            }

            if (product.HasPrice && !product.IsSizeLinked(0))
                units.Add(new PushUnit(product, 0, product.SizeName(0), product.Price!.Value));

            foreach (var size in product.Sizes)
                if (size.Price is decimal p && p > 0m && !product.IsSizeLinked(size.ItmvId))
                    units.Add(new PushUnit(product, size.ItmvId, size.Name, p));

            return units;
        }

        // Base price = the cheapest of the product's own price and its priced sizes, so no
        // option ever carries a negative surcharge; each priced size (and «Κανονικό» for the
        // product's own price) becomes an option with the difference. Null when nothing is
        // priced at all.
        private static PushUnit? OptionGroupUnit(OrexsysProduct product)
        {
            var priced = new List<(string Name, decimal Price, long ItmvId)>();
            if (product.HasPrice) priced.Add((product.SizeName(0), product.Price!.Value, 0));
            foreach (var size in product.Sizes)
                if (size.Price is decimal p && p > 0m) priced.Add((size.Name, p, size.ItmvId));
            if (priced.Count == 0) return null;

            decimal basePrice = priced.Min(x => x.Price);
            return new PushUnit(product, 0, "", basePrice)
            {
                Options = priced.Select(x => new PushOption(x.Name, x.Price - basePrice, x.ItmvId)).ToList(),
            };
        }

        // What Συγχρονισμός sends without being asked: active, priced, something pending.
        // (An option-group unit counts as priced when any size is - see OptionGroupUnit.)
        public static bool IsSendable(OrexsysProduct product, Settings settings) =>
            product.IsActive && (product.HasPrice || product.HasSizes) &&
            PendingUnits(product, settings).Count > 0;

        // Would be sent, but has no price in the configured list and column - neither its own
        // nor any size's. Reported by name so it is never skipped in silence.
        public static bool IsUnpriced(OrexsysProduct product, Settings settings) =>
            IsEligible(product, settings) && product.IsActive && product.ExternalCodes.Count == 0 &&
            !product.HasPrice && !product.Sizes.Any(s => s.Price is decimal p && p > 0m);

        public static List<PushUnit> SendableUnits(OrexsysProduct product, Settings settings) =>
            IsSendable(product, settings) ? PendingUnits(product, settings) : new List<PushUnit>();
    }
}
