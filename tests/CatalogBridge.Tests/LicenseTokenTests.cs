using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // Phase-1 licensing (docs/LICENSING_AND_UPDATES.md §4): offline verification of a signed,
    // machine-bound token; the state per module by date. A throw-away key pair per test run -
    // the real private key is never in the repo.
    [TestClass]
    public class LicenseTokenTests
    {
        private static readonly string PrivateKey;
        private static readonly string PublicKey;
        private const string Fp = "9F2C-A1B3-7D44-0E19-C5F2";
        private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

        static LicenseTokenTests()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            PrivateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }

        private static LicenseToken Token(DateTime until, params string[] modules) =>
            new("catalogbridge", "GK-TEST-TEST-TEST-0000", Fp, "ΣΤΟΑ ΑΕ", modules, 1046, Now.AddDays(-1), until, until, 14);

        [TestMethod]
        public void SignedTokenVerifiesAndRoundTrips()
        {
            string signed = Token(Now.AddYears(1), "catalogue", "orders").Sign(PrivateKey);

            var r = LicenseToken.Verify(signed, "catalogbridge", Fp, new[] { PublicKey });
            Assert.IsTrue(r.Ok, r.Problem);
            Assert.AreEqual("ΣΤΟΑ ΑΕ", r.Token!.Customer);
            Assert.AreEqual(1046L, r.Token.Store);
            Assert.IsTrue(r.Token.Has(Module.Catalogue));
            Assert.IsTrue(r.Token.Has(Module.Orders));
            Assert.AreEqual(14, r.Token.GraceDays);
        }

        [TestMethod]
        public void TamperedPayloadIsNotGenuine()
        {
            string signed = Token(Now.AddYears(1), "catalogue").Sign(PrivateKey);
            // Re-sign the same payload with modules changed, using a DIFFERENT key: signature
            // no longer matches the embedded public key.
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string forged = Token(Now.AddYears(1), "catalogue", "orders").Sign(Convert.ToBase64String(other.ExportPkcs8PrivateKey()));

            Assert.IsFalse(LicenseToken.Verify(forged, "catalogbridge", Fp, new[] { PublicKey }).Ok);
            // and a byte flipped in a genuine one
            string flipped = signed[..^3] + (signed[^3] == 'A' ? "B" : "A") + signed[^2..];
            Assert.IsFalse(LicenseToken.Verify(flipped, "catalogbridge", Fp, new[] { PublicKey }).Ok);
        }

        [TestMethod]
        public void OtherMachine_OtherProduct_AreRefusedWithAReason()
        {
            string signed = Token(Now.AddYears(1), "catalogue").Sign(PrivateKey);

            var machine = LicenseToken.Verify(signed, "catalogbridge", "0000-0000-0000-0000-0000", new[] { PublicKey });
            Assert.IsFalse(machine.Ok);
            StringAssert.Contains(machine.Problem, "άλλον υπολογιστή");

            var product = LicenseToken.Verify(signed, "lighthouse", Fp, new[] { PublicKey });
            Assert.IsFalse(product.Ok);
            StringAssert.Contains(product.Problem, "άλλο πρόγραμμα");
        }

        [TestMethod]
        public void FingerprintMatchIgnoresDashesAndCase() =>
            Assert.IsTrue(LicenseToken.Verify(Token(Now.AddYears(1), "catalogue").Sign(PrivateKey),
                "catalogbridge", "9f2ca1b37d440e19c5f2", new[] { PublicKey }).Ok);

        [TestMethod]
        public void KeyRotation_AnyListedKeyVerifies()
        {
            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string signedWithNew = Token(Now.AddYears(1), "catalogue").Sign(Convert.ToBase64String(newKey.ExportPkcs8PrivateKey()));
            var keys = new List<string> { PublicKey, Convert.ToBase64String(newKey.ExportSubjectPublicKeyInfo()) };

            Assert.IsTrue(LicenseToken.Verify(signedWithNew, "catalogbridge", Fp, keys).Ok);
            Assert.IsFalse(LicenseToken.Verify(signedWithNew, "catalogbridge", Fp, new[] { PublicKey }).Ok, "not listed = not trusted");
        }

        [TestMethod]
        public void EmptyOrGarbageIsNotALicence()
        {
            Assert.IsFalse(LicenseToken.Verify("", "catalogbridge", Fp, new[] { PublicKey }).Ok);
            Assert.IsFalse(LicenseToken.Verify("hello", "catalogbridge", Fp, new[] { PublicKey }).Ok);
            Assert.IsFalse(LicenseToken.Verify("a.b.c", "catalogbridge", Fp, new[] { PublicKey }).Ok);
            Assert.IsFalse(LicenseToken.Verify("!!!.???", "catalogbridge", Fp, new[] { PublicKey }).Ok);
        }

        // §4.1: licensed → grace → expired, per module; a module not in the token is NotLicensed.
        [TestMethod]
        public void StateFollowsTheDates()
        {
            var t = Token(Now.AddDays(10), "catalogue");

            Assert.AreEqual(LicenseState.Licensed, t.StateOf(Module.Catalogue, Now));
            Assert.AreEqual(LicenseState.Licensed, t.StateOf(Module.Catalogue, Now.AddDays(10)), "the last moment still counts");
            Assert.AreEqual(LicenseState.Grace, t.StateOf(Module.Catalogue, Now.AddDays(11)));
            Assert.AreEqual(LicenseState.Grace, t.StateOf(Module.Catalogue, Now.AddDays(24)));
            Assert.AreEqual(LicenseState.Expired, t.StateOf(Module.Catalogue, Now.AddDays(25)));
            Assert.AreEqual(LicenseState.NotLicensed, t.StateOf(Module.Orders, Now), "orders not bought");
        }

        [TestMethod]
        public void FingerprintFormatIsFiveGroupsOfFour()
        {
            string fp = MachineFingerprint.Format(SHA256.HashData(new byte[] { 1, 2, 3 }));
            StringAssert.Matches(fp, new System.Text.RegularExpressions.Regex("^[0-9A-F]{4}(-[0-9A-F]{4}){4}$"));
            Assert.AreEqual(fp, MachineFingerprint.Format(SHA256.HashData(new byte[] { 1, 2, 3 })), "deterministic");
        }
    }
}
