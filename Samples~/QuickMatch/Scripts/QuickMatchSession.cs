using UnityEngine;

namespace Nucleus.Integrations.Facepunch.Samples.QuickMatch
{
    /// <summary>
    /// Carries the matched lobby from the menu scene into the game scene, and holds the names both scenes agree on.
    /// </summary>
    /// <remarks>
    /// This is static because samples may not use <c>DontDestroyOnLoad</c> and nothing else survives the scene load. It is reset from
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>, so it never carries a lobby into a later play session.
    /// </remarks>
    public static class QuickMatchSession
    {
        /// <summary>
        /// The SteamID of the lobby the game scene plays from, or <see cref="UnsetLobbySteamId"/> outside a match.
        /// </summary>
        public static ulong LobbySteamId = UnsetLobbySteamId;
        /// <summary>
        /// The <see cref="LobbySteamId"/> of no lobby.
        /// </summary>
        public const ulong UnsetLobbySteamId = 0;
        /// <summary>
        /// The name of the menu scene.
        /// </summary>
        public const string MenuSceneName = "QuickMatch";
        /// <summary>
        /// The name of the game scene.
        /// </summary>
        public const string GameSceneName = "QuickMatchGame";
        /// <summary>
        /// The lobby data key naming the SteamID that will host the match.
        /// </summary>
        public const string HostKey = "host";

        /// <summary>
        /// Clears the lobby at the head of each play session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => LobbySteamId = UnsetLobbySteamId;
    }
}
