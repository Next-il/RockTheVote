using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace cs2_rockthevote
{
    public partial class Plugin
    {
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnMaplistCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player == null) return;
            _maplistManager.CommandHandler(player);
        }
    }

    public class MaplistCommand : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<RockTheVoteCommand> _logger;
        private readonly StringLocalizer _localizer;
        private readonly MapLister _mapLister;
        private readonly MapMenu _menu;
        private readonly NominationCommand _nominations;

        public MaplistCommand(MapLister mapLister, StringLocalizer localizer, MapMenu menu, NominationCommand nominations, ILogger<RockTheVoteCommand> logger)
        {
            _mapLister = mapLister;
            _localizer = localizer;
            _menu = menu;
            _nominations = nominations;
            _logger = logger;
        }

        /// <summary>
        /// The map list on the HUD: every map with what is on now, on cooldown and nominated, and a click nominates
        /// when the player may. The console print is only the fallback for a server whose HUD did not start: a few
        /// hundred maps make a chat menu nobody can use.
        /// </summary>
        public void CommandHandler(CCSPlayerController? player)
        {
            if (player != null)
            try
            {
                var maps = _mapLister.Maps;
                if (maps == null || maps.Length == 0)
                {
                    player.PrintToChat($" {ChatColors.LightRed}[MapList]{ChatColors.Default} The map list is empty!");
                    return;
                }

                if (_menu.Available)
                {
                    _nominations.OpenMapList(player);
                    return;
                }

                // Tell them to check console
                player.PrintToChat($" {ChatColors.LightRed}[MapList]{ChatColors.Default} Maps have been printed to the console.");

                player.PrintToConsole("====================================");
                player.PrintToConsole("             Server Map List");
                player.PrintToConsole($"             Total Maps: {maps.Length}");
                player.PrintToConsole("====================================");

                foreach (var map in maps)
                {
                    if (string.IsNullOrWhiteSpace(map.Name)) continue;
                    player.PrintToConsole(map.Name);
                }

                player.PrintToConsole("====================================");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.MaplistCommand] An error occurred while reading the map list: {Message}", ex.Message);
            }
        }
    }
}
