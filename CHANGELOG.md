# Changelog

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
