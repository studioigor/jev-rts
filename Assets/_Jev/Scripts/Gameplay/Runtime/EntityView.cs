using UnityEngine;

namespace Jev.Gameplay.Authoring
{
    public enum EntityViewKind { Building, Resource }
    /// <summary>Reusable selection proxy. Gameplay data lives in the authoritative world.</summary>
    public sealed class EntityView : MonoBehaviour
    {
        public string Id;
        public EntityViewKind Kind;
        public GameObject Selection;
        public void SetSelected(bool selected) { if (Selection) Selection.SetActive(selected); }
    }
}
