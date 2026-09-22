using System;
using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CatalogBridge.Tests
{
    // A till order goes to PointToOrder ONCE (owner, 22/09/2026). The pieces that can be
    // tested without the platform: the shared register next to the exe, and the reading of
    // the customer's history that decides «already there».
    [TestClass]
    public class TillOrderOnceTests
    {
        private static string TempFile() => Path.Combine(Path.GetTempPath(), "cb_sent_" + Guid.NewGuid().ToString("N") + ".txt");

        [TestMethod]
        public void RegisterRemembersWhatWasSent_AndSurvivesGarbage()
        {
            string file = TempFile();
            try
            {
                Assert.AreEqual(0, TillOrderSender.LoadSentRegister(file).Count, "missing file = nothing sent");

                TillOrderSender.RememberSent(file, 5, "NN-3K");
                TillOrderSender.RememberSent(file, 9, "");
                File.AppendAllText(file, "not a number\tX\n\n");

                var set = TillOrderSender.LoadSentRegister(file);
                CollectionAssert.AreEquivalent(new[] { 5L, 9L }, new System.Collections.Generic.List<long>(set));
            }
            finally { File.Delete(file); }
        }

        [TestMethod]
        public void RegisterAlsoGivesBackTheToken_ForTheCancelButton()
        {
            string file = TempFile();
            try
            {
                TillOrderSender.RememberSent(file, 10, "TP-5Z");
                TillOrderSender.RememberSent(file, 11, "");          // sent, token unknown - no use for a lookup
                File.AppendAllText(file, "garbage\n");

                var map = TillOrderSender.LoadSentRegisterWithTokens(file);
                Assert.AreEqual("TP-5Z", map[10]);
                Assert.IsFalse(map.ContainsKey(11));
                Assert.AreEqual(0, TillOrderSender.LoadSentRegisterWithTokens(file + ".missing").Count);
            }
            finally { File.Delete(file); }
        }

        [TestMethod]
        public void HistoryEntryCountsOnlyWhenItIsOursAndAlive()
        {
            static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

            // Submitted: externalOrderId became the token; the comment tag is what identifies it.
            Assert.IsTrue(TillOrderSender.IsSentTillOrder(J("{\"externalOrderId\":\"NN-3K\",\"orderToken\":\"NN-3K\",\"status\":\"CLOSED\",\"comment\":\"[OREXSYS#5] Αγορά στο κατάστημα 22/09/2026 09:50: ΠΡΟΙΟΝ 13% ×1 100,00€\"}"), out var ext, out var tok));
            Assert.AreEqual("OREXSYS-5", ext);
            Assert.AreEqual("NN-3K", tok);

            Assert.IsFalse(TillOrderSender.IsSentTillOrder(J("{\"externalOrderId\":\"OREXSYS-5\",\"status\":\"CANCELLED\"}"), out _, out _), "cancelled = may be sent again");

            Assert.IsFalse(TillOrderSender.IsSentTillOrder(J("{\"externalOrderId\":\"OREXSYS-5\",\"status\":\"DELETED\"}"), out _, out _));
            Assert.IsFalse(TillOrderSender.IsSentTillOrder(J("{\"orderToken\":\"AB-12\",\"status\":\"CLOSED\"}"), out _, out _), "a platform order is not ours");
            Assert.IsFalse(TillOrderSender.IsSentTillOrder(J("{\"externalOrderId\":null,\"status\":\"CLOSED\"}"), out _, out _));
            Assert.IsTrue(TillOrderSender.IsSentTillOrder(J("{\"externalOrderId\":\"OREXSYS-7\",\"status\":\"DRAFT\"}"), out _, out _), "a draft with our id blocks too - it must be cleaned, not doubled");
        }

        [TestMethod]
        public void TheCommentTagIsTheKey()
        {
            Assert.AreEqual("OREXSYS-5", TillOrderSender.TagToExternalId("[OREXSYS#5] Αγορά στο κατάστημα 22/09/2026 09:50: ΠΡΟΙΟΝ 13% ×1 100,00€"));
            Assert.AreEqual("OREXSYS-12", TillOrderSender.TagToExternalId("note [OREXSYS#12]"));
            Assert.IsNull(TillOrderSender.TagToExternalId("χωρίς κρεμμύδι"));
            Assert.IsNull(TillOrderSender.TagToExternalId("[OREXSYS#abc]"));
            Assert.IsNull(TillOrderSender.TagToExternalId(""));
        }

        [TestMethod]
        public void AlreadySentMessageNamesTheOrderAndTheToken()
        {
            var ex = new TillOrderSender.AlreadySentException(5, "NN-3K");
            StringAssert.Contains(ex.Message, "#5");
            StringAssert.Contains(ex.Message, "NN-3K");
            StringAssert.Contains(new TillOrderSender.AlreadySentException(6, "").Message, "#6");
        }
    }
}
