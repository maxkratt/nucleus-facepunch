# Quick Start

The smallest Steam session: one peer hosts, a friend pastes the host's SteamID and joins through Steam, and each player moves a capsule with WASD or the arrow keys.

## Requirements

- Nucleus and the Input System package.
- The Shared sample (it holds `SteamBootstrap`), imported beside this one.
- Steam running and logged in, and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

## Setup

1. Open `Scenes/SteamQuickStart.unity`.
2. Open **Tools > Nucleus**, and on its Build tab press **Rebuild** beside Rebuild Network Prefab Collection once, so `QuickStartPlayer` is registered.

## Testing with two accounts

1. On the first machine, press Play (or run a build) and press **Host**, then **Copy my SteamID** and send it to the second account.
2. On the second machine, logged into another Steam account, paste the SteamID and press **Join**.
3. Each player's capsule appears in its own colour; move yours and watch it on the other screen. **Leave** stops the session.

## What to look at

- `NetworkManager` holds the whole setup: `UnityCoreManager`, `UnityTransportManager`, `FacepunchTransport` (Addressing: SteamID), `SteamBootstrap`, `NetworkPlayerSpawner` and `QuickStartPanel`.
- `QuickStartPanel.Join` is all a join takes: set `FacepunchTransport.ServerSteamId`, then `StartClientAsync`.
- `QuickStartPlayer` moves the capsule only on the peer that controls it; `NetworkTransform` replicates it to the rest.
