using System;
using System.Linq;

namespace CatalogBridge
{
    // Decides what phone number to send to PointToOrder for a customer.
    //
    // The platform validates the number as a Greek mobile - 69 followed by 8 digits, 10 in
    // total - and rejects anything else. The card code used to be sent here, which passed
    // customer creation but then made the platform refuse the customer's ORDER later with a
    // "wrong phone number" error, long after the sync looked successful.
    //
    // A real mobile already held in Orexsys is sent as-is. Otherwise a stand-in is built from
    // the Orexsys CUSTID, which keeps it unique per customer - two customers must never share a
    // number, or the platform would treat them as the same person.
    public static class PointToOrderPhone
    {
        private const string MobilePrefix = "69";
        private const int MobileLength = 10;

        // How many digits are left for the customer id after the "69" prefix.
        private const int SuffixDigits = MobileLength - 2;
        private const long SuffixWrap = 100_000_000L;   // 8 digits

        // Accepts what Orexsys actually holds: spaces, dashes and a +30 / 0030 country prefix
        // are all common in hand-entered numbers and none of them make the number invalid.
        public static string? NormalizeMobile(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;

            string digits = new string(phone.Where(char.IsDigit).ToArray());

            // Strip a Greek country code however it was written: "+30 695...", "0030695..." and
            // "30695..." all reduce to the same national number. The 00 goes first so both
            // international forms end up in the same shape.
            if (digits.Length == MobileLength + 4 && digits.StartsWith("0030", StringComparison.Ordinal))
                digits = digits.Substring(4);
            else if (digits.Length == MobileLength + 2 && digits.StartsWith("30", StringComparison.Ordinal))
                digits = digits.Substring(2);

            return digits.Length == MobileLength && digits.StartsWith(MobilePrefix, StringComparison.Ordinal)
                ? digits
                : null;
        }

        public static bool IsRealMobile(string? phone) => NormalizeMobile(phone) != null;

        // The stand-in number for a customer with no usable mobile. Unique per CUSTID.
        public static string ForCustomer(long custId)
        {
            // Ids far beyond the 8 digits available would silently collide once wrapped, so the
            // wrap is explicit rather than accidental. Current databases are in the hundreds, so
            // this is headroom, not a live concern.
            long suffix = Math.Abs(custId) % SuffixWrap;
            return MobilePrefix + suffix.ToString(new string('0', SuffixDigits));
        }

        // What to actually send: the customer's real mobile when they have one, otherwise the
        // CUSTID-based stand-in.
        public static string Resolve(long custId, string? existingPhone) =>
            NormalizeMobile(existingPhone) ?? ForCustomer(custId);
    }
}
