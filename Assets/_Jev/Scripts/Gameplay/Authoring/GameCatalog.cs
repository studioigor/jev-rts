using System;
using System.Linq;
using UnityEngine;
using Jev.Gameplay.Simulation;
using Jev.Gameplay.Presentation;

namespace Jev.Gameplay.Authoring
{
    [CreateAssetMenu(menuName = "Jev RTS/Game Catalog", fileName = "GameCatalog")]
    public sealed class GameCatalog : ScriptableObject
    {
        public UnitPrefabEntry[] Units = Array.Empty<UnitPrefabEntry>();
        public BuildingPrefabEntry[] Buildings = Array.Empty<BuildingPrefabEntry>();
        public EntityView ResourceProxy;
        public EntityView BuildingSelection;
        public GameObject BuildPreview;
        public UnitPrefabEntry Unit(FactionId faction,UnitKind kind) => Units.First(e=>e.Faction==faction&&e.Kind==kind);
        public BuildingPrefabEntry Building(FactionId faction,BuildingKind kind) => Buildings.First(e=>e.Faction==faction&&e.Kind==kind);
    }
    [Serializable] public sealed class UnitPrefabEntry { public FactionId Faction; public UnitKind Kind; public UnitView Prefab; }
    [Serializable] public sealed class BuildingPrefabEntry { public FactionId Faction; public BuildingKind Kind; public GameObject Prefab; public int Width = 3, Height = 3; public Vector3 SelectionSize = new Vector3(3,3,3); }
}
