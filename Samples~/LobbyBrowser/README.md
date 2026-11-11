# Lobby Browser

A public lobby list with distance filtering and an estimated ping per lobby: hosts open public lobbies, and players search, pick one by its ping and join.

## What it shows

- Hosting a public lobby that publishes a name, a sample tag and the host's Steam ping location as lobby data.
- Searching with `SteamMatchmaking.LobbyList`, filtered to this sample's tag and to a distance (Close, Default, Far, Worldwide), sorted by ping.
- Estimating each lobby's ping without contacting its host: the host publishes `SteamNetworkingUtils.LocalPingLocation` as a string, and `SteamNetworkingUtils.EstimatePingTo` combines it with this peer's own. A host whose relay measurement is not ready yet awaits `SteamNetworkingUtils.WaitForPingDataAsync` before publishing.
- Joining a listed lobby, connecting to its owner's SteamID, and leaving it once the client stops.

## Requirements

- Nucleus.
- The Shared sample (it holds `SteamBootstrap`, which starts relay network access), imported beside this one.
- Steam running and logged in, and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamLobbyBrowser.unity`.
2. On the first machine, set a lobby name and press **Host public lobby**.
3. On the second machine, logged into another Steam account, pick a distance and press **Search**; the lobby is listed with its member count and estimated ping. Press **Join**.
4. **Leave** on the host stops its server; the player's client stops and it leaves the lobby too.

The estimate runs through Steam's relays, so it is not zero even for a lobby on the same machine. On App ID 480 every developer's public lobbies share one list, which is why searches filter to this sample's tag.

## What to look at

- `LobbyBrowserPanel.CreateLobbyQuery`: the tag, result count and distance filters. Facepunch has no call for Steam's default distance, so Default adds no distance filter.
- `LobbyBrowserPanel.EstimatePingMilliseconds`: the ping estimate per lobby.
- `LobbyBrowserPanel.PublishPingLocation`: the host's side.
