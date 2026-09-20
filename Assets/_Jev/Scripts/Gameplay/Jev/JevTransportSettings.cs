using System;
using UnityEngine;

namespace Jev.Gameplay.Jev
{
    [Serializable]
    public sealed class JevTransportSettings
    {
        [Tooltip("The TypeSafe endpoint. Credentials are loaded only from the environment or ignored local configuration.")]
        public string Endpoint = "https://api.typesafe.ai/v1/systemone";
        public string Model = JevPromptBuilder.DefaultModel;
        [Range(1, 16)] public int MaxConcurrent = 8;
        [Range(1, 1200)] public int RequestsPerMinute = 600;
        [Range(1, 120)] public int TimeoutSeconds = 30;
        [Range(1, 2)] public int MaxAttempts = 2;
        [Tooltip("Keep redacted requests, replies and attempt timing in LocalSettings/Sessions outside Assets.")]
        public bool WriteSessionLedger = true;
        [Tooltip("Maximum size of each redacted ledger file. One previous file is retained when the active file rotates.")]
        [Range(1, 128)] public int MaxLedgerMegabytes = 16;
        [Header("Estimated match cost (USD)")]
        [Tooltip("Model covered by the configured tariff. Verified at docs.typesafe.ai/models on 2026-09-19. A different model is not silently priced with this tariff.")]
        public string PricingModel = JevPromptBuilder.DefaultModel;
        [Min(0), Tooltip("USD per million input tokens. Jev 1.13 published rate: $0.042. This estimates usage cost, not the account's invoice.")]
        public double InputUsdPerMillion = .042;
        [Min(0), Tooltip("USD per million output tokens. Jev 1.13 outputs are free.")]
        public double OutputUsdPerMillion = 0;
    }

    [Serializable]
    public sealed class JevTransportCounters
    {
        public long Evaluations;
        public long Attempts;
        public long Retries;
        public long SuccessfulAttempts;
        public long SuccessfulEvaluations;
        public long FailedEvaluations;
        public long CancelledEvaluations;
        public long InputTokens;
        public long OutputTokens;
        public double LastLatencyMilliseconds;
    }
}
