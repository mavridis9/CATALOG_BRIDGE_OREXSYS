using System;

namespace CatalogBridge
{
    // Shared by the create-customer form and the per-customer sync prompt, so both enforce the
    // same rule: PointToOrder needs a real email, and it has to fit Orexsys's CUST.EMAIL column.
    public static class EmailValidator
    {
        // CUST.EMAIL is VARCHAR(50).
        public const int MaxLength = 50;

        // Returns null when the address is acceptable, otherwise a Greek message to show the user.
        public static string? Validate(string? email)
        {
            string value = (email ?? "").Trim();

            if (value.Length == 0)
                return "Παρακαλώ εισάγετε το email του πελάτη.";

            if (value.Length > MaxLength)
                return $"Το email πρέπει να έχει έως {MaxLength} χαρακτήρες.";

            if (!IsWellFormed(value))
                return "Το email δεν είναι έγκυρο.";

            return null;
        }

        // MailAddress does the parsing; the round-trip comparison rejects inputs it would
        // otherwise accept loosely (e.g. a display name wrapped around an address).
        private static bool IsWellFormed(string value)
        {
            try
            {
                var parsed = new System.Net.Mail.MailAddress(value);
                return string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase)
                    && value.IndexOf('@') > 0
                    && value.LastIndexOf('.') > value.IndexOf('@');
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
