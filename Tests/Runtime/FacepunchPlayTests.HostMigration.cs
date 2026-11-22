using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newfarm.Server;
using NUnit.Framework;
using Nucleus.Integrations.Newfarm;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nucleus.Integrations.Facepunch.Tests
{
    public partial class FacepunchPlayTests
    {
        /// <summary>
        /// The address every host migration peer names itself by, since all of them run on this machine.
        /// </summary>
        private const string LoopbackAddress = "127.0.0.1";
        /// <summary>
        /// The budget a session host is given to start hosting or to join, in milliseconds.
        /// </summary>
        /// <remarks>Past the ten seconds a client may hold its dial for Steam's relay network, which is still starting when a run begins.</remarks>
        private const uint SessionHostTimeoutMilliseconds = 20_000;
        /// <summary>
        /// The longest a case waits for a session host's join, in seconds, past <see cref="SessionHostTimeoutMilliseconds"/>.
        /// </summary>
        private const float JoinWaitSeconds = 25f;
        /// <summary>
        /// The budget the nothing listening case gives its join, in milliseconds.
        /// </summary>
        private const uint UnansweredJoinTimeoutMilliseconds = 2_000;
        /// <summary>
        /// The longest a handover may take, in seconds.
        /// </summary>
        private const float HandoverWaitSeconds = 45f;

        /// <summary>
        /// The directory a host migration case runs, or <see langword="null"/> when it runs none.
        /// </summary>
        private NewfarmServer _newfarmServer;
        /// <summary>
        /// Stops the directory's loop.
        /// </summary>
        private CancellationTokenSource _directoryCancellationTokenSource;
        /// <summary>
        /// The thread the directory's loop runs on.
        /// </summary>
        private Thread _directoryThread;

        /// <summary>
        /// A session host hosting by IP address names its address and port, and a second peer's session host joins by that credential
        /// and leaves again.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator HostMigration_HostingByIpAddress_NamesItsAddress_AndASecondPeerJoinsByIt()
        {
            ushort port = ProbeFreeUdpPort();
            UnityCoreManager hostCoreManager = NewPeer("SteamHost", out UnityTransportManager hostTransportManager, out FacepunchTransport _);
            UnityCoreManager joinerCoreManager = NewPeer("SteamJoiner", out UnityTransportManager joinerTransportManager, out FacepunchTransport _);

            yield return WaitForTask(hostTransportManager.EnsureAddedAsync());
            yield return WaitForTask(joinerTransportManager.EnsureAddedAsync());

            FacepunchSockets hostFacepunchSockets = (FacepunchSockets)hostTransportManager.Transports[0];
            hostFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            hostFacepunchSockets.SetPort(port);
            FacepunchSockets joinerFacepunchSockets = (FacepunchSockets)joinerTransportManager.Transports[0];
            joinerFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;

            FacepunchSessionHost hostSessionHost = new(hostFacepunchSockets) { HostedAddress = LoopbackAddress };
            FacepunchSessionHost joinerSessionHost = new(joinerFacepunchSockets);

            Task<SessionHostResult> hostingTask = hostSessionHost.StartHostingAsync(SessionHostTimeoutMilliseconds);

            yield return WaitForTask(hostingTask);

            Assert.AreEqual(SessionHostResult.Success, hostingTask.Result, "The session host could not start hosting.");
            Assert.IsTrue(hostSessionHost.IsHostingLinkUp, "The session host is hosting but its server is not up.");
            Assert.AreEqual($"{LoopbackAddress}:{port}", hostSessionHost.HostedCredential, "The session host did not name its address and port.");
            Assert.AreEqual(FacepunchSessionHost.IpAddressAdapterTag, hostSessionHost.AdapterTag, "An IP address was not filed with addresses.");

            Task<SessionHostResult> joiningTask = joinerSessionHost.JoinAsync(hostSessionHost.HostedCredential, SessionHostTimeoutMilliseconds);

            yield return WaitForTask(joiningTask, JoinWaitSeconds);

            Assert.AreEqual(SessionHostResult.Success, joiningTask.Result, $"The second peer could not join [{hostSessionHost.HostedCredential}].");
            Assert.IsTrue(joinerSessionHost.IsJoinedLinkUp, "The join succeeded but the second peer's link is not up.");

            yield return WaitUntil(() => IsClientAuthenticated(joinerCoreManager));
            Assert.IsTrue(IsClientAuthenticated(joinerCoreManager), "The second peer joined but was never admitted.");

            yield return WaitForTask(joinerSessionHost.LeaveAsync());
            Assert.IsFalse(joinerSessionHost.IsJoinedLinkUp, "The second peer's link stayed up after it left.");

            yield return WaitForTask(hostSessionHost.StopHostingAsync());
            Assert.IsFalse(hostSessionHost.IsHostingLinkUp, "The server stayed up after hosting stopped.");
            Assert.IsFalse(hostCoreManager.CoreManager.TransportManager.IsServerStarted, "The host's server was still started after hosting stopped.");
            Assert.AreEqual(string.Empty, hostSessionHost.HostedCredential, "A peer that stopped hosting still named where it hosts.");
        }

        /// <summary>
        /// A join the host refuses reports the credential unreachable, so a directory hears of it, while a join nothing answers is this
        /// peer's own failure and gives up near its budget.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator HostMigration_JoinRefusedByTheHost_IsCredentialUnreachable_AndUnanswered_IsLocalFailure()
        {
            ushort port = ProbeFreeUdpPort();
            NewPeer("SteamHost", out UnityTransportManager hostTransportManager, out FacepunchTransport hostFacepunchTransport);
            hostFacepunchTransport.ConnectingClientFilter = FilterConnectingClient;
            NewPeer("SteamJoiner", out UnityTransportManager joinerTransportManager, out FacepunchTransport _);

            yield return WaitForTask(hostTransportManager.EnsureAddedAsync());
            yield return WaitForTask(joinerTransportManager.EnsureAddedAsync());

            FacepunchSockets hostFacepunchSockets = (FacepunchSockets)hostTransportManager.Transports[0];
            hostFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            hostFacepunchSockets.SetPort(port);
            FacepunchSockets joinerFacepunchSockets = (FacepunchSockets)joinerTransportManager.Transports[0];
            joinerFacepunchSockets.Addressing = FacepunchAddressing.IpAddress;

            yield return WaitForTask(hostTransportManager.StartServerAsync());

            FacepunchSessionHost joinerSessionHost = new(joinerFacepunchSockets);
            Task<SessionHostResult> refusedTask;
            Task<SessionHostResult> unansweredTask;
            float unansweredMilliseconds;
            LogAssert.ignoreFailingMessages = true;

            try
            {
                refusedTask = joinerSessionHost.JoinAsync($"{LoopbackAddress}:{port}", SessionHostTimeoutMilliseconds);

                yield return WaitForTask(refusedTask, JoinWaitSeconds);

                float startedSeconds = Time.realtimeSinceStartup;
                unansweredTask = joinerSessionHost.JoinAsync($"{LoopbackAddress}:{ProbeFreeUdpPort()}", UnansweredJoinTimeoutMilliseconds);

                yield return WaitForTask(unansweredTask);

                unansweredMilliseconds = (Time.realtimeSinceStartup - startedSeconds) * 1000f;
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            Assert.AreEqual(SessionHostResult.CredentialUnreachable, refusedTask.Result, "A join the host refused was not reported as the host's credential being unreachable.");
            Assert.AreEqual(SessionHostResult.LocalFailure, unansweredTask.Result, "A join nothing answered was blamed on the host.");
            Assert.Less(unansweredMilliseconds, UnansweredJoinTimeoutMilliseconds + ConnectTimeoutSlackMilliseconds, "A join nothing answered waited well past its budget.");
            Assert.AreEqual(FacepunchSockets.UnsetConnectTimeoutMilliseconds, joinerFacepunchSockets.ConnectTimeoutMilliseconds, "The join's budget was left on the transport.");
        }

        /// <summary>
        /// Three peers in one session, against a directory run in the editor: the host leaves, one survivor is promoted and hosts at its own
        /// address, and the other rejoins it in the same session.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        /// <remarks>Ignored with a Free <c>Nucleus.dll</c>, which has no sessions.</remarks>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator HostMigration_ThreePeers_TheHostLeaves_OneSurvivorIsPromotedAndTheOtherRejoinsIt()
        {
            ushort directoryPort = StartDirectory();
            ushort hostPort = ProbeFreeUdpPort();
            ushort firstSurvivorPort = ProbeFreeUdpPort();
            ushort secondSurvivorPort = ProbeFreeUdpPort();

            UnityCoreManager hostCoreManager = NewHostMigrationPeer("SteamHost", directoryPort, out UnityTransportManager hostTransportManager, out FacepunchHostMigration hostHostMigration);

            if (!hostCoreManager.CoreManager.ServerManager.IsHostMigrationAvailable)
                Assert.Ignore("Host migration needs Nucleus Pro.");

            UnityCoreManager firstSurvivorCoreManager = NewHostMigrationPeer("SteamFirstSurvivor", directoryPort, out UnityTransportManager firstSurvivorTransportManager, out FacepunchHostMigration firstSurvivorHostMigration);
            UnityCoreManager secondSurvivorCoreManager = NewHostMigrationPeer("SteamSecondSurvivor", directoryPort, out UnityTransportManager secondSurvivorTransportManager, out FacepunchHostMigration secondSurvivorHostMigration);

            yield return AddHostMigrationTransport(hostTransportManager, hostPort, remotePort: hostPort);
            yield return AddHostMigrationTransport(firstSurvivorTransportManager, firstSurvivorPort, remotePort: hostPort);
            yield return AddHostMigrationTransport(secondSurvivorTransportManager, secondSurvivorPort, remotePort: hostPort);

            LogAssert.ignoreFailingMessages = true;

            try
            {
                yield return WaitForTask(hostTransportManager.StartServerAsync());
                yield return WaitUntil(() => hostHostMigration.Migration.State is NewfarmMigrationState.Hosting);
                Assert.AreEqual(NewfarmMigrationState.Hosting, hostHostMigration.Migration.State, "The host never opened a session with the directory.");

                ulong sessionId = hostHostMigration.SessionId;

                yield return WaitForTask(firstSurvivorTransportManager.StartClientAsync());
                yield return WaitForTask(secondSurvivorTransportManager.StartClientAsync());
                yield return WaitUntil(() => firstSurvivorHostMigration.SessionId == sessionId && secondSurvivorHostMigration.SessionId == sessionId);
                Assert.AreEqual(sessionId, firstSurvivorHostMigration.SessionId, "The first client was never told which session it is in.");
                Assert.AreEqual(sessionId, secondSurvivorHostMigration.SessionId, "The second client was never told which session it is in.");

                Object.Destroy(hostCoreManager.gameObject);

                yield return WaitUntil(() => IsHandedOver(firstSurvivorHostMigration, secondSurvivorCoreManager, secondSurvivorHostMigration) || IsHandedOver(secondSurvivorHostMigration, firstSurvivorCoreManager, firstSurvivorHostMigration), HandoverWaitSeconds);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }

            bool isFirstPromoted = firstSurvivorHostMigration.Migration.State is NewfarmMigrationState.Hosting;
            FacepunchHostMigration promotedHostMigration = isFirstPromoted ? firstSurvivorHostMigration : secondSurvivorHostMigration;
            FacepunchHostMigration rejoinedHostMigration = isFirstPromoted ? secondSurvivorHostMigration : firstSurvivorHostMigration;
            UnityCoreManager rejoinedCoreManager = isFirstPromoted ? secondSurvivorCoreManager : firstSurvivorCoreManager;
            UnityTransportManager rejoinedTransportManager = isFirstPromoted ? secondSurvivorTransportManager : firstSurvivorTransportManager;
            ushort promotedPort = isFirstPromoted ? firstSurvivorPort : secondSurvivorPort;

            Assert.AreEqual(NewfarmMigrationState.Hosting, promotedHostMigration.Migration.State, $"No survivor was promoted: the first is [{firstSurvivorHostMigration.Migration.State}], the second [{secondSurvivorHostMigration.Migration.State}].");
            Assert.AreEqual(NewfarmMigrationState.Playing, rejoinedHostMigration.Migration.State, "The other survivor is not playing in the session.");
            Assert.IsTrue(IsClientAuthenticated(rejoinedCoreManager), "The other survivor was never admitted by the promoted peer.");
            Assert.AreEqual($"{LoopbackAddress}:{promotedPort}", ((FacepunchSockets)rejoinedTransportManager.Transports[0]).RemoteAddress, "The other survivor did not rejoin at the promoted peer's address.");
            Assert.AreEqual(promotedHostMigration.SessionId, rejoinedHostMigration.SessionId, "The survivors are not in the same session.");
        }

        /// <summary>
        /// Creates a peer with a <see cref="FacepunchHostMigration"/> beside its transport, pointed at the case's directory.
        /// </summary>
        /// <param name="objectName">The name of the object.</param>
        /// <param name="directoryPort">The port the directory listens on.</param>
        /// <param name="unityTransportManager">The peer's transport manager.</param>
        /// <param name="facepunchHostMigration">The peer's host migration component.</param>
        /// <returns>The peer's core manager.</returns>
        /// <remarks>The component reads its settings when the transport is added, so they are set before that.</remarks>
        private UnityCoreManager NewHostMigrationPeer(string objectName, ushort directoryPort, out UnityTransportManager unityTransportManager, out FacepunchHostMigration facepunchHostMigration)
        {
            UnityCoreManager unityCoreManager = NewPeer(objectName, out unityTransportManager, out FacepunchTransport _);

            facepunchHostMigration = unityCoreManager.gameObject.AddComponent<FacepunchHostMigration>();
            facepunchHostMigration.DirectoryAddress = LoopbackAddress;
            facepunchHostMigration.DirectoryPort = directoryPort;
            facepunchHostMigration.HostedAddress = LoopbackAddress;

            return unityCoreManager;
        }

        /// <summary>
        /// Adds a host migration peer's transport and switches it to IP addressing, listening on its own port and joining another.
        /// </summary>
        /// <param name="unityTransportManager">The peer's transport manager.</param>
        /// <param name="port">The port the peer listens on when it hosts.</param>
        /// <param name="remotePort">The port of the host the peer joins first.</param>
        /// <returns>The coroutine driving the add.</returns>
        private static IEnumerator AddHostMigrationTransport(UnityTransportManager unityTransportManager, ushort port, ushort remotePort)
        {
            yield return WaitForTask(unityTransportManager.EnsureAddedAsync());

            FacepunchSockets facepunchSockets = (FacepunchSockets)unityTransportManager.Transports[0];
            facepunchSockets.Addressing = FacepunchAddressing.IpAddress;
            facepunchSockets.SetPort(port);
            facepunchSockets.RemoteAddress = $"{LoopbackAddress}:{remotePort}";
        }

        /// <summary>
        /// Returns whether a session has moved to one survivor and the other has rejoined it.
        /// </summary>
        /// <param name="promotedHostMigration">The survivor expected to host.</param>
        /// <param name="rejoinedCoreManager">The survivor expected to have rejoined.</param>
        /// <param name="rejoinedHostMigration">That survivor's host migration component.</param>
        /// <returns><see langword="true"/> once the first hosts and the second is playing and admitted.</returns>
        private static bool IsHandedOver(FacepunchHostMigration promotedHostMigration, UnityCoreManager rejoinedCoreManager, FacepunchHostMigration rejoinedHostMigration) =>
            promotedHostMigration.Migration.State is NewfarmMigrationState.Hosting && rejoinedHostMigration.Migration.State is NewfarmMigrationState.Playing && IsClientAuthenticated(rejoinedCoreManager);

        /// <summary>
        /// Runs a directory on a free loopback port for the case, on its own thread.
        /// </summary>
        /// <returns>The port the directory listens on.</returns>
        private ushort StartDirectory()
        {
            ushort directoryPort = ProbeFreeUdpPort();

            _newfarmServer = new(new() { Port = directoryPort });
            _directoryCancellationTokenSource = new();

            CancellationToken cancellationToken = _directoryCancellationTokenSource.Token;
            NewfarmServer newfarmServer = _newfarmServer;
            _directoryThread = new(() => newfarmServer.Run(cancellationToken)) { IsBackground = true, Name = "Facepunch Host Migration Play Test Directory" };
            _directoryThread.Start();

            return directoryPort;
        }

        /// <summary>
        /// Stops the case's directory, if it ran one.
        /// </summary>
        private void StopDirectory()
        {
            if (_newfarmServer is null)
                return;

            // The loop is let finish before the server it runs is disposed under it.
            _directoryCancellationTokenSource.Cancel();
            _directoryThread.Join(2000);
            _newfarmServer.Dispose();
            _directoryCancellationTokenSource.Dispose();

            _newfarmServer = null;
            _directoryCancellationTokenSource = null;
            _directoryThread = null;
        }
    }
}
