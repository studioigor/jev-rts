using System.Linq;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Jev.Gameplay.Runtime
{
    /// <summary>One authored battlefield contract shared by the match and live integration checks.</summary>
    public static class MatchWorldFactory
    {
        public static RtsWorld Create(BattlefieldDefinition battlefield, GameCatalog catalog, MatchSettings settings, FactionId player, int cap)
        {
            var rules = settings.CreateRules(cap);
            rules.PublicLocalTerrain = settings.PublicTerrainNavigation;
            rules.PublicTerrainRadius = Mathf.Max(0, settings.PublicTerrainRadius);
            rules.ExtendedMovementVocabulary = settings.ExtendedMovementVocabulary;
            foreach (var definition in rules.Buildings)
            {
                var entry = catalog.Building(FactionId.Human, definition.Kind);
                definition.FootprintSizeX = entry.Width;
                definition.FootprintSizeY = entry.Height;
            }
            var world = new RtsWorld { Width = battlefield.Width, Height = battlefield.Height, CellSize = battlefield.CellSize, Definitions = rules };
            world.Blocked.UnionWith(battlefield.Blocked);
            world.SightBlocked = battlefield.CreateSightBlockers();
            foreach (var resource in battlefield.Resources) world.AddResource(resource.Kind, resource.Cell, resource.Amount, resource.Id);
            world.StartMatch(player, cap, battlefield.Human.TownHall, battlefield.Human.Worker,
                battlefield.Undead.TownHall, battlefield.Undead.Worker, settings.StartingWood, settings.StartingGold);
            return world;
        }

        public static JObject NavigationAtlas(BattlefieldDefinition battlefield, UnitState unit = null)
        {
            if(!battlefield.DescribeRiverCrossings)
                return new JObject { ["source"]="authored_public_geography_not_an_order",
                    ["geography"]=battlefield.NavigationDescription,
                    ["landmarks"]=new JArray(battlefield.Landmarks.Select(landmark=>new JObject
                        { ["name"]=landmark.Name,["cell"]=new JArray(landmark.Cell.X,landmark.Cell.Y) })) };
            int riverY = battlefield.ToCell(Vector3.zero).Y;
            return new JObject
            {
                ["source"] = "authored_public_geography_not_an_order",
                ["landmarks"] = new JArray(battlefield.Landmarks.Select(landmark => new JObject
                    { ["name"] = landmark.Name, ["cell"] = new JArray(landmark.Cell.X, landmark.Cell.Y) })),
                ["riverApproximateRowY"] = riverY,
                ["geography"] = "The impassable river is near this map row. Crossing landmarks are Старый мост (Old Bridge) and Восточный переход (East Crossing). Landmarks are optional geography, not ordered destinations or evidence of enemy presence.",
                ["orderedGoalOnOppositeRiverBank"] = unit?.Order == null ? (JToken)JValue.CreateNull()
                    : new JValue((unit.Cell.Y < riverY && unit.Order.Cell.Y > riverY) || (unit.Cell.Y > riverY && unit.Order.Cell.Y < riverY))
            };
        }
    }
}
