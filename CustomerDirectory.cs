using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // What the shop types for a new customer. Owner decision 21/09/2026: ONLY the shop creates
    // customers - once, here, for both systems - so the card, the e-mail and the name are the
    // same on both sides and the customer's first online order already finds them (orders
    // match by e-mail only, see EfomTriggerGuard).
    public sealed record CustomerInput(string Name, string Card, string Email, string Mobile)
    {
        public CustomerInput Trimmed() =>
            new((Name ?? "").Trim(), (Card ?? "").Trim(), (Email ?? "").Trim(), (Mobile ?? "").Trim());
    }

    public sealed record OrexsysCustomer(long CustId, string Name, string Card, string Email, string Phone);

    public sealed record PlatformCustomer(long Id, string Name, string Email, string Phone, string MemberCode, bool Registered);

    public enum CustomerLink
    {
        Linked,             // same e-mail on both sides - orders reach this customer
        OrexsysOnly,        // has an e-mail in Orexsys but no PointToOrder account yet
        PlatformOnly,       // account on PointToOrder, nobody in Orexsys carries that e-mail
        DuplicateEmail,     // two or more Orexsys customers carry the same e-mail
    }

    public class CustomerRow
    {
        public OrexsysCustomer? Orexsys { get; init; }
        public PlatformCustomer? Platform { get; init; }
        public CustomerLink Link { get; init; }
        public string InvitedOn { get; set; } = "";

        public string Name => Orexsys?.Name is { Length: > 0 } n ? n : (Platform?.Name ?? "");
        public string Card => Orexsys?.Card is { Length: > 0 } c ? c : (Platform?.MemberCode ?? "");
        public string Email => Orexsys?.Email is { Length: > 0 } e ? e : (Platform?.Email ?? "");
        public string Phone => Orexsys?.Phone is { Length: > 0 } p ? p : (Platform?.Phone ?? "");

        public string LinkText => Link switch
        {
            CustomerLink.Linked => "✓ Και στα δύο",
            CustomerLink.OrexsysOnly => "← Μόνο στο Orexsys",
            CustomerLink.PlatformOnly => "→ Μόνο στο PointToOrder",
            _ => "⛔ Ίδιο e-mail σε 2+ πελάτες",
        };

        // Has the person accepted the invitation (set a password) on PointToOrder?
        public string AccountText =>
            Platform == null ? "—" : Platform.Registered ? "✓ Ενεργός λογαριασμός" :
            InvitedOn.Length > 0 ? $"Πρόσκληση {InvitedOn}" : "Χωρίς πρόσκληση";

        // A card that differs between the two systems is worth seeing: the platform's
        // memberCode cannot be changed by the shop once the person has registered.
        public string CardNote =>
            Orexsys != null && Platform != null && Platform.MemberCode.Length > 0 &&
            !string.Equals(Orexsys.Card, Platform.MemberCode, StringComparison.OrdinalIgnoreCase)
                ? $"⚠ P2O: {Platform.MemberCode}" : "";
    }

    public static class CustomerDirectory
    {
        // ---- pure logic (unit-tested) --------------------------------------------------

        // Null when acceptable, otherwise the Greek message to show. The mobile is optional:
        // PointToOrder insists on a Greek mobile, so an empty one gets a stand-in built from
        // the CUSTID (PointToOrderPhone), but a typed one must be a real Greek mobile - a wrong
        // number passes creation and then makes the platform refuse the customer's ORDER.
        public static string? Validate(CustomerInput raw)
        {
            var c = raw.Trimmed();
            if (c.Name.Length == 0) return "Συμπληρώστε το όνομα του πελάτη.";
            if (c.Name.Length > 50) return "Το όνομα πρέπει να έχει έως 50 χαρακτήρες.";
            if (c.Card.Length == 0) return "Συμπληρώστε τον αριθμό κάρτας.";
            if (c.Card.Length > 50) return "Η κάρτα πρέπει να έχει έως 50 χαρακτήρες.";
            string? email = EmailValidator.Validate(c.Email);
            if (email != null) return email;
            if (c.Mobile.Length > 0 && !PointToOrderPhone.IsRealMobile(c.Mobile))
                return "Το κινητό πρέπει να είναι ελληνικό κινητό (69 και 8 ψηφία) ή κενό.";
            return null;
        }

        private static string Key(string email) => (email ?? "").Trim().ToUpperInvariant();

        // The two customer lists as one: matched on e-mail, exactly as the order trigger does
        // (upper + trim). Orexsys customers without an e-mail are not part of this screen.
        public static List<CustomerRow> Merge(
            IEnumerable<OrexsysCustomer> orexsys, IEnumerable<PlatformCustomer> platform)
        {
            var rows = new List<CustomerRow>();
            var platformByEmail = new Dictionary<string, PlatformCustomer>();
            foreach (var p in platform)
                if (Key(p.Email).Length > 0 && !platformByEmail.ContainsKey(Key(p.Email)))
                    platformByEmail[Key(p.Email)] = p;

            var used = new HashSet<string>();
            foreach (var group in orexsys.Where(o => Key(o.Email).Length > 0).GroupBy(o => Key(o.Email)))
            {
                platformByEmail.TryGetValue(group.Key, out var match);
                if (match != null) used.Add(group.Key);
                bool duplicate = group.Count() > 1;
                foreach (var o in group)
                    rows.Add(new CustomerRow
                    {
                        Orexsys = o,
                        Platform = match,
                        Link = duplicate ? CustomerLink.DuplicateEmail
                             : match != null ? CustomerLink.Linked : CustomerLink.OrexsysOnly,
                    });
            }

            foreach (var p in platform)
                if (Key(p.Email).Length == 0 || !used.Contains(Key(p.Email)))
                    rows.Add(new CustomerRow { Platform = p, Link = CustomerLink.PlatformOnly });

            return rows
                .OrderBy(r => r.Link == CustomerLink.DuplicateEmail ? 0 : r.Link == CustomerLink.PlatformOnly ? 1 :
                              r.Link == CustomerLink.OrexsysOnly ? 2 : 3)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        // ---- Orexsys -------------------------------------------------------------------

        // Only customers carrying an e-mail: a real shop has tens of thousands of customers
        // (STOA: 150.893) that will never exist online.
        public static Task<List<OrexsysCustomer>> LoadOrexsysAsync(Settings settings) => Task.Run(() =>
        {
            var list = new List<OrexsysCustomer>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var command = new FbCommand(
                "SELECT CUSTID, COALESCE(NULLIF(TRIM(FNAME), ''), LNAME) AS CNAME, CARDNO, EMAIL, PHONE " +
                "FROM CUST WHERE CUSTID <> 0 AND TRIM(COALESCE(EMAIL, '')) <> '' ORDER BY 2", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new OrexsysCustomer(
                    Convert.ToInt64(reader["CUSTID"]), Str(reader["CNAME"]), Str(reader["CARDNO"]),
                    Str(reader["EMAIL"]), Str(reader["PHONE"])));
            return list;
        });

        public sealed class DuplicateCustomerException : InvalidOperationException
        {
            public DuplicateCustomerException(string message) : base(message) { }
        }

        // Creates the customer the way ΔΙΑΧΕΙΡΙΣΗ does (verified on the customer's live data
        // there): the full name in both FNAME and LNAME, CUSTID from Orexsys's own trigger, and
        // a CUSP row - the till's phone search reads CUSP, not CUST.PHONE. One transaction.
        // Refuses a card or an e-mail that is already on another customer: a duplicate e-mail
        // would make the order trigger's «select first 1» pick one of the two at random.
        public static Task<long> CreateInOrexsysAsync(Settings settings, CustomerInput raw) => Task.Run(() =>
        {
            var c = raw.Trimmed();
            string? problem = Validate(c);
            if (problem != null) throw new InvalidOperationException(problem);
            string? mobile = PointToOrderPhone.NormalizeMobile(c.Mobile);

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var check = new FbCommand(
                "SELECT FIRST 1 CUSTID, COALESCE(NULLIF(TRIM(FNAME), ''), LNAME) AS CNAME, CARDNO, EMAIL FROM CUST " +
                "WHERE CUSTID <> 0 AND (CARDNO = @card OR UPPER(TRIM(EMAIL)) = UPPER(@email))", connection, transaction))
            {
                check.Parameters.Add("@card", FbDbType.VarChar, 50).Value = c.Card;
                check.Parameters.Add("@email", FbDbType.VarChar, 50).Value = c.Email;
                using var reader = check.ExecuteReader();
                if (reader.Read())
                    throw new DuplicateCustomerException(
                        $"Υπάρχει ήδη πελάτης στο Orexsys με αυτή την κάρτα ή το e-mail: " +
                        $"«{Str(reader["CNAME"])}» (κάρτα {Str(reader["CARDNO"])}, e-mail {Str(reader["EMAIL"])}). " +
                        "Δεν δημιουργήθηκε τίποτα.");
            }

            long custId;
            using (var insert = new FbCommand(
                "INSERT INTO CUST (FNAME, LNAME, CARDNO, PHONE, EMAIL) " +
                "VALUES (@name, @name, @card, @phone, @email) RETURNING CUSTID", connection, transaction))
            {
                insert.Parameters.Add("@name", FbDbType.VarChar, 50).Value = c.Name;
                insert.Parameters.Add("@card", FbDbType.VarChar, 50).Value = c.Card;
                insert.Parameters.Add("@phone", FbDbType.VarChar, 30).Value = (object?)mobile ?? DBNull.Value;
                insert.Parameters.Add("@email", FbDbType.VarChar, 50).Value = c.Email;
                custId = Convert.ToInt64(insert.ExecuteScalar());
            }

            if (mobile != null)
                using (var cusp = new FbCommand("INSERT INTO CUSP (CUSTID, PHONE) VALUES (@id, @phone)", connection, transaction))
                {
                    cusp.Parameters.Add("@id", FbDbType.BigInt).Value = custId;
                    cusp.Parameters.Add("@phone", FbDbType.VarChar, 30).Value = mobile;
                    cusp.ExecuteNonQuery();
                }

            transaction.Commit();
            return custId;
        });

        // ---- PointToOrder --------------------------------------------------------------

        public enum PlatformOutcome { Created, AlreadyThere, RejectedAsDuplicate }

        // Search first (this store), then create. «RejectedAsDuplicate» = the search found
        // nobody here but the platform refused: the e-mail is unique across the whole platform,
        // so it is registered under another store. Reported as NOT created - ΔΙΑΧΕΙΡΙΣΗ once
        // shipped a false «sent» on exactly this case.
        public static async Task<PlatformOutcome> CreateOnPlatformAsync(
            PointToOrderApiClient api, long custId, CustomerInput raw)
        {
            var c = raw.Trimmed();
            if (await api.FindCustomerIdAsync(c.Email, c.Card) != null)
                return PlatformOutcome.AlreadyThere;

            var (first, last) = PointToOrderName.Split(c.Name);
            bool created = await api.CreateCustomerAsync(first, last, c.Card, c.Email,
                PointToOrderPhone.Resolve(custId, c.Mobile));
            return created ? PlatformOutcome.Created : PlatformOutcome.RejectedAsDuplicate;
        }

        // ---- log and invitation record ---------------------------------------------------

        private static string Folder => Path.Combine(AppContext.BaseDirectory, "backups");

        public static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "customers.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line.ReplaceLineEndings(" ")}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { /* a log must never break the screen */ }
        }

        // An invitation is a real e-mail and cannot be recalled, so the screen remembers when
        // one was sent (e-mail → date) next to the exe; PointToOrder itself does not say.
        private static string InvitesPath => Path.Combine(AppContext.BaseDirectory, "customer_invites.txt");

        public static Dictionary<string, string> LoadInvites()
        {
            var map = new Dictionary<string, string>();
            try
            {
                if (File.Exists(InvitesPath))
                    foreach (string line in File.ReadAllLines(InvitesPath))
                    {
                        int i = line.IndexOf('\t');
                        if (i > 0) map[line[..i]] = line[(i + 1)..];
                    }
            }
            catch { }
            return map;
        }

        public static void RecordInvite(string email)
        {
            try { File.AppendAllText(InvitesPath, $"{Key(email)}\t{DateTime.Now:dd/MM/yyyy}{Environment.NewLine}"); }
            catch { }
        }

        public static string InviteKey(string email) => Key(email);

        // ---- the customer's orders (double-click) --------------------------------------------

        public sealed record CustomerOrder(long OrdmId, DateTime When, string Kind, string Status, decimal Total, string Items);

        public sealed class CustomerOrders
        {
            public List<CustomerOrder> Orders { get; } = new();
            public int Cancelled { get; set; }
            public int Count => Orders.Count;
            public decimal Total => Orders.Sum(o => o.Total);
            public decimal Average => Count == 0 ? 0m : Math.Round(Total / Count, 2);
            public DateTime? First => Orders.Count == 0 ? null : Orders.Min(o => o.When);
            public DateTime? Last => Orders.Count == 0 ? null : Orders.Max(o => o.When);
        }

        // Every real order of the customer, online and at the till. The customer link is the
        // one every Orexsys reader uses (CARDCUSTID when set, else CUSTID — a till sale on a
        // card carries the person in CARDCUSTID with CUSTID 0); cancelled = CANCELED = 1 or
        // TOTAL = 0, counted separately and left out of the totals. Items = the main lines
        // (ORDDID = PARENTID; extras hang off them). Read-only.
        public static Task<CustomerOrders> LoadOrdersAsync(Settings settings, long custId) => Task.Run(() =>
        {
            var result = new CustomerOrders();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            const string sql = @"
                SELECT m.ORDMID, m.ODATE, m.OTIME, m.OTYPE, m.STS, m.CANCELED, m.TOTAL,
                       (SELECT LIST(TRIM(d.NAME) || IIF(d.QTY <> 1, ' ×' || CAST(d.QTY AS INTEGER), ''), ', ')
                          FROM ORDD d WHERE d.ORDMID = m.ORDMID AND d.ORDDID = d.PARENTID AND d.QTY > 0) AS ITEMS
                FROM ORDM m
                WHERE (CASE WHEN COALESCE(m.CARDCUSTID, 0) <> 0 THEN m.CARDCUSTID ELSE m.CUSTID END) = @id
                ORDER BY m.ODATE DESC, m.OTIME DESC";
            using var command = new FbCommand(sql, connection);
            command.Parameters.Add("@id", FbDbType.BigInt).Value = custId;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                decimal total = reader["TOTAL"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["TOTAL"]);
                bool cancelled = (reader["CANCELED"] != DBNull.Value && Convert.ToInt32(reader["CANCELED"]) == 1) || total == 0m;
                if (cancelled) { result.Cancelled++; continue; }

                var date = reader["ODATE"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(reader["ODATE"]);
                var time = reader["OTIME"] is TimeSpan t ? t : TimeSpan.Zero;   // Firebird TIME → TimeSpan
                int otype = reader["OTYPE"] == DBNull.Value ? 0 : Convert.ToInt32(reader["OTYPE"]);
                int sts = reader["STS"] == DBNull.Value ? 0 : Convert.ToInt32(reader["STS"]);
                result.Orders.Add(new CustomerOrder(
                    Convert.ToInt64(reader["ORDMID"]), date + time,
                    otype switch { 2 => "Online / delivery", 3 => "Take away", 1 => "Τραπέζι", _ => $"Τύπος {otype}" },
                    sts == 4 ? "Κλειστή" : "Ανοιχτή",
                    total, Str(reader["ITEMS"])));
            }
            return result;
        });

        // ---- «Κάρτα...»: a card for a customer that has none --------------------------------

        public static string? ValidateCard(string card)
        {
            string c = (card ?? "").Trim();
            if (c.Length == 0) return "Συμπληρώστε τον αριθμό κάρτας.";
            if (c.Length > 50) return "Η κάρτα πρέπει να έχει έως 50 χαρακτήρες.";
            return null;
        }

        // What a PointToOrder-only account becomes in Orexsys once it gets a card: its name,
        // e-mail and phone (only if a real Greek mobile - the stand-in numbers are not phones).
        public static CustomerInput FromPlatform(PlatformCustomer p, string card) =>
            new(p.Name, card, p.Email, PointToOrderPhone.NormalizeMobile(p.Phone) ?? "");

        // Fills CUST.CARDNO of one customer that has none. Fenced: the card may not be on
        // another customer, and exactly one row - the one asked for, still without a card -
        // must change, or nothing does.
        public static Task SetCardAsync(Settings settings, long custId, string card) => Task.Run(() =>
        {
            string? problem = ValidateCard(card);
            if (problem != null) throw new InvalidOperationException(problem);
            string c = card.Trim();

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var check = new FbCommand(
                "SELECT FIRST 1 CUSTID, COALESCE(NULLIF(TRIM(FNAME), ''), LNAME) FROM CUST WHERE CARDNO = @card AND CUSTID <> @id",
                connection, transaction))
            {
                check.Parameters.Add("@card", FbDbType.VarChar, 50).Value = c;
                check.Parameters.Add("@id", FbDbType.BigInt).Value = custId;
                using var reader = check.ExecuteReader();
                if (reader.Read())
                    throw new DuplicateCustomerException(
                        $"Η κάρτα «{c}» ανήκει ήδη στον πελάτη «{Str(reader[1])}» (CUSTID {reader[0]}). Δεν άλλαξε τίποτα.");
            }

            using var update = new FbCommand(
                "UPDATE CUST SET CARDNO = @card WHERE CUSTID = @id AND TRIM(COALESCE(CARDNO, '')) = ''",
                connection, transaction);
            update.Parameters.Add("@card", FbDbType.VarChar, 50).Value = c;
            update.Parameters.Add("@id", FbDbType.BigInt).Value = custId;
            if (update.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException("Ο πελάτης δεν βρέθηκε ή έχει ήδη κάρτα. Δεν άλλαξε τίποτα.");
            }
            transaction.Commit();
        });

        private static string Str(object value) => value == DBNull.Value ? "" : (value.ToString() ?? "").Trim();
    }

    public static class PointToOrderName
    {
        // PointToOrder wants firstName/lastName and shows them joined; Orexsys has one name.
        // Sending the full name as both doubled it on the platform (ΔΙΑΧΕΙΡΙΣΗ) - split on the
        // first space instead.
        public static (string FirstName, string LastName) Split(string fullName)
        {
            string trimmed = (fullName ?? "").Trim();
            int space = trimmed.IndexOf(' ');
            return space <= 0 ? (trimmed, trimmed) : (trimmed[..space], trimmed[(space + 1)..].Trim());
        }
    }
}
