using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CatalogBridge
{
    // Runs the mirror (OrderMirror) while «έγκριση από το ταμείο» is on: every 5 s one cheap
    // platform GET (orders/last-updates/ORDER) and one local EFOM read; the SUBMITTED orders are
    // re-searched only when the platform stamp moved or 2′ passed. For each waiting order whose
    // till row says accepted / rejected, the platform is told, once (the platform's own status
    // is the lock: a mirrored order is no longer SUBMITTED, so it is not in the next list).
    // Hands Αρχική the waiting list and today's tally. Nothing here touches Orexsys.
    public class OrderMirrorService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FullRefresh = TimeSpan.FromMinutes(2);
        private readonly DispatcherTimer timer;
        private bool running;
        private DateTime? lastSeenUpdate;
        private DateTime lastFull = DateTime.MinValue;
        private List<PendingOrder> pending = new();
        private string storeCode = "";

        public event Action<MirrorState>? Changed;

        public OrderMirrorService()
        {
            timer = new DispatcherTimer { Interval = Interval };
            timer.Tick += async (s, e) => await RunOnceAsync(false);
        }

        public bool IsRunning => timer.IsEnabled;

        public void Apply(Settings settings)
        {
            bool shouldRun = settings.OrderAcceptanceEnabled && settings.IsApiConfigured && settings.IsDatabaseConfigured
                             && License.CanWrite(Module.Orders);
            if (shouldRun && !timer.IsEnabled)
            {
                storeCode = "";
                timer.Start();
                _ = RunOnceAsync(true);
            }
            else if (!shouldRun && timer.IsEnabled)
            {
                timer.Stop();
                pending = new();
                lastSeenUpdate = null;
                Changed?.Invoke(new MirrorState
                {
                    Status = settings.OrderAcceptanceEnabled && !License.CanWrite(Module.Orders) ? "ΣΤΑΜΑΤΗΜΕΝΟ — " + License.Summary() : "",
                    Error = settings.OrderAcceptanceEnabled,
                });
            }
        }

        public Task RefreshAsync() => RunOnceAsync(true);

        private async Task RunOnceAsync(bool force)
        {
            if (running) return;
            running = true;
            try
            {
                Settings settings = Settings.Load();
                if (!settings.OrderAcceptanceEnabled || !settings.IsApiConfigured || !settings.IsDatabaseConfigured) return;
                if (storeCode.Length == 0) storeCode = await EficRepository.LoadStoreCodeAsync(settings);
                if (storeCode.Length == 0)
                {
                    Changed?.Invoke(new MirrorState { Status = "Δεν έχει οριστεί «Σύνδεση P2O» στις Ρυθμίσεις — ο καθρέφτης δεν ξέρει ποιες γραμμές EFOM είναι του PointToOrder.", Error = true });
                    return;
                }

                using var api = new PointToOrderApiClient(settings);
                DateTime? stamp = await OrderAcceptance.LastUpdateAsync(api);
                if (force || stamp != lastSeenUpdate || DateTime.UtcNow - lastFull >= FullRefresh)
                {
                    pending = await OrderAcceptance.LoadPendingAsync(api);
                    lastSeenUpdate = stamp;
                    lastFull = DateTime.UtcNow;
                }

                var rows = OrderMirror.Index(await OrderMirror.LoadRowsAsync(settings, storeCode, DateTime.Now.AddDays(-2)));
                var now = DateTime.UtcNow;

                // The fix for «not accepted, yet placed in Orexsys»: a row still waiting at the till
                // whose platform order is already rejected / cancelled is voided (-1) before anyone
                // at the till can raise it. The platform's full list (every status) is read only
                // when a waiting row exists that the SUBMITTED list does not explain.
                var unexplained = rows.Values.Where(r => r.Waiting && !r.TaggedCopy && !pending.Any(p => p.Token == r.Token)).ToList();
                if (unexplained.Count > 0)
                {
                    var platform = await OrderMismatch.LoadPlatformAsync(api, DateTime.Now.AddDays(-3));
                    foreach (var r in unexplained)
                    {
                        var p = platform.FirstOrDefault(x => x.Token == r.Token);
                        if (p == null || !OrderMirror.ShouldVoidAtTill(r, p.Status)) continue;
                        try
                        {
                            await OrderMirror.RejectAtTillAsync(settings, r.EfomId);
                            CustomerDirectory.Log($"mirror: {r.Token} is {p.Status} on the platform - voided at the till (EFOM {r.EfomId} → -1)");
                            OrderMirror.Remember(OrderMirror.RegisterFile, new MirrorEvent(now, r.Token, MirrorAction.Reject, 0));
                        }
                        catch (Exception ex) { CustomerDirectory.Log($"mirror: {r.Token} void at the till FAILED - {ex.Message}"); }
                    }
                }
                var still = new List<PendingOrder>();
                int mirrored = 0;
                foreach (var order in pending)
                {
                    rows.TryGetValue(order.Token, out var row);
                    MirrorAction action;
                    try { action = await OrderMirror.MirrorAsync(api, order, row, now); }
                    catch (Exception ex)
                    {
                        CustomerDirectory.Log($"mirror: {order.Token} FAILED - {ex.Message}");
                        Changed?.Invoke(Build(settings, OrderMirror.Pair(pending, rows, now), $"{order.Token}: {ex.Message}", true));
                        return;
                    }
                    if (action == MirrorAction.None) { still.Add(order); continue; }
                    var ev = new MirrorEvent(now, order.Token, action, row!.AcceptMin);
                    OrderMirror.Remember(OrderMirror.RegisterFile, ev);
                    CustomerDirectory.Log($"mirror: {order.Token} {(action == MirrorAction.Accept ? $"ACCEPTED at the till ({row.AcceptMin}′) → CLOSED" : "REJECTED at the till → REJECTED")} · {order.Customer} · {order.Total:0.00}");
                    mirrored++;
                }
                if (mirrored > 0) { pending = still; lastSeenUpdate = null; }   // the stamp moved because of us; re-read next tick

                var waiting = OrderMirror.Pair(still, rows, now);
                string status = waiting.Count == 0
                    ? $"Καμία παραγγελία δεν περιμένει ({DateTime.Now:HH:mm:ss})"
                    : $"{waiting.Count} παραγγελί{(waiting.Count == 1 ? "α περιμένει" : "ες περιμένουν")} το ταμείο ({DateTime.Now:HH:mm:ss})";
                Changed?.Invoke(Build(settings, waiting, status, false));
            }
            catch (Exception ex)
            {
                Changed?.Invoke(new MirrorState { Status = $"Έλεγχος: σφάλμα ({DateTime.Now:HH:mm}) — {ex.Message}", Error = true });
            }
            finally
            {
                running = false;
            }
        }

        private static MirrorState Build(Settings settings, List<WaitingOrder> waiting, string status, bool error) => new()
        {
            Waiting = waiting,
            Today = OrderMirror.LoadToday(OrderMirror.RegisterFile, DateTime.Today),
            Status = status,
            Error = error,
        };
    }
}
