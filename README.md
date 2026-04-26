# Shadow's Sirocco Modpack

A modpack that fixes several issues/bugs in the P2P version of Sirocco.
Each mod has a separate .dll file allowing you to enable/disable any of the mods by simply adding/removing the mod's .dll file from the game.

## How to Install

1. **Make sure you have the latest version of [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) installed to Sirocco**
2. **Download the Sirocco modpack from [latest releases](https://github.com/runi95/sirocco-mod/releases/latest)**

- And extract the `SiroccoModpack.zip` and add `Mods` + `UserLibs` to the root of your Sirocco directory (you can locate the correct directory from Steam by right clicking `Sirocco` > `Properties...` > `Installed Files` > `Browse...`)

4. **Download the latest version of [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET/releases/latest)**

- And add both `Windows-x64\Steamworks.NET.dll` and `Windows-x64\steam_api64.dll` to your `Mods` directory

### Diyu's LobbyMod

Note: I have made several changes to [Diyu's LobbyMod](https://github.com/diyu-git/SiroccoLobbyUI) that are waiting for approval, but due to popular demand I have also included my modded version of his lobby mod to this modpack.

My changes to the lobby mod includes:

- You can view unmodded P2P clients in the lobby
- You can disable bots from the lobby
- You can ban ships from the lobby
- Lobby menu now automatically closes when the game finishes loading
- You can no longer click ready before you have fully connected to the host
- Made long player names more visible in the lobby

## The Mods

Here's a list of all the files this modpack adds to Sirocco's `Mods` directory and what each file does:

- `SiroccoMod.ServerStartGuard.dll`: Disables the F1 "server start" hotkey (this used to freeze your game if accidentally pressed it mid-game)
- `SiroccoMod.Scoreboard.dll`: If you are the host then this fixes the in-game scoreboard for all players (this includes post-game analysis/statistics)
- `SiroccoMod.Chat.dll`: If you are the host then this fixes the in-game chat for all players (the chat is global and visible to both teams)
- `SiroccoMod.LighthouseVision.dll`: If you are the host then this makes the lighthouses work for both teams (previously only the host team had working lighthouses)
- `SiroccoMod.KillFeedFix.dll`: If you are the host then this fixes the in-game kill feed so that it shows up to all players
- `SiroccoMod.DisconnectReturn.dll`: If you are the host then this makes players that disconnect from the game automatically return to their base
- `SiroccoMod.NoMoreAFKAutoDisconnect.dll`: Disables the "Are you still there" message from popping up, you will no longer get kicked for being AFK
- `SiroccoMod.Skins.dll.dll`: If you are the host then this applies random Sampan skins visible for all players. This is mostly just for fun, but it also makes it slightly easier to distinguish between players in the early game
- `SiroccoMod.BalanceTweaks.dll`: If you are the host then this applies a bunch of mostly small balance tweaks to the game based off of a discussion in the #balance channel in the Sirocco Discord server

## Other files

- `UserLibs/SiroccoMod.dll`: This is a shared library file with lots of helper functionality for the mods to use. Without this file each .dll mod file would have to be larger in size to compensate.

## Work In Progress

Here's a list of some mods I have been working on that is currently not in the official modpack:

- `SiroccoMod.Reconnect.dll`: Currently broken, but the intention is to allow for players to reconnect to your games if you're the host
- `SiroccoMod.WeaponShuffle.dll`: This currently only semi-works, but it randomizes the order of weapons around for all players
