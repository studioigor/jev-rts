using System.Collections.Generic;
using System.Linq;
using Jev.Gameplay.Presentation;
using Jev.Gameplay.Simulation;
using UnityEngine;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        public ConstructionSiteView HumanConstructionFoundation, UndeadConstructionFoundation;
        readonly Dictionary<Cell, ConstructionSiteView> constructionSites = new Dictionary<Cell, ConstructionSiteView>();
        readonly HashSet<Cell> requiredConstructionSites = new HashSet<Cell>();
        string constructionSession;

        bool ShowCompletedBuilding(BuildingState building)
        {
            if (!building.Complete) return false;
            var completion = World.Events.LastOrDefault(e => e.Type == "build_completed" && e.TargetId == building.Id);
            if (completion == null || !Feedback || !unitViews.TryGetValue(completion.ActorId, out var worker)) return true;
            return World.TimeSeconds - completion.TimeSeconds >= worker.ClipDuration("Attack") * Feedback.ContactPhase;
        }

        void SyncConstructionSites()
        {
            if (constructionSession != SessionId)
            {
                foreach (var site in constructionSites.Values) if (site) Destroy(site.gameObject);
                constructionSites.Clear(); constructionSession = SessionId;
            }
            requiredConstructionSites.Clear();
            foreach (var building in World.Buildings)
            {
                if (!building.Alive || ShowCompletedBuilding(building)) continue;
                bool visible = building.Faction == PlayerFaction || World.Footprint(building).Any(playerVisible.Contains);
                if (!visible) continue;
                ShowConstructionSite(building.Cell, building.Faction, building.Kind, building.Progress, building.RequiredWork, false, visible);
            }
            // A player's confirmed build ORDER is shown immediately as preparation. No payment,
            // resource change or construction work is invented before a genuine JEV build action.
            foreach (var worker in World.Units)
            {
                var order = worker.Order;
                if (!worker.Alive || worker.Faction != PlayerFaction || worker.Kind != UnitKind.Worker || order?.Kind != OrderKind.Build) continue;
                if (requiredConstructionSites.Contains(order.Cell) || World.Buildings.Any(b => b.Alive && b.Faction == PlayerFaction && b.Cell == order.Cell)) continue;
                ShowConstructionSite(order.Cell, worker.Faction, order.BuildingKind, 0, World.Definitions.Building(order.BuildingKind).RequiredWork, true, true);
            }
            foreach (var cell in constructionSites.Keys.Where(c => !requiredConstructionSites.Contains(c)).ToArray())
            {
                if (constructionSites[cell]) Destroy(constructionSites[cell].gameObject);
                constructionSites.Remove(cell);
            }
        }

        void ShowConstructionSite(Cell cell, FactionId faction, BuildingKind kind, int progress, int required, bool planned, bool visible)
        {
            var source = faction == FactionId.Human ? HumanConstructionFoundation : UndeadConstructionFoundation;
            if (!source) return;
            requiredConstructionSites.Add(cell);
            if (!constructionSites.TryGetValue(cell, out var view) || !view)
            {
                view = Instantiate(source, Battlefield.ToWorld(cell), Quaternion.identity, BuildingsRoot);
                view.name = "Construction " + faction + " " + kind + " " + cell;
                constructionSites[cell] = view;
            }
            var authoredSite = Battlefield.BuildSites.FirstOrDefault(s => s.Faction == faction && s.Kind == kind && s.Cell == cell);
            float yaw = authoredSite != null ? authoredSite.Rotation : faction == FactionId.Human ? 0 : 180;
            view.transform.rotation = Quaternion.Euler(0, yaw, 0);
            var geometry = view.transform.Find("Geometry");
            var definition = World.Definitions.Building(kind);
            var size = new Vector3(definition.FootprintSizeX * Battlefield.CellSize / 3f, 1, definition.FootprintSizeY * Battlefield.CellSize / 3f);
            if (geometry && geometry.localScale != size) geometry.localScale = size;
            view.gameObject.SetActive(visible);
            view.SetProgress(progress, required, planned);
        }

        void ClearConstructionSites()
        {
            foreach (var site in constructionSites.Values) if (site) Destroy(site.gameObject);
            constructionSites.Clear(); requiredConstructionSites.Clear();
        }
    }
}
