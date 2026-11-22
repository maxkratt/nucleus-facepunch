using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Newfarm.Wire;
using Nucleus.Connections;
using Nucleus.Integrations.Newfarm;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using NetAddress = Steamworks.Data.NetAddress;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// Carries a session over Steam Networking Sockets on behalf of a newfarm directory, which is the whole of what this transport
    /// contributes to a handover.
    /// </summary>
    /// <remarks>
    /// Noticing the host go, being elected, adopting the world and finding where the session moved to all belong to
    /// <see cref="NewfarmHostMigration"/>. This is only the part that is Steam: listening, naming where, connecting to somebody else, and
    /// leaving.
    /// The credential follows <see cref="FacepunchSockets.Addressing"/>. By SteamID it is the hosting player's SteamID, so nobody needs an
    /// address. By IP address it is an address and port, which is <see cref="HostedAddress"/> when set and otherwise this machine's own IPv4
    /// address, which only reaches peers on the same network. By fake IP it is the address Steam grants the new host. SteamIDs are filed
    /// under one tag and addresses, real or fake, under another, so a peer never tries to read a SteamID as an address; a fake IP address is
    /// dialled exactly as a real one is.
    /// </remarks>
    /// <seealso cref="FacepunchHostMigration"/>
    public sealed class FacepunchSessionHost : ISessionHost
    {
        /// <inheritdoc/>
        public string AdapterTag => _facepunchSockets.Addressing is FacepunchAddressing.SteamId ? SteamIdAdapterTag : IpAddressAdapterTag;

        /// <inheritdoc/>
        /// <remarks>
        /// A server started some other way than <see cref="StartHostingAsync"/>, which is how a session opens on its own, is named the first
        /// time this is read while it is up, and forgotten once it is down.
        /// </remarks>
        public string HostedCredential
        {
            get
            {
                if (!IsHostingLinkUp)
                {
                    _hostedCredential = string.Empty;

                    return string.Empty;
                }

                if (_hostedCredential.Length == 0 && TryGetHostedCredential(out string hostedCredential))
                    _hostedCredential = hostedCredential;

                return _hostedCredential;
            }
        }

        /// <inheritdoc/>
        public bool IsHostingLinkUp => IsLinkUp(Invoker.Server);

        /// <inheritdoc/>
        public bool IsJoinedLinkUp => IsLinkUp(Invoker.Client);

        /// <summary>
        /// The address other peers reach this peer's server at when peers are addressed by IP address, such as a public address with a
        /// forwarded port. Empty uses the bind address when one is set, and otherwise this machine's own IPv4 address.
        /// </summary>
        /// <remarks>
        /// Read when hosting starts. The configured port is added when this names none. The address and port together may run to
        /// <see cref="NewfarmWireFormat.MaximumTextLength"/> characters, which any IPv4 address fits and a full IPv6 one may not.
        /// </remarks>
        public string HostedAddress = string.Empty;

        /// <summary>
        /// The tag a session addressed by SteamID is filed under with a directory.
        /// </summary>
        public const string SteamIdAdapterTag = "facepunch-steam-id";

        /// <summary>
        /// The tag a session addressed by IP address or fake IP address is filed under with a directory.
        /// </summary>
        public const string IpAddressAdapterTag = "facepunch-steam-ip";

        /// <summary>
        /// The transport this stands a session up on.
        /// </summary>
        private readonly FacepunchSockets _facepunchSockets;

        /// <summary>
        /// Where the session this peer is hosting can be reached, set when hosting starts or when first asked for.
        /// </summary>
        private string _hostedCredential = string.Empty;

        /// <summary>
        /// Completed when Steam settles a fake address request, or <see langword="null"/> while hosting by fake IP is not under way.
        /// </summary>
        private TaskCompletionSource<bool> _fakeIpTaskCompletionSource;

        /// <summary>
        /// Completed with whether the client's link opened, or <see langword="null"/> while no join is under way.
        /// </summary>
        private TaskCompletionSource<bool> _joinTaskCompletionSource;

        /// <summary>
        /// The client Connection the join under way is waiting on, so a change to an earlier link is never read as this one's.
        /// </summary>
        private Connection _joinConnection;

        /// <summary>
        /// Creates an adapter over a Facepunch transport.
        /// </summary>
        /// <param name="facepunchSockets">The transport carrying the session.</param>
        public FacepunchSessionHost(FacepunchSockets facepunchSockets)
        {
            _facepunchSockets = facepunchSockets ?? throw new ArgumentNullException(nameof(facepunchSockets));
        }

        /// <inheritdoc/>
        /// <remarks>Empty. Both sockets are driven by the engine's own loop, and a connect waits on Steam's callbacks, which the game already pumps.</remarks>
        public void Poll() { }

        /// <inheritdoc/>
        /// <remarks>
        /// Opening a listen socket is immediate: Steam either opens one or refuses on the spot. Under fake IP the budget is spent waiting for
        /// Steam to grant the address, which it does only once the server is listening; a refusal ends the wait at once, and a grant that
        /// does not arrive in time stops the server again.
        /// </remarks>
        public async Task<SessionHostResult> StartHostingAsync(uint timeoutMilliseconds)
        {
            if (_facepunchSockets.Addressing is FacepunchAddressing.FakeIp)
                return await StartHostingByFakeIpAsync(timeoutMilliseconds);

            // Named before the server starts, so a peer that cannot say where it is never stands up a session nobody can reach.
            if (!TryGetHostedCredential(out string hostedCredential))
                return SessionHostResult.LocalFailure;

            if (await _facepunchSockets.ConnectAsync(Invoker.Server) is not ConnectionStateChangeResult.Success)
                return SessionHostResult.LocalFailure;

            _hostedCredential = hostedCredential;

            return SessionHostResult.Success;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The client is given the shorter of the budget and its own <see cref="FacepunchSockets.ConnectTimeoutMilliseconds"/>, and the wait
        /// ends when its link opens or comes back down. Facepunch's connect returns while the link is still connecting, so the result is
        /// read from the client's own state change rather than from the connect.
        /// </remarks>
        public async Task<SessionHostResult> JoinAsync(string credential, uint timeoutMilliseconds)
        {
            if (string.IsNullOrEmpty(credential))
            {
                Logger<FacepunchSessionHost>.LogError("A join was asked for with nowhere to join, which is this peer's own fault rather than the host's.");

                return SessionHostResult.LocalFailure;
            }

            // Whatever this peer was in is left before another is tried, so a retry never builds on the attempt before it.
            await LeaveAsync();

            if (_facepunchSockets.Addressing is not FacepunchAddressing.SteamId)
            {
                _facepunchSockets.RemoteAddress = credential;
            }
            else if (ulong.TryParse(credential, out ulong remoteSteamId))
            {
                _facepunchSockets.RemoteSteamId = remoteSteamId;
            }
            else
            {
                Logger<FacepunchSessionHost>.LogError($"[{credential}] is not a SteamID. Every peer in a session must address by the same means.");

                return SessionHostResult.LocalFailure;
            }

            bool isLinkOpened = await ConnectClientAsync(timeoutMilliseconds);

            if (isLinkOpened)
                return SessionHostResult.Success;

            /* Steam carrying the far end's refusal back is the one thing a directory should hear: the host is not there, or will not have
             * this peer. A host that never answered is left to the directory, which stops hearing its heartbeat anyway, so a peer whose own
             * line dropped never has a healthy host challenged. */
            return _facepunchSockets.IsLastConnectRefused ? SessionHostResult.CredentialUnreachable : SessionHostResult.LocalFailure;
        }

        /// <inheritdoc/>
        public async Task StopHostingAsync()
        {
            _hostedCredential = string.Empty;

            // A transport that has been shut down holds no sockets, and so nothing to stop.
            if (_facepunchSockets.TryGetConnection(Invoker.Server, out Connection _))
                await _facepunchSockets.DisconnectLocalConnectionAsync(Invoker.Server);
        }

        /// <inheritdoc/>
        public async Task LeaveAsync()
        {
            if (_facepunchSockets.TryGetConnection(Invoker.Client, out Connection _))
                await _facepunchSockets.DisconnectLocalConnectionAsync(Invoker.Client);
        }

        /// <summary>
        /// Starts the server by fake IP and waits for Steam to settle its address request.
        /// </summary>
        /// <param name="timeoutMilliseconds">The longest to wait for the grant.</param>
        /// <returns><see cref="SessionHostResult.Success"/> once the server is listening at a granted address.</returns>
        /// <remarks>
        /// The wait is heard before the server starts, because a process Steam has already granted an address to settles inside the start.
        /// Facepunch raises nothing for a refused request; the server socket reads one back and says so through
        /// <see cref="FacepunchSockets.ServerFakeIpSettled"/>, as it does when the server stops, Steam shutting down included.
        /// </remarks>
        private async Task<SessionHostResult> StartHostingByFakeIpAsync(uint timeoutMilliseconds)
        {
            _fakeIpTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _facepunchSockets.ServerFakeIpSettled += OnServerFakeIpSettled;

            string hostedCredential;

            try
            {
                if (await _facepunchSockets.ConnectAsync(Invoker.Server) is not ConnectionStateChangeResult.Success)
                    return SessionHostResult.LocalFailure;

                if (_facepunchSockets.ServerFakeAddress is null && !_facepunchSockets.IsServerFakeIpRefused)
                    await WaitAsync(_fakeIpTaskCompletionSource.Task, timeoutMilliseconds);

                hostedCredential = _facepunchSockets.ServerFakeAddress ?? string.Empty;
            }
            finally
            {
                _facepunchSockets.ServerFakeIpSettled -= OnServerFakeIpSettled;
                _fakeIpTaskCompletionSource = null;
            }

            if (hostedCredential.Length == 0)
            {
                if (_facepunchSockets.IsServerFakeIpRefused)
                    Logger<FacepunchSessionHost>.LogError("Steam refused this peer a fake IP address, so it cannot host the session.");
                else if (!IsHostingLinkUp)
                    Logger<FacepunchSessionHost>.LogError("The server stopped before Steam granted it a fake IP address, so this peer cannot host the session.");
                else
                    Logger<FacepunchSessionHost>.LogError($"Steam granted this peer no fake IP address within [{timeoutMilliseconds}] ms, so it cannot host the session.");

                await StopHostingAsync();

                return SessionHostResult.LocalFailure;
            }

            _hostedCredential = hostedCredential;

            return SessionHostResult.Success;
        }

        /// <summary>
        /// Starts the client toward the target already set and waits until its link opens or comes back down.
        /// </summary>
        /// <param name="timeoutMilliseconds">The budget the directory gave the join.</param>
        /// <returns><see langword="true"/> when the client's link opened.</returns>
        /// <remarks>
        /// The client reads its timeout as the connect starts, inside the call, so the budget is lent only for the call and the transport's
        /// own is back before anything else could read it. The client gives up at that deadline, so its own state change ends the wait. A
        /// budget of zero lends nothing. The wait is heard only once the connect has returned, and the state read then covers a link that
        /// opened or failed inside it; both run on the network loop thread, so no change falls between them.
        /// </remarks>
        private async Task<bool> ConnectClientAsync(uint timeoutMilliseconds)
        {
            uint configuredTimeoutMilliseconds = _facepunchSockets.ConnectTimeoutMilliseconds;

            if (timeoutMilliseconds > 0 && (configuredTimeoutMilliseconds is FacepunchSockets.UnsetConnectTimeoutMilliseconds || timeoutMilliseconds < configuredTimeoutMilliseconds))
                _facepunchSockets.ConnectTimeoutMilliseconds = timeoutMilliseconds;

            ConnectionStateChangeResult connectionStateChangeResult;

            try
            {
                connectionStateChangeResult = await _facepunchSockets.ConnectAsync(Invoker.Client);
            }
            finally
            {
                _facepunchSockets.ConnectTimeoutMilliseconds = configuredTimeoutMilliseconds;
            }

            if (connectionStateChangeResult is not ConnectionStateChangeResult.Success || !_facepunchSockets.TryGetConnection(Invoker.Client, out Connection connection))
                return false;

            if (connection.LocalState is LocalConnectionState.Connected)
                return true;

            if (connection.LocalState is LocalConnectionState.Disconnected)
                return false;

            TransportManager transportManager = _facepunchSockets.CoreManager.TransportManager;
            _joinTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _joinConnection = connection;
            transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;

            try
            {
                return await _joinTaskCompletionSource.Task;
            }
            finally
            {
                transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
                _joinTaskCompletionSource = null;
                _joinConnection = null;
            }
        }

        /// <summary>
        /// Names where another peer reaches this one's server once it is listening.
        /// </summary>
        /// <param name="hostedCredential">This peer's SteamID, its fake IP address, or the address and port it listens on.</param>
        /// <returns><see langword="true"/> when there is something another peer could be pointed at.</returns>
        private bool TryGetHostedCredential(out string hostedCredential)
        {
            switch (_facepunchSockets.Addressing)
            {
                // A peer that takes a session over is a player, so its SteamID is the user's.
                case FacepunchAddressing.SteamId:
                    hostedCredential = SteamClient.IsValid ? SteamClient.SteamId.Value.ToString() : string.Empty;

                    if (hostedCredential.Length > 0)
                        return true;

                    Logger<FacepunchSessionHost>.LogError("Steam is not running as a user, so this peer has no SteamID to give other peers.");

                    return false;
                case FacepunchAddressing.FakeIp:
                    hostedCredential = _facepunchSockets.ServerFakeAddress ?? string.Empty;

                    return hostedCredential.Length > 0;
            }

            string address = HostedAddress;

            if (string.IsNullOrEmpty(address))
                address = _facepunchSockets.Configuration.BindAddress;

            if (string.IsNullOrEmpty(address) || IsAnyAddress(address))
                address = GetOwnIPv4Address();

            if (string.IsNullOrEmpty(address))
            {
                Logger<FacepunchSessionHost>.LogError($"This machine has no IPv4 address to give other peers. Set [{nameof(HostedAddress)}] to the address they reach this peer at.");

                hostedCredential = string.Empty;

                return false;
            }

            // An address that already carries a port is kept as it is; the client adds the configured one only when it names none.
            hostedCredential = FacepunchSockets.TryParseIpAddress(address, defaultPort: 0, out NetAddress netAddress, isPortForced: false) && netAddress.Port != 0
                ? address
                : FormatAddressWithPort(address, _facepunchSockets.Configuration.Port);

            // The directory carries a credential of limited length, and a full IPv6 address with its port can run past it.
            if (hostedCredential.Length <= NewfarmWireFormat.MaximumTextLength)
                return true;

            Logger<FacepunchSessionHost>.LogError($"[{hostedCredential}] runs to [{hostedCredential.Length}] characters and a session directory carries at most [{NewfarmWireFormat.MaximumTextLength}]. Set [{nameof(HostedAddress)}] to a shorter address, such as an IPv4 one.");

            hostedCredential = string.Empty;

            return false;
        }

        /// <summary>
        /// Returns whether one side of the transport is connected.
        /// </summary>
        /// <param name="invoker">Which side to ask about.</param>
        /// <returns><see langword="true"/> when that side is connected.</returns>
        private bool IsLinkUp(Invoker invoker) => _facepunchSockets.TryGetConnection(invoker, out Connection connection) && connection.LocalState is LocalConnectionState.Connected;

        /// <summary>
        /// Ends the wait for a fake address.
        /// </summary>
        private void OnServerFakeIpSettled() => _fakeIpTaskCompletionSource?.TrySetResult(true);

        /// <summary>
        /// Ends the wait for a join once this transport's client link has opened or come back down.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Invoker is not Invoker.Client || !ReferenceEquals(connectionStateChange.Connection, _joinConnection))
                return;

            LocalConnectionState localConnectionState = connectionStateChange.Connection.LocalState;

            if (localConnectionState is LocalConnectionState.Connected)
                _joinTaskCompletionSource?.TrySetResult(true);
            else if (localConnectionState is LocalConnectionState.Disconnected)
                _joinTaskCompletionSource?.TrySetResult(false);
        }

        /// <summary>
        /// Waits for a task, or for a budget to run out, whichever comes first.
        /// </summary>
        /// <param name="task">The task to wait for.</param>
        /// <param name="timeoutMilliseconds">The budget, in milliseconds.</param>
        /// <returns>The wait.</returns>
        private static async Task WaitAsync(Task task, uint timeoutMilliseconds)
        {
            using (CancellationTokenSource cancellationTokenSource = new())
            {
                Task delayTask = Task.Delay((int)Math.Min(timeoutMilliseconds, int.MaxValue), cancellationTokenSource.Token);

                await Task.WhenAny(task, delayTask);

                cancellationTokenSource.Cancel();
            }
        }

        /// <summary>
        /// Returns whether an address names every interface rather than one a peer could dial.
        /// </summary>
        /// <param name="address">The address.</param>
        /// <returns><see langword="true"/> for the IPv4 or IPv6 any address.</returns>
        private static bool IsAnyAddress(string address) => IPAddress.TryParse(address, out IPAddress ipAddress) && (ipAddress.Equals(IPAddress.Any) || ipAddress.Equals(IPAddress.IPv6Any));

        /// <summary>
        /// Writes an address with a port, an IPv6 one in brackets so its own colons are not read as the port's.
        /// </summary>
        /// <param name="address">The address, with no port.</param>
        /// <param name="port">The port.</param>
        /// <returns>The address and port, such as <c>203.0.113.7:27015</c> or <c>[2001:db8::7]:27015</c>.</returns>
        private static string FormatAddressWithPort(string address, ushort port) => IPAddress.TryParse(address, out IPAddress ipAddress) && ipAddress.AddressFamily is AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

        /// <summary>
        /// Returns this machine's first IPv4 address that is not loopback, or an empty string when it has none.
        /// </summary>
        /// <returns>The address.</returns>
        /// <remarks>Reaches peers on the same network only. A peer behind a router hosting players outside it sets <see cref="HostedAddress"/>.</remarks>
        private static string GetOwnIPv4Address()
        {
            try
            {
                foreach (IPAddress ipAddress in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ipAddress.AddressFamily is AddressFamily.InterNetwork && !IPAddress.IsLoopback(ipAddress))
                        return ipAddress.ToString();
                }
            }
            catch (SocketException socketException)
            {
                Logger<FacepunchSessionHost>.LogWarning($"Could not read this machine's addresses: [{socketException.Message}].");
            }

            return string.Empty;
        }
    }
}
