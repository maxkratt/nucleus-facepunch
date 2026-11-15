// Only the samples that move a player need the Input System, so the rest of Shared compiles without it.
#if NUCLEUS_SAMPLES_INPUT_SYSTEM
using Nucleus.Integrations.Unity.Systems;
using Nucleus.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Nucleus.Integrations.Facepunch.Samples
{
    /// <summary>
    /// Moves a player's object with WASD or the arrow keys on the peer that controls it, standing it on whatever ground is below and
    /// letting it fall off the edge, then putting it back above the origin once it has fallen too far.
    /// </summary>
    /// <remarks>
    /// Every other peer follows the motion the object's <see cref="Components.NetworkTransform"/> replicates. It adopts the system the
    /// transform declares rather than declaring one. The ground is found by a ray cast down from the object's origin, so the object
    /// itself needs no collider.
    /// </remarks>
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/Shared/README.md")]
    public class PlayerMover : NucleusBehaviour
    {
        /// <summary>
        /// How fast the object moves for the peer driving it, in metres per second.
        /// </summary>
        [SerializeField]
        [Tooltip("How fast the object moves for the peer driving it, in metres per second.")]
        private float _moveSpeed = 6f;
        /// <summary>
        /// How far the object's origin stands above the ground, in metres.
        /// </summary>
        [SerializeField]
        [Tooltip("How far the object's origin stands above the ground, in metres.")]
        private float _groundOffset = 1f;
        /// <summary>
        /// The height below which the object is put back at <see cref="_respawnPosition"/>, in metres.
        /// </summary>
        [SerializeField]
        [Tooltip("The height below which the object is put back at the respawn position, in metres.")]
        private float _respawnHeight = -20f;
        /// <summary>
        /// Where the object is put back after falling below <see cref="_respawnHeight"/>, a little above the ground so it drops onto it.
        /// </summary>
        [SerializeField]
        [Tooltip("Where the object is put back after falling too far, a little above the ground.")]
        private Vector3 _respawnPosition = new(0f, 3f, 0f);

        /// <summary>
        /// How fast the object is falling, in metres per second, positive downwards.
        /// </summary>
        private float _fallSpeed;

        /// <summary>
        /// Clears the fall, so a pooled object starts its next life at rest.
        /// </summary>
        protected override void OnSystemUnlinked() => _fallSpeed = 0f;

        /// <summary>
        /// Moves the object on the peer that controls it.
        /// </summary>
        /// <remarks>A held key is a property of the frame, so the keyboard is read here rather than learned from a callback.</remarks>
        private void Update()
        {
            if (!IsController(ControllerType.AnyController))
                return;

            Keyboard keyboard = Keyboard.current;
            Vector3 position = transform.position;
            float deltaTime = Time.deltaTime;

            if (keyboard is not null)
            {
                float rightInput = (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed ? 1f : 0f);
                float forwardInput = (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed ? 1f : 0f);
                position += new Vector3(rightInput, 0f, forwardInput).normalized * (_moveSpeed * deltaTime);
            }

            _fallSpeed -= UnityEngine.Physics.gravity.y * deltaTime;
            float fallDistance = _fallSpeed * deltaTime;

            // The ray reaches as far as this frame's fall, so a fast fall lands on the ground instead of passing through it.
            if (UnityEngine.Physics.Raycast(position, Vector3.down, out RaycastHit raycastHit, _groundOffset + fallDistance, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                position.y = raycastHit.point.y + _groundOffset;
                _fallSpeed = 0f;
            }
            else
            {
                position.y -= fallDistance;
            }

            if (position.y < _respawnHeight)
            {
                position = _respawnPosition;
                _fallSpeed = 0f;
            }

            transform.position = position;
        }
    }
}
#endif
