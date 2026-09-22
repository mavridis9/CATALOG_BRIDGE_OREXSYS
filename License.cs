using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CatalogBridge
{
    // Phase 1 of docs/LICENSING_AND_UPDATES.md: the licence lives in licence.dat next to the
    // exe, is verified offline against the public key below, and is bound to this machine's
    // fingerprint. There is no server yet - tokens are issued by tools/LicenceSigner on the
    // owner's PC and pasted into Ρυθμίσεις → «Άδεια χρήσης».
    //
    // One rule for the whole app (§4.1): every write path asks License.Blocked(module, out why)
    // first; the technical checks that already exist (trigger patched, API configured) stay
    // behind it. Licence state NEVER removes anything - not the trigger patch, not EFIC rows.
    public static class License
    {
        public const string Product = "catalogbridge";

        // ECDSA P-256 public keys (SubjectPublicKeyInfo, base64). A list so a second key can
        // ship in one release and the first be retired in a later one. The private key is
        // NOT in this repo (owner's machine only; see tools/LicenceSigner/README.md).
        public static readonly string[] PublicKeys =
        {
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAENruhChdAYXd9lKUXL8zxnhNCbNs6GOQ3cOfntxJZu3IkAl4GNUU0V6EqxAA2GcrgMATTLC0owreQXxhoJE941w==",
        };

        public static string TokenFile => Path.Combine(AppContext.BaseDirectory, "licence.dat");

        public static LicenseToken? Current { get; private set; }

        // Why there is no usable token ("" when Current != null).
        public static string Problem { get; private set; } = "Δεν υπάρχει άδεια χρήσης.";

        public static string Fingerprint => fingerprint ??= MachineFingerprint.Compute();
        private static string? fingerprint;

        // Called at start and after Ρυθμίσεις. Never throws.
        public static void Load()
        {
            try
            {
                string token = File.Exists(TokenFile) ? File.ReadAllText(TokenFile, Encoding.UTF8) : "";
                var result = LicenseToken.Verify(token, Product, Fingerprint, PublicKeys);
                Current = result.Token;
                Problem = result.Problem;
            }
            catch (Exception ex)
            {
                Current = null;
                Problem = "Η άδεια δεν διαβάστηκε: " + ex.Message;
            }
        }

        // Verifies, then writes - a token that does not verify is never stored.
        public static LicenseToken.VerifyResult Install(string token)
        {
            var result = LicenseToken.Verify(token, Product, Fingerprint, PublicKeys);
            if (!result.Ok) return result;
            File.WriteAllText(TokenFile, token.Trim(), new UTF8Encoding(false));
            Load();
            Log($"installed: {result.Token!.Licence} «{result.Token.Customer}» modules={string.Join(",", result.Token.Modules)} until {result.Token.LicenceUntil:yyyy-MM-dd}");
            return result;
        }

        public static LicenseState StateOf(Module module) =>
            Current?.StateOf(module, DateTime.UtcNow) ?? LicenseState.NotLicensed;

        // True when the module may write / run its automatic services right now.
        public static bool CanWrite(Module module) => StateOf(module) is LicenseState.Licensed or LicenseState.Grace;

        // The one call every write path makes. Message is what the shop sees.
        public static bool Blocked(Module module, out string message)
        {
            switch (StateOf(module))
            {
                case LicenseState.Licensed:
                case LicenseState.Grace:
                    message = "";
                    return false;
                case LicenseState.Expired:
                    message = $"Η άδεια χρήσης για «{Label(module)}» έληξε στις {Current!.ValidUntil:dd/MM/yyyy}. " +
                              "Επικοινωνήστε για ανανέωση — μέχρι τότε το πρόγραμμα μόνο δείχνει, δεν αλλάζει τίποτα.";
                    return true;
                default:
                    message = Current == null
                        ? "Δεν υπάρχει άδεια χρήσης — ανοίξτε τις Ρυθμίσεις → «Άδεια χρήσης»." + (Problem.Length > 0 ? " (" + Problem + ")" : "")
                        : $"Η άδεια χρήσης δεν περιλαμβάνει το «{Label(module)}».";
                    return true;
            }
        }

        public static string Label(Module module) => module switch
        {
            Module.Catalogue => "Συγχρονισμός Καταλόγου",
            Module.Orders => "Αντιστοίχιση Παραγγελιών & Πελατών",
            _ => module.ToString(),
        };

        // One line for the sidebar: «Άδεια: ΣΤΟΑ ΑΕ · έως 22/09/2027» / «Άδεια: ΛΗΞΕ ...» / «Χωρίς άδεια».
        public static string Summary()
        {
            if (Current == null) return "Χωρίς άδεια χρήσης";
            var worst = Worst();
            string until = Current.LicenceUntil.ToString("dd/MM/yyyy");
            return worst switch
            {
                LicenseState.Expired => $"Άδεια: ΕΛΗΞΕ ({until})",
                LicenseState.Grace => $"Άδεια: {Current.Customer} · έληξε {until} — περίοδος χάριτος",
                _ => $"Άδεια: {Current.Customer} · έως {until}",
            };
        }

        // The state of the modules the token actually carries (a module not bought is not a
        // problem to warn about).
        public static LicenseState Worst()
        {
            if (Current == null) return LicenseState.NotLicensed;
            var worst = LicenseState.Licensed;
            foreach (Module m in Enum.GetValues(typeof(Module)))
                if (Current.Has(m) && StateOf(m) > worst) worst = StateOf(m);
            return worst;
        }

        private static void Log(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "licence.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // §3: SHA-256 of Windows MachineGuid + the system volume's serial, shown as five groups
    // of four hex characters (80 bits - enough, and dictatable over the phone). The raw
    // values never leave the machine; the token carries only this.
    public static class MachineFingerprint
    {
        public static string Compute()
        {
            string guid = "";
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                guid = key?.GetValue("MachineGuid")?.ToString() ?? "";
            }
            catch { }

            uint serial = 0;
            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                GetVolumeInformationW(root, null, 0, out serial, out _, out _, null, 0);
            }
            catch { }

            return Format(SHA256.HashData(Encoding.UTF8.GetBytes($"{guid}|{serial:X8}")));
        }

        public static string Format(byte[] hash)
        {
            string hex = Convert.ToHexString(hash)[..20];
            return string.Join("-", new[] { hex[..4], hex[4..8], hex[8..12], hex[12..16], hex[16..20] });
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(string lpRootPathName, StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
            out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
            StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);
    }
}
