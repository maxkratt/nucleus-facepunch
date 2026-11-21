using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Facepunch.Samples.BanList
{
    /// <summary>
    /// The reason the server gives a client it kicks or refuses.
    /// </summary>
    [NetworkType]
    public struct BanNotice : IMessage
    {
        /// <inheritdoc/>
        /// <remarks>A refusal reaches a client that was never authenticated, so this takes part in the handshake.</remarks>
        public bool IsAuthenticationRequired => false;

        /// <summary>
        /// The reason shown to the player.
        /// </summary>
        public string Reason;
    }
}
