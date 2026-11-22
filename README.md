<div align="center">

# CS2 RockTheVote (RTV)
![GitHub Downloads](https://img.shields.io/github/downloads/M-archand/cs2-rockthevote/total?style=for-the-badge)

General purpose map voting plugin.

</div>

# Features
- Reads from a custom maplist, or from a website URL (`General.MapListUrl`, see [Map list from a URL](#map-list-from-a-url))
- RTV Command (Using !rtv in chat, or with Panorama system that is built into CS2 [F1 = Yes, F2 = No])
- End of Map Vote. Supports map cooldown, map extension option.
- Map cooldown persistence. Recently played maps stay on cooldown across server restarts (stored per server in `csgo/rockthevote/mapcooldown-<port>.txt`, see [Per-server files](#per-server-files)).
- !revote command. During an active End of Map Vote, lets a player reopen the vote as a menu and change their vote. Re-typing !revote while the menu is open does nothing (vote already active). Toggled via EndOfMapVote.EnableRevote.
- Nominate command (nominate a map to appear in the map vote). !nominate opens the map menu: search, filters and pages, so a few hundred maps stay usable. It marks the maps already nominated (and your own pick), the maps on cooldown (with when they are back) and the current map. Partial name matching, and conflicting map name resolution (surf_beginner, surf_beginner2): !nominate surf_beg opens the menu searched for it. Limit 1 per player.

  ![nominate](https://github.com/user-attachments/assets/6ac056bc-9842-4422-ac0d-c7cd814b3ba6)
  
- Supports workshop maps, and custom map names. E.g. "surf_beginner (T1, Staged)"
- Nextmap command. Prints the next map to chat.
- Translated (Google Translate, ymmv)
- Extend command. !extend 10 extends map by 10 minutes (flag restricted)
- Vote Extend command. !ve/!votextend starts a vote to extend the current map (flag restricted)
- Map Chooser command. !mapmenu opens the map menu with your whole map list; click a map, then click it again, and the map changes immediately (flag restricted)
- Optional sound alert when map vote or !rtv starts (configurable sound)
- Optional hud alert
  
  ![hudalert](https://github.com/user-attachments/assets/23c35f20-b4f0-4122-b241-287b44efdb27)
  
- Optional chat/hud vote countdown

 ![hudcountdown](https://github.com/user-attachments/assets/e1034f3c-340a-4d88-8d8a-96526f333fad)
 ![chatcountdown](https://github.com/user-attachments/assets/803826a1-665b-4ab7-9e38-fbb0e8d702be)

- Panorama Vote (F1 = Yes, F2 = No) for !rtv & !voteextend (optional)

![panoramavote](https://github.com/user-attachments/assets/31ebe223-225f-4cef-812e-3bf6c56e590d)
![voteextend](https://github.com/user-attachments/assets/5cfd9a5f-36a5-4a11-ae26-3e74d5387251)
- The map vote as a small Panorama card (vote_hud): vote with !1-!9 in chat, the card never takes your mouse or keys.
- Clickable Panorama menus (rtv_menu) for !nominate, !maps, !votemap, !mapmenu and !revote. A layout cannot take a keystroke, so the search field asks for the text in chat. Set a MenuType to "ChatMenu" for CounterStrikeSharp's !1-!9 chat menu instead; it is also the fallback when the HUD cannot start.

![chatmenu](https://github.com/user-attachments/assets/8d7e9ee8-b26e-47b1-89d8-ced96b13a392)

- Maplist Validator. Send to error log or Discord when a map is no longer available on the workshop. Automatically removes invalid workshop maps from the in-memory map list so they can't be nominated, voted for, or switched to (maplist.txt is left untouched). Optionally set a Steam Web API key to batch validation (100 IDs per request) instead of 1 request/second HTML checks.

  ![MapDiscordWebhook](https://github.com/user-attachments/assets/eaf8d706-abd1-4258-a7a3-b9cb44500802)
  ![WorkshopMapLog](https://github.com/user-attachments/assets/2f65dd9d-1ee9-4217-a753-81358973df2e)
- !maps command. Opens the map list menu: what is on now, on cooldown and nominated; a click nominates when you can. Without the HUD it prints the list to the console.
  
![mapscommand](https://github.com/user-attachments/assets/d4ab1377-0b29-45b6-bdaa-06b6a7664751)

- !cooldown command. List the maps currently on cooldown in the console (most recently played at the top). Configurable aliases, 10 second per-player cooldown.
- !reloadmaps command. Rebuild the map list mid game
- !reloadrtv command. Reload the rtv config mid game


## Requirements
[CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (Tested on v369)

[PanoramaManager](https://www.nuget.org/packages/PanoramaManager) - bundled in the release zip. The layouts (`hud/panorama`: vote_hud and rtv_menu) ship in the workshop addon. CS2MenuManager is no longer needed.

# Installation
- Download the latest release from https://github.com/M-archand/cs2-rockthevote/releases
- Extract the .zip file into `addons/counterstrikesharp/plugins`
- Update the maplist.example.txt to inlcude your desired maps and then rename it to maplist.txt

## [ Configuration ]
- A config file will be created in `addons/counterstrikesharp/configs/plugins/RockTheVote` the first time you load the plugin.
- Changes in the config file will require you to reload the plugin, restart the server, or using !reloadrtv (changing the map won't work).
- Maps that will be used in RTV/nominate/votemap/end of map vote are read from `maplist.txt` next to the config file (`addons/counterstrikesharp/configs/plugins/RockTheVote/maplist.txt`), or from `General.MapListUrl` when that is set. Reload mid-game with !reloadmaps (`css_reloadmaps`).

```json
{
  "ConfigVersion": 25,
  "Rtv": {
    "Enabled": true,
    "EnabledInWarmup": false,
    "EnablePanorama": true, # true = use built in Panorama voting system (F1 = Yes, F2 = No). False = use !rtv in chat
    "MinPlayers": 0,
    "MinRounds": 0,
    "ChangeAtRoundEnd": false, # false = use MapChangeDelay value below. true = wait until round end to change the map
    "MapChangeDelay": 5, # The delay in seconds after the rtv map vote has passed before the map is changed. 0 = immediate
    "SoundEnabled": false, # true = play a sound when the end of map vote starts.
    "SoundVolume": 1.0, # Volume of the alert sound. For any value other than 1.0 you must use a soundevent_hash in SoundPath
    "SoundPath": "sounds/vo/announcer/cs2_classic/felix_broken_fang_pick_1_map_tk01.vsnd_c",
    "MapsToShow": 6, # How many maps to show in the resulting map vote if the rtv passes
    "AlwaysActive": true, # true = rtv vote is always running once triggered by an initional rtv. false = timed duration (RtvVoteDuration)
	  "AlwaysActiveReminder": true, # true = print a reminder to chat at a given interval that an rtv vote is active, and how many more votes are required to pass
	  "ReminderInterval": 120, # how often the reminder is printed to chat in seconds
    "RtvVoteDuration": 60, # How long the rtv vote lasts
    "MapVoteDuration": 60, # How long the resulting map vote will last
    "CooldownDuration": 180, # How many seconds must pass before another !rtv can be initiated
    "MapStartDelay": 180, # How many seconds must pass after the map starts before an !rtv can be called
    "VotePercentage": 51, # Percentage of votes required to pass the vote
    "EnableCountdown": true, # Whether the chat/hud countdown is enabled
    "CountdownType": "chat", # chat = prints to chat on an interval how much time is left in the vote. hud = persistent alert on the hud counting down as each second passes
    "ChatCountdownInterval": 15 # If CountdownType = chat, how often we print to chat how much time is remaining to vote
  },
  "EndOfMapVote": {
    "Enabled": true,
    "EnableRevote": false, # true = players can type !revote to reopen the vote as a menu and change their choice
    "MapsToShow": 6, # How many maps to show in the vote. If IncludeExtendCurrentMap = true, the extension option takes up 1 slot
    "MenuType": "Interactive", # The vote as a menu (EnableHudVote off, and !revote). Interactive = the clickable HUD menu. ChatMenu = !1-!9 in chat. Old CS2MenuManager names (WasdMenu, ...) count as Interactive
    "ChangeMapImmediately": false, # false = change when the map ends. true = change as soon as the VoteDuration ends
    "VoteDuration": 150, # How long the map vote will last (this must be smaller than TriggerSecondsBeforeEnd)
    "SoundEnabled": false, # true = play a sound when the end of map vote starts.
    "SoundVolume": 0.5, # Volume of the alert sound. For any value other than 1.0 you must use a soundevent_hash in SoundPath
    "SoundPath": "1974266470", # Filepath or soundevent_hash of the sound you want to be played
    "TriggerSecondsBeforeEnd": 180, # When the End of Map Vote will be triggered (this must be higher than VoteDuration)
    "TriggerRoundsBeforeEnd": 0, # What round the vote is trigger on (use 0 for game modes like surf/bhop/etc or it'll never appear)
    "DelayToChangeInTheEnd": 0, # How long the mvp screen shows at the end if ChangeMapImmediately = false
    "IncludeExtendCurrentMap": true, # Include an option to extend the current map
    "EnableCountdown": false, # Whether the chat/hud countdown is enabled
    "CountdownType": "chat", # chat = prints to chat on an interval how much time is left in the vote. hud = persistent alert on the hud counting down as each second passes
    "ChatCountdownInterval": 30, # If CountdownType = chat, how often we print to chat how much time is remaining to vote
    "ChatMapChoiceReminder": true, # Only applies when MenuType = ChatMenu. Periodically reprints the map choices and current vote tally to chat
    "ChatMapChoiceInterval": 30, # How often (seconds) the ChatMenu map choices are reprinted
    "EnableHint": true, # Shows a message in the center of the screen notifying that the map vote has started
    "HintType": "csay" # csay = hud message in lower middle. GameHint = game instructor text in center middle
  },
  "Nominate": {
    "Enabled": true,
    "EnabledInWarmup": true,
    "MenuType": "Interactive", # Interactive = the clickable HUD map menu. ChatMenu = !1-!9 in chat
    "NominateLimit": 1, # How many maps a single player can nominate per map vote
    "Permission": "" # empty = anyone can use. "@css/vip" or "@css/admin,@css/mod" = any listed permission can use it
  },
  "Votemap": {
    "Enabled": false,
    "MenuType": "Interactive", # Interactive = the clickable HUD map menu. ChatMenu = !1-!9 in chat
    "VotePercentage": 50, # Percentage of votes required to pass the vote
    "ChangeMapImmediately": true,
    "EnabledInWarmup": false,
    "MinPlayers": 0,
    "MinRounds": 0,
    "Permission": "@css/vip" # empty = anyone can use. "@css/vip" or "@css/admin,@css/mod" = any listed permission can use it
  },
  "VoteExtend": {
    "Enabled": false,
    "EnablePanorama": true, # true = use built in Panorama voting system (F1 = Yes, F2 = No). False = use !ve in chat
    "VoteDuration": 60, # How long the vote will last
    "VotePercentage": 50, # Percentage of votes required to pass the vote
    "CooldownDuration": 180, # How many seconds must pass before another !ve can be called
    "EnableCountdown": true,
    "CountdownType": "chat", # chat = prints to chat on an interval how much time is left in the vote. hud = persistent alert on the hud counting down as each second passes
    "ChatCountdownInterval": 15, # If CountdownType = chat, how often we print to chat how much time is remaining to vote
    "Permission": "@css/vip" # empty = anyone can use. "@css/vip" or "@css/admin,@css/mod" = any listed permission can use it
  },
  "MapChooser": {
    "Command": "mapmenu,mm", The command used to open the menu, multiple can be set
    "MenuType": "Interactive", # Interactive = the clickable HUD map menu. ChatMenu = !1-!9 in chat
    "Permission": "@css/root,@css/changemap" # empty = anyone can use. any listed permission can use it
  },
  "General": {
    "AdminPermission": "@css/root,@css/admin", # Any listed permission can use !reloadrtv or !reloadmaps
    "DebugLogging": false, # true = verbose debug logging to the CSS log (vote flow, map change, hint and validation details)
    "MaxMapExtensions": 2,
    "DisableMapExtensions": "", # Comma separated list of maps that can't be extended, e.g. "surf_utopia_njv, surf_mesa_revo". Listed maps won't show the extend option in the End of Map Vote
    "RoundTimeExtension": 15, # How long the extension will be in minutes for !VoteExtend or End of Map Vote extension
    "MapsInCoolDown": 3, # How many recent maps that won't appear again in the End of Map Vote/can't be nominated (0 = no cooldown, but current map is always in cooldown)
    "CooldownCommands": ["css_cooldown"], # Commands that print the maps currently on cooldown to the player's console, multiple can be set
    "HideHudAfterVote": true, # Only applicable in MenuType = HudMenu. true = closes the hud after the player has voted
    "RandomStartMap": false, # true = a random map will be used when the server restarts. false = will use whatever you set in your startup command
    "IncludeSpectator": true, # true = spectators can vote (only applicable to !rtv). false = spectators can't vote
  	"IncludeAFK": false, # true = AFK players are included in the vote count (only applicable to !rtv). false = AFK players aren't included in the vote count
  	"AFKCheckInterval": 60, # how often an AFK check occurs in seconds (compares players coordinates between current and last check, also run again when the vote is initiated)
    "MapListUrl": "", # blank = read maplist.txt. Set to a URL that returns the map list as a JSON array, e.g. "https://api.next-il.co.il/cs2/maplists/retakes". See "Map list from a URL" below
    "EnableMapValidation": true, # true = the plugin will check if there are any workshop maps in your maplist.txt that are no longer on the workshop
    "SteamApiKey": "", # blank = use 1 request/second HTML checks. Set to a Steam Web API key to batch validation requests (100 IDs per call) Get one here: https://steamcommunity.com/dev/apikey
    "DiscordWebhook": "" # blank = no alert. Discord Webhook will alert you to any workshop maps in your maplist.txt that are no longer on the workshop. Enter the enite "https://discord.com/api/webhooks/xxx" url
  }
}
```
  
# Adding workshop maps
```
surf_beginner:3070321829
surf_nyx (T1, Linear):3129698096
de_dust2
```

# Map list from a URL
Set `General.MapListUrl` and the map list comes from a website instead of maplist.txt, so changing maps is a website edit, not a file change on every server. Leave it blank and nothing changes: maplist.txt is read exactly as before.

```json
"General": {
  "MapListUrl": "https://api.next-il.co.il/cs2/maplists/retakes"
}
```

**The URL.** `GET <MapListUrl>`, no auth. Next-il uses `https://api.next-il.co.il/cs2/maplists/<slug>`, where the slug is the MapChooser mode folder name in lowercase: `surftimer`, `bhoptimer`, `kz`, `retakes`, `retakesxmas`, `surfcombat`, `arena1v1`, `awplego`, `deathmatch`, `deathrun`, `duels`, `football`, `hidenseek`, `jailbreak`, `prophunt`.
- `200` with a JSON array of strings, each one exactly one maplist.txt line, in the order to use: `["de_dust2", "surf_kitsune:3076153623"]`. Lines are parsed by the same code as maplist.txt, so blanks and `//` comments are skipped and `name:workshopid` works the same.
- `404` means there is no such list.
- Anything else is a failure: another status, no answer within 5 seconds, a body that is not a JSON array, an item that is not a string, an item with a line break or no map name, or a list with no maps. One bad item rejects the whole response.

**When it is fetched.** On plugin load, on every map start, and on `css_reloadmaps` (!reloadmaps). The request runs off the main thread and the result is applied on the next frame, so a website edit lands at the next map without a restart. A map vote that is already open keeps its options: a list that arrives during a vote, or while a map change is scheduled, waits until that is over and then applies to the next vote.

**When the URL fails** the map list is never emptied. In order, the plugin uses:
1. the fresh list from the URL
2. the last good list: the one in memory, or after a restart the cache file
3. maplist.txt

While a fetch is out (at most 5 seconds) the plugin already runs on 2 or 3, so it never waits on the network.

**The cache file** is `csgo/rockthevote/maplist-cache-<port>.json` in the server's own game folder (e.g. `/home/container/game/csgo/rockthevote/maplist-cache-27015.json`). It holds the last good list and the URL it came from; a cache saved for another URL is ignored. It follows the [per-server files](#per-server-files) rules: if no safe path exists, the last good list is kept in memory only and one WARN is logged.

**Logs.** One INFO line per fetch with where the maps came from and how many, e.g. `[RTV.MapList] Using 42 maps from https://...`. A failed fetch adds one WARN with the reason, e.g. `[RTV.MapList] Fetching https://... failed: no answer within 5s.`, and the INFO line names the fallback in use. There are no retries; the next map start is the retry.

# Per-server files
The files the plugin writes while running go in the server's own game folder, under `csgo/rockthevote/`, with the server port in the name:
- `mapcooldown-<port>.txt` - the recently played maps (map cooldown), one per line
- `maplist-cache-<port>.json` - the last good list from `MapListUrl`

They are deliberately not next to the config. The config and plugin folders are often shared between servers (bind mounts, one folder for every server of a mode on a node), and several servers rewriting one file there race at restart waves. `csgo/rockthevote/` is not a mount, and the port in the name is a second guard. On Linux the plugin also checks /proc/self/mountinfo: if that folder turns out to be a separate mount, the file is skipped, the data is kept in memory only, and one WARN is logged. The path is worked out once the server has started its first map, since the port may not be set before that.

**Map cooldown is per server.** Up to 2.3.0 the cooldown was `mapcooldown.txt` beside the config, so servers sharing that folder also shared one cooldown. Now each server has its own and only counts the maps it played itself. On the first start after updating, a server with no `mapcooldown-<port>.txt` yet reads the old `mapcooldown.txt` once to seed it, so the cooldown carries over. The old file is never written again and can be deleted once every server has updated.

# Roadmap
- [ ] Add vote percentage required for winning map (e.g. must receive 25% of the vote)
- [ ] Add vote runnoff (e.g. 2nd stage of voting between 2 maps if minimum vote percentage not achieved for a map)

# Translations
| Language             |
| -------------------- |
| English              |
| French               |
| Spanish              |
| Ukrainian            |
| Turkish              |
| Latvian              |
| Hungarian            |
| Polish               |
| Russian              |
| Portuguese (BR)      |
| Chinese (Simplified) |
