using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEditor;

namespace Jev.Gameplay.Runtime.Tests
{
    public sealed class ProductionRulesTests
    {
        [Test]
        public void AuthoredMatchKeepsWorkersInTownHallAndSoldiersInMilitaryBuildings()
        {
            var settings=AssetDatabase.LoadAssetAtPath<MatchSettings>("Assets/_Jev/Settings/Gameplay/MatchSettings.asset");
            Assert.That(settings,Is.Not.Null);
            var rules=settings.CreateRules(settings.DefaultUnitCap);
            // These are the definitions used by both the HUD and JEV commander's legal offers.
            CollectionAssert.AreEqual(new[]{UnitKind.Worker},rules.Building(BuildingKind.TownHall).TrainableUnits);
            CollectionAssert.AreEqual(new[]{UnitKind.Warrior},rules.Building(BuildingKind.Barracks).TrainableUnits);
            CollectionAssert.AreEqual(new[]{UnitKind.Archer},rules.Building(BuildingKind.ArcheryRange).TrainableUnits);
            Assert.That(rules.Building(BuildingKind.House).TrainableUnits,Is.Empty);
            Assert.That(rules.Building(BuildingKind.Tower).TrainableUnits,Is.Empty);
        }
    }
}
