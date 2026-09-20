using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Jev.Gameplay.Jev.Tests
{
    /// <summary>Offline arithmetic/accounting fixtures; no gameplay decisions or HTTP.</summary>
    public sealed class JevMatchCostTests
    {
        const string Model="jev-1.13.0";
        static JObject Usage(long input,long output=0)=>new JObject{["model"]=Model,["usage"]=new JObject{["input_tokens"]=input,["output_tokens"]=output}};
        static void Add(JevMatchCost cost,JObject response,double inputRate=.042,double outputRate=0)
            =>cost.RecordAttempt(cost.Generation,response,Model,Model,inputRate,outputRate);

        [Test]
        public void DisplayRoundsToCentsOnlyAfterAddingFullPrecisionUsage()
        {
            var cost=new JevMatchCost();cost.BeginSession();
            Assert.That(cost.Label,Is.EqualTo("JEV ≈ $0.00"));
            for(int i=0;i<15;i++)Add(cost,Usage(1000),1);
            Assert.That(cost.EstimatedUsd,Is.EqualTo(.015m));
            Assert.That(cost.Label,Is.EqualTo("JEV ≈ $0.02"));
        }

        [Test]
        public void EveryReportedAttemptCountsIncludingRetryAndErrorUsage()
        {
            var cost=new JevMatchCost();
            Add(cost,Usage(1000000,100000));
            var error=Usage(500000,2000);error["error"]="server_error";Add(cost,error);
            Add(cost,Usage(1000000,100000));
            Assert.That(cost.Attempts,Is.EqualTo(3));Assert.That(cost.InputTokens,Is.EqualTo(2500000));
            Assert.That(cost.EstimatedUsd,Is.EqualTo(.105m));Assert.That(cost.Label,Is.EqualTo("JEV ≈ $0.11"));
            Assert.That(cost.MissingUsageAttempts,Is.Zero);
        }

        [Test]
        public void NewMatchResetsBaselineAndLateOldRepliesCannotContaminateIt()
        {
            var cost=new JevMatchCost();cost.BeginSession();int previous=cost.Generation;
            Add(cost,Usage(1000000));cost.BeginSession();
            cost.RecordAttempt(previous,Usage(1000000),Model,Model,.042,0);
            Assert.That(cost.EstimatedUsd,Is.Zero);Assert.That(cost.Attempts,Is.Zero);
            Add(cost,Usage(250000));
            Assert.That(cost.EstimatedUsd,Is.EqualTo(.0105m));Assert.That(cost.Label,Is.EqualTo("JEV ≈ $0.01"));
        }

        [Test]
        public void MissingUsageIsUnknownRatherThanAFreeRequestAndFreeOutputNeedsNoEstimate()
        {
            var cost=new JevMatchCost();Add(cost,null);
            Assert.That(cost.Label,Is.EqualTo("JEV ≈ —"));Assert.That(cost.MissingUsageAttempts,Is.EqualTo(1));
            var response=Usage(1000000);((JObject)response["usage"]).Remove("output_tokens");Add(cost,response);
            Assert.That(cost.EstimatedUsd,Is.EqualTo(.042m));Assert.That(cost.Label,Is.EqualTo("JEV ≈ $0.04*"));
            Assert.That(cost.MissingUsageAttempts,Is.EqualTo(1),"Output is free under the published tariff.");
        }

        [Test]
        public void MalformedUsageAndDifferentModelsAreNotSilentlyPriced()
        {
            var cost=new JevMatchCost();Add(cost,new JObject{["usage"]="not an object"});
            var changed=Usage(1000000);changed["model"]="unknown-model";Add(cost,changed);
            Add(cost,Usage(1000000),double.NaN);
            Assert.That(cost.EstimatedUsd,Is.Zero);Assert.That(cost.MissingUsageAttempts,Is.EqualTo(3));
            Assert.That(cost.Label,Is.EqualTo("JEV ≈ —"));
        }

        [Test]
        public void PartialKnownUsageRemainsCountedWhenAnotherPricedFieldIsMissing()
        {
            var cost=new JevMatchCost();var response=Usage(1000000);((JObject)response["usage"]).Remove("output_tokens");
            Add(cost,response,.042,.1);
            Assert.That(cost.EstimatedUsd,Is.EqualTo(.042m));Assert.That(cost.MissingUsageAttempts,Is.EqualTo(1));
            Assert.That(cost.Label,Is.EqualTo("JEV ≈ $0.04*"));
        }
    }
}
