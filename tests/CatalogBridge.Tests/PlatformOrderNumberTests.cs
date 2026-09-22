using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // Reading the PointToOrder token back out of EFOM.JSON for an order that arrived FROM the
    // platform (owner, 22/09/2026) - a plain substring scan, tolerant of the SQL-side VARCHAR
    // truncation cutting the JSON off before its closing brace (the field sits near the start).
    [TestClass]
    public class PlatformOrderNumberTests
    {
        [TestMethod]
        public void ReadsTheTokenFromRealEfomJson() =>
            Assert.AreEqual("48-PY", CustomerDirectory.PlatformOrderNumberFromEfomJson(
                "{\"credentials\":{},\"orderDetails\":{\"orderNumber\":\"48-PY\",\"storeId\":\"PO4897\"},\"orderItems\":[...TRUNCATED"));

        [TestMethod]
        public void NoEfomRow_ReturnsEmpty() => Assert.AreEqual("", CustomerDirectory.PlatformOrderNumberFromEfomJson(""));

        [TestMethod]
        public void FieldMissing_ReturnsEmpty() =>
            Assert.AreEqual("", CustomerDirectory.PlatformOrderNumberFromEfomJson("{\"orderDetails\":{\"storeId\":\"PO4897\"}}"));

        [TestMethod]
        public void SurvivesTruncationRightAfterTheField() =>
            Assert.AreEqual("TP-5Z", CustomerDirectory.PlatformOrderNumberFromEfomJson("{\"orderDetails\":{\"orderNumber\":\"TP-5Z\""));
    }
}
