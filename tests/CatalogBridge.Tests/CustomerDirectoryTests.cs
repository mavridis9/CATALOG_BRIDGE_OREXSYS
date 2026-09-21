using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // «Πελάτες»: the shop creates the customer once for both systems (owner decision 21/09/2026).
    [TestClass]
    public class CustomerDirectoryTests
    {
        private static CustomerInput Good => new("Μαρία Παππά", "10045", "maria@example.com", "6971234567");

        [TestMethod]
        public void AcceptsACompleteCustomer()
        {
            Assert.IsNull(CustomerDirectory.Validate(Good));
            Assert.IsNull(CustomerDirectory.Validate(Good with { Mobile = "" }), "the mobile is optional");
            Assert.IsNull(CustomerDirectory.Validate(Good with { Mobile = "+30 697 123 4567" }), "written the usual ways");
        }

        [TestMethod]
        public void NameCardAndEmailAreRequired()
        {
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Name = " " }));
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Card = "" }));
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Email = "" }));
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Email = "not-an-email" }));
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Email = new string('a', 45) + "@example.com" }),
                "CUST.EMAIL is VARCHAR(50)");
        }

        // A non-mobile number passes customer creation on PointToOrder and then makes the
        // platform refuse the customer's ORDER later (learned at LIGHTHOUSE).
        [TestMethod]
        public void ATypedPhoneMustBeAGreekMobile()
        {
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Mobile = "2810210048" }));
            Assert.IsNotNull(CustomerDirectory.Validate(Good with { Mobile = "12345" }));
        }

        [TestMethod]
        public void EmptyMobileGetsAStandInBuiltFromTheCustomerId()
        {
            Assert.AreEqual("6900002201", PointToOrderPhone.Resolve(2201, ""));
            Assert.AreEqual("6971234567", PointToOrderPhone.Resolve(2201, "+30 697 123 4567"));
        }

        [TestMethod]
        public void NameIsSplitSoThePlatformDoesNotDoubleIt()
        {
            Assert.AreEqual(("Μαρία", "Παππά"), PointToOrderName.Split("Μαρία Παππά"));
            Assert.AreEqual(("Μαρία", "Παππά Ιωάννου"), PointToOrderName.Split(" Μαρία Παππά Ιωάννου "));
        }

        // Matched on e-mail exactly as the order trigger matches: upper + trim.
        [TestMethod]
        public void MergeLinksByEmailIgnoringCaseAndSpaces()
        {
            var rows = CustomerDirectory.Merge(
                new[]
                {
                    new OrexsysCustomer(1, "Μαρία Παππά", "10045", "Maria@Example.com ", "6971234567"),
                    new OrexsysCustomer(2, "Νίκος", "10046", "nikos@example.com", ""),
                },
                new[]
                {
                    new PlatformCustomer(900, "Maria P", "maria@example.com", "6971234567", "10045", true),
                    new PlatformCustomer(901, "Walk In", "walkin@example.com", "6900000001", "", false),
                });

            Assert.AreEqual(CustomerLink.Linked, rows.Single(r => r.Orexsys?.CustId == 1).Link);
            Assert.AreEqual(900, rows.Single(r => r.Orexsys?.CustId == 1).Platform!.Id);
            Assert.AreEqual(CustomerLink.OrexsysOnly, rows.Single(r => r.Orexsys?.CustId == 2).Link);
            Assert.AreEqual(CustomerLink.PlatformOnly, rows.Single(r => r.Platform?.Id == 901).Link);
            Assert.AreEqual(3, rows.Count);
        }

        // Two Orexsys customers with one e-mail: the trigger's «select first 1» would pick one
        // at random, so both are flagged and listed first.
        [TestMethod]
        public void DuplicateEmailsInOrexsysAreFlaggedAndListedFirst()
        {
            var rows = CustomerDirectory.Merge(
                new[]
                {
                    new OrexsysCustomer(1, "Άννα", "1", "same@example.com", ""),
                    new OrexsysCustomer(2, "Άννα (διπλή)", "2", "SAME@example.com", ""),
                    new OrexsysCustomer(3, "Βασίλης", "3", "v@example.com", ""),
                },
                new PlatformCustomer[0]);

            Assert.AreEqual(2, rows.Count(r => r.Link == CustomerLink.DuplicateEmail));
            Assert.AreEqual(CustomerLink.DuplicateEmail, rows[0].Link);
        }

        [TestMethod]
        public void ACardThatDiffersBetweenTheTwoSystemsIsShown()
        {
            var row = CustomerDirectory.Merge(
                new[] { new OrexsysCustomer(1, "Μαρία", "10045", "m@example.com", "") },
                new[] { new PlatformCustomer(900, "Μαρία", "m@example.com", "", "99999", false) }).Single();

            StringAssert.Contains(row.CardNote, "99999");
            Assert.AreEqual("Χωρίς πρόσκληση", row.AccountText);
        }
    }
}
