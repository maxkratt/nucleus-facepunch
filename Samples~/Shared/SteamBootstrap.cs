using System;
using Nucleus.Integrations.Unity.Managers.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples
{
    /// <summary>
    /// Owns Steam for a sample scene: it initializes the user's Steam, or a Steam game server, pumps its callbacks, and shuts it down
    /// after the transports as the application exits. <see cref="FacepunchTransport"/> never initializes Steam itself, so a game does
    /// this, with this component or its own code.
    /// </summary>
    /// <remarks>
    /// Its <c>Awake</c> runs before every ordinary component's, so Steam is up before any other sample component calls it and before
    /// a transport starts. It stays scene-scoped: Steam itself outlives a scene load, so the next scene's bootstrap adopts the running
    /// Steam rather than initializing it again, and leaving a scene never shuts Steam down. Callbacks are pumped here rather than on
    /// Facepunch's own thread, so every Steam callback arrives on the main thread. A game server needs no Steam client running, so a
    /// headless build can serve, and the transport then runs on the game server's networking interface.
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/Shared/README.md")]
    public class SteamBootstrap : MonoBehaviour
    {
        /// <summary>
        /// The application is exiting and Steam is about to shut down, so this is the last moment Steam calls can be made.
        /// </summary>
        public event SteamShutdownStartedHandler SteamShutdownStarted;
        /// <summary>
        /// Handler for the <see cref="SteamShutdownStarted"/> event.
        /// </summary>
        public delegate void SteamShutdownStartedHandler();
        /// <summary>
        /// Whether Steam is up for this scene, initialized here or adopted from an earlier scene.
        /// </summary>
        /// <remarks>For a game server this is true from initialization; its logon completes a moment later.</remarks>
        public bool IsSteamInitialized { get; private set; }
        /// <summary>
        /// The local user's SteamID, or the game server's once its logon completed, or <see cref="FacepunchSockets.UnsetSteamId"/> before.
        /// </summary>
        public ulong OwnSteamId { get; private set; }
        /// <summary>
        /// Whether this bootstrap initializes a Steam game server rather than the user's Steam.
        /// </summary>
        public bool GameServerEnabled => _gameServerEnabled;

        /// <summary>
        /// The game's Steam App ID, which Steam is initialized with.
        /// </summary>
        /// <remarks>480 is Spacewar, Steam's shared test application.</remarks>
        [SerializeField]
        [Tooltip("The game's Steam App ID. 480 is Spacewar, Steam's test app.")]
        private uint _appId = 480;
        /// <summary>
        /// True to check <see cref="SteamClient.RestartAppIfNecessary"/> first, so a build started outside Steam quits and is relaunched
        /// through it.
        /// </summary>
        /// <remarks>
        /// Steam answers <see langword="false"/> while <c>steam_appid.txt</c> sits beside the executable, so the check matters only in a
        /// shipped build, which leaves that file out. A game server never runs it.
        /// </remarks>
        [SerializeField]
        [Tooltip("Relaunch a build that was started outside Steam through Steam.")]
        private bool _relaunchThroughSteamEnabled;
        /// <summary>
        /// True to initialize a Steam game server instead of the user's Steam, as a dedicated server does. Such a peer can only serve.
        /// </summary>
        [SerializeField]
        [Tooltip("Initialize a Steam game server instead of the user's Steam, as a dedicated server does. Such a peer can only serve.")]
        private bool _gameServerEnabled;
        /// <summary>
        /// The UDP port the game server reports to Steam as its game port.
        /// </summary>
        [SerializeField]
        [Tooltip("Game port the game server reports to Steam.")]
        private ushort _gameServerPort = 27015;
        /// <summary>
        /// The UDP port Steam's server browser queries the game server on.
        /// </summary>
        [SerializeField]
        [Tooltip("Port Steam's server browser queries the game server on.")]
        private ushort _gameServerQueryPort = 27016;
        /// <summary>
        /// The game's folder name the game server reports, which Steam's server browser matches servers by.
        /// </summary>
        [SerializeField]
        [Tooltip("The game's folder name, which Steam's server browser matches servers by. spacewar for App ID 480.")]
        private string _gameServerModDirectory = "spacewar";
        /// <summary>
        /// The description of the game the game server reports to Steam's server browser.
        /// </summary>
        [SerializeField]
        [Tooltip("The game's description, shown in Steam's server browser.")]
        private string _gameServerDescription = "Nucleus Facepunch sample";

        /// <summary>
        /// The scene's transport manager, stopped before Steam shuts down, or <see langword="null"/> when the scene has none.
        /// </summary>
        private UnityTransportManager _unityTransportManager;

        /// <summary>
        /// Initializes Steam or a game server, or adopts the one an earlier scene initialized, and starts relay discovery.
        /// </summary>
        private void Awake()
        {
            _unityTransportManager = FindAnyObjectByType<UnityTransportManager>();

            if (_gameServerEnabled ? TryInitializeGameServer() : TryInitializeClient())
                IsSteamInitialized = true;
            else
                enabled = false;
        }

        /// <summary>
        /// Initializes the user's Steam, unless an earlier scene already did.
        /// </summary>
        /// <returns><see langword="true"/> when Steam is up.</returns>
        private bool TryInitializeClient()
        {
            if (_relaunchThroughSteamEnabled && SteamClient.RestartAppIfNecessary(_appId))
            {
                Debug.Log($"[{nameof(SteamBootstrap)}] was started outside Steam, so it quits while Steam relaunches it.");
                Application.Quit();

                return false;
            }

            // Steam outlives a scene load, so only the first scene of the process initializes it.
            if (!SteamClient.IsValid)
            {
                try
                {
                    SteamClient.Init(_appId, asyncCallbacks: false);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[{nameof(SteamBootstrap)}] could not initialize Steam, which must be running: [{exception.Message}].");

                    return false;
                }
            }

            OwnSteamId = SteamClient.SteamId.Value;

            // Started now so the first connect does not wait on relay discovery.
            SteamNetworkingUtils.InitRelayNetworkAccess();

            Debug.Log($"[{nameof(SteamBootstrap)}] Steam is up as [{SteamClient.Name}] with SteamID [{OwnSteamId}].");

            return true;
        }

        /// <summary>
        /// Initializes a Steam game server and logs it on anonymously, unless an earlier scene already did.
        /// </summary>
        /// <returns><see langword="true"/> when the game server is up.</returns>
        /// <remarks>
        /// The logon completes a moment later, in <see cref="OnGameServerConnected"/>. Facepunch.Steamworks offers only the anonymous
        /// logon, so the server gets a new SteamID each run rather than the persistent one a login token would give.
        /// </remarks>
        private bool TryInitializeGameServer()
        {
            SteamServer.OnSteamServersConnected += OnGameServerConnected;
            SteamServer.OnSteamServerConnectFailure += OnGameServerConnectFailure;

            if (SteamServer.IsValid)
            {
                if (SteamServer.LoggedOn)
                    OwnSteamId = SteamServer.SteamId.Value;

                return true;
            }

            SteamServerInit steamServerInit = new(_gameServerModDirectory, _gameServerDescription)
            {
                GamePort = _gameServerPort,
                QueryPort = _gameServerQueryPort,
                Secure = true,
                VersionString = Application.version
            };

            try
            {
                SteamServer.Init(_appId, steamServerInit, asyncCallbacks: false);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[{nameof(SteamBootstrap)}] could not initialize a Steam game server: [{exception.Message}].");

                return false;
            }

            SteamServer.DedicatedServer = true;
            SteamServer.LogOnAnonymous();
            SteamNetworkingUtils.InitRelayNetworkAccess();

            Debug.Log($"[{nameof(SteamBootstrap)}] Steam game server initialized on game port [{_gameServerPort}] and query port [{_gameServerQueryPort}], logging on anonymously.");

            return true;
        }

        /// <summary>
        /// Pumps Steam's callbacks, which is where lobby notices and the transport's connection status changes arrive from.
        /// </summary>
        private void Update()
        {
            // Something else may have shut Steam down, so each pump checks first.
            if (_gameServerEnabled)
            {
                if (SteamServer.IsValid)
                    SteamServer.RunCallbacks();
            }
            else if (SteamClient.IsValid)
            {
                SteamClient.RunCallbacks();
            }
        }

        /// <summary>
        /// Records the game server's SteamID once its logon completed.
        /// </summary>
        private void OnGameServerConnected()
        {
            OwnSteamId = SteamServer.SteamId.Value;

            Debug.Log($"[{nameof(SteamBootstrap)}] game server logged on with SteamID [{OwnSteamId}].");
        }

        /// <summary>
        /// Logs why the game server's logon failed.
        /// </summary>
        /// <param name="result">The reason Steam gave.</param>
        /// <param name="isStillRetrying">Whether Steam is still retrying the logon.</param>
        private void OnGameServerConnectFailure(Result result, bool isStillRetrying) => Debug.LogError($"[{nameof(SteamBootstrap)}] game server could not log on: [{result}], still retrying: [{isStillRetrying}].");

        /// <summary>
        /// Drops the game server subscriptions, leaving Steam running for the next scene.
        /// </summary>
        /// <remarks>Facepunch's events are static, so a subscription left behind would outlive this scene and this play session.</remarks>
        private void OnDestroy()
        {
            SteamServer.OnSteamServersConnected -= OnGameServerConnected;
            SteamServer.OnSteamServerConnectFailure -= OnGameServerConnectFailure;
        }

        /// <summary>
        /// Stops the transports, then shuts Steam down as the application exits.
        /// </summary>
        /// <remarks>The transports go first so their connections are closed while Steam can still send the close.</remarks>
        private void OnApplicationQuit()
        {
            if (!IsSteamInitialized)
                return;

            SteamShutdownStarted?.Invoke();

            if (_unityTransportManager != null)
                _unityTransportManager.ShutdownAll();

            if (_gameServerEnabled)
            {
                SteamServer.LogOff();
                SteamServer.Shutdown();
            }
            else
            {
                SteamClient.Shutdown();
            }

            IsSteamInitialized = false;
        }
    }
}
