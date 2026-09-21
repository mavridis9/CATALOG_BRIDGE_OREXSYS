using System;
using System.Collections.Generic;
using System.Linq;

namespace CatalogBridge
{
    // One store code that PointToOrder orders arrived under recently (EFOM.STOREID), with how
    // many orders and how many of their lines Orexsys could not match to a product.
    public sealed record OrderFeedCode(string Code, int Orders, int UnmatchedLines, DateTime? LastOrder, bool InEfst = false);

    public sealed class OrderFeedResult
    {
        public string OwnCode { get; init; } = "";
        public List<OrderFeedCode> Foreign { get; init; } = new();

        // Orders from PointToOrder arrive under a code the app does not write its links with,
        // so none of them can match: seen 21/09 on Stavros - the connector sends PO4897, the
        // app wrote EFIC under «1», and the first real order arrived with no product on any line.
        public bool Mismatch => Foreign.Count > 0;

        public string ArrivingCodes => string.Join(", ", Foreign.Select(f => $"«{f.Code}»"));

        public string Message => !Mismatch ? "" :
            $"⛔ Οι παραγγελίες του PointToOrder φτάνουν στο Orexsys με κωδικό {ArrivingCodes}, " +
            (OwnCode.Length > 0
                ? $"αλλά η «Σύνδεση P2O» στις Ρυθμίσεις είναι «{OwnCode}». "
                : "αλλά δεν έχει επιλεγεί «Σύνδεση P2O» στις Ρυθμίσεις. ") +
            "Οι αντιστοιχίσεις γράφονται με αυτόν τον κωδικό, οπότε οι παραγγελίες ΔΕΝ ταιριάζουν με τα " +
            $"προϊόντα ({Foreign.Sum(f => f.UnmatchedLines)} γραμμές χωρίς προϊόν τις τελευταίες 30 μέρες). " +
            (Foreign.All(f => f.InEfst)
                ? $"Επιλέξτε στις Ρυθμίσεις τη «Σύνδεση P2O» {ArrivingCodes}. "
                : "Διορθώστε στο Orexsys τη γραμμή PointToOrder του EFST ώστε ο κωδικός της να είναι " +
                  $"{ArrivingCodes} και επιλέξτε την στις Ρυθμίσεις. ") +
            "Μέχρι τότε δεν στέλνεται τίποτα.";
    }

    public static class OrderFeedCheck
    {
        // Every recent P2O order code that is not the app's own. The rows come from
        // EficRepository.LoadOrderFeedAsync, which already leaves out Wolt / efood / any other
        // non-P2O EFST connection, so a database with only other platforms never alarms.
        public static OrderFeedResult Evaluate(string ownCode, IEnumerable<OrderFeedCode> arriving)
        {
            string own = (ownCode ?? "").Trim();
            return new OrderFeedResult
            {
                OwnCode = own,
                Foreign = arriving
                    .Where(a => a.Code.Trim().Length > 0 &&
                                !string.Equals(a.Code.Trim(), own, StringComparison.Ordinal))
                    .OrderByDescending(a => a.LastOrder)
                    .ToList(),
            };
        }
    }
}
