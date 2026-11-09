using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CodeBoost.Logging;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.NetworkLoop;
using Nucleus.Transports;
using Steamworks;
using NetIdentity = Steamworks.Data.NetIdentity;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// An <see cref="Managers.Server.IClientAuthenticator"/> that admits a client only once Steam has confirmed its auth session ticket
    /// belongs to the SteamID its <see cref="FacepunchSockets"/> link was authenticated as, and that it owns the game.
    /// </summary>
    /// <remarks>
    /// Both sides run it. The client sends its ticket as soon as its link is up; the server begins an auth session bound to the link's
    /// SteamID and approves the client once Steam answers. The host's own client, paired in process, is approved at once. Sessions open on
    /// the same interface Facepunch's networking uses: the user's when it is up, otherwise the game server's.
    /// </remarks>
    public class SteamTicketAuthenticator : Managers.Server.IClientAuthenticator, INetworkLoopStepCallback
    {
        /// <summary>
        /// The engine this authenticator is registered with, or <see langword="null"/> while it is not.
        /// </summary>
        private CoreManager _coreManager;
        /// <summary>
        /// The transport whose links this authenticator vouches for.
        /// </summary>
        private FacepunchSockets _facepunchSockets;
        /// <summary>
        /// The ticket this peer's client last sent, or <see langword="null"/> while it has none outstanding.
        /// </summary>
        private AuthTicket _ownTicket;
        /// <summary>
        /// Connecting clients whose ticket has not arrived yet, with the SteamID their link was authenticated as.
        /// </summary>
        private readonly Dictionary<Connection, ulong> _awaitingTicketSteamIdsByConnection = new();
        /// <summary>
        /// Clients with an open auth session, awaiting Steam's verdict or already approved, by Connection.
        /// </summary>
        private readonly Dictionary<Connection, ulong> _sessionSteamIdsByConnection = new();
        /// <summary>
        /// Clients with an open auth session, by SteamID, which is all a verdict names.
        /// </summary>
        private readonly Dictionary<ulong, Connection> _sessionConnectionsBySteamId = new();
        /// <summary>
        /// Steam's verdicts, applied on the loop thread.
        /// </summary>
        private readonly ConcurrentQueue<TicketVerdict> _ticketVerdicts = new();

        /// <summary>
        /// Registers this as the server's authenticator, and starts sending this peer's own ticket whenever its client links up.
        /// </summary>
        /// <param name="coreManager">The engine to register with.</param>
        /// <param name="facepunchSockets">The transport whose links are vouched for.</param>
        public void Initialize(CoreManager coreManager, FacepunchSockets facepunchSockets)
        {
            _coreManager = coreManager;
            _facepunchSockets = facepunchSockets;

            coreManager.ServerManager.SetAuthenticator(this);
            coreManager.ServerManager.ClientDisconnecting += OnClientDisconnecting;
            coreManager.TransportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            coreManager.MessageManager.RegisterMessageHandler<SteamAuthTicketMessage>(OnTicketReceived);
            coreManager.MessageManager.RegisterMessageHandler<SteamAuthRevokedNotice>(OnRevokedNoticeReceived);
            coreManager.NetworkLoopManager.RegisterNetworkLoopStepCallbacks(this);

            SteamUser.OnValidateAuthTicketResponse += OnTicketResponse;
            SteamServer.OnValidateAuthTicketResponse += OnTicketResponse;
        }

        /// <inheritdoc/>
        public void OnClientConnecting(Connection connection)
        {
            if (_facepunchSockets.IsOwnClientConnection(connection))
            {
                _coreManager.ServerManager.ApproveClient(connection, connection.RemoteAddress);

                return;
            }

            if (!ulong.TryParse(connection.RemoteAddress, out ulong steamId) || steamId == FacepunchSockets.UnsetSteamId)
            {
                Logger<SteamTicketAuthenticator>.LogWarning($"A client with no SteamID [{connection.RemoteAddress}] was refused.");
                _coreManager.ServerManager.DenyClient(connection);

                return;
            }

            _awaitingTicketSteamIdsByConnection[connection] = steamId;
        }

        /// <summary>
        /// Ends every auth session, cancels this peer's ticket, and restores the engine's default authenticator.
        /// </summary>
        public void Deinitialize()
        {
            if (_coreManager is null)
                return;

            List<Connection> sessionConnections = new(_sessionSteamIdsByConnection.Keys);

            foreach (Connection sessionConnection in sessionConnections)
                EndSession(sessionConnection);

            _awaitingTicketSteamIdsByConnection.Clear();
            CancelOwnTicket();

            _coreManager.ServerManager.SetAuthenticator(clientAuthenticator: null);
            _coreManager.ServerManager.ClientDisconnecting -= OnClientDisconnecting;
            _coreManager.TransportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _coreManager.MessageManager.UnregisterMessageHandler<SteamAuthTicketMessage>(OnTicketReceived);
            _coreManager.MessageManager.UnregisterMessageHandler<SteamAuthRevokedNotice>(OnRevokedNoticeReceived);
            _coreManager.NetworkLoopManager.UnregisterNetworkLoopStepCallbacks(this);

            SteamUser.OnValidateAuthTicketResponse -= OnTicketResponse;
            SteamServer.OnValidateAuthTicketResponse -= OnTicketResponse;

            _coreManager = null;
            _facepunchSockets = null;
        }

        /// <inheritdoc/>
        NetworkLoopSteps INetworkLoopStepCallback.GetNetworkLoopSteps() => NetworkLoopSteps.LateVariableUpdate;

        /// <summary>
        /// Applies Steam's queued verdicts.
        /// </summary>
        /// <param name="networkLoopStep">The step being performed.</param>
        /// <param name="stepDelta">Delta information about the step.</param>
        void INetworkLoopStepCallback.OnNetworkLoopStep(NetworkLoopSteps networkLoopStep, StepDelta stepDelta)
        {
            while (_ticketVerdicts.TryDequeue(out TicketVerdict ticketVerdict))
                ApplyTicketVerdict(ticketVerdict);
        }

        /// <summary>
        /// Checks a connecting client's ticket against the SteamID its link was authenticated as, then returns its pooled array.
        /// </summary>
        /// <param name="messageContext">The context the ticket arrived in.</param>
        /// <param name="steamAuthTicketMessage">The ticket.</param>
        private void OnTicketReceived(in MessageContext messageContext, SteamAuthTicketMessage steamAuthTicketMessage)
        {
            ArraySegment<byte> ticketSegment = steamAuthTicketMessage.Ticket;
            Connection connection = messageContext.SenderConnection;

            // A ticket from a client already past this step, or from one this authenticator never saw connect, is ignored.
            if (connection is not null && _awaitingTicketSteamIdsByConnection.Remove(connection, out ulong steamId))
                BeginSession(connection, steamId, ticketSegment);

            // Steam copies the ticket when the session begins, so the pooled array can go back now.
            if (messageContext.IsDeserialized && ticketSegment.Array is not null)
                TrackedArrayPool<byte>.Return(ticketSegment.Array);
        }

        /// <summary>
        /// Begins an auth session for a connecting client's ticket, bound to the SteamID its link was authenticated as.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">The SteamID its link was authenticated as.</param>
        /// <param name="ticketSegment">The ticket it sent, valid only for this call.</param>
        /// <remarks>Facepunch reads the whole array it is given, so the ticket is copied to its exact length.</remarks>
        private void BeginSession(Connection connection, ulong steamId, ArraySegment<byte> ticketSegment)
        {
            if (ticketSegment.Array is null || ticketSegment.Count == 0)
            {
                Deny(connection, steamId, "sent an empty ticket");

                return;
            }

            byte[] ticket = ticketSegment.ToArray();

            if (SteamClient.IsValid)
            {
                BeginAuthResult beginAuthResult = SteamUser.BeginAuthSession(ticket, steamId);

                if (beginAuthResult is not BeginAuthResult.OK)
                {
                    Deny(connection, steamId, $"sent a ticket Steam refused: [{beginAuthResult}]");

                    return;
                }
            }
            else if (!SteamServer.BeginAuthSession(ticket, steamId))
            {
                Deny(connection, steamId, "sent a ticket Steam refused");

                return;
            }

            _sessionSteamIdsByConnection[connection] = steamId;
            _sessionConnectionsBySteamId[steamId] = connection;
        }

        /// <summary>
        /// Logs the reason a server gave for kicking this client after Steam withdrew its approval.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="steamAuthRevokedNotice">The notice.</param>
        private void OnRevokedNoticeReceived(in MessageContext messageContext, SteamAuthRevokedNotice steamAuthRevokedNotice) =>
            Logger<SteamTicketAuthenticator>.LogWarning($"The server kicked this client because Steam withdrew its approval: [{steamAuthRevokedNotice.Response}].");

        /// <summary>
        /// Approves or refuses a client as Steam judged its ticket.
        /// </summary>
        /// <param name="ticketVerdict">Steam's verdict.</param>
        private void ApplyTicketVerdict(TicketVerdict ticketVerdict)
        {
            ulong steamId = ticketVerdict.SteamId;

            if (!_sessionConnectionsBySteamId.TryGetValue(steamId, out Connection connection))
                return;

            AuthResponse authResponse = ticketVerdict.Response;

            if (authResponse is AuthResponse.OK)
            {
                if (!connection.IsAuthenticated)
                    _coreManager.ServerManager.ApproveClient(connection, steamId.ToString());

                return;
            }

            // A verdict withdrawn after approval, such as a VAC ban mid-session, kicks the client and tells it why.
            if (connection.IsAuthenticated)
            {
                Logger<SteamTicketAuthenticator>.LogWarning($"Steam withdrew its approval of client [{steamId}]: [{authResponse}], so it was kicked.");
                connection.Kick(new SteamAuthRevokedNotice { Response = authResponse.ToString() });

                return;
            }

            EndSession(connection);
            Deny(connection, steamId, $"was refused by Steam: [{authResponse}]");
        }

        /// <summary>
        /// Sends this peer's ticket when its client links up to another peer's server, and cancels it when the link drops.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Invoker is not Invoker.Client)
                return;

            LocalConnectionState localConnectionState = connectionStateChange.Connection.LocalState;

            if (localConnectionState is LocalConnectionState.Disconnected)
                CancelOwnTicket();
            else if (localConnectionState is LocalConnectionState.Connected && !_coreManager.TransportManager.IsServerStarted)
                SendOwnTicket();
        }

        /// <summary>
        /// Ends a departing client's auth session, which Steam would otherwise keep open.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void OnClientDisconnecting(Connection connection)
        {
            _awaitingTicketSteamIdsByConnection.Remove(connection);
            EndSession(connection);
        }

        /// <summary>
        /// Writes this peer's ticket, bound to the server it is linked to, and sends it.
        /// </summary>
        private void SendOwnTicket()
        {
            CancelOwnTicket();

            if (!_coreManager.TransportManager.TryGetServerConnection(out Connection serverConnection))
                return;

            // The ticket is bound to the server's identity, so a server that is handed it cannot replay it to another.
            _facepunchSockets.TryGetServerIdentity(out NetIdentity serverIdentity);

            _ownTicket = SteamUser.GetAuthSessionTicket(serverIdentity);

            if (_ownTicket?.Data is null)
            {
                Logger<SteamTicketAuthenticator>.LogError("Steam did not issue an auth session ticket, so the server will refuse this client.");

                return;
            }

            serverConnection.SendMessage(Channel.Reliable, new SteamAuthTicketMessage { Ticket = new(_ownTicket.Data) });
        }

        /// <summary>
        /// Cancels this peer's outstanding ticket, so it can no longer be used.
        /// </summary>
        private void CancelOwnTicket()
        {
            if (_ownTicket is null)
                return;

            if (SteamClient.IsValid)
                _ownTicket.Cancel();

            _ownTicket = null;
        }

        /// <summary>
        /// Ends a client's auth session, if it has one.
        /// </summary>
        /// <param name="connection">The client.</param>
        private void EndSession(Connection connection)
        {
            if (!_sessionSteamIdsByConnection.Remove(connection, out ulong steamId))
                return;

            _sessionConnectionsBySteamId.Remove(steamId);

            if (SteamClient.IsValid)
                SteamUser.EndAuthSession(steamId);
            else if (SteamServer.IsValid)
                SteamServer.EndSession(steamId);
        }

        /// <summary>
        /// Refuses a connecting client and logs why.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">Its SteamID.</param>
        /// <param name="reason">Why, as the end of a sentence.</param>
        private void Deny(Connection connection, ulong steamId, string reason)
        {
            Logger<SteamTicketAuthenticator>.LogWarning($"Client [{steamId}] {reason}, so it was refused.");
            _coreManager.ServerManager.DenyClient(connection);
        }

        /// <summary>
        /// Queues one of Steam's verdicts.
        /// </summary>
        /// <param name="steamId">The account the ticket belongs to.</param>
        /// <param name="ownerSteamId">The account that owns the game.</param>
        /// <param name="authResponse">Steam's verdict.</param>
        private void OnTicketResponse(SteamId steamId, SteamId ownerSteamId, AuthResponse authResponse) => _ticketVerdicts.Enqueue(new(steamId.Value, authResponse));

        /// <summary>
        /// One of Steam's verdicts on a ticket.
        /// </summary>
        private readonly struct TicketVerdict
        {
            /// <summary>
            /// The account the ticket belongs to.
            /// </summary>
            public readonly ulong SteamId;
            /// <summary>
            /// Steam's verdict.
            /// </summary>
            public readonly AuthResponse Response;

            /// <summary>
            /// Creates a new instance.
            /// </summary>
            /// <param name="steamId">The account the ticket belongs to.</param>
            /// <param name="response">Steam's verdict.</param>
            public TicketVerdict(ulong steamId, AuthResponse response)
            {
                SteamId = steamId;
                Response = response;
            }
        }
    }
}
