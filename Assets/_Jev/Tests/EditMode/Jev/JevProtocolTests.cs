using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    public sealed class JevProtocolTests
    {
        static JObject Request(JevBrainRole role = JevBrainRole.Unit)
        {
            return JevPromptBuilder.Build(role, new JObject
            {
                ["self"] = new JObject { ["id"] = "worker-1", ["x"] = 4, ["y"] = 3 },
                ["memory"] = new JObject { ["recentPositions"] = new JArray(new JArray(3, 3), new JArray(4, 3)) }
            }, new Dictionary<string, string>
            {
                { "wait", "Wait until an event." },
                { "move_east", "Execute exactly [(5,3)]." },
                { "segment_EES", "Execute exactly [(5,3),(6,3),(6,4)]." }
            });
        }

        static JObject Response(JObject request, string choice = "wait")
        {
            var probabilities = new JObject();
            foreach (var option in ((JObject)request["questions"]["action"]["criteria"]).Properties())
                probabilities[option.Name] = option.Name == choice ? 1 : 0;
            var answers = new JObject
            {
                ["action"] = new JObject { ["type"] = "choice", ["choice"] = choice, ["probabilities"] = probabilities }
            };
            if (request["questions"]["fear"] != null)
                answers["fear"] = new JObject { ["type"] = "noul", ["noul"] = .3 };
            return new JObject
            {
                ["model"] = "offline-test-fixture",
                ["answers"] = answers,
                ["usage"] = new JObject { ["input_tokens"] = 120, ["output_tokens"] = 30 }
            };
        }

        [Test]
        public void EveryLegalOptionAndPersonalFactSurvivesWithoutRanking()
        {
            var observation = new JObject { ["self"] = new JObject { ["x"] = 6 }, ["map"] = new JArray("??##..", "...S..") };
            var options = new Dictionary<string, string> { { "away", "Move away." }, { "wait", "Wait." }, { "closer", "Move closer." } };
            var request = JevPromptBuilder.BuildUnit(observation, options);
            var preserved=(JObject)request["state"].DeepClone();
            preserved.Remove("currentObjective");
            preserved.Remove("navigationGeometry");
            Assert.That(JToken.DeepEquals(preserved, observation));
            CollectionAssert.AreEqual(options.Keys.ToArray(), ((JObject)request["questions"]["action"]["criteria"]).Properties().Select(p => p.Name));
            observation["self"]["x"] = 99;
            options["away"] = "Mutated";
            Assert.That((int)request["state"]["self"]["x"], Is.EqualTo(6));
            Assert.That((string)request["questions"]["action"]["criteria"]["away"], Is.EqualTo("Move away."));
            Assert.That((string)request["model"], Is.EqualTo("jev-1.13.0"));
        }

        [Test]
        public void ProviderChoiceIsHonoredEvenWhenItIsNotTheArgmaxAndFearIsHigh()
        {
            var request = Request();
            var response = Response(request, "move_east");
            response["answers"]["action"]["probabilities"] = new JObject { ["wait"] = .8, ["move_east"] = .1, ["segment_EES"] = .1 };
            response["answers"]["action"]["confidence"] = 0;
            response["answers"]["fear"]["noul"] = 1;
            var result = JevResponseValidator.Validate(request, response);
            Assert.That(result.Success);
            Assert.That(result.Choice, Is.EqualTo("move_east"));
            Assert.That(result.Confidence, Is.Zero);
            Assert.That(result.FearProbability, Is.EqualTo(1));
        }

        [TestCase(JevBrainRole.Unit, 2)]
        [TestCase(JevBrainRole.Commander, 1)]
        [TestCase(JevBrainRole.Tower, 1)]
        [TestCase(JevBrainRole.Navigation, 1)]
        [TestCase(JevBrainRole.Gather, 1)]
        public void RolesHaveOnlyTheirIndependentQuestions(JevBrainRole role, int count)
        {
            var request = Request(role);
            Assert.That(((JObject)request["questions"]).Count, Is.EqualTo(count));
            var result = JevResponseValidator.Validate(request, Response(request, "segment_EES"));
            Assert.That(result.Success);
            Assert.That(result.Choice, Is.EqualTo("segment_EES"));
            Assert.That(result.FearProbability.HasValue, Is.EqualTo(role == JevBrainRole.Unit));
        }

        [Test]
        public void MalformedAndIncompleteAnswersNeverProduceAnAction()
        {
            var mutations = new Action<JObject>[]
            {
                response => ((JObject)response["answers"]).Remove("fear"),
                response => response["answers"]["extra"] = new JObject(),
                response => response["answers"]["action"]["choice"] = "automatic_path",
                response => response["answers"]["action"]["choice"] = new JObject(),
                response => response["answers"]["action"]["type"] = new JObject(),
                response => ((JObject)response["answers"]["action"]["probabilities"]).Remove("move_east"),
                response => response["answers"]["action"]["probabilities"]["extra"] = 0,
                response => response["answers"]["action"]["probabilities"]["wait"] = -.1,
                response => response["answers"]["action"]["probabilities"]["wait"] = .5,
                response => response["answers"]["action"]["probabilities"]["wait"] = "1",
                response => response["answers"]["action"]["confidence"] = double.NaN,
                response => response["answers"]["fear"]["noul"] = double.PositiveInfinity,
                response => response["answers"]["fear"]["noul"] = 2,
                response => response["answers"]["fear"]["noul"] = true
            };
            foreach (var mutate in mutations)
            {
                var request = Request();
                var response = Response(request);
                mutate(response);
                var result = JevResponseValidator.Validate(request, response);
                Assert.That(result.Success, Is.False);
                Assert.That(result.ErrorCode, Is.EqualTo("invalid_decision"));
                Assert.That(result.Choice, Is.Null);
            }
            Assert.That(JevResponseValidator.Validate(Request(), null).Success, Is.False);
        }

        [Test]
        public void OptionalConfidenceAndMalformedUsageDoNotInventMeasurements()
        {
            var request = Request();
            var response = Response(request);
            response["usage"] = "unavailable";
            var result = JevResponseValidator.Validate(request, response);
            Assert.That(result.Success);
            Assert.That(result.Confidence, Is.Null);
            Assert.That(result.InputTokens, Is.Zero);
            Assert.That(result.OutputTokens, Is.Zero);
            response["answers"]["action"]["choice"] = "move_east";
            Assert.That(result.Choice, Is.EqualTo("wait"));
            Assert.That((string)result.Raw["answers"]["action"]["choice"], Is.EqualTo("wait"));
        }

        [Test]
        public void OneWaitChoiceStillRequiresARealTransportRequest()
        {
            var request = JevPromptBuilder.BuildUnit(new JObject(), new Dictionary<string, string> { { "wait", "Remain idle." } });
            Assert.That(((JObject)request["questions"]["action"]["criteria"]).Count, Is.EqualTo(1));
            Assert.That(JevResponseValidator.Validate(request, null).Success, Is.False);
            Assert.That(JevResponseValidator.Validate(request, Response(request)).Success);
        }

        [Test]
        public void EmptyOrOversizedOptionSetsAreRejectedInsteadOfFiltered()
        {
            Assert.Throws<ArgumentException>(() => JevPromptBuilder.BuildUnit(new JObject(), new Dictionary<string, string>()));
            var options = new Dictionary<string, string>();
            for (int index = 0; index < 256; index++) options.Add("step_" + index, "Exact step.");
            Assert.Throws<ArgumentException>(() => JevPromptBuilder.BuildUnit(new JObject(), options));
            Assert.That(JevResponseValidator.ValidateRequest(new JObject { ["state"] = new JObject(), ["questions"] = new JObject() }), Is.Not.Null);
        }

        [Test]
        public void LedgerRedactionRemovesCredentialsWithoutChangingUsageOrSource()
        {
            const string key = "offline-fixture-secret";
            var source = new JObject
            {
                ["Authorization"] = "Bearer anything",
                ["usage"] = new JObject { ["input_tokens"] = 80 },
                ["echo"] = "prefix " + key,
                [key] = "a property name",
                ["nested"] = new JArray(new JObject { ["api_key"] = "another-value" })
            };
            var redacted = JevCredentials.RedactedCopy(source, key);
            Assert.That(redacted.ToString(), Does.Not.Contain(key).And.Not.Contain("another-value").And.Not.Contain("Bearer anything"));
            Assert.That((int)redacted["usage"]["input_tokens"], Is.EqualTo(80));
            Assert.That((string)source["echo"], Does.Contain(key));
        }
    }
}
