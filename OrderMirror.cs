using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // Online orders, decided in this app and applied to BOTH sides (owner, 01/10/2026 - see
    // docs/ORDER_ACCEPTANCE.md §6-§10). Every online order reaches Orexsys at submission anyway
    // (the relay does not wait for anyone) and waits there as the connector's EFOM row, STATUS 1.
    //   Αποδοχή here  = that row 1 → 2 with the minutes (AcceptAtTillAsync - the till's own accept;
    //                   the vendor trigger raises the till order), then PointToOrder CLOSED + ETA.
    //   Απόρριψη here = that row 1 → -1 (RejectAtTillAsync - nobody raises it), then REJECTED.
    //   The till's own online-orders screen may answer first: then the row is already 2 / -1 and
    //   this app only MIRRORS it to PointToOrder (Decide / MirrorAsync).
    //   A waiting row whose platform order is already rejected / cancelled is voided (-1) by the
    //   service (ShouldVoidAtTill / RowsToVoid) - «not accepted, yet placed» can no longer happen.
    // With the switch on, PointToOrder's auto-accept is off, so until someone answers the
    // customer sees «waiting». Orexsys first, platform second: the customer never hears
    // «accepted» for an order the till could not raise.
    //
    // Idempotence: only an order that is still SUBMITTED (or RECEIVED) on the platform is ever
    // told anything (Decide), and the two Orexsys updates are guarded by STATUS = 1 + exact row
    // count. Till-sent copies (loop-guard tag) are never decided: their -1 is the guard.

    public sealed record TillRow(string Token, int Status, int AcceptMin, DateTime Insdate, bool TaggedCopy, long? OrdmId, long EfomId = 0)
    {
        public bool Waiting => Status == 1;
        public bool Accepted => Status == 2;
        public bool Rejected => Status < 0;
    }

    public enum MirrorAction { None, Accept, Reject }

    // One line on the card: a platform order that is still waiting, and where it stands at the till.
    public sealed record WaitingOrder(PendingOrder Order, TillRow? Row, DateTime NowUtc)
    {
        public bool AtTill => Row is { Waiting: true };
        public TimeSpan Age => NowUtc - Order.SubmittedAt;
        public int Minutes => Math.Max(0, (int)Age.TotalMinutes);

        // What the pill says.
        public string StateText => AtTill ? $"Στο ταμείο · {Minutes}′" : $"Δεν έφτασε στο ταμείο · {Minutes}′";

        // 0 normal, 1 amber (take a look), 2 red (something is wrong / stuck).
        public int Level => !AtTill ? (Age >= OrderMirror.NotAtTillAlarm ? 2 : 1)
                          : Age >= OrderMirror.StuckAlarm ? 2 : Age >= OrderMirror.SlowAlarm ? 1 : 0;

        public string Hint => Level == 0 ? "" : !AtTill
            ? "Ο σύνδεσμος του Orexsys δεν την έφερε ακόμα — ελέγξτε ότι τρέχει."
            : "Περιμένει πολλή ώρα — ανοίξτε την οθόνη online παραγγελιών στο ταμείο.";
    }

    public sealed record MirrorEvent(DateTime When, string Token, MirrorAction Action, int Minutes);

    public sealed class MirrorState
    {
        public List<WaitingOrder> Waiting { get; init; } = new();
        public List<MirrorEvent> Today { get; init; } = new();
        public string Status { get; init; } = "";
        public bool Error { get; init; }

        public int AcceptedToday => Today.Count(e => e.Action == MirrorAction.Accept);
        public int RejectedToday => Today.Count(e => e.Action == MirrorAction.Reject);

        public string TodayText => Today.Count == 0 ? "" :
            $"Σήμερα: {AcceptedToday} δεκτ{(AcceptedToday == 1 ? "ή" : "ές")}" +
            (RejectedToday > 0 ? $" · {RejectedToday} απόρριψ{(RejectedToday == 1 ? "η" : "εις")}" : "") +
            $" · τελευταία {Today.Max(e => e.When).ToLocalTime():HH:mm}";
    }

    public static class OrderMirror
    {
        public static readonly TimeSpan SlowAlarm = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan StuckAlarm = TimeSpan.FromMinutes(8);
        public static readonly TimeSpan NotAtTillAlarm = TimeSpan.FromMinutes(2);
        public const string RejectReason = "Λυπούμαστε, το κατάστημα δεν μπορεί να εξυπηρετήσει αυτή την παραγγελία αυτή τη στιγμή.";

        // ---- pure ---------------------------------------------------------------------------

        public static MirrorAction Decide(string platformStatus, TillRow? row)
        {
            if (platformStatus is not ("SUBMITTED" or "RECEIVED")) return MirrorAction.None;
            if (row == null || row.TaggedCopy) return MirrorAction.None;
            if (row.Accepted) return MirrorAction.Accept;
            if (row.Rejected) return MirrorAction.Reject;
            return MirrorAction.None;
        }

        // Deciding INSIDE the tool (owner, 01/10 evening): Αποδοχή here flips the connector's row
        // to 2 - the same update the till's accept screen makes - so it is only possible while
        // that row exists and still waits. Null = go ahead; otherwise the reason, for the screen.
        public static string? CanAcceptHere(WaitingOrder w)
        {
            if (w.Row == null) return "Δεν έφτασε ακόμα στο ταμείο — περιμένετε λίγο.";
            if (w.Row.TaggedCopy) return "Είναι αντίγραφο παραγγελίας του ταμείου — δεν γίνεται αποδοχή.";
            if (!w.Row.Waiting) return w.Row.Accepted ? "Έχει ήδη γίνει δεκτή στο ταμείο." : "Έχει ήδη απορριφθεί στο ταμείο.";
            return null;
        }

        // Απόρριψη here: fine while the till has not answered (row absent - platform only; or
        // waiting - voided first). Never for a row the till already raised or rejected (the
        // customer would read «rejected» while the kitchen cooks), never for a till copy.
        public static string? CanRejectHere(WaitingOrder w)
        {
            if (w.Row == null) return null;
            if (w.Row.TaggedCopy) return "Είναι αντίγραφο παραγγελίας του ταμείου — δεν γίνεται απόρριψη.";
            if (!w.Row.Waiting) return w.Row.Accepted ? "Έχει ήδη γίνει δεκτή στο ταμείο — ακυρώστε την από τις παραγγελίες του πελάτη." : "Έχει ήδη απορριφθεί στο ταμείο.";
            return null;
        }

        // The accept button names its outcome (and changes with the minutes chosen).
        public static string AcceptLabel(int minutes) => minutes > 0 ? $"Αποδοχή, έτοιμη σε {minutes}′" : "Αποδοχή";

        // «Not accepted, yet placed in Orexsys»: a row still waiting at the till while the platform
        // order is already rejected or cancelled must be voided (-1) before anyone raises it.
        public static bool ShouldVoidAtTill(TillRow row, string platformStatus) =>
            row.Waiting && !row.TaggedCopy && platformStatus is "REJECTED" or "CANCELLED" or "DELETED";

        // The waiting rows to void now, and the tokens whose platform status explains them
        // (CLOSED etc. - no need to ask again until the platform moves). Rows in the SUBMITTED
        // list are the board's; unknown tokens stay unexplained and are asked again.
        public static (List<TillRow> ToVoid, HashSet<string> Explained) RowsToVoid(
            IEnumerable<TillRow> rows, IEnumerable<PendingOrder> pending, IReadOnlyDictionary<string, string> platformStatus)
        {
            var onBoard = new HashSet<string>(pending.Select(p => p.Token), StringComparer.OrdinalIgnoreCase);
            var toVoid = new List<TillRow>();
            var explained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows.Where(r => r.Waiting && !r.TaggedCopy && !onBoard.Contains(r.Token)))
            {
                if (!platformStatus.TryGetValue(r.Token, out var status)) continue;
                if (ShouldVoidAtTill(r, status)) toVoid.Add(r);
                else explained.Add(r.Token);
            }
            return (toVoid, explained);
        }

        // A till order the app itself sent to the platform (comment tag): the shop's own history
        // entry, never something to decide. The service closes it; the board skips it.
        public static bool IsTillCopy(PendingOrder order) => TillOrderSender.TagToExternalId(order.Comment) != null;

        // The platform orders that are still waiting, paired with their till row (by token).
        public static List<WaitingOrder> Pair(IEnumerable<PendingOrder> pending, IReadOnlyDictionary<string, TillRow> rows, DateTime nowUtc) =>
            pending.Where(p => !IsTillCopy(p))
                   .Select(p => new WaitingOrder(p, rows.TryGetValue(p.Token, out var r) ? r : null, nowUtc))
                   .OrderBy(w => w.Order.SubmittedAt).ToList();

        public static Dictionary<string, TillRow> Index(IEnumerable<TillRow> rows)
        {
            // Two rows of one token (connector upsert, or a till copy next to the real one): the
            // one that is NOT a tagged copy wins; among those, the latest.
            var map = new Dictionary<string, TillRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows.OrderBy(r => r.TaggedCopy).ThenBy(r => r.Insdate))
                if (!map.TryGetValue(r.Token, out var have) || (have.TaggedCopy && !r.TaggedCopy) || have.TaggedCopy == r.TaggedCopy)
                    map[r.Token] = r;
            return map;
        }

        public static DateTime? EstimatedTime(TillRow row, DateTime nowUtc) =>
            row.AcceptMin > 0 ? nowUtc.AddMinutes(row.AcceptMin) : null;

        // ---- register (what was mirrored, next to the exe) ----------------------------------

        public static string RegisterFile => Path.Combine(AppContext.BaseDirectory, "mirrored_orders.txt");

        public static List<MirrorEvent> LoadToday(string file, DateTime todayLocal)
        {
            var list = new List<MirrorEvent>();
            if (!File.Exists(file)) return list;
            foreach (var line in File.ReadAllLines(file))
            {
                var p = line.Split('|');
                if (p.Length < 4) continue;
                if (!DateTime.TryParse(p[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) continue;
                if (when.ToLocalTime().Date != todayLocal.Date) continue;
                if (!Enum.TryParse(p[2], out MirrorAction action)) continue;
                int.TryParse(p[3], out int min);
                list.Add(new MirrorEvent(when, p[1], action, min));
            }
            return list;
        }

        public static void Remember(string file, MirrorEvent e) =>
            File.AppendAllText(file, $"{e.When:O}|{e.Token}|{e.Action}|{e.Minutes}{Environment.NewLine}");

        public static bool AlreadyRemembered(IEnumerable<MirrorEvent> today, string token) =>
            today.Any(e => string.Equals(e.Token, token, StringComparison.OrdinalIgnoreCase));

        // Register once: the decision is made where Orexsys changed; a retry of the platform
        // call (service) must not count it again.
        public static void RememberOnce(string file, MirrorEvent e)
        {
            if (!AlreadyRemembered(LoadToday(file, DateTime.Today), e.Token)) Remember(file, e);
        }

        // ---- Orexsys: read the connector's rows ----------------------------------------------

        // The connector's rows for this store since `since`. The loop-guard tag is looked for in
        // the JSON blob with POSITION - never CAST it (Firebird throws past the cast length).
        public static Task<List<TillRow>> LoadRowsAsync(Settings settings, string storeCode, DateTime since) => Task.Run(() =>
        {
            var list = new List<TillRow>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var command = new FbCommand(@"
                SELECT EFOMID, ORDERID, STATUS, ACCEPTMIN, INSDATE, ORDMID,
                       CASE WHEN POSITION(@tag IN JSON) > 0 THEN 1 ELSE 0 END AS TAGGED
                FROM EFOM
                WHERE INSDATE >= @since AND UPPER(TRIM(STOREID)) = UPPER(@store) AND ORDERID IS NOT NULL", connection);
            command.Parameters.Add("@tag", FbDbType.VarChar).Value = EfomTriggerGuard.SentOrderTag;
            command.Parameters.Add("@since", FbDbType.TimeStamp).Value = since;
            command.Parameters.Add("@store", FbDbType.VarChar).Value = storeCode.Trim();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add(new TillRow(
                    (reader["ORDERID"]?.ToString() ?? "").Trim(),
                    reader["STATUS"] == DBNull.Value ? 0 : Convert.ToInt32(reader["STATUS"]),
                    reader["ACCEPTMIN"] == DBNull.Value ? 0 : Convert.ToInt32(reader["ACCEPTMIN"]),
                    reader["INSDATE"] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(reader["INSDATE"]),
                    Convert.ToInt32(reader["TAGGED"]) == 1,
                    reader["ORDMID"] == DBNull.Value ? null : Convert.ToInt64(reader["ORDMID"]),
                    Convert.ToInt64(reader["EFOMID"])));
            return list;
        });

        // ---- Orexsys: the till's own two updates, made from here --------------------------------

        // Αποδοχή inside the tool = exactly what the till's accept screen does: the connector's
        // row goes 1 → 2 with the minutes, and the vendor trigger raises the till order. Guarded
        // by STATUS = 1 and an exact row count: a second click, a row the till already answered,
        // or a stale screen change nothing. Returns the ORDMID the trigger produced (null if the
        // database set it later or not at all - reported, not fatal).
        public static Task<long?> AcceptAtTillAsync(Settings settings, long efomId, int minutes) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var update = new FbCommand("UPDATE EFOM SET STATUS = 2, ACCEPTMIN = @min WHERE EFOMID = @id AND STATUS = 1", connection, transaction);
            update.Parameters.Add("@min", FbDbType.Integer).Value = Math.Max(0, minutes);
            update.Parameters.Add("@id", FbDbType.BigInt).Value = efomId;
            int rows = update.ExecuteNonQuery();
            if (rows != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException("Η παραγγελία δεν περιμένει πια στο ταμείο (έχει ήδη απαντηθεί) — τίποτα δεν άλλαξε.");
            }
            transaction.Commit();
            using var read = new FbCommand("SELECT ORDMID FROM EFOM WHERE EFOMID = @id", connection);
            read.Parameters.Add("@id", FbDbType.BigInt).Value = efomId;
            object? ordm = read.ExecuteScalar();
            return ordm == null || ordm == DBNull.Value ? (long?)null : Convert.ToInt64(ordm);
        });

        // Απόρριψη inside the tool, or the void of a platform-rejected row: 1 → -1, the
        // connector's own cancel value, so nobody at the till can raise it. Same guard.
        public static Task RejectAtTillAsync(Settings settings, long efomId) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var update = new FbCommand("UPDATE EFOM SET STATUS = -1 WHERE EFOMID = @id AND STATUS = 1", connection, transaction);
            update.Parameters.Add("@id", FbDbType.BigInt).Value = efomId;
            int rows = update.ExecuteNonQuery();
            if (rows != 1)
            {
                transaction.Rollback();
                throw new InvalidOperationException("Η παραγγελία δεν περιμένει πια στο ταμείο (έχει ήδη απαντηθεί) — τίποτα δεν άλλαξε.");
            }
            transaction.Commit();
        });

        // ---- the mirror itself ----------------------------------------------------------------

        // Applies one till decision to the platform. Returns what it did (None when nothing to do).
        public static async Task<MirrorAction> MirrorAsync(PointToOrderApiClient api, PendingOrder order, TillRow? row, DateTime nowUtc)
        {
            var action = Decide("SUBMITTED", row);   // the list only ever holds SUBMITTED orders
            if (action == MirrorAction.Accept)
                await OrderAcceptance.AcceptAsync(api, order.Uuid, EstimatedTime(row!, nowUtc));
            else if (action == MirrorAction.Reject)
                await OrderAcceptance.RejectAsync(api, order.Uuid, RejectReason);
            return action;
        }
    }
}
