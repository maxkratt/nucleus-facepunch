using Nucleus.Integrations.Unity.Editor;
using UnityEditor;

namespace Nucleus.Integrations.Facepunch.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="FacepunchHostMigration"/>: the shared host migration inspector, with the address an IP address
    /// host is reached at.
    /// </summary>
    [CustomEditor(typeof(FacepunchHostMigration))]
    [CanEditMultipleObjects]
    public class FacepunchHostMigrationEditor : NetworkHostMigrationEditor
    {
        /// <inheritdoc/>
        protected override void DrawServiceSettings()
        {
            FacepunchTransport facepunchTransport = ((FacepunchHostMigration)target).GetComponent<FacepunchTransport>();

            // Only a host addressed by IP address has an address of its own to publish. A mixed selection shows it, so nothing in use is hidden.
            if (!serializedObject.isEditingMultipleObjects && facepunchTransport != null && facepunchTransport.Addressing is not FacepunchAddressing.IpAddress)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Steam", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_hostedAddress"));
        }
    }
}
