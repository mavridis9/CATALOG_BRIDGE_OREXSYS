using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CatalogBridge
{
    // «Αποδοχή παραγγελιών από το ταμείο» (owner request 01/10/2026): every online order waits on
    // PointToOrder until the shop accepts or rejects it in THIS program. Everything verified live
    // on demo store 1110 on 01/10/2026 — see docs/ORDER_ACCEPTANCE.md for the whole story.
    //
    // How the platform does it (all of it through calls this app already makes elsewhere):
    //   * store setting AUTO_ACCEPT_ORDERS=false → a submitted order stays SUBMITTED;
    //   * accept  = admin PUT status CLOSED  (exactly what auto-accept and the admin screen's
    //               «Confirm» produce; RECEIVED is only «seen, ready at <estimatedTime>»);
    //   * reject  = admin PUT status REJECTED + rejectReason (≤ 1024, shown to the customer);
    //   * what is waiting = orders/search {status:[SUBMITTED]};
    //   * «did anything change?» = GET orders/last-updates/ORDER → lastUpdatedAt — the admin
    //     screen itself polls this every 2 s; we poll it and search only when it moved.
    //
    // This file is the pure part (parsing + decisions), tested without a network; the calls are
    // at the bottom and the timer is OrderAcceptanceService.
    public sealed record PendingOrder(string Uuid, string Token, string Customer, decimal Total, DateTime SubmittedAt,
        DateTime? WishTime, string DeliveryMethod, string Comment, List<PendingLine> Lines)
    {
        public string Items => string.Join(", ", Lines.Select(l => $"{l.Quantity}× {l.Name}" + (l.Options.Length > 0 ? $" ({l.Options})" : "")));

        public string DeliveryText => DeliveryMethod switch
        {
            "ADDRESS" => "Διανομή",
            "IN_STORE_LOCATION" => "Στο κατάστημα",
            _ => "Παραλαβή",
        };

        public string WhenText => WishTime is DateTime w && w - SubmittedAt > TimeSpan.FromMinutes(20)
            ? $"για {w.ToLocalTime():dd/MM HH:mm}"
            : "το συντομότερο";

        public string Title => $"{Token} · {Customer} · {Total:0.00} € · {DeliveryText}, {WhenText}";

        // For the screen: the platform's stamps are UTC.
        public DateTime SubmittedLocal => SubmittedAt.ToLocalTime();
    }

    public sealed record PendingLine(int Quantity, string Name, string Options, decimal Total);

    public static class OrderAcceptance
    {
        public const string SettingKey = "AUTO_ACCEPT_ORDERS";
        public const string AcceptedStatus = "CLOSED";
        public const string RejectedStatus = "REJECTED";
        public const int RejectReasonMax = 1024;
        public const string DefaultRejectReason = "Λυπούμαστε, δεν μπορούμε να εξυπηρετήσουμε την παραγγελία αυτή τη στιγμή.";

        // ---- pure ---------------------------------------------------------------------------

        // One page of orders/search → the orders that are waiting for the shop. Anything that is
        // not SUBMITTED is skipped even if the platform returned it (defensive: the filter is ours).
        public static List<PendingOrder> ParsePending(string searchJson)
        {
            var list = new List<PendingOrder>();
            using var doc = JsonDocument.Parse(searchJson);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
            foreach (var o in data.EnumerateArray())
            {
                if (S(o, "status") != "SUBMITTED") continue;
                var lines = new List<PendingLine>();
                if (o.TryGetProperty("orderItems", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        // A nested option (hierarchyLevel CHILD) belongs to the line above it, as the
                        // connector folds it (EFOD.OPTIONS); the till sees «ΠΙΤΑ ΓΥΡΟΣ (ΠΑΚΕΤΟ|ΧΛ)».
                        bool child = S(it, "hierarchyLevel") == "CHILD" || S(it, "parentOfferId").Length > 0 && S(it, "parentOfferId") != "0";
                        string name = S(it, "itemName").Length > 0 ? S(it, "itemName") : S(it, "shortDescription");
                        if (child && lines.Count > 0)
                        {
                            var parent = lines[^1];
                            lines[^1] = parent with { Options = parent.Options.Length == 0 ? name : parent.Options + "|" + name };
                            continue;
                        }
                        lines.Add(new PendingLine(N(it, "quantity") is decimal q && q > 0 ? (int)Math.Round(q) : 1, name, "", N(it, "totalDiscountedPrice") ?? 0m));
                    }
                }
                list.Add(new PendingOrder(S(o, "uuid"), S(o, "orderToken"), S(o, "customerName"), N(o, "totalDiscountedPrice") ?? 0m,
                    D(o, "submittedAt") ?? D(o, "createdAt") ?? DateTime.MinValue, D(o, "wishTime"), S(o, "deliveryMethod"), S(o, "comment"), lines));
            }
            return list.OrderBy(p => p.SubmittedAt).ToList();
        }

        // GET orders/last-updates/ORDER → when the platform last touched an order of this store.
        public static DateTime? ParseLastUpdate(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return D(doc.RootElement, "lastUpdatedAt");
        }

        // The orders that were not on the previous list: those get the sound.
        public static List<PendingOrder> NewSince(IReadOnlyCollection<PendingOrder> previous, IReadOnlyCollection<PendingOrder> current)
        {
            var known = new HashSet<string>(previous.Select(p => p.Uuid));
            return current.Where(c => !known.Contains(c.Uuid)).ToList();
        }

        // The reason the customer will read. Empty → the default sentence; never longer than the
        // platform accepts (1024, OrderUpdateDto.rejectReason).
        public static string FitRejectReason(string? reason)
        {
            string r = (reason ?? "").Trim();
            if (r.Length == 0) r = DefaultRejectReason;
            return r.Length <= RejectReasonMax ? r : r[..RejectReasonMax];
        }

        // What the store setting must be for each state of the switch: on = the platform must NOT
        // accept by itself; off = back to the platform accepting everything (as every store was found).
        public static string PlatformValueFor(bool acceptanceEnabled) => acceptanceEnabled ? "false" : "true";

        private static string S(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : "") : "";

        private static decimal? N(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

        private static DateTime? D(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
                ? d : null;

        // ---- platform calls -------------------------------------------------------------------

        public static async Task<DateTime?> LastUpdateAsync(PointToOrderApiClient api)
        {
            var (st, body) = await api.ProbeGetAsync($"/api/v1/stores/{api.StoreId}/orders/last-updates/ORDER");
            if (st >= 300) throw new PointToOrderException($"Ο έλεγχος για νέες παραγγελίες απέτυχε ({st}): {body}");
            return ParseLastUpdate(body);
        }

        // Every SUBMITTED order of the last two days (older than that nobody is still waiting for).
        public static async Task<List<PendingOrder>> LoadPendingAsync(PointToOrderApiClient api)
        {
            var all = new List<PendingOrder>();
            for (int page = 0; page < 10; page++)
            {
                var (st, body) = await api.ProbePostAsync($"/api/v1/stores/{api.StoreId}/orders/search?page={page}&size=50&sort=submittedAt,asc",
                    new Dictionary<string, object>
                    {
                        ["status"] = new[] { "SUBMITTED" },
                        ["createdAtFrom"] = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    });
                if (st >= 300) throw new PointToOrderException($"Δεν διαβάστηκαν οι παραγγελίες που περιμένουν ({st}): {body}");
                all.AddRange(ParsePending(body));
                using var doc = JsonDocument.Parse(body);
                int totalPages = doc.RootElement.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : 1;
                if (page + 1 >= totalPages) break;
            }
            return all;
        }

        // Accept = CLOSED; with the till's minutes, estimatedTime = now + minutes (the customer
        // sees «ready at»). Both keys in one PUT, as the admin screen's «Confirm…» does.
        public static async Task AcceptAsync(PointToOrderApiClient api, string uuid, DateTime? estimatedUtc = null)
        {
            var body0 = new Dictionary<string, object> { ["status"] = AcceptedStatus, ["keysToUpdate"] = new[] { "status" } };
            if (estimatedUtc is DateTime e)
            {
                body0["estimatedTime"] = e.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                body0["keysToUpdate"] = new[] { "status", "estimatedTime" };
            }
            var (st, body) = await api.ProbePutAsync($"/api/v2/user/stores/{api.StoreId}/orders/{uuid}", body0);
            if (st >= 300) throw new PointToOrderException($"Η αποδοχή απέτυχε ({st}): {body}");
        }

        public static async Task RejectAsync(PointToOrderApiClient api, string uuid, string? reason)
        {
            var (st, body) = await api.ProbePutAsync($"/api/v2/user/stores/{api.StoreId}/orders/{uuid}",
                new Dictionary<string, object>
                {
                    ["status"] = RejectedStatus, ["rejectReason"] = FitRejectReason(reason),
                    ["keysToUpdate"] = new[] { "status", "rejectReason" },
                });
            if (st >= 300) throw new PointToOrderException($"Η απόρριψη απέτυχε ({st}): {body}");
        }

        // The store setting itself (PUT /api/v1/stores/{id}/settings, a list of {key,value}); the
        // answer is the whole store, whose settings.AUTO_ACCEPT_ORDERS we read back as proof.
        public static async Task SetPlatformAutoAcceptAsync(PointToOrderApiClient api, bool acceptanceEnabled)
        {
            string wanted = PlatformValueFor(acceptanceEnabled);
            var (st, body) = await api.ProbePutAsync($"/api/v1/stores/{api.StoreId}/settings",
                new object[] { new Dictionary<string, object> { ["key"] = SettingKey, ["value"] = wanted } });
            if (st >= 300) throw new PointToOrderException($"Η ρύθμιση του PointToOrder δεν άλλαξε ({st}): {body}");
            string? now = ReadAutoAccept(body);
            if (now != null && !string.Equals(now, wanted, StringComparison.OrdinalIgnoreCase))
                throw new PointToOrderException($"Το PointToOrder απάντησε {SettingKey}={now} αντί για {wanted}.");
        }

        public static async Task<bool?> ReadPlatformAutoAcceptAsync(PointToOrderApiClient api)
        {
            var (st, body) = await api.ProbeGetAsync($"/api/v1/stores/{api.StoreId}");
            if (st >= 300) throw new PointToOrderException($"Δεν διαβάστηκαν οι ρυθμίσεις του καταστήματος ({st}): {body}");
            string? v = ReadAutoAccept(body);
            return v == null ? null : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static string? ReadAutoAccept(string storeJson)
        {
            using var doc = JsonDocument.Parse(storeJson);
            if (!doc.RootElement.TryGetProperty("settings", out var s) || !s.TryGetProperty(SettingKey, out var v)) return null;
            return v.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.String => v.GetString(), _ => v.GetRawText() };
        }
    }
}
