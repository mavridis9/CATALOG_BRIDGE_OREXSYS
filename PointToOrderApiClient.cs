using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace CatalogBridge
{
    public class PointToOrderException : Exception
    {
        public PointToOrderException(string message) : base(message) { }
    }

    public record PointToOrderUserInfo(string Username, List<string> StoreIds);

    // PointToOrder's three product states. Reservable is never written by this app - it exists
    // so a product already in that state isn't silently rewritten into one of the other two.
    public static class OfferItemType
    {
        public const string Orderable = "Orderable";
        public const string Inactive = "Inactive";
        public const string Reservable = "Reservable";
    }

    public class CatalogOffer
    {
        public long OfferId { get; set; }
        public long CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Price { get; set; }

        // PointToOrder's own value: "Orderable", "Inactive" or "Reservable".
        public string ItemType { get; set; } = "";

        // The "Visible" switch in PointToOrder's own admin screen. Their UI sends it as
        // "sellable" and reads it back as "isSellable"; the "visible" field in their published
        // schema is ignored by the server (it returns 200 and changes nothing).
        public bool Sellable { get; set; }

        // Any code-like field the offer happens to carry. Which one the platform actually
        // populates was never established, so every candidate is collected and the matcher
        // tries them all against EFIC.CODE. Empty is normal and not an error.
        public List<string> CodeCandidates { get; } = new();
    }

    public class CatalogCategory
    {
        public long CategoryId { get; set; }
        public string Name { get; set; } = "";
    }

    // What a create call actually returned. RawJson is kept because the response shapes on
    // this API were established by looking at real responses, not by trusting the spec.
    public record CreatedEntity(long Id, string RawJson);

    public class CatalogSnapshot
    {
        public long CatalogId { get; set; }
        public List<CatalogCategory> Categories { get; } = new();
        public List<CatalogOffer> Offers { get; } = new();

        // The first offer exactly as the server sent it. Shown in the diagnostics panel so the
        // real field names can be read off a live response instead of guessed from the spec -
        // the published spec was found wrong on this API more than once.
        public string SampleOfferJson { get; set; } = "";
    }

    // Wraps the PointToOrder "Secured APIs" catalog endpoints at admin.point2order.gr.
    //
    // Authenticates with an interactive username/password login (POST /api/v1/auth/login):
    // PointToOrder support confirmed their "generate API key" feature does not work for this
    // API, so a real login session (JWT) is the only way in. The JWT is cached in memory and
    // refreshed once on a 401.
    //
    // This client is READ-ONLY except for SetOfferStatusAsync, which is the one write verified
    // against the live catalog. Creating offers and updating prices are deliberately absent -
    // see BridgePlan for why.
    public class PointToOrderApiClient : IDisposable
    {
        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly string username;
        private readonly string password;
        private readonly string storeId;
        private string? jwt;

        public PointToOrderApiClient(Settings settings)
        {
            baseUrl = settings.ApiBaseUrl.TrimEnd('/');
            username = settings.ApiUsername;
            password = settings.ApiPassword;
            storeId = settings.ApiStoreId;
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task<PointToOrderUserInfo> GetCurrentUserAsync()
        {
            var response = await SendAuthorizedAsync(
                () => http.GetAsync($"{baseUrl}/api/v1/auth/currentuser"));

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Αποτυχία ανάκτησης χρήστη ({(int)response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string user = root.TryGetProperty("username", out var u) ? (u.GetString() ?? "") : "";

            var storeIds = new List<string>();
            if (root.TryGetProperty("storeRoles", out var roles) && roles.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in roles.EnumerateObject())
                    storeIds.Add(property.Name);
            }

            return new PointToOrderUserInfo(user, storeIds);
        }

        // Reads the store's product catalog, flattened from PointToOrder's category tree.
        // Note the /user/ path segment - the catalog does NOT sit under /stores/{id}/ like the
        // customer endpoints do.
        public async Task<CatalogSnapshot> GetCatalogAsync()
        {
            var response = await SendAuthorizedAsync(
                () => http.GetAsync($"{baseUrl}/api/v1/user/stores/{storeId}/catalog"));

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η ανάγνωση του καταλόγου ({(int)response.StatusCode}): {body}");

            var snapshot = new CatalogSnapshot();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("catalogId", out var catalogId) &&
                catalogId.ValueKind == JsonValueKind.Number)
                snapshot.CatalogId = catalogId.GetInt64();

            if (!root.TryGetProperty("categories", out var categories) ||
                categories.ValueKind != JsonValueKind.Array)
                return snapshot;

            bool sampleTaken = false;

            foreach (var category in categories.EnumerateArray())
            {
                string categoryName = category.TryGetProperty("name", out var cn)
                    ? (cn.GetString() ?? "") : "";
                long categoryId = ReadId(category, "categoryId", "id");

                snapshot.Categories.Add(new CatalogCategory
                {
                    CategoryId = categoryId,
                    Name = categoryName,
                });

                if (!category.TryGetProperty("offers", out var offers) ||
                    offers.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var offer in offers.EnumerateArray())
                {
                    if (!sampleTaken)
                    {
                        snapshot.SampleOfferJson = JsonSerializer.Serialize(
                            offer, new JsonSerializerOptions { WriteIndented = true });
                        sampleTaken = true;
                    }

                    var parsed = new CatalogOffer
                    {
                        OfferId = offer.TryGetProperty("offerId", out var id) &&
                                  id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                        CategoryId = ReadId(offer, "categoryId") is var cid && cid != 0
                            ? cid : categoryId,
                        CategoryName = categoryName,
                        Name = offer.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                        Price = offer.TryGetProperty("price", out var p) &&
                                p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : 0m,
                        ItemType = offer.TryGetProperty("itemType", out var t)
                            ? (t.GetString() ?? "") : "",

                        // Absent means visible: only an explicit false hides a product.
                        Sellable = !offer.TryGetProperty("isSellable", out var s) ||
                                   s.ValueKind != JsonValueKind.False,
                    };

                    CollectCodeCandidates(offer, parsed);
                    snapshot.Offers.Add(parsed);
                }
            }

            return snapshot;
        }

        // The offer's id is itself a match candidate: EFIC.CODE holds numbers in the same shape
        // as PointToOrder's ids, and whether they are the same numbering was never confirmed.
        // Collecting every plausible field lets the live match rate answer that question
        // instead of the code assuming an answer.
        private static readonly string[] CodeFields =
        {
            "code", "externalId", "externalProductId", "productCode", "sku", "posCode", "reference",
        };

        private static void CollectCodeCandidates(JsonElement offer, CatalogOffer parsed)
        {
            if (parsed.OfferId != 0)
                parsed.CodeCandidates.Add(parsed.OfferId.ToString());

            foreach (string field in CodeFields)
            {
                if (!offer.TryGetProperty(field, out var value)) continue;

                string? text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.ToString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(text))
                    parsed.CodeCandidates.Add(text.Trim());
            }
        }

        // Creates a category in the store's catalog. Returns the new category's id, needed both
        // for creating offers inside it and for EFIC bookkeeping.
        //
        // hierarchyLevel is required in practice: without it the server answers 500
        // INTERNAL_ERROR (nothing in this API is marked required in the spec - missing fields
        // surface as 500s, not validation errors). "PARENT" is what every live category and
        // top-level offer carries.
        public async Task<CreatedEntity> CreateCategoryAsync(long catalogId, string name)
        {
            var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/category",
                new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["hierarchyLevel"] = "PARENT",
                }));

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η δημιουργία κατηγορίας ({(int)response.StatusCode}): {body}");

            return new CreatedEntity(ExtractId(body, "categoryId", "id"), body);
        }

        // Diagnostic escape hatches: hit an arbitrary API path and return the raw status and
        // body without throwing. Exist because this API's real behaviour is established by
        // probing, not by trusting the spec. Not used by the app's UI.
        public async Task<(int Status, string Body)> ProbePostAsync(string path, object payload)
        {
            var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                $"{baseUrl}{path}", payload));

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<(int Status, string Body)> ProbeGetAsync(string path)
        {
            var response = await SendAuthorizedAsync(() => http.GetAsync($"{baseUrl}{path}"));

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<(int Status, string Body)> ProbePatchAsync(string path, object payload)
        {
            var response = await SendAuthorizedAsync(() => http.PatchAsync(
                $"{baseUrl}{path}", JsonContent.Create(payload)));

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<(int Status, string Body)> ProbePutAsync(string path, object payload)
        {
            var response = await SendAuthorizedAsync(() => http.PutAsJsonAsync(
                $"{baseUrl}{path}", payload));

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        public async Task<(int Status, string Body)> ProbeDeleteAsync(string path)
        {
            var response = await SendAuthorizedAsync(() => http.DeleteAsync($"{baseUrl}{path}"));

            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // Creates a product (offer) inside a category. Minimal payload on purpose - the
        // SaveOfferRequest schema has 28 optional fields, and the fewer are sent, the fewer
        // can be sent wrong. Returns the new offerId, which becomes EFIC.CODE.
        public async Task<CreatedEntity> CreateOfferAsync(
            long catalogId, long categoryId, string name, decimal price)
        {
            var payload = new Dictionary<string, object>
            {
                ["name"] = name,
                ["price"] = price,
                ["categoryId"] = categoryId,
                ["hierarchyLevel"] = "PARENT",
            };

            var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer", payload));

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η δημιουργία προϊόντος ({(int)response.StatusCode}): {body}");

            return new CreatedEntity(ExtractId(body, "offerId", "id"), body);
        }

        // Adds a single-choice option group («ΕΠΙΛΕΞΤΕ ΜΕΓΕΘΟΣ») to an existing offer. Verified
        // live on demo store 1110 (11/09/2026): an option group is a CHILD category with min/max
        // and parentOfferId, nested straight into the offer's `categories[]`, and each option
        // is an ordinary offer inside that category with its surcharge as `price` - exactly
        // how the admin UI's own option groups read back. Options are born hidden like any new
        // offer, so the returned ids must go through SetOfferStatusAsync(sellable: true) with
        // the parent. Returns the option offerIds in the order given.
        public async Task<List<long>> CreateOptionGroupAsync(
            long catalogId, long parentOfferId, string groupName,
            IReadOnlyList<(string Name, decimal Surcharge)> options)
        {
            var group = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/category",
                new Dictionary<string, object>
                {
                    ["name"] = groupName,
                    ["hierarchyLevel"] = "CHILD",
                    ["min"] = 1,
                    ["max"] = 1,
                    ["parentOfferId"] = parentOfferId,
                }));

            string groupBody = await group.Content.ReadAsStringAsync();
            if (!group.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η δημιουργία ομάδας επιλογών ({(int)group.StatusCode}): {groupBody}");

            long groupId = ExtractId(groupBody, "categoryId", "id");
            if (groupId == 0)
                throw new PointToOrderException("Η ομάδα επιλογών δημιουργήθηκε αλλά δεν επέστρεψε id.");

            var ids = new List<long>();
            foreach (var (name, surcharge) in options)
            {
                var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                    $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer",
                    new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["price"] = surcharge,
                        ["categoryId"] = groupId,
                        ["hierarchyLevel"] = "CHILD",
                    }));

                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new PointToOrderException(
                        $"Δεν ήταν δυνατή η δημιουργία επιλογής «{name}» ({(int)response.StatusCode}): {body}");

                ids.Add(ExtractId(body, "offerId", "id"));
            }

            return ids;
        }

        // Permanently removes products from the platform. There is no undo on PointToOrder's
        // side - a deleted product can only come back by being recreated.
        public async Task DeleteOffersAsync(long catalogId, IReadOnlyList<long> offerIds)
        {
            if (offerIds.Count == 0) return;

            HttpResponseMessage response;
            if (offerIds.Count == 1)
            {
                response = await SendAuthorizedAsync(() => http.DeleteAsync(
                    $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer/{offerIds[0]}"));
            }
            else
            {
                // HttpClient.DeleteAsync cannot carry a body, so the request is built by hand.
                response = await SendAuthorizedAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Delete,
                        $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer/deletedMultipleOffers")
                    {
                        Content = JsonContent.Create(new Dictionary<string, object>
                        {
                            ["offerIds"] = offerIds,
                        }),
                    };
                    return http.SendAsync(request);
                });
            }

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new PointToOrderException(
                    $"Η διαγραφή προϊόντων απέτυχε ({(int)response.StatusCode}): {body}");
            }
        }

        // Removes an empty category. Path shape assumed symmetric with the offer delete;
        // verified live before being relied on.
        public async Task DeleteCategoryAsync(long catalogId, long categoryId)
        {
            var response = await SendAuthorizedAsync(() => http.DeleteAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/category/{categoryId}"));

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new PointToOrderException(
                    $"Η διαγραφή κατηγορίας απέτυχε ({(int)response.StatusCode}): {body}");
            }
        }

        // Create responses on this API have not always matched the spec, so the id is dug out
        // of whichever field actually carries it, at the root or one level down.
        private static long ExtractId(string body, params string[] fieldNames)
        {
            if (string.IsNullOrWhiteSpace(body)) return 0;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                long id = ReadId(root, fieldNames);
                if (id != 0) return id;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in root.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object) continue;

                        id = ReadId(property.Value, fieldNames);
                        if (id != 0) return id;
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON body (e.g. a bare 200 with no content) - caller falls back to
                // re-reading the catalog.
            }

            return 0;
        }

        private static long ReadId(JsonElement element, params string[] fieldNames)
        {
            if (element.ValueKind != JsonValueKind.Object) return 0;

            foreach (string field in fieldNames)
            {
                if (element.TryGetProperty(field, out var value) &&
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out long id))
                    return id;
            }

            return 0;
        }

        public enum PriceUpdateResult { Updated, TooRich, NotFound }

        // Changes an existing offer's price. There is no partial-update endpoint for price
        // (verified live: POSTing a partial SaveOfferRequest BLANKS every omitted field), so
        // this reads the offer's full detail and writes everything readable back with only
        // the price changed - verified live to preserve descriptions, state and stock.
        //
        // Offers carrying things the read CANNOT return (photo, option groups, size variants,
        // translations) are refused with TooRich instead of risked: a write-back would wipe
        // whatever the payload cannot express. Those get their price fixed in the admin panel.
        public async Task<PriceUpdateResult> UpdateOfferPriceAsync(
            long catalogId, long offerId, decimal newPrice)
        {
            var read = await SendAuthorizedAsync(() => http.GetAsync(
                $"{baseUrl}/api/v2/user/stores/{storeId}/catalog/{catalogId}/offer/{offerId}"));

            string body = await read.Content.ReadAsStringAsync();
            if (read.StatusCode == HttpStatusCode.NotFound) return PriceUpdateResult.NotFound;
            if (!read.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η ανάγνωση του προϊόντος ({(int)read.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var d = doc.RootElement;

            bool HasContent(string field) =>
                d.TryGetProperty(field, out var v) &&
                v.ValueKind switch
                {
                    JsonValueKind.Array => v.GetArrayLength() > 0,
                    JsonValueKind.String => (v.GetString() ?? "").Length > 0,
                    JsonValueKind.Null => false,
                    _ => true,
                };

            if (HasContent("standardImage") || HasContent("categories") ||
                HasContent("variants") || HasContent("languageTranslation"))
                return PriceUpdateResult.TooRich;

            var payload = new Dictionary<string, object?>
            {
                ["name"] = d.GetProperty("name").GetString(),
                ["price"] = newPrice,
                ["categoryId"] = d.GetProperty("categoryId").GetInt64(),
                ["hierarchyLevel"] = d.TryGetProperty("hierarchyLevel", out var hl)
                    ? hl.GetString() : "PARENT",
                ["itemType"] = d.TryGetProperty("itemType", out var it) ? it.GetString() : "Orderable",
                ["sellable"] = d.TryGetProperty("isSellable", out var s) && s.GetBoolean(),
                ["isOrderable"] = d.TryGetProperty("isOrderable", out var io) && io.GetBoolean(),
            };

            void CopyIf(string readName, string writeName)
            {
                if (!d.TryGetProperty(readName, out var v)) return;
                payload[writeName] = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
                if (payload[writeName] == null) payload.Remove(writeName);
            }

            CopyIf("shortDescription", "shortDescription");
            CopyIf("priceDescription", "priceDescription");
            CopyIf("discount", "discount");
            CopyIf("position", "position");
            CopyIf("stockLevel", "stockLevel");
            CopyIf("isStockCheckEnabled", "isStockCheckEnabled");
            CopyIf("vatPercentage", "vatPercentage");

            var write = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer/{offerId}", payload));

            if (!write.IsSuccessStatusCode)
            {
                string writeBody = await write.Content.ReadAsStringAsync();
                throw new PointToOrderException(
                    $"Η αλλαγή τιμής απέτυχε ({(int)write.StatusCode}): {writeBody}");
            }

            return PriceUpdateResult.Updated;
        }

        // Switches products between Orderable/Inactive (itemType) and/or shown/hidden
        // (sellable - the "Visible" switch in PointToOrder's own admin screen). Pass null for
        // whichever one should not change.
        //
        // Verified live against the real catalog rather than taken from the published schema,
        // which is misleading here:
        //  - Only the fields actually sent are applied; anything omitted keeps its value, so
        //    the two settings can be changed independently and there is no need (and no safe
        //    way) to echo the whole product back.
        //  - isOrderable is derived BY itemType: sending isOrderable=true alongside
        //    itemType=Inactive still leaves it false.
        //  - The schema's "visible" field is NOT what the admin UI's Visible switch writes -
        //    the server accepts it, returns 200, and ignores it. The UI sends "sellable".
        public async Task SetOfferStatusAsync(
            long catalogId, IReadOnlyList<long> offerIds, string? itemType = null, bool? sellable = null)
        {
            if (offerIds.Count == 0) return;
            if (itemType == null && sellable == null) return;

            var payload = new Dictionary<string, object> { ["offerIds"] = offerIds };
            if (itemType != null) payload["itemType"] = itemType;
            if (sellable != null) payload["sellable"] = sellable.Value;

            var response = await SendAuthorizedAsync(() => http.PatchAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer/changeStatus",
                JsonContent.Create(payload)));

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η αλλαγή κατάστασης προϊόντων ({(int)response.StatusCode}): {body}");
            }
        }

        // Sets the remaining stock for a batch of offers and switches their stock checking on
        // or off. Verified live: changeStatus applies stockLevel/isStockCheckEnabled without
        // touching itemType, sellable or price.
        public async Task SetOfferStockAsync(
            long catalogId, IReadOnlyList<long> offerIds, int stockLevel, bool checkEnabled)
        {
            if (offerIds.Count == 0) return;

            var payload = new Dictionary<string, object>
            {
                ["offerIds"] = offerIds,
                ["stockLevel"] = stockLevel,
                ["isStockCheckEnabled"] = checkEnabled,
            };

            var response = await SendAuthorizedAsync(() => http.PatchAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/catalog/{catalogId}/offer/changeStatus",
                JsonContent.Create(payload)));

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new PointToOrderException(
                    $"Η ενημέρωση αποθέματος απέτυχε ({(int)response.StatusCode}): {body}");
            }
        }

        // Forces a fresh login so it always reflects the credentials currently typed into the
        // Settings form rather than a cached session.
        public async Task<PointToOrderUserInfo> TestConnectionAsync()
        {
            jwt = null;
            return await GetCurrentUserAsync();
        }

        private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<Task<HttpResponseMessage>> send)
        {
            if (jwt == null)
                await LoginAsync();

            SetAuthHeader();
            var response = await send();

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Session likely expired - log in once more and retry.
                jwt = null;
                await LoginAsync();
                SetAuthHeader();
                response = await send();
            }

            return response;
        }

        private void SetAuthHeader()
        {
            http.DefaultRequestHeaders.Remove("Authorization");
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        }

        private async Task LoginAsync()
        {
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync(
                    $"{baseUrl}/api/v1/auth/login", new { username, password });
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η σύνδεση με το PointToOrder:\n{ex.Message}");
            }

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Η σύνδεση στο PointToOrder απέτυχε ({(int)response.StatusCode}): {body}");

            try
            {
                using var doc = JsonDocument.Parse(body);
                jwt = doc.RootElement.GetProperty("tokens").GetProperty("jwt").GetString();
            }
            catch (Exception ex) when (
                ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
            {
                throw new PointToOrderException("Η απάντηση σύνδεσης του PointToOrder δεν περιείχε token.");
            }

            if (string.IsNullOrEmpty(jwt))
                throw new PointToOrderException("Η απάντηση σύνδεσης του PointToOrder δεν περιείχε token.");
        }

        public void Dispose() => http.Dispose();
    }
}
