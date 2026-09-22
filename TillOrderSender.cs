using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // Sends a till (Orexsys) order to PointToOrder so the customer's history is the same on
    // both sides (owner request 21-22/09/2026). Rule: the order is sent only when it can be
    // IDENTICAL - every line a product that exists on PointToOrder (EFIC, this store, that
    // size) and the platform price equal to the till price - otherwise it is refused with the
    // reason and nothing is sent. Same sequence the admin's «Order Capture» page uses (watched
    // live 22/09): draft → items → customer + external id → admin status update. The comment
    // carries «[OREXSYS#<ORDMID>]», which the loop guard in EFOM_BIUD_TRG uses to reject the
    // order when the connector brings it back.
    public static class TillOrderSender
    {
        public sealed record Line(long OrddId, long ItmsId, long ItmvId, string Name, decimal Qty, decimal Price, decimal Total);

        public sealed record Plan(long OrdmId, DateTime When, decimal Total, List<Line> Lines,
            Dictionary<long, long> OfferByOrddId, string ExternalId)
        {
            public static string ExternalIdFor(long ordmId) => $"OREXSYS-{ordmId}";
        }

        // ---- pure rule (unit-tested) ------------------------------------------------------

        // offers: EFIC (ITMSID, ITMVID) → offerId; prices: offerId → platform price.
        // Null problem = sendable.
        public static string? CheckIdentical(
            IReadOnlyList<Line> lines, IReadOnlyDictionary<(long, long), long> offers,
            IReadOnlyDictionary<long, decimal> prices, out Dictionary<long, long> offerByOrddId)
        {
            offerByOrddId = new Dictionary<long, long>();
            if (lines.Count == 0) return "Η παραγγελία δεν έχει γραμμές.";

            var problems = new List<string>();
            foreach (var line in lines)
            {
                if (line.Qty != Math.Floor(line.Qty) || line.Qty <= 0)
                    problems.Add($"«{line.Name}»: ποσότητα {line.Qty} — το PointToOrder δέχεται μόνο ακέραιες");
                if (!offers.TryGetValue((line.ItmsId, line.ItmvId), out long offerId))
                {
                    problems.Add($"«{line.Name}» δεν υπάρχει στο PointToOrder" +
                                 (line.ItmvId != 0 ? " (αυτό το μέγεθος)" : ""));
                    continue;
                }
                if (!prices.TryGetValue(offerId, out decimal platformPrice))
                {
                    problems.Add($"«{line.Name}»: το προϊόν {offerId} δεν βρέθηκε στον κατάλογο του PointToOrder");
                    continue;
                }
                if (platformPrice != line.Price)
                {
                    problems.Add($"«{line.Name}»: τιμή ταμείου {line.Price:0.00}, τιμή PointToOrder {platformPrice:0.00}");
                    continue;
                }
                offerByOrddId[line.OrddId] = offerId;
            }

            if (problems.Count > 0)
            {
                offerByOrddId.Clear();   // all or nothing
                return "Δεν στέλνεται — η παραγγελία δεν θα ήταν ίδια: " + string.Join("· ", problems);
            }
            return null;
        }

        // ---- Orexsys read (read-only) ------------------------------------------------------

        public sealed record OrderHead(long OrdmId, DateTime When, decimal Total, bool FromPlatform, bool Cancelled);

        public static Task<(OrderHead Head, List<Line> Lines)> LoadOrderAsync(Settings settings, long ordmId) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            OrderHead head;
            using (var cmd = new FbCommand(
                "SELECT m.ODATE, m.OTIME, m.TOTAL, m.CANCELED, (SELECT COUNT(*) FROM EFOM e WHERE e.ORDMID = m.ORDMID) AS FROMP2O " +
                "FROM ORDM m WHERE m.ORDMID = @id", connection))
            {
                cmd.Parameters.Add("@id", FbDbType.BigInt).Value = ordmId;
                using var r = cmd.ExecuteReader();
                if (!r.Read()) throw new InvalidOperationException($"Η παραγγελία {ordmId} δεν βρέθηκε.");
                var date = r["ODATE"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(r["ODATE"]);
                var time = r["OTIME"] is TimeSpan t ? t : TimeSpan.Zero;
                decimal total = r["TOTAL"] == DBNull.Value ? 0m : Convert.ToDecimal(r["TOTAL"]);
                head = new OrderHead(ordmId, date + time, total,
                    Convert.ToInt32(r["FROMP2O"]) > 0,
                    (r["CANCELED"] != DBNull.Value && Convert.ToInt32(r["CANCELED"]) == 1) || total == 0m);
            }

            // Main lines only (ORDDID = PARENTID); a paid extra would break «identical», so it
            // is included as a line of its own and then refused by the mapping.
            var lines = new List<Line>();
            using (var cmd = new FbCommand(
                "SELECT d.ORDDID, d.ITMSID, COALESCE(d.ITMVID, 0) AS ITMVID, d.NAME, d.QTY, d.PRICE, d.TOTAL " +
                "FROM ORDD d WHERE d.ORDMID = @id AND d.QTY > 0 AND (d.ORDDID = d.PARENTID OR COALESCE(d.TOTAL, 0) <> 0) " +
                "ORDER BY d.ORDDID", connection))
            {
                cmd.Parameters.Add("@id", FbDbType.BigInt).Value = ordmId;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    lines.Add(new Line(Convert.ToInt64(r["ORDDID"]), Convert.ToInt64(r["ITMSID"]), Convert.ToInt64(r["ITMVID"]),
                        (r["NAME"]?.ToString() ?? "").Trim(), Convert.ToDecimal(r["QTY"]), Convert.ToDecimal(r["PRICE"]),
                        r["TOTAL"] == DBNull.Value ? 0m : Convert.ToDecimal(r["TOTAL"])));
            }
            return (head, lines);
        });

        // EFIC of this store: (ITMSID, ITMVID) → offerId.
        public static Task<Dictionary<(long, long), long>> LoadOffersAsync(Settings settings) => Task.Run(() =>
        {
            var map = new Dictionary<(long, long), long>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            string store = EficRepository.ResolveStoreCode(connection, settings);
            using var cmd = new FbCommand(
                "SELECT ITMSID, COALESCE(ITMVID, 0) AS ITMVID, CODE FROM EFIC WHERE STOREID = @store AND ITMSID IS NOT NULL", connection);
            cmd.Parameters.AddWithValue("@store", store);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (long.TryParse(r["CODE"]?.ToString(), out long offerId))
                    map[(Convert.ToInt64(r["ITMSID"]), Convert.ToInt64(r["ITMVID"]))] = offerId;
            return map;
        });

        // ---- the send ----------------------------------------------------------------------

        public sealed record SendResult(string OrderUuid, string Token, string Status);

        public static async Task<SendResult> SendAsync(
            PointToOrderApiClient api, long customerUserId, string customerName, Plan plan, Action<string>? progress = null)
        {
            string store = api.StoreId;
            progress?.Invoke("Δημιουργία παραγγελίας στο PointToOrder...");
            var (c1, b1) = await api.ProbePostAsync($"/api/v1/stores/{store}/orders", new Dictionary<string, object>
            {
                ["storeId"] = long.Parse(store), ["deliveryMethod"] = "NO_LOCATION",
                ["uiLanguageLocale"] = "el", ["catalogLanguageLocale"] = "el", ["orderItems"] = Array.Empty<object>(),
            });
            if (c1 >= 300) throw new PointToOrderException($"Η δημιουργία απέτυχε ({c1}): {b1}");
            string uuid = JsonDocument.Parse(b1).RootElement.GetProperty("uuid").GetString()!;

            foreach (var line in plan.Lines)
            {
                progress?.Invoke($"Προσθήκη «{line.Name}»...");
                var (c2, b2) = await api.ProbePutAsync($"/api/v1/stores/{store}/orders/{uuid}/orderItems", new Dictionary<string, object>
                {
                    ["offerId"] = plan.OfferByOrddId[line.OrddId], ["quantity"] = (int)line.Qty,
                    ["storeId"] = long.Parse(store), ["uuid"] = Guid.NewGuid().ToString(),
                });
                if (c2 >= 300) throw new PointToOrderException($"Η προσθήκη «{line.Name}» απέτυχε ({c2}): {b2}. Η παραγγελία {uuid} έμεινε πρόχειρη στο PointToOrder.");
            }

            progress?.Invoke("Σύνδεση με τον πελάτη...");
            var (c3, b3) = await api.ProbePutAsync($"/api/v1/stores/{store}/orders/{uuid}", new Dictionary<string, object>
            {
                ["customerUserId"] = customerUserId, ["customerName"] = customerName,
                ["externalOrderId"] = plan.ExternalId, ["deliveryMethod"] = "NO_LOCATION",
                ["comment"] = $"{EfomTriggerGuard.SentOrderTag}{plan.OrdmId}] Αγορά στο κατάστημα {plan.When:dd/MM/yyyy HH:mm}",
            });
            if (c3 >= 300) throw new PointToOrderException($"Η σύνδεση με τον πελάτη απέτυχε ({c3}): {b3}. Η παραγγελία {uuid} έμεινε πρόχειρη στο PointToOrder.");

            progress?.Invoke("Υποβολή...");
            var (c4, b4) = await api.ProbePutAsync($"/api/v2/user/stores/{store}/orders/{uuid}", new Dictionary<string, object>
            {
                ["status"] = "SUBMITTED", ["paymentStatus"] = "PAID_OFFLINE",
                ["keysToUpdate"] = new[] { "status", "paymentStatus" },
            });
            if (c4 >= 300) throw new PointToOrderException($"Η υποβολή απέτυχε ({c4}): {b4}. Η παραγγελία {uuid} έμεινε πρόχειρη στο PointToOrder.");

            var (_, back) = await api.ProbeGetAsync($"/api/v1/stores/{store}/orders/{uuid}?locale=el&simplified=false");
            using var doc = JsonDocument.Parse(back);
            string token = doc.RootElement.TryGetProperty("orderToken", out var t) ? (t.GetString() ?? "") : "";
            string status = doc.RootElement.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            decimal total = doc.RootElement.TryGetProperty("totalDiscountedPrice", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetDecimal() : -1;
            if (total != plan.Total)
                throw new PointToOrderException($"Στάλθηκε ({token}) αλλά το σύνολο στο PointToOrder είναι {total:0.00} αντί για {plan.Total:0.00} — ελέγξτε το.");
            return new SendResult(uuid, token, status);
        }

        // The customer's PointToOrder orders that came from the till: externalOrderId → token.
        public static async Task<Dictionary<string, string>> LoadSentAsync(PointToOrderApiClient api, long customerUserId)
        {
            var sent = new Dictionary<string, string>();
            var (st, body) = await api.ProbePostAsync(
                $"/api/v1/stores/{api.StoreId}/customers/orders?page=0&size=200",
                new Dictionary<string, object> { ["customerId"] = customerUserId });
            if (st >= 300) return sent;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return sent;
            foreach (var o in content.EnumerateArray())
            {
                string ext = o.TryGetProperty("externalOrderId", out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "";
                string status = o.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                if (ext.StartsWith("OREXSYS-", StringComparison.Ordinal) && status != "CANCELLED" && status != "DELETED")
                    sent[ext] = o.TryGetProperty("orderToken", out var t) ? (t.GetString() ?? "") : "";
            }
            return sent;
        }
    }
}
