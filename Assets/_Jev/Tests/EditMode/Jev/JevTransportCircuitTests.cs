using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Jev.Tests
{
    /// <summary>Local failure-state tests only. No fixture launches HTTP or supplies gameplay decisions.</summary>
    public sealed class JevTransportCircuitTests
    {
        [TestCase(401)]
        [TestCase(402)]
        [TestCase(403)]
        public void AccountFailureLatchesUntilExplicitReset(int status)
        {
            var circuit=new JevTransportCircuit();
            Assert.That(circuit.Observe(status,circuit.Generation),Is.True);
            Assert.That(circuit.IsBlocked,Is.True);
            Assert.That(circuit.ErrorCode,Is.EqualTo("http_"+status));
            Assert.That(circuit.Failure().HttpStatus,Is.EqualTo(status));
            Assert.That(circuit.Failure().Choice,Is.Null);
            // Later success, another terminal reply, and transient failures cannot erase/change the first blocker.
            circuit.Observe(200,circuit.Generation);
            circuit.Observe(429,circuit.Generation);
            circuit.Observe(status==402?401:402,circuit.Generation);
            Assert.That(circuit.HttpStatus,Is.EqualTo(status));
            var exposed=circuit.Failure();exposed.ErrorCode="caller_mutation";
            Assert.That(circuit.ErrorCode,Is.EqualTo("http_"+status));
            int oldGeneration=circuit.Generation;
            circuit.Reset();
            Assert.That(circuit.IsBlocked,Is.False);
            Assert.That(circuit.Failure(),Is.Null);
            Assert.That(circuit.Observe(status,oldGeneration),Is.False,"A late old-credential failure cannot undo an explicit retry.");
            Assert.That(circuit.IsBlocked,Is.False);
        }

        [Test]
        public void NetworkRateLimitAndServerFailuresNeverLatch()
        {
            var circuit=new JevTransportCircuit();
            foreach(long status in new long[]{0,200,400,404,408,429,500,502,503,504})
            {
                Assert.That(circuit.Observe(status,circuit.Generation),Is.False);
                Assert.That(circuit.IsBlocked,Is.False);
            }
        }

        [Test]
        public void BlockDrainsQueueAndFutureCallsWithoutHttpAndCallbacksRunOnce()
        {
            WithTransport(transport=>
            {
                var callbacks=new List<JevResult>();
                long first=transport.Evaluate(Request(),callbacks.Add);
                long second=transport.Evaluate(Request(),callbacks.Add);
                long cancelled=transport.Evaluate(Request(),callbacks.Add);
                Assert.That(transport.Cancel(cancelled),Is.True);
                Assert.That(callbacks.Count,Is.EqualTo(1));
                Trip(transport,402);
                Assert.That(transport.IsBlocked,Is.True);
                Assert.That(transport.Pending,Is.Zero);
                Assert.That(transport.InFlight,Is.Zero);
                Assert.That(callbacks.Count,Is.EqualTo(3));
                Assert.That(callbacks[1].ErrorCode,Is.EqualTo("http_402"));
                Assert.That(callbacks[2].ErrorCode,Is.EqualTo("http_402"));
                Assert.That(callbacks[1].HttpStatus,Is.EqualTo(402));
                Assert.That(callbacks[1].Attempts,Is.Zero);
                Assert.That(transport.Cancel(first),Is.False);
                Assert.That(transport.Cancel(second),Is.False);
                transport.Evaluate(Request(),callbacks.Add);
                Assert.That(callbacks.Count,Is.EqualTo(4));
                Assert.That(callbacks[3].ErrorCode,Is.EqualTo("http_402"));
                transport.CancelAll();
                Assert.That(callbacks.Count,Is.EqualTo(4));
                Assert.That(transport.IsBlocked,Is.True,"Cancellation is not authorization to retry the service.");
                typeof(JevTransport).GetMethod("OnEnable",Flags).Invoke(transport,null);
                Assert.That(transport.IsBlocked,Is.True,"Toggling a component must preserve the blocker.");
                Assert.That(transport.Counters.Attempts,Is.Zero);
                transport.ReloadCredentials();
                Assert.That(transport.IsBlocked,Is.False);
                long retry=transport.Evaluate(Request(),callbacks.Add);
                Assert.That(transport.Pending,Is.EqualTo(1));
                Assert.That(transport.Cancel(retry),Is.True);
                Assert.That(transport.Counters.Attempts,Is.Zero);
            });
        }

        [Test]
        public void NewCommandsQueueAheadOfRoutineReviewsWithoutBreakingCommandOrderOrSendingHttp()
        {
            WithTransport(transport=>
            {
                long routine=transport.Evaluate(Request(),null);
                long first=transport.Evaluate(Request(),null,true);
                long second=transport.Evaluate(Request(),null,true);
                var queued=(IList)typeof(JevTransport).GetField("queued",Flags).GetValue(transport);
                long Id(int index)=>(long)queued[index].GetType().GetField("Id").GetValue(queued[index]);
                Assert.That(new[]{Id(0),Id(1),Id(2)},Is.EqualTo(new[]{first,second,routine}));
                Assert.That(transport.InFlight,Is.Zero);Assert.That(transport.Counters.Attempts,Is.Zero);
                transport.CancelAll();
            });
        }

        [Test]
        public void AlreadyActiveSuccessDoesNotClearBlockOrRunCallbackTwice()
        {
            WithTransport(transport=>
            {
                var callbacks=new List<JevResult>();
                long id=transport.Evaluate(Request(),callbacks.Add);
                var queued=(IList)typeof(JevTransport).GetField("queued",Flags).GetValue(transport);
                object ticket=queued[0];queued.RemoveAt(0); // Local lifecycle stand-in; no coroutine or HTTP is started.
                Trip(transport,403);
                var complete=typeof(JevTransport).GetMethod("Complete",Flags);
                complete.Invoke(transport,new[]{ticket,new JevResult{Success=true}});
                complete.Invoke(transport,new[]{ticket,JevResult.Failure("cancelled","Local duplicate completion")});
                Assert.That(callbacks.Count,Is.EqualTo(1));
                Assert.That(callbacks[0].Success,Is.True);
                Assert.That(transport.SuccessCount,Is.EqualTo(1));
                Assert.That(transport.IsBlocked,Is.True);
                Assert.That(transport.LastError,Does.StartWith("http_403:"));
                Assert.That(transport.Cancel(id),Is.False);
                Assert.That(transport.Counters.Attempts,Is.Zero);
            });
        }

        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
        static JObject Request()=>JevPromptBuilder.BuildUnit(new JObject{["fixture"]="circuit_lifecycle_no_http"},
            new Dictionary<string,string>{{"wait","Local test; this request must not be sent."}});
        static void Trip(JevTransport transport,long status)
        {
            var circuit=(JevTransportCircuit)typeof(JevTransport).GetField("circuit",Flags).GetValue(transport);
            typeof(JevTransport).GetMethod("ObserveBlockingStatus",Flags).Invoke(transport,new object[]{status,circuit.Generation});
        }
        static void WithTransport(Action<JevTransport> test)
        {
            string previous=Environment.GetEnvironmentVariable("JEV_API_KEY");GameObject host=null;
            try
            {
                Environment.SetEnvironmentVariable("JEV_API_KEY","offline-circuit-fixture-never-sent");
                host=new GameObject("Offline JEV circuit regression");
                var transport=host.AddComponent<JevTransport>();
                transport.Configure(new JevTransportSettings{WriteSessionLedger=false});
                transport.ReloadCredentials();
                test(transport);
            }
            finally
            {
                if(host!=null)UnityEngine.Object.DestroyImmediate(host);
                Environment.SetEnvironmentVariable("JEV_API_KEY",previous);
            }
        }
    }
}
