using System;
using System.Collections.Generic;
using System.Linq;
using Jev.Gameplay.Diagnostics;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.UI;
using UnityEngine;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        /// <summary>Authored test setup only. All subsequent sensing, decisions, collision and animation use the normal match pipeline.</summary>
        public bool StartScenario(NavigationScenarioDefinition scenario)
        {
            if (!scenario || !scenario.Battlefield || !scenario.Settings || !Catalog || !Transport)
                throw new InvalidOperationException("The navigation scenario is not fully authored.");
            Transport.ReloadCredentials();
            if (!Transport.IsConfigured) { SetStatus("Нужен ключ JEV_API_KEY в LocalSettings/jev.env.", 300); return false; }
            ClearMatch();
            Transport.BeginMatchCost();
            Battlefield = scenario.Battlefield; Settings = scenario.Settings;
            PlayerFaction = scenario.Faction; UnitCap = scenario.Starts.Count;
            SessionId = Guid.NewGuid().ToString("N"); sessionSerial++;
            var rules = Settings.CreateRules(UnitCap);
            rules.PublicLocalTerrain = Settings.PublicTerrainNavigation;
            rules.PublicTerrainRadius = Settings.PublicTerrainRadius;
            rules.ExtendedMovementVocabulary = Settings.ExtendedMovementVocabulary;
            World = new RtsWorld { Width=Battlefield.Width, Height=Battlefield.Height, CellSize=Battlefield.CellSize,
                Definitions=rules, PlayerFaction=PlayerFaction, VictoryEnabled=false };
            World.Faction(PlayerFaction).BasePopulationCapacity = UnitCap;
            World.Blocked.UnionWith(Battlefield.Blocked);
            World.SightBlocked = Battlefield.CreateSightBlockers();
            for (int i=0; i<scenario.Starts.Count; i++)
                World.AddUnit(PlayerFaction, scenario.UnitKind, scenario.Starts[i], "passage-"+(i+1).ToString("00"));
            World.AssertInvariants();
            Phase=MatchPhase.Playing; Time.timeScale=1;
            SyncViews(); RefreshVisibility();
            foreach(var u in World.Units)selection.Add(u.Id);
            RefreshSelection();
            if (CameraRig) CameraRig.ControlsEnabled=true;
            // One shared command, exactly the same entry point as a multi-selection ground click.
            return IssueGroupMove(World.Units.Select(u=>u.Id).ToArray(), scenario.Target).Ok;
        }

        public ActionResult IssueGroupMove(IReadOnlyList<string> ids, Cell target)
        {
            var result=World.IssueGroupMove(ids,target,PlayerFaction);
            if(!result.Ok)return result;
            foreach(var id in ids)
                if(brains.TryGetValue(id,out var brain))
                    RequestChangedOrder(World.Unit(id),brain);
            return result;
        }
    }
}
