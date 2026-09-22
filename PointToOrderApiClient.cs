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

        // ---- customers (ported from ΔΙΑΧΕΙΡΙΣΗ, where each behaviour was verified live) ----
        //
        // customers/search-all, NOT customers/search: plain /search answers 500 «Failed to
        // write request» for every input (also seen here on store 1110, 18/09/2026). E-mail
        // matching is exact and case-insensitive.

        public async Task<List<PlatformCustomer>> GetCustomersAsync()
        {
            var all = new List<PlatformCustomer>();
            for (int page = 0; page < 200; page++)
            {
                int current = page;
                var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                    $"{baseUrl}/api/v1/stores/{storeId}/customers/search-all?page={current}&size=100",
                    new Dictionary<string, object> { ["customerType"] = "ALL" }));

                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new PointToOrderException(
                        $"Η ανάγνωση πελατών από το PointToOrder απέτυχε ({(int)response.StatusCode}): {body}");

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var c in data.EnumerateArray())
                {
                    string Text(string key) =>
                        c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
                    all.Add(new PlatformCustomer(
                        ReadId(c, "id"), Text("name"), Text("email"), Text("phoneNumber"), Text("memberCode"),
                        c.TryGetProperty("registered", out var r) && r.ValueKind == JsonValueKind.True));
                }

                int totalPages = doc.RootElement.TryGetProperty("totalPages", out var tp) && tp.ValueKind == JsonValueKind.Number
                    ? tp.GetInt32() : 1;
                if (page + 1 >= totalPages) break;
            }
            return all;
        }

        // The platform's security filter answers 400 SECURITY_REQUEST_BLOCKED for some values - seen
        // 21/09/2026 for any @example.com address (a known fake domain); real addresses pass.
        private static string Explain(string body) => body.Contains("SECURITY_REQUEST_BLOCKED")
            ? "το PointToOrder απέρριψε το αίτημα για λόγους ασφαλείας — συνήθως επειδή το e-mail δεν είναι πραγματικό (π.χ. @example.com)"
            : body;

        // The platform's own customer id (the invite endpoint takes nothing else): by e-mail
        // first, then by card.
        public async Task<long?> FindCustomerIdAsync(string? email, string? memberCode)
        {
            foreach (var criteria in new[]
            {
                string.IsNullOrWhiteSpace(email) ? null : new Dictionary<string, object> { ["email"] = email!.Trim() },
                string.IsNullOrWhiteSpace(memberCode) ? null : new Dictionary<string, object> { ["memberCode"] = memberCode!.Trim() },
            })
            {
                if (criteria == null) continue;
                var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                    $"{baseUrl}/api/v1/stores/{storeId}/customers/search-all?page=0&size=1", criteria));

                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new PointToOrderException(
                        $"Η αναζήτηση στο PointToOrder απέτυχε ({(int)response.StatusCode}): {Explain(body)}");

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array &&
                    data.GetArrayLength() > 0)
                {
                    long id = ReadId(data[0], "id");
                    if (id != 0) return id;
                }
            }
            return null;
        }

        // True = created. False = the platform refused it as a duplicate: a repeated card is
        // 400 CUSTOMER_VOUCHER_ALREADY_PRESENT, a repeated e-mail 409 «Email already exists»
        // (the e-mail is unique across the WHOLE platform, the search only covers this store).
        // Anything else throws. phoneNumber must be a Greek mobile - see PointToOrderPhone.
        public async Task<bool> CreateCustomerAsync(
            string firstName, string lastName, string memberCode, string email, string phoneNumber)
        {
            var response = await SendAuthorizedAsync(() => http.PostAsJsonAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/customers/create",
                new Dictionary<string, object>
                {
                    ["firstName"] = firstName,
                    ["lastName"] = lastName,
                    ["memberCode"] = memberCode,
                    ["email"] = email,
                    ["phoneNumber"] = phoneNumber,
                }));

            if (response.IsSuccessStatusCode) return true;

            string body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("CUSTOMER_VOUCHER_ALREADY_PRESENT"))
                return false;
            if (response.StatusCode == HttpStatusCode.Conflict &&
                body.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                return false;

            throw new PointToOrderException($"Αποτυχία δημιουργίας πελάτη ({(int)response.StatusCode}): {Explain(body)}");
        }

        // Puts the card (memberCode) on an existing platform account. The update endpoint wants
        // the whole registration record back, so the current one is read first and only the
        // card changes. Returns null on success, else why the platform refused - seen live on
        // 1110: 403 «The user is registered online so only he can change their details» for an
        // account the customer registered themselves; the card then lives in Orexsys only.
        public async Task<string?> TrySetMemberCodeAsync(long customerId, string memberCode)
        {
            var read = await SendAuthorizedAsync(() => http.GetAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/customers/{customerId}"));
            string current = await read.Content.ReadAsStringAsync();
            if (!read.IsSuccessStatusCode)
                return $"δεν διαβάστηκε ο λογαριασμός ({(int)read.StatusCode})";

            using var doc = JsonDocument.Parse(current);
            var r = doc.RootElement;
            string Text(string key) =>
                r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
            long? Nested(string obj) =>
                r.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("id", out var i) &&
                i.ValueKind == JsonValueKind.Number ? i.GetInt64() : null;

            var payload = new Dictionary<string, object>
            {
                ["firstName"] = Text("firstName"),
                ["lastName"] = Text("lastName"),
                ["email"] = Text("email"),
                ["phoneNumber"] = Text("phoneNumber"),
                ["memberCode"] = memberCode,
            };
            if (Nested("country") is long country) payload["countryId"] = country;
            if (Nested("preferredLanguage") is long language) payload["preferredLanguageId"] = language;

            var response = await SendAuthorizedAsync(() => http.PutAsJsonAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/customers/{customerId}", payload));
            if (response.IsSuccessStatusCode) return null;

            string body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return "ο πελάτης έκανε μόνος του εγγραφή στο PointToOrder και μόνο εκείνος μπορεί να αλλάξει τα στοιχεία του";
            if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("CUSTOMER_VOUCHER_ALREADY_PRESENT"))
                return "η κάρτα χρησιμοποιείται ήδη από άλλον λογαριασμό στο PointToOrder";
            return $"{(int)response.StatusCode}: {Explain(body)}";
        }

        // PointToOrder's own «Invite to Register» e-mail. A real e-mail, cannot be recalled.
        public async Task InviteCustomerAsync(long customerId)
        {
            var response = await SendAuthorizedAsync(() => http.PostAsync(
                $"{baseUrl}/api/v1/stores/{storeId}/customers/{customerId}/invite", null));

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new PointToOrderException($"Η αποστολή πρόσκλησης απέτυχε ({(int)response.StatusCode}): {body}");
            }
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

        // The option groups nested in one offer (CHILD categories in the offer's `categories[]`,
        // each with its choice offers) - they do not appear in the catalog listing, so a full
        // reset has to ask per offer. Also returns the raw JSON, for the reset's backup file.
        public async Task<(List<(long CategoryId, List<long> OfferIds)> Groups, string Json)> GetOptionGroupsAsync(
            long catalogId, long offerId)
        {
            var response = await SendAuthorizedAsync(() => http.GetAsync(
                $"{baseUrl}/api/v2/user/stores/{storeId}/catalog/{catalogId}/offer/{offerId}"));

            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new PointToOrderException(
                    $"Δεν ήταν δυνατή η ανάγνωση του προϊόντος {offerId} ({(int)response.StatusCode}): {body}");

            var groups = new List<(long, List<long>)>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("categories", out var categories) &&
                categories.ValueKind == JsonValueKind.Array)
            {
                foreach (var category in categories.EnumerateArray())
                {
                    var offers = new List<long>();
                    if (category.TryGetProperty("offers", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var offer in list.EnumerateArray())
                        {
                            long id = ReadId(offer, "offerId");
                            if (id != 0) offers.Add(id);
                        }
                    long categoryId = ReadId(category, "categoryId", "id");
                    if (categoryId != 0) groups.Add((categoryId, offers));
                }
            }
            return (groups, body);
        }

        public async Task<string> GetCatalogJsonAsync()
        {
            var response = await SendAuthorizedAsync(
                () => http.GetAsync($"{baseUrl}/api/v1/user/stores/{storeId}/catalog"));
            return await response.Content.ReadAsStringAsync();
        }

        public string StoreId => storeId;

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
