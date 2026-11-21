# Quick Match

One Find Match button: search for an open lobby, widening the distance until one is found, or create a lobby and wait. When the lobby is full, every member loads the game and the session starts.

## What it shows

- Filtered lobby searches (sample key, game version, open state, one free slot) at Close, Default, Far and Worldwide, through `SteamMatchmaking.LobbyList`.
- Invisible lobbies: Steam's searches return them, but friends lists do not show them.
- No server until the lobby is full, so matchmaking costs no session.
- Two players who search together both create a lobby; a lone owner keeps searching and moves into any waiting lobby with a lower SteamID.
- Carrying a lobby across a scene load, then `Lobby.SetGameServer` and `SteamMatchmaking.OnLobbyGameCreated` so nobody dials a server that is not listening.
- Only lobby members get in, through `FacepunchTransport.ClientAdmissionFilter`.
- Closing the lobby: a client leaves once it is admitted, and the host leaves once it is alone.

## Requirements

- Nucleus.
- The Shared sample (it holds `SteamBootstrap`), imported beside this one.
- Steam running and logged in, and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.
- **Both scenes in the Build Profile.** Importing a sample does not add them. Select the `QuickMatchPanel` in `Scenes/QuickMatch.unity` and press **Add both scenes to the build**.

## Testing with two accounts

1. Open `Scenes/QuickMatch.unity`, add the scenes as above, and build it for the second account.
2. Press **Find Match** on one account, then on the other. Both load the game and connect.
3. Also try both pressing it together, **Cancel** while waiting, the owner leaving while waiting and while loading, and a second match after **Leave match**.

Distance widening cannot be tested on one machine.

## What to look at

- `QuickMatchPanel.FindMatch`: each distance is searched with `LobbyQuery.RequestAsync` and each result joined with `Lobby.Join`, then a lobby is created if none had room. A counter tells a step that was awaiting Steam when the user pressed **Cancel**.
- `QuickMatchPanel.ReadLobby`: the owner, read on every update, starts a full lobby.
- `QuickMatchPanel.ApplyMergeResults`: the rule that lets exactly one of two waiting players move.
- `QuickMatchGame.TryStartClientFromLobby`: reading the game server for a member that loaded after the host named it.
- `QuickMatchGame.OnClientAdmission`: the lobby member check.
