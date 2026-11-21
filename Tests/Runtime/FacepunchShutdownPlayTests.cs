using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nucleus.Integrations.Facepunch.Tests
{
    /// <summary>
    /// Play mode coverage of the transport outliving Steam: a game that shuts Steam down while a server and a client are still up.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="FacepunchPlayTests"/>, and named to run after it, because its case shuts Steam down, which every other
    /// case needs up. Skipped unless the Steam client is running. A later case in the same editor initializes Steam again.
    /// </remarks>
    public class FacepunchShutdownPlayTests
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
        /// Every GameObject the case created, destroyed at teardown.
        /// </summary>
        private readonly List<GameObject> _createdObjects = new();

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
        /// Destroys every object the case created.
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

            yield return null;
        }

        /// <summary>
        /// Steam shut down under a running server and a connected client stops both, and stopping them again afterwards throws nothing.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator SteamShutDown_WithServerAndClientUp_StopsBothWithoutThrowing()
        {
            ushort port = ProbeFreeUdpPort();

            UnityCoreManager serverCoreManager = NewPeer("SteamServer", out UnityTransportManager serverTransportManager, out FacepunchTransport _);
            UnityCoreManager clientCoreManager = NewPeer("SteamClient", out UnityTransportManager clientTransportManager, out FacepunchTransport clientFacepunchTransport);
            clientFacepunchTransport.RemoteAddress = $"127.0.0.1:{port}";

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            FacepunchSockets serverFacepunchSockets = (FacepunchSockets)serverTransportManager.Transports[0];
            serverFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            serverFacepunchSockets.SetPort(port);
            ((FacepunchSockets)clientTransportManager.Transports[0]).Addressing = FacepunchAddressing.IpAddress;

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientConnected(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
            Assert.IsTrue(IsClientConnected(clientTransportManagerCore), "The client never connected over Steam.");

            LogAssert.Expect(LogType.Warning, new Regex("Steam was shut down while the server was running, so the server stopped\\."));
            LogAssert.Expect(LogType.Warning, new Regex("Steam was shut down while the client was connected, so the client stopped\\."));

            SteamClient.Shutdown();

            yield return WaitUntil(() => !serverTransportManagerCore.IsServerStarted && !IsClientConnected(clientTransportManagerCore));
            Assert.IsFalse(serverTransportManagerCore.IsServerStarted, "The server was still running after Steam shut down.");
            Assert.IsFalse(IsClientConnected(clientTransportManagerCore), "The client was still connected after Steam shut down.");

            Task serverStopTask = serverTransportManagerCore.Transports[0].DisconnectLocalConnectionAsync(Invoker.Server);
            Task clientStopTask = clientTransportManagerCore.Transports[0].DisconnectLocalConnectionAsync(Invoker.Client);

            yield return WaitForTask(serverStopTask);
            yield return WaitForTask(clientStopTask);
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
        /// <param name="transportManager">The peer's transport manager.</param>
        /// <returns><see langword="true"/> while its client is connected.</returns>
        private static bool IsClientConnected(TransportManager transportManager) =>
            transportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.LocalState is LocalConnectionState.Connected;

        /// <summary>
        /// Returns a free loopback UDP port for the server to listen on.
        /// </summary>
        /// <returns>The port.</returns>
        private static ushort ProbeFreeUdpPort()
        {
            using (UdpClient probe = new(new IPEndPoint(IPAddress.Loopback, 0)))
                return (ushort)((IPEndPoint)probe.Client.LocalEndPoint).Port;
        }

        /// <summary>
        /// Waits for a transport Task, failing the case when it faults or does not complete.
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
        /// Yields frames, pumping Steam's callbacks each one while Steam is up, until a condition holds or the wait runs out.
        /// </summary>
        /// <param name="condition">The condition waited for.</param>
        /// <returns>The coroutine driving the wait.</returns>
        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            float deadlineSeconds = Time.realtimeSinceStartup + MaximumWaitSeconds;

            while (!condition() && Time.realtimeSinceStartup < deadlineSeconds)
            {
                if (!FacepunchSockets.IsSteamShutDown())
                    SteamClient.RunCallbacks();

                yield return null;
            }
        }
    }
}
