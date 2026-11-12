---
name: nucleus-facepunch
description: Covers the Nucleus Steam transport built on Facepunch.Steamworks (Steam Networking Sockets) for games on Steam. Explains FacepunchSockets and the Unity FacepunchTransport component, addressing (SteamId through Valve's relay, IpAddress dialled directly over IPv4 or IPv6, FakeIp for game servers), which Steam interface is used (SteamClient or SteamServer, whichever the game initialized), ServerSteamId and ServerAddress, who initializes, pumps and shuts down Steam, finding the host through a Steam lobby (SteamMatchmaking.CreateLobbyAsync, OnLobbyEntered, Lobby.Owner, OnGameLobbyJoinRequested, +connect_lobby) and the Lobbies sample, SteamID identity, SteamTicketAuthenticator, ConnectingClientFilter, PerConnectionReceiveEnabled for IL2CPP servers, the minimum send rate of a SteamId server, slow leaves over SteamId, SetNetworkConditions on Steam, TryGetLinkStatus and SteamLinkStatus, send rate and send buffer settings, and testing on App ID 480. Load it when a project uses Facepunch.Steamworks for multiplayer, when code touches any of these names, when a Steam client cannot connect, or when someone asks how to join a friend, invite through Steam or run a Steam dedicated server with Nucleus.
---

# Nucleus over Steam with Facepunch.Steamworks

The general connection model (transports, Connection lifetime, events, authentication, kicks) belongs to **nucleus-transports-and-connections**. This skill covers only what is specific to Steam through Facepunch.Steamworks.

## Setup

- Facepunch.Steamworks 2.5.2 and Steam's natives ship inside the package (`Runtime/Plugins/Facepunch.Steamworks`). A project with its own copy must delete it, or the duplicate assemblies fail the compile. Steamworks.NET cannot be in the same project: both declare the `Steamworks` namespace. The assembly builds for the editor and Windows, Linux and macOS standalone only.
- Put `FacepunchTransport` on the `UnityCoreManager` GameObject. `UnityTransportManager` adds it in place of the default `SynapseTransport`.
- The transport never initializes, pumps or shuts down Steam. The game does: `SteamClient.Init(appId, false)` before the first start and `SteamClient.RunCallbacks()` every frame (or `SteamClient.Init(appId)` to let Facepunch pump on its own thread). Facepunch does not read `steam_appid.txt` for the App ID, so pass it. A start with Steam down fails with a logged error.
- Shut Steam down only at quit, after `UnityTransportManager.ShutdownAll()` and a moment after the transports close, so closes reach the peer. `SteamClient.Shutdown()` straight after sockets close can hang inside Steam. Keep Steam up across scene loads and leaves. With domain reload off, a play session that exits without `Shutdown` makes the next `Init` throw "already initialized"; adopt the running Steam instead (`SteamClient.IsValid`), as the Shared sample's `SteamBootstrap` does.
- For testing, App ID 480 (Spacewar) with `steam_appid.txt` in the project folder (and beside the executable in a build).

Confirm any API against the source in the package (`Packages/com.maxkratt.nucleus.facepunch/Runtime/`, or `Library/PackageCache/` when installed by git URL) before using it.

## Pick a shape

| Shape | `Addressing` | Steam the game initializes | The client sets |
|---|---|---|---|
| Player hosts, friends join | `SteamId` (default) | `SteamClient` | `ServerSteamId` = the host's SteamID64 |
| Dedicated server, IP hidden behind Valve's relay | `FakeIp` | `SteamServer` on the server | `ServerAddress` = the server's `ServerFakeAddress` |
| Dedicated server on a public port, or two processes on one Steam account | `IpAddress` | `SteamServer` (or `SteamClient` for local tests) | `ServerAddress` = `"ip:port"` or `"[ipv6]:port"` |

- There is no Game Server toggle: the transport uses `SteamClient` when it is valid, else `SteamServer`. A process wanting both must `SteamClient.Init` first; the other order throws `VersionMismatch`.
- A host calls `StartHostAsync()`; its own client pairs in process whatever `ServerSteamId` holds. A pure client sets `ServerSteamId` or `ServerAddress` on the component (both write through and apply at the next start), then calls `StartClientAsync()`.
- A dedicated server needs `SteamServer.Init(appId, init, false)` (with a mod directory and description set), `SteamServer.LogOnAnonymous()` and `SteamServer.RunCallbacks()` every frame. Facepunch has no logon by login token, so its SteamID changes every run; advertise it in the server list or a fake address, never a stored SteamID.
- `SteamId` between two machines needs two Steam accounts. One account across two processes needs `IpAddress`.
- `SteamId` links are not relay only: Facepunch exposes no setting for it, so players may connect directly and learn each other's IP addresses. Use `FakeIp` behind a game server where that matters.

## To find the host through a lobby

Nucleus ships no lobby code; the lobby only tells a joiner which SteamID to dial.

1. The host calls `StartHostAsync()`, then `SteamMatchmaking.CreateLobbyAsync(maxMembers)`, then sets the lobby type (`SetFriendsOnly`, `SetPublic`): Facepunch creates every lobby invisible.
2. A joiner enters the lobby: from an invite or the friends list while the game runs (`SteamFriends.OnGameLobbyJoinRequested`, then `lobby.Join()`), or from `+connect_lobby <id>` when Steam launched the game to accept an invite (read it from `Environment.GetCommandLineArgs()`; it is Steam's flag, not a Nucleus one).
3. When entered (`SteamMatchmaking.OnLobbyEntered`, or the `Join()` result), the joiner reads `lobby.Owner.Id`, skips it when that is its own SteamID (the creator gets the event too), sets `ServerSteamId` to it and calls `StartClientAsync()`.
4. On leaving, call `lobby.Leave()` and `ShutdownAll()`. Unsubscribe Facepunch's static events in `OnDestroy`.

The Lobbies sample (`LobbiesPanel`) shows the whole flow, including joining by typed lobby ID. The Lobby Browser sample searches with `SteamMatchmaking.LobbyList` and estimates ping from a published `NetPingLocation`.

## Identity and admission

- On every addressing, `Connection.RemoteAddress` on the server is the client's Steam-authenticated SteamID64, so the default authenticator already sets `Identity` to it. Do not parse it: call `FacepunchSockets.TryGetSteamId(connection or connectionId, out ulong steamId)`, or `TryGetConnection(steamId, out connection)` for the reverse. Get the `FacepunchSockets` from `connection.Transport`.
- A server knows every client's SteamID and a client knows its own. For a client to know the others, tick Share Steam IDs on the server (`ShareSteamIdsEnabled`, off by default, since a SteamID links to a public profile). The IDs arrive after the client joins, so read `TryGetSteamId` where needed and subscribe to `SteamIdChanged` (`uint connectionId, ulong steamId`, `UnsetSteamId` when the client left). Never let a client report its own SteamID.
- To also have Steam confirm the player owns the game and is not banned, tick Ticket Authentication on server and clients (`SteamTicketAuthenticator`). If only the server has it on, the client is dropped after `ServerManager.AuthenticationTimeoutSeconds` (30 s by default); if only the client has it on, it joins unchecked.
- To refuse by SteamID before the engine sees the client, set `FacepunchSockets.ConnectingClientFilter`, a `bool (ulong steamId)` called on the network loop thread.
- Maximum Connections (at least 1 in the inspector) refuses at accept once the server is full. A host's own client takes one place.

## Behaviour worth knowing

- **IL2CPP servers:** turn on Per-Connection Receive (`PerConnectionReceiveEnabled`). Facepunch's `SocketManager.Receive` allocates 232 bytes per message under IL2CPP; receiving each client through its own call avoids it. No difference on Mono.
- **SteamId servers send at the minimum send rate**, 256 KiB/s by default, not the maximum. Large messages and bursts to clients are slow there (50 MiB took 206 s). Raise Minimum Send Rate (`SendRateMinimumBytesPerSecond`) within the host's upload across all clients.
- **A leave over SteamId reaches the server 3 to 8 seconds late**, against milliseconds over `IpAddress`. Send a game message before leaving if the server must react at once.
- Every send setting and timeout is process wide (Facepunch has no per-connection options), applied at each start.
- Each message carries its channel in a one byte header, so `DatagramOverheadBytes` is 1.
- Kick reasons arrive: a kick closes with Steam's linger. Close reasons are logged once per drop.
- A reliable send Steam refuses (send buffer full) closes the link and is logged naming the send buffer. Raise `SendBufferSizeBytes` for large bursts.
- `SetNetworkConditions` simulates latency and loss only; jitter, reordering and duplication are logged as ignored. It is process wide, and the host's own client is unaffected.
- A FakeIp grant can come seconds after a game server's logon; `ServerFakeAddress` is null until then, and a slow grant logs a warning, then applies.
- Call Facepunch only while `SteamClient.IsValid` (or `SteamServer.IsValid`): some calls, `NetAddress.ToString()` among them, crash the process after `Shutdown`.

Every field, default and launch argument: [references/settings.md](references/settings.md).
