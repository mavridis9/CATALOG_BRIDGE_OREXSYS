using System;
using System.Collections.Generic;
using System.Linq;

namespace CatalogBridge
{
    // The categories chosen in Ρυθμίσεις («Κατηγορίες προς συγχρονισμό») also scope what the
    // technician screens show - Σύγκριση, Πίνακας ελέγχου, Αντιστοιχίσεις (owner request
    // 21/09/2026): only the products of those categories, and on the PointToOrder side only
    // the categories that hold them. With «Όλες» nothing is filtered.
    //
    // A PointToOrder product that is not linked to anything has no Orexsys category, so its
    // PointToOrder category decides: in scope when that category holds a product linked to a
    // chosen Orexsys category (so a category renamed in the preview still counts), or carries
    // the same name as a chosen Orexsys category.
    public static class CategoryScope
    {
        public static bool IsFiltered(Settings settings) => !settings.SyncAllCategories;

        public static string Note(Settings settings) => IsFiltered(settings)
            ? "Εμφανίζονται μόνο οι κατηγορίες που επιλέξατε στις Ρυθμίσεις."
            : "";

        public static bool Includes(OrexsysProduct product, Settings settings) =>
            settings.IncludesCategory(product.CategoryId);

        public static HashSet<long> PlatformCategories(
            OrexsysCatalogSnapshot orexsys, CatalogSnapshot platform, Settings settings)
        {
            var scoped = orexsys.Products.Where(p => Includes(p, settings)).ToList();

            var linkedCodes = new HashSet<string>(
                scoped.SelectMany(p => p.ExternalCodes), StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(
                scoped.Select(p => CatalogDiff.Normalise(p.CategoryName)).Where(n => n.Length > 0));

            var ids = new HashSet<long>();
            foreach (var offer in platform.Offers)
                if (offer.CodeCandidates.Any(linkedCodes.Contains))
                    ids.Add(offer.CategoryId);
            foreach (var category in platform.Categories)
                if (names.Contains(CatalogDiff.Normalise(category.Name)))
                    ids.Add(category.CategoryId);
            return ids;
        }

        public static DiffResult Filter(
            DiffResult diff, OrexsysCatalogSnapshot orexsys, CatalogSnapshot platform, Settings settings)
        {
            if (!IsFiltered(settings)) return diff;

            var platformCategories = PlatformCategories(orexsys, platform, settings);
            var result = new DiffResult { CatalogId = diff.CatalogId };
            result.Rows.AddRange(diff.Rows.Where(r =>
                r.Orexsys != null
                    ? Includes(r.Orexsys, settings)
                    : r.Offer != null && platformCategories.Contains(r.Offer.CategoryId)));
            return result;
        }
    }
}
