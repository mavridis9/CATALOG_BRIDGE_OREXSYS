using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // The pure part of «Αποδοχή παραγγελιών από το ταμείο». The JSON shapes are the ones the
    // platform answered on 01/10/2026 (store 1110, orders 96-54 / M3-93 / 97-TN).
    [TestClass]
    public class OrderAcceptanceTests
    {
        private const string SearchPage = @"{""data"":[
            {""uuid"":""u-1"",""orderToken"":""96-54"",""status"":""SUBMITTED"",""customerName"":""ΔΟΚΙΜΗ"",""totalDiscountedPrice"":4.00,
             ""submittedAt"":""2026-10-01T11:24:39Z"",""createdAt"":""2026-10-01T11:24:39Z"",""deliveryMethod"":""NO_LOCATION"",""comment"":""χωρίς κρεμμύδι"",
             ""orderItems"":[{""itemName"":""ΠΙΤΑ ΓΥΡΟΣ"",""quantity"":1,""totalDiscountedPrice"":4.00,""hierarchyLevel"":""PARENT""},
                             {""itemName"":""ΧΛ"",""quantity"":1,""totalDiscountedPrice"":2.00,""hierarchyLevel"":""CHILD"",""parentOfferId"":27132}]},
            {""uuid"":""u-2"",""orderToken"":""M3-93"",""status"":""REJECTED"",""customerName"":""X"",""totalDiscountedPrice"":4.00,""submittedAt"":""2026-10-01T11:26:55Z""},
            {""uuid"":""u-3"",""orderToken"":""97-TN"",""status"":""SUBMITTED"",""customerName"":""Y"",""totalDiscountedPrice"":12.50,
             ""submittedAt"":""2026-10-01T11:20:00Z"",""wishTime"":""2026-10-02T09:00:00Z"",""deliveryMethod"":""ADDRESS"",
             ""orderItems"":[{""itemName"":""ΠΙΤΑ ΚΟΤΟΓΥΡΟΣ"",""quantity"":2,""totalDiscountedPrice"":12.50}]}
            ],""totalPages"":1}";

        [TestMethod]
        public void ParsePending_KeepsOnlySubmitted_OldestFirst()
        {
            var list = OrderAcceptance.ParsePending(SearchPage);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("97-TN", list[0].Token);   // submitted 11:20, before 96-54 at 11:24
            Assert.AreEqual("96-54", list[1].Token);
            Assert.AreEqual(4.00m, list[1].Total);
            Assert.AreEqual(new DateTime(2026, 10, 1, 11, 24, 39, DateTimeKind.Utc), list[1].SubmittedAt);
        }

        [TestMethod]
        public void ParsePending_FoldsOptionsIntoTheLineAbove()
        {
            var o = OrderAcceptance.ParsePending(SearchPage)[1];
            Assert.AreEqual(1, o.Lines.Count);
            Assert.AreEqual("1× ΠΙΤΑ ΓΥΡΟΣ (ΧΛ)", o.Items);
            Assert.AreEqual("χωρίς κρεμμύδι", o.Comment);
        }

        [TestMethod]
        public void Title_SaysDeliveryAndWhen()
        {
            var list = OrderAcceptance.ParsePending(SearchPage);
            StringAssert.Contains(list[1].Title, "Παραλαβή, το συντομότερο");
            StringAssert.Contains(list[0].Title, "Διανομή, για 02/10");
            StringAssert.Contains(list[0].Items, "2× ΠΙΤΑ ΚΟΤΟΓΥΡΟΣ");
        }

        // orders/search does not carry the lines (seen live 01/10, AM-M3): the ticket reads them
        // from the full order, GET orders/{uuid}?simplified=false.
        [TestMethod]
        public void ParseOrderLines_FromTheFullOrder()
        {
            const string full = @"{""uuid"":""u-1"",""orderToken"":""AM-M3"",""status"":""SUBMITTED"",
                ""orderItems"":[{""itemName"":""ΠΙΤΑ ΓΥΡΟΣ"",""quantity"":2,""totalDiscountedPrice"":8.00,""hierarchyLevel"":""PARENT""},
                                {""itemName"":""ΠΑΚΕΤΟ"",""quantity"":1,""totalDiscountedPrice"":0,""hierarchyLevel"":""CHILD"",""parentOfferId"":27132},
                                {""shortDescription"":""ΚΟΚΑ ΚΟΛΑ"",""quantity"":1,""totalDiscountedPrice"":1.50}]}";
            var lines = OrderAcceptance.ParseOrderLines(full);
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("ΠΙΤΑ ΓΥΡΟΣ", lines[0].Name);
            Assert.AreEqual(2, lines[0].Quantity);
            Assert.AreEqual("ΠΑΚΕΤΟ", lines[0].Options);
            Assert.AreEqual("ΚΟΚΑ ΚΟΛΑ", lines[1].Name);
            Assert.AreEqual(1.50m, lines[1].Total);
            Assert.AreEqual(0, OrderAcceptance.ParseOrderLines(@"{""uuid"":""x""}").Count);
        }

        // The full order's line (AM-M3, 01/10) has no totalDiscountedPrice: the price is in
        // totalNonDiscountedPrice / discountedOfferPrice.
        [TestMethod]
        public void ParseOrderLines_PriceFallsBackToWhatTheFullOrderCarries()
        {
            var a = OrderAcceptance.ParseOrderLines(@"{""orderItems"":[{""itemName"":""ΠΙΤΑ ΓΥΡΟΣ"",""quantity"":1,""offerPrice"":4.00,""discountedOfferPrice"":4.00,""totalNonDiscountedPrice"":4.00,""hierarchyLevel"":""PARENT""}]}");
            Assert.AreEqual(4.00m, a.Single().Total);
            var b = OrderAcceptance.ParseOrderLines(@"{""orderItems"":[{""itemName"":""ΠΙΤΑ"",""quantity"":3,""discountedOfferPrice"":2.50}]}");
            Assert.AreEqual(7.50m, b.Single().Total);
        }

        [TestMethod]
        public void ParsePending_EmptyOrOddPayloads()
        {
            Assert.AreEqual(0, OrderAcceptance.ParsePending(@"{""data"":[],""totalPages"":0}").Count);
            Assert.AreEqual(0, OrderAcceptance.ParsePending(@"{""content"":[]}").Count);
        }

        [TestMethod]
        public void ParseLastUpdate_ReadsTheStamp()
        {
            var d = OrderAcceptance.ParseLastUpdate(@"{""storeId"":1110,""updateType"":""ORDER"",""lastUpdatedAt"":""2026-10-01T11:24:39.835503Z""}");
            Assert.IsNotNull(d);
            Assert.AreEqual(DateTimeKind.Utc, d!.Value.Kind);
            Assert.AreEqual(new DateTime(2026, 10, 1, 11, 24, 39).AddTicks(8355030), d.Value);
            Assert.IsNull(OrderAcceptance.ParseLastUpdate(@"{""storeId"":1110}"));
        }

        [TestMethod]
        public void NewSince_IsByUuid()
        {
            var before = OrderAcceptance.ParsePending(SearchPage);
            var after = new List<PendingOrder>(before) { before[0] with { Uuid = "u-9", Token = "ZZ-99" } };
            var added = OrderAcceptance.NewSince(before, after);
            Assert.AreEqual(1, added.Count);
            Assert.AreEqual("ZZ-99", added[0].Token);
            Assert.AreEqual(0, OrderAcceptance.NewSince(after, before).Count);
        }

        [TestMethod]
        public void RejectReason_DefaultsAndCaps()
        {
            Assert.AreEqual(OrderAcceptance.DefaultRejectReason, OrderAcceptance.FitRejectReason(null));
            Assert.AreEqual(OrderAcceptance.DefaultRejectReason, OrderAcceptance.FitRejectReason("   "));
            Assert.AreEqual("Τελείωσε", OrderAcceptance.FitRejectReason(" Τελείωσε "));
            Assert.AreEqual(OrderAcceptance.RejectReasonMax, OrderAcceptance.FitRejectReason(new string('α', 2000)).Length);
        }

        [TestMethod]
        public void PlatformSetting_IsTheOppositeOfTheSwitch()
        {
            Assert.AreEqual("false", OrderAcceptance.PlatformValueFor(acceptanceEnabled: true));
            Assert.AreEqual("true", OrderAcceptance.PlatformValueFor(acceptanceEnabled: false));
        }

        [TestMethod]
        public void ReadAutoAccept_FromTheStoreAnswer()
        {
            Assert.AreEqual("true", OrderAcceptance.ReadAutoAccept(@"{""id"":1110,""settings"":{""AUTO_ACCEPT_ORDERS"":""true""}}"));
            Assert.AreEqual("false", OrderAcceptance.ReadAutoAccept(@"{""id"":1110,""settings"":{""AUTO_ACCEPT_ORDERS"":false}}"));
            Assert.IsNull(OrderAcceptance.ReadAutoAccept(@"{""id"":1110,""settings"":{}}"));
        }

        [TestMethod]
        public void Switch_IsOffByDefault()
        {
            Assert.IsFalse(new Settings().OrderAcceptanceEnabled);
        }
    }
}
