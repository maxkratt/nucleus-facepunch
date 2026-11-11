using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Samples.VoiceChat.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="VoiceChatPlayer"/>: the push to talk key, the audio source and the lit renderer.
    /// </summary>
    [CustomEditor(typeof(VoiceChatPlayer))]
    public class VoiceChatPlayerEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            // Drawn here rather than by Nucleus, whose script row adds a second help button that ignores this component's HelpURL.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_pushToTalkKey"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_audioSource"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_bodyRenderer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_playerNameTag"));
            serializedObject.ApplyModifiedProperties();
        }
    }
}
