using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using cs2_rockthevote.Core;

namespace cs2_rockthevote
{
    public partial class Plugin
    {
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnNominateCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player == null)
                return;
            
            bool hasPerm = PermissionUtility.HasAny(player, Config.Nominate.Permissions);

            if (!hasPerm)
            {
                command.ReplyToCommand(_localizer.LocalizeWithPrefix("general.incorrect.permission"));
                return;
            }

            _nominationManager.CommandHandler(player, command.GetArg(1)?.Trim().ToLower() ?? "");
        }
    }

    public class NominationCommand : IPluginDependency<Plugin, Config>
    {
        Dictionary<int, List<string>> Nominations = new();
        private string? _nominationsMap;
        private bool _mapExtended;
        private NominateConfig _nomConfig = new();
        private GameRules _gamerules;
        private StringLocalizer _localizer;
        private PluginState _pluginState;
        private MapCooldown _mapCooldown;
        private MapLister _mapLister;
        private readonly MapMenu _menu;
        private Plugin? _plugin;

        public NominationCommand(MapLister mapLister, GameRules gamerules, StringLocalizer localizer, PluginState pluginState, MapCooldown mapCooldown, MapMenu menu)
        {
            _mapLister = mapLister;
            _gamerules = gamerules;
            _localizer = localizer;
            _pluginState = pluginState;
            _mapCooldown = mapCooldown;
            _menu = menu;
        }

        public void MarkMapExtended() => _mapExtended = true;

        public void OnMapStart(string map)
        {
            bool extended = _mapExtended;
            _mapExtended = false;

            if (extended && string.Equals(_nominationsMap, map, StringComparison.OrdinalIgnoreCase))
                return;

            Nominations.Clear();
            _nominationsMap = map;
        }

        // Counts as the menus show them: one per player per map
        private int NominationCount(string map) =>
            Nominations.Values.Count(list => list.Contains(map, StringComparer.OrdinalIgnoreCase));
        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void OnConfigParsed(Config config)
        {
            _nomConfig = config.Nominate;
        }

        public void CommandHandler(CCSPlayerController? player, string map)
        {
            if (player == null)
                return;

            if (_pluginState.DisableCommands || !_nomConfig.Enabled || _pluginState.EofVoteHappening)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.disabled"));
                return;
            }

            if (_gamerules.WarmupRunning && !_nomConfig.EnabledInWarmup)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.warmup"));
                return;
            }
            
            int slot = player.Slot;
            int existingCount = Nominations.TryGetValue(slot, out var userNoms) ? userNoms.Count : 0;
            if (_nomConfig.NominateLimit > 0 && existingCount >= _nomConfig.NominateLimit)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("nominate.limit", _nomConfig.NominateLimit));
                return;
            }

            var mapName = map.Trim();

            if (string.IsNullOrEmpty(mapName))
            {
                _menu.Open(player, MenuSpec(browse: false), chat: MapMenu.WantsChat(_nomConfig.MenuType), toggle: true);
                return;
            }

            var resolved = ResolveMapNameOrPrompt(player, mapName, _localizer);
            if (resolved == null)
                return;

            Nominate(player, resolved);
        }

        public void Nominate(CCSPlayerController player, string map)
        {
            if (player == null || !player.IsValid)
                return;

            var slot = player.Slot;
            var mapName = map.Trim();
            var baseName = MapNameHelper.GetBaseName(mapName);

            // Ensure per-player list exists
            if (!Nominations.TryGetValue(slot, out var userNoms))
            {
                userNoms = new List<string>();
                Nominations[slot] = userNoms;
            }

            // Respect warmup here too (so menu + chat behave the same)
            if (_gamerules.WarmupRunning && !_nomConfig.EnabledInWarmup)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.warmup"));
                return;
            }

            // Enforce per-player nomination limit (0 = unlimited)
            if (_nomConfig.NominateLimit > 0 && userNoms.Count >= _nomConfig.NominateLimit)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("nominate.limit", _nomConfig.NominateLimit));
                return;
            }

            // Prevent nominating the same map multiple times by this player
            if (userNoms.Contains(mapName, StringComparer.OrdinalIgnoreCase))
            {
                int voteCount = Nominations.Values
                    .SelectMany(v => v)
                    .Count(m => m.Equals(mapName, StringComparison.OrdinalIgnoreCase));

                player.PrintToChat(_localizer.LocalizeWithPrefix("nominate.already-nominated", mapName, voteCount));
                return;
            }

            // Can't nominate the current map
            if (baseName.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase))
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.current-map"));
                return;
            }

            // Can't nominate a map on cooldown
            if (_mapCooldown.IsMapInCooldown(baseName))
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.map-played-recently"));
                return;
            }

            // ✅ All validations passed — record the nomination now
            userNoms.Add(mapName);

            int totalVotes = Nominations.Values
                .SelectMany(v => v)
                .Count(m => m.Equals(mapName, StringComparison.OrdinalIgnoreCase));

            Server.PrintToChatAll(_localizer.LocalizeWithPrefix("nominate.nominated", player.PlayerName, mapName, totalVotes));

            // Everyone browsing the list sees the new count
            _menu.Refresh("nominate");
            _menu.Refresh("maps");
        }

        /// <summary>!maps: the same list as !nominate, under its own title. Read-only while the player cannot nominate.</summary>
        public void OpenMapList(CCSPlayerController player)
        {
            _menu.Open(player, MenuSpec(browse: true), toggle: true);
        }

        /// <summary>
        /// Why <paramref name="player"/> cannot nominate right now, as the menu's plain text, or null when they can.
        /// The same checks as <see cref="CommandHandler"/>, run again on every draw and every click.
        /// </summary>
        private string? Block(CCSPlayerController player)
        {
            if (!PermissionUtility.HasAny(player, _nomConfig.Permissions))
                return _localizer.Localize("menu.closed.permission");

            if (_pluginState.DisableCommands || !_nomConfig.Enabled || _pluginState.EofVoteHappening)
                return _localizer.Localize("menu.closed.nominate");

            if (_gamerules.WarmupRunning && !_nomConfig.EnabledInWarmup)
                return _localizer.Localize("menu.closed.warmup");

            int count = Nominations.TryGetValue(player.Slot, out var mine) ? mine.Count : 0;
            if (_nomConfig.NominateLimit > 0 && count >= _nomConfig.NominateLimit)
                return _localizer.Localize("menu.closed.limit", _nomConfig.NominateLimit);

            return null;
        }

        private MapMenuSpec MenuSpec(bool browse) => new()
        {
            Kind = browse ? "maps" : "nominate",
            Title = _ => _localizer.Localize(browse ? "menu.maps.title" : "menu.nominate.title"),
            Rows = Rows,
            Pick = (player, row) =>
            {
                var block = Block(player);
                if (block != null)
                    return MapMenuResult.Stay(block);

                Nominate(player, row.Key);
                return MapMenuResult.Done;
            },
            Hint = player => Block(player) ?? _localizer.Localize("menu.hint.nominate"),
            Now = _ => _menu.NowPlaying(),
            MarkedFilter = count => _localizer.Localize("menu.filter.nominated", count),
        };

        /// <summary>Every map, with how many players nominated it and the player's own pick marked.</summary>
        private List<MapMenuRow> Rows(CCSPlayerController player)
        {
            var block = Block(player);
            Nominations.TryGetValue(player.Slot, out var mine);

            return _menu.MapRows((map, row) =>
            {
                int count = NominationCount(map.Name);
                if (count > 0 && row.Tone == MapRowTone.None)
                {
                    row = row with
                    {
                        Tone = MapRowTone.Marked,
                        Tag = count == 1
                            ? _localizer.Localize("menu.tag.nominated")
                            : _localizer.Localize("menu.tag.nominated-n", count),
                    };
                }

                if (mine != null && mine.Contains(map.Name, StringComparer.OrdinalIgnoreCase))
                {
                    row = row with
                    {
                        Mine = true,
                        Enabled = false,
                        Tag = _localizer.Localize("menu.tag.yours"),
                        Reason = _localizer.Localize("menu.reason.nominated"),
                    };
                }
                else if (block != null && row.Enabled)
                {
                    row = row with { Enabled = false, Reason = block };
                }

                return row;
            });
        }

        // Attempt to resolve the user's text into exactly one map. If 0 matches -> send "invalid" and return null.
        // If > 1 matches -> open the menu searched for it and return null. Otherwise -> return the single map name.
        private string? ResolveMapNameOrPrompt(CCSPlayerController player, string input, StringLocalizer localizer)
        {
            // Exact match
            var exact = _mapLister.GetExactMapName(input);
            if (exact is not null)
                return exact;

            // Find all "contains" matches
            var matches = _mapLister.GetMatchingMapNames(input);

            // No matches found
            if (matches.Count == 0)
            {
                player.PrintToChat(localizer.LocalizeWithPrefix("general.invalid-map"));
                return null;
            }
            // Found more than 1 match
            if (matches.Count > 1)
            {
                _menu.Open(player, MenuSpec(browse: false), query: input, chat: MapMenu.WantsChat(_nomConfig.MenuType));
                return null;
            }

            // Exactly one
            return matches[0];
        }

        public List<string> NominationWinners()
        {
            if (Nominations.Count == 0)
                return new List<string>();

            var counts = new Dictionary<string, int>();
            foreach (var map in Nominations.Values.SelectMany(v => v))
                counts[map] = counts.TryGetValue(map, out var c) ? c + 1 : 1;

            return [.. counts
                .OrderByDescending(x => x.Value)
                .Select(x => x.Key)];
        }

        public void PlayerDisconnected(CCSPlayerController player)
        {
            if (player == null)
                return;

            if (Nominations.Remove(player.Slot))
            {
                _menu.Refresh("nominate");
                _menu.Refresh("maps");
            }
        }
    }
}
