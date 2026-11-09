using System;
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// The Steam auth session ticket a client sends the server so <see cref="SteamTicketAuthenticator"/> can check it with Steam.
    /// </summary>
    [NetworkType]
    public struct SteamAuthTicketMessage : IMessage
    {
        /// <inheritdoc/>
        /// <remarks>False, since the client sending it is by definition not authenticated yet.</remarks>
        public bool IsAuthenticationRequired => false;

        /// <summary>
        /// The ticket as <c>SteamUser.GetAuthSessionTicket</c> wrote it.
        /// </summary>
        /// <remarks>The server reads it into an array rented from <c>TrackedArrayPool</c>, which its handler returns.</remarks>
        public ArraySegment<byte> Ticket;
    }
}
