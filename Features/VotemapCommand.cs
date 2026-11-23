using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using cs2_rockthevote.Core;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace cs2_rockthevote
{
    public partial class Plugin
    {
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnVotemap(CCSPlayerController? player, CommandInfo command)
        {
            if (player == null || command == null)
                return;

            bool hasPerm = PermissionUtility.HasAny(player, Config.Votemap.Permissions);

            if (!hasPerm)
            {
                command.ReplyToCommand(_localizer.LocalizeWithPrefix("general.incorrect.permission"));
                return;
            }
            
            string map = command.GetArg(1).Trim().ToLower();
            _votemapManager.CommandHandler(player, map);
        }
    }

    public class VotemapCommand : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<VotemapCommand> _logger;
        private StringLocalizer _localizer;
        private VotemapConfig _config = new();
        private EndOfMapVote _endOfMapVote;
        private GameRules _gamerules;
        private ChangeMapManager _changeMapManager;
        private PluginState _pluginState;
        private MapCooldown _mapCooldown;
        private MapLister _mapLister;
        private readonly MapMenu _menu;
        private Plugin? _plugin;
        private Dictionary<string, AsyncVoteManager> VotedMaps = new Dictionary<string, AsyncVoteManager>();

        public VotemapCommand(MapLister mapLister, GameRules gamerules, IStringLocalizer stringLocalizer, ChangeMapManager changeMapManager, PluginState pluginState, MapCooldown mapCooldown, EndOfMapVote endOfMapVote, MapMenu menu, ILogger<VotemapCommand> logger)
        {
            _menu = menu;
            _mapLister = mapLister;
            _gamerules = gamerules;
            _localizer = new StringLocalizer(stringLocalizer, "votemap.prefix");
            _changeMapManager = changeMapManager;
            _pluginState = pluginState;
            _mapCooldown = mapCooldown;
            _endOfMapVote = endOfMapVote;
            _logger = logger;
        }

        public void OnMapStart(string map)
        {
            VotedMaps.Clear();
        }

        public void OnConfigParsed(Config config)
        {
            _config = config.Votemap;
        }

        public void PlayerDisconnected(CCSPlayerController player)
        {
            if (player == null)
                return;

            int slot = player.Slot;
            foreach (var map in VotedMaps)
                map.Value.RemoveVote(slot);

            _menu.Refresh("votemap");
        }

        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void CommandHandler(CCSPlayerController? player, string map)
        {
            if (player is null)
                return;

            if (_pluginState.DisableCommands || !_config.Enabled)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.disabled"));
                return;
            }

            if (_gamerules.WarmupRunning)
            {
                if (!_config.EnabledInWarmup)
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.warmup"));
                    return;
                }
            }
            else if (_config.MinRounds > 0 && _config.MinRounds > _gamerules.TotalRoundsPlayed)
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.minimum-rounds", _config.MinRounds));
                return;
            }

            if (ServerManager.ValidPlayerCount() < _config!.MinPlayers)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.minimum-players", _config!.MinPlayers));
                return;
            }

            map = map.ToLower().Trim();

            if (string.IsNullOrEmpty(map))
            {
                _menu.Open(player, MenuSpec(), chat: MapMenu.WantsChat(_config.MenuType), toggle: true);
                return;
            }

            var exact = _mapLister.GetExactMapName(map);
            if (exact is null)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.invalid-map"));
                return;
            }
            if (_mapCooldown.IsMapInCooldown(exact))
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.map-played-recently"));
                return;
            }

            AddVote(player, exact);
        }

        /// <summary>
        /// Why <paramref name="player"/> cannot vote for a map right now, as the menu's plain text, or null when they
        /// can. The same checks as <see cref="CommandHandler"/>, run again on every draw and every click.
        /// </summary>
        private string? Block(CCSPlayerController player)
        {
            if (!PermissionUtility.HasAny(player, _config.Permissions))
                return _localizer.Localize("menu.closed.permission");

            if (_pluginState.DisableCommands || !_config.Enabled)
                return _localizer.Localize("menu.closed.votemap");

            if (_gamerules.WarmupRunning)
            {
                if (!_config.EnabledInWarmup)
                    return _localizer.Localize("menu.closed.warmup");
            }
            else if (_config.MinRounds > 0 && _config.MinRounds > _gamerules.TotalRoundsPlayed)
            {
                return _localizer.Localize("menu.closed.min-rounds", _config.MinRounds);
            }

            if (ServerManager.ValidPlayerCount() < _config.MinPlayers)
                return _localizer.Localize("menu.closed.min-players", _config.MinPlayers);

            return null;
        }

        private MapMenuSpec MenuSpec() => new()
        {
            Kind = "votemap",
            Title = _ => _localizer.Localize("menu.votemap.title"),
            Rows = Rows,
            Pick = (player, row) =>
            {
                var block = Block(player);
                if (block != null)
                    return MapMenuResult.Stay(block);

                AddVote(player, row.Key);
                return MapMenuResult.Done;
            },
            Hint = player => Block(player) ?? _localizer.Localize("menu.hint.votemap", _config.VotePercentage),
            Now = _ => _menu.NowPlaying(),
            MarkedFilter = count => _localizer.Localize("menu.filter.voted", count),
        };

        /// <summary>Every map, with its votes so far against the votes it needs, and the player's own vote marked.</summary>
        private List<MapMenuRow> Rows(CCSPlayerController player)
        {
            var block = Block(player);

            return _menu.MapRows((map, row) =>
            {
                if (!VotedMaps.TryGetValue(map.Name, out var votes) || votes.VoteCount == 0)
                    return block != null && row.Enabled ? row with { Enabled = false, Reason = block } : row;

                if (row.Tone == MapRowTone.None)
                    row = row with { Tone = MapRowTone.Marked, Tag = _localizer.Localize("menu.tag.votes", votes.VoteCount, votes.RequiredVotes) };

                if (votes.Voters.Contains(player.Slot))
                    row = row with { Mine = true, Enabled = false, Reason = _localizer.Localize("menu.reason.voted") };
                else if (block != null && row.Enabled)
                    row = row with { Enabled = false, Reason = block };

                return row;
            });
        }

        public void AddVote(CCSPlayerController player, string map)
        {
            if (map == Server.MapName)
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.current-map"));
                return;
            }

            if (_mapCooldown.IsMapInCooldown(map))
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.map-played-recently"));
                return;
            }

            if (!_mapLister.Maps!.Any(x => x.Name.Equals(map, StringComparison.OrdinalIgnoreCase)))
            {
                player!.PrintToChat(_localizer.LocalizeWithPrefix("general.invalid-map"));
                return;
            }

            if (!VotedMaps.ContainsKey(map))
                VotedMaps.Add(map, new AsyncVoteManager(_config.VotePercentage));

            var voteManager = VotedMaps[map];
            VoteResult result = voteManager.AddVote(player.Slot);
            _menu.Refresh("votemap");
            switch (result.Result)
            {
                case VoteResultEnum.Added:
                    Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("votemap.player-voted", player.PlayerName, map)} {_localizer.Localize("general.votes-needed", result.VoteCount, result.RequiredVotes)}");
                    break;
                case VoteResultEnum.AlreadyAddedBefore:
                    player.PrintToChat($"{_localizer.LocalizeWithPrefix("votemap.already-voted", map)} {_localizer.Localize("general.votes-needed", result.VoteCount, result.RequiredVotes)}");
                    break;
                case VoteResultEnum.VotesAlreadyReached:
                    player.PrintToChat(_localizer.LocalizeWithPrefix("votemap.disabled"));
                    break;
                case VoteResultEnum.VotesReached:
                    Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("votemap.player-voted", player.PlayerName, map)} {_localizer.Localize("general.votes-needed", result.VoteCount, result.RequiredVotes)}");
                    _changeMapManager.ScheduleMapChange(map, prefix: "votemap.prefix");
                    if (_config.ChangeMapImmediately)
                        _changeMapManager.ChangeNextMap();
                    else
                        Server.PrintToChatAll(_localizer.LocalizeWithPrefix("general.changing-map-next-round", map));
                    break;
            }
        }
    }
}
