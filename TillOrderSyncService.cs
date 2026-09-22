using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // Sends the till's closed orders to PointToOrder automatically (owner request 22/09/2026),
    // every 2 minutes while the app is open, so the customer's history is the same on both
    // sides. Switched on/off from the home page («Αυτόματη αποστολή παραγγελιών ταμείου»).
    //
    // What goes: a closed (STS 4), not cancelled till order — not one that came from the
    // platform — placed since the switch was turned on, for a customer whose e-mail has a
    // PointToOrder account, and only when it can be identical (TillOrderSender.CheckIdentical).
    // Orders already on the platform (externalOrderId OREXSYS-<ORDMID>) are never sent twice;
    // the ones sent are remembered next to the exe (TillOrderSender.SentFile, shared with the
    // manual button) so a quiet cycle costs one query and no API call; TillOrderSender.SendAsync
    // re-checks both the register and the platform right before every send and sends one
    // at a time. Refused orders are logged once per session with
    // their reason. Nothing runs while the loop guard is not in the trigger.
    public class TillOrderSyncService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
        private readonly DispatcherTimer timer;
        private readonly HashSet<long> sentThisSession = new();
        private readonly Dictionary<long, string> refused = new();
        private bool running;

        public event Action<string>? StatusChanged;

        public TillOrderSyncService()
        {
            timer = new DispatcherTimer { Interval = Interval };
            timer.Tick += async (s, e) => await RunOnceAsync();
        }

        public bool IsRunning => timer.IsEnabled;

        public void Apply(Settings settings)
        {
            bool shouldRun = settings.TillOrderSyncEnabled && settings.IsDatabaseConfigured && settings.IsApiConfigured;
            if (shouldRun && !timer.IsEnabled)
            {
                timer.Start();
                StatusChanged?.Invoke("Παραγγελίες ταμείου → PointToOrder: ενεργό (κάθε 2′)");
                _ = RunOnceAsync();
            }
            else if (!shouldRun && timer.IsEnabled)
            {
                timer.Stop();
                StatusChanged?.Invoke(settings.TillOrderSyncEnabled ? "Παραγγελίες ταμείου: ανενεργό (ελλιπείς ρυθμίσεις)" : "");
            }
        }

        public Task RunNowAsync() => RunOnceAsync();

        private sealed record Candidate(long OrdmId, long CustId, string CustName, string Email);

        // Closed till orders since the switch was turned on, for customers with an e-mail.
        private static Task<List<Candidate>> LoadCandidatesAsync(Settings settings, DateTime since) => Task.Run(() =>
        {
            var list = new List<Candidate>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            const string sql = @"
                SELECT m.ORDMID, c.CUSTID, COALESCE(NULLIF(TRIM(c.FNAME), ''), c.LNAME) AS CNAME, c.EMAIL
                FROM ORDM m
                JOIN CUST c ON c.CUSTID = (CASE WHEN COALESCE(m.CARDCUSTID, 0) <> 0 THEN m.CARDCUSTID ELSE m.CUSTID END)
                WHERE m.STS = 4 AND COALESCE(m.CANCELED, 0) = 0 AND COALESCE(m.TOTAL, 0) > 0
                  AND m.ODATE >= @since AND c.CUSTID <> 0 AND TRIM(COALESCE(c.EMAIL, '')) <> ''
                  AND NOT EXISTS (SELECT 1 FROM EFOM e WHERE e.ORDMID = m.ORDMID)
                ORDER BY m.ORDMID";
            using var command = new FbCommand(sql, connection);
            command.Parameters.Add("@since", FbDbType.Date).Value = since.Date;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new Candidate(Convert.ToInt64(reader["ORDMID"]), Convert.ToInt64(reader["CUSTID"]),
                    (reader["CNAME"]?.ToString() ?? "").Trim(), (reader["EMAIL"]?.ToString() ?? "").Trim()));
            return list;
        });

        private async Task RunOnceAsync()
        {
            if (running) return;
            running = true;
            try
            {
                Settings settings = Settings.Load();
                if (!settings.TillOrderSyncEnabled) return;

                var state = await Task.Run(() => EfomTriggerGuard.ReadState(settings));
                if (state != EfomTriggerGuard.State.Patched)
                {
                    StatusChanged?.Invoke("Παραγγελίες ταμείου: ΣΤΑΜΑΤΗΜΕΝΟ — η προστασία επιστροφής δεν είναι ενεργή στο Orexsys " +
                                          "(Ρυθμίσεις → Αντιστοίχιση παραγγελιών με e-mail).");
                    return;
                }

                var candidates = await LoadCandidatesAsync(settings, settings.TillOrderSyncSince);
                var done = TillOrderSender.LoadSentRegister();
                var todo = candidates.Where(c => !done.Contains(c.OrdmId) && !sentThisSession.Contains(c.OrdmId) && !refused.ContainsKey(c.OrdmId)).ToList();
                if (todo.Count == 0)
                {
                    StatusChanged?.Invoke($"Παραγγελίες ταμείου: τίποτα νέο ({DateTime.Now:HH:mm})" +
                                          (refused.Count > 0 ? $" · {refused.Count} δεν στέλνονται (βλ. customers.log)" : ""));
                    return;
                }

                using var api = new PointToOrderApiClient(settings);
                var offers = await TillOrderSender.LoadOffersAsync(settings);
                var catalog = await api.GetCatalogAsync();
                var prices = catalog.Offers.GroupBy(x => x.OfferId).ToDictionary(g => g.Key, g => g.First().Price);
                var placeholders = await TillOrderSender.EnsurePlaceholdersAsync(api, catalog);
                var platformIds = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
                var alreadyOnPlatform = new Dictionary<long, Dictionary<string, string>>();

                int sent = 0;
                foreach (var c in todo)
                {
                    try
                    {
                        if (!platformIds.TryGetValue(c.Email, out long? pid))
                            platformIds[c.Email] = pid = await api.FindCustomerIdAsync(c.Email, null);
                        if (pid is not long customerId)
                        {
                            Refuse(c, "ο πελάτης δεν έχει λογαριασμό στο PointToOrder με αυτό το e-mail");
                            continue;
                        }
                        if (!alreadyOnPlatform.TryGetValue(customerId, out var onPlatform))
                            alreadyOnPlatform[customerId] = onPlatform = await TillOrderSender.LoadSentAsync(api, customerId);
                        string externalId = TillOrderSender.Plan.ExternalIdFor(c.OrdmId);
                        if (onPlatform.TryGetValue(externalId, out string? token0))
                        {
                            TillOrderSender.RememberSent(c.OrdmId, token0);   // sent by hand earlier
                            sentThisSession.Add(c.OrdmId);
                            continue;
                        }

                        var (head, lines) = await TillOrderSender.LoadOrderAsync(settings, c.OrdmId);
                        string? problem = TillOrderSender.BuildPlatformLines(lines, offers, prices, placeholders, out var platformLines);
                        if (problem != null) { Refuse(c, problem); continue; }

                        var plan = new TillOrderSender.Plan(head.OrdmId, head.When, head.Total, lines, platformLines, externalId);
                        var result = await TillOrderSender.SendAsync(api, customerId, c.CustName, plan);
                        // (SendAsync already recorded it)
                        sentThisSession.Add(c.OrdmId);
                        onPlatform[externalId] = result.Token;
                        sent++;
                        CustomerDirectory.Log($"auto: till order {c.OrdmId} ({head.Total:0.00}) → {result.Token} {result.Status} for CUSTID {c.CustId}");
                    }
                    catch (TillOrderSender.AlreadySentException ex)
                    {
                        // Sent by hand between our check and our send - fine, it is there once.
                        CustomerDirectory.Log($"auto: till order {c.OrdmId} already sent - {ex.Message}");
                        sentThisSession.Add(c.OrdmId);
                    }
                    catch (Exception ex)
                    {
                        CustomerDirectory.Log($"auto: till order {c.OrdmId} FAILED - {ex.Message}");
                        StatusChanged?.Invoke($"Παραγγελίες ταμείου: σφάλμα στην #{c.OrdmId} ({DateTime.Now:HH:mm}) — {Short(ex.Message)}");
                        return;   // try again next cycle
                    }
                }

                StatusChanged?.Invoke($"Παραγγελίες ταμείου: στάλθηκαν {sent} ({DateTime.Now:HH:mm})" +
                                      (refused.Count > 0 ? $" · {refused.Count} δεν στέλνονται (βλ. customers.log)" : ""));
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Παραγγελίες ταμείου: σφάλμα ({DateTime.Now:HH:mm}) — {Short(ex.Message)}");
            }
            finally
            {
                running = false;
            }
        }

        private void Refuse(Candidate c, string reason)
        {
            refused[c.OrdmId] = reason;
            CustomerDirectory.Log($"auto: till order {c.OrdmId} NOT sent - {reason}");
        }

        private static string Short(string m) => m.Length > 90 ? m[..90] + "…" : m;
    }
}
