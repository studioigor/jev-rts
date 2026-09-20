using System.Linq;
using Jev.Gameplay.Simulation;
using NUnit.Framework;

namespace Jev.Gameplay.Simulation.Tests
{
    public sealed class CombatObservationTests
    {
        [Test]
        public void ReceivedHitsAreExplicitAndWaitingDoesNotEraseDanger()
        {
            var world=new RtsWorld{Width=20,Height=20,VictoryEnabled=false};
            var defender=world.AddUnit(FactionId.Undead,UnitKind.Warrior,new Cell(5,5),"defender");
            var attacker=world.AddUnit(FactionId.Human,UnitKind.Warrior,new Cell(6,5),"attacker");
            Assert.That(world.Execute(attacker.Id,"attack_defender",0).Ok,Is.True);
            Assert.That(world.Execute(defender.Id,"wait",0).Ok,Is.True);
            var o=world.Observe(defender.Id);
            Assert.That((bool)o.Combat["underAttack"],Is.True);
            Assert.That((int)o.Combat["receivedDamage"],Is.EqualTo(attacker.Damage));
            Assert.That((bool)o.Combat["visibleThreats"][0]["hasRecentlyHitSelf"],Is.True);
            Assert.That((bool)o.Combat["visibleThreats"][0]["attackOffered"],Is.True);
            Assert.That(o.LegalActions.Any(a=>a.Id=="attack_attacker"),Is.True);
            world.Tick(6.1);
            Assert.That((bool)world.Observe(defender.Id).Combat["underAttack"],Is.False);
        }

        [Test]
        public void UnseenAttackerDoesNotLeakIdentityOrPositionThroughDamageEvidence()
        {
            var world=new RtsWorld{Width=20,Height=20,VictoryEnabled=false};
            var defender=world.AddUnit(FactionId.Undead,UnitKind.Worker,new Cell(5,5),"defender");defender.Vision=1;
            var attacker=world.AddUnit(FactionId.Human,UnitKind.Archer,new Cell(8,5),"hidden-attacker");
            Assert.That(world.Execute(attacker.Id,"attack_defender",0).Ok,Is.True);
            var o=world.Observe(defender.Id);
            Assert.That((bool)o.Combat["underAttack"],Is.True);
            Assert.That(o.Combat["visibleThreats"],Is.Empty);
            Assert.That(o.Combat.ToString(),Does.Not.Contain("hidden-attacker"));
            Assert.That(o.VisibleUnits,Is.Empty);
        }
    }
}
