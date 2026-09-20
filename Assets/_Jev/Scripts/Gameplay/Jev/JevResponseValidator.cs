using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>Checks the entire original choice set; the provider's choice is never replaced by argmax.</summary>
    public static class JevResponseValidator
    {
        public const int MaximumChoices = 255;

        public static string ValidateRequest(JObject request)
        {
            if (request == null || request["state"] == null) return "A state snapshot is required.";
            if (!(request["questions"] is JObject questions) || !questions.Properties().Any())
                return "At least one question is required.";
            if (!(questions["action"] is JObject action) || Text(action["type"]) != "choice")
                return "The action question must be a Choice.";
            foreach (var property in questions.Properties())
            {
                if (!(property.Value is JObject question)) return "Invalid question.";
                string kind = Text(question["type"]);
                if (kind == "noul") continue;
                if (kind != "choice" || !(question["criteria"] is JObject criteria)) return "Unsupported question type.";
                int count = criteria.Count;
                if (count < 1 || count > MaximumChoices) return "Choice count must be between 1 and 255.";
                if (criteria.Properties().Any(option => string.IsNullOrWhiteSpace(option.Name))) return "Empty choice ID.";
            }
            return null;
        }

        public static JevResult Validate(JObject request, JObject response)
        {
            string requestError = ValidateRequest(request);
            if (requestError != null) return JevResult.Failure("invalid_request", requestError);
            var questions = (JObject)request["questions"];
            if (!(response?["answers"] is JObject answers) || answers.Count != questions.Count ||
                questions.Properties().Any(question => answers.Property(question.Name) == null))
                return Invalid("Missing or unexpected answer set.");

            foreach (var property in questions.Properties())
            {
                var question = (JObject)property.Value;
                if (!(answers[property.Name] is JObject answer)) return Invalid("Invalid answer object.");
                string type = Text(question["type"]);
                if (Text(answer["type"]) != type) return Invalid("Answer type does not match its question.");
                if (type == "noul")
                {
                    if (!Probability(answer["noul"], out _)) return Invalid("Noul must be a finite probability.");
                    continue;
                }

                var criteria = (JObject)question["criteria"];
                if (answer["choice"]?.Type != JTokenType.String || criteria.Property((string)answer["choice"]) == null)
                    return Invalid("Choice is outside the submitted action set.");
                if (!(answer["probabilities"] is JObject probabilities) || probabilities.Count != criteria.Count)
                    return Invalid("Probability distribution does not match the action set.");
                double sum = 0;
                foreach (var option in criteria.Properties())
                {
                    if (!Probability(probabilities[option.Name], out double value))
                        return Invalid("Missing or invalid action probability.");
                    sum += value;
                }
                if (Math.Abs(sum - 1) > .0200000001) return Invalid("Action probabilities do not sum to one.");
                if (answer["confidence"] != null && answer["confidence"].Type != JTokenType.Null &&
                    !Probability(answer["confidence"], out _)) return Invalid("Invalid action confidence.");
            }

            var actionAnswer = (JObject)answers["action"];
            var result = new JevResult
            {
                Success = true,
                Choice = (string)actionAnswer["choice"],
                Model = Text(response["model"]),
                Raw = (JObject)response.DeepClone(),
                InputTokens = UsageCount(response, "input_tokens"),
                OutputTokens = UsageCount(response, "output_tokens")
            };
            if (Probability(actionAnswer["confidence"], out double confidence)) result.Confidence = confidence;
            if (answers["fear"] is JObject fear && Probability(fear["noul"], out double fearValue))
                result.FearProbability = (float)fearValue;
            return result;
        }

        public static long TokenCount(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer) return 0;
            try { return Math.Max(0, token.Value<long>()); }
            catch (OverflowException) { return 0; }
        }

        public static long UsageCount(JObject response, string field)
            => response?["usage"] is JObject usage ? TokenCount(usage[field]) : 0;

        static bool Probability(JToken token, out double value)
        {
            value = 0;
            if (token == null || (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)) return false;
            try { value = token.Value<double>(); }
            catch (Exception exception) when (exception is OverflowException || exception is FormatException)
            { return false; }
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 1;
        }

        static string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;
        static JevResult Invalid(string message) => JevResult.Failure("invalid_decision", message);
    }
}
