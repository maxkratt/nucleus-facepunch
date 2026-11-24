# Changelog

## Nucleus Facepunch.Steamworks 0.5.1
- A server started on Fake IP with `FacepunchHostMigration` now opens its session once Steam grants it an address, where before it ran without one and logged `ServerAddressUnavailable`.

## Nucleus Facepunch.Steamworks 0.5.0
- New host migration, with Nucleus Pro: `FacepunchHostMigration` beside `FacepunchTransport` keeps a session alive across the loss of its host through a newfarm directory, over SteamID, IP address or fake IP. Its Hosted Address names where an IP address host is reached.
- New `FacepunchSessionHost`, the Steam side of a handover for driving `NewfarmHostMigration` directly. A join the host refuses reports `CredentialUnreachable`; one nobody answers reports `LocalFailure`.
- `FacepunchSockets.ServerFakeAddress` reads `null` again once the server stops.

## Nucleus Facepunch.Steamworks 0.4.0
- New `FacepunchSockets.SendFailed` event, raised with the side, the Connection, the channel and Steam's `Result` each time Steam refuses a message. `RefusedReliableSendCount` and `RefusedUnreliableSendCount` count the same refusals and are now read only.
- New `FacepunchSockets.ConnectTimeoutMilliseconds`: the longest a client waits for its link to open before giving up. Zero, the default, leaves it to the Connecting Timeout. It is held by the transport, so unlike Steam's timeout it never touches links already open.
- New `ClientAdmissionFilter` on `SteamTicketAuthenticator` and `FacepunchTransport`, a `bool (Connection, ulong steamId)` asked about each connecting client before it waits for its ticket. A client it refuses can be told why through `ServerManager.DenyClient(connection, notice)`.
- New Ban List sample: kick, ban and blacklist players by SteamID, and tell a refused client why.
- New Quick Match sample: one button matchmaking through invisible Steam lobbies, then a session only the lobby's members can join.

## Nucleus Facepunch.Steamworks 0.3.1
- Simulated latency and loss set with `SetNetworkConditions` are cleared from Steam when the transport shuts down. Steam keeps them for the whole process, so they used to carry into the next session.
- `FacepunchSockets.TryGetLinkStatus` on a client now reads the link through the client's server Connection as well as its own Connection.
- A server whose Steam the game shuts down while it runs now stops without a `NullReferenceException`.

## Nucleus Facepunch.Steamworks 0.3.0
Requires Nucleus 0.6.0 or later.
- Members renamed to match Nucleus Steamworks: `ServerSteamId` is now `RemoteSteamId` and `ServerAddress` is now `RemoteAddress`, on `FacepunchTransport` and `FacepunchSockets`. `FacepunchSockets.RemoteAddress` defaults to an empty string. The inspector labels them Remote SteamID, Remote Address and Remote Fake Address.
- Send rates are set in kilobytes (1,024 bytes) per second: `SendRateMinimumKilobytesPerSecond` (default 256) and `SendRateMaximumKilobytesPerSecond` (default 4096) replace the `BytesPerSecond` members on `FacepunchSockets` and the component.
- Scenes saved with an earlier version lose the component's server SteamID, server address and send rates, which have to be set again.
- New `FacepunchSockets.ServerSteamId`, read only: the SteamID of the server this peer runs, or `UnsetSteamId` while it is not listening. The inspector's server row shows it.
- `FacepunchTransport` gains `Addressing` (read only), `FacepunchSockets` (the engine transport once added) and `ConnectingClientFilter`, which writes through to the transport like `RemoteSteamId`.
- `FacepunchSockets.Addressing` is now a property, and `SteamLinkStatus` has read-only properties in place of its fields, under the same names.
- Samples: updated for the renames.

## Nucleus Facepunch.Steamworks 0.2.1
- Samples: the panels compile against Nucleus versions without `LocalConnectionState.Error` and `TimedOut`. A failed or timed-out connection ends in `Disconnected`, which the panels already handle.

## Nucleus Facepunch.Steamworks 0.2.0
- Lobbies and Lobby Browser samples: the host names itself the lobby's game server with `Lobby.SetGameServer`, and a joiner connects to that game server rather than the lobby owner. A player who enters a lobby whose host has left is told so at once instead of trying to connect.
- Shared sample: new `PlayerMover`, used by the Quick Start and Voice Chat players. It moves the player freely instead of within a radius, lets it fall off the ground, and puts it back above the origin once it falls below a set height. It compiles only with the Input System package.
- Voice Chat sample: the player can now move.
- Quick Start and Voice Chat samples: the player's `NetworkTransform` uses Interval with extrapolation off, so other players' capsules move steadily instead of overshooting and correcting.
- Sample panels: wider field labels and row buttons.

## Nucleus Facepunch.Steamworks 0.1.1
- With Ticket Authentication on, a client that leaves and joins again is no longer dropped by the server. Stopping and starting the transport used to leave the previous ticket authenticator registered beside the new one.
- With Ticket Authentication on, a client now sends its ticket only once Steam reports it ready, rather than straight after asking for it, when Steam could still refuse it.
- Shared sample: `PlayerNameTag` now shows a joining client's name. The server wrote the tag before the transport had recorded that client's SteamID, so the tag stayed empty; it now also follows `FacepunchSockets.SteamIdChanged`.

## Nucleus Facepunch.Steamworks 0.1.0
First release. Requires Nucleus 0.5.0 or later and Unity 6.5 or later. Facepunch.Steamworks 2.5.2 and Steam's native libraries for Windows, Linux and macOS are included.
- Steam Networking Sockets transport for Nucleus over Facepunch.Steamworks, addressing by Steam ID, IPv4 or IPv6 address, or Fake IP, on whichever Steam interface the game initialized.
- Reliable messages past Steam's 512 KiB limit are split and rejoined, and a send queue keeps a full Steam buffer from closing the link.
- Kicks deliver their reason before the link closes, and every drop logs Steam's close reason.
- Optional receive per connection on the server, which avoids a per-message allocation in IL2CPP builds.
- Optional Steam auth ticket validation, a gate on connecting SteamIDs, and a per-client packet size limit on the server.
- SteamID lookups by connection, with optional sharing of every client's SteamID to the other clients.
- Link status, simulated latency and loss, minimum and maximum send rates, and launch arguments.
- `FacepunchTransport` component, with an inspector that shows Steam's state and the running server's SteamID, address or fake address.
- Samples: Quick Start, Lobbies, Lobby Browser, Dedicated Server and Voice Chat, plus the Shared sample they depend on.
- An agent skill for the transport.
