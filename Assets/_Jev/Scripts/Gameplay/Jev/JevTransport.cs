using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Jev.Gameplay.Jev
{
    /// <summary>
    /// One shared, paced transport for unit, tower and commander brains. Unity HTTP and callbacks
    /// stay on the main thread; owned JSON preparation, response parsing and diagnostics use workers.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class JevTransport : MonoBehaviour
    {
        [SerializeField] JevTransportSettings settings = new JevTransportSettings();
        [SerializeField] JevTransportCircuit circuit = new JevTransportCircuit();

        sealed class Ticket
        {
            public long Id;
            public JObject Request;
            public byte[] Payload;
            public Task<JevTransportWork.PreparedRequest> Preparation;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public bool AttemptRecorded;
            public Action<JevResult> Callback;
            public string Credential;
            public UnityWebRequest Http;
            public int Attempts;
            public double EligibleAt;
            public double EnqueuedAt;
            public double PreparationMilliseconds;
            public double StartedAt;
            public bool Completed;
            public bool Urgent;
            public int CircuitGeneration;
            public int CostGeneration;
            public string PricingModel;
            public double InputUsdPerMillion,OutputUsdPerMillion;
        }

        readonly List<Ticket> queued = new List<Ticket>();
        readonly HashSet<Ticket> active = new HashSet<Ticket>();
        readonly Dictionary<long, Ticket> tickets = new Dictionary<long, Ticket>();
        readonly JevTransportCounters counters = new JevTransportCounters();
        readonly JevMatchCost matchCost = new JevMatchCost();
        readonly JevDiagnosticWorkQueue ledgerWrites = new JevDiagnosticWorkQueue();
        long nextRequestId;
        int inFlight;
        double nextStartTime;
        string apiKey;
        string ledgerPath;
        bool closing;
        bool ledgerWarningReported;

        public JevTransportSettings Settings => settings;
        public JevTransportCounters Counters => counters;
        public JevMatchCost MatchCost => matchCost;
        public void BeginMatchCost() => matchCost.BeginSession();
        public int Pending => queued.Count;
        public int InFlight => inFlight;
        public string LastError { get; private set; } = string.Empty;
        public long SuccessCount => counters.SuccessfulEvaluations;
        public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey ?? (apiKey = JevCredentials.ReadKey()));
        public string LedgerPath => ledgerPath;
        /// <summary>Explicit diagnostic/test drain; gameplay must never block waiting for this task.</summary>
        public Task FlushLedgerAsync() => ledgerWrites.Completion;
        public bool IsBlocked => circuit.IsBlocked;
        public string BlockingErrorCode => circuit.ErrorCode;
        public string BlockingErrorMessage => circuit.ErrorMessage;
        public long BlockingHttpStatus => circuit.HttpStatus;
        public JevResult BlockingFailure => circuit.Failure();

        void OnEnable()
        {
            closing = false;
            apiKey = JevCredentials.ReadKey();
            // Component toggles and Unity script reloads must not silently retry a paid/auth failure.
            if (IsBlocked) LastError = BlockingErrorCode + ": " + BlockingErrorMessage;
            else if (IsConfigured) LastError = string.Empty;
        }
        void OnDisable() => CancelAll();
        void OnDestroy() => CancelAll();

        public void ReloadCredentials()
        {
            circuit.Reset();
            apiKey = JevCredentials.ReadKey();
            LastError = string.Empty;
        }

        public void Configure(JevTransportSettings configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            settings = configuration;
        }

        /// <summary>Snapshots the request before queuing. Returns an ID usable by Cancel.</summary>
        public long Evaluate(JObject request, Action<JevResult> callback,bool urgent=false)
        {
            // Preserve the public snapshot contract even when the caller immediately reuses its JSON.
            var snapshot = request == null ? null : (JObject)request.DeepClone();
            return EvaluatePrepared(() => snapshot, callback, urgent);
        }

        /// <summary>
        /// Prepares an exclusively owned observation on a CPU worker. The factory must capture only
        /// immutable data, never Unity objects or live simulation state. Completion callbacks stay main-thread.
        /// </summary>
        public long EvaluatePrepared(Func<JObject> ownedRequestFactory, Action<JevResult> callback, bool urgent=false)
        {
            if (ownedRequestFactory == null) throw new ArgumentNullException(nameof(ownedRequestFactory));
            var ticket = new Ticket { Id = ++nextRequestId, Callback = callback, Urgent=urgent };
            counters.Evaluations++;
            tickets.Add(ticket.Id, ticket);
            if (closing || !isActiveAndEnabled)
                return FailImmediately(ticket, "transport_disabled", "JEV transport is disabled.");
            if (IsBlocked)
            {
                Complete(ticket, circuit.Failure());
                return ticket.Id;
            }
            if (!IsConfigured)
                return FailImmediately(ticket, "missing_credentials", "Set JEV_API_KEY in the environment or LocalSettings/jev.env.");
            if (!ValidEndpoint(settings.Endpoint))
                return FailImmediately(ticket, "invalid_endpoint", "JEV endpoint must use HTTPS or a loopback test address.");
            if (string.IsNullOrWhiteSpace(settings.Model))
                return FailImmediately(ticket, "invalid_model", "JEV model is not configured.");
            // Validation, compaction and encoding are pure work. A Task is only read once completed;
            // Update never waits on it, and retries reuse the exact submitted bytes and choice set.
            ticket.Preparation = JevTransportWork.PrepareAsync(ownedRequestFactory, settings.Model, ticket.Cancellation.Token);
            ticket.Preparation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            ticket.Credential = apiKey;
            ticket.CircuitGeneration = circuit.Generation;
            ticket.CostGeneration=matchCost.Generation;
            ticket.PricingModel=settings.PricingModel;
            ticket.InputUsdPerMillion=settings.InputUsdPerMillion;
            ticket.OutputUsdPerMillion=settings.OutputUsdPerMillion;
            ticket.EligibleAt = Time.realtimeSinceStartupAsDouble;
            Enqueue(ticket);
            return ticket.Id;
        }

        void Enqueue(Ticket ticket)
        {
            ticket.EnqueuedAt = Time.realtimeSinceStartupAsDouble;
            InsertQueued(ticket);
        }

        void InsertQueued(Ticket ticket)
        {
            // Commands retain FIFO order with other commands, ahead of routine reviews.
            // They still use the same concurrency, request-rate and retry-delay limits.
            int firstRoutine=ticket.Urgent?queued.FindIndex(other=>!other.Urgent):-1;
            if(firstRoutine<0)queued.Add(ticket);else queued.Insert(firstRoutine,ticket);
        }

        long FailImmediately(Ticket ticket, string code, string message)
        {
            Complete(ticket, JevResult.Failure(code, message));
            return ticket.Id;
        }

        /// <summary>
        /// Promotes a waiting review after a new order/contact/hit without spending another call.
        /// Active HTTP is preserved; repeated promotion never changes the existing urgent FIFO.
        /// </summary>
        public bool Prioritize(long requestId)
        {
            if (!tickets.TryGetValue(requestId, out var ticket) || ticket.Completed || !queued.Contains(ticket)) return false;
            if (ticket.Urgent) return true;
            queued.Remove(ticket);
            ticket.Urgent = true;
            InsertQueued(ticket);
            return true;
        }

        public bool Cancel(long requestId)
        {
            if (!tickets.TryGetValue(requestId, out Ticket ticket) || ticket.Completed) return false;
            queued.Remove(ticket);
            ticket.Http?.Abort();
            ticket.Cancellation.Cancel();
            Complete(ticket, JevResult.Failure("cancelled", "The JEV request was cancelled."));
            return true;
        }

        public void CancelAll()
        {
            if (closing && tickets.Count == 0) return;
            closing = true;
            var union = new HashSet<Ticket>(tickets.Values);
            union.UnionWith(active);
            var remaining = new List<Ticket>(union);
            queued.Clear();
            foreach (var ticket in remaining)
            {
                ticket.Http?.Abort();
                // A received response can still be in worker parsing. If the component is torn
                // down before it finishes, retain an explicit unknown-usage attempt, not a free call.
                if (active.Contains(ticket) && !ticket.AttemptRecorded)
                    RecordAttempt(ticket, null, 0, "cancelled", Time.realtimeSinceStartupAsDouble - ticket.StartedAt);
                ticket.Cancellation.Cancel();
                Complete(ticket, JevResult.Failure("cancelled", "The JEV request was cancelled."));
            }
            StopAllCoroutines();
            foreach (var ticket in remaining) { ticket.Http?.Dispose(); ticket.Http = null; }
            active.Clear();
            inFlight = 0;
            // Calling CancelAll between matches does not require toggling this component.
            closing = !isActiveAndEnabled;
        }

        void Update()
        {
            if (ledgerWrites.ConsumeFailure()) ReportLedgerFailure();
            if (IsBlocked) { DrainBlockedQueue(); return; }
            if (closing || queued.Count == 0 || inFlight >= Mathf.Clamp(settings.MaxConcurrent, 1, 16)) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < nextStartTime) return;
            for (int index = 0; index < queued.Count; index++)
            {
                Ticket ticket = queued[index];
                if (ticket.Completed || ticket.EligibleAt > now || !ticket.Preparation.IsCompleted) continue;
                if (ticket.Payload == null)
                {
                    if (ticket.Preparation.IsFaulted || ticket.Preparation.IsCanceled)
                    {
                        // Observe without exposing arbitrary exception messages or request data.
                        _ = ticket.Preparation.Exception;
                        queued.RemoveAt(index);
                        Complete(ticket, JevResult.Failure("request_preparation_error", "The JEV request could not be prepared."));
                        return;
                    }
                    var prepared = ticket.Preparation.Result;
                    if (prepared.ErrorCode != null)
                    {
                        queued.RemoveAt(index);
                        Complete(ticket, JevResult.Failure(prepared.ErrorCode, prepared.ErrorMessage));
                        return;
                    }
                    ticket.Request = prepared.Request;
                    ticket.Payload = prepared.Payload;
                    ticket.PreparationMilliseconds = prepared.PreparationMilliseconds;
                }
                queued.RemoveAt(index);
                // Reserve from actual launch time: missed frames cannot cause catch-up bursts.
                nextStartTime = now + 60.0 / Mathf.Clamp(settings.RequestsPerMinute, 1, 1200);
                inFlight++;
                active.Add(ticket);
                StartCoroutine(Send(ticket));
                break;
            }
        }

        IEnumerator Send(Ticket ticket)
        {
            ticket.Attempts++;
            ticket.AttemptRecorded = false;
            counters.Attempts++;
            if (ticket.Attempts > 1) counters.Retries++;
            ticket.StartedAt = Time.realtimeSinceStartupAsDouble;
            UnityWebRequest http = null;
            UnityWebRequestAsyncOperation operation = null;
            try
            {
                http = new UnityWebRequest(settings.Endpoint, "POST");
                ticket.Http = http;
                http.uploadHandler = new UploadHandlerRaw(ticket.Payload);
                http.downloadHandler = new DownloadHandlerBuffer();
                http.timeout = Mathf.Clamp(settings.TimeoutSeconds, 1, 120);
                http.redirectLimit = 0;
                http.SetRequestHeader("Authorization", "Bearer " + ticket.Credential);
                http.SetRequestHeader("Content-Type", "application/json");
                http.SetRequestHeader("Accept", "application/json");
                operation = http.SendWebRequest();
            }
            catch (Exception)
            {
                // A malformed runtime configuration must not strand the semaphore or expose a header.
            }
            if (operation == null)
            {
                inFlight = Math.Max(0, inFlight - 1);
                active.Remove(ticket);
                WriteAttempt(ticket, null, 0, "request_start_error", Time.realtimeSinceStartupAsDouble - ticket.StartedAt);
                http?.Dispose();
                ticket.Http = null;
                Complete(ticket, JevResult.Failure("request_start_error", "The JEV HTTP request could not be started."));
                yield break;
            }
            yield return operation;

            inFlight = Math.Max(0, inFlight - 1);
            long status = http.responseCode;
            double duration = Time.realtimeSinceStartupAsDouble - ticket.StartedAt;
            counters.LastLatencyMilliseconds = duration * 1000;
            bool httpSuccess = http.result == UnityWebRequest.Result.Success && status >= 200 && status < 300;
            double retryDelay = RetryDelay(http.GetResponseHeader("Retry-After"), ticket.Attempts);
            // Download bytes are the sole Unity-owned data copy. Parsing/redaction/validation do not
            // touch the UnityWebRequest, component, counters or simulation while on the worker.
            var parsing = JevTransportWork.ParseAsync(http.downloadHandler?.data, ticket.Credential, ticket.Request, httpSuccess);
            parsing.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            ticket.Http = null;
            http.Dispose();
            while (!parsing.IsCompleted) yield return null;
            JevTransportWork.ParsedResponse parsed;
            if (parsing.IsFaulted || parsing.IsCanceled)
            {
                _ = parsing.Exception;
                parsed = new JevTransportWork.ParsedResponse
                { Validated = JevResult.Failure("invalid_decision", "The JEV response could not be processed.") };
            }
            else parsed = parsing.Result;
            JObject response = parsed.Response;
            counters.InputTokens += JevResponseValidator.UsageCount(response, "input_tokens");
            counters.OutputTokens += JevResponseValidator.UsageCount(response, "output_tokens");
            if (httpSuccess) counters.SuccessfulAttempts++;
            JevResult validated = parsed.Validated;
            string code = ticket.Completed ? "cancelled" : httpSuccess ? validated.ErrorCode : status > 0 ? "http_" + status : "network_error";
            RecordAttempt(ticket, response, status, code, duration, parsed.NonJsonResponse);
            active.Remove(ticket);
            ObserveBlockingStatus(status, ticket.CircuitGeneration);
            if (ticket.Completed) yield break;

            if (httpSuccess)
            {
                validated.HttpStatus = status;
                Complete(ticket, validated);
                yield break;
            }

            bool retryable = status == 0 || status == 429 || status >= 500;
            if (retryable && ticket.Attempts < Mathf.Clamp(settings.MaxAttempts, 1, 2) && !closing && !IsBlocked)
            {
                ticket.EligibleAt = Time.realtimeSinceStartupAsDouble + retryDelay;
                Enqueue(ticket);
                yield break;
            }
            var failure = IsBlocked ? circuit.Failure() : JevResult.Failure(code, status == 401 || status == 403
                ? "JEV authentication was rejected. Check the local API credential."
                : status == 429 ? "JEV request limit was reached; the unit is waiting for its next decision."
                : "JEV did not return a usable response; no action was invented.");
            if (!IsBlocked) failure.HttpStatus = status;
            failure.Raw = parsed.FailureResponse;
            Complete(ticket, failure);
        }

        void ObserveBlockingStatus(long status, int generation)
        {
            if (!circuit.Observe(status, generation)) return;
            LastError = BlockingErrorCode + ": " + BlockingErrorMessage;
            DrainBlockedQueue();
        }

        void RecordAttempt(Ticket ticket, JObject response, long status, string code, double duration, string nonJsonResponse = null)
        {
            if (ticket.AttemptRecorded) return;
            ticket.AttemptRecorded = true;
            RecordCost(ticket, response);
            WriteAttempt(ticket, response, status, code, duration, nonJsonResponse);
        }

        void RecordCost(Ticket ticket,JObject response)
        {
            matchCost.RecordAttempt(ticket.CostGeneration,response,(string)ticket.Request?["model"],ticket.PricingModel,
                ticket.InputUsdPerMillion,ticket.OutputUsdPerMillion);
        }

        void DrainBlockedQueue()
        {
            if (!IsBlocked || queued.Count == 0) return;
            var remaining = queued.ToArray();
            queued.Clear();
            foreach (var ticket in remaining)
            {
                ticket.Cancellation.Cancel();
                Complete(ticket, circuit.Failure());
            }
        }

        void Complete(Ticket ticket, JevResult result)
        {
            if (ticket.Completed) return;
            ticket.Completed = true;
            tickets.Remove(ticket.Id);
            result.RequestId = ticket.Id;
            result.Attempts = ticket.Attempts;
            if (result.Success)
            {
                counters.SuccessfulEvaluations++;
                if (!IsBlocked) LastError = string.Empty;
            }
            else
            {
                if (result.ErrorCode == "cancelled") counters.CancelledEvaluations++;
                else
                {
                    counters.FailedEvaluations++;
                    LastError = IsBlocked ? BlockingErrorCode + ": " + BlockingErrorMessage : result.ErrorCode + ": " + result.ErrorMessage;
                }
            }
            if (ticket.Callback == null) return;
            try { ticket.Callback(result); }
            catch (Exception exception)
            {
                // Callback messages can contain arbitrary caller data; do not echo them or a request body.
                Debug.LogError("JEV result callback failed (" + exception.GetType().Name + ").", this);
            }
        }

        void WriteAttempt(Ticket ticket, JObject response, long status, string code, double duration, string nonJsonResponse = null)
        {
            if (!settings.WriteSessionLedger) return;
            try
            {
                if (string.IsNullOrWhiteSpace(ledgerPath))
                {
                    // Application paths are Unity APIs: resolve the path here; actual IO stays on the worker.
                    string directory = Path.Combine(JevCredentials.LocalSettingsDirectory, "Sessions");
                    string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    ledgerPath = Path.Combine(directory, name + ".jsonl");
                }
                string path = ledgerPath, credential = ticket.Credential;
                long id = ticket.Id, limit = (long)Mathf.Clamp(settings.MaxLedgerMegabytes, 1, 128) * 1024 * 1024;
                int attempt = ticket.Attempts;
                double queueWaitMs = Math.Max(0, ticket.StartedAt - Math.Max(ticket.EnqueuedAt, ticket.EligibleAt)) * 1000;
                double preparationMs = ticket.PreparationMilliseconds;
                var request = ticket.Request; // Immutable, owned submitted JSON; callbacks receive a separate response clone.
                string completedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                ledgerWrites.Enqueue(() =>
                {
                    var entry = new JObject
                    {
                        ["requestId"] = id,
                        ["attempt"] = attempt,
                        ["completedUtc"] = completedUtc,
                        ["latencyMs"] = Math.Round(duration * 1000),
                        ["queueWaitMs"] = Math.Round(queueWaitMs, 3),
                        ["preparationMs"] = Math.Round(preparationMs, 3),
                        ["status"] = status,
                        ["ok"] = code == null,
                        ["error"] = code,
                        ["request"] = JevCredentials.RedactedCopy(request, credential),
                        ["response"] = JevCredentials.RedactedCopy(response, credential),
                        ["nonJsonResponse"] = nonJsonResponse == null ? null : JevCredentials.Redact(nonJsonResponse, credential)
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    AppendLedgerEntry(path, entry, limit);
                });
            }
            catch (Exception) { ReportLedgerFailure(); }
        }

        void ReportLedgerFailure()
        {
            // Diagnostics never interrupt a request. Exception messages may contain secrets/paths.
            if (ledgerWarningReported) return;
            ledgerWarningReported = true;
            Debug.LogWarning("The JEV session ledger could not be written to LocalSettings/Sessions.", this);
        }

        // Only called with an already-redacted record. Paths belong to this transport instance;
        // rotation never enumerates or removes another session's files.
        static void AppendLedgerEntry(string path, JObject entry, long maximumBytes)
        {
            var utf8 = new UTF8Encoding(false);
            byte[] bytes = utf8.GetBytes(entry.ToString(Formatting.None) + "\n");
            if (bytes.LongLength > maximumBytes)
            {
                // A single unusually large prompt must not bypass the disk limit. Keep its request
                // identity and outcome, and make the missing payload explicit in the audit trail.
                var compact = new JObject();
                foreach (var property in entry.Properties())
                    if (property.Name != "request" && property.Name != "response" && property.Name != "nonJsonResponse")
                        compact[property.Name] = property.Value.DeepClone();
                entry = compact;
                entry["payloadOmitted"] = "entry_exceeds_ledger_limit";
                entry["originalUtf8Bytes"] = bytes.LongLength;
                bytes = utf8.GetBytes(entry.ToString(Formatting.None) + "\n");
                if (bytes.LongLength > maximumBytes) return;
            }
            if (File.Exists(path) && new FileInfo(path).Length + bytes.LongLength > maximumBytes)
            {
                string previous = Path.ChangeExtension(path, "previous.jsonl");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
        }

        static bool ValidEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        }

        static double RetryDelay(string header, int attempt)
        {
            double delay = .5 * Math.Pow(2, attempt - 1);
            if (double.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
                !double.IsNaN(seconds) && !double.IsInfinity(seconds)) return Math.Max(delay, Math.Max(0, seconds));
            if (DateTimeOffset.TryParse(header, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
                return Math.Max(delay, Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds));
            return delay;
        }
    }

    /// <summary>Account failures latch until explicit retry. Does not send HTTP or choose an action.</summary>
    [Serializable]
    public sealed class JevTransportCircuit
    {
        [SerializeField] long status;
        [SerializeField] int generation;
        public int Generation => generation;
        public long HttpStatus => status;
        public bool IsBlocked => status != 0;
        public string ErrorCode => IsBlocked ? "http_" + status : string.Empty;
        public string ErrorMessage => status == 402
            ? "Закончились кредиты TypeSafe. Пополните баланс и начните новый матч через меню. Текущий матч приостановлен."
            : status == 401 ? "TypeSafe отклонил ключ API. После исправления ключа начните новый матч через меню."
            : status == 403 ? "TypeSafe запретил доступ. После исправления прав ключа API начните новый матч через меню."
            : string.Empty;

        public bool Observe(long httpStatus, int requestGeneration)
        {
            if (requestGeneration != generation || (httpStatus != 401 && httpStatus != 402 && httpStatus != 403)) return false;
            if (!IsBlocked) status = httpStatus;
            return true;
        }

        public void Reset() { status = 0; generation++; }

        public JevResult Failure()
        {
            if (!IsBlocked) return null;
            var result = JevResult.Failure(ErrorCode, ErrorMessage);
            result.HttpStatus = status;
            return result;
        }
    }
}
