using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.Logging;

namespace cs2_rockthevote
{
    public class MapChooserCommand : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<MapChooserCommand> _logger;
        private readonly StringLocalizer _localizer;
        private readonly MapLister _mapLister;
        private readonly MapMenu _menu;
        private Plugin? _plugin;

        private string[] _permissions = ["@css/root", "@css/admin"];
        private MapChooserConfig _config = new();
        private readonly HashSet<string> _registeredAliases = new(StringComparer.OrdinalIgnoreCase);

        public MapChooserCommand(StringLocalizer localizer, MapLister mapLister, MapMenu menu, ILogger<MapChooserCommand> logger)
        {
            _localizer = localizer;
            _mapLister = mapLister;
            _menu = menu;
            _logger = logger;
        }

        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void OnConfigParsed(Config config)
        {
            _config = config.MapChooser;
            _permissions = _config.Permissions;

            if (string.IsNullOrWhiteSpace(_config.Command))
                return;

            Server.NextFrame(() =>
            {
                foreach (var alias in _config.Command.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // Skip aliases already registered
                    if (_plugin == null || _registeredAliases.Contains(alias))
                        continue;

                    _plugin.AddCommand(alias, "Opens the Map Chooser Menu", ExecuteCommand);
                    _registeredAliases.Add(alias);
                }
            });
        }

        private void ExecuteCommand(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid)
                return;

            if (!Allowed(player))
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.incorrect.permission"));
                return;
            }

            var maps = _mapLister.Maps;
            if (maps is null || maps.Length == 0)
                return;

            _menu.Open(player, MenuSpec(), chat: MapMenu.WantsChat(_config.MenuType), toggle: true);
        }

        private bool Allowed(CCSPlayerController player) =>
            _permissions.Length == 0 || PermissionUtility.HasAny(player, _permissions);

        /// <summary>
        /// Every map, the current one and the cooldown included: an admin may change to any of them. Two clicks,
        /// since one changes the map for the whole server right away.
        /// </summary>
        private MapMenuSpec MenuSpec() => new()
        {
            Kind = "mapchooser",
            Title = _ => _localizer.Localize("menu.mapchooser.title"),
            Rows = _ => _menu.MapRows(unavailableClickable: true),
            Confirm = row => _localizer.Localize("menu.status.confirm", row.Name),
            Pick = (player, row) =>
            {
                // Checked again: the click comes from a client
                if (!Allowed(player))
                    return MapMenuResult.Stay(_localizer.Localize("menu.closed.permission"));

                var map = _mapLister.Maps.FirstOrDefault(m => string.Equals(m.Name, row.Key, StringComparison.OrdinalIgnoreCase));
                if (map is null)
                    return MapMenuResult.Stay(_localizer.Localize("menu.reason.gone"));

                ChangeTo(player, map);
                return MapMenuResult.Done;
            },
            Hint = _ => _localizer.Localize("menu.hint.mapchooser"),
            Now = _ => _menu.NowPlaying(),
        };

        /// <summary>
        /// Straight away, as the old menu did. The name goes to changelevel without its display part
        /// ("surf_nyx (Tier 1, Linear)" is not a map the engine knows); a workshop map goes by its id.
        /// </summary>
        private void ChangeTo(CCSPlayerController admin, Map map)
        {
            var name = MapNameHelper.GetBaseName(map.Name);
            _logger.LogInformation("[RTV.MapChooser] {Admin} changed the map. map={Map} id={Id}", admin.PlayerName, map.Name, map.Id);

            if (!string.IsNullOrEmpty(map.Id) && ulong.TryParse(map.Id, out var mapId))
                Server.ExecuteCommand($"host_workshop_map {mapId}");
            else if (Server.IsMapValid(name))
                Server.ExecuteCommand($"changelevel {name}");
            else
                Server.ExecuteCommand($"ds_workshop_changelevel {name}");
        }
    }
}