using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// The server's word to a client that a connected client has a SteamID, or has left, sent while <see cref="FacepunchSockets.ShareSteamIdsEnabled"/> is on.
    /// </summary>
    [NetworkType]
    public struct SteamIdNotice : IMessage
    {
        /// <summary>
        /// The <see cref="Connections.Connection.Id"/> of the client this is about.
        /// </summary>
        public uint ConnectionId;
        /// <summary>
        /// The client's SteamID64 as Steam authenticated its link, or <see cref="FacepunchSockets.UnsetSteamId"/> when the client has left.
        /// </summary>
        public ulong SteamId;
    }
}
