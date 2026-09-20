using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    /// <summary>Pure offline scheduling/wire regressions. No HTTP call and no invented gameplay choice.</summary>
    public sealed class JevTransportWorkTests
    {
        static JObject Request() => new JObject
        {
            ["model"] = "fixture",
            ["state"] = new JObject { ["self"] = new JObject { ["x"] = 5, ["y"] = 6 }, ["note"] = "Дерево" },
            ["questions"] = new JObject
            {
                ["action"] = new JObject
                {
                    ["type"] = "choice", ["criteria"] = new JObject { ["wait"] = "Wait.", ["attack_enemy"] = "Attack once." }
                }
            }
        };

        static T Finished<T>(Task<T> task)
        {
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True);
            return task.Result;
        }

        [Test]
        public void PreparationRunsOutsideCallerAndPreservesExactWirePayload()
        {
            var source = Request(); var original = source.DeepClone();
            int caller = Thread.CurrentThread.ManagedThreadId, worker = caller;
            var result = Finished(JevTransportWork.PrepareAsync(() =>
            { worker = Thread.CurrentThread.ManagedThreadId; return source; }, "configured-model", CancellationToken.None));
            Assert.That(worker, Is.Not.EqualTo(caller));
            var expected = JevRequestCompaction.Compact(source); expected["model"] = "configured-model";
            Assert.That(result.ErrorCode, Is.Null);
            Assert.That(JToken.DeepEquals(source, original), Is.True);
            Assert.That(JToken.DeepEquals(result.Request, expected), Is.True);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(expected.ToString(Formatting.None)), result.Payload);
        }

        [Test]
        public void InvalidRequestReturnsLocalFailureWithoutPayload()
        {
            var result = Finished(JevTransportWork.PrepareAsync(() => new JObject(), "fixture", CancellationToken.None));
            Assert.That(result.ErrorCode, Is.EqualTo("invalid_request"));
            Assert.That(result.Payload, Is.Null);
        }

        [Test]
        public void CancelledPreparationNeverCallsItsSnapshotFactory()
        {
            var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool invoked = false;
            var task = JevTransportWork.PrepareAsync(() => { invoked = true; return Request(); }, "fixture", cancellation.Token);
            Assert.That(SpinWait.SpinUntil(() => task.IsCompleted, 5000), Is.True);
            Assert.That(task.IsCanceled, Is.True);
            Assert.That(invoked, Is.False);
            cancellation.Dispose();
        }

        [Test]
        public void ResponseWorkerKeepsProviderChoiceAndUsageWhileRedactingSecrets()
        {
            const string secret = "offline-secret-never-sent";
            var response = new JObject
            {
                ["model"] = "fixture", ["usage"] = new JObject { ["input_tokens"] = 921, ["output_tokens"] = 17 },
                ["echo"] = secret, ["apiKey"] = "different-secret",
                ["answers"] = new JObject { ["action"] = new JObject
                    { ["type"] = "choice", ["choice"] = "attack_enemy", ["probabilities"] = new JObject { ["wait"] = .8, ["attack_enemy"] = .2 } } }
            };
            var result = Finished(JevTransportWork.ParseAsync(Encoding.UTF8.GetBytes(response.ToString()), secret, Request(), true));
            Assert.That(result.Validated.Success, Is.True);
            Assert.That(result.Validated.Choice, Is.EqualTo("attack_enemy"), "Do not substitute argmax for the actual JEV choice.");
            Assert.That(result.Validated.InputTokens, Is.EqualTo(921));
            Assert.That(result.Validated.OutputTokens, Is.EqualTo(17));
            Assert.That(result.Response.ToString(), Does.Not.Contain(secret).And.Not.Contain("different-secret"));
            Assert.That(result.Validated.Raw.ToString(), Does.Not.Contain(secret));
            Assert.That(ReferenceEquals(result.Response, result.Validated.Raw), Is.False, "Callbacks cannot mutate the worker-owned ledger response.");
        }

        [Test]
        public void HttpErrorCallbackCannotMutateTheDiagnosticResponse()
        {
            var result = Finished(JevTransportWork.ParseAsync(Encoding.UTF8.GetBytes("{\"error\":\"rejected\"}"), null, Request(), false));
            result.FailureResponse["error"] = "changed by caller";
            Assert.That((string)result.Response["error"], Is.EqualTo("rejected"));
            Assert.That(result.Validated, Is.Null);
        }

        [Test]
        public void DuplicateJsonAndNonJsonResponsesCannotBecomeSuccessfulDecisions()
        {
            const string malformed = "{\"answers\":{},\"answers\":{}}";
            var duplicate = Finished(JevTransportWork.ParseAsync(Encoding.UTF8.GetBytes(malformed), null, Request(), true));
            Assert.That(duplicate.Validated.Success, Is.False);
            Assert.That(duplicate.Response, Is.Null);
            var nonJson = Finished(JevTransportWork.ParseAsync(Encoding.UTF8.GetBytes("rejected fixture-secret"), "fixture-secret", Request(), false));
            Assert.That(nonJson.NonJsonResponse, Is.EqualTo("rejected [REDACTED]"));
            Assert.That(nonJson.Validated, Is.Null);
        }

        [Test]
        public void DiagnosticQueueIsOffThreadOrderedAndRecoversAfterAnIoFailure()
        {
            var queue = new JevDiagnosticWorkQueue(); var order = new List<int>();
            int caller = Thread.CurrentThread.ManagedThreadId; var workerThreads = new List<int>();
            for (int index = 0; index < 20; index++)
            {
                int sequence = index;
                queue.Enqueue(() => { order.Add(sequence); workerThreads.Add(Thread.CurrentThread.ManagedThreadId); });
                if (index == 8) queue.Enqueue(() => { throw new System.IO.IOException("fixture"); });
            }
            Assert.That(queue.Completion.Wait(TimeSpan.FromSeconds(5)), Is.True);
            CollectionAssert.AreEqual(System.Linq.Enumerable.Range(0, 20), order);
            Assert.That(workerThreads, Has.None.EqualTo(caller));
            Assert.That(queue.ConsumeFailure(), Is.True);
            Assert.That(queue.ConsumeFailure(), Is.False);
        }
    }
}
