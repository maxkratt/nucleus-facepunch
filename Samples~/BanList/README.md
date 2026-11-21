# Ban List

Keeping players out by SteamID: a host kicks, bans or blacklists a connected player, and a refused client is told why.

## What it shows

- `FacepunchTransport.ClientAdmissionFilter`, set in `Awake` before the server listens. It runs before the client's ticket is waited for, so a refused client costs no auth session. It is only consulted with Ticket Authentication on.
- A ban, which lasts until the server stops.
- A blacklist, one SteamID per line in `FacepunchBanListBlacklist.txt` under `Application.persistentDataPath`. An admin can edit the file by hand; it is read again each time hosting starts.
- A reason that reaches the client before its disconnect: `DenyClient(connection, notice)` at admission, `Connection.Kick(notice)` for a player already in.

## Requirements

- Nucleus.
- The Shared sample (it holds `SteamBootstrap`), imported beside this one.
- Steam running and logged in, and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamBanList.unity`, and build it for the second account.
2. On the first account press **Host**, then **Copy my SteamID**.
3. On the second account paste it into **Host SteamID** and press **Join**.
4. On the host press **Kick**; the client shows the reason. Join again.
5. Press **Ban**, and try to join again; the client is refused with the reason. Press **Unban** and it can join.
6. Press **Blacklist**, then stop and host again; the player is still refused.

## What to look at

- `BanListPanel.OnClientAdmission`: the filter and the refusal.
- `BanNotice`: `IsAuthenticationRequired` is false, because a refusal reaches a client that was never authenticated.

## Limits

- Steam lobbies have no kick, so in a lobby game a ban only stops the server link; the honest client leaves the lobby when it drops.
- The filter runs before the ticket, so it sees only the player's SteamID and not the game's owner. A banned owner can return on an account borrowing the game through Family Sharing; a game that minds checks the owner in the ticket verdict.
