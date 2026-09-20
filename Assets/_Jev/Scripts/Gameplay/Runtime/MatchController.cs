using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using Newtonsoft.Json.Linq;
using Jev.Gameplay.Authoring;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.Jev;
using Jev.Gameplay.UI;
using Jev.Gameplay.Presentation;
using Jev.Gameplay.Camera;
using Jev.Gameplay.Audio;

namespace Jev.Gameplay.Runtime
{
    [DisallowMultipleComponent]
    public sealed partial class MatchController : GameUiBridge
    {
        public BattlefieldDefinition Battlefield;
        public GameCatalog Catalog;
        public MatchSettings Settings;
        public JevTransport Transport;
        public MatchHud Hud;
        public RtsCameraController CameraRig;
        public UnityEngine.Camera ViewCamera;
        public Transform UnitsRoot, BuildingsRoot, ResourcesRoot;
        public RtsWorld World { get; private set; }
        public MatchPhase Phase { get; private set; } = MatchPhase.Setup;
        public FactionId PlayerFaction { get; private set; }
        public int UnitCap { get; private set; } = 20;
        public string SessionId { get; private set; }
        public IReadOnlyCollection<string> SelectedIds => selection;
        readonly Dictionary<string,UnitView> unitViews = new Dictionary<string,UnitView>();
        readonly Dictionary<string,UnitBrain> brains = new Dictionary<string,UnitBrain>();
        readonly Dictionary<string,EntityView> buildingViews = new Dictionary<string,EntityView>();
        readonly Dictionary<string,EntityView> resourceViews = new Dictionary<string,EntityView>();
        readonly Dictionary<string,GameObject> resourceVisuals = new Dictionary<string,GameObject>();
        readonly Dictionary<string,int> lastHp = new Dictionary<string,int>();
        readonly HashSet<string> selection = new HashSet<string>();
        readonly HashSet<Cell> playerVisible = new HashSet<Cell>();
        readonly HashSet<Cell> playerExplored = new HashSet<Cell>();
        readonly List<JObject> decisions = new List<JObject>();
        const int MaxRetainedDecisions = 512;
        long decisionSequence;
        bool reportWarningReported;
        string status = "Выберите фракцию и начните с ратуши и одного рабочего.";
        float nextVisibilityAt, statusUntil;
        long observedEvents;
        int sessionSerial;
        BuildingKind? placing;
        GameObject placementPreview;
        Vector2 dragStart;
        bool dragging, selectionPressed;
        int nextBrainOffset;

        void Awake()
        {
            UnitCap = Settings.DefaultUnitCap;
            if (Hud) Hud.Initialize(this);
            if (CameraRig) CameraRig.ControlsEnabled = false;
        }
        void OnDestroy() { if(Transport) Transport.CancelAll(); Time.timeScale=1; }
        void Update()
        {
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true)
            {
                if(placing.HasValue){CancelPlacement();return;}
                if(Phase==MatchPhase.Playing)Pause();else if(Phase==MatchPhase.Paused)Resume();
            }
            if(Phase!=MatchPhase.Playing || World==null)return;
            World.Tick(Time.deltaTime);
            SyncViews();
            HandleInput();
            UpdateBrains();
            if(!World.MatchEnded&&Transport.IsBlocked)
            {Pause();SetStatus(Transport.BlockingErrorMessage,3600);return;}
            if(!World.MatchEnded){UpdateCommander();UpdateTowers();}
            if(Time.time>=nextVisibilityAt){nextVisibilityAt=Time.time+.5f;RefreshVisibility();}
            if(World.MatchEnded){ResetSelectionGesture();SyncViews();Phase=World.Draw?MatchPhase.Draw:World.Winner==PlayerFaction?MatchPhase.Victory:MatchPhase.Defeat;Transport.CancelAll();SetStatus(World.Draw?"Ничья. Обе ратуши уничтожены.":Phase==MatchPhase.Victory?"Победа. Ратуша противника уничтожена.":"Поражение. Ваша последняя ратуша уничтожена.",100);ProceduralAudio.Instance?.Play(Phase==MatchPhase.Victory?GameSound.Victory:GameSound.Death);SaveSessionReport();}
        }

        public override void StartMatch(FactionId faction,int cap)
        {
            if(Battlefield==null||Catalog==null||Settings==null||Transport==null){SetStatus("Не назначены настройки матча.",20);return;}
            Transport.ReloadCredentials();
            if(!Transport.IsConfigured){SetStatus("Нужен ключ JEV_API_KEY в LocalSettings/jev.env.",30);return;}
            ClearMatch();
            Transport.BeginMatchCost();
            PlayerFaction=faction; UnitCap=Mathf.Clamp(cap,1,60);SessionId=Guid.NewGuid().ToString("N");sessionSerial++;
            World=CreateWorld();
            Phase=MatchPhase.Playing;Time.timeScale=1;
            nextCommanderAt=Time.time+1;commanderPending=false;commanderTicket=0;
            if(CameraRig){CameraRig.ControlsEnabled=true;CameraRig.Focus(Battlefield.ToWorld((faction==FactionId.Human?Battlefield.Human:Battlefield.Undead).Worker),true);}
            CreateResourceViews();SyncViews();RefreshVisibility();
            string worker=World.Units.First(u=>u.Faction==faction&&u.Alive).Id;
            selection.Add(worker);RefreshSelection();
            SetStatus("ЛКМ — выбор · ПКМ — приказ · рабочий собирает ресурсы и строит.",12);
        }

        RtsWorld CreateWorld() => MatchWorldFactory.Create(Battlefield, Catalog, Settings, PlayerFaction, UnitCap);

        void ClearMatch()
        {
            ClearConstructionSites();
            if(Feedback)Feedback.Clear();
            ApprovedStepCount=0;RejectedStepCount=0;
            ResetSelectionGesture();sessionSerial++;Transport.CancelAll();completedDecisions.Clear();readyUnitDecisions.Clear();ClearDecisionPreparations();CancelPlacement();selection.Clear();playerVisible.Clear();playerExplored.Clear();decisions.Clear();decisionSequence=0;reportWarningReported=false;lastHp.Clear();
            foreach(var view in unitViews.Values)if(view)Destroy(view.gameObject);
            foreach(var view in buildingViews.Values)if(view)Destroy(view.gameObject);
            foreach(var view in resourceViews.Values)if(view)Destroy(view.gameObject);
            foreach(var visual in resourceVisuals.Values)if(visual)visual.SetActive(true);
            unitViews.Clear();brains.Clear();buildingViews.Clear();resourceViews.Clear();resourceVisuals.Clear();towerBrains.Clear();commanderEnemyMemory.Clear();commanderResourceMemory.Clear();commanderWorker=null;commanderPending=false;observedEvents=0;
            World=null;
        }

        public override void Pause(){if(Phase!=MatchPhase.Playing)return;ResetSelectionGesture();Phase=MatchPhase.Paused;Time.timeScale=0;if(CameraRig)CameraRig.ControlsEnabled=false;}
        public override void Resume(){if(Phase!=MatchPhase.Paused)return;Phase=MatchPhase.Playing;Time.timeScale=1;if(CameraRig)CameraRig.ControlsEnabled=true;}
        public override void Restart(){SaveSessionReport();ClearMatch();Phase=MatchPhase.Setup;Time.timeScale=1;if(CameraRig)CameraRig.ControlsEnabled=false;SetStatus("Выберите фракцию для нового сражения.",100);}
        public override void FocusSelection(){var id=selection.FirstOrDefault();var resource=World?.Resource(id);var unit=World?.Units.FirstOrDefault(u=>u.Id==id);var building=World?.Buildings.FirstOrDefault(b=>b.Id==id);if(unit!=null&&(unit.Faction==PlayerFaction||playerVisible.Contains(unit.Cell)))CameraRig.Focus(Battlefield.ToWorld(unit.Cell));else if(building!=null&&(building.Faction==PlayerFaction||World.Footprint(building).Any(playerVisible.Contains)))CameraRig.Focus(Battlefield.ToWorld(building.Cell));else if(resource!=null&&resource.Remaining>0&&playerVisible.Contains(resource.Cell))CameraRig.Focus(Battlefield.ToWorld(resource.Cell));}
        public override void SelectAllArmy(){if(World==null)return;selection.Clear();foreach(var u in World.Units.Where(u=>u.Alive&&u.Faction==PlayerFaction&&u.Kind!=UnitKind.Worker))selection.Add(u.Id);RefreshSelection();}
        public override void CommandHold(){foreach(var u in SelectedUnits())IssueOrder(u,Order.Hold(u.Cell));SetStatus("Приказ удерживать позицию.");}
        public override void CommandStop(){CommandHold();CancelPlacement();}
        public override void Train(UnitKind kind)
        {
            var building=World?.Buildings.FirstOrDefault(b=>selection.Contains(b.Id)&&b.Faction==PlayerFaction&&b.Alive);
            if(building==null)return;
            var result=World.EnqueueTraining(building.Id,kind,PlayerFaction);
            SetStatus(result.Ok?"Юнит добавлен в очередь найма.":FriendlyError(result.Reason));
            ProceduralAudio.Instance?.Play(result.Ok?GameSound.Click:GameSound.Error);
        }
        public override void ChooseBuild(BuildingKind kind)
        {
            if(!SelectedUnits().Any(u=>u.Kind==UnitKind.Worker))return;
            placing=kind;if(placementPreview)Destroy(placementPreview);
            if(Catalog.BuildPreview)placementPreview=Instantiate(Catalog.BuildPreview);
            SetStatus("Выберите свободное место постройки. Esc — отмена.",60);
        }
        void CancelPlacement(){placing=null;if(placementPreview)Destroy(placementPreview);placementPreview=null;}
        IEnumerable<UnitState> SelectedUnits()=>World==null?Enumerable.Empty<UnitState>():World.Units.Where(u=>u.Alive&&u.Faction==PlayerFaction&&selection.Contains(u.Id));
        public bool IssueOrder(UnitState unit,Order order)
        {
            if(World==null||unit==null||order==null||!unit.Alive)return false;
            if(unit.Order!=null&&unit.Order.Kind==order.Kind&&unit.Order.Cell==order.Cell&&unit.Order.TargetId==order.TargetId&&unit.Order.BuildingKind==order.BuildingKind)
            {
                // Repeating a command wakes a waiting unit, without cancelling a request
                // already in flight or discarding an approved segment for the same order.
                if(brains.TryGetValue(unit.Id,out var waiting)&&!waiting.Pending&&!waiting.DecisionQueued&&waiting.ApprovedRoute.Count==0)
                {waiting.Wake("repeated_order");RequestUnitDecision(unit,waiting,true);}
                return true;
            }
            if(!World.SetOrder(unit.Id,order))return false;
            if(brains.TryGetValue(unit.Id,out var brain))
                RequestChangedOrder(unit,brain);
            return true;
        }
        public void SetStatus(string text,float duration=5){status=text;statusUntil=Time.unscaledTime+duration;}
        static string FriendlyError(string reason)=>reason switch {"insufficient_wood"=>"Недостаточно дерева.","insufficient_gold"=>"Недостаточно золота.","population_capacity"=>"Не хватает жилья: постройте дом.","faction_unit_limit"=>"Достигнут лимит юнитов фракции.","build_site_occupied"=>"Здесь нельзя разместить постройку.",_=>"Действие недоступно: "+reason};
        public override UiSnapshot Snapshot
        {
            get
            {
                var snapshot=new UiSnapshot{Phase=Phase,PlayerFaction=PlayerFaction,Cap=UnitCap,Status=Time.unscaledTime<statusUntil||Phase==MatchPhase.Setup?status:"ЛКМ — выбор · ПКМ — приказ · Esc — пауза",
                    JevCostLabel=Transport!=null?Transport.MatchCost.Label:"",JevCostTooltip=Transport!=null?Transport.MatchCost.Tooltip:"",
                    JevStatus=Transport==null?"JEV не подключён":Transport.IsConfigured?$"JEV · {Transport.SuccessCount} решений · {Transport.InFlight} в работе": "JEV · нужен ключ"};
                if(World==null)return snapshot;
                var faction=World.Faction(PlayerFaction);snapshot.Wood=faction.Wood;snapshot.Gold=faction.Gold;snapshot.Used=faction.Population.Used;snapshot.Cap=faction.Population.Capacity;snapshot.Reserved=faction.Population.Reserved;
                var units=SelectedUnits().ToList();var building=World.Buildings.FirstOrDefault(b=>selection.Contains(b.Id)&&b.Alive);
                if(units.Count>0)
                {
                    snapshot.SelectedIconKey=(units[0].Kind==UnitKind.Warrior?"swordsman":units[0].Kind.ToString().ToLowerInvariant())+"-"+units[0].Faction.ToString().ToLowerInvariant();
                    snapshot.HasSelection=true;snapshot.SelectedName=units.Count==1?UnitName(units[0].Kind):"Отряд · "+units.Count;
                    snapshot.Hp=units.Sum(u=>u.Hp);snapshot.HpMax=units.Sum(u=>u.MaxHp);snapshot.CanBuild=units.Any(u=>u.Kind==UnitKind.Worker);snapshot.HasArmySelection=units.Any(u=>u.Kind!=UnitKind.Worker);
                    snapshot.Description=units.Count==1?OrderLabel(units[0]):"ПКМ — общая точка сбора.";
                }
                else if(building!=null)
                {
                    snapshot.SelectedIconKey=building.Kind.ToString().ToLowerInvariant()+"-"+building.Faction.ToString().ToLowerInvariant();
                    snapshot.HasSelection=true;snapshot.SelectedName=BuildingName(building.Kind);snapshot.Hp=building.Hp;snapshot.HpMax=building.MaxHp;
                    snapshot.Description=building.Complete?"Готово":"Строительство: "+building.Progress+" / "+building.RequiredWork;
                    var trainable=World.Definitions.Building(building.Kind).TrainableUnits;
                    bool own=building.Faction==PlayerFaction&&building.Complete;
                    snapshot.CanTrainWorker=own&&trainable.Contains(UnitKind.Worker);snapshot.CanTrainWarrior=own&&trainable.Contains(UnitKind.Warrior);snapshot.CanTrainArcher=own&&trainable.Contains(UnitKind.Archer);
                    ProductionQueueSnapshot.Apply(snapshot, building, World.Definitions, PlayerFaction);
                }
                else if(selection.Count>0)
                {var r=World.Resources.FirstOrDefault(r=>selection.Contains(r.Id));if(r!=null){snapshot.HasSelection=true;snapshot.SelectedName=r.Kind==ResourceKind.Wood?"Древесина":"Золотая жила";snapshot.Description="Осталось: "+r.Remaining;}}
                if(!string.IsNullOrEmpty(Transport.LastError))snapshot.JevStatus="JEV · "+Transport.LastError;
                return snapshot;
            }
        }
        static string UnitName(UnitKind kind)=>kind==UnitKind.Worker?"Рабочий":kind==UnitKind.Warrior?"Мечник":"Лучник";
        static string BuildingName(BuildingKind kind)=>kind switch {BuildingKind.TownHall=>"Ратуша",BuildingKind.House=>"Дом",BuildingKind.Barracks=>"Казарма",BuildingKind.ArcheryRange=>"Стрельбище",_=>"Сторожевая башня"};
        string OrderLabel(UnitState u)
        {
            if(u.Order==null)return "Ожидает приказа";
            if(u.Order.Kind==OrderKind.Build)
            {
                // Own buildings are known even outside sight. Never infer completion from another faction's building.
                bool completed=u.Faction==PlayerFaction&&World.Buildings.Any(b=>b.Alive&&b.Complete&&b.Faction==u.Faction&&b.Kind==u.Order.BuildingKind&&b.Cell==u.Order.Cell);
                return completed?"Строительство завершено":"Строит: "+BuildingName(u.Order.BuildingKind);
            }
            if(u.Order.Kind==OrderKind.Gather)
            {
                var resource=World.Resource(u.Order.TargetId);
                // A resource changing in unexplored/fogged space must not update the player's label.
                return resource!=null&&playerVisible.Contains(resource.Cell)&&resource.Remaining<=0?"Ресурс исчерпан":"Добыча ресурсов";
            }
            return u.Order.Kind switch{OrderKind.Attack=>"Атакует цель",OrderKind.Hold=>"Удерживает позицию",_=>"Движется к цели"};
        }
    }
}
