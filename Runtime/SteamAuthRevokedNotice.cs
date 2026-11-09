using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// The reason a <see cref="SteamTicketAuthenticator"/> server gives a client it kicks because Steam withdrew its approval mid-session.
    /// </summary>
    /// <remarks>
    /// Steam withdraws an approved ticket when, for example, the account is VAC banned, its licence is revoked, or a Family Sharing owner
    /// starts playing. The client's authenticator logs the reason; a game that wants to show it registers its own handler for this message.
    /// </remarks>
    [NetworkType]
    public struct SteamAuthRevokedNotice : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// Steam's verdict, the name of its <c>AuthResponse</c> value.
        /// </summary>
        public string Response;
    }
}
