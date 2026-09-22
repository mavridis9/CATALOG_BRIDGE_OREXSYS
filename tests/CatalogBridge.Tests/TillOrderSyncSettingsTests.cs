using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    [TestClass]
    public class TillOrderSyncSettingsTests
    {
        // The home-page switch is off until the shop turns it on, and «since» defaults to today
        // so older orders are never sent by surprise.
        [TestMethod]
        public void SwitchIsOffByDefaultAndSinceIsToday()
        {
            var s = new Settings();
            Assert.IsFalse(s.TillOrderSyncEnabled);
            Assert.AreEqual(DateTime.Today, s.TillOrderSyncSince);
        }
    }
}
