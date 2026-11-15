#if NUCLEUS_SAMPLES_INPUT_SYSTEM
using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="PlayerMover"/>: the move speed, the height above the ground, and where and when a fallen
    /// object is put back.
    /// </summary>
    [CustomEditor(typeof(PlayerMover))]
    public class PlayerMoverEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_moveSpeed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_groundOffset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_respawnHeight"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_respawnPosition"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
