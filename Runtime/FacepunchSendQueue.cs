using System;
using System.Collections.Generic;
using CodeBoost.Performance;
using Nucleus.Managers.Transports;
using Nucleus.Packets;
using Nucleus.Transports;
using Steamworks;
using ConnectionStatus = Steamworks.Data.ConnectionStatus;
using SendType = Steamworks.Data.SendType;
using SteamConnection = Steamworks.Data.Connection;

namespace Nucleus.Integrations.Facepunch
{
    /// <summary>
    /// One socket's outbound messages: unreliable ones go to Steam at once, and reliable ones wait here until their link has room.
    /// </summary>
    /// <remarks>
    /// Steam refuses a message past 512 KiB, or one that would overfill a connection's send buffer, and a refused reliable message would
    /// leave the peers disagreeing. So a longer reliable packet is split into pieces, and reliable messages are held as pooled copies and
    /// handed over at <see cref="Flush"/>, as far as each link's buffer has room.
    /// </remarks>
    internal sealed class FacepunchSendQueue
    {
        /// <summary>
        /// The send buffer of the socket's connections, in bytes, which queued reliable messages are measured against.
        /// </summary>
        public int SendBufferBytes = FacepunchSockets.DefaultSendBufferBytes;
        /// <summary>
        /// The reliable messages the last <see cref="Flush"/> saw Steam refuse, for the socket to close their links; the socket clears it.
        /// </summary>
        public readonly List<RefusedSend> RefusedReliableSends = new();
        /// <summary>
        /// The reliable messages each connection has waiting, in order.
        /// </summary>
        /// <remarks>An entry lives until its connection closes, so a link that sends reliably allocates its queue once.</remarks>
        private readonly Dictionary<SteamConnection, Queue<QueuedSend>> _queuedReliableSends = new();
        /// <summary>
        /// The reused buffer each unreliable message is built in, grown to the largest message seen.
        /// </summary>
        private byte[] _unreliableBuffer = new byte[DefaultUnreliableBufferBytes];
        /// <summary>
        /// The initial length of <see cref="_unreliableBuffer"/>, in bytes.
        /// </summary>
        private const int DefaultUnreliableBufferBytes = 2048;
        /// <summary>
        /// The bytes Steam's reliable framing adds to each message in its queue, at most, counted when measuring a message against the room left.
        /// </summary>
        private const int ReliableHeaderAllowanceBytes = 16;
        /// <summary>
        /// The flags every reliable message is sent with.
        /// </summary>
        private const SendType ReliableSendType = SendType.Reliable | SendType.NoNagle;
        /// <summary>
        /// The flags every unreliable message is sent with.
        /// </summary>
        private const SendType UnreliableSendType = SendType.Unreliable | SendType.NoNagle;

        /// <summary>
        /// Sends an unreliable packet, or queues a reliable one for the next flush, and hands the packet back to the engine, being copied.
        /// </summary>
        /// <param name="steamConnection">The connection to send on.</param>
        /// <param name="outgoingPacket">The packet to send.</param>
        /// <param name="facepunchSockets">The transport, which counts refused unreliable messages.</param>
        /// <remarks>
        /// The engine splits unreliable packets to the transmission unit itself, so only a reliable packet can reach a message's limit. Its
        /// pieces ride the reliable lane, which keeps them in order, and each but the last carries <see cref="FacepunchMessages.MorePiecesFlag"/>.
        /// </remarks>
        public void Send(SteamConnection steamConnection, OutgoingPacket outgoingPacket, FacepunchSockets facepunchSockets)
        {
            ArraySegment<byte> payload = outgoingPacket.Payload;

            if (outgoingPacket.Channel is Channel.Reliable)
                QueueReliablePieces(steamConnection, payload);
            else
                SendUnreliable(steamConnection, payload, facepunchSockets);

            TransportManager.PacketOffloaded(outgoingPacket);
        }

        /// <summary>
        /// Hands every reliable message a connection has waiting to Steam, whether or not it has room.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <remarks>For a lingering close, which drains Steam's queue anyway; any Steam refuses die with the link.</remarks>
        public void ReleaseAllQueuedReliableSends(SteamConnection steamConnection)
        {
            if (_queuedReliableSends.TryGetValue(steamConnection, out Queue<QueuedSend> queuedReliableSends))
                ReleaseQueuedReliableSends(steamConnection, queuedReliableSends, budgetBytes: int.MaxValue);
        }

        /// <summary>
        /// Hands reliable messages to Steam as far as each link's send buffer has room, and records in <see cref="RefusedReliableSends"/>
        /// every one Steam refused.
        /// </summary>
        /// <remarks>With Steam shut down nothing can be sent, so the queued messages are dropped with it.</remarks>
        public void Flush()
        {
            if (FacepunchSockets.IsSteamShutDown())
            {
                Clear();

                return;
            }

            foreach (KeyValuePair<SteamConnection, Queue<QueuedSend>> queuedReliableSends in _queuedReliableSends)
            {
                if (queuedReliableSends.Value.Count > 0)
                    ReleaseQueuedReliableSends(queuedReliableSends.Key, queuedReliableSends.Value, GetSendBudgetBytes(queuedReliableSends.Key));
            }
        }

        /// <summary>
        /// Returns the reliable messages a closed connection had waiting, which Steam will never take.
        /// </summary>
        /// <param name="steamConnection">The closed connection.</param>
        public void Discard(SteamConnection steamConnection)
        {
            if (!_queuedReliableSends.Remove(steamConnection, out Queue<QueuedSend> queuedReliableSends))
                return;

            while (queuedReliableSends.TryDequeue(out QueuedSend queuedSend))
                TrackedArrayPool<byte>.Return(queuedSend.Bytes, clearArray: false);
        }

        /// <summary>
        /// Returns every queued message without handing any to Steam.
        /// </summary>
        public void Clear()
        {
            foreach (KeyValuePair<SteamConnection, Queue<QueuedSend>> queuedReliableSends in _queuedReliableSends)
            {
                while (queuedReliableSends.Value.TryDequeue(out QueuedSend queuedSend))
                    TrackedArrayPool<byte>.Return(queuedSend.Bytes, clearArray: false);
            }

            _queuedReliableSends.Clear();
            RefusedReliableSends.Clear();
        }

        /// <summary>
        /// Copies a reliable packet into pooled messages queued behind the connection's earlier ones.
        /// </summary>
        /// <param name="steamConnection">The connection to send on.</param>
        /// <param name="payload">The packet's bytes.</param>
        private void QueueReliablePieces(SteamConnection steamConnection, ArraySegment<byte> payload)
        {
            if (!_queuedReliableSends.TryGetValue(steamConnection, out Queue<QueuedSend> queuedReliableSends))
            {
                queuedReliableSends = new();
                _queuedReliableSends.Add(steamConnection, queuedReliableSends);
            }

            int offset = payload.Offset;
            int remainingBytes = payload.Count;

            while (remainingBytes > FacepunchMessages.MaximumPieceBytes)
            {
                QueuePiece(queuedReliableSends, payload.Array!, offset, FacepunchMessages.MaximumPieceBytes, FacepunchMessages.ReliableFlag | FacepunchMessages.MorePiecesFlag);

                offset += FacepunchMessages.MaximumPieceBytes;
                remainingBytes -= FacepunchMessages.MaximumPieceBytes;
            }

            QueuePiece(queuedReliableSends, payload.Array!, offset, remainingBytes, FacepunchMessages.ReliableFlag);
        }

        /// <summary>
        /// Sends an unreliable packet now, counting it when Steam refuses it.
        /// </summary>
        /// <param name="steamConnection">The connection to send on.</param>
        /// <param name="payload">The packet's bytes.</param>
        /// <param name="facepunchSockets">The transport, which counts refused messages.</param>
        private void SendUnreliable(SteamConnection steamConnection, ArraySegment<byte> payload, FacepunchSockets facepunchSockets)
        {
            int messageBytes = payload.Count + FacepunchMessages.HeaderBytes;

            if (_unreliableBuffer.Length < messageBytes)
                _unreliableBuffer = new byte[Math.Max(_unreliableBuffer.Length * 2, messageBytes)];

            _unreliableBuffer[0] = 0;
            Buffer.BlockCopy(payload.Array!, payload.Offset, _unreliableBuffer, FacepunchMessages.HeaderBytes, payload.Count);

            Result result = steamConnection.SendMessage(_unreliableBuffer, 0, messageBytes, UnreliableSendType, FacepunchMessages.UnreliableLane);

            // A link the peer has already closed has no one to reach, and Steam's report of the close is on its way.
            if (result is not Result.OK and not Result.NoConnection)
                facepunchSockets.RefusedUnreliableSendCount++;
        }

        /// <summary>
        /// Copies one piece into a pooled message and queues it.
        /// </summary>
        /// <param name="queuedReliableSends">The connection's queue.</param>
        /// <param name="bytes">The array holding the piece.</param>
        /// <param name="offset">Where the piece starts in <paramref name="bytes"/>.</param>
        /// <param name="count">The piece's length, in bytes.</param>
        /// <param name="header">The message's header byte.</param>
        private static void QueuePiece(Queue<QueuedSend> queuedReliableSends, byte[] bytes, int offset, int count, byte header)
        {
            int messageBytes = count + FacepunchMessages.HeaderBytes;
            byte[] message = TrackedArrayPool<byte>.Rent(messageBytes);

            message[0] = header;
            Buffer.BlockCopy(bytes, offset, message, FacepunchMessages.HeaderBytes, count);

            queuedReliableSends.Enqueue(new(message, messageBytes));
        }

        /// <summary>
        /// Hands a connection's queued reliable messages to Steam, oldest first, while they fit in the room left.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <param name="queuedReliableSends">The connection's queued messages.</param>
        /// <param name="budgetBytes">The room left in the send buffer, in bytes; <see cref="int.MaxValue"/> sends them all.</param>
        /// <remarks>One message larger than the whole buffer would never fit, so it goes once Steam's queue is empty, and Steam's refusal reports it rather than the link waiting forever.</remarks>
        private void ReleaseQueuedReliableSends(SteamConnection steamConnection, Queue<QueuedSend> queuedReliableSends, int budgetBytes)
        {
            bool isBufferEmpty = budgetBytes >= SendBufferBytes;

            while (queuedReliableSends.Count > 0)
            {
                QueuedSend queuedSend = queuedReliableSends.Peek();
                int costBytes = queuedSend.MessageBytes + ReliableHeaderAllowanceBytes;

                if (costBytes > budgetBytes && !isBufferEmpty)
                    break;

                queuedReliableSends.Dequeue();

                Result result = steamConnection.SendMessage(queuedSend.Bytes, 0, queuedSend.MessageBytes, ReliableSendType, FacepunchMessages.ReliableLane);

                if (result is not Result.OK and not Result.NoConnection)
                    RefusedReliableSends.Add(new(steamConnection, queuedSend.MessageBytes, result));

                TrackedArrayPool<byte>.Return(queuedSend.Bytes, clearArray: false);

                budgetBytes -= costBytes;
                isBufferEmpty = false;
            }
        }

        /// <summary>
        /// Measures the room left in a connection's send buffer.
        /// </summary>
        /// <param name="steamConnection">The connection.</param>
        /// <returns>The bytes Steam will still queue before it refuses a send.</returns>
        /// <remarks>Reliable bytes sent but not yet acknowledged are counted as well, which can only make the transport wait longer than it has to, never let a send through that Steam refuses.</remarks>
        private int GetSendBudgetBytes(SteamConnection steamConnection)
        {
            ConnectionStatus connectionStatus = steamConnection.QuickStatus();

            return SendBufferBytes - connectionStatus.PendingReliable - connectionStatus.PendingUnreliable - connectionStatus.SentUnackedReliable;
        }

        /// <summary>
        /// A reliable message Steam refused.
        /// </summary>
        public readonly struct RefusedSend
        {
            /// <summary>
            /// The connection the message was addressed to.
            /// </summary>
            public readonly SteamConnection SteamConnection;
            /// <summary>
            /// The length of the message, in bytes.
            /// </summary>
            public readonly int MessageBytes;
            /// <summary>
            /// Why Steam refused it.
            /// </summary>
            public readonly Result Result;

            /// <summary>
            /// Creates a new instance.
            /// </summary>
            /// <param name="steamConnection">The connection the message was addressed to.</param>
            /// <param name="messageBytes">The length of the message, in bytes.</param>
            /// <param name="result">Why Steam refused it.</param>
            public RefusedSend(SteamConnection steamConnection, int messageBytes, Result result)
            {
                SteamConnection = steamConnection;
                MessageBytes = messageBytes;
                Result = result;
            }
        }

        /// <summary>
        /// A reliable message waiting for room in Steam's queue.
        /// </summary>
        private readonly struct QueuedSend
        {
            /// <summary>
            /// The array holding the message, rented from <see cref="TrackedArrayPool{T0}"/>.
            /// </summary>
            public readonly byte[] Bytes;
            /// <summary>
            /// The length of the message, in bytes.
            /// </summary>
            public readonly int MessageBytes;

            /// <summary>
            /// Creates a new instance.
            /// </summary>
            /// <param name="bytes">The array holding the message.</param>
            /// <param name="messageBytes">The length of the message, in bytes.</param>
            public QueuedSend(byte[] bytes, int messageBytes)
            {
                Bytes = bytes;
                MessageBytes = messageBytes;
            }
        }
    }
}
