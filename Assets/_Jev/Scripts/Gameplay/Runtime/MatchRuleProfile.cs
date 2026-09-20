using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        /// <summary>UI reads actual match prices; editing the profile affects the next match.</summary>
        public override GameRules DisplayRules => World != null ? World.Definitions : Settings != null ? Settings.Rules : null;
        public override int DefaultUnitCap => Settings != null ? Settings.DefaultUnitCap : 20;
    }
}
