using Nucleus.Connections;
using Nucleus.Integrations.Unity.Systems;
using Nucleus.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Nucleus.Integrations.Facepunch.Samples.QuickStart
{
    /// <summary>
    /// One player's capsule. The peer that controls it moves it with WASD or the arrow keys, and every other peer follows the motion
    /// its <see cref="Components.NetworkTransform"/> replicates. It tints itself by its controller, so each player's capsule differs.
    /// </summary>
    /// <remarks>
    /// The scene's <see cref="NetworkPlayerSpawner"/> spawns it on the server and hands control to its player. It adopts the system
    /// the <see cref="Components.NetworkTransform"/> declares rather than declaring one, so the capsule has a single system.
    /// </remarks>
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/QuickStart/README.md")]
    public class QuickStartPlayer : NucleusBehaviour
    {
        /// <summary>
        /// How fast the capsule moves for the peer driving it, in metres per second.
        /// </summary>
        [SerializeField]
        [Tooltip("How fast the capsule moves for the peer driving it, in metres per second.")]
        private float _moveSpeed = 6f;
        /// <summary>
        /// How far from the origin the capsule may move, in metres, so it stays in the camera's view.
        /// </summary>
        [SerializeField]
        [Tooltip("How far from the origin the capsule may move, in metres.")]
        private float _wanderRadius = 12f;
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
        /// Moves the capsule on the peer that controls it.
        /// </summary>
        /// <remarks>A held key is a property of the frame, so the keyboard is read here rather than learned from a callback.</remarks>
        private void Update()
        {
            if (!IsController(ControllerType.AnyController))
                return;

            Keyboard keyboard = Keyboard.current;

            if (keyboard is null)
                return;

            float rightInput = (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed ? 1f : 0f);
            float forwardInput = (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed ? 1f : 0f);
            Vector3 driveDirection = new Vector3(rightInput, 0f, forwardInput).normalized;

            if (driveDirection == Vector3.zero)
                return;

            Vector3 movedPosition = transform.position + driveDirection * (_moveSpeed * Time.deltaTime);
            transform.position = Vector3.ClampMagnitude(new(movedPosition.x, 0f, movedPosition.z), _wanderRadius) + Vector3.up * transform.position.y;
        }

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
