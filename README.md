# Nucleus Facepunch.Steamworks

A Steam Networking Sockets transport for the Nucleus netcode engine, built on [Facepunch.Steamworks](https://github.com/Facepunch/Facepunch.Steamworks) 2.5.2. Players host and join each other by SteamID through Valve's relay, dedicated servers run as Steam game servers behind a fake IP, and two processes can also dial each other by IPv4 or IPv6 address.

## Which Steam transport

There are two Nucleus Steam transports, and a project uses one: both libraries declare the `Steamworks` namespace, so they cannot be installed together.

- **This one** if your game already uses Facepunch.Steamworks, or you prefer its shorter async API.
- **[Nucleus Steamworks.NET](https://github.com/maxkratt/nucleus-steamworksnet)** if your game uses Steamworks.NET, or you need relay-only Steam ID links, a game server login token, or the per-message channel without a header byte.

What Facepunch.Steamworks 2.5.2 does not expose, and what this transport does instead:

| Feature | Here |
|---|---|
| Relay-only Steam ID links | Not available. Steam ID links follow each player's Steam setting for direct connections, so **a player may learn another player's IP address**. Use Fake IP behind a game server where that matters. |
| Game server login token | Not available: a game server logs on anonymously only, so its SteamID changes each run. |
| Choosing the Steam interface | There is no Game Server toggle. The transport uses whichever Steam the game initialized: `SteamClient`, or `SteamServer` on a dedicated server. With both up, the client's interface wins. |
| Per-connection options | Timeouts, send rates and the send buffer are process wide, applied at each start, and also affect any other Steam connection in the process. |
| Message flags on receive | Each message carries its channel in one header byte, so `DatagramOverheadBytes` is 1. |
| Zero-copy send | Each send copies the payload and header into a reused buffer. No garbage either way. |
| Simulated jitter, reordering and duplication | Only latency and loss are simulated; the others are logged as ignored. |
| Steam's estimated send rate in the link status | Left out; every other field is there. |
| Telling the friends list a player is speaking | Not available (Voice Chat sample). |

## Requirements

- Unity 6.5 (6000.5) or later, building for Windows, Linux or macOS.
- Nucleus 0.5.0 or later, in `Assets/Nucleus`.
- Steam running and logged in. App ID 480 (Spacewar) works for testing: put `steam_appid.txt` holding `480` in the project folder, and beside the executable in a build.

Facepunch.Steamworks 2.5.2 and Steam's native libraries are included in the package, under `Runtime/Plugins/Facepunch.Steamworks` with Facepunch's licence. **A project that already holds Facepunch.Steamworks must delete its own copy**, or the duplicate assemblies fail the compile.

## Install

1. Import [Nucleus](https://nucleus-networking.com/).
2. In the Package Manager, **+ > Install package from git URL**, add this package:
   ```
   https://github.com/maxkratt/nucleus-facepunch.git
   ```
   Optionally append `#v0.x.y` to the URL to choose a specific version.

## Quick start

1. Put `FacepunchTransport` on the `UnityCoreManager` GameObject. `UnityTransportManager` uses it in place of the default transport.
2. Initialize Steam in your game: `SteamClient.Init(appId, false)` before the first start and `SteamClient.RunCallbacks()` every frame (or `SteamClient.Init(appId)` to let Facepunch pump on its own thread). The transport never does this itself. The Shared sample's `SteamBootstrap` does it for you.
3. The host calls `StartHostAsync()`.
4. A client sets `FacepunchTransport.ServerSteamId` to the host's SteamID64 and calls `StartClientAsync()`.

| Shape | Addressing | Steam the game initializes | The client sets |
|---|---|---|---|
| Player hosts, friends join | Steam ID (default) | `SteamClient` | `ServerSteamId` |
| Dedicated server behind Valve's relay | Fake IP | `SteamServer` on the server | `ServerAddress` (the server's fake address) |
| Public port, or two processes on one account | IP Address | `SteamServer`, or `SteamClient` for local tests | `ServerAddress` (`ip:port` or `[ipv6]:port`) |

A dedicated server runs on `SteamServer.Init(appId, init, false)`, `SteamServer.LogOnAnonymous()` and `SteamServer.RunCallbacks()` every frame, with no Steam client on the machine. A client always uses the user's Steam. A process that wants both must call `SteamClient.Init` first: the other order throws `VersionMismatch`. Steam ID addressing between two machines needs two Steam accounts; one account across two processes needs IP Address.

### Shutting Steam down

Shut Steam down only when the game quits, after `UnityTransportManager.ShutdownAll()` and a moment after the transports close, so closes reach the other side; otherwise the peer waits out its idle timeout. Keep Steam up across scene loads and leaves. Shutting Steam down straight after closing its sockets can hang inside Steam's own `SteamAPI_Shutdown`. The transport makes no Steam call once Steam is down.

## FacepunchTransport

The Unity component. Its settings are copied into the engine transport, `FacepunchSockets`, when the transport manager adds it, so changes made while playing are discarded. `ServerSteamId`, `ServerAddress` and `BindAddress` are also script properties that write through at any time and apply at the next start. The status box at the top says whether Steam is initialized, and while a server runs it shows its SteamID, fake address or port with a Copy button.

### Connection

| Setting | Default | What it does |
|---|---|---|
| Addressing | Steam ID | How the server is reached: Steam ID through Valve's relay, IP Address dialled directly, or Fake IP, an address Steam grants a game server so its real IP stays hidden. |
| Server's SteamID | `0` | Steam ID only. The SteamID64 a client connects to, such as `76561198012345678`. Zero, or any value while this peer runs a server, joins this peer's own server. |
| Server's Address, Server's Fake Address | empty | IP Address and Fake IP. The `ip`, `ip:port`, `[ipv6]:port` or `localhost` (the IPv4 loopback) a client connects to; for a fake IP, the address the server logs. Host names are not looked up. Empty joins this peer's own server. |
| Virtual Port, Port | `27015` | Steam's virtual port by SteamID, or the UDP port the server listens on by IP address. Server and clients must agree. A fake IP ignores it. |
| Bind Address | empty | IP Address servers. The local IPv4 or IPv6 address, or `localhost`, to listen on. Empty listens on every interface. |

### Session

| Setting | Default | What it does |
|---|---|---|
| Maximum Connections | `250` | The most clients the server accepts at once; a full server refuses at accept. A host's own client takes one place. At least 1 here; the engine's 0 (any number) is reachable from code. |
| Maximum Client Packet | 8 MiB | The largest packet, a split reliable one included, a client may send the server. A client past it is logged and disconnected. Zero sets no limit. It never limits what a client receives. |
| Ticket Authentication | off | Admits a client only once Steam confirms its ticket: it owns the game and is not banned. Server and clients both need it on. A server with it on drops a client with it off after the engine's authentication timeout (30 s); a client with it on joins a server with it off unchecked. |
| Share Steam IDs | off | Tells every client the SteamID of every other client, for names, avatars and friend checks. Only the server's value counts. See [Getting a player's SteamID](#getting-a-players-steamid). |

### Advanced

Collapsed by default, with a summary of its values. Everything but Per-Connection Receive applies to the whole process.

| Setting | Default | What it does |
|---|---|---|
| Per-Connection Receive | off | Receives each client through its own native call instead of one poll group call. `SocketManager.Receive` allocates 232 bytes per received message in IL2CPP builds; this avoids it, at one extra native call per client per frame. **Recommended for IL2CPP servers.** It makes no difference to garbage on Mono. |
| Connecting Timeout | 10 s | How long a link may spend connecting before Steam gives up. Zero leaves Steam's value alone. |
| Idle Timeout | 15 s | How long a peer waits without hearing from the other side before the link drops. Steam keeps links alive on its own thread, so a game hitch does not trip it. Zero leaves Steam's value alone. |
| Minimum Send Rate | 256 KiB/s | The rate Steam never sends slower than on one connection. See [Send rate over Steam ID](#send-rate-over-steam-id). Zero leaves Steam's value alone. |
| Maximum Send Rate | 4 MiB/s | The rate Steam never sends faster than on one connection. Zero leaves Steam's value alone (256 KiB/s). |
| Send Buffer Size | 2 MiB | How much Steam may hold queued per connection. Reliable messages past it wait in the transport. Reliable messages go out in pieces of up to 512 KiB, so keep it at 512 KiB or more. Zero leaves Steam's value alone (512 KiB). |

### From code

These live on `FacepunchSockets`, which `AddToAsync` returns and `TransportManager` holds:

- `ConnectingClientFilter`, a `bool (ulong steamId)` called on the network loop thread before the engine sees a client. Return `false` to refuse it, for a ban list or lobby members only.
- `ServerFakeAddress`, the address Steam grants a Fake IP server once it is ready. A game server's grant can come after its logon, seconds after the start; a slow grant is logged as a warning and applied when it arrives.
- `TryGetLinkStatus(connection, out SteamLinkStatus)`, Steam's ping, connection quality and pending bytes for one link.
- `SetNetworkConditions` simulates latency and loss through Steam's own settings, mid-session too. They are process wide, and the host's own client is never delayed: judge lag from a separate client.
- `RefusedReliableSendCount`, `RefusedUnreliableSendCount`, `LargestReliablePacketBytes`, `LargestUnreliablePacketBytes` and `ServerReceiveMicroseconds`, for diagnostics.

### Launch arguments

Read only when the game calls `ApplyCommandLineArguments()` on the transport.

| Flag | Sets |
|---|---|
| `-port <n>` | Port and virtual port |
| `-address <ip:port>` | `ServerAddress` |
| `-steamid <id>` | `ServerSteamId` |

Steam's own `+connect_lobby <id>` names a lobby, not a host; the Lobbies sample reads it.

### Identity

On every addressing, a client's `Connection.RemoteAddress` on the server is its SteamID64, authenticated by Steam, so the default authenticator already sets `Identity` to it. The transport refuses unauthenticated peers on IP addresses too (`SteamNetworkingUtils.AllowWithoutAuth` is set to 0). Ticket Authentication adds the ownership and ban check on top, and a ticket is bound to the link's SteamID, so a borrowed or forged one is refused.

### Getting a player's SteamID

`FacepunchSockets.TryGetSteamId(connection, out ulong steamId)` returns a client's Steam-authenticated SteamID, and also accepts a connection id such as `NetworkSystem.ControllerConnectionId`. `TryGetConnection(steamId, out connection)` is the reverse. Get the `FacepunchSockets` from `connection.Transport`.

A server knows every client, and a client knows itself. To let clients look up each other, tick **Share Steam IDs** on the server. It is off by default because a SteamID links to the player's public Steam profile. The IDs arrive shortly after a client joins, so subscribe to `SteamIdChanged` as well as reading them; its `steamId` is `FacepunchSockets.UnsetSteamId` when a client leaves.

### Behaviour worth knowing

- Every drop logs Steam's close reason once, and a kick's reason arrives before the close.
- A reliable send that Steam refuses closes the link and logs a line naming the send buffer. Raise Send Buffer Size for large bursts, such as big late-join snapshots.
- Lanes are configured once per link, before any traffic: unreliable state gets three parts of the send rate to reliable's one while both wait.
- A client dialling before Steam's relay network is ready waits for it, up to 10 seconds.

#### Send rate over Steam ID

A server listening by Steam ID sends each client at the **minimum** send rate, not the maximum: at the default 256 KiB/s a burst of 3,000 messages of 1 KiB took 12.4 s and a 50 MiB message 206 s, against 0.5 s for the burst over IP Address. Clients, and servers on IP Address, send at the maximum. Raise Minimum Send Rate to deliver large messages faster, keeping it within the host's upload across all its clients, since every client gets the rate at once.

#### Leaving over Steam ID

A client that leaves a Steam ID server is dropped on the server 3 to 8 seconds later, with the right close reason, against a few milliseconds over IP Address. Steam's peer-to-peer route delivers the close late; the transport closes its side at once. A kick reaches the client in about half a second. Game logic that must react to a leave at once should send its own message before leaving.

## Samples

Import them from the package's **Samples** tab in the Package Manager. Import **Shared** first: every other sample needs it. Each sample has a README with its scene and a two account test.

| Sample | What it shows |
|---|---|
| [Shared](Samples~/Shared/README.md) | `SteamBootstrap` (initializing, pumping and shutting down Steam, or a game server), player name tags and the sample skin. |
| [Quick Start](Samples~/QuickStart/README.md) | Host or join by SteamID and move a capsule. Needs the Input System package. |
| [Lobbies](Samples~/Lobbies/README.md) | Create, join and leave Steam lobbies, invites, members, friends and lobby data. |
| [Lobby Browser](Samples~/LobbyBrowser/README.md) | Search public lobbies by distance and estimated ping. |
| [Dedicated Server](Samples~/DedicatedServer/README.md) | A Steam game server behind a fake IP, with a LAN and internet server browser. |
| [Voice Chat](Samples~/VoiceChat/README.md) | Push to talk Steam voice relayed through the server. Needs the Input System package. |

The Quick Start and Voice Chat samples spawn networked prefabs: after importing them, open **Tools > Nucleus** and press **Rebuild** beside Rebuild Network Prefab Collection on its Build tab.

## Agent skill

`Skills~/nucleus-facepunch` is a skill for AI coding agents covering setup, addressing, lobbies, identity and every setting. Copy the folder into your agent's skills folder, beside the Nucleus skills.

## Tests

The PlayMode tests (`Nucleus.Integrations.Facepunch.Tests`) run in the Test Runner when the package is listed under `testables` in `Packages/manifest.json`. They skip unless Steam is running.

<img src="https://raw.githubusercontent.com/AnkleBreaker-Studio/unity-mcp-plugin/main/icon.png" width="16" alt=""> Testing Powered by [AnkleBreaker MCP](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin).

## Licence

MIT, see [LICENSE.md](LICENSE.md). Facepunch.Steamworks is MIT, see [its licence](Runtime/Plugins/Facepunch.Steamworks/LICENSE.txt). Steam's native libraries are Valve's, distributed under the Steamworks SDK terms.
