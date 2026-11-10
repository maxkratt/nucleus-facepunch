using Nucleus.Components;

namespace Nucleus.Integrations.Facepunch.Samples
{
    /// <summary>
    /// The replicated half of <see cref="PlayerNameTag"/>: the SteamID of the player who controls the object.
    /// </summary>
    /// <remarks>
    /// Only the SteamID travels. Each peer turns it into a persona name through its own Steam, so a name change on Steam shows without
    /// anything being resent.
    /// </remarks>
    public partial class PlayerNameTagComponent : NetworkComponent
    {
        /// <summary>
        /// The members of this component changed, written here or received.
        /// </summary>
        public event MembersChangedHandler MembersChanged;
        /// <summary>
        /// Handler for the <see cref="MembersChanged"/> event.
        /// </summary>
        /// <param name="memberFlags">One bit per member carried, or every bit for a full apply.</param>
        /// <param name="memberChangeDirection">Whether this peer wrote the members or received them.</param>
        public delegate void MembersChangedHandler(ulong memberFlags, MemberChangeDirection memberChangeDirection);
        /// <summary>
        /// The controlling player's SteamID, written by the server from the link Steam authenticated, or
        /// <see cref="FacepunchSockets.UnsetSteamId"/> while no player controls the object.
        /// </summary>
        public readonly NetworkMember<ulong> SteamId = new();

        /// <inheritdoc/>
        public override void OnMembersChanged(ulong memberFlags, MemberChangeDirection memberChangeDirection) => MembersChanged?.Invoke(memberFlags, memberChangeDirection);
    }
}
