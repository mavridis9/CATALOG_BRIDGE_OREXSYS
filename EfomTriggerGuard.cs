using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // Keeps the e-mail correlation patch alive inside Orexsys's EFOM_BIUD_TRG trigger.
    //
    // EFOM holds orders arriving from the online ordering platform, and that trigger decides
    // which existing CUST row an incoming order attaches to. The vendor's stock version matches
    // on phone number via CUSP; we match on the customerEmail carried in the order's JSON
    // payload. CatalogBridge applies the e-mail-ONLY variant (V2, see BuildLookupEmailOnly): with an
    // e-mail the phone is never used; only an order without one keeps the vendor phone lookup.
    //
    // A vendor update overwrites the trigger and silently restores phone-only matching, which is
    // invisible until customers stop being recognised. This runs once at startup, notices the
    // revert and re-applies the patch.
    //
    // Crucially it re-applies by TRANSFORMING whatever source is live, never by restoring a
    // stored copy: a vendor update may carry new fields, new logic or bug fixes, and overwriting
    // those with our old copy could break order intake outright.
    //
    // Ported verbatim from ΔΙΑΧΕΙΡΙΣΗ (CustomerAdder, repo mavridis9/LIGHTHOUSE_TOOL) on
    // 21/09/2026 - same marker, same edits - so either program recognises the other's patch.
    // CatalogBridge adds: an on/off setting (Settings.EmailOrderCorrelation), a read-only state
    // check for Ρυθμίσεις, and an explicit Remove that reverses the five edits. «Off» never
    // removes anything by itself: at LIGHTHOUSE ΔΙΑΧΕΙΡΙΣΗ keeps the patch on, and the two
    // programs must not undo each other at every start.
    public static class EfomTriggerGuard
    {
        public const string Marker = "OREXSYS-EMAIL-CORRELATION-V1";
        private const string TriggerName = "EFOM_BIUD_TRG";

        // An abandoned early test. Its presence after patching means something went wrong.
        private const string AbandonedTest = "where phone = new.storeid";

        public enum Outcome { AlreadyPatched, Patched, NeedsManualReview, Failed, Removed, NothingToRemove }

        public class Result
        {
            public Outcome Outcome { get; init; }
            public string Message { get; init; } = "";
            public string? BackupPath { get; init; }
            public bool IsProblem => Outcome is Outcome.NeedsManualReview or Outcome.Failed;
        }

        // ---- anchors -------------------------------------------------------------------
        //
        // Matched with runs of whitespace treated as equivalent, because the live trigger is
        // formatted with blank lines the vendor's own listing does not have, and a byte-exact
        // comparison would fail on formatting alone. Structure and token order still have to
        // match exactly, and a match is only ever used when it occurs EXACTLY once - so this
        // stays unambiguous rather than becoming a fuzzy guess.

        private const string AnchorDeclare =
            "declare variable d_string type of column efod.options;";

        private const string AnchorLookup = @"
         f_custid = null;
         --try to get customer by exact match on phone number
         select first 1 custid
           from cusp
          where (phone = new.mobile) or
                (phone = new.landline)
            into :f_custid;
         --try to get customer by like phone number
         if (:f_custid is null) then
          select first 1 custid
            from cusp
           where (phone like '%' || new.mobile || '%') or
                 (phone like '%' || new.landline || '%')
             into :f_custid;";

        private const string AnchorCardno = @"
           if (new.cardno is not null) then
            update cust
               set cardno = new.cardno
             where custid = :f_custid;";

        // The customer-creation column list and its matching values list. Both are edited so the
        // new e-mail column and value stay aligned.
        private const string AnchorInsertColumns = "phone,\n                cardno,";
        private const string AnchorInsertValues =
            "coalesce(new.mobile, new.landline),\n               new.cardno,";

        // ---- replacements --------------------------------------------------------------

        private const string ReplDeclare = AnchorDeclare + @"
/* " + Marker + @" */
declare variable f_email varchar(100);
declare variable f_work varchar(400);
declare variable f_jsontxt varchar(4000);
declare variable f_pos integer;";

        // The JSON slicing goes through f_work deliberately. Assigning a 400-char substring
        // straight into the 100-char f_email raises string right truncation, which the
        // "when any do" then swallows - leaving the patch silently doing nothing. That bug has
        // already happened once here; do not collapse these steps.
        private static string BuildLookupReplacement(int emailColumnLength) => @"
         f_custid = null;
         /* " + Marker + @" */
         f_email = null;
         begin
           f_jsontxt = substring(cast(new.json as varchar(4000)) from 1 for 4000);
           f_pos = position('""customerEmail""', f_jsontxt);
           if (f_pos > 0) then
            begin
              f_work = substring(f_jsontxt from f_pos + 15 for 400);
              f_pos = position('""', f_work);
              if (f_pos > 0) then
               begin
                 f_work = substring(f_work from f_pos + 1 for 400);
                 f_pos = position('""', f_work);
                 if (f_pos > 1) then
                   f_work = substring(f_work from 1 for f_pos - 1);
                 else
                   f_work = '';
                 f_email = substring(f_work from 1 for 100);
               end
            end
           when any do
             f_email = null;
         end
         if (trim(coalesce(f_email, '')) = '') then f_email = null;

         if (f_email is not null) then
           select first 1 custid
             from cust
            where upper(trim(email)) = upper(trim(:f_email))
             into :f_custid;

         if (:f_custid is null) then
          begin
           --try to get customer by exact match on phone number
           select first 1 custid
             from cusp
            where (phone = new.mobile) or
                  (phone = new.landline)
             into :f_custid;
           --try to get customer by like phone number
           if (:f_custid is null) then
            select first 1 custid
              from cusp
             where (phone like '%' || new.mobile || '%') or
                   (phone like '%' || new.landline || '%')
               into :f_custid;
          end";

        // ---- e-mail only (CatalogBridge, owner decision 21/09/2026) ------------------------
        //
        // Customer login is mandatory on PointToOrder, so every order carries customerEmail.
        // With an e-mail the phone is NOT consulted at all: no match means Orexsys creates a
        // new customer (edit C2 stores the e-mail on it), and the next order finds that one.
        // Without an e-mail (cannot happen with login mandatory) the vendor's phone lookup is
        // kept, so a stray guest order does not create a nameless customer every time.
        //
        // The V1 marker stays in the text as well, so ΔΙΑΧΕΙΡΙΣΗ - which only knows V1 - sees
        // the trigger as already patched and leaves it alone. A trigger carrying V1 only (the
        // e-mail-then-phone patch) is upgraded in place by swapping this one block.
        public const string MarkerEmailOnly = "OREXSYS-EMAIL-ONLY-V2";

        private static string BuildLookupEmailOnly(int emailColumnLength) => @"
         f_custid = null;
         /* " + Marker + @" */
         /* " + MarkerEmailOnly + @": an order with an e-mail is matched by e-mail only, never by phone */
         f_email = null;
         begin
           f_jsontxt = substring(cast(new.json as varchar(4000)) from 1 for 4000);
           f_pos = position('""customerEmail""', f_jsontxt);
           if (f_pos > 0) then
            begin
              f_work = substring(f_jsontxt from f_pos + 15 for 400);
              f_pos = position('""', f_work);
              if (f_pos > 0) then
               begin
                 f_work = substring(f_work from f_pos + 1 for 400);
                 f_pos = position('""', f_work);
                 if (f_pos > 1) then
                   f_work = substring(f_work from 1 for f_pos - 1);
                 else
                   f_work = '';
                 f_email = substring(f_work from 1 for 100);
               end
            end
           when any do
             f_email = null;
         end
         if (trim(coalesce(f_email, '')) = '') then f_email = null;

         if (f_email is not null) then
           select first 1 custid
             from cust
            where upper(trim(email)) = upper(trim(:f_email))
             into :f_custid;
         else
          begin
           --try to get customer by exact match on phone number
           select first 1 custid
             from cusp
            where (phone = new.mobile) or
                  (phone = new.landline)
             into :f_custid;
           --try to get customer by like phone number
           if (:f_custid is null) then
            select first 1 custid
              from cusp
             where (phone like '%' || new.mobile || '%') or
                   (phone like '%' || new.landline || '%')
               into :f_custid;
          end";

        // V1 (e-mail, then phone) → e-mail only: the one block swapped, exactly once.
        public static bool TryUpgradeToEmailOnly(string source, int emailColumnLength,
            out string upgraded, out string problem)
        {
            problem = "";
            if (!TryReplaceExactlyOnce(source, BuildLookupReplacement(emailColumnLength),
                    BuildLookupEmailOnly(emailColumnLength), out upgraded, out int count))
            {
                upgraded = "";
                problem = count == 0
                    ? "το σημείο B (customer lookup) δεν βρέθηκε όπως το αφήνει η έκδοση V1"
                    : $"το σημείο B (customer lookup) βρέθηκε {count} φορές";
                return false;
            }
            return true;
        }

        // left(...) guards both write sites: f_email is varchar(100) but CUST.EMAIL is narrower
        // (VARCHAR(50) on both live databases). These writes are NOT inside the "when any do",
        // so an over-length address would raise string right truncation, the trigger would throw
        // and the whole EFOM insert - the incoming order - would be rejected. The width is read
        // from the schema rather than hardcoded.
        private static string BuildCardnoReplacement(int emailColumnLength) => @"
           /* " + Marker + @" */
           if (f_email is not null) then
            update cust
               set email = left(:f_email, " + emailColumnLength + @")
             where custid = :f_custid
               and trim(coalesce(email, '')) = '';
" + AnchorCardno;

        private const string ReplInsertColumns = "phone,\n                email,\n                cardno,";
        private static string BuildInsertValues(int emailColumnLength) =>
            "coalesce(new.mobile, new.landline),\n               left(:f_email, " + emailColumnLength +
            "),\n               new.cardno,";

        // ---- entry point ---------------------------------------------------------------

        public static Result Run(Settings settings, string backupDirectory)
        {
            Result result = RunCore(settings, backupDirectory);
            TryLog(backupDirectory, result);
            return result;
        }

        private static Result RunCore(Settings settings, string backupDirectory)
        {
            string connectionString = settings.ToConnectionString();
            try
            {
                string? source;
                int emailLength;

                // Read on its own connection and close it. Firebird refuses a metadata change
                // while another transaction still has the object open, and an implicit read
                // transaction lingering on the same connection is enough to make the ALTER
                // fail at COMMIT - which a rolled-back test never reveals.
                using (var connection = new FbConnection(connectionString))
                {
                    connection.Open();
                    source = ReadTriggerSource(connection);
                    emailLength = source == null ? 0 : GetEmailColumnLength(connection);
                }

                if (source == null)
                    return new Result
                    {
                        Outcome = Outcome.NeedsManualReview,
                        Message = $"Το trigger {TriggerName} δεν βρέθηκε στη βάση.",
                    };

                if (source.Contains(MarkerEmailOnly, StringComparison.Ordinal))
                    return new Result
                    {
                        Outcome = Outcome.AlreadyPatched,
                        Message = "e-mail correlation active (e-mail only)",
                    };

                // Back up before touching anything - never patch without the original on disk.
                string backupPath = WriteBackup(source, backupDirectory);

                // A trigger already carrying the e-mail-then-phone patch (V1, e.g. from
                // ΔΙΑΧΕΙΡΙΣΗ) is upgraded in place; a vendor trigger gets all five edits.
                bool upgrading = source.Contains(Marker, StringComparison.Ordinal);
                string patched, problem;
                bool ok = upgrading
                    ? TryUpgradeToEmailOnly(source, emailLength, out patched, out problem)
                    : TryTransform(source, emailLength, out patched, out problem);
                if (!ok)
                    return new Result
                    {
                        Outcome = Outcome.NeedsManualReview,
                        BackupPath = backupPath,
                        Message = "NOT patched - manual review needed. Ο προμηθευτής άλλαξε τη δομή του " +
                                  $"trigger: {problem}",
                    };

                try
                {
                    using var ddlConnection = new FbConnection(connectionString);
                    ddlConnection.Open();
                    ApplyTrigger(ddlConnection, patched);
                }
                catch (Exception ex) when (IsObjectInUse(ex))
                {
                    return new Result
                    {
                        Outcome = Outcome.Failed,
                        BackupPath = backupPath,
                        Message = "NOT patched - το trigger χρησιμοποιείται αυτή τη στιγμή από άλλο " +
                                  "πρόγραμμα (πιθανότατα το Orexsys ή οι online παραγγελίες).\n\n" +
                                  "Κλείστε το Orexsys σε όλους τους σταθμούς και ξανανοίξτε αυτή την " +
                                  "εφαρμογή.\n\nΛεπτομέρεια: " + ex.Message,
                    };
                }

                // Verify from the database on a fresh connection, not from the string we built.
                string? after;
                using (var verifyConnection = new FbConnection(connectionString))
                {
                    verifyConnection.Open();
                    after = ReadTriggerSource(verifyConnection);
                }
                if (after == null || !after.Contains(Marker, StringComparison.Ordinal) ||
                    !after.Contains(MarkerEmailOnly, StringComparison.Ordinal))
                    return new Result
                    {
                        Outcome = Outcome.Failed,
                        BackupPath = backupPath,
                        Message = "NOT patched - το trigger εφαρμόστηκε αλλά ο έλεγχος απέτυχε (marker missing).",
                    };

                if (after.Contains(AbandonedTest, StringComparison.OrdinalIgnoreCase))
                    return new Result
                    {
                        Outcome = Outcome.Failed,
                        BackupPath = backupPath,
                        Message = "NOT patched - βρέθηκε το εγκαταλελειμμένο test '" + AbandonedTest + "'.",
                    };

                return new Result
                {
                    Outcome = Outcome.Patched,
                    BackupPath = backupPath,
                    Message = upgrading ? "upgraded to e-mail only" : "patched successfully (e-mail only)",
                };
            }
            catch (Exception ex)
            {
                return new Result
                {
                    Outcome = Outcome.Failed,
                    Message = "NOT patched - " + ex.Message,
                };
            }
        }

        // ---- CatalogBridge additions -----------------------------------------------------

        // PatchedWithPhone = the V1 patch (e-mail, then phone) - upgraded by the next Run.
        public enum State { Patched, PatchedWithPhone, NotPatched, Missing }

        // Read-only: is the patch in the live trigger right now? For the line in Ρυθμίσεις.
        public static State ReadState(Settings settings)
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            string? source = ReadTriggerSource(connection);
            return source == null ? State.Missing
                 : source.Contains(MarkerEmailOnly, StringComparison.Ordinal) ? State.Patched
                 : source.Contains(Marker, StringComparison.Ordinal) ? State.PatchedWithPhone
                 : State.NotPatched;
        }

        // Takes the patch out again by reversing the five edits on the live source - same
        // rules as applying it: backup first, every piece exactly once or nothing, one
        // transaction, re-read to confirm. Only ever on an explicit request from Ρυθμίσεις.
        public static Result Remove(Settings settings, string backupDirectory)
        {
            Result result = RemoveCore(settings, backupDirectory);
            TryLog(backupDirectory, result);
            return result;
        }

        private static Result RemoveCore(Settings settings, string backupDirectory)
        {
            string connectionString = settings.ToConnectionString();
            try
            {
                string? source;
                int emailLength;
                using (var connection = new FbConnection(connectionString))
                {
                    connection.Open();
                    source = ReadTriggerSource(connection);
                    emailLength = source == null ? 0 : GetEmailColumnLength(connection);
                }

                if (source == null)
                    return new Result { Outcome = Outcome.NeedsManualReview, Message = $"Το trigger {TriggerName} δεν βρέθηκε στη βάση." };
                if (!source.Contains(Marker, StringComparison.Ordinal))
                    return new Result { Outcome = Outcome.NothingToRemove, Message = "e-mail correlation was not applied - nothing to remove" };

                string backupPath = WriteBackup(source, backupDirectory);

                if (!TryRevert(source, emailLength, out string restored, out string problem))
                    return new Result
                    {
                        Outcome = Outcome.NeedsManualReview,
                        BackupPath = backupPath,
                        Message = "NOT removed - manual review needed: " + problem,
                    };

                try
                {
                    using var ddlConnection = new FbConnection(connectionString);
                    ddlConnection.Open();
                    ApplyTrigger(ddlConnection, restored);
                }
                catch (Exception ex) when (IsObjectInUse(ex))
                {
                    return new Result
                    {
                        Outcome = Outcome.Failed,
                        BackupPath = backupPath,
                        Message = "NOT removed - το trigger χρησιμοποιείται αυτή τη στιγμή από άλλο πρόγραμμα " +
                                  "(πιθανότατα το Orexsys). Κλείστε το Orexsys σε όλους τους σταθμούς και ξαναδοκιμάστε.\n\n" +
                                  "Λεπτομέρεια: " + ex.Message,
                    };
                }

                string? after;
                using (var verifyConnection = new FbConnection(connectionString))
                {
                    verifyConnection.Open();
                    after = ReadTriggerSource(verifyConnection);
                }
                if (after == null || after.Contains(Marker, StringComparison.Ordinal))
                    return new Result { Outcome = Outcome.Failed, BackupPath = backupPath, Message = "NOT removed - the marker is still there after the change." };

                return new Result { Outcome = Outcome.Removed, BackupPath = backupPath, Message = "removed successfully" };
            }
            catch (Exception ex)
            {
                return new Result { Outcome = Outcome.Failed, Message = "NOT removed - " + ex.Message };
            }
        }

        // The exact reverse of TryTransform: each replacement back to its anchor, in reverse
        // order, each exactly once. Pure; unit-tested against the vendor source.
        public static bool TryRevert(string source, int emailColumnLength, out string restored, out string problem)
        {
            restored = "";
            problem = "";

            var edits = new (string Name, string Patched, string Original)[]
            {
                ("C3 (backfill e-mail)", BuildCardnoReplacement(emailColumnLength), AnchorCardno),
                ("C2 (insert values)", BuildInsertValues(emailColumnLength), AnchorInsertValues),
                ("C1 (insert columns)", ReplInsertColumns, AnchorInsertColumns),
                ("B (customer lookup)", source.Contains(MarkerEmailOnly, StringComparison.Ordinal)
                    ? BuildLookupEmailOnly(emailColumnLength) : BuildLookupReplacement(emailColumnLength), AnchorLookup),
                ("A (declarations)", ReplDeclare, AnchorDeclare),
            };

            string working = source;
            foreach (var edit in edits)
            {
                if (!TryReplaceExactlyOnce(working, edit.Patched, edit.Original, out working, out int count))
                {
                    problem = count == 0
                        ? $"το σημείο {edit.Name} δεν βρέθηκε όπως το είχε αφήσει η αντιστοίχιση"
                        : $"το σημείο {edit.Name} βρέθηκε {count} φορές";
                    return false;
                }
            }

            if (working.Contains(Marker, StringComparison.Ordinal))
            {
                problem = "έμεινε σημάδι της αντιστοίχισης μετά την αφαίρεση";
                return false;
            }

            restored = working;
            return true;
        }

        // Firebird reports a metadata lock several ways depending on version and wording, so
        // match on the recognisable fragments rather than a single error code.
        private static bool IsObjectInUse(Exception ex)
        {
            string m = ex.Message;
            return m.Contains("in use", StringComparison.OrdinalIgnoreCase)
                || m.Contains("lock conflict", StringComparison.OrdinalIgnoreCase)
                || m.Contains("object is in use", StringComparison.OrdinalIgnoreCase)
                || m.Contains("unsuccessful metadata update", StringComparison.OrdinalIgnoreCase);
        }

        // A durable record of what happened on each startup. Without this, a failure at the
        // customer's site is a dialog somebody clicked away with nothing left to diagnose.
        private static void TryLog(string directory, Result result)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result.Outcome}  {result.Message}" +
                              (result.BackupPath == null ? "" : $"  [backup: {result.BackupPath}]");
                File.AppendAllText(Path.Combine(directory, "efom_guard.log"),
                    line.ReplaceLineEndings(" ") + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never be the reason startup fails.
            }
        }

        private static string? ReadTriggerSource(FbConnection connection)
        {
            using var command = new FbCommand(
                "select rdb$trigger_source from rdb$triggers where rdb$trigger_name = @Name", connection);
            command.Parameters.Add("@Name", FbDbType.VarChar).Value = TriggerName;
            object? result = command.ExecuteScalar();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        private static int GetEmailColumnLength(FbConnection connection)
        {
            using var command = new FbCommand(
                "select f.rdb$character_length from rdb$relation_fields rf " +
                "join rdb$fields f on f.rdb$field_name = rf.rdb$field_source " +
                "where rf.rdb$relation_name = 'CUST' and rf.rdb$field_name = 'EMAIL'", connection);
            object? result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                throw new InvalidOperationException("Η στήλη CUST.EMAIL δεν βρέθηκε.");
            return Convert.ToInt32(result);
        }

        private static string WriteBackup(string source, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory,
                $"{TriggerName}_{DateTime.Now:yyyyMMdd_HHmmss}.sql");
            File.WriteAllText(path, source, new UTF8Encoding(false));
            return path;
        }

        // Applies all three edits to the CURRENT source. Returns false, with a reason, if any
        // anchor is missing or ambiguous - in which case nothing is applied at all. A
        // half-applied patch is far worse than an unpatched trigger.
        // emailOnly: CatalogBridge always applies the e-mail-only lookup; false gives ΔΙΑΧΕΙΡΙΣΗ's V1
        // (e-mail, then phone) - kept for the tests of the in-place upgrade.
        public static bool TryTransform(string source, int emailColumnLength,
            out string patched, out string problem, bool emailOnly = true)
        {
            patched = "";
            problem = "";

            var edits = new (string Name, string Anchor, string Replacement)[]
            {
                ("A (declarations)", AnchorDeclare, ReplDeclare),
                ("B (customer lookup)", AnchorLookup, emailOnly
                    ? BuildLookupEmailOnly(emailColumnLength) : BuildLookupReplacement(emailColumnLength)),
                ("C1 (insert columns)", AnchorInsertColumns, ReplInsertColumns),
                ("C2 (insert values)", AnchorInsertValues, BuildInsertValues(emailColumnLength)),
                ("C3 (backfill e-mail)", AnchorCardno, BuildCardnoReplacement(emailColumnLength)),
            };

            string working = source;
            foreach (var edit in edits)
            {
                if (!TryReplaceExactlyOnce(working, edit.Anchor, edit.Replacement, out working, out int count))
                {
                    problem = count == 0
                        ? $"το σημείο {edit.Name} δεν βρέθηκε"
                        : $"το σημείο {edit.Name} βρέθηκε {count} φορές";
                    return false;
                }
            }

            patched = working;
            return true;
        }

        // Whitespace-insensitive but structure-exact: every run of whitespace in the anchor
        // matches any run of whitespace in the source, and everything else must match literally.
        // The replacement is only made when there is EXACTLY one match.
        private static bool TryReplaceExactlyOnce(string source, string anchor, string replacement,
            out string result, out int count)
        {
            result = source;

            var pattern = new StringBuilder();
            foreach (string piece in Regex.Split(anchor.Trim(), @"\s+"))
                pattern.Append(pattern.Length == 0 ? "" : @"\s+").Append(Regex.Escape(piece));

            var matches = Regex.Matches(source, pattern.ToString());
            count = matches.Count;
            if (count != 1) return false;

            var m = matches[0];
            result = source.Substring(0, m.Index) + replacement.Trim() + source.Substring(m.Index + m.Length);
            return true;
        }

        private static void ApplyTrigger(FbConnection connection, string patchedSource)
        {
            // All-or-nothing: if the ALTER does not compile, the vendor's working trigger stays
            // live rather than the database being left half-changed.
            using var transaction = connection.BeginTransaction();
            try
            {
                // RDB$TRIGGER_SOURCE stores the BODY only, already beginning with "as" - not a
                // full CREATE TRIGGER statement. So the DDL is just ALTER TRIGGER <name> plus
                // that body verbatim, which leaves the name, firing events (BEFORE INSERT OR
                // UPDATE OR DELETE), position and active state untouched.
                //
                // No SET TERM here: that is an isql client directive, not SQL. The driver sends
                // the single statement as-is, so adding it would be a syntax error.
                string body = patchedSource.TrimStart();
                if (!Regex.IsMatch(body, @"^as\b", RegexOptions.IgnoreCase))
                    body = "as\n" + body;

                string ddl = $"ALTER TRIGGER {TriggerName}\n{body}";

                using var command = new FbCommand(ddl, connection, transaction);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
