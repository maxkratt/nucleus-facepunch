using System;
using Steamworks;
using SteamConnection = Steamworks.Data.Connection;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// A receive-only wrapper around one accepted connection, used while <see cref="FacepunchSockets.PerConnectionReceiveEnabled"/> is on.
    /// </summary>
    /// <remarks>
    /// <c>SocketManager.Receive</c> marshals every message's struct, which allocates per message under IL2CPP; <c>ConnectionManager.Receive</c>
    /// reads it by pointer. The connection stays owned by the server socket, so a wrapper is never closed.
    /// </remarks>
    internal sealed class FacepunchServerLink : ConnectionManager
    {
        /// <summary>
        /// The socket that receives this link's messages.
        /// </summary>
        private readonly FacepunchServerSocket _facepunchServerSocket;

        /// <summary>
        /// Creates a link for an accepted connection.
        /// </summary>
        /// <param name="facepunchServerSocket">The socket that receives this link's messages.</param>
        /// <param name="steamConnection">The accepted connection.</param>
        public FacepunchServerLink(FacepunchServerSocket facepunchServerSocket, SteamConnection steamConnection)
        {
            _facepunchServerSocket = facepunchServerSocket;
            Connection = steamConnection;
        }

        /// <inheritdoc/>
        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) => _facepunchServerSocket.OnClientMessage(Connection, data, size);
    }
}
