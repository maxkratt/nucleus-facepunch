using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Nucleus.Transports.Sockets;
using Steamworks;
using ConnectionInfo = Steamworks.Data.ConnectionInfo;
using NetAddress = Steamworks.Data.NetAddress;
using NetIdentity = Steamworks.Data.NetIdentity;
using SteamConnection = Steamworks.Data.Connection;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// The client half of <see cref="FacepunchSockets"/>: dials the server the transport's <see cref="FacepunchSockets.Addressing"/> points
    /// at, or pairs in process with this peer's own running server.
    /// </summary>
    /// <remarks>Inbound packets are attributed to the server socket's Connection, which is what the engine expects of a client.</remarks>
    public class FacepunchClientSocket : CommonSocket, IConnectionManager
    {
        /// <inheritdoc/>
        public override bool IsServer { get; protected set; } = false;
        /// <inheritdoc/>
        public override bool IsEmulated { get; protected set; } = false;
        /// <summary>
        /// The transport this socket belongs to, which holds the settings it reads.
        /// </summary>
        private FacepunchSockets _facepunchSockets;
        /// <summary>
        /// The client's link to a server, or <see langword="null"/> when the client is not linked over Steam.
        /// </summary>
        private ConnectionManager _connectionManager;
        /// <summary>
        /// The receive list of the pass under way, which message callbacks append to, or <see langword="null"/> outside one.
        /// </summary>
        private List<IncomingPacket> _currentReceivedPackets;
        /// <summary>
        /// True while the client is paired in process with this peer's own server.
        /// </summary>
        private bool _isLoopback;
        /// <summary>
        /// True while the client is connecting but holds its dial until Steam's relay network status has settled.
        /// </summary>
        /// <remarks>A process that dials straight after Steam starts knows only Steam's built-in certificate authorities, so it can refuse a server whose certificate a newer one signed.</remarks>
        private bool _isDialHeld;
        /// <summary>
        /// The <see cref="Stopwatch"/> time after which a held dial is made whatever Steam reports.
        /// </summary>
        private long _dialHoldDeadlineTimestamp;
        /// <summary>
        /// True when Steam has closed the client's link and the drop is due on the next receive.
        /// </summary>
        /// <remarks>The drop waits a pass so the messages read in the pass that saw the close, a kick's reason among them, reach the game before it hears the link has gone.</remarks>
        private bool _isLinkDropDue;
        /// <summary>
        /// The pieces received so far of the server's split reliable packet, by the client's Steam link.
        /// </summary>
        private readonly Dictionary<SteamConnection, ArraySegment<byte>> _splitPackets = new();
        /// <summary>
        /// This socket's outbound messages.
        /// </summary>
        private readonly FacepunchSendQueue _sendQueue = new();
        /// <summary>
        /// Link changes Facepunch reported, applied by the next receive.
        /// </summary>
        private readonly ConcurrentQueue<FacepunchStatusChange> _statusChanges = new();
        /// <summary>
        /// The longest a dial is held for Steam's relay network status, in seconds.
        /// </summary>
        private const long DialHoldTimeoutSeconds = 10;
        /// <summary>
        /// The Steam connection value of a client with no link.
        /// </summary>
        private static readonly SteamConnection NoSteamConnection = default;

        /// <inheritdoc/>
        public override void Initialize(Transport transport)
        {
            base.Initialize(transport);

            InitializeConnection();

            _facepunchSockets = (FacepunchSockets)transport;
        }

        /// <summary>
        /// Hands queued reliable messages to Steam, and closes the link if Steam refused reliable data on it.
        /// </summary>
        internal void FlushQueuedMessages()
        {
            _sendQueue.Flush();

            List<FacepunchSendQueue.RefusedSend> refusedSends = _sendQueue.RefusedReliableSends;

            if (refusedSends.Count == 0)
                return;

            bool isLinkRefused = false;

            foreach (FacepunchSendQueue.RefusedSend refusedSend in refusedSends)
            {
                // A link this socket closed, or Steam closed and is due to drop, refuses its leftovers; that is not news.
                if (isLinkRefused || refusedSend.SteamConnection != GetSteamConnection() || _isLinkDropDue)
                    continue;

                isLinkRefused = true;
                _facepunchSockets.RefusedReliableSendCount++;

                Logger<FacepunchClientSocket>.LogError($"Steam refused a reliable message of [{refusedSend.MessageBytes}] bytes: [{refusedSend.Result}]. The link is closed, because the peers can no longer agree.");
            }

            refusedSends.Clear();

            // Closed only now, since a close flushes again.
            if (isLinkRefused)
                Stop();
        }

        /// <summary>
        /// Leaves the server, or unpairs from this peer's own, and walks the client's state down.
        /// </summary>
        /// <returns>The result of the stop.</returns>
        internal ConnectionStateChangeResult Stop()
        {
            if (!_isLoopback && !_isDialHeld && _connectionManager is null && Connection.LocalState is LocalConnectionState.Disconnected)
                return ConnectionStateChangeResult.AlreadyInState;

            _isDialHeld = false;

            if (_isLoopback)
                ReleaseLoopback();
            else
                CloseSteamConnection();

            FacepunchSockets.ChangeLocalStateToDisconnected(TransportManager, Invoker.Client, Connection);

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Ends the in-process pairing from the server's side, which has dropped this peer's own client or heard its link drop.
        /// </summary>
        /// <remarks>The link is dropped once the packets the server already handed over have dispatched; they stay queued for the next receive.</remarks>
        internal void OnLoopbackDropped()
        {
            if (!_isLoopback)
                return;

            _isLoopback = false;

            if (State is LocalConnectionState.Connected)
                DeferLocalClientLinkDrop();
        }

        /// <summary>
        /// Takes a packet this peer's own server sent its paired client, for the next receive.
        /// </summary>
        /// <param name="incomingPacket">The packet, owning its payload.</param>
        internal void EnqueueLoopbackPacket(IncomingPacket incomingPacket) => IncomingPackets.Enqueue(incomingPacket);

        /// <summary>
        /// Reads Steam's live status of the client's link to the server.
        /// </summary>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns><see langword="true"/> when the client rides a Steam link; a client paired in process has none.</returns>
        internal bool TryGetLinkStatus(out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            return _connectionManager is not null && FacepunchSockets.TryReadLinkStatus(_connectionManager.Connection, out linkStatus);
        }

        /// <summary>
        /// Gets the identity of the server this client is linked to over Steam.
        /// </summary>
        /// <param name="serverIdentity">The server's identity, as Steam authenticated it.</param>
        /// <returns><see langword="true"/> when the client has a Steam link to read it from.</returns>
        internal bool TryGetServerIdentity(out NetIdentity serverIdentity)
        {
            serverIdentity = default;

            if (_connectionManager is null)
                return false;

            serverIdentity = _connectionManager.ConnectionInfo.Identity;

            return true;
        }

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> ConnectAsync() => GetResultTask(Start());

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectAsync() => GetResultTask(Stop());

        /// <inheritdoc/>
        protected override void SendPacket(OutgoingPacket outgoingPacket)
        {
            if (_isLoopback)
            {
                if (State is LocalConnectionState.Connected && _facepunchSockets.ServerSocket.TryEnqueueLoopbackPacket(outgoingPacket))
                    Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);
                else
                    Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            if (_connectionManager is null || _isLinkDropDue || Connection.LocalState is not LocalConnectionState.Connected || FacepunchSockets.IsSteamShutDown())
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            _sendQueue.Send(_connectionManager.Connection, outgoingPacket, _facepunchSockets);
        }

        /// <inheritdoc/>
        /// <remarks>Drops a link Steam closed during the previous pass, makes a held dial once Steam's relay status has settled, applies queued changes, then drains what the server sent.</remarks>
        protected override void ReceivePackets(ref List<IncomingPacket> receivedPackets)
        {
            if (_isLinkDropDue)
            {
                CloseSteamConnection();
                FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Disconnected, Connection);

                return;
            }

            // Facepunch publishes no event for the relay status, so while a dial is held there is no edge to hear; only a held dial reads it.
            if (_isDialHeld && (IsRelayStatusSettled(SteamNetworkingUtils.Status) || Stopwatch.GetTimestamp() >= _dialHoldDeadlineTimestamp))
            {
                _isDialHeld = false;
                Dial();
            }

            while (_statusChanges.TryDequeue(out FacepunchStatusChange statusChange))
                ApplyStatusChange(statusChange);

            // Packets this peer's own server handed over in process, still delivered after it dropped the pairing.
            while (IncomingPackets.TryDequeue(out IncomingPacket incomingPacket))
                receivedPackets.Add(incomingPacket);

            if (_connectionManager is null)
                return;

            // As on the server, a Steam shut down under a live link has dropped it.
            if (FacepunchSockets.IsSteamShutDown())
            {
                Logger<FacepunchClientSocket>.LogWarning("Steam was shut down while the client was connected, so the client stopped.");
                Stop();

                return;
            }

            int previousPacketCount = receivedPackets.Count;

            _currentReceivedPackets = receivedPackets;
            _connectionManager.Receive(FacepunchMessages.MaximumMessagesPerReceive, receiveToEnd: true);
            _currentReceivedPackets = null;

            // A message proves the link is up even when Steam's Connected report has yet to be pumped; left Connecting, the client would drop the server's authentication.
            if (receivedPackets.Count > previousPacketCount && Connection.LocalState is LocalConnectionState.Connecting)
                FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Connected, Connection);
        }

        /// <summary>
        /// Starts connecting to the server <see cref="FacepunchSockets.Addressing"/> points at, or pairs in process with this peer's own server.
        /// </summary>
        /// <returns>The result of the start. A Steam link is still connecting when this returns.</returns>
        private ConnectionStateChangeResult Start()
        {
            if (_isLoopback || _isDialHeld || _connectionManager is not null)
                return ConnectionStateChangeResult.AlreadyInState;

            if (!_facepunchSockets.TryPrepareSteam())
                return ConnectionStateChangeResult.UnspecifiedError;

            // Fresh Connections, so a new link carries none of the last one's ticks or authentication.
            RecycleConnection();

            FacepunchServerSocket serverSocket = _facepunchSockets.ServerSocket;
            serverSocket.RecycleConnectionUnlessListening();

            ClearCollection(IncomingPackets);

            // A peer running its own server only ever joins that server, so a target left over from an earlier join is ignored rather than dialled.
            if (serverSocket.IsListening)
            {
                if (!IsTargetingSelf())
                    Logger<FacepunchClientSocket>.LogWarning($"Ignoring the target [{DescribeTarget()}] because this peer is running a server, whose client joins it. Stop the server first to join another.");

                return StartLoopback(serverSocket);
            }

            if (IsTargetingSelf())
                return StartLoopback(serverSocket);

            if (!FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Connecting, Connection))
                return ConnectionStateChangeResult.UnspecifiedError;

            if (IsRelayStatusSettled(SteamNetworkingUtils.Status))
                return Dial();

            _isDialHeld = true;
            _dialHoldDeadlineTimestamp = Stopwatch.GetTimestamp() + DialHoldTimeoutSeconds * Stopwatch.Frequency;
            Logger<FacepunchClientSocket>.LogInformation($"Waiting for Steam's relay network before connecting to [{DescribeTarget()}].");

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Opens the client's Steam link, or walks the client back down when Steam refuses.
        /// </summary>
        /// <returns>The result of the dial. The link is still connecting when this returns.</returns>
        private ConnectionStateChangeResult Dial()
        {
            _sendQueue.SendBufferBytes = _facepunchSockets.GetSendBufferBytes();
            _connectionManager = Connect();

            if (_connectionManager is not null)
            {
                FacepunchSockets.ConfigureLanes(_connectionManager.Connection);

                return ConnectionStateChangeResult.Success;
            }

            Logger<FacepunchClientSocket>.LogError($"Steam refused to connect to [{DescribeTarget()}].");

            FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Disconnected, Connection);

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Checks whether the client's target names this peer itself.
        /// </summary>
        /// <returns><see langword="true"/> when the client should join this peer's own server.</returns>
        private bool IsTargetingSelf()
        {
            if (_facepunchSockets.Addressing is FacepunchAddressing.SteamId)
                return _facepunchSockets.ServerSteamId is FacepunchSockets.UnsetSteamId || _facepunchSockets.ServerSteamId == FacepunchSockets.GetOwnSteamId();

            return string.IsNullOrWhiteSpace(_facepunchSockets.ServerAddress);
        }

        /// <summary>
        /// Opens the client's Steam link to the target <see cref="FacepunchSockets.Addressing"/> names.
        /// </summary>
        /// <returns>The link, or <see langword="null"/> when Steam refused or the address could not be read.</returns>
        /// <remarks>Facepunch throws <see cref="ArgumentException"/> when Steam returns an invalid connection.</remarks>
        private ConnectionManager Connect()
        {
            try
            {
                if (_facepunchSockets.Addressing is FacepunchAddressing.SteamId)
                    return SteamNetworkingSockets.ConnectRelay(_facepunchSockets.ServerSteamId, _facepunchSockets.VirtualPort, this);

                string serverAddress = _facepunchSockets.ServerAddress;

                if (FacepunchSockets.TryParseIpAddress(serverAddress.Trim(), _facepunchSockets.Configuration.Port, out NetAddress netAddress, isPortForced: false))
                    return SteamNetworkingSockets.ConnectNormal(netAddress, this);

                Logger<FacepunchClientSocket>.LogError($"[{serverAddress}] is not an IP address, such as 203.0.113.7:27015 or [2001:db8::7]:27015. Host names are not looked up.");

                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Describes where the client is connecting, for a log.
        /// </summary>
        /// <returns>The target.</returns>
        private string DescribeTarget() => _facepunchSockets.Addressing is FacepunchAddressing.SteamId ? $"SteamID {_facepunchSockets.ServerSteamId} on virtual port {_facepunchSockets.VirtualPort}" : $"{_facepunchSockets.Addressing} {_facepunchSockets.ServerAddress}";

        /// <summary>
        /// Pairs the client with this peer's own running server.
        /// </summary>
        /// <param name="serverSocket">This peer's server socket.</param>
        /// <returns>The result of the start.</returns>
        /// <remarks>The pairing is recorded before the server admits the client, because admitting it runs its authentication synchronously.</remarks>
        private ConnectionStateChangeResult StartLoopback(FacepunchServerSocket serverSocket)
        {
            if (!serverSocket.IsListening)
            {
                Logger<FacepunchClientSocket>.LogError($"No server to join. Set [{nameof(FacepunchSockets)}.{nameof(FacepunchSockets.ServerSteamId)}] or [{nameof(FacepunchSockets)}.{nameof(FacepunchSockets.ServerAddress)}] to the server, or start this peer's server first.");

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            if (!TryDriveLocalStateTo(Invoker.Client, LocalConnectionState.Connected))
            {
                FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Disconnected, Connection);

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            _isLoopback = true;

            if (serverSocket.TryPairLoopbackClient(FacepunchSockets.GetOwnSteamId()))
                return ConnectionStateChangeResult.Success;

            Stop();

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Walks the client up or down as Facepunch reported its link.
        /// </summary>
        /// <param name="statusChange">The change.</param>
        private void ApplyStatusChange(FacepunchStatusChange statusChange)
        {
            // A change for a link this client has since closed or replaced.
            if (statusChange.SteamConnection != GetSteamConnection())
                return;

            switch (statusChange.State)
            {
                case ConnectionState.Connected:
                    FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Client, LocalConnectionState.Connected, Connection);
                    break;
                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    Logger<FacepunchClientSocket>.LogInformation($"The link to the server closed: [{statusChange.EndReason}].");
                    _isLinkDropDue = true;

                    break;
            }
        }

        /// <summary>
        /// Ends the in-process pairing, telling the server its own client left while the client's Connection still names its roster entry.
        /// </summary>
        private void ReleaseLoopback()
        {
            _isLoopback = false;
            ClearCollection(IncomingPackets);

            _facepunchSockets.ServerSocket.UnpairLoopbackClient();
        }

        /// <summary>
        /// Closes the client's Steam link, if it has one, after handing Steam whatever was queued for it.
        /// </summary>
        private void CloseSteamConnection()
        {
            SteamConnection steamConnection = GetSteamConnection();

            if (_connectionManager is not null)
            {
                _connectionManager.Interface = null;

                if (!FacepunchSockets.IsSteamShutDown())
                {
                    FlushQueuedMessages();

                    // A flush that was refused has closed the link already.
                    _connectionManager?.Close(linger: false, FacepunchMessages.ApplicationCloseReason, "Disconnected by the client.");
                }
            }

            _sendQueue.Discard(steamConnection);
            FacepunchMessages.DiscardSplitPacket(_splitPackets, steamConnection);

            _connectionManager = null;
            _isLinkDropDue = false;
        }

        /// <summary>
        /// Gets the client's Steam connection.
        /// </summary>
        /// <returns>The connection, or <see cref="NoSteamConnection"/> without a link.</returns>
        private SteamConnection GetSteamConnection() => _connectionManager?.Connection ?? NoSteamConnection;

        /// <summary>
        /// Checks whether Steam has finished trying to reach its relay network, whether or not it succeeded.
        /// </summary>
        /// <param name="availability">The relay network's availability.</param>
        /// <returns><see langword="true"/> when a dial no longer gains anything by waiting.</returns>
        private static bool IsRelayStatusSettled(SteamNetworkingAvailability availability)
        {
            return availability is SteamNetworkingAvailability.Current or SteamNetworkingAvailability.Failed or SteamNetworkingAvailability.CannotTry or SteamNetworkingAvailability.Previously;
        }

        /// <inheritdoc/>
        void IConnectionManager.OnConnecting(ConnectionInfo info) { }

        /// <inheritdoc/>
        void IConnectionManager.OnConnected(ConnectionInfo info) => _statusChanges.Enqueue(new(GetSteamConnection(), info));

        /// <inheritdoc/>
        void IConnectionManager.OnDisconnected(ConnectionInfo info) => _statusChanges.Enqueue(new(GetSteamConnection(), info));

        /// <inheritdoc/>
        void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (_currentReceivedPackets is null)
                return;

            // The server is trusted with what it sends, so its packets are never limited.
            if (FacepunchMessages.TryCreatePacket(data, size, _facepunchSockets.ServerSocket.Connection, GetSteamConnection(), _splitPackets, FacepunchSockets.UnsetMaximumClientPacketBytes, out IncomingPacket incomingPacket, out _))
                _currentReceivedPackets.Add(incomingPacket);
        }

        /// <inheritdoc/>
        /// <remarks>The socket is returned only after it has stopped, so its link is already closed.</remarks>
        public override void OnReturn()
        {
            FacepunchMessages.DiscardSplitPackets(_splitPackets);
            _sendQueue.Clear();
            _statusChanges.Clear();
            ClearCollection(IncomingPackets);

            _connectionManager = null;
            _currentReceivedPackets = null;
            _isLoopback = false;
            _isDialHeld = false;
            _isLinkDropDue = false;
            _facepunchSockets = null;

            base.OnReturn();
        }
    }
}
