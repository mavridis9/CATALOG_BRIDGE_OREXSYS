using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
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

        // Lines the platform receives: a real product (offerId, quantity) or the placeholder
        // that carries the amount of everything that is not on PointToOrder.
        public sealed record PlatformLine(long OfferId, int Quantity, string Label);

        public sealed record Plan(long OrdmId, DateTime When, decimal Total, List<Line> Lines,
            List<PlatformLine> PlatformLines, string ExternalId)
        {
            public static string ExternalIdFor(long ordmId) => $"OREXSYS-{ordmId}";
        }

        // The two hidden placeholders (owner decision 22/09/2026): products not on PointToOrder
        // go as «Αγορά στο κατάστημα» × euros + «(λεπτά)» × cents, so the total stays exact
        // while nothing is ever visible to customers (isSellable false + Inactive).
        public const string PlaceholderEuroName = "Αγορά στο κατάστημα";
        public const string PlaceholderCentName = "Αγορά στο κατάστημα (λεπτά)";
        public const string PlaceholderCategoryName = "ΚΑΤΑΣΤΗΜΑ";

        // ---- pure rule (unit-tested) ------------------------------------------------------

        // offers: EFIC (ITMSID, ITMVID) → offerId; prices: offerId → platform price;
        // placeholders: the euro and cent products (0 = none, then unmapped lines refuse).
        // Null problem = sendable. A mapped product with a different price still refuses: that
        // is a catalogue to fix, not an amount to hide in the placeholder.
        public static string? BuildPlatformLines(
            IReadOnlyList<Line> lines, IReadOnlyDictionary<(long, long), long> offers,
            IReadOnlyDictionary<long, decimal> prices, (long Euro, long Cent) placeholders,
            out List<PlatformLine> platformLines)
        {
            platformLines = new List<PlatformLine>();
            if (lines.Count == 0) return "Η παραγγελία δεν έχει γραμμές.";

            var problems = new List<string>();
            decimal other = 0m;
            var otherNames = new List<string>();
            foreach (var line in lines)
            {
                if (!offers.TryGetValue((line.ItmsId, line.ItmvId), out long offerId))
                {
                    if (placeholders.Euro == 0 || placeholders.Cent == 0)
                        problems.Add($"«{line.Name}» δεν υπάρχει στο PointToOrder" + (line.ItmvId != 0 ? " (αυτό το μέγεθος)" : ""));
                    else { other += line.Total; otherNames.Add($"{line.Name} ×{line.Qty:0.##}"); }
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
                if (line.Qty != Math.Floor(line.Qty) || line.Qty <= 0)
                {
                    problems.Add($"«{line.Name}»: ποσότητα {line.Qty} — το PointToOrder δέχεται μόνο ακέραιες");
                    continue;
                }
                platformLines.Add(new PlatformLine(offerId, (int)line.Qty, line.Name));
            }

            if (other < 0m) problems.Add("αρνητικό ποσό για τα προϊόντα εκτός PointToOrder");
            if (problems.Count > 0)
            {
                platformLines.Clear();   // all or nothing
                return "Δεν στέλνεται — η παραγγελία δεν θα ήταν ίδια: " + string.Join("· ", problems);
            }

            if (other > 0m)
            {
                int cents = (int)Math.Round(other * 100m, MidpointRounding.AwayFromZero);
                int euros = cents / 100, rest = cents % 100;
                string label = string.Join(", ", otherNames);
                if (euros > 0) platformLines.Add(new PlatformLine(placeholders.Euro, euros, label));
                if (rest > 0) platformLines.Add(new PlatformLine(placeholders.Cent, rest, label));
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


        // The two placeholder products of this store: found by name in the catalogue, created
        // (hidden + Inactive, in the hidden category «ΚΑΤΑΣΤΗΜΑ») the first time they are
        // needed. Never shown to customers: isSellable false hides an offer from the shop.
        public static async Task<(long Euro, long Cent)> EnsurePlaceholdersAsync(PointToOrderApiClient api, CatalogSnapshot catalog)
        {
            long euro = catalog.Offers.FirstOrDefault(o => o.Name == PlaceholderEuroName)?.OfferId ?? 0;
            long cent = catalog.Offers.FirstOrDefault(o => o.Name == PlaceholderCentName)?.OfferId ?? 0;
            if (euro != 0 && cent != 0) return (euro, cent);

            long categoryId = catalog.Categories.FirstOrDefault(c => c.Name == PlaceholderCategoryName)?.CategoryId
                ?? (await api.CreateCategoryAsync(catalog.CatalogId, PlaceholderCategoryName)).Id;
            var created = new List<long>();
            if (euro == 0) { euro = (await api.CreateOfferAsync(catalog.CatalogId, categoryId, PlaceholderEuroName, 1.00m)).Id; created.Add(euro); }
            if (cent == 0) { cent = (await api.CreateOfferAsync(catalog.CatalogId, categoryId, PlaceholderCentName, 0.01m)).Id; created.Add(cent); }
            // Born hidden; Inactive as well, so they cannot be ordered online in any state.
            await api.SetOfferStatusAsync(catalog.CatalogId, created, OfferItemType.Inactive, sellable: false);
            CustomerDirectory.Log($"PointToOrder: placeholders created euro={euro} cent={cent} (hidden, inactive)");
            return (euro, cent);
        }

        // ---- the send ----------------------------------------------------------------------

        public sealed record SendResult(string OrderUuid, string Token, string Status);

        // ---- «only once» (owner, 22/09/2026) --------------------------------------------------
        //
        // Three fences, all inside SendAsync so the button and the automatic run share them:
        //  1. One send at a time in this process (the timer and the button cannot overlap).
        //  2. Right before creating anything, the local register (sent_till_orders.txt) and
        //     the platform (every page of the customer's history) are asked whether this
        //     externalOrderId already exists - either one means «done», nothing is created.
        //  3. A failure after the draft exists and before it is submitted deletes the draft, so
        //     a retry never finds a second copy waiting; a failure after the submit is recorded
        //     as sent, because by then the order IS on the platform.
        private static readonly System.Threading.SemaphoreSlim OneAtATime = new(1, 1);

        public static string SentFile => Path.Combine(AppContext.BaseDirectory, "sent_till_orders.txt");

        public static HashSet<long> LoadSentRegister() => LoadSentRegister(SentFile);

        public static HashSet<long> LoadSentRegister(string file)
        {
            var set = new HashSet<long>();
            try
            {
                if (File.Exists(file))
                    foreach (string line in File.ReadAllLines(file))
                        if (long.TryParse(line.Split('\t')[0], out long id)) set.Add(id);
            }
            catch { }
            return set;
        }

        public static void RememberSent(long ordmId, string token) => RememberSent(SentFile, ordmId, token);

        public static void RememberSent(string file, long ordmId, string token)
        {
            try { File.AppendAllText(file, $"{ordmId}\t{token}\t{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}", new UTF8Encoding(false)); }
            catch { }
        }

        public sealed class AlreadySentException : InvalidOperationException
        {
            public string Token { get; }
            public AlreadySentException(long ordmId, string token)
                : base($"Η παραγγελία #{ordmId} έχει ήδη σταλεί στο PointToOrder" +
                       (token.Length > 0 ? $" ({token})" : "") + ". Δεν στέλνεται δεύτερη φορά.")
            { Token = token; }
        }

        public static async Task<SendResult> SendAsync(
            PointToOrderApiClient api, long customerUserId, string customerName, Plan plan, Action<string>? progress = null)
        {
            await OneAtATime.WaitAsync();
            try
            {
                // Fence 2: the register first (free), then the platform itself.
                progress?.Invoke("Έλεγχος ότι δεν έχει ήδη σταλεί...");
                if (LoadSentRegister().Contains(plan.OrdmId))
                    throw new AlreadySentException(plan.OrdmId, "");
                var onPlatform = await LoadSentAsync(api, customerUserId);
                if (onPlatform.TryGetValue(plan.ExternalId, out string? existing))
                {
                    RememberSent(plan.OrdmId, existing);
                    throw new AlreadySentException(plan.OrdmId, existing);
                }

                var result = await SendCoreAsync(api, customerUserId, customerName, plan, progress);
                RememberSent(plan.OrdmId, result.Token);
                return result;
            }
            finally
            {
                OneAtATime.Release();
            }
        }

        private static async Task<SendResult> SendCoreAsync(
            PointToOrderApiClient api, long customerUserId, string customerName, Plan plan, Action<string>? progress)
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

            bool submitted = false;
            try
            {
                foreach (var line in plan.PlatformLines)
                {
                    progress?.Invoke($"Προσθήκη «{line.Label}»...");
                    var (c2, b2) = await api.ProbePutAsync($"/api/v1/stores/{store}/orders/{uuid}/orderItems", new Dictionary<string, object>
                    {
                        ["offerId"] = line.OfferId, ["quantity"] = line.Quantity,
                        ["storeId"] = long.Parse(store), ["uuid"] = Guid.NewGuid().ToString(),
                    });
                    if (c2 >= 300) throw new PointToOrderException($"Η προσθήκη «{line.Label}» απέτυχε ({c2}): {b2}");
                }

                progress?.Invoke("Σύνδεση με τον πελάτη...");
                var (c3, b3) = await api.ProbePutAsync($"/api/v1/stores/{store}/orders/{uuid}", new Dictionary<string, object>
                {
                    ["customerUserId"] = customerUserId, ["customerName"] = customerName,
                    ["externalOrderId"] = plan.ExternalId, ["deliveryMethod"] = "NO_LOCATION",
                    ["comment"] = $"{EfomTriggerGuard.SentOrderTag}{plan.OrdmId}] Αγορά στο κατάστημα {plan.When:dd/MM/yyyy HH:mm}: " +
                                  string.Join(", ", plan.Lines.Select(l => $"{l.Name} ×{l.Qty:0.##} {l.Total:0.00}€")),
                });
                if (c3 >= 300) throw new PointToOrderException($"Η σύνδεση με τον πελάτη απέτυχε ({c3}): {b3}");

                progress?.Invoke("Υποβολή...");
                var (c4, b4) = await api.ProbePutAsync($"/api/v2/user/stores/{store}/orders/{uuid}", new Dictionary<string, object>
                {
                    // status ONLY: adding paymentStatus to this call answers 500 (seen 22/09); the
                    // platform ignores paymentStatus on the public update as well, so the comment
                    // is what says «paid at the shop».
                    ["status"] = "SUBMITTED",
                    ["keysToUpdate"] = new[] { "status" },
                });
                if (c4 >= 300) throw new PointToOrderException($"Η υποβολή απέτυχε ({c4}): {b4}");
                submitted = true;
            }
            catch (Exception ex) when (!submitted)
            {
                // Fence 3: never leave a draft behind for a retry to double.
                progress?.Invoke("Καθαρισμός πρόχειρης παραγγελίας...");
                int cd;
                try { (cd, _) = await api.ProbeDeleteAsync($"/api/v1/stores/{store}/orders/{uuid}"); }
                catch { cd = -1; }
                throw new PointToOrderException(ex.Message + (cd is >= 200 and < 300
                    ? " Η πρόχειρη παραγγελία διαγράφηκε — τίποτα δεν έμεινε στο PointToOrder."
                    : $" ΠΡΟΣΟΧΗ: η πρόχειρη παραγγελία {uuid} δεν διαγράφηκε ({cd}) — σβήστε την από το PointToOrder."));
            }

            var (_, back) = await api.ProbeGetAsync($"/api/v1/stores/{store}/orders/{uuid}?locale=el&simplified=false");
            using var doc = JsonDocument.Parse(back);
            string token = doc.RootElement.TryGetProperty("orderToken", out var t) ? (t.GetString() ?? "") : "";
            string status = doc.RootElement.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
            decimal total = doc.RootElement.TryGetProperty("totalDiscountedPrice", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetDecimal() : -1;
            if (total != plan.Total)
            {
                RememberSent(plan.OrdmId, token);   // it IS on the platform - never send it again
                throw new PointToOrderException($"Στάλθηκε ({token}) αλλά το σύνολο στο PointToOrder είναι {total:0.00} αντί για {plan.Total:0.00} — ελέγξτε το.");
            }
            return new SendResult(uuid, token, status);
        }

        // The customer's PointToOrder orders that came from the till: externalOrderId → token,
        // over EVERY page of their history (a long history must not hide an earlier send).
        public static async Task<Dictionary<string, string>> LoadSentAsync(PointToOrderApiClient api, long customerUserId)
        {
            var sent = new Dictionary<string, string>();
            for (int page = 0; page < 100; page++)
            {
                var (st, body) = await api.ProbePostAsync(
                    $"/api/v1/stores/{api.StoreId}/customers/orders?page={page}&size=100",
                    new Dictionary<string, object> { ["customerId"] = customerUserId });
                if (st >= 300) throw new PointToOrderException($"Δεν διαβάστηκε το ιστορικό του πελάτη στο PointToOrder ({st}): {body}");
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) break;
                foreach (var o in content.EnumerateArray())
                    if (IsSentTillOrder(o, out string ext, out string token)) sent[ext] = token;
                bool last = doc.RootElement.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.True;
                if (last || content.GetArrayLength() == 0) break;
            }
            return sent;
        }

        // An order in the platform history counts as «this till order, already there» when it
        // carries our tag and is not cancelled/deleted. The tag is read from the COMMENT
        // («[OREXSYS#<ORDMID>] ...»): the platform overwrites externalOrderId with the order token
        // at submit (seen live 22/09 - N3-38 was a duplicate of #5 because of it); only an
        // unsubmitted draft still shows externalOrderId = OREXSYS-<id>, so that is the fallback.
        public static bool IsSentTillOrder(JsonElement o, out string externalId, out string token)
        {
            string comment = o.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "") : "";
            externalId = TagToExternalId(comment)
                ?? (o.TryGetProperty("externalOrderId", out var e) && e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : "");
            token = o.TryGetProperty("orderToken", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "") : "";
            string status = o.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? (s.GetString() ?? "") : "";
            return externalId.StartsWith("OREXSYS-", StringComparison.Ordinal) && status != "CANCELLED" && status != "DELETED";
        }

        // «[OREXSYS#5] Αγορά...» → "OREXSYS-5"; anything else → null.
        public static string? TagToExternalId(string comment)
        {
            int i = comment.IndexOf(EfomTriggerGuard.SentOrderTag, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + EfomTriggerGuard.SentOrderTag.Length, end = comment.IndexOf(']', start);
            return end > start && long.TryParse(comment[start..end], out long id) ? Plan.ExternalIdFor(id) : null;
        }
    }
}
