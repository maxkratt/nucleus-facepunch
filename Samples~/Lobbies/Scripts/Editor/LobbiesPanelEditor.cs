using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.Lobbies.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="LobbiesPanel"/>: the bootstrap, transport manager and transport it drives, and the lobby size.
    /// </summary>
    [CustomEditor(typeof(LobbiesPanel))]
    public class LobbiesPanelEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_steamBootstrap"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_unityTransportManager"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_facepunchTransport"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumLobbyMembers"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
