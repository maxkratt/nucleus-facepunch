using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nucleus.Integrations.Facepunch.Samples.QuickMatch
{
    /// <summary>
    /// The matchmaking panel: one Find Match button that searches open lobbies at widening distances, joins the first with room, or
    /// creates an invisible lobby and waits, then loads the game scene for every member once the lobby is full.
    /// </summary>
    /// <remarks>
    /// No server runs while matchmaking, and this scene has no Nucleus objects. Two players who search together both find nothing and both
    /// create a lobby, so a lone owner keeps searching and moves into any waiting lobby with a lower SteamID, which only one side can do.
    /// Each search, join and create is awaited, and a counter tells the continuation of one the user has since cancelled.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/maxkratt/nucleus-facepunch/blob/master/Samples~/QuickMatch/README.md")]
    public class QuickMatchPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// How many players start a match, this peer included.
        /// </summary>
        [SerializeField]
        [Tooltip("How many players start a match, this peer included.")]
        private int _targetPlayerCount = 2;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// Where this peer is in matchmaking.
        /// </summary>
        private MatchState _matchState = MatchState.Idle;
        /// <summary>
        /// The lobby this peer is in, or <see langword="null"/> when it is in none.
        /// </summary>
        private Lobby? _lobby;
        /// <summary>
        /// How many members the lobby had when last read.
        /// </summary>
        private int _lobbyMemberCount;
        /// <summary>
        /// Counts the searches this peer has started or cancelled, so a continuation that was awaiting Steam can tell it belongs to one
        /// that is over.
        /// </summary>
        private uint _attemptCount;
        /// <summary>
        /// The last thing that happened, shown on the panel.
        /// </summary>
        private string _statusText = "Press Find Match.";
        /// <summary>
        /// Cancels the wait before a lone owner's next merge search, or <see langword="null"/> when none is waiting.
        /// </summary>
        private CancellationTokenSource _mergeTimerCancellationTokenSource;

        /// <summary>
        /// The distances searched, nearest first.
        /// </summary>
        private static readonly SearchDistance[] SearchDistances =
        {
            SearchDistance.Close,
            SearchDistance.Default,
            SearchDistance.Far,
            SearchDistance.Worldwide
        };

        /// <summary>
        /// The lobby data key marking a lobby as this sample's, since App ID 480 is shared by every developer testing on it.
        /// </summary>
        private const string SampleKey = "nucleus_quick_match";
        /// <summary>
        /// The value of <see cref="SampleKey"/>.
        /// </summary>
        private const string SampleValue = "1";
        /// <summary>
        /// The lobby data key holding the game version, so only matching builds meet.
        /// </summary>
        private const string VersionKey = "version";
        /// <summary>
        /// The lobby data key holding the lobby's state.
        /// </summary>
        private const string StateKey = "state";
        /// <summary>
        /// The state of a lobby still waiting for players.
        /// </summary>
        private const string OpenState = "open";
        /// <summary>
        /// The state of a full lobby whose members are loading the game.
        /// </summary>
        private const string StartingState = "starting";
        /// <summary>
        /// The most lobbies one search returns.
        /// </summary>
        private const int MaximumSearchResults = 20;
        /// <summary>
        /// The shortest wait before a lone owner's next merge search, in seconds.
        /// </summary>
        private const float MinimumMergeDelaySeconds = 2f;
        /// <summary>
        /// The longest wait before a lone owner's next merge search, in seconds.
        /// </summary>
        private const float MaximumMergeDelaySeconds = 5f;

        /// <summary>
        /// Subscribes to Steam's lobby events.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null)
            {
                Debug.LogError($"[{nameof(QuickMatchPanel)}] needs its [{nameof(SteamBootstrap)}] set.");
                enabled = false;

                return;
            }

            // Reaching the menu means any earlier match is over.
            QuickMatchSession.LobbySteamId = QuickMatchSession.UnsetLobbySteamId;

            // The bootstrap wakes first, so Steam is up here or never will be.
            if (!_steamBootstrap.IsSteamInitialized)
                return;

            _steamBootstrap.SteamShutdownStarted += OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 360f, "Quick Match");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot find a match.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label(_statusText);

            if (_matchState is MatchState.Waiting)
                GUILayout.Label($"Players {_lobbyMemberCount} / {_targetPlayerCount}");

            if (_matchState is MatchState.Idle)
            {
                if (GUILayout.Button("Find Match"))
                    FindMatch();
            }
            else if (_matchState is not MatchState.Loading && GUILayout.Button("Cancel"))
            {
                Cancel();
            }

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Searches each distance from the nearest, joins the first lobby with room, and creates a lobby when none is found.
        /// </summary>
        private async void FindMatch()
        {
            _matchState = MatchState.Searching;
            _statusText = "Searching for a match.";

            uint attemptCount = ++_attemptCount;

            foreach (SearchDistance distance in SearchDistances)
            {
                // Facepunch answers null both when nothing matched and when the request failed.
                Lobby[] lobbies = await CreateLobbyQuery(distance).RequestAsync();

                if (IsOver(attemptCount))
                    return;

                if (lobbies is not null && await TryJoinAnyAsync(attemptCount, lobbies))
                    return;

                if (IsOver(attemptCount))
                    return;
            }

            await CreateLobbyAsync(attemptCount);
        }

        /// <summary>
        /// Stops matchmaking and leaves any lobby.
        /// </summary>
        private void Cancel()
        {
            // Every search, join and create still awaiting Steam sees this and undoes itself.
            _attemptCount++;
            _matchState = MatchState.Idle;

            StopMergeTimer();
            LeaveCurrentLobby();

            _statusText = "Cancelled.";
        }

        /// <summary>
        /// Creates the query for this sample's open lobbies of this version with a free slot, within a distance.
        /// </summary>
        /// <param name="distance">How far to search.</param>
        /// <returns>The query, ready to request.</returns>
        /// <remarks>Facepunch builds the filters into each request, so nothing carries over from the last search.</remarks>
        private LobbyQuery CreateLobbyQuery(SearchDistance distance)
        {
            LobbyQuery lobbyQuery = SteamMatchmaking.LobbyList
                .WithKeyValue(SampleKey, SampleValue)
                .WithKeyValue(VersionKey, Application.version)
                .WithKeyValue(StateKey, OpenState)
                .WithSlotsAvailable(1)
                .WithMaxResults(MaximumSearchResults);

            // Steam's default distance applies when no distance filter is set, so Default adds none.
            switch (distance)
            {
                case SearchDistance.Close:
                    return lobbyQuery.FilterDistanceClose();
                case SearchDistance.Far:
                    return lobbyQuery.FilterDistanceFar();
                case SearchDistance.Worldwide:
                    return lobbyQuery.FilterDistanceWorldwide();
                case SearchDistance.Default:
                default:
                    return lobbyQuery;
            }
        }

        /// <summary>
        /// Whether the search this continuation belongs to is over, because it was cancelled or replaced, or the scene closed.
        /// </summary>
        /// <param name="attemptCount">The value of <see cref="_attemptCount"/> when the search began.</param>
        /// <returns><see langword="true"/> when the continuation should do nothing more.</returns>
        private bool IsOver(uint attemptCount) => this == null || attemptCount != _attemptCount;

        /// <summary>
        /// Tries each lobby in turn until one lets this peer in.
        /// </summary>
        /// <param name="attemptCount">The value of <see cref="_attemptCount"/> when the search began.</param>
        /// <param name="lobbies">The lobbies to try, in order.</param>
        /// <returns><see langword="true"/> when this peer entered one.</returns>
        private async Task<bool> TryJoinAnyAsync(uint attemptCount, IReadOnlyList<Lobby> lobbies)
        {
            foreach (Lobby lobby in lobbies)
            {
                _statusText = "Joining a match.";

                RoomEnter roomEnter = await lobby.Join();

                // Cancelled while Steam was joining it.
                if (IsOver(attemptCount))
                {
                    if (roomEnter is RoomEnter.Success)
                        lobby.Leave();

                    return false;
                }

                if (roomEnter is not RoomEnter.Success)
                    continue;

                EnterLobby(lobby);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates an invisible lobby marked as this sample's and waits in it.
        /// </summary>
        /// <param name="attemptCount">The value of <see cref="_attemptCount"/> when the search began.</param>
        /// <remarks>Facepunch creates every lobby invisible and <see cref="Lobby.SetInvisible"/> says so. Steam's searches return invisible lobbies, but friends lists do not show them.</remarks>
        private async Task CreateLobbyAsync(uint attemptCount)
        {
            _statusText = "Creating a lobby.";

            Lobby? createdLobby = await SteamMatchmaking.CreateLobbyAsync(_targetPlayerCount);

            // Cancelled while Steam was creating it.
            if (IsOver(attemptCount))
            {
                createdLobby?.Leave();

                return;
            }

            if (createdLobby is not Lobby lobby)
            {
                _matchState = MatchState.Idle;
                _statusText = "Steam refused the lobby.";

                return;
            }

            Debug.Log($"[{nameof(QuickMatchPanel)}] created lobby [{lobby.Id.Value}].");

            lobby.SetInvisible();
            lobby.SetData(SampleKey, SampleValue);
            lobby.SetData(VersionKey, Application.version);
            lobby.SetData(StateKey, OpenState);

            EnterLobby(lobby);
        }

        /// <summary>
        /// Records the lobby this peer is now in and reads it.
        /// </summary>
        /// <param name="lobby">The lobby this peer created or joined.</param>
        private void EnterLobby(Lobby lobby)
        {
            Debug.Log($"[{nameof(QuickMatchPanel)}] entered lobby [{lobby.Id.Value}].");

            _lobby = lobby;
            _matchState = MatchState.Waiting;

            ReadLobby(lobby);
        }

        /// <summary>
        /// Rereads the lobby, loads the game if it is starting, starts it if this peer owns it and it is full, and otherwise keeps a lone owner looking for a merge.
        /// </summary>
        /// <param name="lobby">The lobby this peer is in.</param>
        /// <remarks>The owner is read on every update because Steam hands the lobby on when its owner leaves.</remarks>
        private void ReadLobby(Lobby lobby)
        {
            if (lobby.GetData(StateKey) == StartingState)
            {
                LoadGame(lobby);

                return;
            }

            bool isOwner = lobby.Owner.IsMe;
            _lobbyMemberCount = lobby.MemberCount;

            if (isOwner && _lobbyMemberCount >= _targetPlayerCount)
            {
                StopMergeTimer();
                lobby.SetJoinable(false);
                lobby.SetData(QuickMatchSession.HostKey, SteamClient.SteamId.Value.ToString());

                // Set last: this is the value every member, this one included, loads the game on.
                lobby.SetData(StateKey, StartingState);

                return;
            }

            if (!isOwner || _lobbyMemberCount > 1)
                StopMergeTimer();
            else if (_mergeTimerCancellationTokenSource is null)
                StartMergeTimer();

            _statusText = "Waiting for players.";
        }

        /// <summary>
        /// Carries the lobby across the scene load and loads the game.
        /// </summary>
        /// <param name="lobby">The lobby this peer is in.</param>
        private void LoadGame(Lobby lobby)
        {
            StopMergeTimer();
            _matchState = MatchState.Loading;
            _statusText = "Starting the match.";

            // A scene load does not take this peer out of the lobby.
            QuickMatchSession.LobbySteamId = lobby.Id.Value;
            SceneManager.LoadScene(QuickMatchSession.GameSceneName);
        }

        /// <summary>
        /// Waits a jittered moment, then looks for a lobby to merge into.
        /// </summary>
        private async void StartMergeTimer()
        {
            _mergeTimerCancellationTokenSource = new();
            CancellationToken cancellationToken = _mergeTimerCancellationTokenSource.Token;

            try
            {
                await Awaitable.WaitForSecondsAsync(UnityEngine.Random.Range(MinimumMergeDelaySeconds, MaximumMergeDelaySeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            StopMergeTimer();
            SearchForMerge();
        }

        /// <summary>
        /// Cancels the wait before the next merge search, if there is one.
        /// </summary>
        private void StopMergeTimer()
        {
            if (_mergeTimerCancellationTokenSource is null)
                return;

            _mergeTimerCancellationTokenSource.Cancel();
            _mergeTimerCancellationTokenSource.Dispose();
            _mergeTimerCancellationTokenSource = null;
        }

        /// <summary>
        /// Searches the widest distance for a lobby a lone owner can move into, then acts on what it found.
        /// </summary>
        private async void SearchForMerge()
        {
            uint attemptCount = _attemptCount;

            Lobby[] lobbies = await CreateLobbyQuery(SearchDistance.Worldwide).RequestAsync();

            if (IsOver(attemptCount))
                return;

            ApplyMergeResults(lobbies);
        }

        /// <summary>
        /// Moves a still lone owner into a found lobby with a lower SteamID, or waits to look again.
        /// </summary>
        /// <param name="lobbies">The lobbies the merge search found, or <see langword="null"/> when it found none or failed.</param>
        private async void ApplyMergeResults(Lobby[] lobbies)
        {
            if (_matchState is not MatchState.Waiting || _lobby is not Lobby ownLobby || ownLobby.MemberCount != 1)
                return;

            List<Lobby> mergeCandidates = new();

            if (lobbies is not null)
            {
                foreach (Lobby lobby in lobbies)
                {
                    if (lobby.Id.Value < ownLobby.Id.Value)
                        mergeCandidates.Add(lobby);
                }
            }

            if (mergeCandidates.Count == 0)
            {
                // A member who came and went during the search has already restarted the timer.
                if (_mergeTimerCancellationTokenSource is null)
                    StartMergeTimer();

                return;
            }

            LeaveCurrentLobby();
            _matchState = MatchState.Searching;
            _statusText = "Moving to another lobby.";

            // A new attempt, so a merge search still awaiting Steam is over.
            uint attemptCount = ++_attemptCount;

            // Should every one of these fail, a new lobby is created.
            if (!await TryJoinAnyAsync(attemptCount, mergeCandidates) && !IsOver(attemptCount))
                await CreateLobbyAsync(attemptCount);
        }

        /// <summary>
        /// Leaves the lobby this peer is in, if any.
        /// </summary>
        private void LeaveCurrentLobby()
        {
            _lobby?.Leave();
            _lobby = null;
        }

        /// <summary>
        /// Whether a lobby Steam reported on is the one this peer is in.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <returns><see langword="true"/> when this peer is in <paramref name="lobby"/>.</returns>
        private bool IsCurrentLobby(Lobby lobby) => _lobby is Lobby currentLobby && currentLobby.Id.Value == lobby.Id.Value;

        /// <summary>
        /// Rereads the lobby when a member enters or leaves.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        /// <param name="friend">The member who entered or left.</param>
        private void OnLobbyMemberChanged(Lobby lobby, Friend friend)
        {
            if (_matchState is MatchState.Waiting && IsCurrentLobby(lobby))
                ReadLobby(lobby);
        }

        /// <summary>
        /// Rereads the lobby when its data changes, which is how every member learns the match is starting.
        /// </summary>
        /// <param name="lobby">The lobby Steam reported on.</param>
        private void OnLobbyDataChanged(Lobby lobby)
        {
            if (_matchState is MatchState.Waiting && IsCurrentLobby(lobby))
                ReadLobby(lobby);
        }

        /// <summary>
        /// Leaves the lobby as the application exits, before the bootstrap shuts Steam down.
        /// </summary>
        private void OnSteamShutdownStarted() => LeaveCurrentLobby();

        /// <summary>
        /// Drops the subscriptions and any waiting timer, leaving the lobby joined for the game scene.
        /// </summary>
        private void OnDestroy()
        {
            StopMergeTimer();

            if (_steamBootstrap != null)
                _steamBootstrap.SteamShutdownStarted -= OnSteamShutdownStarted;

            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberChanged;
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
        }

        /// <summary>
        /// How far a lobby search reaches.
        /// </summary>
        private enum SearchDistance : byte
        {
            /// <summary>
            /// Steam's close distance.
            /// </summary>
            Close,
            /// <summary>
            /// Steam's default distance, which applies when no distance filter is set.
            /// </summary>
            Default,
            /// <summary>
            /// Steam's far distance.
            /// </summary>
            Far,
            /// <summary>
            /// Every lobby, wherever it is.
            /// </summary>
            Worldwide
        }

        /// <summary>
        /// Where a peer is in matchmaking.
        /// </summary>
        private enum MatchState : byte
        {
            /// <summary>
            /// The peer is not matchmaking.
            /// </summary>
            Idle,
            /// <summary>
            /// The peer is searching for, joining or creating a lobby.
            /// </summary>
            Searching,
            /// <summary>
            /// The peer is in a lobby that is waiting for players.
            /// </summary>
            Waiting,
            /// <summary>
            /// The peer is loading the game scene.
            /// </summary>
            Loading
        }
    }
}
