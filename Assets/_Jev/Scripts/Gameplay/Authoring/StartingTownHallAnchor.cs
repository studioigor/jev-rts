using System.Linq;
using UnityEngine;
using Jev.Gameplay.Simulation;

namespace Jev.Gameplay.Authoring
{
    /// <summary>Scene placement handle. The authored Battlefield remains the runtime spawn contract.</summary>
    [ExecuteAlways, DisallowMultipleComponent, SelectionBase]
    public sealed class StartingTownHallAnchor : MonoBehaviour
    {
        public BattlefieldDefinition Battlefield;
        public GameCatalog Catalog;
        public FactionId Faction;
        FactionSpawn Spawn => Faction == FactionId.Human ? Battlefield.Human : Battlefield.Undead;
        void OnEnable() { if (Application.isPlaying) gameObject.SetActive(false); }

#if UNITY_EDITOR
        public string PlacementError()
        {
            if (!Battlefield || !Catalog) return "Назначьте Battlefield и Catalog.";
            var cell = Battlefield.ToCell(transform.position);
            var worker = new Cell(Spawn.Worker.X + cell.X - Spawn.TownHall.X, Spawn.Worker.Y + cell.Y - Spawn.TownHall.Y);
            var entry = Catalog.Building(Faction, BuildingKind.TownHall);
            var other = Faction == FactionId.Human ? Battlefield.Undead : Battlefield.Human;
            var otherEntry = Catalog.Building(Faction == FactionId.Human ? FactionId.Undead : FactionId.Human, BuildingKind.TownHall);
            bool Inside(Cell c, Cell center, int width, int height) => c.X >= center.X-width/2 && c.X < center.X-width/2+width && c.Y >= center.Y-height/2 && c.Y < center.Y-height/2+height;
            bool Free(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < Battlefield.Width && c.Y < Battlefield.Height &&
                !Battlefield.Blocked.Contains(c) && !Battlefield.Resources.Any(r => r.Amount > 0 && r.Cell == c) &&
                !Inside(c, other.TownHall, otherEntry.Width, otherEntry.Height) && c != other.Worker;
            for (int y=0;y<entry.Height;y++) for (int x=0;x<entry.Width;x++)
                if (!Free(new Cell(cell.X-entry.Width/2+x,cell.Y-entry.Height/2+y)))
                    return "Ратуша пересекает препятствие, ресурс, другую базу или границу карты. Сохраняется прежняя стартовая позиция.";
            if (!Free(worker) || Inside(worker,cell,entry.Width,entry.Height))
                return "Стартовому рабочему здесь нет места. Сохраняется прежняя стартовая позиция.";
            return null;
        }
        void Update()
        {
            if (Application.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode || !Battlefield || !Catalog) return;
            if (UnityEditor.EditorUtility.IsPersistent(this) || UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null) return;
            var cell = Battlefield.ToCell(transform.position);
            float yaw = transform.eulerAngles.y;
            if (PlacementError() != null) return;
            var snapped = Battlefield.ToWorld(cell);
            var upright = Quaternion.Euler(0,yaw,0);
            if (transform.position != snapped || Quaternion.Angle(transform.rotation,upright) > .01f || transform.localScale != Vector3.one)
            {
                UnityEditor.Undo.RecordObject(transform,"Snap starting town hall to grid");
                transform.SetPositionAndRotation(snapped,upright);transform.localScale=Vector3.one;
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
            }
            if (cell == Spawn.TownHall && Mathf.Abs(Mathf.DeltaAngle(yaw,Spawn.Rotation)) < .01f) return;
            UnityEditor.Undo.RecordObject(Battlefield,"Move starting town hall");
            UnityEditor.Undo.RecordObject(transform,"Snap starting town hall to grid");
            Spawn.Worker = new Cell(Spawn.Worker.X+cell.X-Spawn.TownHall.X,Spawn.Worker.Y+cell.Y-Spawn.TownHall.Y);
            Spawn.TownHall = cell; Spawn.Rotation = yaw;
            foreach (var site in Battlefield.BuildSites.Where(s=>s.Faction==Faction && s.Kind==BuildingKind.TownHall))
            { site.Cell=cell;site.Rotation=yaw; }
            transform.position=Battlefield.ToWorld(cell);
            UnityEditor.EditorUtility.SetDirty(Battlefield);
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
        }
        [ContextMenu("Reset marker to saved starting position")]
        public void ResetToSavedPosition()
        {
            if (!Battlefield || Application.isPlaying) return;
            UnityEditor.Undo.RecordObject(transform,"Reset starting town hall marker");
            transform.SetPositionAndRotation(Battlefield.ToWorld(Spawn.TownHall),Quaternion.Euler(0,Spawn.Rotation,0));
            UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
        }
        void OnDrawGizmos()
        {
            if (!Battlefield || !Catalog || Application.isPlaying) return;
            var cell=Battlefield.ToCell(transform.position);var point=Battlefield.ToWorld(cell);
            var entry=Catalog.Building(Faction,BuildingKind.TownHall);
            Gizmos.color=PlacementError()==null ? (Faction==FactionId.Human ? new Color(1,.75f,.2f) : new Color(.2f,1,.8f)) : Color.red;
            Gizmos.DrawWireCube(point+Vector3.up*.12f,new Vector3(entry.Width*Battlefield.CellSize,.2f,entry.Height*Battlefield.CellSize));
            var worker=new Cell(Spawn.Worker.X+cell.X-Spawn.TownHall.X,Spawn.Worker.Y+cell.Y-Spawn.TownHall.Y);
            var workerPoint=Battlefield.ToWorld(worker);Gizmos.DrawWireSphere(workerPoint+Vector3.up*.6f,.4f);
            UnityEditor.Handles.Label(point+Vector3.up*6,Faction==FactionId.Human?"HUMAN · START TOWN HALL":"UNDEAD · START TOWN HALL");
            UnityEditor.Handles.Label(workerPoint+Vector3.up*1.2f,"Starting worker");
        }
#endif
    }
}
