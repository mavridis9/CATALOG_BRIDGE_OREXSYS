using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CatalogBridge
{
    // The licence token - docs/LICENSING_AND_UPDATES.md §4. Pure: no files, no registry, no
    // clock of its own, so the signing tool (tools/LicenceSigner) and the tests share this
    // file with the app. Format: base64url(payload JSON) "." base64url(signature).
    //
    // Signature: ECDSA P-256 over SHA-256, IEEE P1363 (r‖s) - built into .NET, no package.
    // (The design doc said Ed25519; P-256 was chosen to keep the single-file exe free of a
    // crypto dependency. Same security class, same key handling.)
    public sealed record LicenseToken(
        string Product, string Licence, string Installation, string Customer,
        IReadOnlyList<string> Modules, long? Store,
        DateTime Issued, DateTime ValidUntil, DateTime LicenceUntil, int GraceDays)
    {
        public bool Has(Module module) => Modules.Contains(ModuleId(module), StringComparer.OrdinalIgnoreCase);

        public static string ModuleId(Module module) => module switch
        {
            Module.Catalogue => "catalogue",
            Module.Orders => "orders",
            _ => throw new ArgumentOutOfRangeException(nameof(module)),
        };

        // What the token allows RIGHT NOW for one module (§4.1). No token → NotLicensed is the
        // caller's job; this only answers for a verified token.
        public LicenseState StateOf(Module module, DateTime nowUtc)
        {
            if (!Has(module)) return LicenseState.NotLicensed;
            if (nowUtc <= ValidUntil) return LicenseState.Licensed;
            if (nowUtc <= ValidUntil.AddDays(GraceDays)) return LicenseState.Grace;
            return LicenseState.Expired;
        }

        // ---- wire format --------------------------------------------------------------------

        public string ToPayloadJson() => JsonSerializer.Serialize(new
        {
            v = 1,
            product = Product,
            licence = Licence,
            installation = Installation,
            customer = Customer,
            modules = Modules,
            store = Store,
            issued = Issued.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            valid_until = ValidUntil.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            licence_until = LicenceUntil.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            grace_days = GraceDays,
        });

        public static LicenseToken FromPayloadJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("v", out var v) || v.GetInt32() != 1) throw new FormatException("unsupported token version");
            string S(string name) => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";
            DateTime D(string name) => DateTime.Parse(S(name), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            var modules = r.TryGetProperty("modules", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
            long? store = r.TryGetProperty("store", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt64() : null;
            int grace = r.TryGetProperty("grace_days", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : 0;
            return new LicenseToken(S("product"), S("licence"), S("installation"), S("customer"), modules, store,
                D("issued"), D("valid_until"), D("licence_until"), grace);
        }

        // Sign with a PKCS#8 private key (base64). Only the signing tool calls this.
        public string Sign(string privateKeyPkcs8Base64)
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8Base64), out _);
            byte[] payload = Encoding.UTF8.GetBytes(ToPayloadJson());
            byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Base64Url(payload) + "." + Base64Url(signature);
        }

        public sealed record VerifyResult(LicenseToken? Token, string Problem)
        {
            public bool Ok => Token != null;
        }

        // Verifies signature (against ANY of the public keys - rotation), product and machine.
        // Expiry is not judged here: an expired-but-genuine token is still "this shop's
        // licence" and is what StateOf reports on. Problems are the Greek text the shop sees.
        public static VerifyResult Verify(string token, string product, string installation, IEnumerable<string> publicKeysSpkiBase64)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) return new VerifyResult(null, "Δεν υπάρχει άδεια χρήσης.");
            string[] parts = token.Split('.');
            if (parts.Length != 2) return new VerifyResult(null, "Η άδεια δεν έχει σωστή μορφή.");

            byte[] payload, signature;
            try { payload = FromBase64Url(parts[0]); signature = FromBase64Url(parts[1]); }
            catch (FormatException) { return new VerifyResult(null, "Η άδεια δεν έχει σωστή μορφή."); }

            bool genuine = false;
            foreach (string spki in publicKeysSpkiBase64)
            {
                try
                {
                    using var key = ECDsa.Create();
                    key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
                    if (key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    { genuine = true; break; }
                }
                catch (CryptographicException) { }
            }
            if (!genuine) return new VerifyResult(null, "Η άδεια δεν είναι γνήσια (η υπογραφή δεν επαληθεύεται).");

            LicenseToken parsed;
            try { parsed = FromPayloadJson(Encoding.UTF8.GetString(payload)); }
            catch (Exception ex) when (ex is JsonException or FormatException) { return new VerifyResult(null, "Η άδεια δεν διαβάζεται: " + ex.Message); }

            if (!string.Equals(parsed.Product, product, StringComparison.OrdinalIgnoreCase))
                return new VerifyResult(null, $"Η άδεια είναι για άλλο πρόγραμμα ({parsed.Product}).");
            if (!string.Equals(Normalize(parsed.Installation), Normalize(installation), StringComparison.OrdinalIgnoreCase))
                return new VerifyResult(null, "Η άδεια είναι για άλλον υπολογιστή (δεν ταιριάζει το αποτύπωμα).");
            return new VerifyResult(parsed, "");
        }

        private static string Normalize(string fingerprint) => (fingerprint ?? "").Replace("-", "").Trim();

        public static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromBase64Url(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            switch (b.Length % 4) { case 2: b += "=="; break; case 3: b += "="; break; case 1: throw new FormatException(); }
            return Convert.FromBase64String(b);
        }
    }

    public enum Module { Catalogue, Orders }

    // Licensed → everything. Grace → everything, amber warning (valid_until passed, still
    // inside grace_days). Expired → read-only, automatic services off. NotLicensed → the
    // module is not in the token (or there is no token): its screens are hidden.
    public enum LicenseState { Licensed, Grace, Expired, NotLicensed }
}
