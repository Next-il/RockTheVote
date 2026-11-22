using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace cs2_rockthevote
{
    public partial class Plugin
    {
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnReloadMapsCommand(CCSPlayerController? player, CommandInfo command)
        {
            _reloadMapsCommand.CommandHandler(player, command);
        }
    }

    public class ReloadMapsCommand : IPluginDependency<Plugin, Config>
    {
        private readonly MapLister _mapLister;
        private readonly ILogger<ReloadMapsCommand> _logger;
        private GeneralConfig _generalConfig = new();

        public ReloadMapsCommand(MapLister mapLister, ILogger<ReloadMapsCommand> logger)
        {
            _mapLister = mapLister;
            _logger = logger;
        }

        public void OnConfigParsed(Config config)
        {
            _generalConfig = config.General;
        }

        public void CommandHandler(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null && !PermissionUtility.HasAny(player, _generalConfig.AdminPermissions))
            {
                command.ReplyToCommand($"[RTV] {ChatColors.Red}You do not have the correct permission to execute this command.");
                return;
            }

            // With MapListUrl set the reload is a fetch: the answer comes back a frame or more later,
            // and a failed fetch keeps the list as it is.
            if (_mapLister.UsesUrl)
            {
                command.ReplyToCommand($"[RTV] Fetching the map list from {_mapLister.MapListUrl} ...");
                _mapLister.FetchFromUrl(report: message =>
                {
                    if (player is null)
                        Server.PrintToConsole($"[RTV] {message}");
                    else if (player.IsValid)
                        player.PrintToChat($"[RTV] {message}");
                });
                return;
            }

            try
            {
                _mapLister.LoadMaps();
                if (!_mapLister.MapsLoaded)
                {
                    command.ReplyToCommand($"[RTV] {ChatColors.Red}Map list reload failed. Check server console/logs for the expected maplist path.");
                    return;
                }

                command.ReplyToCommand($"[RTV] {ChatColors.Lime}Map list reloaded successfully.");
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError(ex, "[RTV.ReloadMaps] maplist.txt not found while running css_reloadmaps.");
                command.ReplyToCommand($"[RTV] {ChatColors.Red}maplist.txt not found.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.ReloadMaps] Failed to reload map list via css_reloadmaps.");
                command.ReplyToCommand($"[RTV] {ChatColors.Red}Failed to reload map list. Check server logs for details.");
            }
        }
    }
}
