using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CatalogBridge
{
    // Watches PointToOrder for orders waiting to be accepted while «Αποδοχή παραγγελιών από το
    // ταμείο» is on: every 10 s one cheap GET (orders/last-updates/ORDER); only when that
    // timestamp moves — or every 2 minutes regardless, as a safety net — the SUBMITTED orders
    // are searched and handed to Αρχική. A new order rings once. The decisions (accept /
    // reject) are the user's clicks in SyncView; this only looks.
    //
    // Runs only while the switch is on, the API is configured and the «orders» module is
    // licensed and not expired (expired = the shop must turn auto-accept back on, see
    // SyncView.AcceptanceButton_Click — orders must never be left waiting for nobody).
    public class OrderAcceptanceService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan FullRefresh = TimeSpan.FromMinutes(2);
        private readonly DispatcherTimer timer;
        private bool running;
        private DateTime? lastSeenUpdate;
        private DateTime lastFull = DateTime.MinValue;
        private List<PendingOrder> current = new();

        // (pending orders, status line, the ones that are new since the previous list)
        public event Action<List<PendingOrder>, string, List<PendingOrder>>? Checked;

        public OrderAcceptanceService()
        {
            timer = new DispatcherTimer { Interval = Interval };
            timer.Tick += async (s, e) => await RunOnceAsync(false);
        }

        public bool IsRunning => timer.IsEnabled;

        public void Apply(Settings settings)
        {
            bool shouldRun = settings.OrderAcceptanceEnabled && settings.IsApiConfigured && License.CanWrite(Module.Orders);
            if (shouldRun && !timer.IsEnabled)
            {
                timer.Start();
                _ = RunOnceAsync(true);
            }
            else if (!shouldRun && timer.IsEnabled)
            {
                timer.Stop();
                current = new();
                lastSeenUpdate = null;
                Checked?.Invoke(current, settings.OrderAcceptanceEnabled && !License.CanWrite(Module.Orders)
                    ? "ΣΤΑΜΑΤΗΜΕΝΟ — " + License.Summary() : "", new());
            }
        }

        // After an accept/reject: re-read now, do not wait for the next tick.
        public Task RefreshAsync() => RunOnceAsync(true);

        private async Task RunOnceAsync(bool force)
        {
            if (running) return;
            running = true;
            try
            {
                Settings settings = Settings.Load();
                if (!settings.OrderAcceptanceEnabled || !settings.IsApiConfigured) return;
                using var api = new PointToOrderApiClient(settings);

                DateTime? stamp = await OrderAcceptance.LastUpdateAsync(api);
                bool moved = stamp != lastSeenUpdate;
                if (!force && !moved && DateTime.UtcNow - lastFull < FullRefresh)
                {
                    Checked?.Invoke(current, StatusLine(), new());
                    return;
                }

                var fresh = await OrderAcceptance.LoadPendingAsync(api);
                var added = OrderAcceptance.NewSince(current, fresh);
                current = fresh;
                lastSeenUpdate = stamp;
                lastFull = DateTime.UtcNow;
                Checked?.Invoke(current, StatusLine(), added);
            }
            catch (Exception ex)
            {
                Checked?.Invoke(current, $"Έλεγχος: σφάλμα ({DateTime.Now:HH:mm}) — {ex.Message}", new());
            }
            finally
            {
                running = false;
            }
        }

        private string StatusLine() => current.Count == 0
            ? $"Καμία παραγγελία δεν περιμένει ({DateTime.Now:HH:mm:ss})"
            : $"{current.Count} παραγγελί{(current.Count == 1 ? "α περιμένει" : "ες περιμένουν")} απάντηση ({DateTime.Now:HH:mm:ss})";
    }
}
