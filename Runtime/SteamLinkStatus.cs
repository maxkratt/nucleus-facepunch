using ConnectionStatus = Steamworks.Data.ConnectionStatus;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// Steam's live view of one link: its ping, its quality each way, its rates and what is still queued on it.
    /// </summary>
    /// <remarks>Read through <see cref="FacepunchSockets.TryGetLinkStatus"/>. Steam measures these itself, beside the engine's own round trip time.</remarks>
    public readonly struct SteamLinkStatus
    {
        /// <summary>
        /// Steam's current estimate of the round trip time, in milliseconds.
        /// </summary>
        public int PingMilliseconds { get; }
        /// <summary>
        /// The share of packets this side received intact, from 0 to 1, or a negative value while Steam has no measurement.
        /// </summary>
        public float LocalQuality { get; }
        /// <summary>
        /// The share of packets the other side received intact, from 0 to 1, or a negative value while Steam has no measurement.
        /// </summary>
        public float RemoteQuality { get; }
        /// <summary>
        /// The bytes a second currently sent.
        /// </summary>
        public float OutBytesPerSecond { get; }
        /// <summary>
        /// The bytes a second currently received.
        /// </summary>
        public float InBytesPerSecond { get; }
        /// <summary>
        /// The reliable bytes queued and not yet sent.
        /// </summary>
        /// <remarks>A figure that keeps growing means the link cannot keep up.</remarks>
        public uint PendingReliableBytes { get; }
        /// <summary>
        /// The unreliable bytes queued and not yet sent.
        /// </summary>
        public uint PendingUnreliableBytes { get; }
        /// <summary>
        /// The reliable bytes sent and not yet acknowledged.
        /// </summary>
        public uint SentUnacknowledgedReliableBytes { get; }

        /// <summary>
        /// Creates a new instance from Steam's own status.
        /// </summary>
        /// <param name="connectionStatus">The status Steam reported.</param>
        internal SteamLinkStatus(in ConnectionStatus connectionStatus)
        {
            PingMilliseconds = connectionStatus.Ping;
            LocalQuality = connectionStatus.ConnectionQualityLocal;
            RemoteQuality = connectionStatus.ConnectionQualityRemote;
            OutBytesPerSecond = connectionStatus.OutBytesPerSec;
            InBytesPerSecond = connectionStatus.InBytesPerSec;
            PendingReliableBytes = (uint)connectionStatus.PendingReliable;
            PendingUnreliableBytes = (uint)connectionStatus.PendingUnreliable;
            SentUnacknowledgedReliableBytes = (uint)connectionStatus.SentUnackedReliable;
        }
    }
}
