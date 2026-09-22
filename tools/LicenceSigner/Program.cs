using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CatalogBridge;

// Phase-1 licensing tool (docs/LICENSING_AND_UPDATES.md §10): the owner's PC signs tokens by
// hand; the server will do the same later with the same key.
//
//   sign-licence keygen <private.key>
//       creates an ECDSA P-256 key pair; writes the PRIVATE key to the file (keep it out of
//       git and backed up), prints the PUBLIC key to paste into License.PublicKeys.
//   sign-licence sign --key <private.key> --customer "ΣΤΟΑ ΑΕ" --fingerprint 9F2C-....
//       [--modules catalogue,orders] [--until 2027-09-22] [--store 1046] [--licence GK-...]
//       [--grace 14] [--product catalogbridge] [--out token.txt]
//       prints the token (and writes it to --out).
//   sign-licence verify --pub <spki-base64> --fingerprint 9F2C-.... <token>
//   sign-licence newkey            prints a fresh licence key (GK-XXXX-XXXX-XXXX-XXXX)
Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0) { Usage(); return 1; }

string? Opt(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

switch (args[0])
{
    case "keygen":
    {
        if (args.Length < 2) { Usage(); return 1; }
        string file = args[1];
        if (File.Exists(file)) { Console.Error.WriteLine($"refusing to overwrite {file}"); return 1; }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(file, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), new UTF8Encoding(false));
        Console.WriteLine("private key written to " + Path.GetFullPath(file) + "  (back it up; never commit it)");
        Console.WriteLine();
        Console.WriteLine("public key - paste into License.PublicKeys:");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case "newkey":
        Console.WriteLine(LicenceKey.New(Opt("prefix") ?? "GK"));
        return 0;
    case "sign":
    {
        string keyFile = Opt("key") ?? throw new ArgumentException("--key");
        string customer = Opt("customer") ?? throw new ArgumentException("--customer");
        string fingerprint = Opt("fingerprint") ?? throw new ArgumentException("--fingerprint");
        var modules = (Opt("modules") ?? "catalogue,orders").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        DateTime until = DateTime.ParseExact(Opt("until") ?? DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        until = until.Date.AddDays(1).AddSeconds(-1);   // the whole last day counts
        long? store = Opt("store") is string s ? long.Parse(s) : null;
        string licence = Opt("licence") ?? LicenceKey.New(Opt("prefix") ?? "GK");
        int grace = int.Parse(Opt("grace") ?? "14");
        string product = Opt("product") ?? "catalogbridge";

        var token = new LicenseToken(product, licence, fingerprint, customer, modules, store,
            DateTime.UtcNow, until, until, grace);
        string signed = token.Sign(File.ReadAllText(keyFile).Trim());
        if (Opt("out") is string outFile) File.WriteAllText(outFile, signed, new UTF8Encoding(false));
        Console.WriteLine($"licence {licence} for «{customer}» on {fingerprint}: {string.Join(", ", modules)}, until {until:yyyy-MM-dd}" +
                          (store is null ? "" : $", store {store}"));
        Console.WriteLine();
        Console.WriteLine(signed);
        return 0;
    }
    case "verify":
    {
        string pub = Opt("pub") ?? throw new ArgumentException("--pub");
        string fingerprint = Opt("fingerprint") ?? throw new ArgumentException("--fingerprint");
        string tok = args[^1];
        var r = LicenseToken.Verify(tok, Opt("product") ?? "catalogbridge", fingerprint, new[] { pub });
        if (!r.Ok) { Console.WriteLine("INVALID: " + r.Problem); return 2; }
        var t = r.Token!;
        Console.WriteLine($"OK  {t.Licence} «{t.Customer}» modules={string.Join(",", t.Modules)} valid_until={t.ValidUntil:u} grace={t.GraceDays}d");
        foreach (Module m in Enum.GetValues(typeof(Module))) Console.WriteLine($"    {m}: {t.StateOf(m, DateTime.UtcNow)}");
        return 0;
    }
    default:
        Usage();
        return 1;
}

static void Usage()
{
    Console.WriteLine("sign-licence keygen <private.key>");
    Console.WriteLine("sign-licence newkey [--prefix GK]");
    Console.WriteLine("sign-licence sign --key <private.key> --customer \"...\" --fingerprint XXXX-XXXX-XXXX-XXXX-XXXX [--modules catalogue,orders] [--until yyyy-MM-dd] [--store N] [--licence GK-...] [--grace 14] [--out file]");
    Console.WriteLine("sign-licence verify --pub <spki-base64> --fingerprint XXXX-... <token>");
}

// §3: GK-XXXX-XXXX-XXXX-XXXX, Crockford base32 (no I L O U), last group = checksum of the
// first twelve characters so a typo is caught before any network call (phase 2).
static class LicenceKey
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(string prefix)
    {
        var rnd = RandomNumberGenerator.GetBytes(12);
        var body = new StringBuilder();
        foreach (byte b in rnd) body.Append(Alphabet[b % 32]);
        string s = body.ToString();
        return $"{prefix}-{s[..4]}-{s[4..8]}-{s[8..12]}-{Checksum(s)}";
    }

    public static string Checksum(string twelve)
    {
        uint h = 2166136261;
        foreach (char c in twelve) { h ^= c; h *= 16777619; }
        var sb = new StringBuilder();
        for (int i = 0; i < 4; i++) { sb.Append(Alphabet[(int)(h % 32)]); h /= 32; }
        return sb.ToString();
    }
}
