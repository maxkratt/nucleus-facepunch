using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.QuickStart.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="QuickStartPlayer"/>: the move speed, wander radius and tinted renderer.
    /// </summary>
    [CustomEditor(typeof(QuickStartPlayer))]
    public class QuickStartPlayerEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_moveSpeed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_wanderRadius"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_bodyRenderer"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
