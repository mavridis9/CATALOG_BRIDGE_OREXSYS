using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // «Ασυμφωνίες παραγγελιών» (owner, 23/09/2026): an order that exists on both sides but is
    // cancelled on one and still active on the other - e.g. an online order cancelled at the
    // till, still CLOSED in the customer's PointToOrder history. Found by comparing the last
    // 30 days of Orexsys orders that have a PointToOrder counterpart against one paged
    // orders/search on the platform; shown on Αρχική; fixed only on the user's click
    // (OrderMismatchService + SyncView). Nothing here writes.
    public enum MismatchKind
    {
        CancelledInOrexsysActiveOnPlatform,   // → «Ακύρωση στο PointToOrder»
        CancelledOnPlatformActiveInOrexsys,   // → «Ακύρωση στο Orexsys»
    }

    public sealed record OrexsysOrderSide(long OrdmId, DateTime When, string Customer, decimal Total, bool Cancelled, string PlatformOrderNumber);

    public sealed record PlatformOrderSide(string Uuid, string Token, string Status, string Comment, string Customer, decimal Total)
    {
        public bool Cancelled => Status is "CANCELLED" or "DELETED";
        public bool Draft => Status == "DRAFT" || Token.Length == 0;
    }

    public sealed record Mismatch(MismatchKind Kind, long OrdmId, DateTime When, string Customer, decimal Total, string Token, string Uuid)
    {
        public string Title => $"#{OrdmId} · {When:dd/MM/yyyy HH:mm} · {Customer} · {Total:0.00} €";

        public string Problem => Kind == MismatchKind.CancelledInOrexsysActiveOnPlatform
            ? $"Ακυρωμένη στο Orexsys, αλλά στο PointToOrder ({Token}) είναι ακόμα ενεργή."
            : $"Ακυρωμένη στο PointToOrder ({Token}), αλλά στο Orexsys είναι ακόμα ενεργή.";

        public string Action => Kind == MismatchKind.CancelledInOrexsysActiveOnPlatform
            ? "Ακύρωση στο PointToOrder" : "Ακύρωση στο Orexsys";
    }

    public static class OrderMismatch
    {
        public static readonly TimeSpan Window = TimeSpan.FromDays(30);

        // ---- pure (unit-tested) --------------------------------------------------------------
        //
        // The link between the two sides, in order of trust: (1) an order that arrived FROM the
        // platform carries its token in EFOM.JSON; (2) a till order sent there is in the local
        // registry; (3) failing both, the platform order's comment tag «[OREXSYS#id]» (a send
        // from another PC). Orders on either side with no counterpart are not mismatches - they
        // are simply not the same order.
        public static List<Mismatch> Compare(
            IEnumerable<OrexsysOrderSide> orexsys, IReadOnlyDictionary<long, string> sentTokens, IEnumerable<PlatformOrderSide> platform)
        {
            var byToken = new Dictionary<string, PlatformOrderSide>(StringComparer.OrdinalIgnoreCase);
            var byTag = new Dictionary<long, PlatformOrderSide>();
            foreach (var p in platform)
            {
                if (p.Draft) continue;
                if (!byToken.ContainsKey(p.Token)) byToken[p.Token] = p;
                string? ext = TillOrderSender.TagToExternalId(p.Comment);
                if (ext != null && long.TryParse(ext["OREXSYS-".Length..], out long id) && !byTag.ContainsKey(id)) byTag[id] = p;
            }

            var result = new List<Mismatch>();
            foreach (var o in orexsys)
            {
                PlatformOrderSide? p = null;
                if (o.PlatformOrderNumber.Length > 0) byToken.TryGetValue(o.PlatformOrderNumber, out p);
                if (p == null && sentTokens.TryGetValue(o.OrdmId, out string? token)) byToken.TryGetValue(token, out p);
                if (p == null) byTag.TryGetValue(o.OrdmId, out p);
                if (p == null) continue;

                if (o.Cancelled && !p.Cancelled)
                    result.Add(new Mismatch(MismatchKind.CancelledInOrexsysActiveOnPlatform, o.OrdmId, o.When, o.Customer, o.Total > 0 ? o.Total : p.Total, p.Token, p.Uuid));
                else if (!o.Cancelled && p.Cancelled)
                    result.Add(new Mismatch(MismatchKind.CancelledOnPlatformActiveInOrexsys, o.OrdmId, o.When, o.Customer, o.Total, p.Token, p.Uuid));
            }
            return result.OrderByDescending(m => m.When).ToList();
        }

        // ---- the two sides ------------------------------------------------------------------

        // Every Orexsys order of the last 30 days, with its customer and - for one that arrived
        // from the platform - the token out of EFOM.JSON. Read-only.
        public static Task<List<OrexsysOrderSide>> LoadOrexsysAsync(Settings settings, DateTime since) => Task.Run(() =>
        {
            var list = new List<OrexsysOrderSide>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            const string sql = @"
                SELECT m.ORDMID, m.ODATE, m.OTIME, m.CANCELED, m.TOTAL,
                       (SELECT COALESCE(NULLIF(TRIM(c.FNAME), ''), c.LNAME) FROM CUST c
                          WHERE c.CUSTID = (CASE WHEN COALESCE(m.CARDCUSTID, 0) <> 0 THEN m.CARDCUSTID ELSE m.CUSTID END)) AS CNAME,
                       (SELECT FIRST 1 CAST(e.JSON AS VARCHAR(8000)) FROM EFOM e WHERE e.ORDMID = m.ORDMID) AS EFOMJSON
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
                list.Add(new OrexsysOrderSide(Convert.ToInt64(reader["ORDMID"]), date + time,
                    (reader["CNAME"]?.ToString() ?? "").Trim(), total, cancelled,
                    CustomerDirectory.PlatformOrderNumberFromEfomJson(reader["EFOMJSON"]?.ToString() ?? "")));
            }
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
                    list.Add(new PlatformOrderSide(S("uuid"), S("orderToken"), S("status"), S("comment"), S("customerName"), total));
                }
                int totalPages = doc.RootElement.TryGetProperty("totalPages", out var tpg) && tpg.ValueKind == JsonValueKind.Number ? tpg.GetInt32() : 1;
                if (page + 1 >= totalPages || data.GetArrayLength() == 0) break;
            }
            return list;
        }

        public static async Task<List<Mismatch>> FindAsync(Settings settings, PointToOrderApiClient api)
        {
            DateTime since = DateTime.Now - Window;
            var orexsys = await LoadOrexsysAsync(settings, since);
            var platform = await LoadPlatformAsync(api, since.AddDays(-1));   // a day of slack for time zones
            return Compare(orexsys, TillOrderSender.LoadSentRegisterWithTokens(), platform);
        }
    }
}
