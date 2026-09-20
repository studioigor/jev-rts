using UnityEditor;
using Jev.Gameplay.Authoring;

namespace Jev.Editor.Gameplay
{
    [CustomEditor(typeof(StartingTownHallAnchor))]
    public sealed class StartingTownHallAnchorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var anchor=(StartingTownHallAnchor)target;
            EditorGUILayout.HelpBox("W — переместить, E — повернуть. Позиция привязывается к сетке и записывается в Battlefield. Стартовый рабочий переносится вместе с ратушей. Ctrl/Cmd+S — сохранить.",MessageType.Info);
            var error=anchor.PlacementError();
            if(error!=null)EditorGUILayout.HelpBox(error,MessageType.Warning);
        }
    }
}
