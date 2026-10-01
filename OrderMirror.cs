using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // The mirror (owner decision 01/10/2026, after the measurements in docs/ORDER_ACCEPTANCE.md):
    // the TILL decides. Every online order reaches Orexsys at submission anyway (the relay does
    // not wait for anyone) and waits there as EFOM STATUS 1 until the till's own «online
    // orders» screen accepts it (STATUS 2, ACCEPTMIN minutes, the vendor trigger raises the till
    // order) or rejects it (STATUS -1). This app only MIRRORS that decision to PointToOrder, so
    // the customer learns it: 2 → CLOSED (+ estimatedTime from ACCEPTMIN), -1 → REJECTED.
    // With the switch on, PointToOrder's auto-accept is off, so until the till answers the
    // customer sees «waiting» — honest, and nothing is lost if this app is closed: the till still
    // gets every order; only the customer's screen lags until the app runs again.
    //
    // Idempotence comes from PointToOrder itself: only an order that is still SUBMITTED (or
    // RECEIVED) on the platform is ever touched (Decide). Till-sent copies (loop-guard tag) are
    // never mirrored: their -1 is the guard, not a rejection.

    public sealed record TillRow(string Token, int Status, int AcceptMin, DateTime Insdate, bool TaggedCopy, long? OrdmId)
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

        // The platform orders that are still waiting, paired with their till row (by token).
        public static List<WaitingOrder> Pair(IEnumerable<PendingOrder> pending, IReadOnlyDictionary<string, TillRow> rows, DateTime nowUtc) =>
            pending.Select(p => new WaitingOrder(p, rows.TryGetValue(p.Token, out var r) ? r : null, nowUtc))
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

        // ---- Orexsys (read-only) --------------------------------------------------------------

        // The connector's rows for this store since `since`. The loop-guard tag is looked for in
        // the JSON blob with POSITION - never CAST it (Firebird throws past the cast length).
        public static Task<List<TillRow>> LoadRowsAsync(Settings settings, string storeCode, DateTime since) => Task.Run(() =>
        {
            var list = new List<TillRow>();
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var command = new FbCommand(@"
                SELECT ORDERID, STATUS, ACCEPTMIN, INSDATE, ORDMID,
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
                    reader["ORDMID"] == DBNull.Value ? null : Convert.ToInt64(reader["ORDMID"])));
            return list;
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
