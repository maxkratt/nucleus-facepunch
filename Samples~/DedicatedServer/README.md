# Dedicated Server

A headless Steam game server reached by a fake IP through Steam's relay, with ticket authentication, listed in Steam's server browser; and a client that lists those servers and joins one.

## What it shows

- `Scenes/SteamDedicatedServer.unity`: `SteamBootstrap` with its game server option on (anonymous logon), `FacepunchTransport` on Fake IP with ticket authentication, the transport manager starting the server automatically, and `DedicatedServerListing` setting the server name, map, player limit and game tags through `SteamServer`. The fake address Steam grants goes into the game tags, so a browsing client reads it from the server's entry.
- `Scenes/SteamServerBrowser.unity`: `ServerBrowserPanel` listing this sample's servers with `ServerList.Internet` (filtered by game tag) or `ServerList.LocalNetwork`, with name, map, players and ping, and joining one by its fake address; a fake address can also be pasted. The transport is on Fake IP with ticket authentication to match the server.

## Requirements

- Nucleus.
- The Shared sample (it holds `SteamBootstrap`), imported beside this one.
- The App ID is set on `SteamBootstrap` (480 for testing). The server needs no Steam client; the browser does.

## Testing

1. Build `SteamDedicatedServer.unity` alone as a Windows or Linux build (Dedicated Server build target, or any build run with `-batchmode -nographics`) and run it. Its log shows the game server logging on and the fake address Steam granted.
2. On a machine logged into Steam, open `SteamServerBrowser.unity`, press Play and **Refresh internet**. Steam's master server can take a minute or two to list a new server. Press **Join** beside it.
3. Or paste the fake address from the server's log into the address field and press **Join address**.
4. Stop the server; the client returns to the browser.

`SteamBootstrap` starts either a game server or the Steam user, not both, so run the server and the browser as two processes.

## Notes

- Steam grants a new fake address every time the server starts, which is why it is published in the listing rather than configured.
- Facepunch.Steamworks logs a game server on anonymously only, so the server gets a new SteamID each run. Some setups list only servers that logged on with a login token; the pasted address works either way.
- The player count Steam lists comes from the auth sessions the server opens, which ticket authentication does for each player.
