using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Jev.Tests
{
    /// <summary>Local lifecycle/file-system regression only. Never sends or fakes an API response.</summary>
    public sealed class JevTransportReloadTests
    {
        [Test]
        public void EmptyStringsAfterUnityReloadRecoverCredentialsAndCreateRedactedLedger()
        {
            const string fixtureKey = "offline-reload-fixture-never-sent";
            string oldEnvironment = Environment.GetEnvironmentVariable("JEV_API_KEY");
            string createdLedger = null;
            GameObject host = null;
            try
            {
                // Avoid reading or changing the user's local credential file.
                Environment.SetEnvironmentVariable("JEV_API_KEY", fixtureKey);
                host = new GameObject("Local JEV reload regression");
                host.SetActive(false);
                var transport = host.AddComponent<JevTransport>();
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                typeof(JevTransport).GetField("apiKey", flags).SetValue(transport, string.Empty);
                typeof(JevTransport).GetField("ledgerPath", flags).SetValue(transport, string.Empty);

                // OnEnable must refresh an empty string, not only a null string.
                host.SetActive(true);
                // Ordinary MonoBehaviours do not receive runtime callbacks in an EditMode fixture.
                typeof(JevTransport).GetMethod("OnEnable", flags).Invoke(transport, null);
                Assert.That(transport.IsConfigured, Is.True);

                var ticketType = typeof(JevTransport).GetNestedType("Ticket", BindingFlags.NonPublic);
                var ticket = Activator.CreateInstance(ticketType, true);
                ticketType.GetField("Id").SetValue(ticket, 1001L);
                ticketType.GetField("Attempts").SetValue(ticket, 1);
                ticketType.GetField("Credential").SetValue(ticket, fixtureKey);
                ticketType.GetField("Request").SetValue(ticket, JevPromptBuilder.BuildUnit(
                    new JObject { ["localFixture"] = true, ["echo"] = fixtureKey },
                    new Dictionary<string, string> { { "wait", "Offline ledger fixture only." } }));

                // Exercise the same actual writer after Unity has restored path="".
                // This is a diagnostic attempt record, with no response or HTTP call.
                typeof(JevTransport).GetMethod("WriteAttempt", flags).Invoke(transport,
                    new object[] { ticket, null, 0L, "offline_reload_regression", 0d, null });
                createdLedger = transport.LedgerPath;
                Assert.That(transport.FlushLedgerAsync().Wait(TimeSpan.FromSeconds(5)), Is.True, "Only this offline regression waits for diagnostic IO.");
                Assert.That(createdLedger, Is.Not.Null.And.Not.Empty);
                Assert.That(File.Exists(createdLedger), Is.True);
                string text = File.ReadAllText(createdLedger);
                Assert.That(text, Does.Not.Contain(fixtureKey));
                var record = JObject.Parse(text.Trim());
                Assert.That((long)record["requestId"], Is.EqualTo(1001));
                Assert.That((string)record["error"], Is.EqualTo("offline_reload_regression"));
                Assert.That((string)record["request"]["state"]["echo"], Is.EqualTo("[REDACTED]"));
                Assert.That(transport.Pending, Is.Zero);
                Assert.That(transport.InFlight, Is.Zero);
                Assert.That(transport.Counters.Attempts, Is.Zero, "This regression must never use the network.");
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                Environment.SetEnvironmentVariable("JEV_API_KEY", oldEnvironment);
                if (!string.IsNullOrEmpty(createdLedger) && File.Exists(createdLedger)) File.Delete(createdLedger);
            }
        }
    }
}
