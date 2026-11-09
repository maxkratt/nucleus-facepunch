using UnityEngine;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// What a <see cref="FacepunchSockets"/> client dials to reach the server, which also decides how the server listens.
    /// </summary>
    public enum FacepunchAddressing : byte
    {
        /// <summary>
        /// The host's SteamID and a virtual port, so no port needs opening. The link normally rides Valve's relay network, and Steam may
        /// connect two players directly when their Steam settings allow it.
        /// </summary>
        [InspectorName("Steam ID")]
        SteamId,
        /// <summary>
        /// A real IPv4 or IPv6 address and UDP port, dialled directly, for a dedicated server with a public port, or two processes on one machine.
        /// </summary>
        [InspectorName("IP Address")]
        IpAddress,
        /// <summary>
        /// A fake IP address and port that Steam grants the server, which clients dial like an address while the traffic still rides the
        /// relay, so a dedicated server gets relay protection without exposing its real address.
        /// </summary>
        [InspectorName("Fake IP")]
        FakeIp
    }
}
