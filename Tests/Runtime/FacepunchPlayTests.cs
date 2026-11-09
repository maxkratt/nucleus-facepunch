using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CodeBoost.Performance;
using NUnit.Framework;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Messages;
using Nucleus.Serializers;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nucleus.Integrations.Facepunch.Tests
{
    /// <summary>
    /// Play mode coverage of <see cref="FacepunchSockets"/> on a running Steam: a host's own client and a host kicking it, a remote client,
    /// a kick with a reason, a client that disconnects and joins again, and a client that sends past the server's packet limit.
    /// </summary>
    /// <remarks>
    /// Skipped unless the Steam client is running, since there is no mock of Steam. Every peer runs in this process on one account, so
    /// the remote client dials the server by <see cref="FacepunchAddressing.IpAddress"/> on the loopback: a client targeting its own SteamID
    /// would be read as joining its own server. Steam is initialized when nothing else has and its callbacks are pumped by every wait,
    /// since no bootstrap runs here. It is never shut down: <c>SteamAPI_Shutdown</c> called a frame after a case closed its sockets once
    /// deadlocked inside steamclient64, the main thread holding a Steam lock while it waited on a Steam worker thread queued for that same
    /// lock. A later run in the same editor finds Steam still up, with domain reload on or off, and the editor's exit releases it.
    /// </remarks>
    public class FacepunchPlayTests
    {
        /// <summary>
        /// The longest any wait runs before it gives up, in seconds.
        /// </summary>
        private const float MaximumWaitSeconds = 15f;
        /// <summary>
        /// The Steam application the tests run as, Spacewar.
        /// </summary>
        private const uint AppId = 480;
        /// <summary>
        /// The reason the server gives the client it kicks.
        /// </summary>
        private const string KickReason = "Kicked by the Steam play mode test.";
        /// <summary>
        /// The largest client packet the limit case's server accepts, 1 MiB.
        /// </summary>
        private const uint PacketLimitBytes = 1024 * 1024;
        /// <summary>
        /// The length of the payload sent under the limit: past one 512 KiB piece, so it arrives split, and under 1 MiB.
        /// </summary>
        private const int UnderLimitPayloadCharacters = 600 * 1024;
        /// <summary>
        /// The length of the payload sent past the limit, 1.5 MiB.
        /// </summary>
        private const int OverLimitPayloadCharacters = 1536 * 1024;
        /// <summary>
        /// The size of the world state the bulk join case sends, in bytes.
        /// </summary>
        private const int BulkMessageBytes = 50 * 1024 * 1024;
        /// <summary>
        /// The longest the bulk join case waits for its world state to arrive, in seconds.
        /// </summary>
        private const float BulkWaitSeconds = 240f;
        /// <summary>
        /// The send rate floor the bulk join case raises Steam's to, matching the component's default ceiling, in bytes per second.
        /// </summary>
        private const uint RaisedSendRateMinimumBytesPerSecond = 4 * 1024 * 1024;
        /// <summary>
        /// The length of the first client's payload in the per-connection case, distinct so each arrival can be told apart.
        /// </summary>
        private const int FirstClientPayloadCharacters = 100;
        /// <summary>
        /// The length of the second client's payload in the per-connection case.
        /// </summary>
        private const int SecondClientPayloadCharacters = 200;

        /// <summary>
        /// Every GameObject the case created, destroyed at teardown.
        /// </summary>
        private readonly List<GameObject> _createdObjects = new();
        /// <summary>
        /// The reason the kicked client received, or <see langword="null"/> before one arrives.
        /// </summary>
        private string _receivedKickReason;
        /// <summary>
        /// The length of the last payload the server received, or zero before one arrives.
        /// </summary>
        private int _receivedPayloadCharacters;
        /// <summary>
        /// The length of the last bulk message a client received, or zero while none has arrived.
        /// </summary>
        private int _receivedBulkBytes;
        /// <summary>
        /// How many small unreliable messages a client has received.
        /// </summary>
        private int _receivedTickCount;

        /// <summary>
        /// Skips the case unless Steam is running, and initializes Steam unless something already has.
        /// </summary>
        [SetUp]
        public void SetUp()
        {
            if (!FacepunchSockets.IsSteamShutDown())
                return;

            try
            {
                SteamClient.Init(AppId, asyncCallbacks: false);
            }
            catch (Exception exception)
            {
                Assert.Ignore($"Steam would not initialize, and must be running: [{exception.Message}].");
            }
        }

        /// <summary>
        /// Destroys every object the case created, which shuts their peers down, and leaves Steam running.
        /// </summary>
        /// <returns>The coroutine driving the teardown.</returns>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (GameObject createdObject in _createdObjects)
            {
                if (createdObject != null)
                    Object.Destroy(createdObject);
            }

            _createdObjects.Clear();
            _receivedKickReason = null;
            _receivedPayloadCharacters = 0;
            _receivedBulkBytes = 0;
            _receivedTickCount = 0;

            // Lets the destroyed peers close their links while Steam can still send the close.
            yield return null;
            SteamClient.RunCallbacks();
        }

        /// <summary>
        /// A host's own client joins its server in process and is reported as the server's own client.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator Host_OwnClient_JoinsInProcess()
        {
            UnityCoreManager hostCoreManager = NewPeer("SteamHost", out UnityTransportManager hostTransportManager, out FacepunchTransport _);

            yield return WaitForTask(hostTransportManager.StartHostAsync());
            yield return WaitUntil(() => IsClientAuthenticated(hostCoreManager));

            Assert.IsTrue(IsClientAuthenticated(hostCoreManager), "The host's own client never authenticated.");

            FacepunchSockets facepunchSockets = (FacepunchSockets)hostTransportManager.Transports[0];
            Assert.IsTrue(TryGetOwnClientConnection(hostCoreManager, facepunchSockets, out Connection _), "The server does not report the host's client as its own.");
        }

        /// <summary>
        /// A host that kicks its own client with a reason delivers the reason before the client's link drops, keeps its server running,
        /// and pairs its client again when it rejoins.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator Host_KicksOwnClientWithAReason_ClientReceivesTheReasonAndRejoins()
        {
            UnityCoreManager hostCoreManager = NewPeer("SteamHost", out UnityTransportManager hostTransportManager, out FacepunchTransport _);

            yield return WaitForTask(hostTransportManager.StartHostAsync());
            yield return WaitUntil(() => IsClientAuthenticated(hostCoreManager));

            FacepunchSockets facepunchSockets = (FacepunchSockets)hostTransportManager.Transports[0];
            Assert.IsTrue(TryGetOwnClientConnection(hostCoreManager, facepunchSockets, out Connection ownClientConnection), "The server holds no own client to kick.");

            MessageManager hostMessageManager = hostCoreManager.CoreManager.MessageManager;
            hostMessageManager.RegisterMessageHandler<SteamPlayTestKickNotice>(OnKickNoticeReceived);

            try
            {
                ownClientConnection.Kick(new SteamPlayTestKickNotice { Reason = KickReason });

                yield return WaitUntil(() => !IsClientConnected(hostCoreManager));
            }
            finally
            {
                hostMessageManager.UnregisterMessageHandler<SteamPlayTestKickNotice>(OnKickNoticeReceived);
            }

            Assert.IsFalse(IsClientConnected(hostCoreManager), "The kicked own client's link never dropped.");
            Assert.AreEqual(KickReason, _receivedKickReason, "The kick's reason did not reach the host's own client before its link dropped.");
            Assert.IsTrue(hostCoreManager.CoreManager.TransportManager.IsServerStarted, "The server stopped when it kicked its own client.");
            Assert.IsFalse(TryGetOwnClientConnection(hostCoreManager, facepunchSockets, out Connection _), "The server still holds the own client it kicked.");

            yield return WaitForTask(hostTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(hostCoreManager));

            Assert.IsTrue(IsClientAuthenticated(hostCoreManager), "The host's own client never authenticated again after rejoining.");
            Assert.IsTrue(TryGetOwnClientConnection(hostCoreManager, facepunchSockets, out Connection _), "The server does not report the rejoined client as its own.");
        }

        /// <summary>
        /// A client in another peer joins the server through Steam and is admitted under the account's SteamID.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_JoinsByAddress_IsAdmittedUnderItsSteamId()
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _);

            Assert.IsTrue(IsClientAuthenticated(clientCoreManager), "The remote client never authenticated.");
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection remoteClientConnection), "The server holds no remote client.");
            Assert.AreEqual(SteamClient.SteamId.Value.ToString(), remoteClientConnection.RemoteAddress, "The remote client was not admitted under the account's SteamID.");
        }

        /// <summary>
        /// A client joins by IPv6, both to a server bound to the IPv6 loopback and to one listening on every interface, and by IPv4 to the
        /// latter, so one server serves both families.
        /// </summary>
        /// <param name="addressPair">The server's bind address, then the host the client dials.</param>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_JoinsByEitherAddressFamily_IsAdmitted([ValueSource(nameof(GetAddressPairs))] string[] addressPair)
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _, bindAddress: addressPair[0], clientHost: addressPair[1]);

            Assert.IsTrue(IsClientAuthenticated(clientCoreManager), $"The client dialling [{addressPair[1]}] never authenticated with a server bound to [{addressPair[0]}].");
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection _), "The server holds no remote client.");
        }

        /// <summary>
        /// The server knows each client's SteamID, a client knows its own, and a client learns the others' and their leaving only while sharing is on.
        /// </summary>
        /// <param name="isShareSteamIdsEnabled">Whether the server shares SteamIDs.</param>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator Clients_JoinAndLeave_SteamIdsReachOnlyThePeersAllowedToKnow([Values(true, false)] bool isShareSteamIdsEnabled)
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager firstClientCoreManager, out UnityTransportManager firstClientTransportManager, isShareSteamIdsEnabled: isShareSteamIdsEnabled);

            ulong accountSteamId = SteamClient.SteamId.Value;
            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverCoreManager.GetComponent<UnityTransportManager>().Transports[0];
            FacepunchSockets firstClientFacepunchSockets = (FacepunchSockets)firstClientTransportManager.Transports[0];
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection firstRemoteClientConnection), "The server holds no remote client.");
            uint firstClientId = firstRemoteClientConnection.Id;

            Assert.IsTrue(serverFacepunchSockets.TryGetSteamId(firstRemoteClientConnection, out ulong serverReadSteamId), "The server does not know its client's SteamID.");
            Assert.AreEqual(accountSteamId, serverReadSteamId, "The server read the wrong SteamID.");
            Assert.IsTrue(serverFacepunchSockets.TryGetConnection(accountSteamId, out Connection foundConnection), "The server could not find the client by SteamID.");
            Assert.AreEqual(firstClientId, foundConnection.Id, "The server found the wrong client by SteamID.");
            Assert.IsTrue(firstClientFacepunchSockets.TryGetSteamId(firstClientId, out ulong ownSteamId), "The client does not know its own SteamID.");
            Assert.AreEqual(accountSteamId, ownSteamId, "The client read the wrong SteamID for itself.");

            uint secondClientId = 0;
            bool isSecondClientForgotten = false;
            firstClientFacepunchSockets.SteamIdChanged += (connectionId, steamId) =>
            {
                if (connectionId == firstClientId)
                    return;

                if (steamId is FacepunchSockets.UnsetSteamId)
                    isSecondClientForgotten = true;
                else
                    secondClientId = connectionId;
            };

            UnityCoreManager secondClientCoreManager = NewPeer("SteamSecondClient", out UnityTransportManager secondClientTransportManager, out FacepunchTransport secondClientFacepunchTransport);
            secondClientFacepunchTransport.ServerAddress = $"127.0.0.1:{serverFacepunchSockets.Configuration.Port}";

            yield return WaitForTask(secondClientTransportManager.EnsureAddedAsync());

            FacepunchSockets secondClientFacepunchSockets = (FacepunchSockets)secondClientTransportManager.Transports[0];
            secondClientFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;

            yield return WaitForTask(secondClientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(secondClientCoreManager));
            Assert.IsTrue(IsClientAuthenticated(secondClientCoreManager), "The second client never authenticated.");

            if (!isShareSteamIdsEnabled)
            {
                yield return new WaitForSeconds(1f);

                Assert.AreEqual(0u, secondClientId, "A client learned another client's SteamID with sharing off.");
                Assert.IsFalse(secondClientFacepunchSockets.TryGetSteamId(firstClientId, out ulong _), "The second client learned the first client's SteamID with sharing off.");

                yield break;
            }

            yield return WaitUntil(() => secondClientId != 0 && secondClientFacepunchSockets.TryGetSteamId(firstClientId, out ulong _));

            Assert.IsTrue(firstClientFacepunchSockets.TryGetSteamId(secondClientId, out ulong sharedSteamId), "The first client never learned the second client's SteamID.");
            Assert.AreEqual(accountSteamId, sharedSteamId, "The first client learned the wrong SteamID.");
            Assert.IsTrue(secondClientFacepunchSockets.TryGetSteamId(firstClientId, out ulong _), "The late client never learned the earlier client's SteamID.");

            yield return WaitForTask(secondClientTransportManager.Transports[0].DisconnectLocalConnectionAsync(Invoker.Client));
            yield return WaitUntil(() => isSecondClientForgotten);

            Assert.IsTrue(isSecondClientForgotten, "The first client was never told the second client left.");
            Assert.IsFalse(firstClientFacepunchSockets.TryGetSteamId(secondClientId, out ulong _), "The first client still holds the SteamID of a client that left.");
        }

        /// <summary>
        /// A client kicked with a reason receives the reason before its link drops.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_KickedWithAReason_ReceivesTheReasonBeforeTheDrop()
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _);
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection remoteClientConnection), "The server holds no remote client to kick.");

            MessageManager clientMessageManager = clientCoreManager.CoreManager.MessageManager;
            clientMessageManager.RegisterMessageHandler<SteamPlayTestKickNotice>(OnKickNoticeReceived);

            try
            {
                remoteClientConnection.Kick(new SteamPlayTestKickNotice { Reason = KickReason });

                yield return WaitUntil(() => !IsClientConnected(clientCoreManager));
            }
            finally
            {
                clientMessageManager.UnregisterMessageHandler<SteamPlayTestKickNotice>(OnKickNoticeReceived);
            }

            Assert.IsFalse(IsClientConnected(clientCoreManager), "The kicked client's link never dropped.");
            Assert.AreEqual(KickReason, _receivedKickReason, "The kick's reason did not reach the client before its link dropped.");

            yield return WaitUntil(() => !TryGetRemoteClientConnection(serverCoreManager, out Connection _));
            Assert.IsFalse(TryGetRemoteClientConnection(serverCoreManager, out Connection _), "The server still holds the kicked client.");
        }

        /// <summary>
        /// A client that disconnects is released by the server, and joins again on the same transport.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_DisconnectsAndRejoins_IsAdmittedAgain()
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager clientTransportManager);
            Assert.IsTrue(IsClientAuthenticated(clientCoreManager), "The remote client never authenticated.");

            Transport clientTransport = clientTransportManager.Transports[0];

            yield return WaitForTask(clientTransport.DisconnectLocalConnectionAsync(Invoker.Client));
            yield return WaitUntil(() => !IsClientConnected(clientCoreManager) && !TryGetRemoteClientConnection(serverCoreManager, out Connection _));

            Assert.IsFalse(IsClientConnected(clientCoreManager), "The client was still connected after disconnecting.");
            Assert.IsFalse(TryGetRemoteClientConnection(serverCoreManager, out Connection _), "The server still holds the client that disconnected.");

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientCoreManager) && TryGetRemoteClientConnection(serverCoreManager, out Connection _));

            Assert.IsTrue(IsClientAuthenticated(clientCoreManager), "The client never authenticated again after rejoining.");
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection _), "The server does not hold the rejoined client.");
        }

        /// <summary>
        /// A client's split reliable message under the server's packet limit arrives whole, and one past it gets the client disconnected.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_SendsPacketPastTheLimit_IsDisconnected()
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _);
            Assert.IsTrue(clientCoreManager.CoreManager.TransportManager.TryGetServerConnection(out Connection serverConnection), "The client holds no server connection.");

            // Read on every receive, so it applies to the running server.
            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverCoreManager.GetComponent<UnityTransportManager>().Transports[0];
            serverFacepunchSockets.MaximumClientPacketBytes = PacketLimitBytes;

            MessageManager serverMessageManager = serverCoreManager.CoreManager.MessageManager;
            serverMessageManager.RegisterMessageHandler<SteamPlayTestPayload>(OnPayloadReceived);

            try
            {
                serverConnection.SendMessage(Channel.Reliable, new SteamPlayTestPayload { Text = new string('x', UnderLimitPayloadCharacters) });

                yield return WaitUntil(() => _receivedPayloadCharacters > 0);
                Assert.AreEqual(UnderLimitPayloadCharacters, _receivedPayloadCharacters, "A split packet under the limit did not arrive whole.");

                serverConnection.SendMessage(Channel.Reliable, new SteamPlayTestPayload { Text = new string('x', OverLimitPayloadCharacters) });

                yield return WaitUntil(() => !IsClientConnected(clientCoreManager) && !TryGetRemoteClientConnection(serverCoreManager, out Connection _));
            }
            finally
            {
                serverMessageManager.UnregisterMessageHandler<SteamPlayTestPayload>(OnPayloadReceived);
            }

            Assert.IsFalse(TryGetRemoteClientConnection(serverCoreManager, out Connection _), "The server still holds the client that went past the limit.");
            Assert.IsFalse(IsClientConnected(clientCoreManager), "The client's link never dropped after it went past the limit.");
            Assert.AreEqual(UnderLimitPayloadCharacters, _receivedPayloadCharacters, "The packet past the limit reached the server.");
            Assert.IsTrue(serverCoreManager.CoreManager.TransportManager.IsServerStarted, "The server stopped when it dropped the client.");
        }

        /// <summary>
        /// A client that joins is sent a 50 MiB reliable world state at once, and it arrives whole while unreliable messages keep arriving.
        /// </summary>
        /// <param name="latencyMilliseconds">The latency Steam simulates on every send, which in one process delays both directions.</param>
        /// <param name="isSendRateFloorRaised">True to raise Steam's send rate floor to the component's default ceiling.</param>
        /// <returns>The coroutine driving the case.</returns>
        /// <remarks>
        /// Explicit, since it moves 50 MiB and takes up to a minute per case. It logs the time to arrive, Steam's send rate, the peak
        /// bytes waiting in Steam and the peak managed memory of both peers, which share this process.
        /// </remarks>
        [UnityTest]
        [Explicit]
        [Timeout(300000)]
        public IEnumerator RemoteClient_JoinsAndIsSentABulkWorldState_ArrivesWholeWhileStateFlows([Values(0u, 100u)] uint latencyMilliseconds, [Values(false, true)] bool isSendRateFloorRaised)
        {
            uint sendRateMinimumBytesPerSecond = isSendRateFloorRaised ? RaisedSendRateMinimumBytesPerSecond : FacepunchSockets.UnsetSendSetting;

            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _, sendRateMinimumBytesPerSecond);
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection remoteClientConnection), "The server holds no remote client.");

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverCoreManager.GetComponent<UnityTransportManager>().Transports[0];
            serverFacepunchSockets.SetNetworkConditions(new NetworkConditions { LatencyMilliseconds = latencyMilliseconds });

            MessageManager clientMessageManager = clientCoreManager.CoreManager.MessageManager;
            clientMessageManager.RegisterMessageHandler<SteamPlayTestBulkMessage>(OnBulkMessageReceived);
            clientMessageManager.RegisterMessageHandler<SteamPlayTestPayload>(OnTickReceived);

            byte[] bulkBytes = new byte[BulkMessageBytes];
            long baselineMemoryBytes = GC.GetTotalMemory(forceFullCollection: true);
            long peakMemoryBytes = baselineMemoryBytes;
            uint peakPendingReliableBytes = 0;
            float peakSendRateBytesPerSecond = 0f;
            int ticksBeforeArrival = 0;
            int frameCount = 0;
            float startSeconds = Time.realtimeSinceStartup;

            try
            {
                remoteClientConnection.SendMessage(Channel.Reliable, new SteamPlayTestBulkMessage { Bytes = new(bulkBytes) });

                float deadlineSeconds = startSeconds + BulkWaitSeconds;

                while (_receivedBulkBytes == 0 && IsClientConnected(clientCoreManager) && Time.realtimeSinceStartup < deadlineSeconds)
                {
                    remoteClientConnection.SendMessage(Channel.Unreliable, new SteamPlayTestPayload { Text = "tick" });
                    SteamClient.RunCallbacks();

                    peakMemoryBytes = Math.Max(peakMemoryBytes, GC.GetTotalMemory(forceFullCollection: false));

                    // A status read is a native call, so it is read every tenth frame.
                    if (frameCount++ % 10 == 0 && serverFacepunchSockets.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus linkStatus))
                    {
                        peakPendingReliableBytes = Math.Max(peakPendingReliableBytes, linkStatus.PendingReliableBytes);
                        peakSendRateBytesPerSecond = Math.Max(peakSendRateBytesPerSecond, linkStatus.OutBytesPerSecond);
                    }

                    ticksBeforeArrival = _receivedTickCount;

                    yield return null;
                }
            }
            finally
            {
                clientMessageManager.UnregisterMessageHandler<SteamPlayTestBulkMessage>(OnBulkMessageReceived);
                clientMessageManager.UnregisterMessageHandler<SteamPlayTestPayload>(OnTickReceived);
                serverFacepunchSockets.SetNetworkConditions(networkConditions: null);
            }

            float arrivalSeconds = Time.realtimeSinceStartup - startSeconds;
            Debug.Log($"[{nameof(FacepunchPlayTests)}] bulk join at [{latencyMilliseconds}] ms, floor raised [{isSendRateFloorRaised}]: [{_receivedBulkBytes}] bytes in [{arrivalSeconds:F2}] s over [{frameCount}] frames, peak send rate [{peakSendRateBytesPerSecond}] B/s, peak pending reliable [{peakPendingReliableBytes}] B, peak managed memory [{(peakMemoryBytes - baselineMemoryBytes) / (1024 * 1024)}] MiB over the baseline, [{ticksBeforeArrival}] unreliable messages received during the transfer, refused reliable [{serverFacepunchSockets.RefusedReliableSendCount}], refused unreliable [{serverFacepunchSockets.RefusedUnreliableSendCount}].");

            Assert.AreEqual(BulkMessageBytes, _receivedBulkBytes, "The world state did not arrive whole.");
            Assert.IsTrue(IsClientConnected(clientCoreManager), "The client's link dropped during the transfer.");
            Assert.AreEqual(0, serverFacepunchSockets.RefusedReliableSendCount, "Steam refused a reliable send during the transfer.");
            Assert.Greater(ticksBeforeArrival, 0, "No unreliable message reached the client during the transfer.");
        }

        /// <summary>
        /// Two clients of a per-connection receive server each reach it, and the one left after the other disconnects still does.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator PerConnectionReceive_TwoClients_BothReachTheServerAndOneSurvivesTheOtherLeaving()
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager firstClientCoreManager, out UnityTransportManager _, isPerConnectionReceiveEnabled: true);
            Assert.IsTrue(firstClientCoreManager.CoreManager.TransportManager.TryGetServerConnection(out Connection firstServerConnection), "The first client holds no server connection.");

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverCoreManager.GetComponent<UnityTransportManager>().Transports[0];

            UnityCoreManager secondClientCoreManager = NewPeer("SteamSecondClient", out UnityTransportManager secondClientTransportManager, out FacepunchTransport secondClientFacepunchTransport);
            secondClientFacepunchTransport.ServerAddress = $"127.0.0.1:{serverFacepunchSockets.Configuration.Port}";

            yield return WaitForTask(secondClientTransportManager.EnsureAddedAsync());

            ((FacepunchSockets)secondClientTransportManager.Transports[0]).Addressing = FacepunchAddressing.IpAddress;

            yield return WaitForTask(secondClientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(secondClientCoreManager));
            Assert.IsTrue(IsClientAuthenticated(secondClientCoreManager), "The second client never authenticated.");
            Assert.IsTrue(secondClientCoreManager.CoreManager.TransportManager.TryGetServerConnection(out Connection secondServerConnection), "The second client holds no server connection.");

            MessageManager serverMessageManager = serverCoreManager.CoreManager.MessageManager;
            serverMessageManager.RegisterMessageHandler<SteamPlayTestPayload>(OnPayloadReceived);

            try
            {
                firstServerConnection.SendMessage(Channel.Reliable, new SteamPlayTestPayload { Text = new string('x', FirstClientPayloadCharacters) });

                yield return WaitUntil(() => _receivedPayloadCharacters == FirstClientPayloadCharacters);
                Assert.AreEqual(FirstClientPayloadCharacters, _receivedPayloadCharacters, "The first client's message never reached the server.");

                secondServerConnection.SendMessage(Channel.Reliable, new SteamPlayTestPayload { Text = new string('x', SecondClientPayloadCharacters) });

                yield return WaitUntil(() => _receivedPayloadCharacters == SecondClientPayloadCharacters);
                Assert.AreEqual(SecondClientPayloadCharacters, _receivedPayloadCharacters, "The second client's message never reached the server.");

                yield return WaitForTask(secondClientTransportManager.Transports[0].DisconnectLocalConnectionAsync(Invoker.Client));
                yield return WaitUntil(() => serverCoreManager.CoreManager.TransportManager.ActiveConnections.Count == 1);
                Assert.AreEqual(1, serverCoreManager.CoreManager.TransportManager.ActiveConnections.Count, "The server still holds the client that left.");

                firstServerConnection.SendMessage(Channel.Reliable, new SteamPlayTestPayload { Text = new string('x', UnderLimitPayloadCharacters) });

                yield return WaitUntil(() => _receivedPayloadCharacters == UnderLimitPayloadCharacters);
                Assert.AreEqual(UnderLimitPayloadCharacters, _receivedPayloadCharacters, "The remaining client's split message did not arrive whole after the other client left.");
            }
            finally
            {
                serverMessageManager.UnregisterMessageHandler<SteamPlayTestPayload>(OnPayloadReceived);
            }
        }

        /// <summary>
        /// Records the reason a kicked client received.
        /// </summary>
        /// <param name="messageContext">The context of the message.</param>
        /// <param name="steamPlayTestKickNotice">The notice.</param>
        private void OnKickNoticeReceived(in MessageContext messageContext, SteamPlayTestKickNotice steamPlayTestKickNotice) => _receivedKickReason = steamPlayTestKickNotice.Reason;

        /// <summary>
        /// Records the length of the last payload the server received.
        /// </summary>
        /// <param name="messageContext">The context of the message.</param>
        /// <param name="steamPlayTestPayload">The payload.</param>
        private void OnPayloadReceived(in MessageContext messageContext, SteamPlayTestPayload steamPlayTestPayload) => _receivedPayloadCharacters = steamPlayTestPayload.Text.Length;

        /// <summary>
        /// Records the length of a bulk message a client received, then returns its pooled array.
        /// </summary>
        /// <param name="messageContext">The context the message arrived in.</param>
        /// <param name="steamPlayTestBulkMessage">The message.</param>
        private void OnBulkMessageReceived(in MessageContext messageContext, SteamPlayTestBulkMessage steamPlayTestBulkMessage)
        {
            _receivedBulkBytes = steamPlayTestBulkMessage.Bytes.Count;

            if (messageContext.IsDeserialized && steamPlayTestBulkMessage.Bytes.Array is not null)
                TrackedArrayPool<byte>.Return(steamPlayTestBulkMessage.Bytes.Array);
        }

        /// <summary>
        /// Counts a small unreliable message a client received.
        /// </summary>
        /// <param name="messageContext">The context the message arrived in.</param>
        /// <param name="steamPlayTestPayload">The message.</param>
        private void OnTickReceived(in MessageContext messageContext, SteamPlayTestPayload steamPlayTestPayload) => _receivedTickCount++;

        /// <summary>
        /// Starts a server peer listening on a loopback port, then a client peer that joins it by address, and waits for the join.
        /// </summary>
        /// <param name="serverCoreManager">The server peer.</param>
        /// <param name="clientCoreManager">The client peer.</param>
        /// <param name="clientTransportManager">The client peer's transport manager.</param>
        /// <param name="sendRateMinimumBytesPerSecond">The send rate floor the server gives its connections, or <see cref="FacepunchSockets.UnsetSendSetting"/> for Steam's.</param>
        /// <param name="isShareSteamIdsEnabled">Whether the server shares SteamIDs with its clients.</param>
        /// <param name="isPerConnectionReceiveEnabled">Whether the server receives each client through its own call.</param>
        /// <param name="bindAddress">The address the server listens on.</param>
        /// <param name="clientHost">The host the client dials, with the port added, an IPv6 one written in brackets.</param>
        /// <returns>The coroutine driving the start.</returns>
        private IEnumerator StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager clientTransportManager, uint sendRateMinimumBytesPerSecond = FacepunchSockets.UnsetSendSetting, bool isShareSteamIdsEnabled = false, bool isPerConnectionReceiveEnabled = false, string bindAddress = "localhost", string clientHost = "127.0.0.1")
        {
            ushort port = ProbeFreeUdpPort();

            serverCoreManager = NewPeer("SteamServer", out UnityTransportManager serverTransportManager, out FacepunchTransport serverFacepunchTransport);
            serverFacepunchTransport.BindAddress = bindAddress;
            clientCoreManager = NewPeer("SteamClient", out clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.ServerAddress = $"{clientHost}:{port}";

            return StartServerAndRemoteClient(serverCoreManager, serverTransportManager, clientCoreManager, clientTransportManager, port, sendRateMinimumBytesPerSecond, isShareSteamIdsEnabled, isPerConnectionReceiveEnabled);
        }

        /// <summary>
        /// Starts the server on <paramref name="port"/> by IP address, then the client, and waits until the client is admitted.
        /// </summary>
        /// <param name="serverCoreManager">The server peer.</param>
        /// <param name="serverTransportManager">The server peer's transport manager.</param>
        /// <param name="clientCoreManager">The client peer.</param>
        /// <param name="clientTransportManager">The client peer's transport manager.</param>
        /// <param name="port">The loopback port the server listens on.</param>
        /// <param name="sendRateMinimumBytesPerSecond">The send rate floor the server gives its connections, or <see cref="FacepunchSockets.UnsetSendSetting"/> for Steam's.</param>
        /// <param name="isShareSteamIdsEnabled">Whether the server shares SteamIDs with its clients.</param>
        /// <param name="isPerConnectionReceiveEnabled">Whether the server receives each client through its own call.</param>
        /// <returns>The coroutine driving the start.</returns>
        /// <remarks>The component carries no addressing setter, so the engine transport each start builds is switched to IP addressing before it starts.</remarks>
        private static IEnumerator StartServerAndRemoteClient(UnityCoreManager serverCoreManager, UnityTransportManager serverTransportManager, UnityCoreManager clientCoreManager, UnityTransportManager clientTransportManager, ushort port, uint sendRateMinimumBytesPerSecond, bool isShareSteamIdsEnabled, bool isPerConnectionReceiveEnabled)
        {
            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverTransportManager.Transports[0];
            serverFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            serverFacepunchSockets.SetPort(port);
            serverFacepunchSockets.SendRateMinimumBytesPerSecond = sendRateMinimumBytesPerSecond;
            serverFacepunchSockets.ShareSteamIdsEnabled = isShareSteamIdsEnabled;
            serverFacepunchSockets.PerConnectionReceiveEnabled = isPerConnectionReceiveEnabled;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitUntil(() => serverCoreManager.CoreManager.TransportManager.IsServerStarted);
            Assert.IsTrue(serverCoreManager.CoreManager.TransportManager.IsServerStarted, "The server never started.");

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            FacepunchSockets clientFacepunchSockets = (FacepunchSockets)clientTransportManager.Transports[0];
            clientFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientCoreManager) && TryGetRemoteClientConnection(serverCoreManager, out Connection _));
        }

        /// <summary>
        /// Creates an active peer carrying a core manager, a transport manager and a <see cref="FacepunchTransport"/>, owned by the case.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="unityTransportManager">The peer's transport manager.</param>
        /// <param name="facepunchTransport">The peer's Steam transport component.</param>
        /// <returns>The peer's core manager.</returns>
        private UnityCoreManager NewPeer(string objectName, out UnityTransportManager unityTransportManager, out FacepunchTransport facepunchTransport)
        {
            GameObject peerObject = new(objectName);
            peerObject.SetActive(false);
            _createdObjects.Add(peerObject);

            UnityCoreManager unityCoreManager = peerObject.AddComponent<UnityCoreManager>();
            unityTransportManager = peerObject.AddComponent<UnityTransportManager>();
            facepunchTransport = peerObject.AddComponent<FacepunchTransport>();
            peerObject.SetActive(true);

            return unityCoreManager;
        }

        /// <summary>
        /// Returns whether a peer's local client is connected.
        /// </summary>
        /// <param name="unityCoreManager">The peer to ask.</param>
        /// <returns><see langword="true"/> while its client is connected.</returns>
        private static bool IsClientConnected(UnityCoreManager unityCoreManager) =>
            unityCoreManager.CoreManager.TransportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.LocalState is LocalConnectionState.Connected;

        /// <summary>
        /// Returns whether a peer's local client is connected and authenticated.
        /// </summary>
        /// <param name="unityCoreManager">The peer to ask.</param>
        /// <returns><see langword="true"/> while its client is authenticated.</returns>
        private static bool IsClientAuthenticated(UnityCoreManager unityCoreManager) =>
            unityCoreManager.CoreManager.TransportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection is { LocalState: LocalConnectionState.Connected, IsAuthenticated: true };

        /// <summary>
        /// Finds the server's connection for its own host client.
        /// </summary>
        /// <param name="serverCoreManager">The server peer.</param>
        /// <param name="facepunchSockets">The server's transport.</param>
        /// <param name="ownClientConnection">The connection, when found.</param>
        /// <returns><see langword="true"/> when the server holds its own client.</returns>
        private static bool TryGetOwnClientConnection(UnityCoreManager serverCoreManager, FacepunchSockets facepunchSockets, out Connection ownClientConnection)
        {
            foreach (Connection connection in serverCoreManager.CoreManager.TransportManager.ActiveConnections)
            {
                if (!facepunchSockets.IsOwnClientConnection(connection))
                    continue;

                ownClientConnection = connection;

                return true;
            }

            ownClientConnection = null;

            return false;
        }

        /// <summary>
        /// Finds the server's connection for a remote client.
        /// </summary>
        /// <param name="serverCoreManager">The server peer, which runs no client of its own.</param>
        /// <param name="remoteClientConnection">The connection, when found.</param>
        /// <returns><see langword="true"/> when the server holds a remote client.</returns>
        private static bool TryGetRemoteClientConnection(UnityCoreManager serverCoreManager, out Connection remoteClientConnection)
        {
            foreach (Connection connection in serverCoreManager.CoreManager.TransportManager.ActiveConnections)
            {
                remoteClientConnection = connection;

                return true;
            }

            remoteClientConnection = null;

            return false;
        }

        /// <summary>
        /// Lists the address pairs the family case runs: the server's bind address, then the host the client dials.
        /// </summary>
        /// <returns>The pairs.</returns>
        private static IEnumerable<string[]> GetAddressPairs()
        {
            yield return new[] { "::1", "[::1]" };
            yield return new[] { string.Empty, "[::1]" };
            yield return new[] { string.Empty, "127.0.0.1" };
        }

        /// <summary>
        /// Returns a free loopback UDP port for the server to listen on.
        /// </summary>
        /// <returns>The port.</returns>
        /// <remarks>The probe is closed before the port is used, so the server races anything else on the machine for it.</remarks>
        private static ushort ProbeFreeUdpPort()
        {
            using (UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0)))
                return (ushort)((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }

        /// <summary>
        /// Waits for a transport Task while pumping Steam's callbacks, failing the case rather than hanging when it does not complete.
        /// </summary>
        /// <param name="task">The task to wait on.</param>
        /// <returns>The coroutine driving the wait.</returns>
        private static IEnumerator WaitForTask(Task task)
        {
            yield return WaitUntil(() => task.IsCompleted);

            if (task.IsFaulted)
                throw task.Exception;
            if (!task.IsCompleted)
                throw new TimeoutException($"A transport Task did not complete within {MaximumWaitSeconds} s.");
        }

        /// <summary>
        /// Yields frames, pumping Steam's callbacks each one, until a condition holds or the wait runs out.
        /// </summary>
        /// <param name="condition">The condition waited for.</param>
        /// <returns>The coroutine driving the wait.</returns>
        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            float deadlineSeconds = Time.realtimeSinceStartup + MaximumWaitSeconds;

            while (!condition() && Time.realtimeSinceStartup < deadlineSeconds)
            {
                SteamClient.RunCallbacks();

                yield return null;
            }
        }
    }

    /// <summary>
    /// The reason the play mode test's server gives a client it kicks.
    /// </summary>
    [NetworkType]
    public struct SteamPlayTestKickNotice : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The reason the server gave for the kick.
        /// </summary>
        public string Reason;
    }

    /// <summary>
    /// A payload a play mode test's client sends its server, sized to fall either side of the server's packet limit.
    /// </summary>
    [NetworkType]
    public struct SteamPlayTestPayload : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The text carried, whose length sets the packet's size.
        /// </summary>
        public string Text;
    }

    /// <summary>
    /// A large reliable world state a play mode test's server sends a client as it joins.
    /// </summary>
    [NetworkType]
    public struct SteamPlayTestBulkMessage : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The bytes carried, which the receiver reads into an array rented from <c>TrackedArrayPool</c> and returns.
        /// </summary>
        public ArraySegment<byte> Bytes;
    }
}
