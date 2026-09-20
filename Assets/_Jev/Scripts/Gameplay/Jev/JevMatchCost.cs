using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Jev.Gameplay.Jev
{
    /// <summary>One match's reported attempt usage, including retries. Estimates are never presented as an invoice.</summary>
    public sealed class JevMatchCost
    {
        public int Generation { get; private set; }
        public decimal EstimatedUsd { get; private set; }
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long Attempts { get; private set; }
        public long MissingUsageAttempts { get; private set; }
        public long PricedAttempts { get; private set; }

        public void BeginSession()
        {
            Generation++;EstimatedUsd=0;InputTokens=OutputTokens=Attempts=MissingUsageAttempts=PricedAttempts=0;
        }

        public void RecordAttempt(int generation,JObject response,string requestedModel,string pricingModel,double inputRate,double outputRate)
        {
            // Replies from an aborted/replaced match cannot accrue against the new game.
            if(generation!=Generation)return;
            Attempts++;
            var usage=response?["usage"] as JObject;
            bool inputKnown=TryCount(usage?["input_tokens"],out long input);
            bool outputKnown=TryCount(usage?["output_tokens"],out long output);
            InputTokens+=input;OutputTokens+=output;
            string model=response?["model"]?.Type==JTokenType.String?(string)response["model"]:requestedModel;
            if(string.IsNullOrWhiteSpace(pricingModel)||!string.Equals(model,pricingModel,StringComparison.Ordinal)||!ValidRate(inputRate)||!ValidRate(outputRate))
            {MissingUsageAttempts++;return;}
            if(inputKnown)EstimatedUsd+=input*(decimal)inputRate/1000000m;
            if(outputKnown)EstimatedUsd+=output*(decimal)outputRate/1000000m;
            if(inputKnown&&inputRate>0||outputKnown&&outputRate>0||inputRate==0&&outputRate==0)PricedAttempts++;
            if(!inputKnown&&inputRate>0||!outputKnown&&outputRate>0)MissingUsageAttempts++;
        }

        public string Label
        {
            get
            {
                if(MissingUsageAttempts>0&&PricedAttempts==0)return "JEV ≈ —";
                string value=decimal.Round(EstimatedUsd,2,MidpointRounding.AwayFromZero).ToString("F2",CultureInfo.InvariantCulture);
                return "JEV ≈ $"+value+(MissingUsageAttempts>0?"*":"");
            }
        }
        public string Tooltip=>"Оценка текущей партии по usage API и настроенному тарифу; включает известный расход повторных запросов. Это не подтверждённое списание со счёта."
            +(MissingUsageAttempts>0?" * Для части попыток нет полного usage или подходящего тарифа; итоговый расход может быть выше.":"");

        static bool ValidRate(double rate)=>!double.IsNaN(rate)&&!double.IsInfinity(rate)&&rate>=0&&rate<=1000000;
        static bool TryCount(JToken token,out long count)
        {
            count=0;if(token?.Type!=JTokenType.Integer)return false;
            try{long value=token.Value<long>();if(value<0)return false;count=value;return true;}
            catch(OverflowException){return false;}
        }
    }
}
