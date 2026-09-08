using System;
using System.Collections.Generic;
using System.Data;
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

    // Reads and writes the EFIC correlation table. The write conventions were read off the
    // live triggers rather than guessed:
    //  - EFIC_BIUD_TRG generates EFICID from the efic_sq sequence when null and stamps RECVER,
    //    so inserts send neither and read the id back with RETURNING.
    //  - EFIC_AIUD_TRG backfills EFOD.ITMSID on any still-unmapped order lines with the same
    //    STOREID+CODE whenever a row is inserted or updated - fixing a correlation here
    //    retroactively repairs pending external order lines, courtesy of Orexsys itself.
    //  - EFIC_LD logs every delete through Orexsys's own logdelete audit.
    public static class EficRepository
    {
        public static Task<List<EficRow>> LoadAsync(Settings settings) => Task.Run(() =>
        {
            var rows = new List<EficRow>();

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            const string sql = @"
                SELECT e.EFICID, e.STOREID, e.CODE, e.NAME, e.ITMSID,
                       i.CODE AS ICODE, i.NAME AS INAME, c.ISEXTRA
                FROM EFIC e
                LEFT JOIN ITMS i ON i.ITMSID = e.ITMSID
                LEFT JOIN ITMC c ON c.ITMCID = i.ITMCID
                ORDER BY e.EFICID";

            using var command = new FbCommand(sql, connection);
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
                    OrexsysCode = ReadString(reader, "ICODE"),
                    OrexsysName = ReadString(reader, "INAME"),
                    TargetsExtra = reader["ISEXTRA"] != DBNull.Value &&
                                   Convert.ToInt32(reader["ISEXTRA"]) == 1,
                });
            }

            return rows;
        });

        // The store code EFIC rows are scoped by (EFST.EFSTID, e.g. PO11521). Empty on a fresh
        // installation - the caller then has to ask the operator for it, because nothing else
        // in the database knows it.
        public static Task<string> LoadStoreCodeAsync(Settings settings) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            using var command = new FbCommand(
                "SELECT FIRST 1 EFSTID FROM EFST ORDER BY ID", connection);

            object? result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? "" : (result.ToString() ?? "").Trim();
        });

        public static Task<long> InsertAsync(
            Settings settings, string storeCode, long offerId, string platformName, long itmsId) =>
            Task.Run(() =>
            {
                using var connection = new FbConnection(settings.ToConnectionString());
                connection.Open();

                // ITMVID 0 and CORRELATE 1 mirror every one of the 435 live rows. NAME is capped
                // to its VARCHAR(200); CODE is the offerId and always fits.
                const string sql = @"
                    INSERT INTO EFIC (STOREID, CODE, NAME, ITMSID, ITMVID, CORRELATE)
                    VALUES (@storeid, @code, @name, @itmsid, 0, 1)
                    RETURNING EFICID";

                using var command = new FbCommand(sql, connection);
                command.Parameters.AddWithValue("@storeid", storeCode);
                command.Parameters.AddWithValue("@code", offerId.ToString());
                command.Parameters.AddWithValue("@name", Cap(platformName, 200));
                command.Parameters.AddWithValue("@itmsid", itmsId);

                return Convert.ToInt64(command.ExecuteScalar());
            });

        // Points an existing correlation at a different Orexsys product. The AIUD trigger then
        // re-runs its EFOD backfill for this store+code, so pending unmapped order lines pick
        // the correction up automatically.
        public static Task RepairAsync(Settings settings, long eficId, long itmsId) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            using var command = new FbCommand(
                "UPDATE EFIC SET ITMSID = @itmsid, ITMVID = 0, CORRELATE = 1 WHERE EFICID = @id",
                connection);
            command.Parameters.AddWithValue("@itmsid", itmsId);
            command.Parameters.AddWithValue("@id", eficId);
            command.ExecuteNonQuery();
        });

        public static Task DeleteAsync(Settings settings, IReadOnlyList<long> eficIds) => Task.Run(() =>
        {
            if (eficIds.Count == 0) return;

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            using var transaction = connection.BeginTransaction();
            using var command = new FbCommand(
                "DELETE FROM EFIC WHERE EFICID = @id", connection, transaction);
            var parameter = command.Parameters.Add("@id", FbDbType.BigInt);

            foreach (long id in eficIds)
            {
                parameter.Value = id;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        });

        private static string ReadString(IDataRecord record, string column)
        {
            object value = record[column];
            return value == DBNull.Value ? "" : (value.ToString() ?? "").Trim();
        }

        private static string Cap(string value, int max) =>
            value.Length <= max ? value : value.Substring(0, max);
    }
}
