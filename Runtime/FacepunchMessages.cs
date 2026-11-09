using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Steamworks;
using SteamConnection = Steamworks.Data.Connection;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// Reads received Steam messages into engine packets, and holds the layout and limits both sockets of <see cref="FacepunchSockets"/> share.
    /// </summary>
    /// <remarks>
    /// Every message starts with one header byte, because Facepunch's receive callbacks report neither the message's flags nor its lane.
    /// </remarks>
    internal static class FacepunchMessages
    {
        /// <summary>
        /// The bytes the header adds to every message.
        /// </summary>
        internal const int HeaderBytes = 1;
        /// <summary>
        /// The header bit set on a message that rode the reliable channel.
        /// </summary>
        internal const byte ReliableFlag = 1;
        /// <summary>
        /// The header bit set on a piece of a split reliable packet that has another piece after it.
        /// </summary>
        internal const byte MorePiecesFlag = 2;
        /// <summary>
        /// The most bytes Steam carries in one message, the header included.
        /// </summary>
        internal const int MaximumMessageBytes = 512 * 1024;
        /// <summary>
        /// The most payload bytes one message carries; a longer reliable packet is sent in pieces of this length.
        /// </summary>
        internal const int MaximumPieceBytes = MaximumMessageBytes - HeaderBytes;
        /// <summary>
        /// The most messages taken from Steam in one receive call; Facepunch keeps calling until the queue is empty.
        /// </summary>
        internal const int MaximumMessagesPerReceive = 256;
        /// <summary>
        /// The reason code a connection this transport closes is closed with: Steam's first application-defined reason.
        /// </summary>
        internal const int ApplicationCloseReason = (int)NetConnectionEnd.App_Min;
        /// <summary>
        /// The lane reliable packets ride.
        /// </summary>
        internal const ushort ReliableLane = 0;
        /// <summary>
        /// The lane unreliable packets ride.
        /// </summary>
        internal const ushort UnreliableLane = 1;

        /// <summary>
        /// Copies a received message into an engine packet, joining the pieces of a split reliable packet.
        /// </summary>
        /// <param name="data">The message, which Facepunch releases once the callback returns.</param>
        /// <param name="size">The message's length, in bytes, including its header.</param>
        /// <param name="sender">The Connection the packet came from.</param>
        /// <param name="steamConnection">The Steam connection the message arrived on.</param>
        /// <param name="splitPackets">The socket's split packets, which a piece is added to.</param>
        /// <param name="maximumPacketBytes">The largest packet accepted, or <see cref="FacepunchSockets.UnsetMaximumClientPacketBytes"/> for no limit.</param>
        /// <param name="incomingPacket">The packet, owning a payload array rented from <see cref="TrackedArrayPool{T0}"/>.</param>
        /// <param name="isOverLimit"><see langword="true"/> when the message would take its packet past <paramref name="maximumPacketBytes"/>; the pieces joined so far have then been released, and the sender should be dropped.</param>
        /// <returns><see langword="true"/> when the message completed a packet.</returns>
        internal static bool TryCreatePacket(IntPtr data, int size, Connection sender, SteamConnection steamConnection, Dictionary<SteamConnection, ArraySegment<byte>> splitPackets, uint maximumPacketBytes, out IncomingPacket incomingPacket, out bool isOverLimit)
        {
            incomingPacket = default;
            isOverLimit = false;

            // A peer running this transport always sends a header, so a shorter message is not a packet.
            if (size < HeaderBytes)
                return false;

            byte header = Marshal.ReadByte(data);
            IntPtr payloadPointer = data + HeaderBytes;
            int payloadBytes = size - HeaderBytes;
            bool isReliable = (header & ReliableFlag) != 0;
            bool hasMorePieces = isReliable && (header & MorePiecesFlag) != 0;

            if (!hasMorePieces && (!isReliable || splitPackets.Count == 0 || !splitPackets.ContainsKey(steamConnection)))
            {
                if (maximumPacketBytes is not FacepunchSockets.UnsetMaximumClientPacketBytes && (uint)payloadBytes > maximumPacketBytes)
                {
                    isOverLimit = true;

                    return false;
                }

                byte[] payload = TrackedArrayPool<byte>.Rent(payloadBytes);
                Marshal.Copy(payloadPointer, payload, 0, payloadBytes);

                incomingPacket = new(new(payload, 0, payloadBytes), isReliable ? Channel.Reliable : Channel.Unreliable, sender);

                return true;
            }

            splitPackets.TryGetValue(steamConnection, out ArraySegment<byte> splitPacket);

            // Checked before anything is copied, so a packet past the limit never grows the buffer.
            if (maximumPacketBytes is not FacepunchSockets.UnsetMaximumClientPacketBytes && (ulong)splitPacket.Count + (ulong)payloadBytes > maximumPacketBytes)
            {
                DiscardSplitPacket(splitPackets, steamConnection);
                isOverLimit = true;

                return false;
            }

            byte[] splitBytes = splitPacket.Array ?? TrackedArrayPool<byte>.Rent(MaximumPieceBytes * 2);
            int splitLength = splitPacket.Count;

            if (splitBytes.Length < splitLength + payloadBytes)
                TrackedArrayPool<byte>.ResizeWithoutClearing(ref splitBytes, Math.Max(splitBytes.Length * 2, splitLength + payloadBytes));

            Marshal.Copy(payloadPointer, splitBytes, splitLength, payloadBytes);
            splitLength += payloadBytes;

            if (hasMorePieces)
            {
                splitPackets[steamConnection] = new(splitBytes, 0, splitLength);

                return false;
            }

            splitPackets.Remove(steamConnection);
            incomingPacket = new(new(splitBytes, 0, splitLength), Channel.Reliable, sender);

            return true;
        }

        /// <summary>
        /// Returns the pieces received so far of a closed connection's split reliable packet, which will never be completed.
        /// </summary>
        /// <param name="splitPackets">The socket's split packets.</param>
        /// <param name="steamConnection">The closed connection.</param>
        internal static void DiscardSplitPacket(Dictionary<SteamConnection, ArraySegment<byte>> splitPackets, SteamConnection steamConnection)
        {
            if (splitPackets.Remove(steamConnection, out ArraySegment<byte> splitPacket))
                TrackedArrayPool<byte>.Return(splitPacket.Array, clearArray: false);
        }

        /// <summary>
        /// Returns the pieces of every split reliable packet a socket was still joining.
        /// </summary>
        /// <param name="splitPackets">The socket's split packets.</param>
        internal static void DiscardSplitPackets(Dictionary<SteamConnection, ArraySegment<byte>> splitPackets)
        {
            foreach (KeyValuePair<SteamConnection, ArraySegment<byte>> splitPacket in splitPackets)
                TrackedArrayPool<byte>.Return(splitPacket.Value.Array, clearArray: false);

            splitPackets.Clear();
        }
    }
}
