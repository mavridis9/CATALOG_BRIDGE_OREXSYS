using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // One product as Orexsys holds it, with the price already resolved out of the configured
    // price list, and whatever external-catalog codes EFIC maps to it.
    public class OrexsysProduct
    {
        public long ItmsId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public long CategoryId { get; set; }
        public string CategoryName { get; set; } = "";
        public bool IsActive { get; set; }

        // ITMC.ISEXTRA = 1 marks the product's category as an extras/modifiers category (the
        // «ΕΧ_» ones - almond milk, γύρος toppings...). Those are not standalone products: they
        // ride on another item's order line. 216 of LIGHTHOUSE2's 703 items are extras, and
        // zero of the 435 genuine EFIC correlations point at one - so extras are excluded from
        // matching, from the push list, and from the correlation pickers.
        public bool IsExtra { get; set; }

        // Null when the item has no row in the configured price list at all, which is a
        // different thing from a row priced 0.00 - the diff reports them differently.
        public decimal? Price { get; set; }

        // EFIC.CODE values mapped to this item. Normally one; a handful of items carry two
        // (size variants sold as separate platform products), so this is a list rather than a
        // single value and the diff refuses to guess when there is more than one.
        public List<string> ExternalCodes { get; } = new();

        // Which size each code stands for (EFIC.ITMVID; 0 = the product itself). A code not
        // listed here counts as size 0 - that is every row written before size support.
        public Dictionary<string, long> LinkSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long SizeOfCode(string code) => LinkSizes.TryGetValue(code, out long id) ? id : 0;

        public bool IsSizeLinked(long itmvId)
        {
            foreach (string code in ExternalCodes)
                if (SizeOfCode(code) == itmvId) return true;
            return false;
        }

        // «XL» for a size, «Κανονικό» for size 0 on a product that has sizes, "" otherwise.
        public string SizeName(long itmvId)
        {
            if (itmvId == 0) return HasSizes ? "Κανονικό" : "";
            foreach (var size in Sizes)
                if (size.ItmvId == itmvId) return size.Name;
            return $"μέγεθος {itmvId}";
        }

        // Every item has a PRLD row on the live databases, but only 408 of 703 carry a non-zero
        // price - the rest are extras and modifiers that Orexsys leaves at 0.00. Treating 0.00
        // as "no price set" is what stops the diff reporting a false price difference for every
        // one of them against whatever the platform charges.
        public bool HasPrice => Price.HasValue && Price.Value > 0m;

        // The sizes the till offers for this product (ITMV), in Orexsys's own order. Empty for
        // a product without sizes - which, as of 11/09/2026, is every product on every
        // database we have seen.
        public List<OrexsysSize> Sizes { get; } = new();

        public bool HasSizes => Sizes.Count > 0;

        // «0,5L 3,00 · 1L 5,00» - for grids and messages. A size with no price in the
        // configured list shows «—».
        public string SizesText => string.Join(" · ", Sizes.Select(s =>
            s.Price is decimal p && p > 0m ? $"{s.Name} {p.ToString("0.00", Greek)}" : $"{s.Name} —"));

        private static readonly System.Globalization.CultureInfo Greek = new("el-GR");
    }

    // One size of a product: an active PRLD row with ITMVID <> 0. ITMV itself is one global
    // list shared by all products (Κανονικό, ΔΙΠΛΟ, 0,5L, ΜΙΣΗ ΜΕΡΙΔΑ...); which of them a
    // product has is decided by its PRLD rows, exactly as Orexsys's GETITMSIZES does.
    public sealed record OrexsysSize(long ItmvId, string Name, decimal? Price);

    public class OrexsysCatalogSnapshot
    {
        public List<OrexsysProduct> Products { get; } = new();
        public string PriceListName { get; set; } = "";
        public int MappedCount { get; set; }
    }

    // Reads the Orexsys product master. Everything here was checked against the live
    // LIGHTHOUSE2 / LIGHTHOUSE / Stavros databases rather than taken from column names:
    //
    //  - ITMS has NO price column. Prices live in PRLD (price-list detail), keyed by
    //    PRLSID + ITMSID, with three separate columns: RESTPRICE, DELIVERYPRICE, TAKEAWAYPRICE.
    //    ITML looks like the natural price table and is empty on all three databases.
    //  - PRLD is unique on PRLSID + ITMSID + ITMVID (PRLD_UNQ_IDX; ITMVID is NOT NULL, default
    //    0). ITMVID 0 is the product's own price; every other ITMVID is a size with its own
    //    price. The join below therefore pins ITMVID = 0 - without it a product with sizes
    //    would be read (and pushed) once per size. Sizes are read separately.
    //  - ITMS.USEINWEBORDER is 1 on every row on all three databases, so it is a default and
    //    NOT a usable "should this be on the web menu" filter. Don't be tempted.
    //  - EFIC.CODE holds the external platform's item code (e.g. 25348 for ITMSID 224). EFIC's
    //    EXTID and EXTCODE columns are null on every row, so CODE is the only mapping key.
    public static class OrexsysCatalog
    {
        public static Task<OrexsysCatalogSnapshot> LoadAsync(Settings settings) =>
            Task.Run(() => Load(settings));

        private static OrexsysCatalogSnapshot Load(Settings settings)
        {
            var snapshot = new OrexsysCatalogSnapshot();
            var byItmsId = new Dictionary<long, OrexsysProduct>();

            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();

            // The price column is chosen from a fixed enum, never from user text, so it cannot
            // carry SQL through this interpolation.
            string priceColumn = settings.PriceColumn;

            string sql = $@"
                SELECT i.ITMSID, i.CODE, i.NAME, i.ISACTIVE, i.ITMCID, c.NAME AS CATNAME,
                       c.ISEXTRA, p.{priceColumn} AS PRICE
                FROM ITMS i
                LEFT JOIN ITMC c ON c.ITMCID = i.ITMCID
                LEFT JOIN PRLD p ON p.ITMSID = i.ITMSID AND p.PRLSID = @prlsid AND p.ITMVID = 0
                ORDER BY c.NAME, i.NAME";

            using (var command = new FbCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@prlsid", settings.PriceListId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var product = new OrexsysProduct
                    {
                        ItmsId = Convert.ToInt64(reader["ITMSID"]),
                        Code = ReadString(reader, "CODE"),
                        Name = ReadString(reader, "NAME"),
                        CategoryId = reader["ITMCID"] == DBNull.Value
                            ? 0 : Convert.ToInt64(reader["ITMCID"]),
                        CategoryName = ReadString(reader, "CATNAME"),
                        IsActive = ReadBool(reader, "ISACTIVE"),
                        IsExtra = ReadBool(reader, "ISEXTRA"),
                        Price = reader["PRICE"] == DBNull.Value
                            ? null : Convert.ToDecimal(reader["PRICE"]),
                    };

                    snapshot.Products.Add(product);
                    byItmsId[product.ItmsId] = product;
                }
            }

            // Sizes, the way GETITMSIZES decides them: any active PRLD row with ITMVID <> 0, in
            // ANY price list - the till asks for a size whichever list is in use. The price shown
            // is the configured list's, and only if that row is active there.
            string sizesSql = $@"
                SELECT s.ITMSID, s.ITMVID, v.NAME AS SIZENAME,
                       (SELECT q.{priceColumn} FROM PRLD q
                         WHERE q.PRLSID = @prlsid AND q.ITMSID = s.ITMSID
                           AND q.ITMVID = s.ITMVID AND q.ISACTIVE = 1) AS PRICE
                FROM (SELECT DISTINCT ITMSID, ITMVID FROM PRLD
                       WHERE ITMVID <> 0 AND ISACTIVE = 1) s
                LEFT JOIN ITMV v ON v.ITMVID = s.ITMVID
                ORDER BY s.ITMSID, v.ORDERING, s.ITMVID";

            using (var command = new FbCommand(sizesSql, connection))
            {
                command.Parameters.AddWithValue("@prlsid", settings.PriceListId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (!byItmsId.TryGetValue(Convert.ToInt64(reader["ITMSID"]), out var product))
                        continue;

                    long itmvId = Convert.ToInt64(reader["ITMVID"]);
                    string name = ReadString(reader, "SIZENAME");
                    product.Sizes.Add(new OrexsysSize(
                        itmvId,
                        name.Length > 0 ? name : $"μέγεθος {itmvId}",
                        reader["PRICE"] == DBNull.Value ? null : Convert.ToDecimal(reader["PRICE"])));
                }
            }

            // Only this store's links count: another store's or another platform's link (Wolt,
            // efood) must not make a product look "already sent" here. An empty code matches
            // nothing - see EficRepository.ResolveStoreCode.
            using (var command = new FbCommand(
                "SELECT ITMSID, CODE, ITMVID FROM EFIC WHERE CODE IS NOT NULL AND STOREID = @store",
                connection))
            {
                command.Parameters.AddWithValue("@store",
                    EficRepository.ResolveStoreCode(connection, settings));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader["ITMSID"] == DBNull.Value) continue;

                    long itmsId = Convert.ToInt64(reader["ITMSID"]);
                    string code = ReadString(reader, "CODE");
                    long itmvId = reader["ITMVID"] == DBNull.Value ? 0 : Convert.ToInt64(reader["ITMVID"]);

                    if (code.Length > 0 && byItmsId.TryGetValue(itmsId, out var product))
                    {
                        product.ExternalCodes.Add(code);
                        product.LinkSizes[code] = itmvId;
                    }
                }
            }

            foreach (var product in snapshot.Products)
                if (product.ExternalCodes.Count > 0) snapshot.MappedCount++;

            snapshot.PriceListName = ReadPriceListName(connection, settings.PriceListId);

            return snapshot;
        }

        private static string ReadPriceListName(FbConnection connection, long priceListId)
        {
            using var command = new FbCommand("SELECT NAME FROM PRLS WHERE PRLSID = @id", connection);
            command.Parameters.AddWithValue("@id", priceListId);

            object? result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? "" : (result.ToString() ?? "").Trim();
        }

        // Lists the price lists available, so Settings can offer them instead of asking staff to
        // type a PRLSID.
        public static Task<List<(long Id, string Name)>> LoadPriceListsAsync(Settings settings) =>
            Task.Run(() =>
            {
                var lists = new List<(long, string)>();

                using var connection = new FbConnection(settings.ToConnectionString());
                connection.Open();

                using var command = new FbCommand(
                    "SELECT PRLSID, NAME FROM PRLS ORDER BY PRLSID", connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                    lists.Add((Convert.ToInt64(reader["PRLSID"]), ReadString(reader, "NAME")));

                return lists;
            });

        // The till's remaining-quantity counters: SCRB holds one row per stock-tracked item
        // per POS screen button (SCRN is the screen-layout table). Items without a row are
        // not stock-tracked. MIN across duplicate rows so the platform never oversells.
        public static Task<Dictionary<long, int>> LoadRemainingStockAsync(Settings settings) =>
            Task.Run(() =>
            {
                var stock = new Dictionary<long, int>();

                using var connection = new FbConnection(settings.ToConnectionString());
                connection.Open();

                using var command = new FbCommand(
                    "SELECT ITMSID, MIN(REMAIN) AS REMAIN FROM SCRB " +
                    "WHERE ITMSID IS NOT NULL AND REMAIN IS NOT NULL GROUP BY ITMSID",
                    connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    long itmsId = Convert.ToInt64(reader["ITMSID"]);
                    int remain = Convert.ToInt32(Math.Floor(Convert.ToDecimal(reader["REMAIN"])));
                    stock[itmsId] = Math.Max(0, remain);
                }

                return stock;
            });

        private static string ReadString(IDataRecord record, string column)
        {
            object value = record[column];
            return value == DBNull.Value ? "" : (value.ToString() ?? "").Trim();
        }

        private static bool ReadBool(IDataRecord record, string column)
        {
            object value = record[column];
            return value != DBNull.Value && Convert.ToInt32(value) == 1;
        }
    }
}
