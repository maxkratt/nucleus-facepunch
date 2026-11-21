using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Client;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using Connection = Nucleus.Connections.Connection;

namespace Nucleus.Integrations.Facepunch.Samples.QuickMatch
{
    /// <summary>
    /// Starts the match in the game scene: the chosen host serves and names itself the lobby's game server, and every other member
    /// connects to that server, then leaves the lobby once it is in.
    /// </summary>
    /// <remarks>
    /// The lobby's <see cref="QuickMatchSession.HostKey"/> says who hosts, because the owner can change while members are loading. A
    /// lobby whose owner is no longer that host has lost its server, so a member that sees this returns to the menu.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/QuickMatch/README.md")]
    public class QuickMatchGame : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The transport manager the match starts and stops.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Unity Transport Manager.")]
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The Facepunch transport, which needs Ticket Authentication on for the member check to run.
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
        /// The lobby this match came from, or <see langword="null"/> once this peer has left it.
        /// </summary>
        private Lobby? _lobby;
        /// <summary>
        /// The SteamID chosen to host this match.
        /// </summary>
        private ulong _hostSteamId;
        /// <summary>
        /// Whether this peer hosts the match.
        /// </summary>
        private bool _isHost;
        /// <summary>
        /// Whether this host has named itself the lobby's game server.
        /// </summary>
        private bool _isGameServerNamed;
        /// <summary>
        /// Whether this member has started its client.
        /// </summary>
        private bool _hasStartedClient;
        /// <summary>
        /// Whether this peer is already on its way back to the menu, since leaving stops the client that also reports it.
        /// </summary>
        private bool _isReturning;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Starting the match.";
        /// <summary>
        /// The transport manager whose state changes tell a member that its client stopped.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The client manager that reports this peer being admitted.
        /// </summary>
        private ClientManager _clientManager;

        /// <summary>
        /// Sets the member check before the server can listen, then registers the subscriptions.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _facepunchTransport == null || NucleusUnity.BoundCoreManager is null || !_steamBootstrap.IsSteamInitialized)
            {
                Debug.LogError($"[{nameof(QuickMatchGame)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(FacepunchTransport)}] set, a bound CoreManager and Steam running.");
                enabled = false;

                return;
            }

            if (QuickMatchSession.LobbySteamId != QuickMatchSession.UnsetLobbySteamId)
                _lobby = new Lobby(QuickMatchSession.LobbySteamId);

            _facepunchTransport.ClientAdmissionFilter = OnClientAdmission;

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _clientManager = NucleusUnity.BoundCoreManager.ClientManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            _clientManager.LocalClientAuthenticated += OnLocalClientAuthenticated;
            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyGameCreated += OnLobbyGameCreated;
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberChanged;
        }

        /// <summary>
        /// Hosts or joins, once every component in the scene has woken.
        /// </summary>
        private void Start()
        {
            if (_lobby is not Lobby lobby)
            {
                ReturnToMenu("The game scene was opened without a match.");

                return;
            }

            if (!ulong.TryParse(lobby.GetData(QuickMatchSession.HostKey), out _hostSteamId))
            {
                ReturnToMenu("The lobby names no host.");

                return;
            }

            _isHost = _hostSteamId == SteamClient.SteamId.Value;

            if (_isHost)
                StartHost(lobby);
            else
                TryStartClientFromLobby(lobby);
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 360f, "Quick Match");
            GUILayout.Label(_statusText);

            if (GUILayout.Button("Leave match"))
                ReturnToMenu("Left the match.");

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Starts the server, then names this peer the lobby's game server, so nobody dials a server that is not listening.
        /// </summary>
        /// <param name="lobby">The lobby this match came from.</param>
        private async void StartHost(Lobby lobby)
        {
            _statusText = "Starting the server.";

            await _unityTransportManager.StartHostAsync();

            // The user pressed Leave, or the scene closed, while the server was starting.
            if (this == null || _isReturning)
                return;

            lobby.SetGameServer(SteamClient.SteamId);
            _isGameServerNamed = true;
            _statusText = "Hosting the match.";

            LeaveLobbyIfAlone();
        }

        /// <summary>
        /// Reads the lobby's game server and owner, connects when the server is ready, and returns to the menu when the host has left.
        /// </summary>
        /// <param name="lobby">The lobby this match came from.</param>
        /// <remarks>A member that loads after the host named the server has already missed <see cref="OnLobbyGameCreated"/>, so this reads it.</remarks>
        private void TryStartClientFromLobby(Lobby lobby)
        {
            if (lobby.Owner.Id.Value != _hostSteamId)
            {
                ReturnToMenu("The host left before the match started.");

                return;
            }

            uint serverIp = 0;
            ushort serverPort = 0;
            SteamId serverSteamId = default;

            if (lobby.GetGameServer(ref serverIp, ref serverPort, ref serverSteamId))
                StartClient(serverSteamId);
            else
                _statusText = "Waiting for the host's server.";
        }

        /// <summary>
        /// Starts this member's client toward the game server, unless it already started.
        /// </summary>
        /// <param name="serverSteamId">The game server's SteamID.</param>
        private async void StartClient(SteamId serverSteamId)
        {
            if (_hasStartedClient)
                return;

            _hasStartedClient = true;
            _facepunchTransport.RemoteSteamId = serverSteamId.Value;
            _statusText = "Connecting to the host.";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Leaves the lobby, if this peer is still in it.
        /// </summary>
        private void LeaveLobby()
        {
            _lobby?.Leave();
            _lobby = null;
        }

        /// <summary>
        /// Ends the lobby once this host is its last member, which is the only way a Steam lobby closes.
        /// </summary>
        private void LeaveLobbyIfAlone()
        {
            if (_isGameServerNamed && _lobby is Lobby lobby && lobby.MemberCount == 1)
                LeaveLobby();
        }

        /// <summary>
        /// Leaves the lobby, stops every role and loads the menu.
        /// </summary>
        /// <param name="reason">Why, for the log.</param>
        private void ReturnToMenu(string reason)
        {
            if (_isReturning)
                return;

            _isReturning = true;
            Debug.Log($"[{nameof(QuickMatchGame)}] is returning to the menu: {reason}");

            LeaveLobby();
            _unityTransportManager.ShutdownAll();
            SceneManager.LoadScene(QuickMatchSession.MenuSceneName);
        }

        /// <summary>
        /// Whether a lobby Steam reported on is the one this peer is in.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <returns><see langword="true"/> when this peer is in <paramref name="lobby"/>.</returns>
        private bool IsCurrentLobby(Lobby lobby) => _lobby is Lobby currentLobby && currentLobby.Id.Value == lobby.Id.Value;

        /// <summary>
        /// Admits only a member of the lobby.
        /// </summary>
        /// <param name="connection">The connecting client.</param>
        /// <param name="steamId">The SteamID its link was authenticated as.</param>
        /// <returns><see langword="true"/> when the client is in the lobby.</returns>
        /// <remarks>A member stays in the lobby until it is authenticated, so a real member is always found here.</remarks>
        private bool OnClientAdmission(Connection connection, ulong steamId)
        {
            if (_lobby is not Lobby lobby)
                return false;

            foreach (Friend member in lobby.Members)
            {
                if (member.Id.Value == steamId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Connects a waiting member once the lobby names its game server.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <param name="serverIp">The game server's IPv4 address, unused, since the host names itself by SteamID.</param>
        /// <param name="serverPort">The game server's port, unused for the same reason.</param>
        /// <param name="serverSteamId">The game server's SteamID.</param>
        private void OnLobbyGameCreated(Lobby lobby, uint serverIp, ushort serverPort, SteamId serverSteamId)
        {
            if (!_isHost && IsCurrentLobby(lobby))
                StartClient(serverSteamId);
        }

        /// <summary>
        /// Lets a host close the lobby when it is alone, and sends a member still waiting back to the menu if the host left.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <param name="friend">The member who entered or left.</param>
        private void OnLobbyMemberChanged(Lobby lobby, Friend friend)
        {
            if (!IsCurrentLobby(lobby))
                return;

            if (_isHost)
                LeaveLobbyIfAlone();
            else if (!_hasStartedClient && lobby.Owner.Id.Value != _hostSteamId)
                ReturnToMenu("The host left before the match started.");
        }

        /// <summary>
        /// Leaves the lobby once this member is in the match.
        /// </summary>
        /// <param name="localClientConnection">This peer's connection.</param>
        private void OnLocalClientAuthenticated(Connection localClientConnection)
        {
            if (_isHost)
                return;

            _statusText = "In the match.";
            LeaveLobby();
        }

        /// <summary>
        /// Returns a member to the menu once its client stops or fails to connect.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (_isHost || connectionStateChange.Invoker is not Invoker.Client)
                return;

            if (connectionStateChange.Connection.LocalState is LocalConnectionState.Disconnected)
                ReturnToMenu("The connection to the host ended.");
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted() => LeaveLobby();

        /// <summary>
        /// Drops the subscriptions.
        /// </summary>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _clientManager.LocalClientAuthenticated -= OnLocalClientAuthenticated;
            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyGameCreated -= OnLobbyGameCreated;
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberChanged;
        }
    }
}
