using System;
using System.Collections.Generic;
using UnityEngine;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Authoring
{
    [CreateAssetMenu(menuName = "Jev RTS/Battlefield", fileName = "Battlefield")]
    public sealed class BattlefieldDefinition : ScriptableObject
    {
        public int Width = 80, Height = 66;
        public float CellSize = 1.2f;
        public Vector2 Origin = new Vector2(-48, -39.6f);
        public float[] Heights = Array.Empty<float>();
        public List<Cell> Blocked = new List<Cell>();
        [Tooltip("Use the authored opaque scenery mask instead of treating all unwalkable terrain (including water) as a wall.")]
        public bool HasAuthoredSightBlockers;
        [Tooltip("Opaque scenery only. Living buildings and harvestable trees are added by the simulation while present.")]
        public List<Cell> SightBlocked = new List<Cell>();
        public FactionSpawn Human, Undead;
        public List<BuildSite> BuildSites = new List<BuildSite>();
        public List<ResourcePlacement> Resources = new List<ResourcePlacement>();
        [Header("Resource stocks used when authoring nodes")]
        [Min(1)] public int WoodPerTree = 100;
        [Min(1)] public int GoldPerMine = 10000;
        public List<Landmark> Landmarks = new List<Landmark>();
        public bool DescribeRiverCrossings = true;
        [TextArea] public string NavigationDescription = "";
        public Cell ToCell(Vector3 point) => new Cell(Mathf.FloorToInt((point.x-Origin.x)/CellSize), Mathf.FloorToInt((point.z-Origin.y)/CellSize));
        public HashSet<Cell> CreateSightBlockers() => HasAuthoredSightBlockers ? new HashSet<Cell>(SightBlocked) : null;
        public Vector3 ToWorld(Cell cell)
        {
            int index = Mathf.Clamp(cell.Y,0,Height-1)*Width+Mathf.Clamp(cell.X,0,Width-1);
            return new Vector3(Origin.x+(cell.X+.5f)*CellSize, Heights.Length > index ? Heights[index] : 0, Origin.y+(cell.Y+.5f)*CellSize);
        }
        public float SampleHeight(Vector3 point)
        {
            float x=(point.x-Origin.x)/CellSize-.5f, y=(point.z-Origin.y)/CellSize-.5f;
            int ix=Mathf.FloorToInt(x), iy=Mathf.FloorToInt(y);
            return Mathf.Lerp(Mathf.Lerp(ToWorld(new Cell(ix,iy)).y,ToWorld(new Cell(ix+1,iy)).y,x-ix),Mathf.Lerp(ToWorld(new Cell(ix,iy+1)).y,ToWorld(new Cell(ix+1,iy+1)).y,x-ix),y-iy);
        }
    }
    [Serializable] public sealed class FactionSpawn { public Cell TownHall, Worker; public float Rotation; }
    [Serializable] public sealed class BuildSite { public string Id; public FactionId Faction; public BuildingKind Kind; public Cell Cell; public float Rotation; }
    [Serializable] public sealed class ResourcePlacement { public string Id; public ResourceKind Kind; public Cell Cell; [Min(1)] public int Amount = 100; public string VisualPath; }
    [Serializable] public sealed class Landmark { public string Name; public Cell Cell; }
}
