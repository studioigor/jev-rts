using System;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>Pure CPU work on owned JSON snapshots. Never reads Unity objects or selects an action.</summary>
    public static class JevTransportWork
    {
        // Twenty simultaneous actors must not saturate every core with JSON/regex work.
        static readonly SemaphoreSlim CpuSlots = new SemaphoreSlim(2, 2);

        public sealed class PreparedRequest
        {
            public JObject Request;
            public byte[] Payload;
            public string ErrorCode;
            public string ErrorMessage;
            public double PreparationMilliseconds;
        }

        public sealed class ParsedResponse
        {
            public JObject Response;
            public JevResult Validated;
            public JObject FailureResponse;
            public string NonJsonResponse;
        }

        public static Task<PreparedRequest> PrepareAsync(Func<JObject> ownedRequestFactory, string model, CancellationToken cancellation)
            => RunAsync(() =>
            {
                long started = Stopwatch.GetTimestamp();
                var request = ownedRequestFactory();
                string error = JevResponseValidator.ValidateRequest(request);
                if (error != null) return new PreparedRequest
                { ErrorCode = "invalid_request", ErrorMessage = error, PreparationMilliseconds = ElapsedMilliseconds(started) };
                var compact = JevRequestCompaction.Compact(request);
                compact["model"] = model;
                var payload = Encoding.UTF8.GetBytes(compact.ToString(Formatting.None));
                return new PreparedRequest { Request = compact, Payload = payload, PreparationMilliseconds = ElapsedMilliseconds(started) };
            }, cancellation);

        public static Task<ParsedResponse> ParseAsync(byte[] bytes, string credential, JObject submittedRequest, bool httpSuccess)
            => RunAsync(() =>
            {
                string raw = JevCredentials.Redact(bytes == null ? null : Encoding.UTF8.GetString(bytes), credential);
                JObject response = null;
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try { response = JObject.Parse(raw, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
                    catch (JsonException) { }
                }
                if (response != null) response = (JObject)JevCredentials.RedactedCopy(response, credential);
                return new ParsedResponse
                {
                    Response = response,
                    Validated = httpSuccess ? JevResponseValidator.Validate(submittedRequest, response) : null,
                    FailureResponse = !httpSuccess && response != null ? (JObject)response.DeepClone() : null,
                    NonJsonResponse = response == null ? raw : null
                };
            }, CancellationToken.None);

        static double ElapsedMilliseconds(long started) => (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

        static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellation)
            => Task.Run(async () =>
            {
                await CpuSlots.WaitAsync(cancellation).ConfigureAwait(false);
                try { cancellation.ThrowIfCancellationRequested(); return work(); }
                finally { CpuSlots.Release(); }
            }, cancellation);
    }

    /// <summary>Serial, nonblocking diagnostic writes; one session's rotation cannot race another write.</summary>
    public sealed class JevDiagnosticWorkQueue
    {
        readonly object gate = new object();
        Task tail = Task.CompletedTask;
        int failed;

        public Task Completion { get { lock (gate) return tail; } }
        public bool ConsumeFailure() => Interlocked.Exchange(ref failed, 0) != 0;

        public void Enqueue(Action write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            lock (gate)
                tail = tail.ContinueWith(_ =>
                {
                    try { write(); }
                    catch (Exception) { Interlocked.Exchange(ref failed, 1); }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
