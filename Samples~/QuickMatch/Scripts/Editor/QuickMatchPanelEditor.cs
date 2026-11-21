using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.QuickMatch.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="QuickMatchPanel"/>: its fields, and a warning with a fix while the sample's two scenes are not in the build.
    /// </summary>
    [CustomEditor(typeof(QuickMatchPanel))]
    public class QuickMatchPanelEditor : UnityEditor.Editor
    {
        /// <summary>
        /// The paths of the sample's scenes that are missing from the build.
        /// </summary>
        private readonly List<string> _missingScenePaths = new();

        /// <summary>
        /// Finds which of the sample's scenes the build is missing.
        /// </summary>
        private void OnEnable() => FindMissingScenes();

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_steamBootstrap"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_targetPlayerCount"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            serializedObject.ApplyModifiedProperties();

            if (_missingScenePaths.Count == 0)
                return;

            EditorGUILayout.HelpBox("Quick Match loads its scenes by name, and importing a sample does not add them to the build.", MessageType.Warning);

            if (GUILayout.Button("Add both scenes to the build"))
                AddMissingScenes();
        }

        /// <summary>
        /// Records the sample's scenes that are absent from, or disabled in, the build.
        /// </summary>
        private void FindMissingScenes()
        {
            _missingScenePaths.Clear();

            foreach (string sceneGuid in AssetDatabase.FindAssets($"{QuickMatchSession.MenuSceneName} t:Scene"))
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuid);
                string sceneName = Path.GetFileNameWithoutExtension(scenePath);

                if (sceneName is not (QuickMatchSession.MenuSceneName or QuickMatchSession.GameSceneName))
                    continue;

                if (!IsSceneInBuild(scenePath))
                    _missingScenePaths.Add(scenePath);
            }
        }

        /// <summary>
        /// Adds the missing scenes to the build, enabled.
        /// </summary>
        private void AddMissingScenes()
        {
            List<EditorBuildSettingsScene> buildScenes = new(EditorBuildSettings.scenes);

            foreach (string scenePath in _missingScenePaths)
            {
                buildScenes.RemoveAll(buildScene => buildScene.path == scenePath);
                buildScenes.Add(new(scenePath, enabled: true));
            }

            EditorBuildSettings.scenes = buildScenes.ToArray();
            FindMissingScenes();
        }

        /// <summary>
        /// Checks whether a scene is enabled in the build.
        /// </summary>
        /// <param name="scenePath">The scene's asset path.</param>
        /// <returns><see langword="true"/> when the build includes it.</returns>
        private static bool IsSceneInBuild(string scenePath)
        {
            foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
            {
                if (buildScene.path == scenePath && buildScene.enabled)
                    return true;
            }

            return false;
        }
    }
}
