using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CatalogBridge
{
    // What a full reset of one PointToOrder store would remove: every top-level product, the
    // option groups nested in them (with their choices) and every category.
    public sealed class ResetPlan
    {
        public string StoreId { get; init; } = "";
        public long CatalogId { get; init; }
        public string CatalogJson { get; init; } = "";
        public List<CatalogOffer> Offers { get; init; } = new();
        public List<CatalogCategory> Categories { get; init; } = new();
        public List<(long OfferId, long GroupId, List<long> ChoiceIds)> OptionGroups { get; init; } = new();
        public List<string> OfferDetailJson { get; init; } = new();

        public int ChoiceCount => OptionGroups.Sum(g => g.ChoiceIds.Count);
    }

    // «Επαναφορά από την αρχή» (owner request 21/09/2026): empty the PointToOrder store and the
    // app's EFIC rows so everything can be sent again. The EFIC half is EficRepository's
    // DeleteAllUnderCodesAsync (fenced there); this is the PointToOrder half.
    public static class ResetService
    {
        // The live LIGHTHOUSE store (CLAUDE.md §2 rule 10). Never emptied from this tool.
        private static readonly HashSet<string> ProtectedStores = new() { "1046" };

        public static bool IsProtected(string storeId) => ProtectedStores.Contains(storeId.Trim());

        public static async Task<ResetPlan> PlanAsync(PointToOrderApiClient api, Action<string>? progress = null)
        {
            if (IsProtected(api.StoreId))
                throw new PointToOrderException(
                    $"Το κατάστημα {api.StoreId} είναι ζωντανό κατάστημα πελάτη — η επαναφορά δεν επιτρέπεται.");

            string json = await api.GetCatalogJsonAsync();
            var catalog = await api.GetCatalogAsync();

            var groups = new List<(long, long, List<long>)>();
            var details = new List<string>();
            int i = 0;
            foreach (var offer in catalog.Offers)
            {
                progress?.Invoke($"Ανάγνωση προϊόντων {++i}/{catalog.Offers.Count}...");
                var (found, detail) = await api.GetOptionGroupsAsync(catalog.CatalogId, offer.OfferId);
                details.Add(detail);
                foreach (var (groupId, choices) in found) groups.Add((offer.OfferId, groupId, choices));
            }

            return new ResetPlan
            {
                StoreId = api.StoreId,
                CatalogId = catalog.CatalogId,
                CatalogJson = json,
                Offers = catalog.Offers.ToList(),
                Categories = catalog.Categories.ToList(),
                OptionGroups = groups,
                OfferDetailJson = details,
            };
        }

        // Everything the reset is about to delete, as it was: there is no undo on PointToOrder.
        public static string SaveBackup(ResetPlan plan)
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, $"P2O_{plan.StoreId}_{DateTime.Now:yyyyMMdd_HHmmss}_before_reset.json");

            var sb = new StringBuilder();
            sb.Append("{\"storeId\":\"").Append(plan.StoreId).Append("\",\"catalog\":")
              .Append(plan.CatalogJson.Length > 0 ? plan.CatalogJson : "null")
              .Append(",\"offerDetails\":[").Append(string.Join(",", plan.OfferDetailJson)).Append("]}");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            return file;
        }

        // Choices, then option groups, then products, then categories - a category or group can
        // only go once it is empty. Failures are collected, not fatal; the caller re-reads the
        // store afterwards to say what is left.
        public static async Task<List<string>> ExecuteAsync(
            PointToOrderApiClient api, ResetPlan plan, Action<string> progress)
        {
            if (IsProtected(api.StoreId) || api.StoreId != plan.StoreId)
                throw new PointToOrderException("Το κατάστημα άλλαξε ή είναι προστατευμένο — δεν διαγράφηκε τίποτα.");

            var failures = new List<string>();

            async Task Try(string what, Func<Task> action)
            {
                try { await action(); }
                catch (Exception ex) { failures.Add($"{what}: {ex.Message}"); }
            }

            var choices = plan.OptionGroups.SelectMany(g => g.ChoiceIds).Distinct().ToList();
            if (choices.Count > 0)
            {
                progress($"Διαγραφή {choices.Count} επιλογών μεγέθους...");
                await Try("επιλογές", () => api.DeleteOffersAsync(plan.CatalogId, choices));
            }

            foreach (var group in plan.OptionGroups.Select(g => g.GroupId).Distinct())
            {
                progress("Διαγραφή ομάδων επιλογών...");
                await Try($"ομάδα επιλογών {group}", () => api.DeleteCategoryAsync(plan.CatalogId, group));
            }

            var offers = plan.Offers.Select(o => o.OfferId).Where(id => id != 0).Distinct().ToList();
            if (offers.Count > 0)
            {
                progress($"Διαγραφή {offers.Count} προϊόντων...");
                await Try("προϊόντα", () => api.DeleteOffersAsync(plan.CatalogId, offers));
            }

            int c = 0;
            foreach (var category in plan.Categories.Where(x => x.CategoryId != 0))
            {
                progress($"Διαγραφή κατηγοριών {++c}/{plan.Categories.Count}...");
                await Try($"κατηγορία «{category.Name}»", () => api.DeleteCategoryAsync(plan.CatalogId, category.CategoryId));
            }

            return failures;
        }
    }
}
