using UnityEditor;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamBootstrap"/>: the App ID, then either the relaunch check or the game server option
    /// with its ports and listing, and whether Steam is up and the local SteamID while playing.
    /// </summary>
    [CustomEditor(typeof(SteamBootstrap))]
    public class SteamBootstrapEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_appId"));
            SerializedProperty gameServerEnabledSerializedProperty = serializedObject.FindProperty("_gameServerEnabled");

            // The relaunch check is the user's Steam only, so a game server hides it.
            if (!gameServerEnabledSerializedProperty.boolValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_relaunchThroughSteamEnabled"), new GUIContent("Relaunch Through Steam"));

            EditorGUILayout.PropertyField(gameServerEnabledSerializedProperty, new GUIContent("Game Server", gameServerEnabledSerializedProperty.tooltip));

            using (new EditorGUI.IndentLevelScope())
            {
                if (gameServerEnabledSerializedProperty.boolValue)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerPort"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerQueryPort"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerModDirectory"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_gameServerDescription"));
                }
            }

            serializedObject.ApplyModifiedProperties();

            if (!EditorApplication.isPlaying)
                return;

            SteamBootstrap steamBootstrap = (SteamBootstrap)target;

            EditorGUILayout.LabelField("Steam Initialized", steamBootstrap.IsSteamInitialized.ToString());
            EditorGUILayout.LabelField("SteamID", steamBootstrap.OwnSteamId.ToString());
        }
    }
}
