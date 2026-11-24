using System.Threading.Tasks;
using CodeBoost.Logging;
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

        /// <summary>
        /// The transport this hosts sessions for, or <see langword="null"/> until <see cref="CreateSessionHost"/> has found it.
        /// </summary>
        private FacepunchSockets _facepunchSockets;

        /// <inheritdoc/>
        protected override ISessionHost CreateSessionHost(Transport transport)
        {
            if (transport is FacepunchSockets facepunchSockets)
            {
                _facepunchSockets = facepunchSockets;

                return new FacepunchSessionHost(facepunchSockets) { HostedAddress = _hostedAddress };
            }

            Debug.LogError($"[{nameof(FacepunchHostMigration)}] on [{name}] needs a {nameof(FacepunchTransport)} beside it and found [{transport.GetType().Name}], so host migration is off.");

            return null;
        }

        /// <summary>
        /// Opens the session once Steam grants the server its fake address, when the server started before it had one.
        /// </summary>
        /// <param name="sessionOpenResult">Why the session could not be opened.</param>
        /// <remarks>
        /// A server started the ordinary way opens its session the moment it starts and reads its address straight away. Over
        /// <see cref="FacepunchAddressing.FakeIp"/> that address is still <see langword="null"/> until Steam grants it, a few seconds later, so the open
        /// fails with <see cref="SessionOpenResult.ServerAddressUnavailable"/>. This waits for the grant and then tries again, or
        /// tries at once when the grant has already landed by the time the failure is heard.
        /// </remarks>
        protected override void OnSessionOpenFailed(SessionOpenResult sessionOpenResult)
        {
            base.OnSessionOpenFailed(sessionOpenResult);

            if (sessionOpenResult is not SessionOpenResult.ServerAddressUnavailable || _facepunchSockets is not { Addressing: FacepunchAddressing.FakeIp })
                return;

            // Unsubscribed first, so a second failure while waiting never subscribes twice.
            _facepunchSockets.ServerFakeIpSettled -= OnServerFakeIpSettled;

            if (_facepunchSockets.ServerFakeAddress is null && !_facepunchSockets.IsServerFakeIpRefused)
                _facepunchSockets.ServerFakeIpSettled += OnServerFakeIpSettled;
            else
                TryOpenSession();
        }

        /// <inheritdoc/>
        /// <remarks>Stops waiting for a fake address, then calls the base, so the session is handed on as usual.</remarks>
        protected override void OnDestroy()
        {
            if (_facepunchSockets is not null)
                _facepunchSockets.ServerFakeIpSettled -= OnServerFakeIpSettled;

            base.OnDestroy();
        }

        /// <summary>
        /// Stops waiting and opens the session.
        /// </summary>
        /// <remarks>Raised when Steam grants the fake address, refuses it, or the server stops, which is why the address is read rather than assumed.</remarks>
        private void OnServerFakeIpSettled()
        {
            _facepunchSockets.ServerFakeIpSettled -= OnServerFakeIpSettled;
            TryOpenSession();
        }

        /// <summary>
        /// Opens the session when Steam granted an address and the server is still running without one, otherwise logs why it cannot.
        /// </summary>
        /// <remarks>A peer already hosting or being promoted publishes its address through its own hosting start and is left alone.</remarks>
        private void TryOpenSession()
        {
            if (Migration is null || Migration.State is NewfarmMigrationState.Hosting or NewfarmMigrationState.Promoting)
                return;

            if (_facepunchSockets.ServerFakeAddress is null)
            {
                if (_facepunchSockets.IsServerFakeIpRefused)
                    Logger<FacepunchHostMigration>.LogError($"Steam refused the server on [{name}] a fake IP address, so its session could not be opened.");
                else
                    Logger<FacepunchHostMigration>.LogError($"The server on [{name}] stopped before Steam granted it a fake IP address, so its session could not be opened.");

                return;
            }

            if (!_facepunchSockets.CoreManager.TransportManager.IsServerStarted)
            {
                Logger<FacepunchHostMigration>.LogError($"The server on [{name}] stopped before its session could be opened.");

                return;
            }

            _ = OpenSessionAsync();
        }

        /// <summary>
        /// Opens the session now that the server has its fake address, and logs the outcome.
        /// </summary>
        private async Task OpenSessionAsync()
        {
            SessionOpenResult sessionOpenResult = await Migration.OpenSessionAsync();

            if (sessionOpenResult is SessionOpenResult.Success)
                Logger<FacepunchHostMigration>.LogInformation($"The server on [{name}] opened its session after Steam granted it a fake IP address.");
            else
                Logger<FacepunchHostMigration>.LogError($"The server on [{name}] could not open its session after Steam granted it a fake IP address: [{sessionOpenResult}].");
        }
    }
}
