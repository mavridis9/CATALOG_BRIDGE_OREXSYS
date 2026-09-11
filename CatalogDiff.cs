using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CatalogBridge
{
    public enum DiffStatus
    {
        Identical,
        ActiveMismatch,

        // Active in the POS but not sellable/visible online - possibly intentional (not
        // everything belongs on the web menu), possibly a pushed product nobody ever made
        // visible. Informational, so it gets its own bucket instead of hiding in "identical".
        HiddenOnline,

        OrexsysOnly,
        PlatformOnly,
    }

    // How a row's two sides were tied together, so the operator can tell a solid mapping from a
    // guess. A name match is a guess: it is shown, but it is never treated as authoritative.
    public enum MatchKind { None, ByCode, ByName }

    public class DiffRow
    {
        public OrexsysProduct? Orexsys { get; set; }
        public CatalogOffer? Offer { get; set; }
        public DiffStatus Status { get; set; }
        public MatchKind MatchedBy { get; set; }

        public string Category =>
            Orexsys?.CategoryName is { Length: > 0 } c ? c : (Offer?.CategoryName ?? "");

        // The size this row is about, when the pair is one size of a product sent as separate
        // items («ΤΟΣΤ ΖΑΜΠΟΝ ΚΑΣΕΡΙ (XL)»); EFIC.ITMVID via the matched code.
        public string SizeName =>
            Orexsys == null || Offer == null || MatchedBy != MatchKind.ByCode ? ""
            : Orexsys.SizeName(Orexsys.SizeOfCode(Offer.OfferId.ToString()));

        public string OrexsysName =>
            Orexsys == null ? "" : SizeName.Length > 0 ? $"{Orexsys.Name} ({SizeName})" : Orexsys.Name;
        public string PlatformName => Offer?.Name ?? "";
        public string OrexsysCode => Orexsys?.Code ?? "";

        public string OrexsysPriceText => Orexsys?.Price is decimal p ? p.ToString("0.00") : "—";
        public string PlatformPriceText => Offer != null ? Offer.Price.ToString("0.00") : "—";

        public string PlatformStateText
        {
            get
            {
                if (Offer == null) return "—";
                string state = Offer.ItemType switch
                {
                    OfferItemType.Orderable => "Ενεργό",
                    OfferItemType.Inactive => "Ανενεργό",
                    "" => "—",
                    _ => Offer.ItemType,
                };
                return Offer.Sellable ? state : state + " · κρυφό";
            }
        }

        public string OrexsysStateText =>
            Orexsys == null ? "—" : (Orexsys.IsActive ? "Ενεργό" : "Ανενεργό");

        // True when the two sides were tied together by code but their names share nothing at
        // all. Seen live: EFIC mapped «ΚΡΙΤΣΙ ΝΤΟΜΑΤ ΘΥΜ» onto the platform's «Γαλοπούλα τυρί
        // wrap». The EFIC row is stale, not the price - so the pairing is worth doubting before
        // anyone edits a price on the strength of it.
        public string MatchedByText => MatchedBy switch
        {
            MatchKind.ByCode => "κωδικό",
            MatchKind.ByName => "όνομα (?)",
            _ => "",
        };

        public string StatusText => Status switch
        {
            DiffStatus.Identical => "✓ Συμφωνούν",
            DiffStatus.ActiveMismatch => "⚠ Διαφορά κατάστασης",
            DiffStatus.HiddenOnline => "💤 Κρυφό online",
            DiffStatus.OrexsysOnly => "← Μόνο στο Orexsys",
            DiffStatus.PlatformOnly => "→ Μόνο στο PointToOrder",
            _ => "",
        };
    }

    public class DiffResult
    {
        public List<DiffRow> Rows { get; } = new();
        public long CatalogId { get; set; }

        public int CountOf(DiffStatus status) => Rows.Count(r => r.Status == status);
        public int MatchedByCode => Rows.Count(r => r.MatchedBy == MatchKind.ByCode);
        public int MatchedByName => Rows.Count(r => r.MatchedBy == MatchKind.ByName);
    }

    public static class CatalogDiff
    {
        public static DiffResult Build(
            OrexsysCatalogSnapshot orexsys, CatalogSnapshot platform, Settings settings)
        {
            var result = new DiffResult { CatalogId = platform.CatalogId };

            var unmatchedOffers = new List<CatalogOffer>(platform.Offers);
            var takenOffers = new HashSet<CatalogOffer>();

            // Pass 1 - match on codes. EFIC.CODE against every code-like field the offer
            // carries (its own id included). This is the only mapping treated as reliable.
            var offersByCode = new Dictionary<string, CatalogOffer>(StringComparer.OrdinalIgnoreCase);
            foreach (var offer in platform.Offers)
                foreach (string candidate in offer.CodeCandidates)
                    if (!offersByCode.ContainsKey(candidate))
                        offersByCode[candidate] = offer;

            var pairs = new List<(OrexsysProduct Product, CatalogOffer? Offer, MatchKind Kind)>();

            foreach (var product in orexsys.Products)
            {
                // Extras (ITMC.ISEXTRA=1) are not standalone products and never participate in
                // matching - zero of the 435 genuine EFIC rows point at one. The single
                // exception: an extra that somehow HAS an EFIC code is a data anomaly, and
                // showing it beats hiding it.
                if (product.IsExtra && product.ExternalCodes.Count == 0)
                    continue;

                // A product sent as separate items per size has several codes, one per size
                // (ITMVID in EFIC): every one of them that exists online is its own row, so a
                // size switched off at the till is switched off online too, not just the
                // first one found.
                bool matched = false;

                foreach (string code in product.ExternalCodes)
                {
                    if (offersByCode.TryGetValue(code, out var candidate) && !takenOffers.Contains(candidate))
                    {
                        takenOffers.Add(candidate);
                        pairs.Add((product, candidate, MatchKind.ByCode));
                        matched = true;
                    }
                }

                if (!matched)
                    pairs.Add((product, null, MatchKind.None));
            }

            // Pass 2 - fall back to normalised names for whatever is left over. Greek names are
            // compared without accents and without case, since the platform and the POS spell
            // the same product differently ("Espresso μονός" vs "ESPRESSO ΜΟΝΟΣ").
            var offersByName = new Dictionary<string, List<CatalogOffer>>();
            foreach (var offer in platform.Offers)
            {
                if (takenOffers.Contains(offer)) continue;

                string key = Normalise(offer.Name);
                if (key.Length == 0) continue;

                if (!offersByName.TryGetValue(key, out var list))
                    offersByName[key] = list = new List<CatalogOffer>();
                list.Add(offer);
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Offer != null) continue;

                // Extras never match by name either - a platform product sharing an extra's
                // name ("ΓΑΛΑ ΑΜΥΓΔΑΛΟΥ") is a different thing, not the same product.
                if (pairs[i].Product.IsExtra) continue;

                string key = Normalise(pairs[i].Product.Name);
                if (key.Length == 0) continue;

                // An ambiguous name (two platform products spelled the same) is left unmatched
                // rather than paired arbitrarily - a wrong pairing would show a bogus price
                // difference and invite someone to "fix" a price that was never wrong.
                if (!offersByName.TryGetValue(key, out var candidates) || candidates.Count != 1)
                    continue;

                var offer = candidates[0];
                if (takenOffers.Contains(offer)) continue;

                takenOffers.Add(offer);
                pairs[i] = (pairs[i].Product, offer, MatchKind.ByName);
            }

            foreach (var (product, offer, kind) in pairs)
            {
                result.Rows.Add(new DiffRow
                {
                    Orexsys = product,
                    Offer = offer,
                    MatchedBy = kind,
                    Status = ClassifyMatched(product, offer, settings),
                });
            }

            foreach (var offer in unmatchedOffers)
            {
                if (takenOffers.Contains(offer)) continue;

                result.Rows.Add(new DiffRow
                {
                    Offer = offer,
                    MatchedBy = MatchKind.None,
                    Status = DiffStatus.PlatformOnly,
                });
            }

            return result;
        }

        // The IDs are the truth (user decision 2026-09-01): ITMSID ↔ offerId via EFIC.CODE.
        // A code match is authoritative regardless of what either side calls the product -
        // names on PointToOrder are deliberately different from the POS names.
        private static DiffStatus ClassifyMatched(
            OrexsysProduct product, CatalogOffer? offer, Settings settings)
        {
            if (offer == null) return DiffStatus.OrexsysOnly;

            // An item Orexsys has switched off but the platform still sells is the one mismatch
            // that loses money rather than just looking untidy, so it outranks a price gap.
            bool platformActive = string.Equals(
                offer.ItemType, OfferItemType.Orderable, StringComparison.OrdinalIgnoreCase);

            if (!product.IsActive && platformActive)
                return DiffStatus.ActiveMismatch;

            // The reverse direction: sellable in the store but not on the web menu - either a
            // deliberate choice or a pushed product nobody ever made visible.
            if (product.IsActive && (!platformActive || !offer.Sellable))
                return DiffStatus.HiddenOnline;

            // Prices are deliberately NOT compared (user decision 2026-09-01): when an order
            // is placed, the PointToOrder prices travel with it into Orexsys, so the
            // platform's price IS the online price. Drift is a pricing choice, not an error.
            return DiffStatus.Identical;
        }

        // Uppercases and strips accents so Greek names compare sensibly across the two systems.
        // Final sigma is folded onto sigma, which .NET's invariant uppercase does not do.
        public static string Normalise(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                builder.Append(c == 'ς' ? 'σ' : c);
            }

            string stripped = builder.ToString()
                .Normalize(NormalizationForm.FormC)
                .ToUpperInvariant();

            // Collapse internal whitespace so "ESPRESSO  ΜΟΝΟΣ" matches "Espresso μονός".
            return string.Join(" ", stripped.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
