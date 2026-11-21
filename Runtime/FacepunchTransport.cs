using System.Threading.Tasks;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Core;
using Nucleus.Transports;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// Inspector-configurable component for the <see cref="FacepunchSockets"/> transport. Place it on the UnityCoreManager's GameObject;
    /// the <see cref="UnityTransportManager"/> adds it to the CoreManager with these settings applied.
    /// </summary>
    /// <remarks>Steam itself is initialized and pumped by the game, never by this component.</remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch#facepunchtransport")]
    public class FacepunchTransport : NetworkTransport
    {
        /// <summary>
        /// The SteamID of the peer hosting the session, which a client connects to.
        /// </summary>
        /// <remarks>Accepted at any time and applied at the next client start.</remarks>
        public ulong RemoteSteamId
        {
            get => _remoteSteamId;
            set
            {
                _remoteSteamId = value;

                if (FacepunchSockets is not null)
                    FacepunchSockets.RemoteSteamId = value;
            }
        }
        /// <summary>
        /// The address and port a client connects to in <see cref="FacepunchAddressing.IpAddress"/> and <see cref="FacepunchAddressing.FakeIp"/>.
        /// </summary>
        /// <remarks>Accepted at any time and applied at the next client start.</remarks>
        public string RemoteAddress
        {
            get => _remoteAddress;
            set
            {
                _remoteAddress = value;

                if (FacepunchSockets is not null)
                    FacepunchSockets.RemoteAddress = value;
            }
        }
        /// <summary>
        /// The local address an <see cref="FacepunchAddressing.IpAddress"/> server listens on: an IPv4 or IPv6 address, <c>localhost</c>, or empty to listen on every interface.
        /// </summary>
        /// <remarks>Accepted at any time and applied at the next server start.</remarks>
        public string BindAddress
        {
            get => _bindAddress;
            set
            {
                _bindAddress = value;

                if (FacepunchSockets is not null)
                    FacepunchSockets.SetBindAddress(value);
            }
        }
        /// <summary>
        /// An optional gate every client asking to connect passes before the engine sees it, or <see langword="null"/> to accept anyone while the server has room.
        /// </summary>
        /// <remarks>Accepted at any time, and applied to clients asking after the call. See <see cref="FacepunchSockets.ConnectingClientFilter"/>.</remarks>
        public FacepunchSockets.ConnectingClientFilterHandler ConnectingClientFilter
        {
            get => _connectingClientFilter;
            set
            {
                _connectingClientFilter = value;

                if (FacepunchSockets is not null)
                    FacepunchSockets.ConnectingClientFilter = value;
            }
        }
        /// <summary>
        /// How the server is reached, as set in the inspector.
        /// </summary>
        public FacepunchAddressing Addressing => _addressing;
        /// <summary>
        /// The transport this component configured, or <see langword="null"/> before it has.
        /// </summary>
        /// <remarks>Replaced at every start, since the transport manager adds the transport again each time.</remarks>
        public FacepunchSockets FacepunchSockets { get; private set; }
        /// <summary>
        /// The fewest connections the inspector lets a server accept.
        /// </summary>
        /// <remarks>Zero is left out because the engine reads it as <see cref="ServerConfiguration.UnsetMaximumConnections"/>, any number.</remarks>
        public const uint MinimumMaximumConnections = 1;
        /// <summary>
        /// How the server is reached.
        /// </summary>
        [SerializeField]
        [Tooltip("How the server is reached: by SteamID through Steam's relay, by IPv4 or IPv6 address, or by a fake IP Steam grants a game server.")]
        private FacepunchAddressing _addressing;
        /// <summary>
        /// The SteamID64 of the server a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("SteamID64 of the server a client connects to, such as 76561198012345678. Zero joins this peer's own server. Ignored while this peer runs a server; its client joins that server.")]
        private ulong _remoteSteamId;
        /// <summary>
        /// The address and port a client connects to in <see cref="FacepunchAddressing.IpAddress"/> and <see cref="FacepunchAddressing.FakeIp"/>.
        /// </summary>
        [SerializeField]
        [Tooltip("IP address and port a client connects to, such as 203.0.113.7:27015 or [2001:db8::7]:27015; for a fake IP, the address the server logs. Host names are not looked up. Empty joins this peer's own server.")]
        private string _remoteAddress = string.Empty;
        /// <summary>
        /// The port server and clients agree on: the Steam virtual port in <see cref="FacepunchAddressing.SteamId"/>, the UDP port in <see cref="FacepunchAddressing.IpAddress"/>.
        /// </summary>
        [SerializeField]
        [Tooltip("Port server and clients agree on: Steam's virtual port by SteamID, the UDP port the server listens on by IP address.")]
        private ushort _port = 27015;
        /// <summary>
        /// The local IPv4 or IPv6 address an <see cref="FacepunchAddressing.IpAddress"/> server listens on.
        /// </summary>
        [SerializeField]
        [Tooltip("Local IPv4 or IPv6 address, or localhost, an IP address server listens on. Empty listens on every interface.")]
        private string _bindAddress = string.Empty;
        /// <summary>
        /// Seconds a link may spend connecting before Steam gives up on it.
        /// </summary>
        /// <remarks>Process-wide. Zero leaves Steam's value alone.</remarks>
        [SerializeField]
        [Tooltip("Seconds a link may spend connecting before Steam gives up. Applies to the whole process. Zero leaves Steam's value alone.")]
        private ushort _connectingTimeoutSeconds = 10;
        /// <summary>
        /// Seconds a connected peer waits without hearing from the other side before Steam drops the link.
        /// </summary>
        /// <remarks>Process-wide. Zero leaves Steam's value alone.</remarks>
        [SerializeField]
        [Tooltip("Seconds a peer waits without hearing from the other side before the link drops. Applies to the whole process. Zero leaves Steam's value alone.")]
        private ushort _idleTimeoutSeconds = 15;
        /// <summary>
        /// The most clients the server accepts at once, the host's own client included.
        /// </summary>
        [SerializeField]
        [Tooltip("Most clients the server accepts at once. A host's own client takes one place, so 4 seats the host and three others.")]
        private uint _maximumConnections = 250;
        /// <summary>
        /// The largest packet a client may send the server, in bytes, before the server disconnects it.
        /// </summary>
        [SerializeField]
        [Tooltip("Largest packet a client may send the server; a client that sends a larger one is disconnected. Zero sets no limit.")]
        private uint _maximumClientPacketBytes = 8 * 1024 * 1024;
        /// <summary>
        /// True to receive each client through its own native call, which avoids a per-message allocation on IL2CPP servers.
        /// </summary>
        [SerializeField]
        [Tooltip("Receive each client through its own call. Avoids 232 bytes of garbage per received message on IL2CPP servers, at one extra native call per client per frame. Makes no difference to garbage on Mono.")]
        private bool _perConnectionReceiveEnabled;
        /// <summary>
        /// The send rate Steam never drops below on a connection, in kilobytes (1,024 bytes) per second.
        /// </summary>
        /// <remarks>
        /// Process-wide. A <see cref="FacepunchAddressing.SteamId"/> server sends each client at this rate, not at the maximum, so it
        /// decides how long a large reliable message or a burst takes to reach a client there. Defaults to Steam's 256 KiB a second,
        /// because every client of a listen server gets the rate at once and together they can exceed a home upload.
        /// </remarks>
        [SerializeField]
        [Tooltip("Rate Steam never sends slower than on a connection. A SteamID server sends each client at this rate, so raise it to deliver large messages faster, within the host's upload across all clients. Applies to the whole process. Zero leaves Steam's value alone.")]
        private uint _sendRateMinimumKilobytesPerSecond = 256;
        /// <summary>
        /// The send rate Steam never rises above on a connection, in kilobytes (1,024 bytes) per second.
        /// </summary>
        /// <remarks>Process-wide. Defaults to 4 MiB a second rather than Steam's 256 KiB, so a late join or a large burst can use a good link.</remarks>
        [SerializeField]
        [Tooltip("Rate Steam never sends faster than on a connection. Applies to the whole process. Zero leaves Steam's value alone.")]
        private uint _sendRateMaximumKilobytesPerSecond = 4096;
        /// <summary>
        /// The most bytes Steam holds queued on a connection before it refuses a send.
        /// </summary>
        /// <remarks>Process-wide. Defaults to 2 MiB rather than Steam's 512 KiB so a large late join or scene load fits.</remarks>
        [SerializeField]
        [Tooltip("Data Steam may hold queued per connection. Applies to the whole process. Reliable messages past it wait; only one message larger than it closes the link. Zero leaves Steam's value alone.")]
        private uint _sendBufferSizeBytes = 2 * 1024 * 1024;
        /// <summary>
        /// True to admit a client only once Steam has confirmed its auth session ticket, through a <see cref="SteamTicketAuthenticator"/>.
        /// </summary>
        [SerializeField]
        [Tooltip("Admit a client only once Steam confirms its ticket: it owns the game and is not banned. Server and clients both need it on; a client with it off is dropped after the authentication timeout.")]
        private bool _ticketAuthenticationEnabled;
        /// <summary>
        /// True for the server to tell every client the SteamID of every other client.
        /// </summary>
        [SerializeField]
        [Tooltip("Tell every client the SteamID of every other client, for names, avatars and friend checks. SteamIDs link to public Steam profiles, so leave it off where players should not learn who the others are. Only the server's value counts.")]
        private bool _shareSteamIdsEnabled;
        /// <summary>
        /// The gate handed to the transport at each start, or <see langword="null"/> for none.
        /// </summary>
        private FacepunchSockets.ConnectingClientFilterHandler _connectingClientFilter;
        /// <summary>
        /// The authenticator this component registered, or <see langword="null"/> when ticket authentication is off.
        /// </summary>
        private SteamTicketAuthenticator _steamTicketAuthenticator;

        /// <inheritdoc/>
        public override async Task<Transport> AddToAsync(CoreManager coreManager)
        {
            // The transport manager removes the transport on shutdown and adds it again on the next start, so the last start's authenticator goes first.
            _steamTicketAuthenticator?.Deinitialize();
            _steamTicketAuthenticator = null;

            FacepunchSockets = (FacepunchSockets)await coreManager.TransportManager.AddTransportAsync<FacepunchSockets>();

            FacepunchSockets.Addressing = _addressing;
            FacepunchSockets.RemoteSteamId = _remoteSteamId;
            FacepunchSockets.RemoteAddress = _remoteAddress;
            FacepunchSockets.SetPort(_port);
            FacepunchSockets.SetBindAddress(_bindAddress);
            FacepunchSockets.Configuration.ConnectingTimeoutSeconds = _connectingTimeoutSeconds;
            FacepunchSockets.Configuration.ConnectedTimeoutSeconds = _idleTimeoutSeconds;
            FacepunchSockets.SetMaximumConnections(_maximumConnections);
            FacepunchSockets.MaximumClientPacketBytes = _maximumClientPacketBytes;
            FacepunchSockets.PerConnectionReceiveEnabled = _perConnectionReceiveEnabled;
            FacepunchSockets.ShareSteamIdsEnabled = _shareSteamIdsEnabled;
            FacepunchSockets.SendRateMinimumKilobytesPerSecond = _sendRateMinimumKilobytesPerSecond;
            FacepunchSockets.SendRateMaximumKilobytesPerSecond = _sendRateMaximumKilobytesPerSecond;
            FacepunchSockets.SendBufferSizeBytes = _sendBufferSizeBytes;
            FacepunchSockets.ConnectingClientFilter = _connectingClientFilter;

            if (_ticketAuthenticationEnabled)
            {
                _steamTicketAuthenticator = new();
                _steamTicketAuthenticator.Initialize(coreManager, FacepunchSockets);
            }

            return FacepunchSockets;
        }

        /// <summary>
        /// Unregisters the ticket authenticator, if this component registered one.
        /// </summary>
        private void OnDestroy()
        {
            _steamTicketAuthenticator?.Deinitialize();
            _steamTicketAuthenticator = null;
        }
    }
}
