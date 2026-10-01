using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CatalogBridge
{
    // Runs while «Online παραγγελίες» is on: every 5 s one cheap platform GET
    // (orders/last-updates/ORDER) and one local EFOM read; the SUBMITTED orders are re-searched
    // only when the platform stamp moved or 2′ passed. Three jobs, in this order:
    //   1. a waiting row whose platform order is already rejected / cancelled is voided (-1) in
    //      Orexsys - the one Orexsys write this service makes (OrderMirror.RejectAtTillAsync);
    //   2. a till copy (loop-guard tag) still SUBMITTED on the platform is closed there - the
    //      shop's own history entry, never shown on the board;
    //   3. for each waiting order whose till row says accepted / rejected (the till's screen, or
    //      a button here whose platform call failed), the platform is told, once - the platform's
    //      own status is the lock.
    // Hands Αρχική the waiting list and today's tally.
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
        // Waiting rows whose platform status was looked up and needs no action: not asked again
        // until the platform stamp moves (review finding: the full search ran every 5 s otherwise).
        private readonly HashSet<string> explained = new(StringComparer.OrdinalIgnoreCase);

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
                bool moved = force || stamp != lastSeenUpdate || DateTime.UtcNow - lastFull >= FullRefresh;
                if (moved)
                {
                    pending = await OrderAcceptance.LoadPendingAsync(api);
                    lastSeenUpdate = stamp;
                    lastFull = DateTime.UtcNow;
                    explained.Clear();
                }

                var rows = OrderMirror.Index(await OrderMirror.LoadRowsAsync(settings, storeCode, DateTime.Now.AddDays(-2)));
                var now = DateTime.UtcNow;

                // 1. «not accepted, yet placed in Orexsys»: a row still waiting at the till whose
                // platform order is already rejected / cancelled is voided (-1) before anyone at the
                // till can raise it. The platform's full list (every status) is read only when a
                // waiting row exists that neither the SUBMITTED list nor an earlier look-up explains.
                var copies = pending.Where(OrderMirror.IsTillCopy).ToList();
                bool needLookup = rows.Values.Any(r => r.Waiting && !r.TaggedCopy && !explained.Contains(r.Token) && !pending.Any(p => p.Token == r.Token));
                if (needLookup)
                {
                    var platform = await OrderMismatch.LoadPlatformAsync(api, DateTime.Now.AddDays(-3));
                    var statusByToken = platform.GroupBy(p => p.Token, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.CreatedAt).First().Status, StringComparer.OrdinalIgnoreCase);
                    var (toVoid, nowExplained) = OrderMirror.RowsToVoid(rows.Values, pending, statusByToken);
                    explained.UnionWith(nowExplained);
                    foreach (var r in toVoid)
                    {
                        try
                        {
                            await OrderMirror.RejectAtTillAsync(settings, r.EfomId);
                            CustomerDirectory.Log($"mirror: {r.Token} is {statusByToken[r.Token]} on the platform - voided at the till (EFOM {r.EfomId} → -1)");
                            OrderMirror.RememberOnce(OrderMirror.RegisterFile, new MirrorEvent(now, r.Token, MirrorAction.Reject, 0));
                        }
                        catch (Exception ex) { CustomerDirectory.Log($"mirror: {r.Token} void at the till FAILED - {ex.Message}"); }
                    }
                }

                // 2. Till copies the app itself sent: SUBMITTED only because auto-accept is off.
                // They are the shop's own history entries - closed here, never shown.
                foreach (var c in copies)
                {
                    try
                    {
                        await OrderAcceptance.AcceptAsync(api, c.Uuid);
                        CustomerDirectory.Log($"mirror: till copy {c.Token} closed on the platform (history entry, not an order to decide)");
                    }
                    catch (Exception ex) { CustomerDirectory.Log($"mirror: till copy {c.Token} close FAILED - {ex.Message}"); }
                }
                if (copies.Count > 0) { pending = pending.Where(p => !OrderMirror.IsTillCopy(p)).ToList(); lastSeenUpdate = null; }
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
                    OrderMirror.RememberOnce(OrderMirror.RegisterFile, ev);   // a button here may have registered it already
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
