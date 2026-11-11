using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CodeBoost.Logging;
using CodeBoost.Performance;
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
    /// The server half of <see cref="FacepunchSockets"/>: listens on Steam in the transport's <see cref="FacepunchSockets.Addressing"/> and
    /// treats every client Steam admits, and this peer's own client paired in process, as an ordinary remote Connection.
    /// </summary>
    /// <remarks>
    /// Facepunch's callbacks are queued and applied on the network loop thread. This peer's own client is keyed by
    /// <see cref="LoopbackSteamConnection"/>, and its roster entry is the client socket's own Connection, which this socket only borrows.
    /// </remarks>
    public class FacepunchServerSocket : CommonSocket, ISocketManager
    {
        /// <inheritdoc/>
        public override bool IsServer { get; protected set; } = true;
        /// <inheritdoc/>
        public override bool IsEmulated { get; protected set; } = false;
        /// <summary>
        /// True while the server is listening.
        /// </summary>
        internal bool IsListening => _socketManager is not null;
        /// <summary>
        /// The transport this socket belongs to, which holds the settings it reads.
        /// </summary>
        private FacepunchSockets _facepunchSockets;
        /// <summary>
        /// The listen socket, or <see langword="null"/> when the server is not running.
        /// </summary>
        private SocketManager _socketManager;
        /// <summary>
        /// The receive list of the pass under way, which message callbacks append to, or <see langword="null"/> outside one.
        /// </summary>
        private List<IncomingPacket> _currentReceivedPackets;
        /// <summary>
        /// True when the running server receives through one <see cref="FacepunchServerLink"/> per client.
        /// </summary>
        private bool _isPerConnectionReceive;
        /// <summary>
        /// The <see cref="Stopwatch"/> time after which an unanswered fake address request is read back, or <see cref="UnsetTimestamp"/> when none is outstanding.
        /// </summary>
        private long _fakeIpDeadlineTimestamp = UnsetTimestamp;
        /// <summary>
        /// The socket id this peer's own client was admitted with, or <see cref="Connection.UnsetId"/> while it is not paired.
        /// </summary>
        private uint _loopbackIdBySocket = Connection.UnsetId;
        /// <summary>
        /// Remote clients by the Steam connection carrying them.
        /// </summary>
        /// <remarks>The host's own client paired in process is keyed by <see cref="LoopbackSteamConnection"/>.</remarks>
        private readonly Dictionary<SteamConnection, Connection> _connectionsBySteamConnection = new();
        /// <summary>
        /// The Steam connection carrying each remote client, so an outbound packet can be addressed.
        /// </summary>
        private readonly Dictionary<Connection, SteamConnection> _steamConnectionsByConnection = new();
        /// <summary>
        /// The receive wrapper of each remote client while <see cref="_isPerConnectionReceive"/> is on.
        /// </summary>
        private readonly Dictionary<SteamConnection, FacepunchServerLink> _serverLinks = new();
        /// <summary>
        /// The pieces received so far of each client's split reliable packet.
        /// </summary>
        private readonly Dictionary<SteamConnection, ArraySegment<byte>> _splitPackets = new();
        /// <summary>
        /// Admitted clients whose links Steam has closed, and whose drop is due on the next receive.
        /// </summary>
        /// <remarks>
        /// Steam still hands over what arrived before a close until the connection is closed locally, so each client stays mapped for one
        /// pass. A list rather than a set, because a departure handler can drop another entry while the list is drained.
        /// </remarks>
        private readonly List<SteamConnection> _departedSteamConnections = new();
        /// <summary>
        /// The links a flush found Steam refusing reliable data on, closed once the flush has finished.
        /// </summary>
        private readonly List<SteamConnection> _refusedSteamConnections = new();
        /// <summary>
        /// The clients that sent a packet past the limit during the receive pass, disconnected once it has finished.
        /// </summary>
        private readonly List<SteamConnection> _overLimitSteamConnections = new();
        /// <summary>
        /// This socket's outbound messages.
        /// </summary>
        private readonly FacepunchSendQueue _sendQueue = new();
        /// <summary>
        /// Connection changes Facepunch reported, applied by the next receive.
        /// </summary>
        private readonly ConcurrentQueue<FacepunchStatusChange> _statusChanges = new();
        /// <summary>
        /// Fake addresses Steam granted, applied by the next receive.
        /// </summary>
        private readonly ConcurrentQueue<NetAddress> _fakeIpResults = new();
        /// <summary>
        /// Gives out the per-socket ids remote clients are known by, and takes back a departed client's id after a delay.
        /// </summary>
        private readonly DelayedUIntPool _idBySocketPool = new(isOwnTimerEnabled: false);
        /// <summary>
        /// The key of this peer's own client, which has no Steam connection.
        /// </summary>
        private static readonly SteamConnection LoopbackSteamConnection = default;
        /// <summary>
        /// How long a departed client's socket id is held before it is given out again, in minutes.
        /// </summary>
        private const double IdReturnMinutes = 5d;
        /// <summary>
        /// The weight each frame's sample carries in <see cref="FacepunchSockets.ServerReceiveMicroseconds"/>.
        /// </summary>
        private const double ReceiveSmoothing = 0.05;
        /// <summary>
        /// The fake port a <see cref="FacepunchAddressing.FakeIp"/> server listens on: the first and only one it asks for.
        /// </summary>
        private const int FakePortIndex = 0;
        /// <summary>
        /// How long Steam gets to answer a fake address request, in seconds.
        /// </summary>
        /// <remarks>Facepunch raises nothing when a request fails, so an unanswered request is read back after this long.</remarks>
        private const long FakeIpTimeoutSeconds = 5;
        /// <summary>
        /// The value of <see cref="_fakeIpDeadlineTimestamp"/> while no request is outstanding.
        /// </summary>
        private const long UnsetTimestamp = 0;

        /// <inheritdoc/>
        public override void Initialize(Transport transport)
        {
            base.Initialize(transport);

            InitializeConnection();

            _facepunchSockets = (FacepunchSockets)transport;
            LocalConnectionStateChanged += OnLocalConnectionStateChanged;

            _idBySocketPool.OnRent();
            _idBySocketPool.Initialize(IdReturnMinutes, firstRentedValue: Connection.UnsetId + 1);
        }

        /// <summary>
        /// Hands queued reliable messages to Steam, and closes any link Steam refused reliable data on.
        /// </summary>
        internal void FlushQueuedMessages()
        {
            _sendQueue.Flush();

            List<FacepunchSendQueue.RefusedSend> refusedSends = _sendQueue.RefusedReliableSends;

            if (refusedSends.Count == 0)
                return;

            foreach (FacepunchSendQueue.RefusedSend refusedSend in refusedSends)
            {
                SteamConnection steamConnection = refusedSend.SteamConnection;

                // A link this socket closed, or Steam closed and is due to drop, refuses its leftovers; that is not news.
                bool isLinkOpen = _connectionsBySteamConnection.ContainsKey(steamConnection) && !_departedSteamConnections.Contains(steamConnection);

                if (!isLinkOpen || _refusedSteamConnections.Contains(steamConnection))
                    continue;

                _facepunchSockets.RefusedReliableSendCount++;
                _refusedSteamConnections.Add(steamConnection);

                Logger<FacepunchServerSocket>.LogError($"Steam refused a reliable message of [{refusedSend.MessageBytes}] bytes: [{refusedSend.Result}]. The link is closed, because the peers can no longer agree.");
            }

            refusedSends.Clear();

            // Closed only now, since a close flushes again; taken off the end, since that flush can add to the list.
            while (_refusedSteamConnections.Count > 0)
            {
                int lastIndex = _refusedSteamConnections.Count - 1;
                SteamConnection refusedSteamConnection = _refusedSteamConnections[lastIndex];
                _refusedSteamConnections.RemoveAt(lastIndex);

                if (_connectionsBySteamConnection.TryGetValue(refusedSteamConnection, out Connection connection))
                    DisconnectRemoteClient(connection, isLingering: false);
            }
        }

        /// <summary>
        /// Pairs this peer's own client with the running server, adopting the client socket's Connection as its roster entry.
        /// </summary>
        /// <param name="steamId">This peer's SteamID, which is the paired client's address.</param>
        /// <returns><see langword="true"/> when the engine admitted the client.</returns>
        internal bool TryPairLoopbackClient(ulong steamId) => TryAdmitClient(LoopbackSteamConnection, steamId);

        /// <summary>
        /// Tells the engine this peer's own client has left the server, if it was paired.
        /// </summary>
        internal void UnpairLoopbackClient()
        {
            if (!_connectionsBySteamConnection.Remove(LoopbackSteamConnection, out Connection loopbackConnection))
                return;

            _steamConnectionsByConnection.Remove(loopbackConnection);
            ClearCollection(IncomingPackets);

            NotifyRemoteDisconnected(loopbackConnection, TakeLoopbackIdBySocket());
        }

        /// <summary>
        /// Takes a packet this peer's own client sent, for the next receive, as a packet from its roster entry.
        /// </summary>
        /// <param name="outgoingPacket">The client's packet. Its payload is copied, so the caller hands it back to the engine.</param>
        /// <returns><see langword="true"/> when the client is paired and the packet was taken.</returns>
        internal bool TryEnqueueLoopbackPacket(OutgoingPacket outgoingPacket)
        {
            if (!_connectionsBySteamConnection.TryGetValue(LoopbackSteamConnection, out Connection loopbackConnection))
                return false;

            IncomingPackets.Enqueue(new(CopyIntoOwnedPayload(outgoingPacket.Payload, out _), outgoingPacket.Channel, loopbackConnection));

            return true;
        }

        /// <summary>
        /// Rents a fresh Connection for this socket unless the server is running.
        /// </summary>
        /// <remarks>A pure client attributes inbound packets to this socket's Connection, so a new client link needs it clean too.</remarks>
        internal void RecycleConnectionUnlessListening()
        {
            if (!IsListening)
                RecycleConnection();
        }

        /// <summary>
        /// Reads Steam's live status of the link carrying a remote client.
        /// </summary>
        /// <param name="connection">The remote client.</param>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns><see langword="true"/> when the client rides a Steam link; this peer's own client has none.</returns>
        internal bool TryGetLinkStatus(Connection connection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            return _steamConnectionsByConnection.TryGetValue(connection, out SteamConnection steamConnection) && FacepunchSockets.TryReadLinkStatus(steamConnection, out linkStatus);
        }

        /// <summary>
        /// Turns one received message into a packet from the client on its connection.
        /// </summary>
        /// <param name="steamConnection">The connection the message arrived on.</param>
        /// <param name="data">The message.</param>
        /// <param name="size">The message's length, in bytes.</param>
        /// <remarks>Clients past the limit are collected rather than dropped here, so nothing changes under Facepunch's receive loop.</remarks>
        internal void OnClientMessage(SteamConnection steamConnection, IntPtr data, int size)
        {
            // A client dropped earlier this frame can still have messages waiting; they belong to nobody now.
            if (_currentReceivedPackets is null || !_connectionsBySteamConnection.TryGetValue(steamConnection, out Connection sender))
                return;

            uint maximumClientPacketBytes = _facepunchSockets.MaximumClientPacketBytes;

            if (FacepunchMessages.TryCreatePacket(data, size, sender, steamConnection, _splitPackets, maximumClientPacketBytes, out IncomingPacket incomingPacket, out bool isOverLimit))
                _currentReceivedPackets.Add(incomingPacket);
            else if (isOverLimit && !_overLimitSteamConnections.Contains(steamConnection))
                _overLimitSteamConnections.Add(steamConnection);
        }

        /// <inheritdoc/>
        protected override void SweepIdPools() => _idBySocketPool.Sweep();

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> ConnectAsync() => GetResultTask(Start());

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectAsync() => GetResultTask(Stop());

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectRemoteClientAsync(Connection connection) => GetResultTask(DisconnectRemoteClient(connection, isLingering: false));

        /// <inheritdoc/>
        /// <remarks>Steam's linger keeps the connection open until the reliable data already queued on it has been acknowledged, which is what a kick's reason needs.</remarks>
        protected override Task<ConnectionStateChangeResult> DisconnectRemoteClientAfterDeliveryAsync(Connection connection) => GetResultTask(DisconnectRemoteClient(connection, isLingering: true));

        /// <inheritdoc/>
        protected override void SendPacket(OutgoingPacket outgoingPacket)
        {
            if (outgoingPacket.Receiver is null || !_steamConnectionsByConnection.TryGetValue(outgoingPacket.Receiver, out SteamConnection steamConnection) || _departedSteamConnections.Contains(steamConnection))
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            if (steamConnection == LoopbackSteamConnection)
            {
                _facepunchSockets.ClientSocket.EnqueueLoopbackPacket(new(CopyIntoOwnedPayload(outgoingPacket.Payload, out _), outgoingPacket.Channel, Connection));
                Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);

                return;
            }

            if (FacepunchSockets.IsSteamShutDown())
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            _sendQueue.Send(steamConnection, outgoingPacket, _facepunchSockets);
        }

        /// <inheritdoc/>
        protected override void ReceivePackets(ref List<IncomingPacket> receivedPackets)
        {
            // Held, because a departure handler can shut the transport down, which returns this socket and clears its reference.
            FacepunchSockets facepunchSockets = _facepunchSockets;
            long startTimestamp = Stopwatch.GetTimestamp();

            ReceiveServerPackets(receivedPackets);

            double sampleMicroseconds = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000d / Stopwatch.Frequency;
            facepunchSockets.ServerReceiveMicroseconds += (sampleMicroseconds - facepunchSockets.ServerReceiveMicroseconds) * ReceiveSmoothing;
        }

        /// <summary>
        /// Opens the listen socket in <see cref="FacepunchSockets.Addressing"/>.
        /// </summary>
        /// <returns>The result of the start.</returns>
        private ConnectionStateChangeResult Start()
        {
            if (IsListening)
                return ConnectionStateChangeResult.AlreadyInState;

            if (!_facepunchSockets.TryPrepareSteam())
                return ConnectionStateChangeResult.UnspecifiedError;

            _sendQueue.SendBufferBytes = _facepunchSockets.GetSendBufferBytes();
            _isPerConnectionReceive = _facepunchSockets.PerConnectionReceiveEnabled;

            // A fresh Connection, so the new server's bookkeeping starts clean rather than carrying the last session's ticks.
            RecycleConnection();

            _socketManager = CreateListenSocket();

            if (!IsListening)
            {
                Logger<FacepunchServerSocket>.LogError($"Steam could not open a [{_facepunchSockets.Addressing}] listen socket.");

                FacepunchSockets.ChangeLocalState(TransportManager, Invoker.Server, LocalConnectionState.Disconnected, Connection);

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            if (TryDriveLocalStateTo(Invoker.Server, LocalConnectionState.Connected))
            {
                Logger<FacepunchServerSocket>.LogInformation($"Steam server listening by [{_facepunchSockets.Addressing}].");

                return ConnectionStateChangeResult.Success;
            }

            Stop();

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Opens the listen socket for <see cref="FacepunchSockets.Addressing"/>.
        /// </summary>
        /// <returns>The socket, or <see langword="null"/> when Steam refused.</returns>
        /// <remarks>Facepunch throws <see cref="ArgumentException"/> when Steam returns an invalid socket.</remarks>
        private SocketManager CreateListenSocket()
        {
            try
            {
                switch (_facepunchSockets.Addressing)
                {
                    case FacepunchAddressing.IpAddress:
                    {
                        Configuration configuration = _facepunchSockets.Configuration;

                        if (FacepunchSockets.TryParseIpAddress(configuration.BindAddress, configuration.Port, out NetAddress bindAddress, isPortForced: true))
                            return SteamNetworkingSockets.CreateNormalSocket(bindAddress, this);

                        Logger<FacepunchServerSocket>.LogError($"Steam could not read the bind address [{configuration.BindAddress}].");

                        return null;
                    }
                    case FacepunchAddressing.FakeIp:
                        RequestFakeIp();

                        return SteamNetworkingSockets.CreateRelaySocketFakeIP(FakePortIndex, this);
                    case FacepunchAddressing.SteamId:
                    default:
                        return SteamNetworkingSockets.CreateRelaySocket(_facepunchSockets.VirtualPort, this);
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Asks Steam for a fake address, or adopts the one this process was already granted.
        /// </summary>
        private void RequestFakeIp()
        {
            SteamNetworkingSockets.OnFakeIPResult += OnFakeIpResult;

            if (SteamNetworkingSockets.RequestFakeIP(numFakePorts: 1))
                _fakeIpDeadlineTimestamp = Stopwatch.GetTimestamp() + FakeIpTimeoutSeconds * Stopwatch.Frequency;
            else
                ReadFakeIp();
        }

        /// <summary>
        /// Reads back the fake address Steam holds for this process, for a request that raised nothing.
        /// </summary>
        private void ReadFakeIp()
        {
            _fakeIpDeadlineTimestamp = UnsetTimestamp;

            Result result = SteamNetworkingSockets.GetFakeIP(FakePortIndex, out NetAddress netAddress);

            // A game server's grant waits for its logon, which can take longer than the timeout; the grant still raises the event.
            if (result is Result.OK)
                ApplyFakeIp(netAddress);
            else if (result is Result.Busy)
                Logger<FacepunchServerSocket>.LogWarning($"Steam has not answered the fake address request after [{FakeIpTimeoutSeconds}] seconds. The address is applied when Steam grants it.");
            else
                Logger<FacepunchServerSocket>.LogError($"Steam did not grant a fake address: [{result}]. Clients cannot reach this server by one.");
        }

        /// <summary>
        /// Records and logs the fake address Steam granted this server.
        /// </summary>
        /// <param name="netAddress">The address.</param>
        /// <remarks>Formatting an address is a native call, so this runs only while Steam is up.</remarks>
        private void ApplyFakeIp(NetAddress netAddress)
        {
            _fakeIpDeadlineTimestamp = UnsetTimestamp;
            _facepunchSockets.ServerFakeAddress = netAddress.ToString();

            Logger<FacepunchServerSocket>.LogInformation($"Steam granted this server the fake address [{_facepunchSockets.ServerFakeAddress}].");
        }

        /// <summary>
        /// Drops every client, closes the listen socket, and walks the server's state down.
        /// </summary>
        /// <returns>The result of the stop.</returns>
        private ConnectionStateChangeResult Stop()
        {
            if (!IsListening)
                return ConnectionStateChangeResult.AlreadyInState;

            // A host's own client loses its server with it, as it would over a socket.
            if (_connectionsBySteamConnection.ContainsKey(LoopbackSteamConnection))
                _facepunchSockets.ClientSocket.Stop();

            List<Connection> departingConnections = new(_steamConnectionsByConnection.Keys);

            foreach (Connection departingConnection in departingConnections)
                DisconnectRemoteClient(departingConnection, isLingering: false);

            ReleaseListenSocket();
            FacepunchSockets.ChangeLocalStateToDisconnected(TransportManager, Invoker.Server, Connection);

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Closes one remote client's link and tells the engine it is gone.
        /// </summary>
        /// <param name="connection">The client to drop.</param>
        /// <param name="isLingering">True to keep the Steam connection open until its queued reliable data is acknowledged.</param>
        /// <returns>The result of the disconnect.</returns>
        private ConnectionStateChangeResult DisconnectRemoteClient(Connection connection, bool isLingering)
        {
            if (!IsListening)
                return ConnectionStateChangeResult.InvalidSocketState;

            if (connection is null || !_steamConnectionsByConnection.Remove(connection, out SteamConnection steamConnection))
                return ConnectionStateChangeResult.InvalidConnection;

            _connectionsBySteamConnection.Remove(steamConnection);
            _departedSteamConnections.Remove(steamConnection);
            _serverLinks.Remove(steamConnection);

            if (steamConnection == LoopbackSteamConnection)
            {
                ClearCollection(IncomingPackets);

                // The host's own client is told its server dropped it, once what it was already sent has dispatched.
                _facepunchSockets.ClientSocket.OnLoopbackDropped();

                return NotifyRemoteDisconnected(connection, TakeLoopbackIdBySocket());
            }

            if (!FacepunchSockets.IsSteamShutDown())
            {
                // A kick's reason has to reach Steam before the close, or the linger has nothing to deliver.
                if (isLingering)
                    _sendQueue.ReleaseAllQueuedReliableSends(steamConnection);

                FlushQueuedMessages();
                steamConnection.Close(isLingering, FacepunchMessages.ApplicationCloseReason, "Disconnected by the server.");
            }

            _sendQueue.Discard(steamConnection);
            FacepunchMessages.DiscardSplitPacket(_splitPackets, steamConnection);

            return NotifyRemoteDisconnected(connection, connection.IdBySocket);
        }

        /// <summary>
        /// Drops the clients Steam closed during the previous pass, applies queued changes, then drains every admitted client's messages.
        /// </summary>
        /// <param name="receivedPackets">The list to append received packets to.</param>
        private void ReceiveServerPackets(List<IncomingPacket> receivedPackets)
        {
            // Taken off the end before each drop, so a departure handler that drops another entry leaves the drain intact.
            while (_departedSteamConnections.Count > 0)
            {
                int lastIndex = _departedSteamConnections.Count - 1;
                SteamConnection departedSteamConnection = _departedSteamConnections[lastIndex];
                _departedSteamConnections.RemoveAt(lastIndex);

                DropDepartedClient(departedSteamConnection);
            }

            // A departure handler that shut the transport down has returned this socket.
            if (TransportManager is null)
                return;

            // A game that shut Steam down while the server ran has taken every link with it, so the server stops rather than calling into it.
            if (IsListening && FacepunchSockets.IsSteamShutDown())
            {
                Logger<FacepunchServerSocket>.LogWarning("Steam was shut down while the server was running, so the server stopped.");
                Stop();

                return;
            }

            while (_fakeIpResults.TryDequeue(out NetAddress netAddress))
                ApplyFakeIp(netAddress);

            // Facepunch raises nothing when a request fails, so there is no edge to hear; this only compares two numbers until it expires.
            if (_fakeIpDeadlineTimestamp is not UnsetTimestamp && Stopwatch.GetTimestamp() >= _fakeIpDeadlineTimestamp)
                ReadFakeIp();

            while (TransportManager is not null && _statusChanges.TryDequeue(out FacepunchStatusChange statusChange))
                ApplyStatusChange(statusChange);

            if (TransportManager is null)
                return;

            while (IncomingPackets.TryDequeue(out IncomingPacket incomingPacket))
                receivedPackets.Add(incomingPacket);

            if (!IsListening)
                return;

            _currentReceivedPackets = receivedPackets;

            if (_isPerConnectionReceive)
            {
                foreach (KeyValuePair<SteamConnection, FacepunchServerLink> serverLink in _serverLinks)
                    serverLink.Value.Receive(FacepunchMessages.MaximumMessagesPerReceive, receiveToEnd: true);
            }
            else
            {
                _socketManager.Receive(FacepunchMessages.MaximumMessagesPerReceive, receiveToEnd: true);
            }

            _currentReceivedPackets = null;

            DisconnectOverLimitClients();
        }

        /// <summary>
        /// Disconnects the clients that sent a packet past <see cref="FacepunchSockets.MaximumClientPacketBytes"/> during the receive pass.
        /// </summary>
        private void DisconnectOverLimitClients()
        {
            while (TransportManager is not null && _overLimitSteamConnections.Count > 0)
            {
                int lastIndex = _overLimitSteamConnections.Count - 1;
                SteamConnection overLimitSteamConnection = _overLimitSteamConnections[lastIndex];
                _overLimitSteamConnections.RemoveAt(lastIndex);

                if (!_connectionsBySteamConnection.TryGetValue(overLimitSteamConnection, out Connection connection))
                    continue;

                Logger<FacepunchServerSocket>.LogWarning($"Steam client [{connection.RemoteAddress}] sent a packet larger than [{_facepunchSockets.MaximumClientPacketBytes}] bytes, so it was disconnected.");
                DisconnectRemoteClient(connection, isLingering: false);
            }
        }

        /// <summary>
        /// Accepts, admits or drops a client as Facepunch reported it.
        /// </summary>
        /// <param name="statusChange">The change.</param>
        private void ApplyStatusChange(FacepunchStatusChange statusChange)
        {
            SteamConnection steamConnection = statusChange.SteamConnection;

            switch (statusChange.State)
            {
                case ConnectionState.Connecting:
                    AcceptOrRefuse(steamConnection, statusChange.SteamId);
                    break;
                case ConnectionState.Connected:
                    if (!TryAdmitClient(steamConnection, statusChange.SteamId))
                        steamConnection.Close(linger: false, FacepunchMessages.ApplicationCloseReason, "Refused by the server.");

                    break;
                case ConnectionState.ClosedByPeer:
                case ConnectionState.ProblemDetectedLocally:
                    Logger<FacepunchServerSocket>.LogInformation($"A client closed: [{statusChange.EndReason}].");

                    // Steam keeps a closed connection's handle until it is closed locally too; an admitted client keeps it one pass longer.
                    if (!_connectionsBySteamConnection.ContainsKey(steamConnection))
                        steamConnection.Close(linger: false, FacepunchMessages.ApplicationCloseReason, debugString: null);
                    else if (!_departedSteamConnections.Contains(steamConnection))
                        _departedSteamConnections.Add(steamConnection);

                    break;
            }
        }

        /// <summary>
        /// Accepts a client asking to connect, unless the server is full or <see cref="FacepunchSockets.ConnectingClientFilter"/> refuses it.
        /// </summary>
        /// <param name="steamConnection">The Steam connection asking.</param>
        /// <param name="steamId">Who is asking, or <see cref="FacepunchSockets.UnsetSteamId"/> when it has no SteamID.</param>
        private void AcceptOrRefuse(SteamConnection steamConnection, ulong steamId)
        {
            uint maximumConnections = _facepunchSockets.ServerConfiguration.MaximumConnections;

            if (maximumConnections is not ServerConfiguration.UnsetMaximumConnections && _steamConnectionsByConnection.Count >= maximumConnections)
            {
                steamConnection.Close(linger: false, FacepunchMessages.ApplicationCloseReason, "The server is full.");

                return;
            }

            FacepunchSockets.ConnectingClientFilterHandler connectingClientFilter = _facepunchSockets.ConnectingClientFilter;

            if (connectingClientFilter is not null && !connectingClientFilter(steamId))
            {
                steamConnection.Close(linger: false, FacepunchMessages.ApplicationCloseReason, "Refused by the server.");

                return;
            }

            Result result = steamConnection.Accept();

            if (result is Result.OK)
                FacepunchSockets.ConfigureLanes(steamConnection);
            else
                Logger<FacepunchServerSocket>.LogError($"Steam could not accept a client: [{result}].");
        }

        /// <summary>
        /// Registers a client with the engine and starts receiving its messages.
        /// </summary>
        /// <param name="steamConnection">The Steam connection carrying it, or <see cref="LoopbackSteamConnection"/> for this peer's own client.</param>
        /// <param name="steamId">Who it is, as Steam authenticated it.</param>
        /// <returns><see langword="true"/> when the engine admitted the client.</returns>
        /// <remarks>Both maps are filled before the engine hears of the client, because admitting it runs its authentication synchronously, and the reply that admits it is sent from there.</remarks>
        private bool TryAdmitClient(SteamConnection steamConnection, ulong steamId)
        {
            if (!_idBySocketPool.EnsureRent(out uint idBySocket))
            {
                Logger<FacepunchServerSocket>.LogWarning($"Steam client [{steamId}] was refused: the server has no socket id to give it.");

                return false;
            }

            bool isLoopback = steamConnection == LoopbackSteamConnection;
            Connection connection;

            if (isLoopback)
            {
                connection = _facepunchSockets.ClientSocket.Connection;
                connection.SetRemoteAddress(steamId.ToString());
                _loopbackIdBySocket = idBySocket;
            }
            else
            {
                connection = ResettableObjectPool<Connection>.Rent();
                connection.Initialize(idBySocket, isEmulated: false, Transport, remoteAddress: steamId.ToString());

                if (_isPerConnectionReceive)
                    _serverLinks[steamConnection] = new(this, steamConnection);
            }

            _connectionsBySteamConnection[steamConnection] = connection;
            _steamConnectionsByConnection[connection] = steamConnection;

            ConnectionStateChangeResult connectionStateChangeResult = TransportManager.SetRemoteConnectionStateAsConnected(idBySocket, connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success)
                return true;

            Logger<FacepunchServerSocket>.LogError($"Steam client [{steamId}] could not be registered: [{connectionStateChangeResult}].");

            _connectionsBySteamConnection.Remove(steamConnection);
            _steamConnectionsByConnection.Remove(connection);
            _serverLinks.Remove(steamConnection);
            _idBySocketPool.ReturnUnsafe(idBySocket);

            // Borrowed from the client socket, which alone returns it to the pool, so it is released rather than pooled.
            if (isLoopback)
            {
                _loopbackIdBySocket = Connection.UnsetId;
                connection.ReleaseServerRole();
            }
            else
            {
                ResettableObjectPool<Connection>.Return(connection);
            }

            return false;
        }

        /// <summary>
        /// Takes the socket id this peer's own client was admitted with.
        /// </summary>
        /// <returns>The id, or <see cref="Connection.UnsetId"/> when it was not paired.</returns>
        private uint TakeLoopbackIdBySocket()
        {
            uint idBySocket = _loopbackIdBySocket;
            _loopbackIdBySocket = Connection.UnsetId;

            return idBySocket;
        }

        /// <summary>
        /// Closes the listen socket and stops hearing from it.
        /// </summary>
        /// <remarks>Facepunch reports a locally closed connection later; without an interface that report goes nowhere.</remarks>
        private void ReleaseListenSocket()
        {
            SteamNetworkingSockets.OnFakeIPResult -= OnFakeIpResult;
            _fakeIpDeadlineTimestamp = UnsetTimestamp;

            if (_socketManager is not null)
            {
                _socketManager.Interface = null;
                _socketManager.Close();
            }

            _socketManager = null;

            ClearCollection(IncomingPackets);
        }

        /// <summary>
        /// Tells the engine a remote client has gone, and returns its socket id once the engine has let it go.
        /// </summary>
        /// <param name="connection">The departed client.</param>
        /// <param name="idBySocket">The id the client was admitted with, read before the engine may recycle the Connection.</param>
        /// <returns>The result of the state change.</returns>
        private ConnectionStateChangeResult NotifyRemoteDisconnected(Connection connection, uint idBySocket)
        {
            ConnectionStateChangeResult connectionStateChangeResult = SetRemoteConnectionStateAsDisconnected(connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success && TransportManager is not null)
                _idBySocketPool.ReturnUnsafe(idBySocket);

            return connectionStateChangeResult;
        }

        /// <summary>
        /// Closes the handle of a client whose link Steam closed during the previous pass, and tells the engine it is gone.
        /// </summary>
        /// <param name="steamConnection">The departed client's Steam connection.</param>
        private void DropDepartedClient(SteamConnection steamConnection)
        {
            if (!FacepunchSockets.IsSteamShutDown())
                steamConnection.Close(linger: false, FacepunchMessages.ApplicationCloseReason, debugString: null);

            _sendQueue.Discard(steamConnection);
            FacepunchMessages.DiscardSplitPacket(_splitPackets, steamConnection);
            _serverLinks.Remove(steamConnection);

            if (!_connectionsBySteamConnection.Remove(steamConnection, out Connection connection))
                return;

            _steamConnectionsByConnection.Remove(connection);
            NotifyRemoteDisconnected(connection, connection.IdBySocket);
        }

        /// <summary>
        /// Unpairs this peer's own client when its link drops by any path other than this transport's own.
        /// </summary>
        /// <param name="connectionStateChange">The change.</param>
        /// <param name="localConnectionState">The state the role moved to.</param>
        /// <param name="isServerStarted">True when this peer's server is running.</param>
        private void OnLocalConnectionStateChanged(ConnectionStateChange connectionStateChange, LocalConnectionState localConnectionState, bool isServerStarted)
        {
            if (connectionStateChange.Invoker is not Invoker.Client || localConnectionState is not LocalConnectionState.Disconnected)
                return;

            if (!_connectionsBySteamConnection.TryGetValue(LoopbackSteamConnection, out Connection loopbackConnection) || !ReferenceEquals(connectionStateChange.Connection, loopbackConnection))
                return;

            _facepunchSockets.ClientSocket.OnLoopbackDropped();
            UnpairLoopbackClient();
        }

        /// <summary>
        /// Queues a fake address Steam granted.
        /// </summary>
        /// <param name="netAddress">The address.</param>
        private void OnFakeIpResult(NetAddress netAddress) => _fakeIpResults.Enqueue(netAddress);

        /// <inheritdoc/>
        void ISocketManager.OnConnecting(SteamConnection connection, ConnectionInfo info) => _statusChanges.Enqueue(new(connection, info));

        /// <inheritdoc/>
        void ISocketManager.OnConnected(SteamConnection connection, ConnectionInfo info) => _statusChanges.Enqueue(new(connection, info));

        /// <inheritdoc/>
        void ISocketManager.OnDisconnected(SteamConnection connection, ConnectionInfo info) => _statusChanges.Enqueue(new(connection, info));

        /// <inheritdoc/>
        void ISocketManager.OnMessage(SteamConnection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel) => OnClientMessage(connection, data, size);

        /// <inheritdoc/>
        /// <remarks>The socket is returned only after it has stopped. A remote client's Connection goes back to its pool through the TransportManager, so the maps are cleared rather than drained.</remarks>
        public override void OnReturn()
        {
            if (TransportManager is not null)
                LocalConnectionStateChanged -= OnLocalConnectionStateChanged;

            _idBySocketPool.OnReturn();

            _connectionsBySteamConnection.Clear();
            _steamConnectionsByConnection.Clear();
            _serverLinks.Clear();
            _departedSteamConnections.Clear();
            _refusedSteamConnections.Clear();
            _overLimitSteamConnections.Clear();
            FacepunchMessages.DiscardSplitPackets(_splitPackets);
            _sendQueue.Clear();
            _statusChanges.Clear();
            _fakeIpResults.Clear();
            ClearCollection(IncomingPackets);

            _socketManager = null;
            _currentReceivedPackets = null;
            _isPerConnectionReceive = false;
            _fakeIpDeadlineTimestamp = UnsetTimestamp;
            _loopbackIdBySocket = Connection.UnsetId;
            _facepunchSockets = null;

            base.OnReturn();
        }
    }
}
