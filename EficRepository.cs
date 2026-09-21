using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // One EFIC correlation row, joined with the Orexsys product it points at.
    public class EficRow
    {
        public long EficId { get; set; }
        public string StoreId { get; set; } = "";
        public string Code { get; set; } = "";       // = the platform's offerId
        public string Name { get; set; } = "";       // platform-side name as EFIC recorded it
        public long ItmsId { get; set; }
        public string OrexsysCode { get; set; } = "";
        public string OrexsysName { get; set; } = "";

        // The size (ITMV) this row stands for; 0 = the product itself. Shown blank for 0 so
        // the size-less majority of rows stays uncluttered.
        public long ItmvId { get; set; }
        public string SizeName { get; set; } = "";
        public string SizeText => ItmvId == 0 ? "" : (SizeName.Length > 0 ? SizeName : $"#{ItmvId}");

        // A correlation pointing at an extras-category product (ITMC.ISEXTRA=1) is almost
        // certainly wrong: extras are modifiers on another item's line, and none of the 435
        // genuine live rows targets one.
        public bool TargetsExtra { get; set; }

        // Filled in from the live platform catalog, not from the database. Health is judged
        // by IDs only (user decision): a row is fine as long as its offerId exists on the
        // platform and its ITMSID is a real, non-extra product - names never matter.
        public string PlatformName { get; set; } = "";
        public bool FoundOnPlatform { get; set; }

        public string PlatformText => FoundOnPlatform ? PlatformName : "— δεν υπάρχει πλέον —";
        public string HealthText =>
            TargetsExtra ? "❓ Σε extra" :
            !FoundOnPlatform ? "⚠ Ορφανή" : "✓";
    }

    // Thrown instead of writing when a write could reach an EFIC row that is not ours. The
    // message is Greek and shown as-is by every screen's existing error handling.
    public class EficSafetyException : InvalidOperationException
    {
        public EficSafetyException(string message) : base(message) { }
    }

    // Reads and writes the EFIC correlation table. The write conventions were read off the
    // live triggers rather than guessed:
    //  - EFIC_BIUD_TRG generates EFICID from the efic_sq sequence when null and stamps RECVER,
    //    so inserts send neither and read the id back with RETURNING.
    //  - EFIC_AIUD_TRG backfills EFOD.ITMSID on any still-unmapped order lines with the same
    //    STOREID+CODE whenever a row is inserted or updated - fixing a correlation here
    //    retroactively repairs pending external order lines, courtesy of Orexsys itself.
    //  - EFIC_LD logs every delete through Orexsys's own logdelete audit.
    //
    // EFIC is shared by every delivery platform Orexsys talks to: STOA.FDB, a real customer,
    // carries live Wolt (211 rows) and efood (233) correlations. Those rows are never ours to
    // touch, so the write side is fenced three ways - all enforced HERE, never trusted to a
    // caller:
    //  1. Writes run only under the resolved P2O store code: never an empty one, never the
    //     EFSTID of another platform (see ResolveStoreCode / RequireOwnStoreCode).
    //  2. Every UPDATE/DELETE is constrained by STOREID as well as EFICID and must hit exactly
    //     the rows asked for, or it is rolled back - a wrong or stale EFICID cannot reach a
    //     Wolt or efood row even through a bug upstream.
    //  3. Before the first write to a database in a session, the whole table (every platform,
    //     every column) is saved to backups\EFIC_<database>_<time>.csv next to the exe. If
    //     that fails, nothing is written.
    public static class EficRepository
    {
        // How EFST marks the PointToOrder row: APPTYPE 60 («P2O» on LIGHTHOUSE2). Wolt is 28
        // and efood 6 on STOA.FDB. COALESCE so a row with NULLs counts as "not P2O".
        private const string IsP2ORow =
            "(COALESCE(APPTYPE, -1) = 60 OR COALESCE(NAME, '') CONTAINING 'P2O')";

        // The store code this installation's EFIC rows are scoped by: the Settings override if
        // set, otherwise the P2O row of EFST. Stavros.FDB kept a PO1007 row after moving to
        // store 1110, and STOA lists Wolt first in EFST - so every read filters by this code,
        // and neither the override nor the fallback may be another platform's code. Empty
        // means no usable P2O store: then no EFIC row counts as ours and nothing is written.
        internal static string ResolveStoreCode(FbConnection connection, Settings settings)
        {
            string configured = settings.EficStoreCode.Trim();
            if (configured.Length > 0)
                return BelongsToOtherPlatform(connection, configured) ? "" : configured;

            using var command = new FbCommand(
                $"SELECT FIRST 1 EFSTID FROM EFST WHERE {IsP2ORow} ORDER BY ID", connection);

            object? result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? "" : (result.ToString() ?? "").Trim();
        }

        // True when the code is the EFSTID of a non-P2O platform. A code EFST does not know at
        // all is fine: that is every fresh P2O install.
        private static bool BelongsToOtherPlatform(FbConnection connection, string code)
        {
            using var command = new FbCommand(
                $"SELECT COUNT(*) FROM EFST WHERE EFSTID = @code AND NOT {IsP2ORow}", connection);
            command.Parameters.AddWithValue("@code", code);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        private static string RequireOwnStoreCode(FbConnection connection, Settings settings)
        {
            string code = ResolveStoreCode(connection, settings);
            if (code.Length > 0) return code;

            string configured = settings.EficStoreCode.Trim();
            throw new EficSafetyException(configured.Length > 0
                ? $"Ο «Κωδ. EFIC» «{configured}» ανήκει σε άλλη πλατφόρμα (π.χ. Wolt / efood). " +
                  "Δεν γράφτηκε τίποτα — ορίστε στις Ρυθμίσεις τον κωδικό του PointToOrder."
                : "Δεν έχει οριστεί κωδικός PointToOrder («Κωδ. EFIC»). Δεν γράφτηκε τίποτα.");
        }

        public static Task<List<EficRow>> LoadAsync(Settings settings) => Task.Run(() =>
        {
            var rows = new List<EficRow>();

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            // An empty code matches no row, which is the point: never another platform's links.
            // (Not "(@store = '' OR ...)" - Firebird types the parameter from the '' literal as
            // CHAR(0) and truncates it.)
            const string sql = @"
                SELECT e.EFICID, e.STOREID, e.CODE, e.NAME, e.ITMSID, e.ITMVID,
                       i.CODE AS ICODE, i.NAME AS INAME, c.ISEXTRA, v.NAME AS VNAME
                FROM EFIC e
                LEFT JOIN ITMS i ON i.ITMSID = e.ITMSID
                LEFT JOIN ITMC c ON c.ITMCID = i.ITMCID
                LEFT JOIN ITMV v ON v.ITMVID = e.ITMVID
                WHERE e.STOREID = @store
                ORDER BY e.EFICID";

            using var command = new FbCommand(sql, connection);
            command.Parameters.AddWithValue("@store", ResolveStoreCode(connection, settings));
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new EficRow
                {
                    EficId = Convert.ToInt64(reader["EFICID"]),
                    StoreId = ReadString(reader, "STOREID"),
                    Code = ReadString(reader, "CODE"),
                    Name = ReadString(reader, "NAME"),
                    ItmsId = reader["ITMSID"] == DBNull.Value ? 0 : Convert.ToInt64(reader["ITMSID"]),
                    ItmvId = reader["ITMVID"] == DBNull.Value ? 0 : Convert.ToInt64(reader["ITMVID"]),
                    SizeName = ReadString(reader, "VNAME"),
                    OrexsysCode = ReadString(reader, "ICODE"),
                    OrexsysName = ReadString(reader, "INAME"),
                    TargetsExtra = reader["ISEXTRA"] != DBNull.Value &&
                                   Convert.ToInt32(reader["ISEXTRA"]) == 1,
                });
            }

            return rows;
        });

        // What a full reset would find in EFIC, one line per STOREID. Other platforms' codes
        // (Wolt / efood - a non-P2O EFST line) are listed only so the dialog can say they stay.
        public enum EficCodeKind { Own, OtherPointToOrder, NoEfstLine, OtherPlatform }

        public sealed record EficCodeSummary(string Code, int Rows, EficCodeKind Kind, string EfstName);

        public static Task<List<EficCodeSummary>> LoadCodeSummaryAsync(Settings settings) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            string own = ResolveStoreCode(connection, settings);

            const string sql = @"
                SELECT TRIM(e.STOREID) AS CODE, COUNT(*) AS N,
                       MAX(s.NAME) AS EFSTNAME, MAX(s.APPTYPE) AS APPTYPE, COUNT(s.EFSTID) AS INEFST
                FROM EFIC e
                LEFT JOIN EFST s ON s.EFSTID = e.STOREID
                GROUP BY TRIM(e.STOREID)
                ORDER BY 2 DESC";

            var list = new List<EficCodeSummary>();
            using var command = new FbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string code = ReadString(reader, "CODE");
                string name = ReadString(reader, "EFSTNAME");
                bool inEfst = Convert.ToInt32(reader["INEFST"]) > 0;
                bool p2o = reader["APPTYPE"] != DBNull.Value && Convert.ToInt32(reader["APPTYPE"]) == 60 ||
                           name.Contains("P2O", StringComparison.OrdinalIgnoreCase);

                var kind = code == own && own.Length > 0 ? EficCodeKind.Own
                         : !inEfst ? EficCodeKind.NoEfstLine
                         : p2o ? EficCodeKind.OtherPointToOrder
                         : EficCodeKind.OtherPlatform;
                list.Add(new EficCodeSummary(code, Convert.ToInt32(reader["N"]), kind, name));
            }
            return list;
        });

        // The reset: removes EVERY row under the given PointToOrder codes. Fenced like every
        // other write, and harder: the own code must resolve (so a database with no P2O setup
        // is refused outright), no code may be empty or another platform's EFST line (Wolt /
        // efood rows can never be reached), the whole table is backed up first, and the rows
        // deleted must equal the rows counted inside the same transaction, or it rolls back.
        public static Task<int> DeleteAllUnderCodesAsync(Settings settings, IReadOnlyCollection<string> codes) =>
            Task.Run(() =>
            {
                if (codes.Count == 0) return 0;

                using var connection = new FbConnection(settings.ToConnectionString());
                connection.Open();

                RequireOwnStoreCode(connection, settings);
                foreach (string code in codes)
                    if (code.Trim().Length == 0 || BelongsToOtherPlatform(connection, code))
                        throw new EficSafetyException(
                            $"Ο κωδικός «{code}» ανήκει σε άλλη πλατφόρμα (π.χ. Wolt / efood) ή είναι κενός. " +
                            "Δεν διαγράφηκε τίποτα.");

                EnsureBackedUp(connection, settings);

                using var transaction = connection.BeginTransaction();
                int expected = 0, deleted = 0;
                foreach (string code in codes)
                {
                    using var count = new FbCommand("SELECT COUNT(*) FROM EFIC WHERE STOREID = @store", connection, transaction);
                    count.Parameters.AddWithValue("@store", code);
                    expected += Convert.ToInt32(count.ExecuteScalar());

                    using var delete = new FbCommand("DELETE FROM EFIC WHERE STOREID = @store", connection, transaction);
                    delete.Parameters.AddWithValue("@store", code);
                    deleted += delete.ExecuteNonQuery();
                }

                if (deleted != expected)
                {
                    transaction.Rollback();
                    throw new EficSafetyException(
                        $"Αναμένονταν {expected} γραμμές, διαγράφηκαν {deleted}. Έγινε αναίρεση — δεν άλλαξε τίποτα.");
                }

                transaction.Commit();
                return deleted;
            });

        // The store codes PointToOrder orders arrived under in the last 30 days (EFOM.STOREID),
        // for OrderFeedCheck. Orders of Wolt / efood / any non-P2O EFST connection are left out,
        // and so are orders whose APPTYPE is another platform's; an order under a code EFST
        // does not know at all IS included - that is exactly the broken case (21/09, Stavros:
        // connector sent PO4897, EFST said PO1110, app wrote «1»). Read-only.
        public static Task<(string OwnCode, List<OrderFeedCode> Arriving)> LoadOrderFeedAsync(Settings settings) =>
            Task.Run(() =>
            {
                using var connection = new FbConnection(settings.ToConnectionString());
                connection.Open();

                string own = ResolveStoreCode(connection, settings);

                const string sql = @"
                    SELECT TRIM(m.STOREID) AS CODE, COUNT(*) AS ORDERS, MAX(m.ORDERDATETIME) AS LASTORDER,
                           MAX((SELECT COUNT(*) FROM EFST s WHERE s.EFSTID = m.STOREID)) AS INEFST,
                           SUM((SELECT COUNT(*) FROM EFOD d
                                 WHERE d.EFOMID = m.EFOMID AND d.ITMSID IS NULL
                                   AND COALESCE(d.CODE, '') NOT IN ('', '0', '-1'))) AS UNMATCHED
                    FROM EFOM m
                    WHERE m.ORDERDATETIME >= DATEADD(-30 DAY TO CURRENT_TIMESTAMP)
                      AND m.STOREID IS NOT NULL
                      AND COALESCE(m.APPTYPE, 60) = 60
                      AND NOT EXISTS (SELECT 1 FROM EFST s
                                       WHERE s.EFSTID = m.STOREID
                                         AND NOT (COALESCE(s.APPTYPE, -1) = 60 OR COALESCE(s.NAME, '') CONTAINING 'P2O'))
                    GROUP BY TRIM(m.STOREID)";

                var arriving = new List<OrderFeedCode>();
                using var command = new FbCommand(sql, connection);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    arriving.Add(new OrderFeedCode(
                        ReadString(reader, "CODE"),
                        Convert.ToInt32(reader["ORDERS"]),
                        reader["UNMATCHED"] == DBNull.Value ? 0 : Convert.ToInt32(reader["UNMATCHED"]),
                        reader["LASTORDER"] == DBNull.Value ? null : Convert.ToDateTime(reader["LASTORDER"]),
                        reader["INEFST"] != DBNull.Value && Convert.ToInt32(reader["INEFST"]) > 0));
                }

                return (own, arriving);
            });

        public static async Task<OrderFeedResult> CheckOrderFeedAsync(Settings settings)
        {
            var (own, arriving) = await LoadOrderFeedAsync(settings);
            return OrderFeedCheck.Evaluate(own, arriving);
        }

        // The P2O store code new EFIC rows are written with. Empty when neither Settings nor
        // EFST names a usable P2O store - the caller then has to ask the operator for it.
        public static Task<string> LoadStoreCodeAsync(Settings settings) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            return ResolveStoreCode(connection, settings);
        });

        // One EFST line = one platform connection in Orexsys (Wolt, efood, PointToOrder...).
        public sealed record EfstConnection(string EfstId, string Name, string Remarks, bool IsPointToOrder);

        // Every connection EFST holds, flagged P2O or not, in Orexsys's own order. Settings
        // offers only the P2O ones; the others are listed by name so the technician can see
        // what the database does have when there is no P2O line yet.
        public static Task<List<EfstConnection>> LoadConnectionsAsync(Settings settings) => Task.Run(() =>
        {
            var connections = new List<EfstConnection>();

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            using var command = new FbCommand(
                "SELECT EFSTID, NAME, REMARKS, " +
                $"CASE WHEN {IsP2ORow} THEN 1 ELSE 0 END AS ISP2O FROM EFST ORDER BY ID", connection);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                connections.Add(new EfstConnection(
                    ReadString(reader, "EFSTID"),
                    ReadString(reader, "NAME"),
                    ReadString(reader, "REMARKS"),
                    Convert.ToInt32(reader["ISP2O"]) == 1));
            }

            return connections;
        });

        // itmvId: the size (ITMV) this platform offer stands for; 0 = the product itself. It is
        // what EFOD_BIUD_TRG copies onto the incoming order line, so the till gets the size.
        public static Task<long> InsertAsync(
            Settings settings, string storeCode, long offerId, string platformName, long itmsId,
            long itmvId = 0) =>
            Task.Run(() =>
            {
                using var connection = new FbConnection(settings.ToConnectionString());
                connection.Open();

                string own = RequireOwnStoreCode(connection, settings);
                if (!string.Equals(storeCode, own, StringComparison.Ordinal))
                    throw new EficSafetyException(
                        $"Ο κωδικός «{storeCode}» δεν είναι ο κωδικός PointToOrder αυτής της " +
                        $"εγκατάστασης («{own}»). Δεν γράφτηκε τίποτα.");

                EnsureBackedUp(connection, settings);

                // Mirrors a row made by Orexsys itself for PointToOrder (owner's reference, 11/09):
                // SALESNAME = NAME = the platform name, ITMVID = the size (0 = none), CORRELATE
                // 1, the rest NULL. SALESNAME is "the name as it arrives on the order" —
                // EFOD_BIUD_TRG and EFIC_AIUD_TRG match on it when an order line comes without
                // a usable code. Both are VARCHAR(200); CODE is the offerId and always fits.
                const string sql = @"
                    INSERT INTO EFIC (STOREID, CODE, NAME, SALESNAME, ITMSID, ITMVID, CORRELATE)
                    VALUES (@storeid, @code, @name, @name, @itmsid, @itmvid, 1)
                    RETURNING EFICID";

                using var command = new FbCommand(sql, connection);
                command.Parameters.AddWithValue("@storeid", own);
                command.Parameters.AddWithValue("@code", offerId.ToString());
                command.Parameters.AddWithValue("@name", Cap(platformName, 200));
                command.Parameters.AddWithValue("@itmsid", itmsId);
                command.Parameters.AddWithValue("@itmvid", itmvId);

                return Convert.ToInt64(command.ExecuteScalar());
            });

        // Points an existing correlation at a different Orexsys product (and size: 0 = the
        // product itself). The AIUD trigger then re-runs its EFOD backfill for this
        // store+code, so pending unmapped order lines pick the correction up automatically.
        public static Task RepairAsync(Settings settings, long eficId, long itmsId, long itmvId = 0) =>
            Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            string own = RequireOwnStoreCode(connection, settings);
            EnsureBackedUp(connection, settings);

            using var command = new FbCommand(
                "UPDATE EFIC SET ITMSID = @itmsid, ITMVID = @itmvid, CORRELATE = 1 " +
                "WHERE EFICID = @id AND STOREID = @store", connection);
            command.Parameters.AddWithValue("@itmsid", itmsId);
            command.Parameters.AddWithValue("@itmvid", itmvId);
            command.Parameters.AddWithValue("@id", eficId);
            command.Parameters.AddWithValue("@store", own);

            if (command.ExecuteNonQuery() != 1)
                throw new EficSafetyException(
                    "Η αντιστοίχιση δεν ανήκει στο PointToOrder αυτής της εγκατάστασης ή δεν " +
                    "υπάρχει πλέον. Δεν άλλαξε τίποτα.");
        });

        // All or nothing: if any id is not one of our rows, the whole delete is rolled back.
        public static Task DeleteAsync(Settings settings, IReadOnlyList<long> eficIds) => Task.Run(() =>
        {
            if (eficIds.Count == 0) return;

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            string own = RequireOwnStoreCode(connection, settings);
            EnsureBackedUp(connection, settings);

            using var transaction = connection.BeginTransaction();
            using var command = new FbCommand(
                "DELETE FROM EFIC WHERE EFICID = @id AND STOREID = @store", connection, transaction);
            var idParameter = command.Parameters.Add("@id", FbDbType.BigInt);
            command.Parameters.AddWithValue("@store", own);

            foreach (long id in eficIds)
            {
                idParameter.Value = id;
                if (command.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    throw new EficSafetyException(
                        $"Η γραμμή EFIC {id} δεν ανήκει στο PointToOrder αυτής της εγκατάστασης " +
                        "ή δεν υπάρχει πλέον. Δεν διαγράφηκε τίποτα.");
                }
            }

            transaction.Commit();
        });

        // Removes the rows of one product whose platform offers no longer exist (its
        // OrphanCodes), just before the product is sent again. Same fences as DeleteAsync:
        // own store code only, STOREID in the WHERE, exactly one row per code or rollback.
        // Returns the number of rows removed.
        public static Task<int> DeleteOrphanLinksAsync(
            Settings settings, long itmsId, IReadOnlyList<string> codes) => Task.Run(() =>
        {
            if (codes.Count == 0) return 0;

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            string own = RequireOwnStoreCode(connection, settings);
            EnsureBackedUp(connection, settings);

            using var transaction = connection.BeginTransaction();
            using var command = new FbCommand(
                "DELETE FROM EFIC WHERE STOREID = @store AND CODE = @code AND ITMSID = @itmsid",
                connection, transaction);
            command.Parameters.AddWithValue("@store", own);
            var codeParameter = command.Parameters.Add("@code", FbDbType.VarChar, 200);
            command.Parameters.AddWithValue("@itmsid", itmsId);

            int removed = 0;
            foreach (string code in codes)
            {
                codeParameter.Value = code;
                int n = command.ExecuteNonQuery();
                if (n != 1)
                {
                    transaction.Rollback();
                    throw new EficSafetyException(
                        $"Η παλιά αντιστοίχιση {code} δεν βρέθηκε μία φορά στο PointToOrder αυτής " +
                        $"της εγκατάστασης ({n} γραμμές). Δεν διαγράφηκε τίποτα.");
                }
                removed += n;
            }

            transaction.Commit();
            return removed;
        });

        // Databases already backed up in this session. Keyed by the database path so a
        // technician switching databases mid-session still gets a backup of the new one.
        private static readonly HashSet<string> BackedUpDatabases =
            new(StringComparer.OrdinalIgnoreCase);

        private static void EnsureBackedUp(FbConnection connection, Settings settings)
        {
            lock (BackedUpDatabases)
            {
                if (BackedUpDatabases.Contains(settings.Database)) return;

                string folder = Path.Combine(AppContext.BaseDirectory, "backups");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder,
                    $"EFIC_{Path.GetFileNameWithoutExtension(settings.Database)}_" +
                    $"{DateTime.Now:yyyyMMdd_HHmmss}.csv");

                var csv = new StringBuilder();

                using (var command = new FbCommand("SELECT * FROM EFIC ORDER BY EFICID", connection))
                using (var reader = command.ExecuteReader())
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        csv.Append(i == 0 ? "" : ";").Append(Quote(reader.GetName(i)));
                    csv.AppendLine();

                    while (reader.Read())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            string value = reader.IsDBNull(i)
                                ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                            csv.Append(i == 0 ? "" : ";").Append(Quote(value));
                        }
                        csv.AppendLine();
                    }
                }

                // UTF-8 with BOM so Excel opens the Greek names correctly.
                File.WriteAllText(file, csv.ToString(), new UTF8Encoding(true));
                BackedUpDatabases.Add(settings.Database);
            }
        }

        // Every field quoted: RAWTEXT carries JSON with ';', quotes and line breaks.
        private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private static string ReadString(IDataRecord record, string column)
        {
            object value = record[column];
            return value == DBNull.Value ? "" : (value.ToString() ?? "").Trim();
        }

        private static string Cap(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max);
    }
}
