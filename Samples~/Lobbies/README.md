# Lobbies

Finding the host through a Steam lobby: the host starts its server and creates a lobby, and a player who enters it connects to the lobby owner's SteamID. The lobby only finds the host; the session runs over the transport like any other.

## What it shows

- Hosting a public, friends-only or private lobby.
- Inviting through the Steam overlay, and accepting an invite or "Join game" from Steam, whether the game is running (`SteamFriends.OnGameLobbyJoinRequested`) or started by Steam with `+connect_lobby <lobby id>`.
- Joining a friend who is in a lobby of this game (Refresh friends), or a lobby ID typed or pasted.
- The member list with Steam names and small avatars, kept current from Facepunch's lobby events.
- Lobby data: the host sets a game mode and map, and every member reads them.
- Leaving, and the host leaving: Steam passes the lobby to another member, but the server went with the host, so each member leaves the lobby once its client stops.

## Requirements

- Nucleus.
- The Shared sample (it holds `SteamBootstrap`), imported beside this one.
- Steam running and logged in, and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamLobbies.unity`. Build it for the second machine (invites and `+connect_lobby` start a build, not the editor).
2. On the first machine, pick a lobby type and press **Host lobby**.
3. Join from the second account in any of these ways: **Invite friends** on the host and accept in Steam; **Refresh friends** and **Join** beside the host; or **Copy lobby ID** on the host and paste it into **Lobby ID**.
4. Change the game mode or map on the host and press **Apply lobby data**; the member sees it.
5. Press **Leave** on the host; the member's client stops and it leaves the lobby too.

On App ID 480 (Spacewar) a public lobby is visible to every developer testing on it, so prefer friends-only or private while testing.

## What to look at

- `LobbiesPanel.Host`: `StartHostAsync`, then `SteamMatchmaking.CreateLobbyAsync`, so nobody can dial a server that is not yet listening. Facepunch creates the lobby invisible, so `OnLobbyCreated` sets the chosen type.
- `LobbiesPanel.JoinLobby`: `Lobby.Join`, then `FacepunchTransport.ServerSteamId` set to the lobby owner and `StartClientAsync`.
- `LobbiesPanel.OnConnectionLocalStateChanged`: a member leaves the lobby once its client stops.
