using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.Presentation;
using Jev.Gameplay.Audio;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        readonly HashSet<string> expiredUnitViews = new HashSet<string>();
        string visualSessionId;
        void CreateResourceViews()
        {
            foreach(var resource in Battlefield.Resources)
            {
                if(Catalog.ResourceProxy==null)continue;
                var proxy=Instantiate(Catalog.ResourceProxy,Battlefield.ToWorld(resource.Cell),Quaternion.identity,ResourcesRoot);
                proxy.Id=resource.Id;proxy.Kind=EntityViewKind.Resource;proxy.name=resource.Id;proxy.SetSelected(false);resourceViews.Add(resource.Id,proxy);
                proxy.gameObject.SetActive(false);
                if(!string.IsNullOrWhiteSpace(resource.VisualPath)){var visual=GameObject.Find(resource.VisualPath);if(visual)resourceVisuals[resource.Id]=visual;}
            }
        }
        void SyncViews()
        {
            if (visualSessionId != SessionId) { expiredUnitViews.Clear(); visualSessionId = SessionId; }
            if (Feedback) Feedback.Bind(this);
            foreach(var unit in World.Units)
            {
                if (expiredUnitViews.Contains(unit.Id)) continue;
                if(!unitViews.TryGetValue(unit.Id,out var view))
                {
                    view=Instantiate(Catalog.Unit(unit.Faction,unit.Kind).Prefab,Battlefield.ToWorld(unit.Cell),Quaternion.identity,UnitsRoot);
                    view.name=UnitName(unit.Kind)+" ["+unit.Id+"]";view.Initialize(unit.Id,unit.Kind,unit.Faction);unitViews.Add(unit.Id,view);
                    var brain=view.GetComponent<UnitBrain>();brain.UnitId=unit.Id;brain.Facing=unit.Faction==FactionId.Human?Vector3.forward:Vector3.back;brains.Add(unit.Id,brain);lastHp[unit.Id]=unit.Hp;
                    view.gameObject.SetActive(unit.Faction==PlayerFaction||playerVisible.Contains(unit.Cell));
                }
                bool combatAudible=unit.Faction==PlayerFaction||playerVisible.Contains(unit.Cell);
                if(lastHp.TryGetValue(unit.Id,out int previous)&&unit.Hp<previous)
                {
                    if(!Feedback)view.TakeHit(combatAudible);
                    var brain=brains[unit.Id];brain.InterruptForThreat("damage");
                    if(brain.Pending)Transport.Prioritize(brain.Ticket);
                }
                lastHp[unit.Id]=unit.Hp;
                if(!unit.Alive)
                {
                    view.Die(combatAudible);var brain=brains[unit.Id];if(brain.Pending)Transport.Cancel(brain.Ticket);brain.Pending=false;brain.ApprovedRoute.Clear();selection.Remove(unit.Id);
                    foreach(var collider in view.GetComponentsInChildren<Collider>())collider.enabled=false;
                    if (view.CorpseExpired)
                    {
                        expiredUnitViews.Add(unit.Id); unitViews.Remove(unit.Id); brains.Remove(unit.Id); lastHp.Remove(unit.Id);
                        Destroy(view.gameObject);
                    }
                }
            }
            foreach(var building in World.Buildings)
            {
                if(!buildingViews.TryGetValue(building.Id,out var view))
                {
                    var entry=Catalog.Building(building.Faction,building.Kind);
                    float yaw=building.Faction==FactionId.Human?0:180;
                    var site=Battlefield.BuildSites.FirstOrDefault(s=>s.Faction==building.Faction&&s.Kind==building.Kind&&s.Cell==building.Cell);
                    if(site!=null)yaw=site.Rotation;
                    if(building.Kind==BuildingKind.TownHall)yaw=(building.Faction==FactionId.Human?Battlefield.Human:Battlefield.Undead).Rotation;
                    var instance=Instantiate(entry.Prefab,Battlefield.ToWorld(building.Cell),Quaternion.Euler(0,yaw,0),BuildingsRoot);
                    instance.name=BuildingName(building.Kind)+" ["+building.Id+"]";view=instance.GetComponent<EntityView>();view.Id=building.Id;view.Kind=EntityViewKind.Building;view.SetSelected(false);buildingViews.Add(building.Id,view);
                    view.gameObject.SetActive(building.Faction==PlayerFaction||World.Footprint(building).Any(playerVisible.Contains));
                }
                if(!building.Alive){view.gameObject.SetActive(false);selection.Remove(building.Id);continue;}
                var visual=view.transform.Find("Visual");
                if(visual){visual.localScale=Vector3.one;visual.gameObject.SetActive(ShowCompletedBuilding(building));}
            }
            SyncConstructionSites();
            foreach(var e in World.Events.Where(e=>e.Sequence>observedEvents).ToList())
            {
                observedEvents=e.Sequence;
                PresentInteraction(e);
                bool audible=e.Faction==PlayerFaction||playerVisible.Contains(e.To);
                if(!Feedback&&e.Type=="gather"&&audible)ProceduralAudio.Instance?.Play(GameSound.Gather,Battlefield.ToWorld(e.To),true);
                else if(!Feedback&&e.Type=="build_progress"&&audible)ProceduralAudio.Instance?.Play(GameSound.Build,Battlefield.ToWorld(e.To),true);
                else if(e.Type=="training_completed"&&e.Faction==PlayerFaction)SetStatus("Новый юнит готов.");
            }
            foreach(var resource in World.Resources.Where(r=>r.Remaining<=0 && playerVisible.Contains(r.Cell)))
            {
                if(resourceViews.TryGetValue(resource.Id,out var proxy))proxy.gameObject.SetActive(false);
                if(resourceVisuals.TryGetValue(resource.Id,out var visual)&&(!Feedback||!Feedback.RetainResource(visual.transform)))visual.SetActive(false);
                selection.Remove(resource.Id);
                if(hoveredResource==resource.Id)hoveredResource=null;
            }
        }
        void RefreshVisibility()
        {
            playerVisible.Clear();
            if(!Settings.EnableFogOfWar)
            {for(int y=0;y<World.Height;y++)for(int x=0;x<World.Width;x++)playerVisible.Add(new Cell(x,y));}
            else
            {
                playerVisible.UnionWith(FactionVision(PlayerFaction));
            }
            playerExplored.UnionWith(playerVisible);
            if (ViewCamera && ViewCamera.TryGetComponent<FogOfWarView>(out var fogView))
                fogView.SetVisibility(this, Battlefield, playerVisible, playerExplored);
            foreach(var id in selection.ToArray())
            {
                var unit=World.Unit(id);var building=World.Building(id);var resource=World.Resource(id);
                if(unit!=null&&unit.Faction!=PlayerFaction&&!playerVisible.Contains(unit.Cell))selection.Remove(id);
                if(building!=null&&building.Faction!=PlayerFaction&&!World.Footprint(building).Any(playerVisible.Contains))selection.Remove(id);
                if(resource!=null&&(!playerVisible.Contains(resource.Cell)||resource.Remaining<=0))selection.Remove(id);
            }
            var hovered=World.Resource(hoveredResource);
            if(hovered!=null&&(!playerVisible.Contains(hovered.Cell)||hovered.Remaining<=0))hoveredResource=null;
            RefreshSelection();
            foreach(var unit in World.Units)
                if(unitViews.TryGetValue(unit.Id,out var view))view.gameObject.SetActive(unit.Faction==PlayerFaction||playerVisible.Contains(unit.Cell));
            foreach(var b in World.Buildings)
                if(buildingViews.TryGetValue(b.Id,out var view))view.gameObject.SetActive(b.Alive&&(b.Faction==PlayerFaction||World.Footprint(b).Any(c=>playerVisible.Contains(c))));
            foreach(var resource in World.Resources)
            {
                bool visible=playerVisible.Contains(resource.Cell);
                if(resourceViews.TryGetValue(resource.Id,out var proxy))proxy.gameObject.SetActive(visible&&resource.Remaining>0);
                // Scenery retains the last observed state in explored fog. Hidden harvesting must not reveal itself.
                if(visible&&resource.Remaining<=0&&resourceVisuals.TryGetValue(resource.Id,out var visual)&&(!Feedback||!Feedback.RetainResource(visual.transform)))visual.SetActive(false);
            }
        }
    }
}
