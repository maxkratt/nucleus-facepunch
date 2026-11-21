using System;
using System.Collections.Generic;
using System.IO;
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Messages;
using Nucleus.Managers.Server;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.BanList
{
    /// <summary>
    /// The Ban List panel: a host kicks, bans or blacklists connected players and lifts those entries, and a refused client reads why.
    /// </summary>
    /// <remarks>
    /// A ban lasts until the server stops. A blacklist entry is a SteamID on its own line of a text file, so an admin can edit it by hand,
    /// and the file is read again whenever hosting starts. Both are checked by <see cref="FacepunchTransport.ClientAdmissionFilter"/>,
    /// which runs before the client's ticket is waited for.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/BanList/README.md")]
    public class BanListPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The transport manager the panel starts and stops.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Unity Transport Manager.")]
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The Steam transport, which needs Ticket Authentication on for the filter to run.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Facepunch Transport, set to SteamID addressing with Ticket Authentication on.")]
        private FacepunchTransport _facepunchTransport;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// Whether this peer is hosting.
        /// </summary>
        private bool _isHosting;
        /// <summary>
        /// Whether this peer has started a client that has not stopped yet.
        /// </summary>
        private bool _isJoining;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Not connected.";
        /// <summary>
        /// The reason the server last gave this client, or <see langword="null"/> when it gave none.
        /// </summary>
        private string _receivedReason;
        /// <summary>
        /// The host's SteamID typed into the panel.
        /// </summary>
        private string _hostSteamIdText = string.Empty;
        /// <summary>
        /// The full path of the blacklist file.
        /// </summary>
        private string _blacklistPath;
        /// <summary>
        /// How far the panel's lists are scrolled.
        /// </summary>
        private Vector2 _scrollPosition;
        /// <summary>
        /// The players connected to this host, its own client excluded.
        /// </summary>
        private readonly List<ConnectedPlayer> _connectedPlayers = new();
        /// <summary>
        /// The SteamIDs banned until the server stops.
        /// </summary>
        private readonly HashSet<ulong> _bannedSteamIds = new();
        /// <summary>
        /// The SteamIDs in the blacklist file.
        /// </summary>
        private readonly HashSet<ulong> _blacklistedSteamIds = new();
        /// <summary>
        /// The text each banned or blacklisted SteamID is listed under, by SteamID.
        /// </summary>
        private readonly Dictionary<ulong, string> _listedPlayerTextsBySteamId = new();
        /// <summary>
        /// The server manager whose events maintain <see cref="_connectedPlayers"/> and which refuses clients.
        /// </summary>
        private ServerManager _serverManager;
        /// <summary>
        /// The transport manager whose state changes tell a client that it stopped.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The message manager the notice handler is registered with.
        /// </summary>
        private MessageManager _messageManager;

        /// <summary>
        /// The name of the blacklist file under <see cref="Application.persistentDataPath"/>.
        /// </summary>
        private const string BlacklistFileName = "FacepunchBanListBlacklist.txt";
        /// <summary>
        /// The reason a banned player is given.
        /// </summary>
        private const string BannedReason = "You are banned from this server until it restarts.";
        /// <summary>
        /// The reason a blacklisted player is given.
        /// </summary>
        private const string BlacklistedReason = "You are blacklisted on this server.";
        /// <summary>
        /// The reason a kicked player is given.
        /// </summary>
        private const string KickedReason = "You were kicked by the host.";
        /// <summary>
        /// The name Steam reports for a user whose name it has not fetched.
        /// </summary>
        private const string UnknownPersonaName = "[unknown]";

        /// <summary>
        /// Registers the filter before the server can listen, then the events and the notice handler.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _facepunchTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(BanListPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(FacepunchTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            if (!_steamBootstrap.IsSteamInitialized)
                return;

            _blacklistPath = Path.Combine(Application.persistentDataPath, BlacklistFileName);
            _facepunchTransport.ClientAdmissionFilter = OnClientAdmission;

            _serverManager = NucleusUnity.BoundCoreManager.ServerManager;
            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _messageManager = NucleusUnity.BoundCoreManager.MessageManager;
            _serverManager.ClientAuthenticated += OnClientAuthenticated;
            _serverManager.ClientDisconnecting += OnClientDisconnecting;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            _messageManager.RegisterMessageHandler<BanNotice>(OnBanNoticeReceived);
            SteamFriends.OnPersonaStateChange += OnPersonaStateChanged;
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 460f, "Ban List");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_isHosting)
                DrawHost();
            else if (_isJoining)
                DrawClient();
            else
                DrawStart();

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Draws Host and joining by a typed SteamID.
        /// </summary>
        private void DrawStart()
        {
            if (GUILayout.Button("Host"))
                Host();

            SampleGUI.Section("Join");
            _hostSteamIdText = SampleGUI.Field("Host SteamID", _hostSteamIdText);

            if (!GUILayout.Button("Join"))
                return;

            if (ulong.TryParse(_hostSteamIdText.Trim(), out ulong hostSteamId))
                Join(hostSteamId);
            else
                _statusText = "That is not a SteamID.";
        }

        /// <summary>
        /// Draws the connected players with their actions, the banned and blacklisted SteamIDs, and Stop.
        /// </summary>
        private void DrawHost()
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Copy my SteamID"))
                GUIUtility.systemCopyBuffer = _steamBootstrap.OwnSteamId.ToString();

            if (GUILayout.Button("Stop"))
                Stop();

            GUILayout.EndHorizontal();

            // A button must not change a list while it is being walked, so each click is applied once the lists are drawn.
            ConnectedPlayer kickedPlayer = null;
            ConnectedPlayer bannedPlayer = null;
            ConnectedPlayer blacklistedPlayer = null;
            ulong unbannedSteamId = FacepunchSockets.UnsetSteamId;
            ulong unlistedSteamId = FacepunchSockets.UnsetSteamId;

            _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);
            SampleGUI.Section($"Players {_connectedPlayers.Count}");

            foreach (ConnectedPlayer connectedPlayer in _connectedPlayers)
            {
                GUILayout.Label(connectedPlayer.Name);
                GUILayout.BeginHorizontal();

                if (GUILayout.Button("Kick"))
                    kickedPlayer = connectedPlayer;

                if (GUILayout.Button("Ban"))
                    bannedPlayer = connectedPlayer;

                if (GUILayout.Button("Blacklist"))
                    blacklistedPlayer = connectedPlayer;

                GUILayout.EndHorizontal();
            }

            SampleGUI.Section($"Banned until restart {_bannedSteamIds.Count}");

            foreach (ulong bannedSteamId in _bannedSteamIds)
            {
                if (SampleGUI.PlayerRow(avatar: null, _listedPlayerTextsBySteamId[bannedSteamId], buttonText: "Unban"))
                    unbannedSteamId = bannedSteamId;
            }

            SampleGUI.Section($"Blacklist {_blacklistedSteamIds.Count}");

            foreach (ulong blacklistedSteamId in _blacklistedSteamIds)
            {
                if (SampleGUI.PlayerRow(avatar: null, _listedPlayerTextsBySteamId[blacklistedSteamId], buttonText: "Unban"))
                    unlistedSteamId = blacklistedSteamId;
            }

            GUILayout.EndScrollView();

            if (kickedPlayer is not null)
                kickedPlayer.Connection.Kick(new BanNotice { Reason = KickedReason });

            if (bannedPlayer is not null)
            {
                _bannedSteamIds.Add(bannedPlayer.SteamId);
                RequestListedPlayerText(bannedPlayer.SteamId);
                bannedPlayer.Connection.Kick(new BanNotice { Reason = BannedReason });
            }

            if (blacklistedPlayer is not null)
            {
                _blacklistedSteamIds.Add(blacklistedPlayer.SteamId);
                RequestListedPlayerText(blacklistedPlayer.SteamId);
                WriteBlacklist();
                blacklistedPlayer.Connection.Kick(new BanNotice { Reason = BlacklistedReason });
            }

            if (unbannedSteamId is not FacepunchSockets.UnsetSteamId)
                _bannedSteamIds.Remove(unbannedSteamId);

            if (unlistedSteamId is not FacepunchSockets.UnsetSteamId)
            {
                _blacklistedSteamIds.Remove(unlistedSteamId);
                WriteBlacklist();
            }
        }

        /// <summary>
        /// Draws Leave.
        /// </summary>
        private void DrawClient()
        {
            if (GUILayout.Button("Leave"))
                Stop();
        }

        /// <summary>
        /// Starts this peer as a host with an empty ban list and the blacklist as the file now holds it.
        /// </summary>
        private async void Host()
        {
            _isHosting = true;
            _bannedSteamIds.Clear();
            ReadBlacklist();
            _statusText = "Hosting.";

            await _unityTransportManager.StartHostAsync();
        }

        /// <summary>
        /// Starts this peer as a client of the typed host.
        /// </summary>
        /// <param name="hostSteamId">The host's SteamID.</param>
        private async void Join(ulong hostSteamId)
        {
            _isJoining = true;
            _receivedReason = null;
            _facepunchTransport.RemoteSteamId = hostSteamId;
            _statusText = $"Connecting to [{hostSteamId}].";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Stops every role this peer runs.
        /// </summary>
        private void Stop()
        {
            // Cleared before the shutdown, whose client stop would otherwise report a dropped session.
            _isHosting = false;
            _isJoining = false;
            _connectedPlayers.Clear();

            _unityTransportManager.ShutdownAll();

            _statusText = "Not connected.";
        }

        /// <summary>
        /// Replaces the blacklist with the file's SteamIDs, skipping any line that is not one.
        /// </summary>
        private void ReadBlacklist()
        {
            _blacklistedSteamIds.Clear();

            if (!File.Exists(_blacklistPath))
                return;

            try
            {
                foreach (string line in File.ReadAllLines(_blacklistPath))
                {
                    if (ulong.TryParse(line.Trim(), out ulong steamId) && _blacklistedSteamIds.Add(steamId))
                        RequestListedPlayerText(steamId);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.LogError($"[{nameof(BanListPanel)}] could not read the blacklist at [{_blacklistPath}]: [{exception.Message}].");
            }
        }

        /// <summary>
        /// Writes the blacklist to its file, one SteamID per line.
        /// </summary>
        private void WriteBlacklist()
        {
            List<string> lines = new(_blacklistedSteamIds.Count);

            foreach (ulong steamId in _blacklistedSteamIds)
                lines.Add(steamId.ToString());

            try
            {
                File.WriteAllLines(_blacklistPath, lines);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.LogError($"[{nameof(BanListPanel)}] could not write the blacklist at [{_blacklistPath}]: [{exception.Message}].");
            }
        }

        /// <summary>
        /// Asks Steam for a listed player's name and records what to list them under until it arrives.
        /// </summary>
        /// <param name="steamId">The listed SteamID.</param>
        private void RequestListedPlayerText(ulong steamId)
        {
            SteamFriends.RequestUserInformation(steamId, nameonly: true);
            StoreListedPlayerText(steamId);
        }

        /// <summary>
        /// Records the text a listed player is shown under: their name and SteamID, or the SteamID alone while Steam does not know the name.
        /// </summary>
        /// <param name="steamId">The listed SteamID.</param>
        private void StoreListedPlayerText(ulong steamId)
        {
            string personaName = new Friend(steamId).Name;

            _listedPlayerTextsBySteamId[steamId] = string.IsNullOrEmpty(personaName) || personaName == UnknownPersonaName ? steamId.ToString() : $"{personaName} ({steamId})";
        }

        /// <summary>
        /// Refuses a banned or blacklisted SteamID with its reason.
        /// </summary>
        /// <param name="connection">The connecting client.</param>
        /// <param name="steamId">The SteamID its link was authenticated as.</param>
        /// <returns><see langword="true"/> when the client may continue to the ticket check.</returns>
        private bool OnClientAdmission(Connection connection, ulong steamId)
        {
            if (_bannedSteamIds.Contains(steamId))
            {
                _serverManager.DenyClient(connection, new BanNotice { Reason = BannedReason });

                return false;
            }

            if (_blacklistedSteamIds.Contains(steamId))
            {
                _serverManager.DenyClient(connection, new BanNotice { Reason = BlacklistedReason });

                return false;
            }

            return true;
        }

        /// <summary>
        /// Lists an admitted player, unless it is this host's own client.
        /// </summary>
        /// <param name="connection">The admitted client.</param>
        private void OnClientAuthenticated(Connection connection)
        {
            // The ticket authenticator approves a client with its SteamID as its identity.
            if (!ulong.TryParse(connection.Identity, out ulong steamId) || steamId == _steamBootstrap.OwnSteamId)
                return;

            SteamFriends.RequestUserInformation(steamId, nameonly: true);
            _connectedPlayers.Add(new(connection, steamId, new Friend(steamId).Name));
        }

        /// <summary>
        /// Removes a departing player from the list.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void OnClientDisconnecting(Connection connection)
        {
            for (int i = _connectedPlayers.Count - 1; i >= 0; i--)
            {
                if (_connectedPlayers[i].Connection == connection)
                    _connectedPlayers.RemoveAt(i);
            }
        }

        /// <summary>
        /// Shows the reason the server gave this client.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="banNotice">The notice.</param>
        /// <remarks>A notice is unauthenticated, so anything a client sends a host is ignored.</remarks>
        private void OnBanNoticeReceived(in MessageContext messageContext, BanNotice banNotice)
        {
            if (!_isHosting)
                _receivedReason = banNotice.Reason;
        }

        /// <summary>
        /// Shows this client's progress, and returns it to the start buttons once it stops.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_isJoining || connectionStateChange.Invoker is not Invoker.Client)
                return;

            switch (connectionStateChange.Connection.LocalState)
            {
                case LocalConnectionState.Connected:
                    _statusText = "Connected to the host.";
                    break;
                case LocalConnectionState.Disconnected:
                    _isJoining = false;
                    _statusText = _receivedReason ?? "The connection ended.";

                    break;
            }
        }

        /// <summary>
        /// Rereads a listed player's name once Steam has it.
        /// </summary>
        /// <param name="friend">The user whose information changed.</param>
        private void OnPersonaStateChanged(Friend friend)
        {
            ulong steamId = friend.Id.Value;

            foreach (ConnectedPlayer connectedPlayer in _connectedPlayers)
            {
                if (connectedPlayer.SteamId == steamId)
                    connectedPlayer.Name = friend.Name;
            }

            if (_listedPlayerTextsBySteamId.ContainsKey(steamId))
                StoreListedPlayerText(steamId);
        }

        /// <summary>
        /// Drops the subscriptions.
        /// </summary>
        private void OnDestroy()
        {
            if (_serverManager is null)
                return;

            _serverManager.ClientAuthenticated -= OnClientAuthenticated;
            _serverManager.ClientDisconnecting -= OnClientDisconnecting;
            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _messageManager.UnregisterMessageHandler<BanNotice>(OnBanNoticeReceived);
            SteamFriends.OnPersonaStateChange -= OnPersonaStateChanged;
        }

        /// <summary>
        /// A player connected to this host.
        /// </summary>
        private sealed class ConnectedPlayer
        {
            /// <summary>
            /// The player's Steam display name, as last read.
            /// </summary>
            public string Name;
            /// <summary>
            /// The player's connection.
            /// </summary>
            public readonly Connection Connection;
            /// <summary>
            /// The player's SteamID.
            /// </summary>
            public readonly ulong SteamId;

            /// <summary>
            /// Creates the entry.
            /// </summary>
            /// <param name="connection">The player's connection.</param>
            /// <param name="steamId">The player's SteamID.</param>
            /// <param name="name">The player's Steam display name.</param>
            public ConnectedPlayer(Connection connection, ulong steamId, string name)
            {
                Connection = connection;
                SteamId = steamId;
                Name = name;
            }
        }
    }
}
