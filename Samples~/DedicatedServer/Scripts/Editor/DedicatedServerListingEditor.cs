using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.DedicatedServer.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="DedicatedServerListing"/>: the bootstrap, and the server name, map and player limit it lists.
    /// </summary>
    [CustomEditor(typeof(DedicatedServerListing))]
    public class DedicatedServerListingEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_steamBootstrap"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_serverName"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_mapName"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumPlayers"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
