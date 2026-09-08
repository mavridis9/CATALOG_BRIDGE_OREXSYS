using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CatalogBridge
{
    // Pushes the till's remaining-quantity counters (SCRB.REMAIN) to PointToOrder as
    // stockLevel, every 5 minutes while the app is open. Enable/disable lives in Settings.
    //
    // Behaviour:
    //  - Only EFIC-mapped items are touched (IDs are the truth).
    //  - Items WITH an SCRB counter get stockLevel = REMAIN and stock checking ON.
    //  - An item whose counter disappears mid-session gets stock checking switched OFF once,
    //    so it does not stay frozen at the last number forever.
    //  - Values already sent this session are skipped, so a quiet 5 minutes costs one SCRB
    //    read and zero API calls.
    public class StockSyncService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        private readonly DispatcherTimer timer;
        private readonly Dictionary<long, int> lastSent = new();   // offerId -> stockLevel
        private bool running;

        public event Action<string>? StatusChanged;

        public StockSyncService()
        {
            timer = new DispatcherTimer { Interval = Interval };
            timer.Tick += async (s, e) => await RunOnceAsync();
        }

        // Called at startup and whenever the Settings dialog closes.
        public void Apply(Settings settings)
        {
            bool shouldRun = settings.StockSyncEnabled &&
                             settings.IsDatabaseConfigured && settings.IsApiConfigured;

            if (shouldRun && !timer.IsEnabled)
            {
                timer.Start();
                StatusChanged?.Invoke("Απόθεμα: ενεργό (κάθε 5′)");
                _ = RunOnceAsync();   // first pass immediately, not in 5 minutes
            }
            else if (!shouldRun && timer.IsEnabled)
            {
                timer.Stop();
                lastSent.Clear();
                StatusChanged?.Invoke(settings.StockSyncEnabled
                    ? "Απόθεμα: ανενεργό (ελλιπείς ρυθμίσεις)" : "");
            }
        }

        private async Task RunOnceAsync()
        {
            if (running) return;
            running = true;

            try
            {
                var settings = Settings.Load();

                var stockByItms = await OrexsysCatalog.LoadRemainingStockAsync(settings);
                var eficRows = await EficRepository.LoadAsync(settings);

                // offerId -> remaining, via the EFIC identity. A product with several codes
                // (size variants) sends the same counter to each mapped offer.
                var target = new Dictionary<long, int>();
                foreach (var row in eficRows)
                {
                    if (!long.TryParse(row.Code, out long offerId)) continue;
                    if (stockByItms.TryGetValue(row.ItmsId, out int remain))
                        target[offerId] = remain;
                }

                var changed = target
                    .Where(kv => !lastSent.TryGetValue(kv.Key, out int sent) || sent != kv.Value)
                    .ToList();

                var untracked = lastSent.Keys.Where(id => !target.ContainsKey(id)).ToList();

                if (changed.Count == 0 && untracked.Count == 0)
                {
                    StatusChanged?.Invoke($"Απόθεμα: χωρίς αλλαγές ({DateTime.Now:HH:mm})");
                    return;
                }

                long catalogId;
                using (var api = new PointToOrderApiClient(settings))
                {
                    var catalog = await api.GetCatalogAsync();
                    catalogId = catalog.CatalogId;

                    // changeStatus takes one stockLevel per call, so batch by value.
                    foreach (var group in changed.GroupBy(kv => kv.Value))
                    {
                        await api.SetOfferStockAsync(
                            catalogId, group.Select(kv => kv.Key).ToList(), group.Key, true);
                    }

                    if (untracked.Count > 0)
                        await api.SetOfferStockAsync(catalogId, untracked, 0, false);
                }

                foreach (var kv in changed) lastSent[kv.Key] = kv.Value;
                foreach (long id in untracked) lastSent.Remove(id);

                StatusChanged?.Invoke(
                    $"Απόθεμα: {changed.Count} ενημερώσεις ({DateTime.Now:HH:mm})");
            }
            catch (Exception ex)
            {
                // The next tick retries; the sidebar shows that something is off.
                StatusChanged?.Invoke($"Απόθεμα: σφάλμα ({DateTime.Now:HH:mm}) — {Short(ex.Message)}");
            }
            finally
            {
                running = false;
            }
        }

        private static string Short(string message) =>
            message.Length <= 60 ? message : message.Substring(0, 59) + "…";
    }
}
