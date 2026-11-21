using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CodeBoost.Performance;
using NUnit.Framework;
using Nucleus.Components;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.Server;
using Nucleus.Managers.Transports;
using Nucleus.Serializers;
using Nucleus.Systems;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nucleus.Integrations.Facepunch.Tests
{
    /// <summary>
    /// Play mode coverage of <see cref="FacepunchSockets"/> on a running Steam: a host's own client and a host kicking it, a remote client,
    /// a kick with a reason, a client that disconnects and joins again, a client that sends past the server's packet limit, reliable and
    /// unreliable messages sent both ways, each arriving on its own channel and the reliable ones in order, link status and simulated
    /// latency, the connecting filter, launch arguments, an unreliable message overtaking a reliable burst, and late joins over wide values.
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
        /// The send rate floor the bulk join case raises Steam's to, matching the component's default ceiling, in kilobytes per second.
        /// </summary>
        private const uint RaisedSendRateMinimumKilobytesPerSecond = 4096;
        /// <summary>
        /// The length of the first client's payload in the per-connection case, distinct so each arrival can be told apart.
        /// </summary>
        private const int FirstClientPayloadCharacters = 100;
        /// <summary>
        /// The length of the second client's payload in the per-connection case.
        /// </summary>
        private const int SecondClientPayloadCharacters = 200;
        /// <summary>
        /// How many reliable and how many unreliable messages the channel case sends in each direction.
        /// </summary>
        private const int SequencedMessageCount = 500;
        /// <summary>
        /// The latency the simulated conditions cases set, in milliseconds.
        /// </summary>
        private const uint SimulatedLatencyMilliseconds = 100;
        /// <summary>
        /// The size of the reliable burst an unreliable message is sent behind, in bytes.
        /// </summary>
        private const int BurstBytes = 400_000;
        /// <summary>
        /// The send rate both peers of the burst case keep to, in kilobytes per second, so the burst takes about half a second to leave.
        /// </summary>
        private const uint BurstSendRateKilobytesPerSecond = 768;
        /// <summary>
        /// How many systems the server holds before the late client joins.
        /// </summary>
        private const int LateJoinSystemCount = 50;
        /// <summary>
        /// The width of each late join system's label, in kibibytes.
        /// </summary>
        private const int LabelKibibytes = 64;
        /// <summary>
        /// How many times the world state's own size the server may send for a late join.
        /// </summary>
        private const double MaximumSentToStateRatio = 1.5d;
        /// <summary>
        /// The longest a late join may take, in seconds, many times what the defaults need to carry the state once.
        /// </summary>
        private const float LateJoinSeconds = 60f;
        /// <summary>
        /// An object pacing budget under Steam's own send rate at the default tick rate of thirty, with room for the ordinary traffic beside it.
        /// </summary>
        private const byte BudgetUnderSteamsOwnRateKilobytes = 7;
        /// <summary>
        /// Steam's own send rate, both its floor and its ceiling, in bytes per second.
        /// </summary>
        private const int SteamsOwnSendRateBytesPerSecond = 256 * 1024;

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
        /// The indices of the sequenced messages received, in arrival order, in four buckets: client to server reliable, client to server
        /// unreliable, server to client reliable, then server to client unreliable.
        /// </summary>
        private readonly List<int>[] _receivedSequencedIndices = { new(), new(), new(), new() };
        /// <summary>
        /// How many sequenced messages arrived on a channel other than the one they were sent on.
        /// </summary>
        private int _sequencedChannelMismatchCount;
        /// <summary>
        /// The SteamID the connecting filter was last asked about, or <see cref="FacepunchSockets.UnsetSteamId"/> before it is asked.
        /// </summary>
        private ulong _filteredSteamId;
        /// <summary>
        /// Whether the connecting filter lets clients in.
        /// </summary>
        private bool _isFilteredClientAllowed;
        /// <summary>
        /// When the reliable burst arrived, in seconds since startup, or below zero before it arrives.
        /// </summary>
        private float _burstArrivedSeconds = -1f;
        /// <summary>
        /// When the unreliable probe arrived, in seconds since startup, or below zero before it arrives.
        /// </summary>
        private float _probeArrivedSeconds = -1f;

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
            _sequencedChannelMismatchCount = 0;
            _filteredSteamId = FacepunchSockets.UnsetSteamId;
            _isFilteredClientAllowed = false;
            _burstArrivedSeconds = -1f;
            _probeArrivedSeconds = -1f;

            foreach (List<int> receivedIndices in _receivedSequencedIndices)
                receivedIndices.Clear();

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
            secondClientFacepunchTransport.RemoteAddress = $"127.0.0.1:{serverFacepunchSockets.Configuration.Port}";

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
            uint sendRateMinimumKilobytesPerSecond = isSendRateFloorRaised ? RaisedSendRateMinimumKilobytesPerSecond : FacepunchSockets.UnsetSendSetting;

            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _, sendRateMinimumKilobytesPerSecond);
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
            secondClientFacepunchTransport.RemoteAddress = $"127.0.0.1:{serverFacepunchSockets.Configuration.Port}";

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
        /// Reliable and unreliable messages sent both ways between a client and its server each arrive on the channel they were sent on, and
        /// the reliable ones arrive in the order they were sent.
        /// </summary>
        /// <param name="isHostOwnClient">True to run the client as the host's own client, false for a remote client.</param>
        /// <param name="isPerConnectionReceiveEnabled">Whether the server receives each client through its own call.</param>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator Channels_SentBothWays_EachArrivesOnItsChannelAndReliableInOrder([Values(false, true)] bool isHostOwnClient, [Values(false, true)] bool isPerConnectionReceiveEnabled)
        {
            UnityCoreManager serverCoreManager;
            UnityCoreManager clientCoreManager;
            Connection clientToServerConnection;
            Connection serverToClientConnection;

            if (isHostOwnClient)
            {
                serverCoreManager = NewPeer("SteamHost", out UnityTransportManager hostTransportManager, out FacepunchTransport _);
                clientCoreManager = serverCoreManager;

                yield return WaitForTask(hostTransportManager.EnsureAddedAsync());

                FacepunchSockets hostFacepunchSockets = (FacepunchSockets)hostTransportManager.Transports[0];
                hostFacepunchSockets.PerConnectionReceiveEnabled = isPerConnectionReceiveEnabled;

                yield return WaitForTask(hostTransportManager.StartHostAsync());
                yield return WaitUntil(() => IsClientAuthenticated(serverCoreManager));

                Assert.IsTrue(IsClientAuthenticated(serverCoreManager), "The host's own client never authenticated.");
                Assert.IsTrue(serverCoreManager.CoreManager.TransportManager.TryGetServerConnection(out clientToServerConnection), "The host's client holds no server connection.");
                Assert.IsTrue(TryGetOwnClientConnection(serverCoreManager, hostFacepunchSockets, out serverToClientConnection), "The server does not report the host's client as its own.");
            }
            else
            {
                yield return StartServerAndRemoteClient(out serverCoreManager, out clientCoreManager, out UnityTransportManager _, isPerConnectionReceiveEnabled: isPerConnectionReceiveEnabled);

                Assert.IsTrue(clientCoreManager.CoreManager.TransportManager.TryGetServerConnection(out clientToServerConnection), "The client holds no server connection.");
                Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out serverToClientConnection), "The server holds no remote client.");
            }

            MessageManager serverMessageManager = serverCoreManager.CoreManager.MessageManager;
            MessageManager clientMessageManager = clientCoreManager.CoreManager.MessageManager;
            serverMessageManager.RegisterMessageHandler<SteamPlayTestSequencedMessage>(OnSequencedMessageReceived);

            // A host's own client shares the server's message manager, so the one handler serves both directions.
            if (!isHostOwnClient)
                clientMessageManager.RegisterMessageHandler<SteamPlayTestSequencedMessage>(OnSequencedMessageReceived);

            try
            {
                for (int i = 0; i < SequencedMessageCount; i++)
                {
                    clientToServerConnection.SendMessage(Channel.Reliable, new SteamPlayTestSequencedMessage { Index = i, IsSentReliable = true });
                    clientToServerConnection.SendMessage(Channel.Unreliable, new SteamPlayTestSequencedMessage { Index = i, IsSentReliable = false });
                    serverToClientConnection.SendMessage(Channel.Reliable, new SteamPlayTestSequencedMessage { Index = i, IsSentReliable = true });
                    serverToClientConnection.SendMessage(Channel.Unreliable, new SteamPlayTestSequencedMessage { Index = i, IsSentReliable = false });
                }

                yield return WaitUntil(AreSequencedBucketsFull);
            }
            finally
            {
                serverMessageManager.UnregisterMessageHandler<SteamPlayTestSequencedMessage>(OnSequencedMessageReceived);

                if (!isHostOwnClient)
                    clientMessageManager.UnregisterMessageHandler<SteamPlayTestSequencedMessage>(OnSequencedMessageReceived);
            }

            Assert.AreEqual(0, _sequencedChannelMismatchCount, "A message arrived on a channel other than the one it was sent on.");

            AssertSequencedDelivery(_receivedSequencedIndices[0], _receivedSequencedIndices[1], "client to server");
            AssertSequencedDelivery(_receivedSequencedIndices[2], _receivedSequencedIndices[3], "server to client");
        }

        /// <summary>
        /// A transport given simulated conditions clears them from Steam, which keeps them for the whole process, when it shuts down, so
        /// they do not carry into the next play session.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator Transport_GivenNetworkConditions_ClearsThemFromSteamWhenShutDown()
        {
            UnityCoreManager peerCoreManager = NewPeer("SteamPeer", out UnityTransportManager peerTransportManager, out FacepunchTransport _);

            yield return WaitForTask(peerTransportManager.EnsureAddedAsync());

            FacepunchSockets facepunchSockets = (FacepunchSockets)peerTransportManager.Transports[0];
            facepunchSockets.SetNetworkConditions(new NetworkConditions { LatencyMilliseconds = SimulatedLatencyMilliseconds });
            Assert.AreEqual((int)SimulatedLatencyMilliseconds, GetSteamsSimulatedLatencyMilliseconds(), "Steam did not take the simulated latency.");

            Object.Destroy(peerCoreManager.gameObject);

            yield return WaitUntil(() => GetSteamsSimulatedLatencyMilliseconds() == 0);
            Assert.AreEqual(0, GetSteamsSimulatedLatencyMilliseconds(), "Steam still simulated latency after the transport shut down.");
        }

        /// <summary>
        /// Returns the send latency Steam simulates for the whole process, in milliseconds.
        /// </summary>
        /// <returns>The latency.</returns>
        /// <remarks>Steam stores it as an integer, which Facepunch's getter hands back unconverted in a float's bits.</remarks>
        private static int GetSteamsSimulatedLatencyMilliseconds() => BitConverter.SingleToInt32Bits(SteamNetworkingUtils.FakeSendPacketLag);

        /// <summary>
        /// Launch arguments set the address and SteamID a client connects to and the port, and a SteamID that is not a number is refused
        /// with an error.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator LaunchArguments_SetTheAddressSteamIdAndPort()
        {
            NewPeer("SteamPeer", out UnityTransportManager peerTransportManager, out FacepunchTransport _);

            yield return WaitForTask(peerTransportManager.EnsureAddedAsync());

            FacepunchSockets facepunchSockets = (FacepunchSockets)peerTransportManager.Transports[0];
            facepunchSockets.ApplyCommandLineArguments(new CommandLineArguments(new[] { $"-{FacepunchSockets.AddressArgument}", "203.0.113.7:27015", $"-{FacepunchSockets.SteamIdArgument}", "76561198012345678", $"-{Transport.PortArgument}", "27016" }));

            Assert.AreEqual("203.0.113.7:27015", facepunchSockets.RemoteAddress, "The address argument was not applied.");
            Assert.AreEqual(76561198012345678UL, facepunchSockets.RemoteSteamId, "The SteamID argument was not applied.");
            Assert.AreEqual(27016, facepunchSockets.Configuration.Port, "The port argument was not applied to the IP port.");
            Assert.AreEqual(27016, facepunchSockets.VirtualPort, "The port argument was not applied to the virtual port.");

            LogAssert.Expect(LogType.Error, new Regex("is not a SteamID"));
            facepunchSockets.ApplyCommandLineArguments(new CommandLineArguments(new[] { $"-{FacepunchSockets.SteamIdArgument}", "someone" }));

            Assert.AreEqual(76561198012345678UL, facepunchSockets.RemoteSteamId, "A SteamID argument that is not a number replaced the one set before.");
        }

        /// <summary>
        /// A remote client's link has a Steam status on both sides, and a host's own client, paired in process, has none.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_ByIpAddress_HasALinkStatusOnBothSides_AndTheHostsOwnClientHasNone()
        {
            ushort port = ProbeFreeUdpPort();
            UnityCoreManager hostCoreManager = NewPeer("SteamHost", out UnityTransportManager hostTransportManager, out FacepunchTransport _);

            yield return WaitForTask(hostTransportManager.EnsureAddedAsync());

            FacepunchSockets hostFacepunchSockets = (FacepunchSockets)hostTransportManager.Transports[0];
            hostFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            hostFacepunchSockets.SetPort(port);

            yield return WaitForTask(hostTransportManager.StartHostAsync());
            yield return WaitUntil(() => IsClientAuthenticated(hostCoreManager));
            Assert.IsTrue(TryGetOwnClientConnection(hostCoreManager, hostFacepunchSockets, out Connection ownClientConnection), "The host's own client never joined.");

            UnityCoreManager clientCoreManager = NewPeer("SteamClient", out UnityTransportManager clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.RemoteAddress = $"127.0.0.1:{port}";

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            FacepunchSockets clientFacepunchSockets = (FacepunchSockets)clientTransportManager.Transports[0];
            clientFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientCoreManager) && hostCoreManager.CoreManager.TransportManager.ActiveConnections.Count == 2);
            Assert.IsTrue(IsClientAuthenticated(clientCoreManager), "The remote client never authenticated.");

            Connection remoteClientConnection = null;

            foreach (Connection activeConnection in hostCoreManager.CoreManager.TransportManager.ActiveConnections)
            {
                if (!hostFacepunchSockets.IsOwnClientConnection(activeConnection))
                    remoteClientConnection = activeConnection;
            }

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            clientTransportManagerCore.TryGetLocalClientConnection(out Connection clientConnection);
            clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);

            Assert.IsNotNull(remoteClientConnection, "The host holds no remote client.");
            Assert.IsTrue(hostFacepunchSockets.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus _), "The host read no link status for its remote client.");
            Assert.IsTrue(clientFacepunchSockets.TryGetLinkStatus(clientConnection, out SteamLinkStatus _), "The client read no link status through its own Connection.");
            Assert.IsTrue(clientFacepunchSockets.TryGetLinkStatus(serverConnection, out SteamLinkStatus _), "The client read no link status through its server Connection.");
            Assert.IsFalse(hostFacepunchSockets.TryGetLinkStatus(ownClientConnection, out SteamLinkStatus _), "The host's own client, paired in process, read a Steam link status.");
        }

        /// <summary>
        /// Simulated latency reaches Steam: the ping Steam measures on a remote client's link rises by well over the latency set, since
        /// both peers share this process and Steam delays each one's sends, and falls back once the conditions are cleared.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_ByIpAddress_UnderSimulatedLatency_SteamsPingRises()
        {
            yield return StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager _);
            Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection remoteClientConnection), "The server holds no remote client.");

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverCoreManager.GetComponent<UnityTransportManager>().Transports[0];
            MessageManager clientMessageManager = clientCoreManager.CoreManager.MessageManager;
            clientMessageManager.RegisterMessageHandler<SteamPlayTestPayload>(OnTickReceived);

            serverFacepunchSockets.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus baselineLinkStatus);
            int raisedPingMilliseconds = baselineLinkStatus.PingMilliseconds + (int)SimulatedLatencyMilliseconds * 3 / 2;

            try
            {
                serverFacepunchSockets.SetNetworkConditions(new NetworkConditions { LatencyMilliseconds = SimulatedLatencyMilliseconds });

                yield return WaitUntil(() => IsPingAtLeast(serverFacepunchSockets, remoteClientConnection, raisedPingMilliseconds));
                serverFacepunchSockets.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus raisedLinkStatus);
                Assert.GreaterOrEqual(raisedLinkStatus.PingMilliseconds, raisedPingMilliseconds, $"Steam's ping rose from [{baselineLinkStatus.PingMilliseconds}] ms only to [{raisedLinkStatus.PingMilliseconds}] ms under [{SimulatedLatencyMilliseconds}] ms of simulated latency.");

                serverFacepunchSockets.SetNetworkConditions(networkConditions: null);

                yield return WaitUntil(() => !IsPingAtLeast(serverFacepunchSockets, remoteClientConnection, raisedPingMilliseconds));
                serverFacepunchSockets.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus clearedLinkStatus);
                Assert.Less(clearedLinkStatus.PingMilliseconds, raisedPingMilliseconds, "Steam's ping stayed raised after the conditions were cleared.");
            }
            finally
            {
                serverFacepunchSockets.SetNetworkConditions(networkConditions: null);
                clientMessageManager.UnregisterMessageHandler<SteamPlayTestPayload>(OnTickReceived);
            }
        }

        /// <summary>
        /// A client the connecting filter refuses never joins, the filter is asked with the client's own SteamID, and the same client
        /// joins once the filter lets it in.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_RefusedByTheConnectingFilter_NeverJoins_AndJoinsOnceLetIn()
        {
            ushort port = ProbeFreeUdpPort();

            UnityCoreManager serverCoreManager = NewPeer("SteamServer", out UnityTransportManager serverTransportManager, out FacepunchTransport serverFacepunchTransport);
            serverFacepunchTransport.ConnectingClientFilter = FilterConnectingClient;

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverTransportManager.Transports[0];
            serverFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            serverFacepunchSockets.SetPort(port);

            yield return WaitForTask(serverTransportManager.StartServerAsync());

            UnityCoreManager clientCoreManager = NewPeer("SteamClient", out UnityTransportManager clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.RemoteAddress = $"127.0.0.1:{port}";

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            ((FacepunchSockets)clientTransportManager.Transports[0]).Addressing = FacepunchAddressing.IpAddress;

            LogAssert.ignoreFailingMessages = true;

            try
            {
                yield return WaitForTask(clientTransportManager.StartClientAsync());
                yield return WaitUntil(() => _filteredSteamId is not FacepunchSockets.UnsetSteamId && IsClientDisconnected(clientCoreManager));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.AreEqual(SteamClient.SteamId.Value, _filteredSteamId, "The filter was not asked with the connecting client's SteamID.");
            Assert.IsTrue(IsClientDisconnected(clientCoreManager), "A client the filter refused was never closed.");
            Assert.IsFalse(IsClientAuthenticated(clientCoreManager), "A client the filter refused joined.");
            Assert.AreEqual(0, serverCoreManager.CoreManager.TransportManager.ActiveConnections.Count, "The server holds a client the filter refused.");

            _isFilteredClientAllowed = true;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientCoreManager));
            Assert.IsTrue(IsClientAuthenticated(clientCoreManager), "The client never joined once the filter let it in.");
        }

        /// <summary>
        /// An unreliable message sent behind a reliable burst still queued in Steam arrives before the burst does, because the two ride
        /// lanes of their own.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator UnreliableMessage_SentBehindAReliableBurst_ArrivesFirst()
        {
            int steamSendRateMinimumBytesPerSecond = SteamNetworkingUtils.SendRateMin;
            int steamSendRateMaximumBytesPerSecond = SteamNetworkingUtils.SendRateMax;
            ushort port = ProbeFreeUdpPort();

            UnityCoreManager serverCoreManager = NewPeer("SteamServer", out UnityTransportManager serverTransportManager, out FacepunchTransport _);
            UnityCoreManager clientCoreManager = NewPeer("SteamClient", out UnityTransportManager clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.RemoteAddress = $"127.0.0.1:{port}";

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            // Slow enough that the burst takes about half a second to leave, so anything queued behind it would plainly wait. Steam's
            // rates are process wide, so both peers set them.
            UseIpAddressingAndSendRate((FacepunchSockets)serverTransportManager.Transports[0], port, BurstSendRateKilobytesPerSecond);
            UseIpAddressingAndSendRate((FacepunchSockets)clientTransportManager.Transports[0], port, BurstSendRateKilobytesPerSecond);

            MessageManager clientMessageManager = clientCoreManager.CoreManager.MessageManager;
            clientMessageManager.RegisterMessageHandler<SteamPlayTestBulkMessage>(OnBurstReceived);
            clientMessageManager.RegisterMessageHandler<SteamPlayTestPayload>(OnProbeReceived);

            try
            {
                yield return WaitForTask(serverTransportManager.StartServerAsync());
                yield return WaitForTask(clientTransportManager.StartClientAsync());
                yield return WaitUntil(() => IsClientAuthenticated(clientCoreManager) && TryGetRemoteClientConnection(serverCoreManager, out Connection _));
                Assert.IsTrue(TryGetRemoteClientConnection(serverCoreManager, out Connection remoteClientConnection), "The server holds no remote client.");

                remoteClientConnection.SendMessage(Channel.Reliable, new SteamPlayTestBulkMessage { Bytes = new(new byte[BurstBytes]) });

                // Two frames later, so the burst is already in Steam's queue when the unreliable message is handed over.
                yield return null;
                yield return null;

                remoteClientConnection.SendMessage(Channel.Unreliable, new SteamPlayTestPayload { Text = "probe" });

                yield return WaitUntil(() => _burstArrivedSeconds >= 0f && _probeArrivedSeconds >= 0f);
            }
            finally
            {
                clientMessageManager.UnregisterMessageHandler<SteamPlayTestBulkMessage>(OnBurstReceived);
                clientMessageManager.UnregisterMessageHandler<SteamPlayTestPayload>(OnProbeReceived);
                SteamNetworkingUtils.SendRateMin = steamSendRateMinimumBytesPerSecond;
                SteamNetworkingUtils.SendRateMax = steamSendRateMaximumBytesPerSecond;
            }

            Assert.GreaterOrEqual(_burstArrivedSeconds, 0f, "The reliable burst never arrived.");
            Assert.GreaterOrEqual(_probeArrivedSeconds, 0f, "The unreliable message never arrived.");
            Assert.Less(_probeArrivedSeconds, _burstArrivedSeconds, "The unreliable message waited behind the reliable burst.");
        }

        /// <summary>
        /// A late join over a world of 64 KiB values, at the component's default send rates, arrives whole having been sent little more
        /// than the state.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator LateJoin_OverWideValues_AtTheDefaults_IsSentTheStateAboutOnce()
        {
            yield return RunLateJoin(ServerManager.DefaultMaximumObjectKilobytesPerTick, isSteamsOwnSendRate: false);
        }

        /// <summary>
        /// The same join at Steam's own send rate of 256 KiB a second, paced under it, arrives whole having been sent little more than the
        /// state, though each value takes several ticks to arrive.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator LateJoin_OverWideValues_AtSteamsOwnRate_IsSentTheStateAboutOnce()
        {
            yield return RunLateJoin(BudgetUnderSteamsOwnRateKilobytes, isSteamsOwnSendRate: true);
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
        /// Records when the reliable burst arrived, then returns its pooled array.
        /// </summary>
        /// <param name="messageContext">The context the message arrived in.</param>
        /// <param name="steamPlayTestBulkMessage">The message.</param>
        private void OnBurstReceived(in MessageContext messageContext, SteamPlayTestBulkMessage steamPlayTestBulkMessage)
        {
            _burstArrivedSeconds = Time.realtimeSinceStartup;

            if (messageContext.IsDeserialized && steamPlayTestBulkMessage.Bytes.Array is not null)
                TrackedArrayPool<byte>.Return(steamPlayTestBulkMessage.Bytes.Array);
        }

        /// <summary>
        /// Records when the unreliable probe arrived.
        /// </summary>
        /// <param name="messageContext">The context the message arrived in.</param>
        /// <param name="steamPlayTestPayload">The message.</param>
        private void OnProbeReceived(in MessageContext messageContext, SteamPlayTestPayload steamPlayTestPayload) => _probeArrivedSeconds = Time.realtimeSinceStartup;

        /// <summary>
        /// Records the SteamID a connecting client is asking under, and lets it in only once the case allows it.
        /// </summary>
        /// <param name="steamId">The connecting client's SteamID64.</param>
        /// <returns><see langword="true"/> to let the client in.</returns>
        private bool FilterConnectingClient(ulong steamId)
        {
            _filteredSteamId = steamId;

            return _isFilteredClientAllowed;
        }

        /// <summary>
        /// Starts a server holding a world of wide values, joins a client to it by IP address, and checks the client holds every value
        /// having been sent little more than the state.
        /// </summary>
        /// <param name="maximumObjectKilobytesPerTick">The server's object pacing budget.</param>
        /// <param name="isSteamsOwnSendRate">True to leave both peers' send rates unset at Steam's own, false for the component's defaults.</param>
        /// <returns>The coroutine driving the join.</returns>
        private IEnumerator RunLateJoin(byte maximumObjectKilobytesPerTick, bool isSteamsOwnSendRate)
        {
            int steamSendRateMinimumBytesPerSecond = SteamNetworkingUtils.SendRateMin;
            int steamSendRateMaximumBytesPerSecond = SteamNetworkingUtils.SendRateMax;
            ushort port = ProbeFreeUdpPort();

            UnityCoreManager serverCoreManager = NewPeer("SteamServer", out UnityTransportManager serverTransportManager, out FacepunchTransport _);
            UnityCoreManager clientCoreManager = NewPeer("SteamClient", out UnityTransportManager clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.RemoteAddress = $"127.0.0.1:{port}";

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverTransportManager.Transports[0];
            FacepunchSockets clientFacepunchSockets = (FacepunchSockets)clientTransportManager.Transports[0];

            // Steam's rates are process wide and an earlier case may have moved them, so Steam's own are written back first.
            if (isSteamsOwnSendRate)
            {
                SteamNetworkingUtils.SendRateMin = SteamsOwnSendRateBytesPerSecond;
                SteamNetworkingUtils.SendRateMax = SteamsOwnSendRateBytesPerSecond;
                UseIpAddressingAndSendRate(serverFacepunchSockets, port, FacepunchSockets.UnsetSendSetting);
                UseIpAddressingAndSendRate(clientFacepunchSockets, port, FacepunchSockets.UnsetSendSetting);
            }
            else
            {
                serverFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
                serverFacepunchSockets.SetPort(port);
                clientFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            }

            CoreManager server = serverCoreManager.CoreManager;
            CoreManager client = clientCoreManager.CoreManager;
            server.ServerManager.MaximumObjectKilobytesPerTick = maximumObjectKilobytesPerTick;

            try
            {
                yield return WaitForTask(serverTransportManager.StartServerAsync());
                yield return WaitUntil(() => server.TransportManager.IsServerStarted);
                Assert.IsTrue(server.TransportManager.IsServerStarted, "The server never started.");

                uint[] systemIds = new uint[LateJoinSystemCount];
                string[] labels = new string[LateJoinSystemCount];
                System.Random random = new(LabelKibibytes);
                char[] characters = new char[LabelKibibytes * 1024];

                for (int systemIndex = 0; systemIndex < LateJoinSystemCount; systemIndex++)
                {
                    Assert.IsTrue(NetworkSystemPool.TryRent<NetworkSystem, SteamPlayTestLargeLabelComponent>(server, out NetworkSystem serverSystem, canStartSystem: true), "The server could not start a system.");

                    for (int characterIndex = 0; characterIndex < characters.Length; characterIndex++)
                        characters[characterIndex] = (char)random.Next('a', 'z' + 1);

                    labels[systemIndex] = new(characters);
                    Assert.IsTrue(serverSystem.TryGetComponent(out SteamPlayTestLargeLabelComponent serverComponent), "The server's system holds no label component.");
                    serverComponent.Label.Value = labels[systemIndex];
                    systemIds[systemIndex] = serverSystem.Id;
                }

                yield return new WaitForSecondsRealtime(0.5f);

                long sentBytesBeforeJoin = server.TransportManager.TotalBytesSent;
                float joinStartedSeconds = Time.realtimeSinceStartup;

                yield return WaitForTask(clientTransportManager.StartClientAsync());
                yield return WaitUntil(() => CountLabelsHeld(client, systemIds, labels) == LateJoinSystemCount, LateJoinSeconds);

                int heldCount = CountLabelsHeld(client, systemIds, labels);
                long stateBytes = (long)LateJoinSystemCount * LabelKibibytes * 1024;
                long sentBytes = server.TransportManager.TotalBytesSent - sentBytesBeforeJoin;
                double sentToStateRatio = (double)sentBytes / stateBytes;
                string joinSummary = $"[{LateJoinSystemCount}] x [{LabelKibibytes}] KiB at [{maximumObjectKilobytesPerTick}] kB a tick, Steam's own rate [{isSteamsOwnSendRate}]: held [{heldCount}] after [{Time.realtimeSinceStartup - joinStartedSeconds:F1}] s, state [{stateBytes}] bytes, sent [{sentBytes}] ({sentToStateRatio:F2}x), received [{client.TransportManager.TotalBytesReceived}].";

                Debug.Log($"[{nameof(FacepunchPlayTests)}] late join {joinSummary}");

                Assert.AreEqual(LateJoinSystemCount, heldCount, joinSummary);
                Assert.LessOrEqual(sentToStateRatio, MaximumSentToStateRatio, joinSummary);
            }
            finally
            {
                SteamNetworkingUtils.SendRateMin = steamSendRateMinimumBytesPerSecond;
                SteamNetworkingUtils.SendRateMax = steamSendRateMaximumBytesPerSecond;
            }
        }

        /// <summary>
        /// Counts the systems a client holds with the server's label, resolving each by Id on every read.
        /// </summary>
        /// <param name="client">The client peer.</param>
        /// <param name="systemIds">The server's systems.</param>
        /// <param name="labels">Each system's label.</param>
        /// <returns>How many systems the client holds with the right label.</returns>
        private static int CountLabelsHeld(CoreManager client, uint[] systemIds, string[] labels)
        {
            int heldCount = 0;

            for (int systemIndex = 0; systemIndex < systemIds.Length; systemIndex++)
            {
                if (client.SystemManager.TryGetSystemReference(systemIds[systemIndex], out NetworkSystem clientSystem) && clientSystem.TryGetComponent(out SteamPlayTestLargeLabelComponent clientComponent) && clientComponent.Label.Value == labels[systemIndex])
                    heldCount++;
            }

            return heldCount;
        }

        /// <summary>
        /// Switches an added peer's transport to IP addressing on a port, with both send rates at one value.
        /// </summary>
        /// <param name="facepunchSockets">The peer's transport.</param>
        /// <param name="port">The port the peer listens on, when it is a server.</param>
        /// <param name="sendRateKilobytesPerSecond">The send rate floor and ceiling, or <see cref="FacepunchSockets.UnsetSendSetting"/> to leave Steam's.</param>
        private static void UseIpAddressingAndSendRate(FacepunchSockets facepunchSockets, ushort port, uint sendRateKilobytesPerSecond)
        {
            facepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            facepunchSockets.SetPort(port);
            facepunchSockets.SendRateMinimumKilobytesPerSecond = sendRateKilobytesPerSecond;
            facepunchSockets.SendRateMaximumKilobytesPerSecond = sendRateKilobytesPerSecond;
        }

        /// <summary>
        /// Returns whether Steam's measured ping on a server's link to a client has reached a figure, sending a little on it so the
        /// measurement keeps moving.
        /// </summary>
        /// <param name="serverFacepunchSockets">The server's transport.</param>
        /// <param name="connection">The server's Connection for the client.</param>
        /// <param name="pingMilliseconds">The figure.</param>
        /// <returns><see langword="true"/> once Steam's ping is at least <paramref name="pingMilliseconds"/>.</returns>
        private static bool IsPingAtLeast(FacepunchSockets serverFacepunchSockets, Connection connection, int pingMilliseconds)
        {
            connection.SendMessage(Channel.Reliable, new SteamPlayTestPayload { Text = "ping" });

            return serverFacepunchSockets.TryGetLinkStatus(connection, out SteamLinkStatus linkStatus) && linkStatus.PingMilliseconds >= pingMilliseconds;
        }

        /// <summary>
        /// Records the index of a sequenced message in the bucket of its direction and mode, and counts it when it arrived on the wrong channel.
        /// </summary>
        /// <param name="messageContext">The context the message arrived in.</param>
        /// <param name="steamPlayTestSequencedMessage">The message.</param>
        private void OnSequencedMessageReceived(in MessageContext messageContext, SteamPlayTestSequencedMessage steamPlayTestSequencedMessage)
        {
            Channel expectedChannel = steamPlayTestSequencedMessage.IsSentReliable ? Channel.Reliable : Channel.Unreliable;

            if (messageContext.Channel != expectedChannel)
                _sequencedChannelMismatchCount++;

            int bucketIndex = (messageContext.IsSenderServer ? 2 : 0) + (steamPlayTestSequencedMessage.IsSentReliable ? 0 : 1);
            _receivedSequencedIndices[bucketIndex].Add(steamPlayTestSequencedMessage.Index);
        }

        /// <summary>
        /// Returns whether every sequenced bucket holds all the messages sent to it.
        /// </summary>
        /// <returns><see langword="true"/> once each of the four buckets holds <see cref="SequencedMessageCount"/> messages.</returns>
        private bool AreSequencedBucketsFull()
        {
            foreach (List<int> receivedIndices in _receivedSequencedIndices)
            {
                if (receivedIndices.Count < SequencedMessageCount)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Starts a server peer listening on a loopback port, then a client peer that joins it by address, and waits for the join.
        /// </summary>
        /// <param name="serverCoreManager">The server peer.</param>
        /// <param name="clientCoreManager">The client peer.</param>
        /// <param name="clientTransportManager">The client peer's transport manager.</param>
        /// <param name="sendRateMinimumKilobytesPerSecond">The send rate floor the server gives its connections, or <see cref="FacepunchSockets.UnsetSendSetting"/> for Steam's.</param>
        /// <param name="isShareSteamIdsEnabled">Whether the server shares SteamIDs with its clients.</param>
        /// <param name="isPerConnectionReceiveEnabled">Whether the server receives each client through its own call.</param>
        /// <param name="bindAddress">The address the server listens on.</param>
        /// <param name="clientHost">The host the client dials, with the port added, an IPv6 one written in brackets.</param>
        /// <returns>The coroutine driving the start.</returns>
        private IEnumerator StartServerAndRemoteClient(out UnityCoreManager serverCoreManager, out UnityCoreManager clientCoreManager, out UnityTransportManager clientTransportManager, uint sendRateMinimumKilobytesPerSecond = FacepunchSockets.UnsetSendSetting, bool isShareSteamIdsEnabled = false, bool isPerConnectionReceiveEnabled = false, string bindAddress = "localhost", string clientHost = "127.0.0.1")
        {
            ushort port = ProbeFreeUdpPort();

            serverCoreManager = NewPeer("SteamServer", out UnityTransportManager serverTransportManager, out FacepunchTransport serverFacepunchTransport);
            serverFacepunchTransport.BindAddress = bindAddress;
            clientCoreManager = NewPeer("SteamClient", out clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.RemoteAddress = $"{clientHost}:{port}";

            return StartServerAndRemoteClient(serverCoreManager, serverTransportManager, clientCoreManager, clientTransportManager, port, sendRateMinimumKilobytesPerSecond, isShareSteamIdsEnabled, isPerConnectionReceiveEnabled);
        }

        /// <summary>
        /// Starts the server on <paramref name="port"/> by IP address, then the client, and waits until the client is admitted.
        /// </summary>
        /// <param name="serverCoreManager">The server peer.</param>
        /// <param name="serverTransportManager">The server peer's transport manager.</param>
        /// <param name="clientCoreManager">The client peer.</param>
        /// <param name="clientTransportManager">The client peer's transport manager.</param>
        /// <param name="port">The loopback port the server listens on.</param>
        /// <param name="sendRateMinimumKilobytesPerSecond">The send rate floor the server gives its connections, or <see cref="FacepunchSockets.UnsetSendSetting"/> for Steam's.</param>
        /// <param name="isShareSteamIdsEnabled">Whether the server shares SteamIDs with its clients.</param>
        /// <param name="isPerConnectionReceiveEnabled">Whether the server receives each client through its own call.</param>
        /// <returns>The coroutine driving the start.</returns>
        /// <remarks>The component carries no addressing setter, so the engine transport each start builds is switched to IP addressing before it starts.</remarks>
        private static IEnumerator StartServerAndRemoteClient(UnityCoreManager serverCoreManager, UnityTransportManager serverTransportManager, UnityCoreManager clientCoreManager, UnityTransportManager clientTransportManager, ushort port, uint sendRateMinimumKilobytesPerSecond, bool isShareSteamIdsEnabled, bool isPerConnectionReceiveEnabled)
        {
            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverTransportManager.Transports[0];
            serverFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            serverFacepunchSockets.SetPort(port);
            serverFacepunchSockets.SendRateMinimumKilobytesPerSecond = sendRateMinimumKilobytesPerSecond;
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
        /// Returns whether a peer's local client has come all the way down to disconnected.
        /// </summary>
        /// <param name="unityCoreManager">The peer to ask.</param>
        /// <returns><see langword="true"/> once its client is disconnected.</returns>
        private static bool IsClientDisconnected(UnityCoreManager unityCoreManager) =>
            unityCoreManager.CoreManager.TransportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.LocalState is LocalConnectionState.Disconnected;

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
        /// Asserts that the reliable messages of one direction arrived in the order sent, and that its unreliable ones all arrived once.
        /// </summary>
        /// <param name="reliableIndices">The indices of the reliable messages, in arrival order.</param>
        /// <param name="unreliableIndices">The indices of the unreliable messages, in arrival order.</param>
        /// <param name="direction">The name of the direction, for the failure messages.</param>
        private static void AssertSequencedDelivery(List<int> reliableIndices, List<int> unreliableIndices, string direction)
        {
            Assert.AreEqual(SequencedMessageCount, reliableIndices.Count, $"The {direction} reliable messages did not all arrive.");

            for (int i = 0; i < reliableIndices.Count; i++)
                Assert.AreEqual(i, reliableIndices[i], $"The {direction} reliable message at position [{i}] arrived out of order.");

            Assert.AreEqual(SequencedMessageCount, unreliableIndices.Count, $"The {direction} unreliable messages did not all arrive exactly once.");

            HashSet<int> distinctIndices = new();

            foreach (int unreliableIndex in unreliableIndices)
            {
                Assert.That(unreliableIndex, Is.InRange(0, SequencedMessageCount - 1), $"A {direction} unreliable message carried an index outside the sent range.");
                distinctIndices.Add(unreliableIndex);
            }

            Assert.AreEqual(SequencedMessageCount, distinctIndices.Count, $"A {direction} unreliable message arrived more than once.");
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
        /// <param name="seconds">The longest the wait runs, in seconds.</param>
        /// <returns>The coroutine driving the wait.</returns>
        private static IEnumerator WaitUntil(Func<bool> condition, float seconds = MaximumWaitSeconds)
        {
            float deadlineSeconds = Time.realtimeSinceStartup + seconds;

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

    /// <summary>
    /// A numbered message a play mode test sends in both directions on both channels, to check order and the channel it arrives on.
    /// </summary>
    [NetworkType]
    public struct SteamPlayTestSequencedMessage : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The position of the message among those sent in its direction and mode.
        /// </summary>
        public int Index;

        /// <summary>
        /// Whether the message was sent on the reliable channel, rather than the unreliable one.
        /// </summary>
        public bool IsSentReliable;
    }

    /// <summary>
    /// A component holding one wide string, so a world of these is far wider than a single tick of a slow link.
    /// </summary>
    public partial class SteamPlayTestLargeLabelComponent : NetworkComponent
    {
        /// <summary>
        /// The string, many kilobytes wide in the cases that use it.
        /// </summary>
        public readonly NetworkMember<string> Label = new();
    }
}
