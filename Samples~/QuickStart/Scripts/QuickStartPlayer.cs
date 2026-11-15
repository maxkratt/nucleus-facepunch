using Nucleus.Connections;
using Nucleus.Integrations.Unity.Systems;
using Nucleus.Systems;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.QuickStart
{
    /// <summary>
    /// One player's capsule, tinted by its controller so each player's capsule differs. The capsule's <see cref="PlayerMover"/> moves it.
    /// </summary>
    /// <remarks>
    /// The scene's <see cref="NetworkPlayerSpawner"/> spawns it on the server and hands control to its player. It adopts the system
    /// the <see cref="Components.NetworkTransform"/> declares rather than declaring one, so the capsule has a single system.
    /// </remarks>
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/QuickStart/README.md")]
    public class QuickStartPlayer : NucleusBehaviour
    {
        /// <summary>
        /// The renderer tinted by the capsule's controller.
        /// </summary>
        [SerializeField]
        [Tooltip("The renderer tinted by the capsule's controller.")]
        private Renderer _bodyRenderer;

        /// <summary>
        /// This capsule's own material instance, so tinting it leaves the shared asset alone.
        /// </summary>
        private Material _bodyMaterial;

        /// <summary>
        /// The golden ratio's fraction, which spreads consecutive connection ids around the hue circle.
        /// </summary>
        private const float HueStep = 0.618034f;

        /// <summary>
        /// Takes this capsule's own material instance, then lets the base adopt the system.
        /// </summary>
        /// <remarks>
        /// The material is taken first because on a receiving client the system can already be linked when this runs, and the base
        /// then raises <see cref="OnSystemLinked"/> from inside its own <c>Awake</c>.
        /// </remarks>
        protected override void Awake()
        {
            if (_bodyRenderer != null)
                _bodyMaterial = _bodyRenderer.material;

            base.Awake();
        }

        /// <summary>
        /// Destroys the material instance taken in <see cref="Awake"/>.
        /// </summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();

            if (_bodyMaterial != null)
                Destroy(_bodyMaterial);
        }

        /// <summary>
        /// Tints the capsule for its controller as its system arrives.
        /// </summary>
        protected override void OnSystemLinked() => ApplyControllerColor();

        /// <summary>
        /// Tints the capsule for its new controller.
        /// </summary>
        /// <param name="previousControllerConnection">The previous controlling Connection, or null.</param>
        /// <param name="currentControllerConnection">The current controlling Connection, or null.</param>
        protected override void OnControllerChanged(Connection previousControllerConnection, Connection currentControllerConnection) => ApplyControllerColor();

        /// <summary>
        /// Tints the capsule by its controller's connection id, or grey while the server controls it.
        /// </summary>
        private void ApplyControllerColor()
        {
            if (_bodyMaterial == null)
                return;

            uint controllerConnectionId = NetworkSystem.ControllerConnectionId;
            _bodyMaterial.color = controllerConnectionId == NetworkSystem.UnsetId ? Color.grey : Color.HSVToRGB(controllerConnectionId * HueStep % 1f, 0.65f, 0.95f);
        }
    }
}
