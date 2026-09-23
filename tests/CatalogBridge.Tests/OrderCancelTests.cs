using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // One click, both platforms (owner, 22/09/2026): only the side(s) still active get touched.
    [TestClass]
    public class OrderCancelTests
    {
        [TestMethod]
        public void BothActive_CancelsBoth() =>
            Assert.AreEqual(CancelSide.Both, OrderCancel.Decide(cancelledInOrexsys: false, platformStatus: "CLOSED"));

        [TestMethod]
        public void NoPlatformOrderAtAll_CancelsOrexsysOnly() =>
            Assert.AreEqual(CancelSide.OrexsysOnly, OrderCancel.Decide(cancelledInOrexsys: false, platformStatus: null));

        [TestMethod]
        public void AlreadyCancelledOnPlatform_CancelsOrexsysOnly()
        {
            Assert.AreEqual(CancelSide.OrexsysOnly, OrderCancel.Decide(false, "CANCELLED"));
            Assert.AreEqual(CancelSide.OrexsysOnly, OrderCancel.Decide(false, "DELETED"));
        }

        // The owner's own words: "if it's already cancelled in Orexsys, the button cancels it in
        // the other platform".
        [TestMethod]
        public void AlreadyCancelledInOrexsys_CancelsPlatformOnly() =>
            Assert.AreEqual(CancelSide.PlatformOnly, OrderCancel.Decide(cancelledInOrexsys: true, platformStatus: "CLOSED"));

        [TestMethod]
        public void AlreadyCancelledInOrexsys_AndNoPlatformOrder_DoesNothing() =>
            Assert.AreEqual(CancelSide.None, OrderCancel.Decide(cancelledInOrexsys: true, platformStatus: null));

        [TestMethod]
        public void AlreadyCancelledBothSides_DoesNothing()
        {
            Assert.AreEqual(CancelSide.None, OrderCancel.Decide(true, "CANCELLED"));
            Assert.AreEqual(CancelSide.None, OrderCancel.Decide(true, "DELETED"));
        }

        // CANCELORDER remarks = VARCHAR(50); a longer one blew up live on 23/09.
        [TestMethod]
        public void RemarksAreCappedAtWhatTheProcedureTakes()
        {
            Assert.AreEqual(50, OrderCancel.FitRemarks(new string('x', 200)).Length);
            Assert.AreEqual("short", OrderCancel.FitRemarks("short"));
            Assert.AreEqual("", OrderCancel.FitRemarks(null!));
        }

        [TestMethod]
        public void UnknownPlatformStatus_IsTreatedAsActive() =>
            // "" = a token is known but its status could not be read - never assume cancelled.
            Assert.AreEqual(CancelSide.Both, OrderCancel.Decide(false, ""));
    }
}
