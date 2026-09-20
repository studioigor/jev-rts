using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Jev.Tests
{
    /// <summary>Queued identity/priority checks only. No credential lookup, request preparation or HTTP.</summary>
    public sealed class JevTransportPriorityTests
    {
        const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        GameObject host;
        JevTransport transport;
        Type ticketType;
        IList queue;
        IDictionary tickets;

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("Offline transport scheduling fixture");
            host.SetActive(false);
            transport = host.AddComponent<JevTransport>();
            ticketType = typeof(JevTransport).GetNestedType("Ticket", BindingFlags.NonPublic);
            queue = (IList)typeof(JevTransport).GetField("queued", PrivateInstance).GetValue(transport);
            tickets = (IDictionary)typeof(JevTransport).GetField("tickets", PrivateInstance).GetValue(transport);
        }

        [TearDown]
        public void TearDown() { if (host) UnityEngine.Object.DestroyImmediate(host); }

        object Ticket(long id, bool urgent = false, bool queued = true)
        {
            object ticket = Activator.CreateInstance(ticketType, true);
            ticketType.GetField("Id").SetValue(ticket, id);
            ticketType.GetField("Urgent").SetValue(ticket, urgent);
            ticketType.GetField("EnqueuedAt").SetValue(ticket, 12.5d);
            ticketType.GetField("Request").SetValue(ticket, new JObject { ["fixture"] = true });
            tickets.Add(id, ticket);
            if (queued) typeof(JevTransport).GetMethod("InsertQueued", PrivateInstance).Invoke(transport, new[] { ticket });
            return ticket;
        }

        long[] QueueIds()
        {
            var ids = new long[queue.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = (long)ticketType.GetField("Id").GetValue(queue[i]);
            return ids;
        }

        [Test]
        public void TacticalPromotionPreservesTicketSnapshotAndExistingUrgentFifo()
        {
            Ticket(1); Ticket(2, true); object promoted = Ticket(3);
            object snapshot = ticketType.GetField("Request").GetValue(promoted);
            Assert.That(transport.Prioritize(3), Is.True);
            CollectionAssert.AreEqual(new long[] { 2, 3, 1 }, QueueIds());
            Assert.That(ticketType.GetField("Request").GetValue(promoted), Is.SameAs(snapshot));
            Assert.That((double)ticketType.GetField("EnqueuedAt").GetValue(promoted), Is.EqualTo(12.5d));
            Assert.That(ticketType.GetField("Preparation").GetValue(promoted), Is.Null);
            Assert.That(transport.Counters.Evaluations, Is.Zero);
            Assert.That(transport.Counters.Attempts, Is.Zero);
        }

        [Test]
        public void RepeatedHitsDoNotMoveAnUrgentReviewBackwardsInItsQueue()
        {
            Ticket(1, true); Ticket(2, true); Ticket(3);
            for (int i = 0; i < 10; i++) Assert.That(transport.Prioritize(1), Is.True);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, QueueIds());
            Assert.That(transport.Counters.CancelledEvaluations, Is.Zero);
        }

        [Test]
        public void ActiveCompletedAndUnknownTicketsAreNotResentOrRequeued()
        {
            Ticket(1, queued: false); object complete = Ticket(2);
            ticketType.GetField("Completed").SetValue(complete, true);
            Assert.That(transport.Prioritize(1), Is.False);
            Assert.That(transport.Prioritize(2), Is.False);
            Assert.That(transport.Prioritize(999), Is.False);
            CollectionAssert.AreEqual(new long[] { 2 }, QueueIds());
            Assert.That(transport.Counters.Attempts, Is.Zero);
        }
    }
}
