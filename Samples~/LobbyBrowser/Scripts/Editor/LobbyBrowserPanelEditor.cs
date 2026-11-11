using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.LobbyBrowser.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="LobbyBrowserPanel"/>: the bootstrap, transport manager and transport it drives, the hosted lobby size and the search result limit.
    /// </summary>
    [CustomEditor(typeof(LobbyBrowserPanel))]
    public class LobbyBrowserPanelEditor : UnityEditor.Editor
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
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumSearchResults"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
