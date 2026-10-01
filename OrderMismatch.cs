using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // «Ασυμφωνίες παραγγελιών» (owner, 23/09/2026): the last 30 days of Orexsys orders that have
    // a PointToOrder counterpart, compared against one paged orders/search on the platform, plus
    // the platform orders that have NO counterpart although they should. Shown on Αρχική; fixed
    // only on the user's click (OrderMismatchService + SyncView). Nothing here writes.
    public enum MismatchKind
    {
        CancelledInOrexsysActiveOnPlatform,   // → «Ακύρωση στο PointToOrder»
        CancelledOnPlatformActiveInOrexsys,   // → «Ακύρωση στο Orexsys»
        DifferentAmount,                      // same order, different total - shown, no button
        PlatformOrderMissingInOrexsys,        // online order that never became an Orexsys order - shown, no button
    }

    public sealed record OrexsysOrderSide(long OrdmId, DateTime When, long CustId, string Customer, decimal Total, bool Cancelled, string PlatformOrderNumber);

    public sealed record PlatformOrderSide(string Uuid, string Token, string Status, string Comment, long CustomerUserId, string Customer, decimal Total, DateTime CreatedAt)
    {
        // REJECTED counts as cancelled: the shop said no (01/10), so it must not be active anywhere.
        public bool Cancelled => Status is "CANCELLED" or "DELETED" or "REJECTED";

        // Still waiting for the shop's answer (auto-accept off): the online-orders board's business.
        public bool AwaitingShop => Status is "SUBMITTED" or "RECEIVED";
        public bool Draft => Status == "DRAFT" || Token.Length == 0;
    }

    // What the connector wrote in EFOM for a platform order: ORDERID is the token; ORDMID is set
    // once the trigger made it a till order; STATUS -1 = rejected (loop guard, or a real failure).
    public sealed record EfomEntry(string OrderId, long? OrdmId, int Status);

    public sealed record Mismatch(MismatchKind Kind, long OrdmId, DateTime When, string Customer, decimal Total, string Token, string Uuid, string Detail = "")
    {
        public string Title => (OrdmId > 0 ? $"#{OrdmId} · " : $"{Token} · ") + $"{When:dd/MM/yyyy HH:mm} · {Customer} · {Total:0.00} €";

        // One line for the grid column.
        public string Short => Kind switch
        {
            MismatchKind.CancelledInOrexsysActiveOnPlatform => $"Ακυρωμένη στο Orexsys · ενεργή στο PointToOrder ({Token})",
            MismatchKind.CancelledOnPlatformActiveInOrexsys => $"Ακυρωμένη στο PointToOrder ({Token}) · ενεργή στο Orexsys",
            MismatchKind.DifferentAmount => $"Διαφορετικό ποσό: {Detail}",
            _ => $"Δεν έφτασε στο Orexsys: {Detail}",
        };

        public string Problem => Short + ".";

        public string Action => Kind switch
        {
            MismatchKind.CancelledInOrexsysActiveOnPlatform => "Ακύρωση στο PointToOrder",
            MismatchKind.CancelledOnPlatformActiveInOrexsys => "Ακύρωση στο Orexsys",
            _ => "",
        };

        public bool HasAction => Action.Length > 0;

        // «Αρ.» column: the Orexsys number, or the platform token when there is no Orexsys order.
        public string Ref => OrdmId > 0 ? OrdmId.ToString() : Token;
    }

    // A customer that exists on both sides (same e-mail - the link the trigger uses).
    public sealed record LinkedCustomer(OrexsysCustomer Orexsys, long PlatformId);

    // Per linked customer, the last 30 days on each side (owner, 23/09/2026: «calculate the total
    // of Orexsys and P2O on every customer and compare it»). Only the customers whose two sides
    // DIFFER are reported; the details name the orders that exist on one side only.
    public sealed record CustomerTotals(
        OrexsysCustomer Customer, int OrexsysCount, decimal OrexsysTotal, int PlatformCount, decimal PlatformTotal, string Details)
    {
        public decimal Difference => OrexsysTotal - PlatformTotal;
        public string OrexsysText => $"{OrexsysCount} · {OrexsysTotal:0.00} €";
        public string PlatformText => $"{PlatformCount} · {PlatformTotal:0.00} €";
        public string DifferenceText => (Difference > 0 ? "+" : "") + Difference.ToString("0.00") + " €";
    }

    public sealed class MismatchReport
    {
        public List<Mismatch> Orders { get; init; } = new();
        public List<CustomerTotals> Customers { get; init; } = new();
        public int Count => Orders.Count + Customers.Count;
    }

    public static class OrderMismatch
    {
        public static readonly TimeSpan Window = TimeSpan.FromDays(30);

        // A platform order younger than this is not yet «missing» in Orexsys - the connector
        // polls, and the trigger runs on its own time.
        public static readonly TimeSpan ConnectorSlack = TimeSpan.FromMinutes(15);

        // ---- the link (shared by both comparisons) --------------------------------------------
        //
        // In order of trust: (1) an order that arrived FROM the platform carries its token in
        // EFOM.ORDERID; (2) a till order sent there is in the local registry; (3) failing both,
        // the platform order's comment tag «[OREXSYS#id]» (a send from another PC).
        private sealed class Links
        {
            private readonly Dictionary<string, PlatformOrderSide> byToken = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<long, PlatformOrderSide> byTag = new();
            private readonly IReadOnlyDictionary<long, string> sentTokens;

            public Links(IEnumerable<PlatformOrderSide> platform, IReadOnlyDictionary<long, string> sentTokens)
            {
                this.sentTokens = sentTokens;
                foreach (var p in platform)
                {
                    if (p.Draft) continue;
                    if (!byToken.ContainsKey(p.Token)) byToken[p.Token] = p;
                    if (TaggedOrdmId(p) is long id && !byTag.ContainsKey(id)) byTag[id] = p;
                }
            }

            public PlatformOrderSide? Counterpart(OrexsysOrderSide o)
            {
                PlatformOrderSide? p = null;
                if (o.PlatformOrderNumber.Length > 0) byToken.TryGetValue(o.PlatformOrderNumber, out p);
                if (p == null && sentTokens.TryGetValue(o.OrdmId, out string? token)) byToken.TryGetValue(token, out p);
                if (p == null) byTag.TryGetValue(o.OrdmId, out p);
                return p;
            }
        }

        public static long? TaggedOrdmId(PlatformOrderSide p)
        {
            string? ext = TillOrderSender.TagToExternalId(p.Comment);
            return ext != null && long.TryParse(ext["OREXSYS-".Length..], out long id) ? id : null;
        }

        // ---- order-level (pure, unit-tested) --------------------------------------------------
        //
        // Orders on both sides: cancelled on one and active on the other, or active on both
        // with a different amount. Then the other direction: a platform order (not a draft, not
        // cancelled, older than ConnectorSlack) that no Orexsys order claims - either the
        // connector never brought it (no EFOM row) or Orexsys rejected it (EFOM STATUS -1 with
        // no ORDMID). Copies of till orders the loop guard rejected on purpose carry the tag and
        // are not reported.
        public static List<Mismatch> Compare(
            IEnumerable<OrexsysOrderSide> orexsys, IReadOnlyDictionary<long, string> sentTokens,
            IEnumerable<PlatformOrderSide> platform, IEnumerable<EfomEntry> efom, DateTime now)
        {
            var platformList = platform.ToList();
            var links = new Links(platformList, sentTokens);
            var efomByToken = efom.GroupBy(e => e.OrderId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.OrdmId ?? 0).First(), StringComparer.OrdinalIgnoreCase);

            var result = new List<Mismatch>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in orexsys)
            {
                var p = links.Counterpart(o);
                if (p == null) continue;
                claimed.Add(p.Token);

                if (o.Cancelled && !p.Cancelled)
                    result.Add(new Mismatch(MismatchKind.CancelledInOrexsysActiveOnPlatform, o.OrdmId, o.When, o.Customer, o.Total > 0 ? o.Total : p.Total, p.Token, p.Uuid));
                else if (!o.Cancelled && p.Cancelled)
                    result.Add(new Mismatch(MismatchKind.CancelledOnPlatformActiveInOrexsys, o.OrdmId, o.When, o.Customer, o.Total, p.Token, p.Uuid));
                else if (!o.Cancelled && o.Total != p.Total)
                    result.Add(new Mismatch(MismatchKind.DifferentAmount, o.OrdmId, o.When, o.Customer, o.Total, p.Token, p.Uuid,
                        $"Orexsys {o.Total:0.00} € · PointToOrder ({p.Token}) {p.Total:0.00} €"));
            }

            foreach (var p in platformList)
            {
                if (p.Draft || p.Cancelled || p.AwaitingShop || claimed.Contains(p.Token)) continue;
                if (TaggedOrdmId(p) != null) continue;                       // our own copy of a till order
                if (now - p.CreatedAt < ConnectorSlack) continue;             // give the connector its time
                efomByToken.TryGetValue(p.Token, out var e);
                if (e != null && e.OrdmId is > 0) continue;                   // it IS in Orexsys - just not in this window / customer
                string detail = e == null
                    ? "δεν την κατέβασε ποτέ ο connector (καμία γραμμή EFOM)"
                    : e.Status == 1
                        ? "περιμένει ακόμα στο ταμείο (EFOM STATUS 1) ενώ το PointToOrder τη θεωρεί δεκτή"
                        : $"το Orexsys την απέρριψε (EFOM STATUS {e.Status}, χωρίς παραγγελία)";
                result.Add(new Mismatch(MismatchKind.PlatformOrderMissingInOrexsys, 0, p.CreatedAt.ToLocalTime(), p.Customer, p.Total, p.Token, p.Uuid, detail));
            }
            return result.OrderByDescending(m => m.When).ToList();
        }

        // ---- per-customer totals (pure, unit-tested) ------------------------------------------
        //
        // Active = not cancelled (and, on the platform, not a draft). The rest is what makes the
        // totals differ and is spelled out: «#12 100,00 μόνο στο Orexsys» (a till order that was
        // refused / never sent), «AB-12 6,00 μόνο στο PointToOrder» (an online order that never
        // reached Orexsys, or landed on another customer), «#10/TP-5Z: 14,56 ≠ 14,00».
        public static List<CustomerTotals> CompareCustomers(
            IEnumerable<LinkedCustomer> linked, IEnumerable<OrexsysOrderSide> orexsys,
            IReadOnlyDictionary<long, string> sentTokens, IEnumerable<PlatformOrderSide> platform)
        {
            var platformList = platform.Where(p => !p.Draft).ToList();
            var links = new Links(platformList, sentTokens);
            var orexsysByCust = orexsys.Where(o => !o.Cancelled).GroupBy(o => o.CustId).ToDictionary(g => g.Key, g => g.ToList());
            var platformByCust = platformList.Where(p => !p.Cancelled && p.CustomerUserId != 0)
                .GroupBy(p => p.CustomerUserId).ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<CustomerTotals>();
            foreach (var c in linked)
            {
                var mine = orexsysByCust.TryGetValue(c.Orexsys.CustId, out var ol) ? ol : new List<OrexsysOrderSide>();
                var theirs = platformByCust.TryGetValue(c.PlatformId, out var pl) ? pl : new List<PlatformOrderSide>();
                decimal oTotal = mine.Sum(o => o.Total), pTotal = theirs.Sum(p => p.Total);
                if (mine.Count == theirs.Count && oTotal == pTotal) continue;

                var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var details = new List<string>();
                foreach (var o in mine)
                {
                    var p = links.Counterpart(o);
                    if (p == null) details.Add($"#{o.OrdmId} {o.Total:0.00} μόνο στο Orexsys");
                    else
                    {
                        matched.Add(p.Token);
                        if (p.Total != o.Total) details.Add($"#{o.OrdmId}/{p.Token}: {o.Total:0.00} ≠ {p.Total:0.00}");
                    }
                }
                foreach (var p in theirs)
                    if (!matched.Contains(p.Token)) details.Add($"{p.Token} {p.Total:0.00} μόνο στο PointToOrder");

                result.Add(new CustomerTotals(c.Orexsys, mine.Count, oTotal, theirs.Count, pTotal, string.Join(" · ", details)));
            }
            return result.OrderByDescending(r => Math.Abs(r.Difference)).ToList();
        }

        // ---- the two sides ------------------------------------------------------------------

        // Every Orexsys order of the last 30 days, with its effective customer and - for one that
        // arrived from the platform - the token (EFOM.ORDERID). Read-only. Never CAST the JSON
        // BLOB to VARCHAR here: Firebird raises «string right truncation» for a JSON longer than
        // the cast (seen 23/09), it does not truncate.
        public static Task<List<OrexsysOrderSide>> LoadOrexsysAsync(Settings settings, DateTime since) => Task.Run(() =>
        {
            var list = new List<OrexsysOrderSide>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            const string sql = @"
                SELECT m.ORDMID, m.ODATE, m.OTIME, m.CANCELED, m.TOTAL,
                       (CASE WHEN COALESCE(m.CARDCUSTID, 0) <> 0 THEN m.CARDCUSTID ELSE COALESCE(m.CUSTID, 0) END) AS ECUSTID,
                       (SELECT COALESCE(NULLIF(TRIM(c.FNAME), ''), c.LNAME) FROM CUST c
                          WHERE c.CUSTID = (CASE WHEN COALESCE(m.CARDCUSTID, 0) <> 0 THEN m.CARDCUSTID ELSE m.CUSTID END)) AS CNAME,
                       (SELECT FIRST 1 e.ORDERID FROM EFOM e WHERE e.ORDMID = m.ORDMID) AS TOKEN
                FROM ORDM m
                WHERE m.ODATE >= @since
                ORDER BY m.ORDMID";
            using var command = new FbCommand(sql, connection);
            command.Parameters.Add("@since", FbDbType.Date).Value = since.Date;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                decimal total = reader["TOTAL"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["TOTAL"]);
                bool cancelled = (reader["CANCELED"] != DBNull.Value && Convert.ToInt32(reader["CANCELED"]) == 1) || total == 0m;
                var date = reader["ODATE"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(reader["ODATE"]);
                var time = reader["OTIME"] is TimeSpan t ? t : TimeSpan.Zero;
                list.Add(new OrexsysOrderSide(Convert.ToInt64(reader["ORDMID"]), date + time, Convert.ToInt64(reader["ECUSTID"]),
                    (reader["CNAME"]?.ToString() ?? "").Trim(), total, cancelled, (reader["TOKEN"]?.ToString() ?? "").Trim()));
            }
            return list;
        });

        // What the connector brought in since `since` (EFOM.INSDATE), token by token. Read-only.
        public static Task<List<EfomEntry>> LoadEfomAsync(Settings settings, DateTime since) => Task.Run(() =>
        {
            var list = new List<EfomEntry>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var command = new FbCommand(
                "SELECT ORDERID, ORDMID, STATUS FROM EFOM WHERE INSDATE >= @since AND ORDERID IS NOT NULL", connection);
            command.Parameters.Add("@since", FbDbType.TimeStamp).Value = since;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new EfomEntry((reader["ORDERID"]?.ToString() ?? "").Trim(),
                    reader["ORDMID"] == DBNull.Value ? null : Convert.ToInt64(reader["ORDMID"]),
                    reader["STATUS"] == DBNull.Value ? 0 : Convert.ToInt32(reader["STATUS"])));
            return list;
        });

        // Every PointToOrder order created since `since`, whatever its status - orders/search is
        // the one listing that does not hide cancelled ones (see TillOrderSender.FindOrderByTokenAsync).
        public static async Task<List<PlatformOrderSide>> LoadPlatformAsync(PointToOrderApiClient api, DateTime since)
        {
            var list = new List<PlatformOrderSide>();
            for (int page = 0; page < 100; page++)
            {
                var (st, body) = await api.ProbePostAsync($"/api/v1/stores/{api.StoreId}/orders/search?page={page}&size=100",
                    new Dictionary<string, object> { ["createdAtFrom"] = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") });
                if (st >= 300) throw new PointToOrderException($"Δεν διαβάστηκαν οι παραγγελίες του PointToOrder ({st}): {body}");
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
                foreach (var o in data.EnumerateArray())
                {
                    string S(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
                    decimal total = o.TryGetProperty("totalDiscountedPrice", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetDecimal() : 0m;
                    long cust = o.TryGetProperty("customerUserId", out var cu) && cu.ValueKind == JsonValueKind.Number ? cu.GetInt64() : 0;
                    DateTime created = DateTime.TryParse(S("createdAt"), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ca) ? ca : DateTime.MinValue;
                    list.Add(new PlatformOrderSide(S("uuid"), S("orderToken"), S("status"), S("comment"), cust, S("customerName"), total, created));
                }
                int totalPages = doc.RootElement.TryGetProperty("totalPages", out var tpg) && tpg.ValueKind == JsonValueKind.Number ? tpg.GetInt32() : 1;
                if (page + 1 >= totalPages || data.GetArrayLength() == 0) break;
            }
            return list;
        }

        // The customers that exist on both sides, matched exactly as Πελάτες matches them.
        public static async Task<List<LinkedCustomer>> LoadLinkedAsync(Settings settings, PointToOrderApiClient api)
        {
            var orexsys = await CustomerDirectory.LoadOrexsysAsync(settings);
            var platform = await api.GetCustomersAsync();
            return CustomerDirectory.Merge(orexsys, platform)
                .Where(r => r.Link == CustomerLink.Linked && r.Orexsys != null && r.Platform is { Id: > 0 })
                .Select(r => new LinkedCustomer(r.Orexsys!, r.Platform!.Id))
                .ToList();
        }

        public static async Task<MismatchReport> FindAsync(Settings settings, PointToOrderApiClient api)
        {
            DateTime since = DateTime.Now - Window;
            var orexsys = await LoadOrexsysAsync(settings, since);
            var efom = await LoadEfomAsync(settings, since.AddDays(-1));
            var platform = await LoadPlatformAsync(api, since.AddDays(-1));   // a day of slack for time zones
            var linked = await LoadLinkedAsync(settings, api);
            var registry = TillOrderSender.LoadSentRegisterWithTokens();
            return new MismatchReport
            {
                Orders = Compare(orexsys, registry, platform, efom, DateTime.UtcNow),
                Customers = CompareCustomers(linked, orexsys, registry, platform),
            };
        }
    }
}
