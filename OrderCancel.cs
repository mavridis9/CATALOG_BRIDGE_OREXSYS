using System;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;

namespace CatalogBridge
{
    // One click, both platforms (owner, 22/09/2026): cancel an order in Orexsys AND, if it also
    // exists on PointToOrder (a till order sent there, or one that arrived from there), on the
    // platform too. Whichever side is already cancelled is left alone - the button only ever
    // does what is still needed, so pressing it again does nothing.
    public enum CancelSide { None, OrexsysOnly, PlatformOnly, Both }

    public static class OrderCancel
    {
        // platformStatus: null = no PointToOrder order is known for this Orexsys order at all
        // (nothing to do on that side); otherwise its status there ("CLOSED", "CANCELLED", ...).
        public static CancelSide Decide(bool cancelledInOrexsys, string? platformStatus)
        {
            bool cancelledOnPlatform = platformStatus is null || platformStatus is "CANCELLED" or "DELETED";
            if (cancelledInOrexsys && cancelledOnPlatform) return CancelSide.None;
            if (cancelledInOrexsys) return CancelSide.PlatformOnly;
            if (cancelledOnPlatform) return CancelSide.OrexsysOnly;
            return CancelSide.Both;
        }

        // ---- Orexsys side --------------------------------------------------------------------
        //
        // The official cancellation, exactly as the till and ΔΙΑΧΕΙΡΙΣΗ do it (ported from
        // ΔΙΑΧΕΙΡΙΣΗ's DayClose.Cancel, mavridis9/LIGHTHOUSE_TOOL) - never a manual UPDATE. Read
        // CANCELORDER in full before touching this: it does not check STS (accepts a closed
        // order), reverses the ORDD/ORDP/PRED lines and the customer's counters, and for any
        // fiscal document on the order issues a cancellation document through COPYDOC rather
        // than deleting anything.
        public const int AdministratorUsrsId = 0;

        // CANCELORDER's remarks parameter is VARCHAR(50) (seen live 23/09: "string right truncation,
        // expected length 50, actual 200" at procedure line 51) - capped here so no caller can hit it.
        public const int RemarksMaxLength = 50;

        public static string FitRemarks(string remarks) =>
            (remarks ?? "").Length <= RemarksMaxLength ? (remarks ?? "") : remarks![..RemarksMaxLength];

        public static void CancelInOrexsys(FbConnection connection, FbTransaction transaction, long ordmId, string remarks)
        {
            remarks = FitRemarks(remarks);
            using var command = new FbCommand(
                "EXECUTE PROCEDURE CANCELORDER(@Id, @Usr, @Remarks, NULL)", connection, transaction);
            command.Parameters.Add("@Id", FbDbType.BigInt).Value = ordmId;
            command.Parameters.Add("@Usr", FbDbType.Integer).Value = AdministratorUsrsId;
            command.Parameters.Add("@Remarks", FbDbType.VarChar, RemarksMaxLength).Value = remarks;
            command.ExecuteNonQuery();
        }

        public static Task CancelInOrexsysAsync(Settings settings, long ordmId, string remarks) => Task.Run(() =>
        {
            using var connection = new FbConnection(settings.ToConnectionString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            CancelInOrexsys(connection, transaction, ordmId, remarks);
            transaction.Commit();
        });
    }
}
