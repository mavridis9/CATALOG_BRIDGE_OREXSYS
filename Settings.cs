using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CatalogBridge
{
    // Which of the three Orexsys price columns (PRLD) the platform's single price is compared
    // against. Verified on the live databases: RESTPRICE is populated on only 5 of 703 rows,
    // while DELIVERYPRICE and TAKEAWAYPRICE are populated on 408 and are equal to each other on
    // all but 2 rows - so Delivery is the sensible default and the choice only matters for
    // those two items, but it is a real business decision so it stays configurable.
    public enum PriceSource { Delivery, TakeAway, Restaurant }

    public class Settings
    {
        private const string EncryptedPrefix = "ENC:";

        // Deliberately a different entropy string from CustomerAdder's: the two apps keep
        // separate settings.ini files and neither should be able to decrypt the other's.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CatalogBridge.Settings.v1");

        public string Server { get; init; } = "localhost";
        public string Port { get; init; } = "3050";
        public string Database { get; init; } = "";
        public string User { get; init; } = "SYSDBA";
        public string Password { get; init; } = "";

        // Orexsys databases are WIN1253 (Greek). Connecting with the wrong charset returns
        // mangled product names rather than failing outright, so this defaults to WIN1253
        // instead of UTF8.
        public string Charset { get; init; } = "WIN1253";

        public string ApiBaseUrl { get; init; } = "https://admin.point2order.gr";
        public string ApiUsername { get; init; } = "";
        public string ApiPassword { get; init; } = "";
        public string ApiStoreId { get; init; } = "";

        // Orexsys price list to read prices from (PRLD.PRLSID). The live databases have exactly
        // one list, PRLSID 1 «Βασικός Τιμοκατάλογος».
        public long PriceListId { get; init; } = 1;

        public PriceSource PriceSource { get; init; } = PriceSource.Delivery;

        // Prices are decimal currency, so a plain equality test would flag rounding noise as a
        // difference. Anything within this many euro counts as "same price".
        public decimal PriceTolerance { get; init; } = 0.005m;

        // Push the till's remaining-quantity counters (SCRB.REMAIN) to PointToOrder as
        // stockLevel, automatically every 5 minutes while the app is open.
        public bool StockSyncEnabled { get; init; }

        // Which technician screens the sidebar shows. The end user sees only «Αρχική» (the
        // one-button sync); technicians enable what they need from the Settings screen.
        public bool ShowDashboard { get; init; }
        public bool ShowCompare { get; init; }
        public bool ShowPush { get; init; }
        public bool ShowEfic { get; init; }

        // The store code new EFIC rows are scoped by (EFIC.STOREID, e.g. PO11521). Normally
        // read from EFST at runtime; this override exists for fresh installations where EFST
        // is still empty (verified on Stavros.FDB) and the operator has to supply it once.
        public string EficStoreCode { get; init; } = "";

        public static string IniPath => Path.Combine(AppContext.BaseDirectory, "settings.ini");

        public static bool Exists() => File.Exists(IniPath);

        public static Settings Load()
        {
            if (!File.Exists(IniPath))
                return new Settings();

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in File.ReadAllLines(IniPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }

            string Get(string key, string fallback) =>
                values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

            long GetLong(string key, long fallback) =>
                values.TryGetValue(key, out var v) && long.TryParse(v, out long n) ? n : fallback;

            return new Settings
            {
                Server = Get("Server", "localhost"),
                Port = Get("Port", "3050"),
                Database = Get("Database", ""),
                User = Get("User", "SYSDBA"),
                Password = DecryptSecret(Get("Password", "")),
                Charset = Get("Charset", "WIN1253"),
                ApiBaseUrl = Get("ApiBaseUrl", "https://admin.point2order.gr"),
                ApiUsername = Get("ApiUsername", ""),
                ApiPassword = DecryptSecret(Get("ApiPassword", "")),
                ApiStoreId = Get("ApiStoreId", ""),
                PriceListId = GetLong("PriceListId", 1),
                PriceSource = Enum.TryParse(Get("PriceSource", "Delivery"), true, out PriceSource ps)
                    ? ps : PriceSource.Delivery,
                EficStoreCode = Get("EficStoreCode", ""),
                StockSyncEnabled = Get("StockSyncEnabled", "false") == "true",
                ShowDashboard = Get("ShowDashboard", "false") == "true",
                ShowCompare = Get("ShowCompare", "false") == "true",
                ShowPush = Get("ShowPush", "false") == "true",
                ShowEfic = Get("ShowEfic", "false") == "true",
            };
        }

        public static void Save(Settings settings)
        {
            string[] lines =
            {
                "; Ρυθμίσεις CatalogBridge (Orexsys <-> PointToOrder).",
                "; Οι κωδικοί πρόσβασης είναι κρυπτογραφημένοι (DPAPI, δεμένοι με αυτόν τον υπολογιστή).",
                "; Ορίστε τους από την οθόνη Ρυθμίσεων της εφαρμογής, όχι με το χέρι.",
                "",
                "; --- Orexsys / Firebird ---",
                $"Server={settings.Server}",
                $"Port={settings.Port}",
                $"Database={settings.Database}",
                $"User={settings.User}",
                $"Password={EncryptSecret(settings.Password)}",
                $"Charset={settings.Charset}",
                "",
                "; --- PointToOrder API ---",
                $"ApiBaseUrl={settings.ApiBaseUrl}",
                $"ApiUsername={settings.ApiUsername}",
                $"ApiPassword={EncryptSecret(settings.ApiPassword)}",
                $"ApiStoreId={settings.ApiStoreId}",
                "",
                "; --- Τιμοκατάλογος ---",
                "; PriceSource: Delivery | TakeAway | Restaurant",
                $"PriceListId={settings.PriceListId}",
                $"PriceSource={settings.PriceSource}",
                "",
                "; Κωδικός καταστήματος για νέες γραμμές EFIC (π.χ. PO11521).",
                "; Κενό = διαβάζεται αυτόματα από τον πίνακα EFST.",
                $"EficStoreCode={settings.EficStoreCode}",
                "",
                "; Αυτόματη αποστολή υπολοίπων (SCRB) στο PointToOrder κάθε 5 λεπτά.",
                $"StockSyncEnabled={(settings.StockSyncEnabled ? "true" : "false")}",
                "",
                "; Οθόνες τεχνικού (true = εμφανίζονται στο πλάι).",
                $"ShowDashboard={(settings.ShowDashboard ? "true" : "false")}",
                $"ShowCompare={(settings.ShowCompare ? "true" : "false")}",
                $"ShowPush={(settings.ShowPush ? "true" : "false")}",
                $"ShowEfic={(settings.ShowEfic ? "true" : "false")}",
            };

            File.WriteAllLines(IniPath, lines);
        }

        public string ToConnectionString() =>
            $"User={User};Password={Password};Database={Database};DataSource={Server};Port={Port};Charset={Charset};";

        public bool IsDatabaseConfigured =>
            !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(Database);

        public bool IsApiConfigured =>
            !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
            !string.IsNullOrWhiteSpace(ApiUsername) &&
            !string.IsNullOrWhiteSpace(ApiPassword) &&
            !string.IsNullOrWhiteSpace(ApiStoreId);

        // The PRLD column this configuration reads prices from.
        public string PriceColumn => PriceSource switch
        {
            PriceSource.TakeAway => "TAKEAWAYPRICE",
            PriceSource.Restaurant => "RESTPRICE",
            _ => "DELIVERYPRICE",
        };

        public string PriceSourceLabel => PriceSource switch
        {
            PriceSource.TakeAway => "Take away",
            PriceSource.Restaurant => "Καταστήματος",
            _ => "Delivery",
        };

        private static string EncryptSecret(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";

            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine);
            return EncryptedPrefix + Convert.ToBase64String(encrypted);
        }

        private static string DecryptSecret(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";

            // A hand-edited settings.ini may still hold a plain-text value.
            if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
                return stored;

            try
            {
                byte[] encrypted = Convert.FromBase64String(stored.Substring(EncryptedPrefix.Length));
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine));
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                // Encrypted on a different PC, or corrupted - force reconfiguration.
                return "";
            }
        }
    }
}
