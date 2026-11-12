using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.LobbyBrowser
{
    /// <summary>
    /// The Lobby Browser panel: a host opens a public lobby that publishes its name and its Steam ping location, and a player lists the
    /// public lobbies of this sample within a chosen distance, sees each one's estimated ping, and joins one.
    /// </summary>
    /// <remarks>
    /// The ping is estimated without sending anything to the host: Steam's relay network measures each peer's ping to its relays, the
    /// host publishes that as a ping location string in its lobby data, and <see cref="SteamNetworkingUtils.EstimatePingTo"/> combines
    /// it with this peer's own. Both sides need relay network access, which <see cref="SteamBootstrap"/> starts. A joined player
    /// connects to the lobby owner through <see cref="FacepunchTransport.ServerSteamId"/>, and leaves the lobby once its client stops.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/LobbyBrowser/README.md")]
    public class LobbyBrowserPanel : MonoBehaviour
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
        /// The Facepunch transport, which a joining player points at the lobby owner.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Facepunch Transport, set to SteamID addressing.")]
        private FacepunchTransport _facepunchTransport;
        /// <summary>
        /// The most players a hosted lobby holds, the host included.
        /// </summary>
        [SerializeField]
        [Tooltip("Most players a hosted lobby holds, the host included.")]
        private int _maximumLobbyMembers = 8;
        /// <summary>
        /// The most lobbies one search returns.
        /// </summary>
        [SerializeField]
        [Tooltip("Most lobbies one search returns.")]
        private int _maximumSearchResults = 20;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The lobby this peer is in, or <see langword="null"/> when it is in none.
        /// </summary>
        private Lobby? _lobby;
        /// <summary>
        /// The distance the next search covers.
        /// </summary>
        private LobbyDistance _lobbyDistance = LobbyDistance.Default;
        /// <summary>
        /// Whether this peer created the lobby it is in.
        /// </summary>
        private bool _isLobbyOwner;
        /// <summary>
        /// Whether this peer has hosted or begun joining since it last left, which swaps the browser for the lobby view.
        /// </summary>
        private bool _hasStarted;
        /// <summary>
        /// Whether this peer's client has connected since it last joined, so a dropped session reads differently from a failed connect.
        /// </summary>
        private bool _hasConnected;
        /// <summary>
        /// Counts the times this peer has left, so a host or join still awaiting Steam can tell it belongs to a session already left.
        /// </summary>
        private uint _leaveCount;
        /// <summary>
        /// Counts the searches this peer has started, so the answer to an older search does not replace a newer one's.
        /// </summary>
        private uint _searchCount;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Search for lobbies, or host one.";
        /// <summary>
        /// The name a hosted lobby is listed under.
        /// </summary>
        private string _lobbyNameText = string.Empty;
        /// <summary>
        /// The lobbies the last search found.
        /// </summary>
        private readonly List<LobbyListing> _lobbyListings = new();
        /// <summary>
        /// The transport manager whose state changes tell a joined player that its client stopped.
        /// </summary>
        private TransportManager _transportManager;

        /// <summary>
        /// The names of the <see cref="LobbyDistance"/> members, in their order, as the panel shows them.
        /// </summary>
        private static readonly string[] LobbyDistanceNames = { "Close", "Default", "Far", "Worldwide" };

        /// <summary>
        /// The lobby data key that marks a lobby as this sample's, so a search on a shared App ID such as 480 skips every other game's.
        /// </summary>
        private const string SampleKey = "nucleus_sample";
        /// <summary>
        /// The value stored under <see cref="SampleKey"/>.
        /// </summary>
        private const string SampleValue = "lobby_browser";
        /// <summary>
        /// The lobby data key the host's lobby name is stored under.
        /// </summary>
        private const string NameKey = "name";
        /// <summary>
        /// The lobby data key the host's ping location string is stored under.
        /// </summary>
        private const string PingLocationKey = "ping_location";
        /// <summary>
        /// The ping shown for a lobby whose ping could not be estimated.
        /// </summary>
        private const int UnsetPingMilliseconds = -1;

        /// <summary>
        /// Subscribes to Steam's lobby creation and to the transport.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _facepunchTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(LobbyBrowserPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(FacepunchTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            // The bootstrap wakes first, so Steam is up here or never will be.
            if (!_steamBootstrap.IsSteamInitialized)
            {
                enabled = false;

                return;
            }

            _lobbyNameText = $"{SteamClient.Name}'s lobby";

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyCreated += OnLobbyCreated;
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 464f, "Lobby Browser");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_hasStarted)
            {
                if (_lobby is Lobby lobby)
                    GUILayout.Label(_isLobbyOwner ? $"Hosting lobby {lobby.Id.Value}." : $"In lobby {lobby.Id.Value}.");

                if (GUILayout.Button("Leave"))
                    Leave("Left the lobby.");
            }
            else
            {
                DrawBrowser();
            }

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Draws the host controls, the distance filter, Search and the lobbies found.
        /// </summary>
        private void DrawBrowser()
        {
            GUILayout.Label("Lobby name");
            _lobbyNameText = GUILayout.TextField(_lobbyNameText);

            if (GUILayout.Button("Host public lobby"))
                Host();

            GUILayout.Space(8f);
            _lobbyDistance = (LobbyDistance)GUILayout.Toolbar((int)_lobbyDistance, LobbyDistanceNames);

            if (GUILayout.Button("Search"))
                Search();

            foreach (LobbyListing lobbyListing in _lobbyListings)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(lobbyListing.Name, GUILayout.Width(190f));
                GUILayout.Label($"{lobbyListing.MemberCount}/{lobbyListing.MemberLimit}", GUILayout.Width(50f));
                GUILayout.Label(lobbyListing.PingMilliseconds == UnsetPingMilliseconds ? "? ms" : $"{lobbyListing.PingMilliseconds} ms", GUILayout.Width(60f));

                if (GUILayout.Button("Join"))
                    JoinLobby(lobbyListing.Lobby);

                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Starts this peer as a host, then creates the public lobby the browser lists it under.
        /// </summary>
        /// <remarks>The server is up before the lobby exists, so no player can dial a server that is not listening.</remarks>
        private async void Host()
        {
            _hasStarted = true;
            _statusText = "Starting the server.";

            uint leaveCount = _leaveCount;
            await _unityTransportManager.StartHostAsync();

            // The user pressed Leave, or the scene closed, while the server was starting.
            if (this == null || leaveCount != _leaveCount)
                return;

            _statusText = "Creating the lobby.";

            // A refusal is reported in OnLobbyCreated, which carries Steam's reason; the task answers null for it.
            Lobby? createdLobby = await SteamMatchmaking.CreateLobbyAsync(_maximumLobbyMembers);

            // The user pressed Leave, and perhaps hosted again, or the scene closed, while Steam was creating the lobby.
            // Steam's lobby event cannot tell one create from another, so the lobby is adopted or left here.
            if (this == null || leaveCount != _leaveCount)
            {
                createdLobby?.Leave();

                return;
            }

            if (createdLobby is not Lobby lobby)
                return;

            _isLobbyOwner = true;
            _lobby = lobby;

            // Facepunch creates every lobby invisible.
            lobby.SetPublic();
            lobby.SetData(SampleKey, SampleValue);
            lobby.SetData(NameKey, _lobbyNameText);
            PublishPingLocation(lobby);

            _statusText = "Hosting. Players can find this lobby with Search.";
        }

        /// <summary>
        /// Asks Steam for this sample's public lobbies within the chosen distance, then lists them with each one's estimated ping.
        /// </summary>
        /// <remarks>Facepunch builds the filters into each request, so nothing carries over from the last search.</remarks>
        private async void Search()
        {
            _statusText = "Searching.";

            uint searchCount = ++_searchCount;
            Lobby[] lobbies = await CreateLobbyQuery().RequestAsync();

            // A newer search started, or the scene closed, while Steam was answering.
            if (this == null || searchCount != _searchCount)
                return;

            _lobbyListings.Clear();

            // Facepunch answers null both when nothing matched and when the request failed.
            if (lobbies is not null)
            {
                foreach (Lobby lobby in lobbies)
                    _lobbyListings.Add(new(lobby, lobby.GetData(NameKey), lobby.MemberCount, lobby.MaxMembers, EstimatePingMilliseconds(lobby)));
            }

            _lobbyListings.Sort(LobbyListing.CompareByPing);
            _statusText = _lobbyListings.Count == 0 ? "No lobbies found at that distance, or the search failed." : $"Lobbies found: {_lobbyListings.Count}.";
        }

        /// <summary>
        /// Enters a lobby, then starts the client toward its owner.
        /// </summary>
        /// <param name="lobby">The lobby to enter.</param>
        private async void JoinLobby(Lobby lobby)
        {
            _hasStarted = true;
            _statusText = $"Entering lobby [{lobby.Id.Value}].";

            uint leaveCount = _leaveCount;
            RoomEnter roomEnter = await lobby.Join();

            // The user may have pressed Leave, and perhaps started again, or the scene may have closed, while Steam was entering the lobby.
            if (this == null || leaveCount != _leaveCount)
            {
                if (roomEnter is RoomEnter.Success)
                    lobby.Leave();

                return;
            }

            if (roomEnter is not RoomEnter.Success)
            {
                _hasStarted = false;
                _statusText = $"Could not enter the lobby: [{roomEnter}].";

                return;
            }

            _lobby = lobby;

            Friend owner = lobby.Owner;

            if (owner.IsMe)
                return;

            StartClient(owner.Id);
        }

        /// <summary>
        /// Starts this peer as a client of the lobby owner.
        /// </summary>
        /// <param name="serverSteamId">The lobby owner's SteamID.</param>
        private async void StartClient(SteamId serverSteamId)
        {
            _facepunchTransport.ServerSteamId = serverSteamId.Value;
            _statusText = $"Connecting to the host [{serverSteamId.Value}].";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Leaves the lobby and stops every role this peer runs.
        /// </summary>
        /// <param name="statusText">What the panel shows afterwards.</param>
        private void Leave(string statusText)
        {
            _lobby?.Leave();

            // Cleared before the shutdown, whose client stop would otherwise call this again.
            _leaveCount++;
            _hasStarted = false;
            _hasConnected = false;
            _isLobbyOwner = false;
            _lobby = null;

            _unityTransportManager.ShutdownAll();

            _statusText = statusText;
        }

        /// <summary>
        /// Publishes this peer's ping location in its lobby's data once the relay network has measured it.
        /// </summary>
        /// <param name="lobby">The lobby this peer created.</param>
        /// <remarks>Facepunch raises no event when the relay network becomes available, so the host awaits its ping data instead.</remarks>
        private async void PublishPingLocation(Lobby lobby)
        {
            uint leaveCount = _leaveCount;
            await SteamNetworkingUtils.WaitForPingDataAsync();

            // The user left the lobby, or the scene closed, while the relay network was measuring.
            if (this == null || leaveCount != _leaveCount)
                return;

            if (SteamNetworkingUtils.LocalPingLocation is not NetPingLocation pingLocation)
            {
                Debug.LogWarning($"[{nameof(LobbyBrowserPanel)}] has no ping location to publish, so players see this lobby's ping as unknown.");

                return;
            }

            lobby.SetData(PingLocationKey, pingLocation.ToString());
        }

        /// <summary>
        /// Creates the query for this sample's lobbies within the chosen distance.
        /// </summary>
        /// <returns>The query, ready to request.</returns>
        private LobbyQuery CreateLobbyQuery()
        {
            LobbyQuery lobbyQuery = SteamMatchmaking.LobbyList.WithKeyValue(SampleKey, SampleValue).WithMaxResults(_maximumSearchResults);

            // Steam's default distance applies when no distance filter is set, so Default adds none.
            switch (_lobbyDistance)
            {
                case LobbyDistance.Close:
                    return lobbyQuery.FilterDistanceClose();
                case LobbyDistance.Far:
                    return lobbyQuery.FilterDistanceFar();
                case LobbyDistance.Worldwide:
                    return lobbyQuery.FilterDistanceWorldwide();
                case LobbyDistance.Default:
                default:
                    return lobbyQuery;
            }
        }

        /// <summary>
        /// Reports why Steam refused a lobby this peer asked for.
        /// </summary>
        /// <param name="result">Steam's result.</param>
        /// <param name="lobby">The lobby Steam created.</param>
        /// <remarks>A created lobby is adopted in <see cref="Host"/>, which can tell whether it belongs to a session already left.</remarks>
        private void OnLobbyCreated(Result result, Lobby lobby)
        {
            if (result is Result.OK || !_hasStarted)
                return;

            _statusText = $"Steam refused the lobby: [{result}]. The server is still up.";
            Debug.LogError($"[{nameof(LobbyBrowserPanel)}] could not create a lobby: [{result}].");
        }

        /// <summary>
        /// Shows the session's progress, and leaves the lobby once a joined player's client stops or fails to connect, since the server it dialled is gone.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_hasStarted || _isLobbyOwner || connectionStateChange.Invoker is not Invoker.Client)
                return;

            switch (connectionStateChange.Connection.LocalState)
            {
                case LocalConnectionState.Connected:
                    _hasConnected = true;
                    _statusText = "Connected to the host.";

                    break;
                case LocalConnectionState.Disconnected:
                case LocalConnectionState.Error:
                case LocalConnectionState.TimedOut:
                    Leave(_hasConnected ? "The host left, or the connection dropped." : "Could not connect to the host.");
                    break;
            }
        }

        /// <summary>
        /// Drops the subscriptions.
        /// </summary>
        /// <remarks>Facepunch's events are static, so a subscription left behind would outlive this scene and this play session.</remarks>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyCreated -= OnLobbyCreated;
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted() => _lobby?.Leave();

        /// <summary>
        /// Estimates the ping to a lobby's host from the ping location it published.
        /// </summary>
        /// <param name="lobby">The lobby.</param>
        /// <returns>The estimated ping in milliseconds, or <see cref="UnsetPingMilliseconds"/> when it could not be estimated.</returns>
        private static int EstimatePingMilliseconds(Lobby lobby)
        {
            if (NetPingLocation.TryParseFromString(lobby.GetData(PingLocationKey)) is not NetPingLocation pingLocation)
                return UnsetPingMilliseconds;

            int pingMilliseconds = SteamNetworkingUtils.EstimatePingTo(pingLocation);

            return pingMilliseconds < 0 ? UnsetPingMilliseconds : pingMilliseconds;
        }

        /// <summary>
        /// The distances a search can cover, in the order the panel shows them.
        /// </summary>
        private enum LobbyDistance : byte
        {
            /// <summary>
            /// Lobbies in the same region as this peer.
            /// </summary>
            Close,
            /// <summary>
            /// Lobbies in this peer's region and nearby ones, Steam's default.
            /// </summary>
            Default,
            /// <summary>
            /// Lobbies about half the world away or nearer.
            /// </summary>
            Far,
            /// <summary>
            /// Lobbies anywhere.
            /// </summary>
            Worldwide
        }

        /// <summary>
        /// One lobby a search found.
        /// </summary>
        private readonly struct LobbyListing
        {
            /// <summary>
            /// The lobby.
            /// </summary>
            public readonly Lobby Lobby;
            /// <summary>
            /// The name its host listed it under.
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// How many members it holds.
            /// </summary>
            public readonly int MemberCount;
            /// <summary>
            /// The most members it holds.
            /// </summary>
            public readonly int MemberLimit;
            /// <summary>
            /// The estimated ping to its host, in milliseconds, or <see cref="UnsetPingMilliseconds"/> when it could not be estimated.
            /// </summary>
            public readonly int PingMilliseconds;

            /// <summary>
            /// Creates the listing.
            /// </summary>
            /// <param name="lobby">The lobby.</param>
            /// <param name="name">The name its host listed it under.</param>
            /// <param name="memberCount">How many members it holds.</param>
            /// <param name="memberLimit">The most members it holds.</param>
            /// <param name="pingMilliseconds">The estimated ping to its host, or <see cref="UnsetPingMilliseconds"/>.</param>
            public LobbyListing(Lobby lobby, string name, int memberCount, int memberLimit, int pingMilliseconds)
            {
                Lobby = lobby;
                Name = name;
                MemberCount = memberCount;
                MemberLimit = memberLimit;
                PingMilliseconds = pingMilliseconds;
            }

            /// <summary>
            /// Orders listings by estimated ping, lowest first, with unknown pings last.
            /// </summary>
            /// <param name="leftLobbyListing">The first listing.</param>
            /// <param name="rightLobbyListing">The second listing.</param>
            /// <returns>A negative number when the first sorts earlier, zero when they tie, and a positive number otherwise.</returns>
            public static int CompareByPing(LobbyListing leftLobbyListing, LobbyListing rightLobbyListing)
            {
                uint leftPingMilliseconds = (uint)leftLobbyListing.PingMilliseconds;
                uint rightPingMilliseconds = (uint)rightLobbyListing.PingMilliseconds;

                // The unset ping wraps to the largest unsigned value, so it sorts last.
                return leftPingMilliseconds.CompareTo(rightPingMilliseconds);
            }
        }
    }
}
