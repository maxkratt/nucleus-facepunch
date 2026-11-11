using System;
using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks.Data;
using UnityEngine;
using ServerList = Steamworks.ServerList;

namespace Nucleus.Integrations.Facepunch.Samples.DedicatedServer
{
    /// <summary>
    /// The server browser: lists this sample's dedicated servers from Steam's internet or LAN list, with name, map, players and ping,
    /// and joins one by the fake address it publishes; a fake address can also be typed or pasted.
    /// </summary>
    /// <remarks>
    /// The transport must be on <see cref="FacepunchAddressing.FakeIp"/>, with ticket authentication on to match the server. A join
    /// sets <see cref="FacepunchTransport.ServerAddress"/> to the fake address and starts the client, which Steam's relay carries to
    /// the server.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/main/Samples~/DedicatedServer/README.md")]
    public class ServerBrowserPanel : MonoBehaviour
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
        /// The Facepunch transport, which a join points at the server's fake address.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Facepunch Transport, set to Fake IP addressing.")]
        private FacepunchTransport _facepunchTransport;
        /// <summary>
        /// How long one search waits for servers to answer, in seconds.
        /// </summary>
        /// <remarks>A server that has not answered by then is left out of the list.</remarks>
        [SerializeField]
        [Tooltip("How long one search waits for servers to answer, in seconds.")]
        private float _searchTimeoutSeconds = 10f;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// Whether this peer has joined since it last left, which swaps the browser for Leave.
        /// </summary>
        private bool _hasStarted;
        /// <summary>
        /// Whether this peer's client has connected since it last joined, so a dropped session reads differently from a failed connect.
        /// </summary>
        private bool _hasConnected;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Refresh to list dedicated servers, or type a fake address.";
        /// <summary>
        /// The fake address typed or pasted into the panel.
        /// </summary>
        private string _serverAddressText = string.Empty;
        /// <summary>
        /// The search in flight, or <see langword="null"/> when none is.
        /// </summary>
        /// <remarks>Disposing it cancels it, and Facepunch's query then completes as cancelled.</remarks>
        private ServerList.Base _serverList;
        /// <summary>
        /// The servers the last search found.
        /// </summary>
        private readonly List<ServerListing> _serverListings = new();
        /// <summary>
        /// The transport manager whose state changes tell the panel the client connected or stopped.
        /// </summary>
        private TransportManager _transportManager;

        /// <summary>
        /// The server list filter that keeps only servers carrying every listed game tag.
        /// </summary>
        private const string GameTagsFilterKey = "gametagsand";

        /// <summary>
        /// Subscribes to the transport.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _facepunchTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(ServerBrowserPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(FacepunchTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            // The bootstrap wakes first, so Steam is up here or never will be.
            if (!_steamBootstrap.IsSteamInitialized)
            {
                enabled = false;

                return;
            }

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 564f, "Server Browser");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot browse or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_hasStarted)
            {
                if (GUILayout.Button("Leave"))
                    Leave("Left the server.");

                SampleGUI.EndPanel();

                return;
            }

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh internet"))
                RefreshInternet();

            if (GUILayout.Button("Refresh LAN"))
                RefreshLan();

            GUILayout.EndHorizontal();

            foreach (ServerListing serverListing in _serverListings)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(serverListing.Name, GUILayout.Width(200f));
                GUILayout.Label(serverListing.MapName, GUILayout.Width(90f));
                GUILayout.Label($"{serverListing.PlayerCount}/{serverListing.MaximumPlayers}", GUILayout.Width(50f));
                GUILayout.Label($"{serverListing.PingMilliseconds} ms", GUILayout.Width(60f));

                if (GUILayout.Button("Join"))
                    Join(serverListing.Address);

                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8f);
            GUILayout.Label("Server's fake address, such as 169.254.1.2:27015");
            _serverAddressText = GUILayout.TextField(_serverAddressText);

            if (GUILayout.Button("Join address"))
                Join(_serverAddressText.Trim());

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Asks Steam's master server for this sample's dedicated servers.
        /// </summary>
        private void RefreshInternet()
        {
            ServerList.Internet internetServerList = new();
            internetServerList.AddFilter(GameTagsFilterKey, DedicatedServerListing.SampleTag);

            _statusText = "Searching the internet list.";
            Search(internetServerList);
        }

        /// <summary>
        /// Asks the local network for dedicated servers of this game; servers without this sample's tag are skipped as they answer.
        /// </summary>
        private void RefreshLan()
        {
            _statusText = "Searching the local network.";
            Search(new ServerList.LocalNetwork());
        }

        /// <summary>
        /// Cancels the search in flight, runs a new one, and lists this sample's servers that answered it.
        /// </summary>
        /// <param name="serverList">The search to run, which this method disposes once it completes.</param>
        private async void Search(ServerList.Base serverList)
        {
            CancelSearch();
            _serverListings.Clear();
            _serverList = serverList;

            bool isAnswered = await serverList.RunQueryAsync(_searchTimeoutSeconds);

            // A newer search, a join or this panel's destruction cancelled and disposed this one, so its answer is stale.
            if (serverList != _serverList)
                return;

            // Facepunch ends a query unanswered only when it was cancelled or Steam went away.
            if (!isAnswered)
            {
                CancelSearch();
                _statusText = "The search stopped because Steam is no longer running.";

                return;
            }

            foreach (ServerInfo serverInfo in serverList.Responsive)
            {
                if (TryGetSampleAddress(serverInfo.Tags, out string address))
                    _serverListings.Add(new(serverInfo.Name, serverInfo.Map, serverInfo.Players, serverInfo.MaxPlayers, serverInfo.Ping, address));
            }

            CancelSearch();

            _statusText = _serverListings.Count == 0 ? "No servers found." : $"Servers found: {_serverListings.Count}.";
        }

        /// <summary>
        /// Cancels and disposes the search in flight, if any.
        /// </summary>
        private void CancelSearch()
        {
            if (_serverList is null)
                return;

            // The application's exit shuts Steam down before this object is destroyed, and that shutdown already freed the search.
            if (_steamBootstrap.IsSteamInitialized)
                _serverList.Dispose();

            _serverList = null;
        }

        /// <summary>
        /// Starts this peer as a client of a server's fake address.
        /// </summary>
        /// <param name="serverAddress">The fake address and port.</param>
        private async void Join(string serverAddress)
        {
            if (string.IsNullOrEmpty(serverAddress))
            {
                _statusText = "That server published no address.";

                return;
            }

            CancelSearch();

            _facepunchTransport.ServerAddress = serverAddress;
            _hasStarted = true;
            _statusText = $"Connecting to [{serverAddress}].";

            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Stops the client.
        /// </summary>
        /// <param name="statusText">What the panel shows afterwards.</param>
        private void Leave(string statusText)
        {
            // Cleared before the shutdown, whose client stop would otherwise call this again.
            _hasStarted = false;
            _hasConnected = false;

            _unityTransportManager.ShutdownAll();

            _statusText = statusText;
        }

        /// <summary>
        /// Shows the session's progress, and returns to the browser once the client stops or fails to connect.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_hasStarted || connectionStateChange.Invoker is not Invoker.Client)
                return;

            switch (connectionStateChange.Connection.LocalState)
            {
                case LocalConnectionState.Connected:
                    _hasConnected = true;
                    _statusText = "Connected to the server.";

                    break;
                case LocalConnectionState.Disconnected:
                case LocalConnectionState.Error:
                case LocalConnectionState.TimedOut:
                    Leave(_hasConnected ? "The server closed the connection, or it dropped." : "Could not connect to the server.");
                    break;
            }
        }

        /// <summary>
        /// Cancels the search and drops the subscription.
        /// </summary>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            CancelSearch();
        }

        /// <summary>
        /// Reads the fake address a server publishes in its game tags, when it carries this sample's tag.
        /// </summary>
        /// <param name="gameTags">The server's game tags.</param>
        /// <param name="address">The fake address and port, or <see langword="null"/> when the server published none.</param>
        /// <returns><see langword="true"/> when the server is this sample's, whether or not it published an address.</returns>
        private static bool TryGetSampleAddress(string[] gameTags, out string address)
        {
            address = null;

            if (Array.IndexOf(gameTags, DedicatedServerListing.SampleTag) < 0)
                return false;

            foreach (string gameTag in gameTags)
            {
                if (gameTag.StartsWith(DedicatedServerListing.FakeAddressTagPrefix, StringComparison.Ordinal))
                    address = gameTag.Substring(DedicatedServerListing.FakeAddressTagPrefix.Length);
            }

            return true;
        }

        /// <summary>
        /// One server a search found.
        /// </summary>
        private readonly struct ServerListing
        {
            /// <summary>
            /// The name the server is listed under.
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// The map the server lists.
            /// </summary>
            public readonly string MapName;
            /// <summary>
            /// How many players the server holds.
            /// </summary>
            public readonly int PlayerCount;
            /// <summary>
            /// The most players the server holds.
            /// </summary>
            public readonly int MaximumPlayers;
            /// <summary>
            /// The ping Steam measured to the server's query port, in milliseconds.
            /// </summary>
            public readonly int PingMilliseconds;
            /// <summary>
            /// The fake address and port the server published, or <see langword="null"/> when it published none.
            /// </summary>
            public readonly string Address;

            /// <summary>
            /// Creates the listing.
            /// </summary>
            /// <param name="name">The name the server is listed under.</param>
            /// <param name="mapName">The map the server lists.</param>
            /// <param name="playerCount">How many players the server holds.</param>
            /// <param name="maximumPlayers">The most players the server holds.</param>
            /// <param name="pingMilliseconds">The ping Steam measured to the server.</param>
            /// <param name="address">The fake address and port the server published, or <see langword="null"/>.</param>
            public ServerListing(string name, string mapName, int playerCount, int maximumPlayers, int pingMilliseconds, string address)
            {
                Name = name;
                MapName = mapName;
                PlayerCount = playerCount;
                MaximumPlayers = maximumPlayers;
                PingMilliseconds = pingMilliseconds;
                Address = address;
            }
        }
    }
}
