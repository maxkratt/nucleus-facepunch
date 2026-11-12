# Facepunch transport settings

## FacepunchTransport component (Unity, `Nucleus.Integrations.Facepunch`)

Values are copied into the engine `FacepunchSockets` when it is added. The `ServerSteamId` and `ServerAddress` script properties also write through to that engine transport once it exists, applied at the next client start; `BindAddress` does the same, applied at the next server start. Zero in a send setting or timeout leaves Steam's value alone. Timeouts and send settings are process wide, since Facepunch has no per-connection options.

| Serialized field | Default | Engine target |
|---|---|---|
| `_addressing` | `FacepunchAddressing.SteamId` | `FacepunchSockets.Addressing` (`SteamId`, `IpAddress`, `FakeIp`) |
| `_serverSteamId` | `0` (join own server) | `FacepunchSockets.ServerSteamId`, a SteamID64 (`76561198012345678`) |
| `_serverAddress` | empty (join own server) | `FacepunchSockets.ServerAddress`: `"ip"`, `"ip:port"`, `"[ipv6]:port"` or `localhost` (the IPv4 loopback); without a port, `_port` is used; host names are not looked up; a fake IP is always IPv4 |
| `_port` | `27015` | `FacepunchSockets.SetPort`, so both `VirtualPort` (`SteamId`) and `Configuration.Port` (the UDP port an `IpAddress` server listens on); server and clients must agree; `FakeIp` ignores it |
| `_bindAddress` | empty (every interface) | `Transport.SetBindAddress`, the local IPv4 or IPv6 address (or `localhost`) an `IpAddress` server listens on; scoped link-local addresses are refused |
| `_connectingTimeoutSeconds` | `10` | `Configuration.ConnectingTimeoutSeconds`, enforced by Steam |
| `_idleTimeoutSeconds` | `15` | `Configuration.ConnectedTimeoutSeconds`, enforced by Steam |
| `_maximumConnections` | `250` (at least 1) | `Transport.SetMaximumConnections`; a full server refuses at accept, and a host's own client takes one place. The engine's `0` (any number) is reachable only from code |
| `_maximumClientPacketBytes` | `8388608` (8 MiB) | `FacepunchSockets.MaximumClientPacketBytes`; a client whose packet (a split reliable one included) would pass it is disconnected; `0` (`UnsetMaximumClientPacketBytes`) sets no limit. Never applied to what a client receives |
| `_perConnectionReceiveEnabled` | `false` | `FacepunchSockets.PerConnectionReceiveEnabled`; the server receives each client through its own `ConnectionManager.Receive` instead of `SocketManager.Receive`, which allocates 232 bytes per message under IL2CPP. Recommended for IL2CPP servers. Read at the next server start |
| `_sendRateMinimumBytesPerSecond` | `262144` (256 KiB/s) | `FacepunchSockets.SendRateMinimumBytesPerSecond`; a `SteamId` server sends each client at this rate, so it sets how fast large messages reach clients there |
| `_sendRateMaximumBytesPerSecond` | `4194304` (4 MiB/s) | `FacepunchSockets.SendRateMaximumBytesPerSecond` (Steam's default is 256 KiB/s) |
| `_sendBufferSizeBytes` | `2097152` (2 MiB) | `FacepunchSockets.SendBufferSizeBytes`; reliable messages past it wait in the transport, and only one piece larger than it is refused and closes the link. Keep it at 512 KiB or more |
| `_ticketAuthenticationEnabled` | `false` | Installs a `SteamTicketAuthenticator`; server and clients both need it on |
| `_shareSteamIdsEnabled` | `false` | `FacepunchSockets.ShareSteamIdsEnabled`; the server tells every client each other client's SteamID. Only the server's value counts |

## FacepunchSockets (engine)

Engine-only members: `ConnectingClientFilter` (`bool (ulong steamId)`, `null` admits anyone while there is room), `SteamIdChanged`, `ServerFakeAddress` (read only, `null` until Steam grants a `FakeIp` server its address), `TryGetLinkStatus`, `TryGetSteamId`, `TryGetConnection`, `ServerReceiveMicroseconds`, `RefusedReliableSendCount`, `RefusedUnreliableSendCount`, `LargestReliablePacketBytes`, `LargestUnreliablePacketBytes`, `UnsetSteamId` (`0`), `UnsetSendSetting` (`0`). `DatagramOverheadBytes` is `1`: Facepunch's receive callbacks report neither flags nor lane, so each message carries its channel in a header byte. Reliable messages past 512 KiB are split into pieces of 512 KiB less the header and rejoined. `SetNetworkConditions` applies `LatencyMilliseconds` and `PacketLossChance` on the send side, through `SteamNetworkingUtils.FakeSendPacketLag` and `FakeSendPacketLoss`; the other fields are ignored with a warning.

## Launch arguments

| Flag | Constant | Sets |
|---|---|---|
| `port` | `Transport.PortArgument` | `Configuration.Port` and `VirtualPort` |
| `address` | `FacepunchSockets.AddressArgument` | `ServerAddress` |
| `steamid` | `FacepunchSockets.SteamIdArgument` | `ServerSteamId` |

Nothing calls `ApplyCommandLineArguments()` for you; see **nucleus-transports-and-connections**. Steam's own `+connect_lobby <id>` names a lobby, not a host, so the game reads it (as the Lobbies sample's `LobbiesPanel` does).
