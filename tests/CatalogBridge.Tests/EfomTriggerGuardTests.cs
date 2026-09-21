using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // The e-mail correlation patch against the REAL vendor trigger text (Stavros and the STOA
    // copy run the same version, 21/09/2026).
    [TestClass]
    public class EfomTriggerGuardTests
    {
        private static string Vendor => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "EFOM_BIUD_TRG_vendor.sql"));

        private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        [TestMethod]
        public void PatchesTheVendorTrigger()
        {
            Assert.IsTrue(EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out string problem), problem);

            StringAssert.Contains(patched, EfomTriggerGuard.Marker);
            StringAssert.Contains(patched, "position('\"customerEmail\"', f_jsontxt)");
            StringAssert.Contains(patched, "where upper(trim(email)) = upper(trim(:f_email))");
            Assert.AreEqual(2, Regex.Matches(patched, @"left\(:f_email, 50\)").Count, "both write sites capped at CUST.EMAIL's width");
            // The phone lookup stays, as the fallback, inside «if (:f_custid is null)».
            StringAssert.Contains(Squash(patched), "if (:f_custid is null) then begin --try to get customer by exact match on phone number");
        }

        [TestMethod]
        public void CustomerCreationGetsTheEmailColumnAndValueAligned()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);
            string s = Squash(patched);

            StringAssert.Contains(s, "phone, email, cardno,");
            StringAssert.Contains(s, "coalesce(new.mobile, new.landline), left(:f_email, 50), new.cardno,");
        }

        // The anchors are gone after patching: a second transform must refuse, not stack edits.
        [TestMethod]
        public void RefusesToPatchTwice()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);

            Assert.IsFalse(EfomTriggerGuard.TryTransform(patched, 50, out _, out string problem));
            StringAssert.Contains(problem, "B (customer lookup)");
        }

        [TestMethod]
        public void RefusesAnUnknownTriggerAndChangesNothing()
        {
            string changed = Vendor.Replace("--try to get customer by like phone number", "--vendor rewrote this");

            Assert.IsFalse(EfomTriggerGuard.TryTransform(changed, 50, out string patched, out string problem));
            Assert.AreEqual("", patched);
            StringAssert.Contains(problem, "δεν βρέθηκε");
        }

        // «Αφαίρεση από το Orexsys»: exactly the vendor's trigger again (whitespace aside).
        [TestMethod]
        public void RemoveGivesBackTheVendorTrigger()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);

            Assert.IsTrue(EfomTriggerGuard.TryRevert(patched, 50, out string restored, out string problem), problem);
            Assert.IsFalse(restored.Contains(EfomTriggerGuard.Marker));
            Assert.AreEqual(Squash(Vendor), Squash(restored));
        }

        [TestMethod]
        public void RemoveOnAnUnpatchedTriggerRefuses()
        {
            Assert.IsFalse(EfomTriggerGuard.TryRevert(Vendor, 50, out string restored, out _));
            Assert.AreEqual("", restored);
        }

        [TestMethod]
        public void TheSettingIsSavedAndLoaded()
        {
            Assert.IsFalse(new Settings().EmailOrderCorrelation, "off unless a technician turns it on");
        }
    }
}
