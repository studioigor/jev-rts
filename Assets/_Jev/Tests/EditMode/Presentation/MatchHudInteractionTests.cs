using System.Reflection;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class HudTestBridge : GameUiBridge
    {
        public readonly UiSnapshot State = new UiSnapshot();
        public int TrainingCalls;
        public override UiSnapshot Snapshot => State;
        public override void StartMatch(FactionId faction, int cap) { }
        public override void Train(UnitKind kind) { TrainingCalls++; }
        public override void ChooseBuild(BuildingKind kind) { }
        public override void CommandHold() { }
        public override void CommandStop() { }
        public override void Pause() { State.Phase = MatchPhase.Paused; }
        public override void Resume() { State.Phase = MatchPhase.Playing; }
        public override void Restart() { State.Phase = MatchPhase.Setup; }
        public override void SelectAllArmy() { }
        public override void FocusSelection() { }
    }

    public sealed class MatchHudInteractionTests
    {
        GameObject root;
        MatchHud hud;
        HudTestBridge bridge;
        EventSystem events;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("HUD test", typeof(RectTransform));
            hud = root.AddComponent<MatchHud>();
            bridge = root.AddComponent<HudTestBridge>();
            events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
            events.transform.SetParent(root.transform, false);
            typeof(MatchHud).GetField("uiEventSystem", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(hud, events);
            hud.humanButton = Button("Human"); hud.undeadButton = Button("Undead");
            hud.resumeButton = Button("Resume"); hud.resultRestartButton = Button("ResultRestart");
            hud.trainWorker = Button("Train"); hud.holdButton = Button("Hold"); hud.stopButton = Button("Stop"); hud.focusButton = Button("Focus");
            hud.Initialize(bridge);
        }

        [TearDown] public void TearDown() { Object.DestroyImmediate(root); }

        Button Button(string name)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Button));
            obj.transform.SetParent(root.transform, false);
            return obj.GetComponent<Button>();
        }

        void Refresh(MatchPhase phase)
        {
            bridge.State.Phase = phase;
            typeof(MatchHud).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hud, new object[] { bridge.State });
        }

        [Test]
        public void KeyboardNavigationBelongsToCameraOnlyWhilePlaying()
        {
            Refresh(MatchPhase.Setup);
            Assert.That(events.sendNavigationEvents, Is.True);
            Assert.That(events.currentSelectedGameObject, Is.EqualTo(hud.humanButton.gameObject));
            Refresh(MatchPhase.Playing);
            Assert.That(events.sendNavigationEvents, Is.False);
            Assert.That(events.currentSelectedGameObject, Is.Null);
            events.SetSelectedGameObject(hud.trainWorker.gameObject);
            Refresh(MatchPhase.Paused);
            Assert.That(events.sendNavigationEvents, Is.True);
            Assert.That(events.currentSelectedGameObject, Is.EqualTo(hud.resumeButton.gameObject));
            foreach (var phase in new[] { MatchPhase.Victory, MatchPhase.Defeat, MatchPhase.Draw })
            {
                Refresh(phase);
                Assert.That(events.currentSelectedGameObject, Is.EqualTo(hud.resultRestartButton.gameObject));
            }
        }

        [Test]
        public void BackgroundSubmitCannotTrainDuringPauseResultsOrSetup()
        {
            typeof(MatchHud).GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hud, new object[]
                { hud.trainWorker, (UnityAction)(() => bridge.Train(UnitKind.Worker)), new[] { MatchPhase.Playing } });
            bridge.State.Phase = MatchPhase.Playing;
            hud.trainWorker.onClick.Invoke();
            Assert.That(bridge.TrainingCalls, Is.EqualTo(1));
            // Exercise the instant before a throttled HUD refresh can disable the background button.
            foreach (var phase in new[] { MatchPhase.Paused, MatchPhase.Victory, MatchPhase.Defeat, MatchPhase.Draw, MatchPhase.Setup })
            {
                bridge.State.Phase = phase;
                hud.trainWorker.onClick.Invoke();
            }
            Assert.That(bridge.TrainingCalls, Is.EqualTo(1));
        }

        [Test]
        public void PauseAndRestartHideUnfinishedMarquee()
        {
            hud.selectionRectangle = new GameObject("Marquee", typeof(RectTransform)).GetComponent<RectTransform>();
            hud.selectionRectangle.SetParent(root.transform, false);
            Refresh(MatchPhase.Playing);
            hud.selectionRectangle.gameObject.SetActive(true);
            Refresh(MatchPhase.Paused);
            Assert.That(hud.selectionRectangle.gameObject.activeSelf, Is.False);
            hud.selectionRectangle.gameObject.SetActive(true);
            Refresh(MatchPhase.Setup);
            Assert.That(hud.selectionRectangle.gameObject.activeSelf, Is.False);
        }

        [Test]
        public void ResourceCardDoesNotOfferUnitCommandsOrEmptyHealthBar()
        {
            var health = new GameObject("Health", typeof(RectTransform)); health.transform.SetParent(root.transform, false);
            hud.healthFill = new GameObject("Fill", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            hud.healthFill.transform.SetParent(health.transform, false);
            bridge.State.HasSelection = true; bridge.State.HpMax = 0;
            Refresh(MatchPhase.Playing);
            Assert.That(hud.holdButton.interactable, Is.False);
            Assert.That(hud.stopButton.interactable, Is.False);
            Assert.That(hud.focusButton.interactable, Is.True);
            Assert.That(health.activeSelf, Is.False);
            bridge.State.HasArmySelection = true; bridge.State.HpMax = 100; bridge.State.Hp = 50;
            Refresh(MatchPhase.Playing);
            Assert.That(hud.holdButton.interactable, Is.True);
            Assert.That(health.activeSelf, Is.True);
            Assert.That(hud.healthFill.rectTransform.anchorMax.x, Is.EqualTo(.5f));
        }

        [Test]
        public void TrainingButtonsOnlyExposeTypesAvailableInSelectedBuilding()
        {
            hud.trainWarrior = Button("TrainWarrior"); hud.trainArcher = Button("TrainArcher");
            bridge.State.HasSelection = true; bridge.State.CanTrainWorker = true;
            Refresh(MatchPhase.Playing);
            Assert.That(hud.trainWorker.gameObject.activeSelf, Is.True);
            Assert.That(hud.trainWarrior.gameObject.activeSelf, Is.False);
            Assert.That(hud.trainArcher.gameObject.activeSelf, Is.False);
            bridge.State.CanTrainWorker = false; bridge.State.CanTrainArcher = true;
            Refresh(MatchPhase.Playing);
            Assert.That(hud.trainWorker.gameObject.activeSelf, Is.False);
            Assert.That(hud.trainWarrior.gameObject.activeSelf, Is.False);
            Assert.That(hud.trainArcher.gameObject.activeSelf, Is.True);
            Refresh(MatchPhase.Paused);
            Assert.That(hud.trainArcher.gameObject.activeSelf, Is.True);
            Assert.That(hud.trainArcher.interactable, Is.False);
        }

        [Test]
        public void ProductionProgressAndSlotsClearWhenSelectionChanges()
        {
            hud.productionPanel = new GameObject("Production", typeof(RectTransform)).GetComponent<RectTransform>();
            hud.productionPanel.SetParent(root.transform, false);
            var track = new GameObject("Progress", typeof(RectTransform)); track.transform.SetParent(hud.productionPanel, false);
            hud.productionProgressFill = new GameObject("Fill", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            hud.productionProgressFill.transform.SetParent(track.transform, false);
            var slot = new GameObject("Slot", typeof(RectTransform)); slot.transform.SetParent(hud.productionPanel, false);
            hud.productionSlots = new[] { new MatchHud.ProductionSlot { Root = slot } };
            bridge.State.HasProduction = true; bridge.State.CanTrainWorker = true;
            bridge.State.ProductionQueue = new[] { new ProductionQueueItem { Kind = UnitKind.Worker } };
            bridge.State.ProductionProgress = .75f;
            Refresh(MatchPhase.Playing);
            Assert.That(hud.productionPanel.gameObject.activeSelf, Is.True);
            Assert.That(slot.activeSelf, Is.True);
            Assert.That(hud.productionProgressFill.rectTransform.anchorMax.x, Is.EqualTo(.75f));
            bridge.State.HasProduction = false; bridge.State.CanTrainWorker = false;
            bridge.State.ProductionQueue = System.Array.Empty<ProductionQueueItem>();
            bridge.State.ProductionProgress = 0;
            Refresh(MatchPhase.Playing);
            Assert.That(hud.productionPanel.gameObject.activeSelf, Is.False);
            Assert.That(slot.activeSelf, Is.False);
            Assert.That(track.activeSelf, Is.False);
        }
    }
}
