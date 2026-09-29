using System;
using System.Collections.Generic;
using System.Linq;
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

        // Active products with a price in the chosen column, PER PRICE LIST (PRLSID → count):
        // the sync's own rule, so a category at 0 in its list is skipped whatever is ticked.
        public Dictionary<long, int> PricedByList { get; init; } = new();

        public int PricedIn(long priceListId) => PricedByList.TryGetValue(priceListId, out int n) ? n : 0;

        // The list this category is actually priced in: the one with the most priced products
        // (ties → the lowest id). 0 when it has no price anywhere.
        public long BestPriceList =>
            PricedByList.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key)
                .Select(p => p.Key).FirstOrDefault();

        // Kept for callers that think in one list.
        public int Priced { get; init; }
    }

    public static class OrexsysCategories
    {
        // Real categories (not extras, not the internal SYSTEM row) holding at least one
        // product, in Orexsys's own order, with the priced count in EVERY price list. That is
        // what makes the right list obvious: on STOA.FDB, which serves two businesses, every
        // ΣΤΟΑ category shows 0 under list 1 (ΚΑΛΑΜΑΚΙ) and its real count under list 2 (ΣΤΟΑ).
        public static Task<List<OrexsysCategory>> LoadAsync(Settings settings) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            // The price column comes from a fixed enum, never from user text. ITMVID = 0 is the
            // product's own (size-less) row: PRLD is unique on PRLSID+ITMSID+ITMVID, so without
            // it a product with sizes would be counted once per size.
            var priced = new Dictionary<long, Dictionary<long, int>>();
            using (var command = new FbCommand($@"
                SELECT i.ITMCID, p.PRLSID, COUNT(*) AS PRICED
                FROM ITMS i
                JOIN PRLD p ON p.ITMSID = i.ITMSID AND p.ITMVID = 0
                WHERE i.ISACTIVE = 1 AND p.{settings.PriceColumn} > 0
                GROUP BY i.ITMCID, p.PRLSID", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader["ITMCID"] == DBNull.Value) continue;
                    long category = Convert.ToInt64(reader["ITMCID"]);
                    if (!priced.TryGetValue(category, out var byList)) priced[category] = byList = new Dictionary<long, int>();
                    byList[Convert.ToInt64(reader["PRLSID"])] = Convert.ToInt32(reader["PRICED"]);
                }
            }

            var categories = new List<OrexsysCategory>();
            using (var command = new FbCommand(@"
                SELECT c.ITMCID, c.NAME, COUNT(i.ITMSID) AS TOTAL
                FROM ITMC c
                JOIN ITMS i ON i.ITMCID = c.ITMCID
                WHERE COALESCE(c.ISEXTRA, 0) = 0 AND c.ITMCID <> 0
                GROUP BY c.ITMCID, c.NAME
                ORDER BY c.ITMCID", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    long id = Convert.ToInt64(reader["ITMCID"]);
                    var byList = priced.TryGetValue(id, out var found) ? found : new Dictionary<long, int>();
                    categories.Add(new OrexsysCategory
                    {
                        Id = id,
                        Name = reader["NAME"] == DBNull.Value ? "" : (reader["NAME"].ToString() ?? "").Trim(),
                        Total = Convert.ToInt32(reader["TOTAL"]),
                        PricedByList = byList,
                        Priced = byList.TryGetValue(settings.PriceListFor(id), out int n) ? n : 0,
                    });
                }
            }
            return categories;
        });
    }
}
