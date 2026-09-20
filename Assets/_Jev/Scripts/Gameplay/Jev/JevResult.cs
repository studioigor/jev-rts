using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>Validated model output or an explicit infrastructure failure. Never a fallback action.</summary>
    public sealed class JevResult
    {
        public bool Success;
        public long RequestId;
        public string Choice;
        public float? FearProbability;
        public double? Confidence;
        public string ErrorCode;
        public string ErrorMessage;
        public long HttpStatus;
        public int Attempts;
        public long InputTokens;
        public long OutputTokens;
        public string Model;
        public JObject Raw;

        public static JevResult Failure(string code, string message)
        {
            return new JevResult { ErrorCode = code, ErrorMessage = message };
        }
    }
}
