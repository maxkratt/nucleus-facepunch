using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.QuickStart
{
    /// <summary>
    /// The Quick Start panel: host, or join a host by its SteamID, and leave. The host copies its SteamID from here and sends it to a
    /// friend, who pastes it and joins.
    /// </summary>
    /// <remarks>
    /// Joining is one property and one call: <see cref="FacepunchTransport.ServerSteamId"/> names the host, and
    /// <see cref="UnityTransportManager.StartClientAsync()"/> dials it through Steam. The transport must be set to
    /// <see cref="FacepunchAddressing.SteamId"/>.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/main/Samples~/QuickStart/README.md")]
    public class QuickStartPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam, read for whether Steam is up and the local SteamID.
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
        /// The Steam transport, which a join points at the host's SteamID.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Facepunch Transport, set to SteamID addressing.")]
        private FacepunchTransport _facepunchTransport;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The host's SteamID as typed or pasted into the panel.
        /// </summary>
        private string _serverSteamIdText = string.Empty;
        /// <summary>
        /// The bound transport manager, watched for this peer's client connecting or stopping.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The line under the SteamID saying what the session is doing.
        /// </summary>
        private string _statusText = "Host, or paste a host's SteamID and join.";
        /// <summary>
        /// Whether this peer has hosted or joined since it last left, which swaps the start buttons for Leave.
        /// </summary>
        private bool _hasStarted;
        /// <summary>
        /// Whether this peer is the host, whose own client stopping is part of its own Leave.
        /// </summary>
        private bool _isHost;
        /// <summary>
        /// Whether this peer's client connected since it joined, which tells a host that left from a join that never connected.
        /// </summary>
        private bool _hasConnected;

        /// <summary>
        /// Checks that every reference is set, then watches this peer's client connection.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _facepunchTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(QuickStartPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(FacepunchTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
        }

        /// <summary>
        /// Drops the subscription.
        /// </summary>
        private void OnDestroy()
        {
            if (_transportManager is not null)
                _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 364f, "Quick Start");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label($"My SteamID: {_steamBootstrap.OwnSteamId}");

            if (GUILayout.Button("Copy my SteamID"))
                GUIUtility.systemCopyBuffer = _steamBootstrap.OwnSteamId.ToString();

            GUILayout.Label(_statusText);

            if (_hasStarted)
            {
                if (GUILayout.Button("Leave"))
                    Leave();
            }
            else
            {
                if (GUILayout.Button("Host"))
                    Host();

                GUILayout.Label("Host's SteamID");
                _serverSteamIdText = GUILayout.TextField(_serverSteamIdText);

                if (GUILayout.Button("Join"))
                    Join();
            }

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Starts this peer as a host: a server, and a client of its own that joins it in process.
        /// </summary>
        private async void Host()
        {
            _hasStarted = true;
            _isHost = true;
            _statusText = "Hosting.";
            await _unityTransportManager.StartHostAsync();
        }

        /// <summary>
        /// Starts this peer as a client of the typed SteamID.
        /// </summary>
        private async void Join()
        {
            if (!ulong.TryParse(_serverSteamIdText.Trim(), out ulong serverSteamId))
            {
                Debug.LogWarning($"[{nameof(QuickStartPanel)}] could not read [{_serverSteamIdText}] as a SteamID; paste the host's 17 digit SteamID.");

                return;
            }

            _facepunchTransport.ServerSteamId = serverSteamId;
            _hasStarted = true;
            _hasConnected = false;
            _statusText = "Connecting.";
            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Stops every role this peer runs. A host's players are despawned, and a client drops what it received.
        /// </summary>
        /// <param name="statusText">The line shown once the panel is back on its start buttons.</param>
        private void Leave(string statusText = "Host, or paste a host's SteamID and join.")
        {
            _hasStarted = false;
            _isHost = false;
            _statusText = statusText;
            _unityTransportManager.ShutdownAll();
        }

        /// <summary>
        /// Shows a joined client's progress, and returns to the start buttons once it stops or fails to connect, since the host it
        /// dialled is gone.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_hasStarted || _isHost || connectionStateChange.Invoker is not Invoker.Client)
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
    }
}
