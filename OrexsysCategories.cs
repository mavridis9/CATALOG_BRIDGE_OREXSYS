using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // One Orexsys category as the category picker shows it.
    public class OrexsysCategory
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public int Total { get; init; }

        // Active products with a price in the configured list and column - the sync's own
        // rule, so a category at 0 here is skipped by the sync whatever is ticked.
        public int Priced { get; init; }
    }

    public static class OrexsysCategories
    {
        // Real categories (not extras, not the internal SYSTEM row) holding at least one
        // product, in Orexsys's own order. The priced count is what makes a wrong price list
        // obvious: on STOA.FDB, which serves two businesses, every ΣΤΟΑ category shows 0 under
        // list 1 (ΚΑΛΑΜΑΚΙ) because those products are priced only in list 2 (ΣΤΟΑ).
        public static Task<List<OrexsysCategory>> LoadAsync(Settings settings) => Task.Run(() =>
        {
            var categories = new List<OrexsysCategory>();

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            // The price column comes from a fixed enum, never from user text. ITMVID = 0 is the
            // product's own (size-less) row: PRLD is unique on PRLSID+ITMSID+ITMVID, so without
            // it a product with sizes would be counted once per size.
            string sql = $@"
                SELECT c.ITMCID, c.NAME, COUNT(i.ITMSID) AS TOTAL,
                       SUM(CASE WHEN i.ISACTIVE = 1 AND p.{settings.PriceColumn} > 0
                                THEN 1 ELSE 0 END) AS PRICED
                FROM ITMC c
                JOIN ITMS i ON i.ITMCID = c.ITMCID
                LEFT JOIN PRLD p ON p.ITMSID = i.ITMSID AND p.PRLSID = @prlsid AND p.ITMVID = 0
                WHERE COALESCE(c.ISEXTRA, 0) = 0 AND c.ITMCID <> 0
                GROUP BY c.ITMCID, c.NAME
                ORDER BY c.ITMCID";

            using var command = new FbCommand(sql, connection);
            command.Parameters.AddWithValue("@prlsid", settings.PriceListId);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                categories.Add(new OrexsysCategory
                {
                    Id = Convert.ToInt64(reader["ITMCID"]),
                    Name = reader["NAME"] == DBNull.Value ? "" : (reader["NAME"].ToString() ?? "").Trim(),
                    Total = Convert.ToInt32(reader["TOTAL"]),
                    Priced = reader["PRICED"] == DBNull.Value ? 0 : Convert.ToInt32(reader["PRICED"]),
                });
            }

            return categories;
        });
    }
}
