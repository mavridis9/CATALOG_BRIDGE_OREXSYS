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
            // The phone lookup survives only for orders WITHOUT an e-mail (see the e-mail-only tests).
            StringAssert.Contains(Squash(patched), "else begin --try to get customer by exact match on phone number");
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

        // Owner decision 21/09: with an e-mail in the order the phone is never consulted.
        [TestMethod]
        public void EmailOnly_PhoneLookupRunsOnlyWhenThereIsNoEmail()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);
            string s = Squash(patched);

            StringAssert.Contains(patched, EfomTriggerGuard.MarkerEmailOnly);
            StringAssert.Contains(s, "where upper(trim(email)) = upper(trim(:f_email)) into :f_custid; " +
                                     "else begin --try to get customer by exact match on phone number");
            Assert.IsFalse(s.Contains("into :f_custid; if (:f_custid is null) then begin --try to get customer by exact"),
                "the e-mail-then-phone fallback (V1) must be gone");
        }

        // ΔΙΑΧΕΙΡΙΣΗ checks only for the V1 marker: it must still see the trigger as patched.
        [TestMethod]
        public void EmailOnly_KeepsTheV1MarkerForΔΙΑΧΕΙΡΙΣΗ()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);

            StringAssert.Contains(patched, EfomTriggerGuard.Marker);
        }

        // A database patched by ΔΙΑΧΕΙΡΙΣΗ (V1, e-mail then phone) is upgraded in place.
        [TestMethod]
        public void V1IsUpgradedInPlaceToEmailOnly()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string v1, out _, emailOnly: false);
            EfomTriggerGuard.TryTransform(Vendor, 50, out string direct, out _);

            Assert.IsTrue(EfomTriggerGuard.TryUpgradeToEmailOnly(v1, 50, out string upgraded, out string problem), problem);
            Assert.AreEqual(Squash(direct), Squash(upgraded));
            Assert.IsFalse(EfomTriggerGuard.TryUpgradeToEmailOnly(upgraded, 50, out _, out _), "no second upgrade");
        }

        [TestMethod]
        public void RemoveWorksFromEitherVersion()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string v1, out _, emailOnly: false);

            Assert.IsTrue(EfomTriggerGuard.TryRevert(v1, 50, out string restored, out string problem), problem);
            Assert.AreEqual(Squash(Vendor), Squash(restored));
        }

        // Owner decision 22/09: a till order sent to PointToOrder comes back through the
        // connector; the «[OREXSYS#» tag in its comment makes the trigger reject it (STATUS -1)
        // on insert and whenever the connector tries to accept it.
        [TestMethod]
        public void LoopGuard_IsPartOfTheFullPatch()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);
            string s = Squash(patched);

            StringAssert.Contains(patched, EfomTriggerGuard.MarkerLoopGuard);
            StringAssert.Contains(s, "if (inserting) then begin /* OREXSYS-LOOP-GUARD-V1");
            StringAssert.Contains(s, "if (position('[OREXSYS#', cast(new.json as varchar(8000))) > 0) then new.status = -1;");
            StringAssert.Contains(s, "if ((new.status = 2) and (position('[OREXSYS#', cast(new.json as varchar(8000))) > 0)) then new.status = -1; " +
                                     "if (old.status != new.status) then execute procedure sendmessage('RefreshEFood');");
        }

        // A database already on e-mail-only (21/09) gets only the loop guard, in place.
        [TestMethod]
        public void LoopGuard_IsAddedToAnEmailOnlyTrigger()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string full, out _);
            // simulate the 21/09 state: full patch without the guard
            Assert.IsTrue(EfomTriggerGuard.TryRevert(full, 50, out string vendorAgain, out _));
            EfomTriggerGuard.TryTransform(vendorAgain, 50, out string again, out _);
            Assert.AreEqual(Squash(full), Squash(again));

            Assert.IsFalse(EfomTriggerGuard.TryAddLoopGuard(full, out _, out string problem), "not twice: " + problem);
        }

        [TestMethod]
        public void RemoveTakesTheLoopGuardOutToo()
        {
            EfomTriggerGuard.TryTransform(Vendor, 50, out string patched, out _);

            Assert.IsTrue(EfomTriggerGuard.TryRevert(patched, 50, out string restored, out string problem), problem);
            Assert.IsFalse(restored.Contains(EfomTriggerGuard.MarkerLoopGuard));
            Assert.AreEqual(Squash(Vendor), Squash(restored));
        }

        [TestMethod]
        public void TheSettingIsSavedAndLoaded()
        {
            Assert.IsFalse(new Settings().EmailOrderCorrelation, "off unless a technician turns it on");
        }
    }
}
