using Steamworks;
using ConnectionInfo = Steamworks.Data.ConnectionInfo;
using SteamConnection = Steamworks.Data.Connection;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// A connection state change Facepunch reported, held until the network loop thread applies it.
    /// </summary>
    internal readonly struct FacepunchStatusChange
    {
        /// <summary>
        /// The connection that changed.
        /// </summary>
        public readonly SteamConnection SteamConnection;
        /// <summary>
        /// The state it moved to.
        /// </summary>
        public readonly ConnectionState State;
        /// <summary>
        /// The remote peer's SteamID64, or <see cref="FacepunchSockets.UnsetSteamId"/> when it has none.
        /// </summary>
        public readonly ulong SteamId;
        /// <summary>
        /// Why the link ended, when it has.
        /// </summary>
        public readonly NetConnectionEnd EndReason;

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="steamConnection">The connection that changed.</param>
        /// <param name="connectionInfo">Facepunch's description of the change.</param>
        public FacepunchStatusChange(SteamConnection steamConnection, ConnectionInfo connectionInfo)
        {
            SteamConnection = steamConnection;
            State = connectionInfo.State;
            SteamId = connectionInfo.Identity.IsSteamId ? connectionInfo.Identity.SteamId.Value : FacepunchSockets.UnsetSteamId;
            EndReason = connectionInfo.EndReason;
        }
    }
}
