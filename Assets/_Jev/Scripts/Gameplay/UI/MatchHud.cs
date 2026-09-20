using Jev.Gameplay.Audio;
using Jev.Gameplay.Simulation;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Jev.Gameplay.UI
{
    /// <summary>Updates the authored Canvas; no visual hierarchy is generated during play.</summary>
    [DisallowMultipleComponent]
    public sealed class MatchHud : MonoBehaviour
    {
        [Header("Match")]
        [SerializeField] GameUiBridge bridge;
        [Header("Authored panels")]
        public GameObject setupPanel, playingPanel, pausePanel, resultPanel, selectionPanel;
        public RectTransform selectionRectangle;
        [Header("Text")]
        public TMPro.TMP_Text resourcesText, selectionName, selectionDescription, healthText, queueText, statusText, jevStatusText;
        public TMPro.TMP_Text capText, factionDescription, resultTitle, resultDescription;
        public UnityEngine.UI.Image healthFill, factionAccent;
        [Header("Setup")]
        public UnityEngine.UI.Button humanButton, undeadButton, startButton;
        public UnityEngine.UI.Slider unitCapSlider;
        [Header("Commands")]
        public UnityEngine.UI.Button trainWorker, trainWarrior, trainArcher;
        public UnityEngine.UI.Button buildTownHall, buildHouse, buildBarracks, buildArcheryRange, buildTower;
        public UnityEngine.UI.Button holdButton, stopButton, focusButton, armyButton, pauseButton, resumeButton, restartButton, resultRestartButton;
        public GameObject trainingGroup, buildingGroup, unitCommandsGroup;
        [Header("Visual style")]
        public UnityEngine.UI.Image selectionPortrait, humanCardBorder, undeadCardBorder;
        public TMPro.TMP_Text woodValue, goldValue, populationValue, commandTitle;
        public TMPro.TMP_Text jevCostText;
        public RectTransform actionPanel;
        public SpriteEntry[] portraits = System.Array.Empty<SpriteEntry>();
        public UnityEngine.UI.Image[] commandIcons = System.Array.Empty<UnityEngine.UI.Image>();
        [Header("Production queue")]
        public RectTransform productionPanel;
        public TMPro.TMP_Text productionLabel, productionProgressLabel, productionOverflow;
        public UnityEngine.UI.Image productionProgressFill;
        public ProductionSlot[] productionSlots = System.Array.Empty<ProductionSlot>();
        [System.Serializable] public sealed class ProductionSlot
        {
            public GameObject Root;
            public UnityEngine.UI.Image Portrait, Accent;
            public TMPro.TMP_Text Number;
        }
        public bool showDiagnostics;
        [System.Serializable] public sealed class SpriteEntry { public string Id; public Sprite Sprite; }
        FactionId chosenFaction;
        int chosenCap = 20;
        float refreshAt;
        float nextCostRefresh;
        MatchPhase lastPhase = (MatchPhase)(-1);
        EventSystem uiEventSystem;
        readonly Color gold = new Color(.88f, .73f, .44f), cyan = new Color(.3f, .84f, .8f);

        public void Initialize(GameUiBridge source)
        {
            bridge = source; refreshAt = 0; nextCostRefresh = 0;
            chosenCap = Mathf.Clamp(bridge != null ? bridge.DefaultUnitCap : 20, 1, 60);
            if (unitCapSlider != null) unitCapSlider.SetValueWithoutNotify(chosenCap);
            RefreshSetup();
        }
        public GameUiBridge Bridge => bridge;

        void Awake()
        {
            uiEventSystem = GetComponentInChildren<EventSystem>(true);
            Bind(humanButton, () => ChooseFaction(FactionId.Human), MatchPhase.Setup);
            Bind(undeadButton, () => ChooseFaction(FactionId.Undead), MatchPhase.Setup);
            Bind(startButton, () => bridge?.StartMatch(chosenFaction, chosenCap), MatchPhase.Setup);
            Bind(trainWorker, () => bridge?.Train(UnitKind.Worker), MatchPhase.Playing);
            Bind(trainWarrior, () => bridge?.Train(UnitKind.Warrior), MatchPhase.Playing);
            Bind(trainArcher, () => bridge?.Train(UnitKind.Archer), MatchPhase.Playing);
            Bind(buildTownHall, () => bridge?.ChooseBuild(BuildingKind.TownHall), MatchPhase.Playing);
            Bind(buildHouse, () => bridge?.ChooseBuild(BuildingKind.House), MatchPhase.Playing);
            Bind(buildBarracks, () => bridge?.ChooseBuild(BuildingKind.Barracks), MatchPhase.Playing);
            Bind(buildArcheryRange, () => bridge?.ChooseBuild(BuildingKind.ArcheryRange), MatchPhase.Playing);
            Bind(buildTower, () => bridge?.ChooseBuild(BuildingKind.Tower), MatchPhase.Playing);
            Bind(holdButton, () => bridge?.CommandHold(), MatchPhase.Playing);
            Bind(stopButton, () => bridge?.CommandStop(), MatchPhase.Playing);
            Bind(focusButton, () => bridge?.FocusSelection(), MatchPhase.Playing);
            Bind(armyButton, () => bridge?.SelectAllArmy(), MatchPhase.Playing);
            Bind(pauseButton, () => bridge?.Pause(), MatchPhase.Playing);
            Bind(resumeButton, () => bridge?.Resume(), MatchPhase.Paused);
            Bind(restartButton, () => bridge?.Restart(), MatchPhase.Paused);
            Bind(resultRestartButton, () => bridge?.Restart(), MatchPhase.Victory, MatchPhase.Defeat, MatchPhase.Draw);
            if (unitCapSlider != null)
            {
                chosenCap = Mathf.Clamp(bridge != null ? bridge.DefaultUnitCap : 20, 1, 60);
                unitCapSlider.SetValueWithoutNotify(chosenCap);
                unitCapSlider.onValueChanged.AddListener(value => { chosenCap = Mathf.RoundToInt(value); RefreshSetup(); });
            }
            ChooseFaction(FactionId.Human);
            if (selectionRectangle != null) selectionRectangle.gameObject.SetActive(false);
        }

        void Bind(UnityEngine.UI.Button button, UnityEngine.Events.UnityAction action, params MatchPhase[] allowedPhases)
        {
            if (button == null) return;
            button.onClick.AddListener(() =>
            {
                // A selected background button can still receive Submit while a modal opens.
                if (bridge == null || System.Array.IndexOf(allowedPhases, bridge.Snapshot.Phase) < 0) return;
                ProceduralAudio.Instance?.Play(GameSound.Click); action(); Refresh(bridge.Snapshot);
            });
        }

        void ChooseFaction(FactionId faction) { chosenFaction = faction; RefreshSetup(); }

        void RefreshSetup()
        {
            if (capText != null) capText.text = $"ЛИМИТ ОТРЯДА <color=#E9D7AD><b>{chosenCap}</b></color>";
            if (factionDescription != null) factionDescription.text = chosenFaction == FactionId.Human
                ? "Сталь, огонь и последний оплот.\nВозведите базу и защитите живых."
                : "Холод, тьма и вечная ночь.\nПробудите войско и покорите долину.";
            if (factionAccent != null) factionAccent.color = chosenFaction == FactionId.Human ? gold : cyan;
            if (humanCardBorder) humanCardBorder.color = chosenFaction == FactionId.Human ? gold : new Color(.22f,.27f,.29f);
            if (undeadCardBorder) undeadCardBorder.color = chosenFaction == FactionId.Undead ? cyan : new Color(.22f,.27f,.29f);
            FactionButton(humanButton, chosenFaction == FactionId.Human, gold);
            FactionButton(undeadButton, chosenFaction == FactionId.Undead, cyan);
        }

        static void FactionButton(UnityEngine.UI.Button button, bool selected, Color accent)
        {
            if (button == null) return;
            ColorBlockFor(button, selected ? Color.Lerp(new Color(.045f, .065f, .075f), accent, .14f) : new Color(.05f, .065f, .075f));
        }

        static void ColorBlockFor(UnityEngine.UI.Button button, Color normal)
        {
            var colors = button.colors; colors.normalColor = normal; colors.highlightedColor = normal * 1.35f;
            colors.pressedColor = normal * .8f; colors.selectedColor = normal; button.colors = colors;
        }

        void Update()
        {
            if (bridge == null) return;
            if (Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + .1f;
            Refresh(bridge.Snapshot);
        }

        void Refresh(UiSnapshot state)
        {
            if (state == null) return;
            if (Time.unscaledTime >= nextCostRefresh)
            {
                nextCostRefresh = Time.unscaledTime + 1;
                RefreshCosts();
            }
            SetActive(setupPanel, state.Phase == MatchPhase.Setup);
            SetActive(playingPanel, state.Phase != MatchPhase.Setup);
            SetActive(pausePanel, state.Phase == MatchPhase.Paused);
            bool result = state.Phase == MatchPhase.Victory || state.Phase == MatchPhase.Defeat || state.Phase == MatchPhase.Draw;
            SetActive(resultPanel, result);
            SetActive(selectionPanel, state.HasSelection);
            if (resourcesText != null) resourcesText.text = $"ДЕРЕВО  <b>{state.Wood}</b>      ЗОЛОТО  <b>{state.Gold}</b>      АРМИЯ  <b>{state.Used}/{state.Cap}</b>" + (state.Reserved > 0 ? $"  <color=#949FAE>+{state.Reserved}</color>" : "");
            if(woodValue) woodValue.text=state.Wood.ToString();
            if(goldValue) goldValue.text=state.Gold.ToString();
            if(populationValue) populationValue.text=$"{state.Used}<color=#849397> / {state.Cap}</color>"+(state.Reserved>0?$" <size=65%>+{state.Reserved}</size>":"");
            if (jevCostText)
            {
                jevCostText.text = state.JevCostLabel;
                SetActive(jevCostText.gameObject, state.Phase != MatchPhase.Setup);
            }
            if(selectionPortrait)
            {
                selectionPortrait.sprite=Portrait(state.SelectedIconKey);
                selectionPortrait.enabled=selectionPortrait.sprite!=null;
            }
            RefreshCommandIcons(state.PlayerFaction);
            if (selectionName != null) selectionName.text = state.SelectedName;
            if (selectionDescription != null) selectionDescription.text = state.Description;
            if (healthText != null) healthText.text = state.HpMax > 0 ? $"{Mathf.CeilToInt(state.Hp)} / {Mathf.CeilToInt(state.HpMax)}" : "";
            if (healthFill != null)
            {
                float ratio = state.HpMax > 0 ? Mathf.Clamp01(state.Hp / state.HpMax) : 0;
                healthFill.fillAmount = ratio;
                // The authored solid-color Image has no sprite; its anchors provide the fill.
                healthFill.rectTransform.anchorMax = new Vector2(ratio, 1);
                SetActive(healthFill.transform.parent.gameObject, state.HpMax > 0);
            }
            if (queueText != null) queueText.text = state.QueueLabel;
            if (statusText != null) statusText.text = state.Status;
            if (statusText != null && statusText.transform.parent is RectTransform statusRect)
            {
                bool setup = state.Phase == MatchPhase.Setup;
                statusRect.anchorMin = statusRect.anchorMax = new Vector2(.5f, setup ? 0 : 1);
                statusRect.pivot = new Vector2(.5f, setup ? 0 : 1);
                statusRect.anchoredPosition = new Vector2(0, setup ? 26 : -94);
                bool routine = string.IsNullOrEmpty(state.Status) || state.Status.StartsWith("ЛКМ") || state.Status.StartsWith("Выберите фракцию");
                statusText.text = routine ? "" : state.Status;
                var backdrop=statusRect.GetComponent<UnityEngine.UI.Image>();
                if(backdrop)backdrop.enabled=!routine&&!string.IsNullOrEmpty(state.Status);
            }
            if (jevStatusText != null) { jevStatusText.text = state.JevStatus; jevStatusText.gameObject.SetActive(showDiagnostics); }
            bool hasTraining=state.CanTrainWorker||state.CanTrainWarrior||state.CanTrainArcher;
            bool hasProduction=state.HasProduction||hasTraining;
            if(actionPanel)
            {
                actionPanel.sizeDelta=new Vector2(actionPanel.sizeDelta.x,hasProduction&&hasTraining?300:state.CanBuild||hasProduction?194:90);
                if(unitCommandsGroup) ((RectTransform)unitCommandsGroup.transform).anchoredPosition=new Vector2(18,hasProduction&&hasTraining?-258:state.CanBuild||hasProduction?-151:-44);
            }
            if(commandTitle)commandTitle.text=state.CanBuild?"ПОСТРОЙКИ":hasProduction?"НАЙМ ВОЙСК":"ПРИКАЗЫ";
            SetActive(trainingGroup, hasTraining);
            // Eligibility belongs to the selected building, not to the faction's full roster.
            if(trainWorker)SetActive(trainWorker.gameObject,state.CanTrainWorker);
            if(trainWarrior)SetActive(trainWarrior.gameObject,state.CanTrainWarrior);
            if(trainArcher)SetActive(trainArcher.gameObject,state.CanTrainArcher);
            RefreshProduction(state, hasTraining);
            SetActive(buildingGroup, state.CanBuild);
            SetActive(unitCommandsGroup, state.HasSelection);
            bool playing = state.Phase == MatchPhase.Playing;
            Enable(trainWorker, playing && state.CanTrainWorker); Enable(trainWarrior, playing && state.CanTrainWarrior); Enable(trainArcher, playing && state.CanTrainArcher);
            Enable(holdButton, playing && (state.CanBuild || state.HasArmySelection));
            Enable(stopButton, playing && (state.CanBuild || state.HasArmySelection));
            Enable(focusButton, playing && state.HasSelection); Enable(armyButton, playing); Enable(pauseButton, playing);
            if (resultTitle != null) resultTitle.text = state.Phase == MatchPhase.Draw ? "НИЧЬЯ" : state.Phase == MatchPhase.Victory ? "ПОБЕДА" : "ОПЛОТ ПАЛ";
            if (resultDescription != null) resultDescription.text = state.Phase == MatchPhase.Draw ? "Обе ратуши уничтожены в последнем столкновении." : state.Phase == MatchPhase.Victory ? "Вражеская ратуша уничтожена. Эта долина принадлежит вам." : "Ваша ратуша уничтожена. Долина ждёт следующей битвы.";
            if (!playing && selectionRectangle != null) selectionRectangle.gameObject.SetActive(false);
            if (lastPhase != state.Phase) ApplyKeyboardFocus(state.Phase);
            lastPhase = state.Phase;
        }

        void ApplyKeyboardFocus(MatchPhase phase)
        {
            if (uiEventSystem == null) return;
            // The default UI input actions bind WASD/arrows too; the RTS camera owns them in play.
            uiEventSystem.sendNavigationEvents = phase != MatchPhase.Playing;
            UnityEngine.UI.Button target = phase == MatchPhase.Setup ? (chosenFaction == FactionId.Human ? humanButton : undeadButton)
                : phase == MatchPhase.Paused ? resumeButton
                : phase == MatchPhase.Victory || phase == MatchPhase.Defeat || phase == MatchPhase.Draw ? resultRestartButton : null;
            uiEventSystem.SetSelectedGameObject(target != null ? target.gameObject : null);
        }

        public void SetSelectionRect(Vector2 start, Vector2 end, bool visible)
        {
            if (selectionRectangle == null) return;
            selectionRectangle.gameObject.SetActive(visible);
            if (!visible) return;
            var parent = selectionRectangle.parent as RectTransform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, start, null, out var a);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, end, null, out var b);
            selectionRectangle.anchoredPosition = Vector2.Min(a, b);
            selectionRectangle.sizeDelta = new Vector2(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
        }

        static void SetActive(GameObject item, bool active) { if (item != null && item.activeSelf != active) item.SetActive(active); }
        static void Enable(UnityEngine.UI.Button item, bool enabled) { if (item != null) item.interactable = enabled; }

        void RefreshProduction(UiSnapshot state, bool hasTraining)
        {
            if (productionPanel)
            {
                SetActive(productionPanel.gameObject, state.HasProduction);
                productionPanel.anchoredPosition = new Vector2(18, hasTraining ? -151 : -43);
            }
            if (productionLabel) productionLabel.text = state.ProductionLabel;
            if (productionProgressLabel) productionProgressLabel.text = state.ProductionProgressLabel;
            var queue = state.ProductionQueue;
            int count = queue != null ? queue.Length : 0;
            if (productionProgressFill)
            {
                float progress = Mathf.Clamp01(state.ProductionProgress);
                productionProgressFill.fillAmount = progress;
                productionProgressFill.rectTransform.anchorMax = new Vector2(progress, 1);
                productionProgressFill.color = state.PlayerFaction == FactionId.Human ? gold : cyan;
                SetActive(productionProgressFill.transform.parent.gameObject, count > 0);
            }
            for (int i = 0; i < productionSlots.Length; i++)
            {
                var slot = productionSlots[i];
                bool visible = state.HasProduction && i < count;
                SetActive(slot.Root, visible);
                if (!visible) continue;
                if (slot.Portrait) { slot.Portrait.sprite = Portrait(queue[i].IconKey); slot.Portrait.enabled = slot.Portrait.sprite != null; }
                if (slot.Number) slot.Number.text = (i + 1).ToString();
                if (slot.Accent) slot.Accent.color = i == 0 ? (state.PlayerFaction == FactionId.Human ? gold : cyan) : new Color(.23f, .28f, .29f);
            }
            if (productionOverflow)
            {
                int extra = count - productionSlots.Length;
                productionOverflow.text = extra > 0 ? "+ " + extra + "\nв очереди" : "";
            }
        }

        void RefreshCosts()
        {
            var rules = bridge.DisplayRules;
            if (rules == null) return;
            CostLabel(trainWorker, "Рабочий", rules.Unit(UnitKind.Worker).Cost);
            CostLabel(trainWarrior, "Мечник", rules.Unit(UnitKind.Warrior).Cost);
            CostLabel(trainArcher, "Лучник", rules.Unit(UnitKind.Archer).Cost);
            CostLabel(buildTownHall, "Ратуша", rules.Building(BuildingKind.TownHall).Cost);
            var house = rules.Building(BuildingKind.House);
            CostLabel(buildHouse, "Дом", house.Cost, house.PopulationProvided > 0 ? " · +" + house.PopulationProvided + " мест" : "");
            CostLabel(buildBarracks, "Казарма", rules.Building(BuildingKind.Barracks).Cost);
            CostLabel(buildArcheryRange, "Стрельбище", rules.Building(BuildingKind.ArcheryRange).Cost);
            CostLabel(buildTower, "Башня", rules.Building(BuildingKind.Tower).Cost);
        }

        Sprite Portrait(string id)
        {
            foreach(var entry in portraits)if(entry.Id==id)return entry.Sprite;
            return null;
        }
        void RefreshCommandIcons(FactionId faction)
        {
            string[] keys={"worker","swordsman","archer","townhall","house","barracks","archeryrange","tower"};
            string suffix=faction==FactionId.Human?"-human":"-undead";
            for(int i=0;i<commandIcons.Length&&i<keys.Length;i++)
                if(commandIcons[i])commandIcons[i].sprite=Portrait(keys[i]+suffix);
        }

        static void CostLabel(UnityEngine.UI.Button button, string title, ResourceCost cost, string suffix = "")
        {
            if (button == null || cost == null) return;
            var label = button.GetComponentInChildren<TMPro.TMP_Text>();
            if (label == null) return;
            string value = cost.Wood > 0 ? cost.Wood + " дер." : "";
            if (cost.Gold > 0) value += (value.Length > 0 ? " · " : "") + cost.Gold + " зол.";
            if (value.Length == 0) value = "Бесплатно";
            label.text = title + "\n<size=75%><color=#A5AFA7>" + value + suffix + "</color></size>";
        }
    }
}
