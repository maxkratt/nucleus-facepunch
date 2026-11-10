using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="PlayerNameTag"/>: the skin, the height above the object, and the speaking icon with its
    /// size, which is drawn only once an icon is set.
    /// </summary>
    [CustomEditor(typeof(PlayerNameTag))]
    public class PlayerNameTagEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_guiSkin"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_heightOffset"));
            SerializedProperty speakingIconSerializedProperty = serializedObject.FindProperty("_speakingIcon");
            EditorGUILayout.PropertyField(speakingIconSerializedProperty);

            using (new EditorGUI.IndentLevelScope())
            {
                if (speakingIconSerializedProperty.objectReferenceValue != null)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_speakingIconSize"));
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
