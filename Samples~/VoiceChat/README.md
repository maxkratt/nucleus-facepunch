# Voice Chat

Push to talk over Steam voice: hold V, and Steam records and compresses your voice; each chunk goes out as an unreliable Nucleus call on your player's object, the server relays it to the other players, and each of them decompresses it and plays it on your capsule's `AudioSource`. A capsule lights up green, and a speaking icon shows over its name tag, while its player talks.

## Requirements

- Nucleus and the Input System package.
- The Shared sample (it holds `SteamBootstrap`), imported beside this one.
- Steam running and logged in, a microphone Steam can use (Steam > Settings > Voice), and `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

## Setup

1. Open `Scenes/SteamVoiceChat.unity`.
2. Open **Tools > Nucleus**, and on its Build tab press **Rebuild** beside Rebuild Network Prefab Collection once, so `VoiceChatPlayer` is registered.

## Testing with two accounts

1. On the first machine, press Play (or run a build) and press **Host**, then **Copy my SteamID** and send it to the second account.
2. On the second machine, logged into another Steam account, paste the SteamID and press **Join**.
3. Hold V on either machine and talk: the other machine hears you and sees your capsule light up and the speaking icon over your name. You never hear yourself.

## What to look at

- `VoiceChatPlayer.SendRecordedVoice`: `SteamUser.ReadVoiceData` into a reused stream, then `TrySendRpc(RpcTarget.Observers, Channel.Unreliable, ...)`. Unreliable because a lost chunk is a short gap, while a resent one arrives too late to play.
- `VoiceChatPlayer.PlayVoice`: `SteamUser.DecompressVoice` at Steam's optimal sample rate into a one second ring that a streamed `AudioClip` plays from, gathering 0.1 s before it starts so uneven arrival does not stutter. It uses Facepunch's pointer overload, since the array and stream overloads allocate on every chunk. A dedicated server only relays.
- Facepunch decompresses at `SteamUser.SampleRate`, one setting for the whole process, which this sample sets to `SteamUser.OptimalSampleRate`.
- Facepunch does not expose Steam's call that marks a player as speaking in game, so Steam's friends list is not told.
- Voice is not positional: set the `AudioSource` spatial blend on the prefab to make it so.
- `VoiceChatPanel` is the Quick Start panel (host, join by SteamID, leave), so this sample needs nothing but Shared.
