using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.NetworkLoop;
using Nucleus.Managers.Transports;
using Nucleus.Packets;
using Nucleus.Transports;
using Steamworks;
using ConnectionStatus = Steamworks.Data.ConnectionStatus;
using NetAddress = Steamworks.Data.NetAddress;
using NetIdentity = Steamworks.Data.NetIdentity;
using SteamConnection = Steamworks.Data.Connection;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// A <see cref="Transport"/> that carries a session over Steam Networking Sockets through Facepunch.Steamworks: peer to peer by SteamID
    /// through Valve's relay network, by IP address, or by a fake IP address Steam grants a game server.
    /// </summary>
    /// <remarks>
    /// A pair of sockets on <see cref="SocketPairTransport{T0,T1}"/>. Facepunch's connection reports are queued and applied on the network
    /// loop thread when each socket receives. A host's own client is paired in process. Facepunch uses whichever Steam interface the game
    /// initialized, and the user's when both are up. Steam has to be initialized, and its callbacks pumped, by the game; this transport never
    /// initializes or shuts Steam down.
    /// </remarks>
    public class FacepunchSockets : SocketPairTransport<FacepunchServerSocket, FacepunchClientSocket>, INetworkLoopStepCallback
    {
        /// <summary>
        /// Decides whether a client asking to connect may proceed, before the engine sees it.
        /// </summary>
        /// <param name="steamId">Who is asking, as Steam authenticated it, or <see cref="UnsetSteamId"/> when it has no SteamID.</param>
        /// <returns><see langword="true"/> to accept the client.</returns>
        public delegate bool ConnectingClientFilterHandler(ulong steamId);
        /// <summary>
        /// A connected client's SteamID became known to this peer, or the client left.
        /// </summary>
        /// <remarks>
        /// Raised on the network loop thread. A server hears of every client; a client hears of itself, and of the others while the server
        /// has <see cref="ShareSteamIdsEnabled"/> on. It is not raised for the entries dropped when a session ends.
        /// </remarks>
        public event SteamIdChangedHandler SteamIdChanged;
        /// <summary>
        /// Handler for the <see cref="SteamIdChanged"/> event.
        /// </summary>
        /// <param name="connectionId">The <see cref="Connection.Id"/> of the client.</param>
        /// <param name="steamId">The client's SteamID64, or <see cref="UnsetSteamId"/> when the client has left.</param>
        public delegate void SteamIdChangedHandler(uint connectionId, ulong steamId);
        /// <summary>
        /// How the server is reached, and so how a client dials it.
        /// </summary>
        /// <remarks>Read at the next start of either half. Both sides have to agree on it.</remarks>
        public FacepunchAddressing Addressing;
        /// <summary>
        /// The SteamID64 of the server a client connects to in <see cref="FacepunchAddressing.SteamId"/>.
        /// </summary>
        /// <remarks><see cref="UnsetSteamId"/>, or this peer's own SteamID, joins the server this same peer is running. Read at the next client start.</remarks>
        public ulong ServerSteamId;
        /// <summary>
        /// The address and port a client connects to in <see cref="FacepunchAddressing.IpAddress"/> and <see cref="FacepunchAddressing.FakeIp"/>,
        /// such as <c>203.0.113.7:27015</c> or <c>[2001:db8::7]:27015</c>.
        /// </summary>
        /// <remarks>
        /// <see langword="null"/> or empty joins the server this same peer is running. Without a port, <see cref="Configuration.Port"/> is
        /// used, and <c>localhost</c> is the IPv4 loopback; host names are not looked up. A fake IP is always IPv4. Read at the next client start.
        /// </remarks>
        public string ServerAddress;
        /// <summary>
        /// The Steam virtual port the server listens on and the client connects to in <see cref="FacepunchAddressing.SteamId"/>.
        /// </summary>
        /// <remarks><see cref="FacepunchAddressing.IpAddress"/> listens on <see cref="Configuration.Port"/> instead; <see cref="SetPort"/> sets both.</remarks>
        public int VirtualPort;
        /// <summary>
        /// The send rate Steam never drops below on a connection, in bytes per second.
        /// </summary>
        /// <remarks>Process-wide: Facepunch offers no per-connection options. <see cref="UnsetSendSetting"/> leaves Steam's current value. Applied at each start.</remarks>
        public uint SendRateMinimumBytesPerSecond = UnsetSendSetting;
        /// <summary>
        /// The send rate Steam never rises above on a connection, in bytes per second.
        /// </summary>
        /// <remarks>Process-wide: Facepunch offers no per-connection options. <see cref="UnsetSendSetting"/> leaves Steam's current value. Applied at each start.</remarks>
        public uint SendRateMaximumBytesPerSecond = UnsetSendSetting;
        /// <summary>
        /// The most bytes Steam holds queued on a connection before it refuses a send, in bytes.
        /// </summary>
        /// <remarks>
        /// Process-wide: Facepunch offers no per-connection options. Reliable messages that would overfill it wait in this transport until
        /// Steam drains; unreliable ones are dropped. <see cref="UnsetSendSetting"/> leaves Steam's current value. Applied at each start.
        /// </remarks>
        public uint SendBufferSizeBytes = UnsetSendSetting;
        /// <summary>
        /// The largest packet the server accepts from a client, in bytes; a client that sends a larger one is disconnected.
        /// </summary>
        /// <remarks><see cref="UnsetMaximumClientPacketBytes"/> sets no limit. Read on every receive.</remarks>
        public uint MaximumClientPacketBytes = UnsetMaximumClientPacketBytes;
        /// <summary>
        /// True to receive each client through its own native call rather than one poll group call.
        /// </summary>
        /// <remarks>
        /// <c>SocketManager.Receive</c> allocates 232 bytes per message under IL2CPP, and this path avoids that, at one native call per
        /// client per frame. It makes no difference to garbage on Mono. Read at the next server start.
        /// </remarks>
        public bool PerConnectionReceiveEnabled;
        /// <summary>
        /// True for the server to tell every client the SteamID of every other client.
        /// </summary>
        /// <remarks>Only the server's setting counts. A SteamID links to the player's public Steam profile, so leave this off where players should not learn who the others are.</remarks>
        public bool ShareSteamIdsEnabled;
        /// <summary>
        /// An optional gate every client asking to connect passes before the engine sees it, or <see langword="null"/> to accept anyone while the server has room.
        /// </summary>
        /// <remarks>Called on the network loop thread. A refused client is closed with a reason it can log.</remarks>
        public ConnectingClientFilterHandler ConnectingClientFilter;
        /// <inheritdoc/>
        /// <remarks>One, because Facepunch's receive callbacks report neither a message's flags nor its lane, so each message carries its channel in a header byte.</remarks>
        public override uint DatagramOverheadBytes => FacepunchMessages.HeaderBytes;
        /// <summary>
        /// The time the server half spends receiving each frame, in microseconds, smoothed over roughly the last twenty frames.
        /// </summary>
        public double ServerReceiveMicroseconds { get; internal set; }
        /// <summary>
        /// How many reliable sends Steam has refused since this transport was added, each of which closed its link.
        /// </summary>
        public int RefusedReliableSendCount { get; internal set; }
        /// <summary>
        /// How many unreliable sends Steam has refused since this transport was added, each of which was dropped.
        /// </summary>
        public int RefusedUnreliableSendCount { get; internal set; }
        /// <summary>
        /// The largest reliable packet the engine has handed this transport since it was added, in bytes.
        /// </summary>
        public int LargestReliablePacketBytes { get; private set; }
        /// <summary>
        /// The largest unreliable packet the engine has handed this transport since it was added, in bytes.
        /// </summary>
        public int LargestUnreliablePacketBytes { get; private set; }
        /// <summary>
        /// The fake address and port Steam granted this server in <see cref="FacepunchAddressing.FakeIp"/>, or <see langword="null"/> until Steam grants one.
        /// </summary>
        public string ServerFakeAddress { get; internal set; }
        /// <summary>
        /// The value of <see cref="ServerSteamId"/> that means this peer's own server.
        /// </summary>
        public const ulong UnsetSteamId = 0;
        /// <summary>
        /// The value of a send setting that leaves Steam's value alone.
        /// </summary>
        public const uint UnsetSendSetting = 0;
        /// <summary>
        /// The <see cref="MaximumClientPacketBytes"/> that sets no limit.
        /// </summary>
        public const uint UnsetMaximumClientPacketBytes = 0;
        /// <summary>
        /// The launch-argument flag which sets <see cref="ServerAddress"/>.
        /// </summary>
        public const string AddressArgument = "address";
        /// <summary>
        /// The launch-argument flag which sets <see cref="ServerSteamId"/>.
        /// </summary>
        public const string SteamIdArgument = "steamid";
        /// <summary>
        /// The send buffer Steam gives a connection by default, in bytes.
        /// </summary>
        internal const int DefaultSendBufferBytes = 512 * 1024;
        /// <summary>
        /// The name read as the IPv4 loopback address wherever an address is accepted; no other host name is looked up.
        /// </summary>
        internal const string LocalhostText = "localhost";
        /// <summary>
        /// The conditions Steam simulates, or <see langword="null"/> for none.
        /// </summary>
        private NetworkConditions? _networkConditions;
        /// <summary>
        /// True while this transport's step callback is registered with the network loop.
        /// </summary>
        private bool _isStepCallbackRegistered;
        /// <summary>
        /// The SteamID of each client this peer knows of, by <see cref="Connection.Id"/>.
        /// </summary>
        private readonly Dictionary<uint, ulong> _steamIdsByConnectionId = new();
        /// <summary>
        /// The <see cref="Connection.Id"/> of each client this peer knows of, by SteamID.
        /// </summary>
        private readonly Dictionary<ulong, uint> _connectionIdsBySteamId = new();
        /// <summary>
        /// The priority of each lane; both share one, so neither starves the other.
        /// </summary>
        private static readonly int[] LanePriorities = { 0, 0 };
        /// <summary>
        /// The share of the send rate each lane gets while both have data waiting, three to one in favour of unreliable state.
        /// </summary>
        private static readonly ushort[] LaneWeights = { 1, 3 };
        /// <summary>
        /// The length of the IPv6 form Steam stores every address in, an IPv4 address included.
        /// </summary>
        private const int IPv6AddressBytes = 16;
        /// <summary>
        /// The length of Steam's address, the IPv6 form followed by the port.
        /// </summary>
        private const int NetAddressBytes = IPv6AddressBytes + sizeof(ushort);

        /// <inheritdoc/>
        /// <remarks>Registers the frame's flush once both sockets are rented and initialized.</remarks>
        public override async Task InitializeAsync(CoreManager coreManager, uint transportId)
        {
            await base.InitializeAsync(coreManager, transportId);

            coreManager.NetworkLoopManager.RegisterNetworkLoopStepCallbacks(this);
            _isStepCallbackRegistered = true;

            coreManager.ServerManager.ClientAuthenticated += OnClientAuthenticated;
            coreManager.ServerManager.ClientDisconnecting += OnClientDisconnecting;
            coreManager.ClientManager.LocalClientAuthenticated += OnLocalClientAuthenticated;
            coreManager.TransportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            coreManager.MessageManager.RegisterMessageHandler<SteamIdNotice>(OnSteamIdNoticeReceived);
        }

        /// <inheritdoc/>
        /// <remarks>Steam carries every link through a relay or a direct route it chooses itself, so the security checks stay on.</remarks>
        public override bool IsLocalTransport() => false;

        /// <inheritdoc/>
        /// <remarks>Sets both <see cref="VirtualPort"/> and <see cref="Configuration.Port"/>, so whichever <see cref="Addressing"/> runs uses it.</remarks>
        public override void SetPort(ushort port)
        {
            VirtualPort = port;
            Configuration.Port = port;
        }

        /// <inheritdoc/>
        /// <remarks>Applied to clients arriving after the call; clients already admitted stay.</remarks>
        public override void SetMaximumConnections(uint maximumConnections) => ServerConfiguration.MaximumConnections = maximumConnections;

        /// <inheritdoc/>
        /// <remarks>Used only by a <see cref="FacepunchAddressing.IpAddress"/> server. Every other mode, and the client, lets Steam choose its local address.</remarks>
        public override bool SetBindAddress(string bindAddress)
        {
            if (string.IsNullOrEmpty(bindAddress))
            {
                Configuration.BindAddress = null;

                return true;
            }

            if (!string.Equals(bindAddress, LocalhostText, StringComparison.OrdinalIgnoreCase) && !IPAddress.TryParse(bindAddress, out _))
            {
                Logger<FacepunchSockets>.LogError($"The bind address [{bindAddress}] is not an IP address or [{LocalhostText}].");

                return false;
            }

            Configuration.BindAddress = bindAddress;

            return true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Facepunch exposes only simulated latency and loss, so jitter, reordering and duplication are logged as ignored. Steam keeps these
        /// settings for the whole process. The host's own client, paired in process, never crosses Steam and is unaffected.
        /// </remarks>
        public override void SetNetworkConditions(NetworkConditions? networkConditions)
        {
            _networkConditions = networkConditions;

            if (networkConditions is { } conditions && (conditions.JitterMilliseconds > 0f || conditions.OutOfOrderChance > 0f || conditions.DuplicateChance > 0f))
                Logger<FacepunchSockets>.LogWarning("Facepunch can only simulate latency and packet loss, so the jitter, out of order and duplicate conditions were ignored.");

            // Steam may not be up yet; the conditions are applied again at every start.
            ApplyNetworkConditions();
        }

        /// <inheritdoc/>
        public override NetworkConditions? GetNetworkConditions() => _networkConditions;

        /// <summary>
        /// Reads Steam's live status of the link carrying a Connection.
        /// </summary>
        /// <param name="connection">A remote client on this peer's server, or this peer's own client Connection for its link to the server.</param>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns><see langword="true"/> when the Connection rides a Steam link; the host's own client, paired in process, has none.</returns>
        public bool TryGetLinkStatus(Connection connection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            if (connection is null)
                return false;

            // The host's own client is both of these Connections at once, and has no Steam link on either side.
            if (ClientSocket is not null && connection == ClientSocket.Connection)
                return ClientSocket.TryGetLinkStatus(out linkStatus);

            return ServerSocket is not null && ServerSocket.TryGetLinkStatus(connection, out linkStatus);
        }

        /// <summary>
        /// Gets the SteamID of a connected client.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="steamId">The client's SteamID64, as Steam authenticated its link.</param>
        /// <returns><see langword="true"/> when this peer knows it.</returns>
        public bool TryGetSteamId(uint connectionId, out ulong steamId) => _steamIdsByConnectionId.TryGetValue(connectionId, out steamId);

        /// <summary>
        /// Gets the SteamID of a connected client.
        /// </summary>
        /// <param name="connection">The client's Connection, on any peer.</param>
        /// <param name="steamId">The client's SteamID64, as Steam authenticated its link.</param>
        /// <returns><see langword="true"/> when this peer knows it.</returns>
        public bool TryGetSteamId(Connection connection, out ulong steamId)
        {
            steamId = UnsetSteamId;

            return connection is not null && TryGetSteamId(connection.Id, out steamId);
        }

        /// <summary>
        /// Gets the Connection of the client playing as a SteamID.
        /// </summary>
        /// <param name="steamId">The SteamID64 to find.</param>
        /// <param name="connection">This peer's Connection for that client.</param>
        /// <returns><see langword="true"/> when this peer knows the SteamID and holds a Connection for its client.</returns>
        public bool TryGetConnection(ulong steamId, out Connection connection)
        {
            connection = null;

            return _connectionIdsBySteamId.TryGetValue(steamId, out uint connectionId) && CoreManager.TransportManager.TryGetConnection(connectionId, out connection);
        }

        /// <inheritdoc/>
        public override async Task ShutdownAsync()
        {
            await base.ShutdownAsync();

            if (!_isStepCallbackRegistered)
                return;

            CoreManager.NetworkLoopManager.UnregisterNetworkLoopStepCallbacks(this);
            _isStepCallbackRegistered = false;

            CoreManager.ServerManager.ClientAuthenticated -= OnClientAuthenticated;
            CoreManager.ServerManager.ClientDisconnecting -= OnClientDisconnecting;
            CoreManager.ClientManager.LocalClientAuthenticated -= OnLocalClientAuthenticated;
            CoreManager.TransportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            CoreManager.MessageManager.UnregisterMessageHandler<SteamIdNotice>(OnSteamIdNoticeReceived);
            ForgetAllSteamIds();
        }

        /// <inheritdoc/>
        NetworkLoopSteps INetworkLoopStepCallback.GetNetworkLoopSteps() => NetworkLoopSteps.LateVariableUpdate;

        /// <summary>
        /// Hands the frame's queued reliable messages of both sockets to Steam, the engine having sent everything it will this frame.
        /// </summary>
        /// <param name="networkLoopStep">The step being performed.</param>
        /// <param name="stepDelta">Delta information about the step.</param>
        void INetworkLoopStepCallback.OnNetworkLoopStep(NetworkLoopSteps networkLoopStep, StepDelta stepDelta)
        {
            ServerSocket?.FlushQueuedMessages();
            ClientSocket?.FlushQueuedMessages();
        }

        /// <summary>
        /// Gets the identity of the server this peer's client is linked to over Steam.
        /// </summary>
        /// <param name="serverIdentity">The server's identity, as Steam authenticated it.</param>
        /// <returns><see langword="true"/> when the client half has a Steam link to read it from.</returns>
        internal bool TryGetServerIdentity(out NetIdentity serverIdentity)
        {
            serverIdentity = default;

            return ClientSocket is not null && ClientSocket.TryGetServerIdentity(out serverIdentity);
        }

        /// <summary>
        /// Checks Steam is initialized, applies the process-wide settings and starts relay discovery.
        /// </summary>
        /// <returns><see langword="true"/> when Steam is ready to connect.</returns>
        internal bool TryPrepareSteam()
        {
            if (IsSteamShutDown())
            {
                Logger<FacepunchSockets>.LogError("Steam is not initialized. Call SteamClient.Init, or SteamServer.Init for a dedicated server, before connecting.");

                return false;
            }

            SteamNetworkingUtils.InitRelayNetworkAccess();
            ApplyGlobalSettings();
            ApplyNetworkConditions();

            return true;
        }

        /// <summary>
        /// Gets the send buffer the connections opened next will have.
        /// </summary>
        /// <returns>The buffer, in bytes.</returns>
        internal int GetSendBufferBytes() => SendBufferSizeBytes is UnsetSendSetting ? DefaultSendBufferBytes : (int)SendBufferSizeBytes;

        /// <summary>
        /// Checks whether every Steam interface has shut down under this transport.
        /// </summary>
        /// <returns><see langword="true"/> when no Steam call may be made.</returns>
        /// <remarks>The game owns Steam, and a native call after its shutdown crashes the process rather than throwing.</remarks>
        internal static bool IsSteamShutDown() => !SteamClient.IsValid && !SteamServer.IsValid;

        /// <summary>
        /// Gets this peer's own SteamID from whichever Steam interface is up.
        /// </summary>
        /// <returns>The SteamID64.</returns>
        internal static ulong GetOwnSteamId() => SteamClient.IsValid ? SteamClient.SteamId.Value : SteamServer.SteamId.Value;

        /// <summary>
        /// Changes a socket's local Connection state, logging when the engine refuses.
        /// </summary>
        /// <param name="transportManager">The socket's TransportManager.</param>
        /// <param name="invoker">The role to move.</param>
        /// <param name="localConnectionState">The state to move to.</param>
        /// <param name="connection">The role's Connection.</param>
        /// <returns><see langword="true"/> when the role is in that state afterwards.</returns>
        internal static bool ChangeLocalState(TransportManager transportManager, Invoker invoker, LocalConnectionState localConnectionState, Connection connection)
        {
            ConnectionStateChangeResult connectionStateChangeResult = transportManager.ChangeLocalConnectionState(invoker, localConnectionState, connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success or ConnectionStateChangeResult.AlreadyInState)
                return true;

            Logger<FacepunchSockets>.LogError($"The {invoker} could not be moved to [{localConnectionState}]: [{connectionStateChangeResult}].");

            return false;
        }

        /// <summary>
        /// Walks a socket's local Connection down through disconnecting to disconnected, unless it is already there.
        /// </summary>
        /// <param name="transportManager">The socket's TransportManager.</param>
        /// <param name="invoker">The role to move.</param>
        /// <param name="connection">The role's Connection.</param>
        internal static void ChangeLocalStateToDisconnected(TransportManager transportManager, Invoker invoker, Connection connection)
        {
            if (connection.LocalState is LocalConnectionState.Disconnected)
                return;

            // Connecting goes straight down: passing through Disconnecting is for a link that was up.
            if (connection.LocalState is LocalConnectionState.Connected)
                ChangeLocalState(transportManager, invoker, LocalConnectionState.Disconnecting, connection);

            ChangeLocalState(transportManager, invoker, LocalConnectionState.Disconnected, connection);
        }

        /// <summary>
        /// Reads Steam's live status of one link.
        /// </summary>
        /// <param name="steamConnection">The link.</param>
        /// <param name="linkStatus">The status, when Steam is up to report one.</param>
        /// <returns><see langword="true"/> when Steam reported the link's status.</returns>
        internal static bool TryReadLinkStatus(SteamConnection steamConnection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            if (steamConnection.Id == 0 || IsSteamShutDown())
                return false;

            ConnectionStatus connectionStatus = steamConnection.QuickStatus();
            linkStatus = new(in connectionStatus);

            return true;
        }

        /// <summary>
        /// Gives a connection its reliable and unreliable lanes, so unreliable state never waits behind a reliable burst.
        /// </summary>
        /// <param name="steamConnection">The connection to configure.</param>
        /// <remarks>Done once, before any traffic: reconfiguring lanes on a link that has carried traffic proved unreliable.</remarks>
        internal static void ConfigureLanes(SteamConnection steamConnection)
        {
            Result result = steamConnection.ConfigureConnectionLanes(LanePriorities, LaneWeights);

            if (result is not Result.OK)
                Logger<FacepunchSockets>.LogError($"Steam could not configure lanes: [{result}]. Unreliable packets will share the reliable lane.");
        }

        /// <summary>
        /// Reads an IPv4 or IPv6 address, with or without a port, into Steam's form.
        /// </summary>
        /// <param name="text">The address, such as <c>203.0.113.7</c>, <c>203.0.113.7:27015</c>, <c>2001:db8::7</c> or <c>[2001:db8::7]:27015</c>. <c>localhost</c> is the IPv4 loopback, and empty or <see langword="null"/> is every interface.</param>
        /// <param name="defaultPort">The port used when <paramref name="text"/> names none.</param>
        /// <param name="netAddress">The address read.</param>
        /// <param name="isPortForced">True to use <paramref name="defaultPort"/> even when <paramref name="text"/> names a port.</param>
        /// <returns><see langword="true"/> when <paramref name="text"/> was an address. Host names are not looked up.</returns>
        internal static bool TryParseIpAddress(string text, ushort defaultPort, out NetAddress netAddress, bool isPortForced)
        {
            netAddress = default;

            if (string.IsNullOrEmpty(text))
            {
                netAddress = NetAddress.AnyIp(defaultPort);

                return true;
            }

            string hostText = text;
            string portText = null;
            int separatorIndex = text.IndexOf(':');

            // An IPv6 address carries colons of its own, so its port is written after brackets: [2001:db8::7]:27015.
            if (text[0] == '[')
            {
                int closeIndex = text.IndexOf(']');

                if (closeIndex < 0)
                    return false;

                hostText = text.Substring(1, closeIndex - 1);

                if (closeIndex + 1 < text.Length)
                {
                    if (text[closeIndex + 1] != ':')
                        return false;

                    portText = text.Substring(closeIndex + 2);
                }
            }
            else if (separatorIndex >= 0 && separatorIndex == text.LastIndexOf(':'))
            {
                hostText = text.Substring(0, separatorIndex);
                portText = text.Substring(separatorIndex + 1);
            }

            ushort port = defaultPort;

            if (portText is not null)
            {
                if (!ushort.TryParse(portText, out ushort parsedPort))
                    return false;

                if (!isPortForced)
                    port = parsedPort;
            }

            // NetAddress.LocalHost is the IPv6 loopback, which an IPv4 client cannot reach, so localhost is read as 127.0.0.1.
            if (string.Equals(hostText, LocalhostText, StringComparison.OrdinalIgnoreCase))
            {
                netAddress = NetAddress.From(IPAddress.Loopback, port);

                return true;
            }

            if (!IPAddress.TryParse(hostText, out IPAddress ipAddress))
                return false;

            switch (ipAddress.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    netAddress = NetAddress.From(ipAddress, port);

                    return true;
                // A scope, as in fe80::1%3, has no place in Steam's address, so a link-local address that needs one is refused.
                case AddressFamily.InterNetworkV6 when ipAddress.ScopeId == 0:
                    netAddress = CreateIPv6Address(ipAddress, port);

                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Builds Steam's form of an IPv6 address.
        /// </summary>
        /// <param name="ipAddress">The IPv6 address.</param>
        /// <param name="port">The port.</param>
        /// <returns>The address.</returns>
        /// <remarks>
        /// Facepunch 2.5.2's <see cref="NetAddress.From(IPAddress, ushort)"/> throws for IPv6, so the address is written in Steam's own layout,
        /// the one <see cref="NetAddress"/> declares: the 16 address bytes in network order, then the port in the machine's order, which is
        /// little endian on every platform Steam ships for.
        /// </remarks>
        private static NetAddress CreateIPv6Address(IPAddress ipAddress, ushort port)
        {
            Span<byte> addressBytes = stackalloc byte[NetAddressBytes];
            ipAddress.TryWriteBytes(addressBytes, out int _);
            BinaryPrimitives.WriteUInt16LittleEndian(addressBytes.Slice(IPv6AddressBytes), port);

            return MemoryMarshal.Read<NetAddress>(addressBytes);
        }

        /// <inheritdoc/>
        /// <remarks>Records the packet's size, then routes it to the sending role's socket.</remarks>
        protected override void OnSendPacket(OutgoingPacket outgoingPacket)
        {
            RecordPacketSize(outgoingPacket.Channel, outgoingPacket.Payload.Count);

            base.OnSendPacket(outgoingPacket);
        }

        /// <inheritdoc/>
        /// <remarks>Applies <see cref="Transport.PortArgument"/> to <see cref="VirtualPort"/> as well, and adds <see cref="AddressArgument"/> and <see cref="SteamIdArgument"/>.</remarks>
        protected override void OnApplyCommandLineArguments(CommandLineArguments commandLineArguments)
        {
            base.OnApplyCommandLineArguments(commandLineArguments);

            if (commandLineArguments.TryGetUInt16(PortArgument, out ushort port))
                VirtualPort = port;

            if (commandLineArguments.TryGetValue(AddressArgument, out string address))
                ServerAddress = address;

            if (!commandLineArguments.TryGetValue(SteamIdArgument, out string steamIdText))
                return;

            if (ulong.TryParse(steamIdText, out ulong steamId))
                ServerSteamId = steamId;
            else
                Logger<FacepunchSockets>.LogError($"The launch argument [{SteamIdArgument}] value [{steamIdText}] is not a SteamID.");
        }

        /// <summary>
        /// Records the size of a packet the engine handed either socket.
        /// </summary>
        /// <param name="channel">The channel the packet rides.</param>
        /// <param name="payloadBytes">The packet's length, in bytes.</param>
        private void RecordPacketSize(Channel channel, int payloadBytes)
        {
            if (channel is Channel.Reliable)
            {
                if (payloadBytes > LargestReliablePacketBytes)
                    LargestReliablePacketBytes = payloadBytes;
            }
            else if (payloadBytes > LargestUnreliablePacketBytes)
            {
                LargestUnreliablePacketBytes = payloadBytes;
            }
        }

        /// <summary>
        /// Writes the transport's settings into Steam's process-wide configuration.
        /// </summary>
        /// <remarks>Facepunch's listen and connect calls take no options. Unauthenticated IP connections are turned off explicitly, so a setting elsewhere in the game cannot let an unidentified peer in.</remarks>
        private void ApplyGlobalSettings()
        {
            SteamNetworkingUtils.AllowWithoutAuth = 0;

            if (Configuration.ConnectingTimeoutSeconds > 0)
                SteamNetworkingUtils.ConnectionTimeout = Configuration.ConnectingTimeoutSeconds * 1000;

            if (Configuration.ConnectedTimeoutSeconds > 0)
                SteamNetworkingUtils.Timeout = Configuration.ConnectedTimeoutSeconds * 1000;

            if (SendRateMinimumBytesPerSecond is not UnsetSendSetting)
                SteamNetworkingUtils.SendRateMin = (int)SendRateMinimumBytesPerSecond;

            if (SendRateMaximumBytesPerSecond is not UnsetSendSetting)
                SteamNetworkingUtils.SendRateMax = (int)SendRateMaximumBytesPerSecond;

            if (SendBufferSizeBytes is not UnsetSendSetting)
                SteamNetworkingUtils.SendBufferSize = (int)SendBufferSizeBytes;
        }

        /// <summary>
        /// Writes <see cref="_networkConditions"/> into Steam's simulation, if Steam is up.
        /// </summary>
        private void ApplyNetworkConditions()
        {
            if (IsSteamShutDown())
                return;

            NetworkConditions networkConditions = _networkConditions ?? default;

            SteamNetworkingUtils.FakeSendPacketLag = networkConditions.LatencyMilliseconds;
            SteamNetworkingUtils.FakeSendPacketLoss = networkConditions.PacketLossChance * 100f;
        }

        /// <summary>
        /// Records an authenticated client's SteamID on the server, and shares it when <see cref="ShareSteamIdsEnabled"/> is on.
        /// </summary>
        /// <param name="connection">The client.</param>
        private void OnClientAuthenticated(Connection connection)
        {
            if (connection.Transport != this || !ulong.TryParse(connection.RemoteAddress, out ulong steamId) || steamId == UnsetSteamId)
                return;

            uint connectionId = connection.Id;
            RecordSteamId(connectionId, steamId);

            if (!ShareSteamIdsEnabled)
                return;

            CoreManager.ServerManager.SendMessageExcept(Channel.Reliable, new SteamIdNotice { ConnectionId = connectionId, SteamId = steamId }, connection);

            // The host's own client reads this peer's records directly.
            if (IsOwnClientConnection(connection))
                return;

            foreach (KeyValuePair<uint, ulong> steamIdByConnectionId in _steamIdsByConnectionId)
                connection.SendMessage(Channel.Reliable, new SteamIdNotice { ConnectionId = steamIdByConnectionId.Key, SteamId = steamIdByConnectionId.Value });
        }

        /// <summary>
        /// Forgets a departing client's SteamID on the server and tells the remaining clients.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void OnClientDisconnecting(Connection connection)
        {
            if (!TryForgetSteamId(connection.Id))
                return;

            // The server's stop walks its clients too, and by then there is no one left to tell.
            if (ShareSteamIdsEnabled && CoreManager.TransportManager.IsServerStarted)
                CoreManager.ServerManager.SendMessageExcept(Channel.Reliable, new SteamIdNotice { ConnectionId = connection.Id, SteamId = UnsetSteamId }, connection);
        }

        /// <summary>
        /// Records this pure client's own SteamID once the server has assigned its Connection an id.
        /// </summary>
        /// <param name="localClientConnection">This peer's own client Connection.</param>
        private void OnLocalClientAuthenticated(Connection localClientConnection)
        {
            if (localClientConnection.Transport == this && !CoreManager.TransportManager.IsServerStarted)
                RecordSteamId(localClientConnection.Id, GetOwnSteamId());
        }

        /// <summary>
        /// Applies the server's word about a client's SteamID.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="steamIdNotice">The notice.</param>
        /// <remarks>Only the server may say who a client is; a notice from a client would let it claim another player's account.</remarks>
        private void OnSteamIdNoticeReceived(in MessageContext messageContext, SteamIdNotice steamIdNotice)
        {
            if (!messageContext.IsSenderServer || CoreManager.TransportManager.IsServerStarted)
                return;

            if (steamIdNotice.SteamId is UnsetSteamId)
                TryForgetSteamId(steamIdNotice.ConnectionId);
            else
                RecordSteamId(steamIdNotice.ConnectionId, steamIdNotice.SteamId);
        }

        /// <summary>
        /// Forgets every SteamID when this peer's session ends.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Connection.LocalState is not LocalConnectionState.Disconnected)
                return;

            if (connectionStateChange.Invoker is Invoker.Server || !CoreManager.TransportManager.IsServerStarted)
                ForgetAllSteamIds();
        }

        /// <summary>
        /// Records a client's SteamID and reports it, unless this peer already holds that value.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="steamId">The client's SteamID64.</param>
        private void RecordSteamId(uint connectionId, ulong steamId)
        {
            if (_steamIdsByConnectionId.TryGetValue(connectionId, out ulong recordedSteamId) && recordedSteamId == steamId)
                return;

            _steamIdsByConnectionId[connectionId] = steamId;
            _connectionIdsBySteamId[steamId] = connectionId;

            SteamIdChanged?.Invoke(connectionId, steamId);
        }

        /// <summary>
        /// Forgets a client's SteamID and reports that it left.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <returns><see langword="true"/> when this peer held a SteamID for the client.</returns>
        private bool TryForgetSteamId(uint connectionId)
        {
            if (!_steamIdsByConnectionId.Remove(connectionId, out ulong steamId))
                return false;

            // One account connected twice is held under its latest connection, which an earlier one leaving must not remove.
            if (_connectionIdsBySteamId.TryGetValue(steamId, out uint recordedConnectionId) && recordedConnectionId == connectionId)
                _connectionIdsBySteamId.Remove(steamId);

            SteamIdChanged?.Invoke(connectionId, UnsetSteamId);

            return true;
        }

        /// <summary>
        /// Drops every SteamID this peer holds, without reporting them.
        /// </summary>
        private void ForgetAllSteamIds()
        {
            _steamIdsByConnectionId.Clear();
            _connectionIdsBySteamId.Clear();
        }
    }
}
