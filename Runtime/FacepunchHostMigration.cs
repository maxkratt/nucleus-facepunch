using Nucleus.Integrations.Newfarm;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Transports;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// Host migration over Steam Networking Sockets: a session survives losing whoever hosts it, whether peers reach each other by SteamID,
    /// by IP address or by fake IP.
    /// </summary>
    /// <remarks>
    /// Put it beside a <see cref="FacepunchTransport"/>; having it there is what turns host migration on. A game then starts and joins as it
    /// would without it: a server opens a session with the directory once it is running, and every client that authenticates is told which
    /// session it is in, so a peer that joined by SteamID or address survives losing that host. A host addressed by SteamID publishes its
    /// SteamID; one addressed by IP address publishes <see cref="HostedAddress"/>, or its bind address when that is empty; one addressed by
    /// fake IP publishes the fake address Steam grants it when it takes over.
    /// Sessions need Nucleus Pro. This compiles in both editions, but with a Free <c>Nucleus.dll</c>
    /// <see cref="Nucleus.Managers.Server.ServerManager.IsHostMigrationAvailable"/> reads <see langword="false"/> and every session call
    /// returns <see langword="false"/>.
    /// </remarks>
    [RequireComponent(typeof(FacepunchTransport))]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch#host-migration")]
    public class FacepunchHostMigration : NetworkHostMigration
    {
        /// <summary>
        /// The address other peers reach this peer at when it hosts by IP address.
        /// </summary>
        /// <remarks>Read when the transport is added. Has no effect when peers are addressed by SteamID or fake IP.</remarks>
        public string HostedAddress { get => _hostedAddress; set => _hostedAddress = value; }

        /// <summary>
        /// The address other peers reach this peer at when it hosts by IP address.
        /// </summary>
        [SerializeField]
        [Tooltip("Address other peers reach this peer at when it hosts by IP address, such as a public address with a forwarded port. Leave empty to use the bind address, or this machine's own IPv4 address, which only reaches the same network.")]
        private string _hostedAddress = string.Empty;

        /// <inheritdoc/>
        protected override ISessionHost CreateSessionHost(Transport transport)
        {
            if (transport is FacepunchSockets facepunchSockets)
                return new FacepunchSessionHost(facepunchSockets) { HostedAddress = _hostedAddress };

            Debug.LogError($"[{nameof(FacepunchHostMigration)}] on [{name}] needs a {nameof(FacepunchTransport)} beside it and found [{transport.GetType().Name}], so host migration is off.");

            return null;
        }
    }
}
