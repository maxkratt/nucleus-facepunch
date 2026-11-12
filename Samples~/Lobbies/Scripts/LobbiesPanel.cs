using System;
using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.Lobbies
{
    /// <summary>
    /// The Lobbies panel: host a public, friends-only or private Steam lobby, invite through the overlay, join a friend's lobby, a
    /// typed lobby ID or the lobby Steam names with <c>+connect_lobby</c>, see the members with their names and avatars, and read the
    /// game mode and map the host sets as lobby data.
    /// </summary>
    /// <remarks>
    /// The lobby only finds the host; the session itself runs over <see cref="FacepunchTransport"/>, whose
    /// <see cref="FacepunchTransport.ServerSteamId"/> a member sets to the lobby owner before starting its client. The host starts its
    /// server before it creates the lobby, so no member can dial a server that is not yet listening. When the host leaves, Steam hands
    /// the lobby to another member, but the server went with the host, so each member leaves the lobby once its client stops.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/Lobbies/README.md")]
    public class LobbiesPanel : MonoBehaviour
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
        /// The Facepunch transport, which a member points at the lobby owner.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Facepunch Transport, set to SteamID addressing.")]
        private FacepunchTransport _facepunchTransport;
        /// <summary>
        /// The most players a lobby holds, the host included.
        /// </summary>
        [SerializeField]
        [Tooltip("Most players a lobby holds, the host included.")]
        private int _maximumLobbyMembers = 8;
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
        /// The type the next hosted lobby is created as.
        /// </summary>
        private LobbyType _lobbyType = LobbyType.FriendsOnly;
        /// <summary>
        /// Whether this peer created the lobby it is in.
        /// </summary>
        private bool _isLobbyOwner;
        /// <summary>
        /// Whether this peer has hosted or begun joining since it last left, which swaps the start buttons for the lobby view.
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
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Not in a lobby.";
        /// <summary>
        /// The lobby ID typed into the panel.
        /// </summary>
        private string _lobbyIdText = string.Empty;
        /// <summary>
        /// How far the member list is scrolled, once the lobby holds more members than it shows at once.
        /// </summary>
        private Vector2 _memberScrollPosition;
        /// <summary>
        /// The game mode the host is editing, set as lobby data on Apply.
        /// </summary>
        private string _gameModeText = "Deathmatch";
        /// <summary>
        /// The map the host is editing, set as lobby data on Apply.
        /// </summary>
        private string _mapText = "Arena";
        /// <summary>
        /// The lobby's game mode, as last read from its lobby data.
        /// </summary>
        private string _lobbyGameMode = string.Empty;
        /// <summary>
        /// The lobby's map, as last read from its lobby data.
        /// </summary>
        private string _lobbyMap = string.Empty;
        /// <summary>
        /// The most members the lobby holds, as last read from Steam.
        /// </summary>
        private int _lobbyMemberLimit;
        /// <summary>
        /// The lobby's members, as last read from Steam.
        /// </summary>
        private readonly List<LobbyMember> _lobbyMembers = new();
        /// <summary>
        /// Friends who are in a lobby of this game, as last read when the user pressed Refresh.
        /// </summary>
        private readonly List<LobbyMember> _friendsInLobbies = new();
        /// <summary>
        /// Each user's small avatar, by SteamID, created once Steam has the image.
        /// </summary>
        private readonly Dictionary<ulong, Texture2D> _avatarsBySteamId = new();
        /// <summary>
        /// The users whose avatar Steam is still fetching, so a member list reread while it does asks only once.
        /// </summary>
        private readonly HashSet<ulong> _avatarRequestSteamIds = new();
        /// <summary>
        /// The transport manager whose state changes tell a member that its client stopped.
        /// </summary>
        private TransportManager _transportManager;

        /// <summary>
        /// The names of the <see cref="LobbyType"/> members, in their order, as the panel shows them.
        /// </summary>
        private static readonly string[] LobbyTypeNames = { "Public", "Friends only", "Private" };

        /// <summary>
        /// The lobby data key the host sets the game mode under.
        /// </summary>
        private const string GameModeKey = "game_mode";
        /// <summary>
        /// The lobby data key the host sets the map under.
        /// </summary>
        private const string MapKey = "map";
        /// <summary>
        /// The launch argument Steam passes a game it starts to accept a lobby invite, followed by the lobby's ID.
        /// </summary>
        private const string ConnectLobbyArgument = "+connect_lobby";
        /// <summary>
        /// How many members the list shows before it scrolls.
        /// </summary>
        private const int VisibleMemberRowCount = 4;
        /// <summary>
        /// The member list's height once it scrolls, in pixels: four rows of thirty.
        /// </summary>
        private const float MemberListHeight = 120f;

        /// <summary>
        /// Subscribes to Steam's lobby and friend events and to the transport, then enters the lobby named on the command line, if any.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _facepunchTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(LobbiesPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(FacepunchTransport)}] set, and a bound CoreManager.");
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
            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyCreated += OnLobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
            SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
            SteamFriends.OnPersonaStateChange += OnPersonaStateChanged;

            if (TryGetLaunchLobby(out SteamId launchLobbySteamId))
                JoinLobby(launchLobbySteamId);
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 404f, "Lobbies");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_hasStarted)
                DrawLobby();
            else
                DrawStart();

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Draws the lobby type and Host, the friends in lobbies, and joining by a typed lobby ID.
        /// </summary>
        private void DrawStart()
        {
            SampleGUI.Section("Host");
            _lobbyType = (LobbyType)GUILayout.Toolbar((int)_lobbyType, LobbyTypeNames);

            if (GUILayout.Button("Host lobby"))
                Host();

            SampleGUI.Section("Friends in a lobby");

            if (GUILayout.Button("Refresh friends"))
                RefreshFriendsInLobbies();

            foreach (LobbyMember friend in _friendsInLobbies)
            {
                _avatarsBySteamId.TryGetValue(friend.SteamId.Value, out Texture2D avatar);

                if (SampleGUI.PlayerRow(avatar, friend.Name, buttonText: "Join"))
                    JoinLobby(friend.LobbySteamId);
            }

            SampleGUI.Section("Join by lobby ID");
            _lobbyIdText = GUILayout.TextField(_lobbyIdText);

            if (GUILayout.Button("Join lobby"))
            {
                if (ulong.TryParse(_lobbyIdText.Trim(), out ulong lobbyId))
                    JoinLobby(lobbyId);
                else
                    _statusText = "That is not a lobby ID.";
            }
        }

        /// <summary>
        /// Draws the lobby: invite, the lobby data, the members and Leave.
        /// </summary>
        /// <remarks>The host edits the lobby data in place; a member sees it as one line, since it cannot change it.</remarks>
        private void DrawLobby()
        {
            if (_lobby is Lobby lobby)
            {
                GUILayout.BeginHorizontal();

                if (GUILayout.Button("Copy lobby ID"))
                    GUIUtility.systemCopyBuffer = lobby.Id.Value.ToString();

                if (GUILayout.Button("Invite friends"))
                    SteamFriends.OpenGameInviteOverlay(lobby.Id);

                GUILayout.EndHorizontal();

                SampleGUI.Section("Lobby data");

                if (_isLobbyOwner)
                {
                    _gameModeText = SampleGUI.Field("Game mode", _gameModeText);
                    _mapText = SampleGUI.Field("Map", _mapText);

                    if (GUILayout.Button("Apply lobby data"))
                        SetLobbyData(lobby);
                }
                else
                {
                    GUILayout.Label(_lobbyGameMode.Length == 0 && _lobbyMap.Length == 0 ? "The host has set none." : $"{_lobbyGameMode} on {_lobbyMap}");
                }

                SampleGUI.Section($"Members {_lobbyMembers.Count} / {_lobbyMemberLimit}");

                // A full lobby would push Leave off a small window, so a long member list scrolls.
                bool isMemberListScrolling = _lobbyMembers.Count > VisibleMemberRowCount;

                if (isMemberListScrolling)
                    _memberScrollPosition = GUILayout.BeginScrollView(_memberScrollPosition, GUILayout.Height(MemberListHeight));

                foreach (LobbyMember lobbyMember in _lobbyMembers)
                {
                    _avatarsBySteamId.TryGetValue(lobbyMember.SteamId.Value, out Texture2D avatar);
                    SampleGUI.PlayerRow(avatar, lobbyMember.Name, badge: lobbyMember.IsOwner ? "HOST" : null);
                }

                if (isMemberListScrolling)
                    GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);

            if (GUILayout.Button("Leave"))
                Leave("Left the lobby.");
        }

        /// <summary>
        /// Starts this peer as a host, then creates the lobby players find it through.
        /// </summary>
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
            _statusText = $"Hosting a {LobbyTypeNames[(int)_lobbyType].ToLowerInvariant()} lobby.";

            ApplyLobbyType(lobby);
            SetLobbyData(lobby);
            RefreshLobby(lobby);
        }

        /// <summary>
        /// Enters a lobby, then starts the client toward its owner.
        /// </summary>
        /// <param name="lobbySteamId">The lobby to enter.</param>
        private async void JoinLobby(SteamId lobbySteamId)
        {
            _hasStarted = true;
            _statusText = $"Entering lobby [{lobbySteamId.Value}].";

            uint leaveCount = _leaveCount;
            Lobby lobby = new(lobbySteamId);
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
            RefreshLobby(lobby);

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
            _lobbyMembers.Clear();
            _lobbyGameMode = string.Empty;
            _lobbyMap = string.Empty;

            _unityTransportManager.ShutdownAll();

            _statusText = statusText;
        }

        /// <summary>
        /// Sets the lobby's visibility to the type the host chose.
        /// </summary>
        /// <param name="lobby">The lobby this peer created.</param>
        /// <remarks>Facepunch creates every lobby invisible, so the type is applied once Steam has created it.</remarks>
        private void ApplyLobbyType(Lobby lobby)
        {
            switch (_lobbyType)
            {
                case LobbyType.Public:
                    lobby.SetPublic();
                    break;
                case LobbyType.FriendsOnly:
                    lobby.SetFriendsOnly();
                    break;
                case LobbyType.Private:
                    lobby.SetPrivate();
                    break;
            }
        }

        /// <summary>
        /// Sets the game mode and map the host typed as lobby data, which reaches every member through <see cref="OnLobbyDataChanged"/>.
        /// </summary>
        /// <param name="lobby">The lobby this peer created.</param>
        private void SetLobbyData(Lobby lobby)
        {
            lobby.SetData(GameModeKey, _gameModeText);
            lobby.SetData(MapKey, _mapText);
        }

        /// <summary>
        /// Reads the lobby's members and data from Steam, and asks for each member's avatar.
        /// </summary>
        /// <param name="lobby">The lobby this peer is in.</param>
        private void RefreshLobby(Lobby lobby)
        {
            _lobbyMembers.Clear();

            ulong ownerSteamId = lobby.Owner.Id.Value;

            foreach (Friend member in lobby.Members)
            {
                _lobbyMembers.Add(new(member.Id, member.Name, lobby.Id, member.Id.Value == ownerSteamId));
                RequestAvatar(member.Id);
            }

            _lobbyMemberLimit = lobby.MaxMembers;
            _lobbyGameMode = lobby.GetData(GameModeKey);
            _lobbyMap = lobby.GetData(MapKey);
        }

        /// <summary>
        /// Lists the friends who are in a lobby of this game, from what Steam knows of each friend's game.
        /// </summary>
        private void RefreshFriendsInLobbies()
        {
            _friendsInLobbies.Clear();

            foreach (Friend friend in SteamFriends.GetFriends())
            {
                if (!friend.IsPlayingThisGame || friend.GameInfo?.Lobby is not Lobby friendLobby)
                    continue;

                _friendsInLobbies.Add(new(friend.Id, friend.Name, friendLobby.Id, isOwner: false));
                RequestAvatar(friend.Id);
            }

            _statusText = _friendsInLobbies.Count == 0 ? "No friends are in a lobby of this game." : $"Friends in a lobby of this game: {_friendsInLobbies.Count}.";
        }

        /// <summary>
        /// Asks Steam for a user's small avatar, unless the panel already has it or is already waiting for it.
        /// </summary>
        /// <param name="steamId">The user.</param>
        private async void RequestAvatar(SteamId steamId)
        {
            if (_avatarsBySteamId.ContainsKey(steamId.Value) || !_avatarRequestSteamIds.Add(steamId.Value))
                return;

            Image? image = await SteamFriends.GetSmallAvatarAsync(steamId);

            // The scene may have closed while Steam fetched the image.
            if (this == null)
                return;

            _avatarRequestSteamIds.Remove(steamId.Value);

            if (image is Image avatarImage)
                StoreAvatar(steamId.Value, avatarImage);
        }

        /// <summary>
        /// Copies a Steam image into a texture and stores it as the user's avatar.
        /// </summary>
        /// <param name="steamId">The user.</param>
        /// <param name="image">The image Steam returned.</param>
        private void StoreAvatar(ulong steamId, Image image)
        {
            int width = (int)image.Width;
            int height = (int)image.Height;
            int rowBytes = width * 4;

            // Steam's rows run top to bottom and a texture's bottom to top, so the rows are reversed.
            byte[] textureBytes = new byte[image.Data.Length];

            for (int row = 0; row < height; row++)
                Buffer.BlockCopy(image.Data, row * rowBytes, textureBytes, (height - 1 - row) * rowBytes, rowBytes);

            Texture2D avatar = new(width, height, TextureFormat.RGBA32, mipChain: false);
            avatar.LoadRawTextureData(textureBytes);
            avatar.Apply();

            _avatarsBySteamId[steamId] = avatar;
        }

        /// <summary>
        /// Whether a lobby Steam reported on is the one this peer is in.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <returns><see langword="true"/> when this peer is in <paramref name="lobby"/>.</returns>
        private bool IsCurrentLobby(Lobby lobby) => _lobby is Lobby currentLobby && currentLobby.Id.Value == lobby.Id.Value;

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
            Debug.LogError($"[{nameof(LobbiesPanel)}] could not create a lobby: [{result}].");
        }

        /// <summary>
        /// Rereads the members when one enters, leaves or changes its member data in this peer's lobby.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <param name="friend">The member.</param>
        private void OnLobbyMemberChanged(Lobby lobby, Friend friend)
        {
            if (IsCurrentLobby(lobby))
                RefreshLobby(lobby);
        }

        /// <summary>
        /// Rereads the lobby when its data changes.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        private void OnLobbyDataChanged(Lobby lobby)
        {
            if (IsCurrentLobby(lobby))
                RefreshLobby(lobby);
        }

        /// <summary>
        /// Enters the lobby the user accepted an invite to, or chose to join a friend in, from Steam while the game was running.
        /// </summary>
        /// <param name="lobby">The lobby to enter.</param>
        /// <param name="friendSteamId">The friend who invited the user, or whom the user chose to join.</param>
        private void OnGameLobbyJoinRequested(Lobby lobby, SteamId friendSteamId)
        {
            if (_hasStarted)
            {
                _statusText = "Leave this lobby before joining another.";

                return;
            }

            JoinLobby(lobby.Id);
        }

        /// <summary>
        /// Rereads the lobby when a user's name or avatar arrives or changes, which also asks again for an avatar Steam lacked before.
        /// </summary>
        /// <param name="friend">The user whose state changed.</param>
        /// <remarks>
        /// Facepunch does not say what changed, so an avatar the panel already holds is kept: one that changes while the panel is open
        /// shows from the next scene.
        /// </remarks>
        private void OnPersonaStateChanged(Friend friend)
        {
            if (_lobby is Lobby lobby)
                RefreshLobby(lobby);
        }

        /// <summary>
        /// Shows the session's progress, and leaves the lobby once this member's client stops or fails to connect, since the server it dialled is gone.
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
        /// Drops the subscriptions and avatar textures.
        /// </summary>
        /// <remarks>
        /// Facepunch's events are static, so a subscription left behind would outlive this scene and this play session. Steam keeps
        /// running for the next scene; the bootstrap shuts it down when the application exits.
        /// </remarks>
        private void OnDestroy()
        {
            if (_transportManager is null)
                return;

            _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
            _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyCreated -= OnLobbyCreated;
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDataChanged -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
            SteamFriends.OnPersonaStateChange -= OnPersonaStateChanged;

            foreach (Texture2D avatar in _avatarsBySteamId.Values)
                Destroy(avatar);

            _avatarsBySteamId.Clear();
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted() => _lobby?.Leave();

        /// <summary>
        /// Reads the lobby Steam named on the command line when it started the game to accept an invite.
        /// </summary>
        /// <param name="lobbySteamId">The lobby, when one was named.</param>
        /// <returns><see langword="true"/> when the command line names a lobby.</returns>
        private static bool TryGetLaunchLobby(out SteamId lobbySteamId)
        {
            string[] arguments = Environment.GetCommandLineArgs();

            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], ConnectLobbyArgument, StringComparison.OrdinalIgnoreCase) && ulong.TryParse(arguments[i + 1], out ulong lobbyId))
                {
                    lobbySteamId = lobbyId;

                    return true;
                }
            }

            lobbySteamId = default;

            return false;
        }

        /// <summary>
        /// A visibility the host can create its lobby with, in the order the panel shows them.
        /// </summary>
        private enum LobbyType : byte
        {
            /// <summary>
            /// Anyone can find the lobby in a lobby list query, and join it.
            /// </summary>
            Public,
            /// <summary>
            /// The host's friends can see and join the lobby through Steam.
            /// </summary>
            FriendsOnly,
            /// <summary>
            /// Only an invite or the lobby's ID gets a player in.
            /// </summary>
            Private
        }

        /// <summary>
        /// A user the panel lists: a lobby member, or a friend in a lobby.
        /// </summary>
        private readonly struct LobbyMember
        {
            /// <summary>
            /// The user's SteamID.
            /// </summary>
            public readonly SteamId SteamId;
            /// <summary>
            /// The user's Steam display name.
            /// </summary>
            public readonly string Name;
            /// <summary>
            /// The lobby the user is in.
            /// </summary>
            public readonly SteamId LobbySteamId;
            /// <summary>
            /// Whether the user owns that lobby.
            /// </summary>
            public readonly bool IsOwner;

            /// <summary>
            /// Creates the entry.
            /// </summary>
            /// <param name="steamId">The user's SteamID.</param>
            /// <param name="name">The user's Steam display name.</param>
            /// <param name="lobbySteamId">The lobby the user is in.</param>
            /// <param name="isOwner">Whether the user owns that lobby.</param>
            public LobbyMember(SteamId steamId, string name, SteamId lobbySteamId, bool isOwner)
            {
                SteamId = steamId;
                Name = name;
                LobbySteamId = lobbySteamId;
                IsOwner = isOwner;
            }
        }
    }
}
