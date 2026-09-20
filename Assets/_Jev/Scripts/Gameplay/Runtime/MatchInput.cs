using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.Presentation;
using Jev.Gameplay.Audio;
using Jev.Gameplay.Camera;

namespace Jev.Gameplay.Runtime
{
    public sealed partial class MatchController
    {
        string hoveredResource;
        float nextHoverAt;
        void ResetSelectionGesture()
        {
            selectionPressed=false;dragging=false;hoveredResource=null;RefreshSelection();
            if(Hud)Hud.SetSelectionRect(Vector2.zero,Vector2.zero,false);
        }
        void HandleInput()
        {
            var mouse=Mouse.current;if(mouse==null||ViewCamera==null)return;
            bool rightCommand=mouse.rightButton.wasPressedThisFrame&&!RtsCameraController.CommandModifierHeld;
            var pointer=mouse.position.ReadValue();
            bool overUi=EventSystem.current!=null&&EventSystem.current.IsPointerOverGameObject();
            if(Time.unscaledTime>=nextHoverAt)
            {
                nextHoverAt=Time.unscaledTime+.08f;
                bool canWork=SelectedUnits().Any(u=>u.Kind==UnitKind.Worker);
                var resource=overUi?null:World.Resource(EntityAt(pointer,canWork,canWork));
                string hovered=resource!=null&&resource.Remaining>0?resource.Id:null;
                if(hoveredResource!=hovered){hoveredResource=hovered;RefreshSelection();}
            }
            if(Keyboard.current?.spaceKey.wasPressedThisFrame==true)FocusSelection();
            if(Keyboard.current?.hKey.wasPressedThisFrame==true)CommandHold();
            if(Keyboard.current?.tabKey.wasPressedThisFrame==true)SelectAllArmy();
            bool hasGround=GroundAt(pointer,out var point);
            if(placing.HasValue)
            {
                if(!SelectedUnits().Any(u=>u.Kind==UnitKind.Worker)){CancelPlacement();SetStatus("Выберите рабочего для строительства.");return;}
                if(rightCommand){CancelPlacement();return;}
                if(hasGround)
                {
                    var cell=Battlefield.ToCell(point);var kind=placing.Value;
                    var footprint=World.Footprint(kind,cell).ToArray();
                    bool flat=footprint.All(World.InBounds)&&footprint.Max(c=>Battlefield.ToWorld(c).y)-footprint.Min(c=>Battlefield.ToWorld(c).y)<=.85f;
                    bool valid=flat&&footprint.All(playerVisible.Contains)&&World.CanPlaceBuilding(kind,cell);
                    if(placementPreview)
                    {
                        placementPreview.transform.position=Battlefield.ToWorld(cell)+Vector3.up*.08f;
                        var d=World.Definitions.Building(kind);placementPreview.transform.localScale=new Vector3(d.FootprintSizeX*Battlefield.CellSize,1,d.FootprintSizeY*Battlefield.CellSize);
                        var line=placementPreview.GetComponentInChildren<LineRenderer>();if(line)line.startColor=line.endColor=valid?new Color(.3f,1,.65f):new Color(1,.25f,.2f);
                    }
                    if(mouse.leftButton.wasPressedThisFrame&&!overUi)
                    {
                        if(!valid){SetStatus("Нужна свободная разведанная площадка.");ProceduralAudio.Instance?.Play(GameSound.Error);return;}
                        foreach(var worker in SelectedUnits().Where(u=>u.Kind==UnitKind.Worker))IssueOrder(worker,Order.Build(kind,cell));
                        SetStatus("Рабочим передан проект: "+BuildingName(kind)+".");CancelPlacement();ProceduralAudio.Instance?.Play(GameSound.Order);
                    }
                }
                return;
            }
            if(mouse.leftButton.wasPressedThisFrame&&!overUi){selectionPressed=true;dragStart=pointer;dragging=false;}
            if(selectionPressed&&mouse.leftButton.isPressed)
            {dragging|=(pointer-dragStart).sqrMagnitude>64;if(Hud)Hud.SetSelectionRect(dragStart,pointer,dragging);}
            if(selectionPressed&&mouse.leftButton.wasReleasedThisFrame)
            {
                selectionPressed=false;if(Hud)Hud.SetSelectionRect(dragStart,pointer,false);
                bool additive=Keyboard.current!=null&&(Keyboard.current.leftShiftKey.isPressed||Keyboard.current.rightShiftKey.isPressed);
                if(!additive)selection.Clear();
                if(dragging)
                {
                    var lo=Vector2.Min(dragStart,pointer);var hi=Vector2.Max(dragStart,pointer);
                    foreach(var u in World.Units.Where(u=>u.Alive&&u.Faction==PlayerFaction))
                    {var p=ViewCamera.WorldToScreenPoint(unitViews[u.Id].transform.position+Vector3.up);if(p.z>0&&p.x>=lo.x&&p.x<=hi.x&&p.y>=lo.y&&p.y<=hi.y)selection.Add(u.Id);}
                }
                else if(!overUi)
                {
                    string id=EntityAt(pointer);if(id!=null){if(additive&&selection.Contains(id))selection.Remove(id);else selection.Add(id);}
                }
                RefreshSelection();ProceduralAudio.Instance?.Play(GameSound.Select);dragging=false;
            }
            if(rightCommand&&!overUi&&hasGround)IssueContextCommand(pointer,point);
        }
        void IssueContextCommand(Vector2 pointer,Vector3 point)
        {
            var selected=SelectedUnits().ToList();if(selected.Count==0)return;
            // Selection boxes include empty air around roofs and can overlap a tree on screen.
            // A completed friendly building is selectable, but cannot consume a worker command.
            string target=EntityAt(pointer,true,selected.Any(u=>u.Kind==UnitKind.Worker));
            var enemy=World.Unit(target);var building=World.Building(target);var resource=World.Resource(target);
            if(resource!=null&&resource.Remaining>0)
            {foreach(var u in selected)if(u.Kind==UnitKind.Worker)IssueOrder(u,Order.Gather(resource.Id,resource.Cell));SetStatus("Приказ добывать "+(resource.Kind==ResourceKind.Wood?"дерево.":"золото."));}
            else if(enemy!=null&&enemy.Faction!=PlayerFaction&&enemy.Alive)
            {foreach(var u in selected)IssueOrder(u,Order.Attack(enemy.Id,enemy.Cell));SetStatus("Приказ атаковать.");}
            else if(building!=null&&building.Alive&&(building.Faction!=PlayerFaction||(!building.Complete&&selected.Any(u=>u.Kind==UnitKind.Worker))))
            {
                foreach(var u in selected)
                    if(building.Faction!=PlayerFaction)IssueOrder(u,Order.Attack(building.Id,building.Cell));
                    else if(!building.Complete&&u.Kind==UnitKind.Worker)IssueOrder(u,Order.Build(building.Kind,building.Cell));
                SetStatus(building.Faction==PlayerFaction?"Приказ продолжить строительство.":"Приказ атаковать здание.");
            }
            else
            {
                var targetCell=Battlefield.ToCell(point);
                if(!World.InBounds(targetCell)){SetStatus("Цель находится за границей карты.");return;}
                if(World.Blocked.Contains(targetCell)){SetStatus("Выберите проходимый участок земли.");ProceduralAudio.Instance?.Play(GameSound.Error);return;}
                // A group receives one common target. Every member chooses its own movement and resting place.
                if(selected.Count==1)IssueOrder(selected[0],Order.Move(targetCell));
                else
                {
                    var result=IssueGroupMove(selected.Select(u=>u.Id).ToArray(),targetCell);
                    if(!result.Ok){SetStatus("Для всего отряда здесь недостаточно места. Выберите более просторную точку.");return;}
                }
                SetStatus("Приказ двигаться.");
            }
            ProceduralAudio.Instance?.Play(GameSound.Order);
        }
        string EntityAt(Vector2 screen,bool forCommand=false,bool canWork=false)
        {
            foreach(var hit in Physics.RaycastAll(ViewCamera.ScreenPointToRay(screen),300,~0,QueryTriggerInteraction.Collide).OrderBy(h=>h.distance))
            {
                var unit=hit.collider.GetComponentInParent<UnitView>();
                if(unit!=null){var state=World.Unit(unit.Id);if(state!=null&&state.Alive&&(!forCommand||state.Faction!=PlayerFaction)&&(state.Faction==PlayerFaction||playerVisible.Contains(state.Cell)))return unit.Id;}
                var entity=hit.collider.GetComponentInParent<EntityView>();
                if(entity!=null)
                {
                    var b=World.Building(entity.Id);var r=World.Resource(entity.Id);
                    if(b!=null&&b.Alive&&(!forCommand||b.Faction!=PlayerFaction||(!b.Complete&&canWork))&&(b.Faction==PlayerFaction||World.Footprint(b).Any(playerVisible.Contains)))return entity.Id;
                    if(r!=null&&r.Remaining>0&&(!forCommand||canWork)&&playerVisible.Contains(r.Cell))return entity.Id;
                }
            }
            return null;
        }
        bool GroundAt(Vector2 screen,out Vector3 point)
        {
            var ray=ViewCamera.ScreenPointToRay(screen);
            foreach(var hit in Physics.RaycastAll(ray,400,~0,QueryTriggerInteraction.Ignore).OrderBy(h=>h.distance))
                if(hit.collider.name.Contains("scene-terrain")||hit.collider.transform.root.name=="00 Terrain"||hit.collider.name.Contains("bridge"))
                {point=hit.point;return true;}
            var plane=new Plane(Vector3.up,Vector3.zero);
            if(plane.Raycast(ray,out float distance)){point=ray.GetPoint(distance);return true;}point=default;return false;
        }
        void RefreshSelection()
        {
            foreach(var pair in unitViews)pair.Value.SetSelected(selection.Contains(pair.Key));
            foreach(var pair in buildingViews)pair.Value.SetSelected(selection.Contains(pair.Key));
            foreach(var pair in resourceViews)pair.Value.SetSelected(selection.Contains(pair.Key)||pair.Key==hoveredResource);
        }
    }
}
