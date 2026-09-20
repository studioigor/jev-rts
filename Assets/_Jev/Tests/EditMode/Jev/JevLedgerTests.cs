using System;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    public sealed class JevLedgerTests
    {
        static void Append(string path, JObject entry, long limit)
        {
            typeof(JevTransport).GetMethod("AppendLedgerEntry", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { path, entry, limit });
        }

        [Test]
        public void LedgerRotatesOnePreviousFileAndKeepsRedactedUnicodeRecordsWithinByteLimit()
        {
            string directory = Path.Combine(Path.GetTempPath(), "jev-ledger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "test.jsonl");
                const string key = "offline-secret-fixture";
                var source = new JObject { ["requestId"] = 1, ["request"] = new JObject { ["text"] = new string('Ж', 200), ["echo"] = key } };
                var record = (JObject)JevCredentials.RedactedCopy(source, key);
                long limit = Encoding.UTF8.GetByteCount(record.ToString(Newtonsoft.Json.Formatting.None) + "\n") + 1;
                for (int i = 1; i <= 5; i++) { record["requestId"] = i; Append(path, record, limit); }
                string previous = Path.ChangeExtension(path, "previous.jsonl");
                Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(2));
                Assert.That(new FileInfo(path).Length, Is.LessThanOrEqualTo(limit));
                Assert.That(new FileInfo(previous).Length, Is.LessThanOrEqualTo(limit));
                Assert.That((int)JObject.Parse(File.ReadAllText(path))["requestId"], Is.EqualTo(5));
                Assert.That((int)JObject.Parse(File.ReadAllText(previous))["requestId"], Is.EqualTo(4));
                Assert.That(File.ReadAllText(path) + File.ReadAllText(previous), Does.Not.Contain(key));
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void OversizedLedgerEntryRetainsOutcomeAndExplicitlyOmitsItsPayload()
        {
            string directory = Path.Combine(Path.GetTempPath(), "jev-ledger-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "test.jsonl");
                var record = new JObject { ["requestId"] = 42, ["status"] = 200, ["ok"] = true, ["request"] = new JObject { ["state"] = new string('x', 4096) } };
                Append(path, record, 512);
                var retained = JObject.Parse(File.ReadAllText(path));
                Assert.That(new FileInfo(path).Length, Is.LessThanOrEqualTo(512));
                Assert.That((int)retained["requestId"], Is.EqualTo(42));
                Assert.That((bool)retained["ok"], Is.True);
                Assert.That((string)retained["payloadOmitted"], Is.EqualTo("entry_exceeds_ledger_limit"));
                Assert.That((long)retained["originalUtf8Bytes"], Is.GreaterThan(4096));
                Assert.That(retained["request"], Is.Null);
                Assert.That(record["request"], Is.Not.Null, "Journaling must not alter the submitted request.");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
