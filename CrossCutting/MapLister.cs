using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace cs2_rockthevote
{
    public class MapLister : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<MapLister> _logger;
        public Map[] Maps { get; private set; } = Array.Empty<Map>();
        public bool MapsLoaded { get; private set; } = false;
        public event EventHandler<Map[]>? EventMapsLoaded;
        private Plugin? _plugin;
        private ILogger _debugLogger = NullLogger<MapLister>.Instance;

        public MapLister(ILogger<MapLister> logger)
        {
            _logger = logger;
        }

        public void Clear()
        {
            MapsLoaded = false;
            Maps = Array.Empty<Map>();
        }

        public void LoadMaps()
        {
            Clear();

            if (_plugin is null)
            {
                _debugLogger.LogWarning("[RTV.MapLister] LoadMaps called before plugin was assigned.");
                return;
            }

            // Beside the plugin's own config file, so each server keeps its own list while sharing
            // one plugin directory.
            string? mapsFile = PluginPaths.Resolve("maplist.txt");

            if (mapsFile is null || !File.Exists(mapsFile))
            {
                _debugLogger.LogError("[RTV.MapLister] No maplist.txt at {Path}.", mapsFile ?? "(config not parsed yet)");

                if (mapsFile is not null)
                {
                    SeedExample();
                    Server.PrintToConsole($"[RTV] maplist.txt not found. Put it here: {mapsFile}");
                }

                EventMapsLoaded?.Invoke(this, Maps);
                return;
            }

            try
            {
                Maps = [.. File.ReadAllText(mapsFile)
                    .Replace("\r\n", "\n")
                    .Split("\n")
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("//"))
                    .Select(mapLine =>
                    {
                        string[] args = mapLine.Split(":");
                        string mapName = args[0];
                        string? mapValue = args.Length == 2 ? args[1] : null;
                        return new Map(mapName, mapValue);
                    })];

                MapsLoaded = true;
                _debugLogger.LogInformation("[RTV.MapLister] Loaded {MapCount} maps from {MapListPath}.", Maps.Length, mapsFile);
                Server.PrintToConsole($"[RTV] Loaded {Maps.Length} maps from {mapsFile}");
            }
            catch (Exception ex)
            {
                Clear();
                _debugLogger.LogError(ex, "[RTV.MapLister] Failed to load map list from {MapListPath}.", mapsFile);
                Server.PrintToConsole($"[RTV] Failed to load maplist.txt: {ex.Message}");
            }

            EventMapsLoaded?.Invoke(this, Maps);
        }

        /// <summary>
        /// Drops maplist.example.txt into the preferred location on a server that has no map list
        /// anywhere. Without this the admin is told a path that does not exist and has to create
        /// the folder themselves; with it, the file is sitting there ready to be renamed.
        /// </summary>
        private void SeedExample()
        {
            if (_plugin is null) return;

            try
            {
                var target = PluginPaths.Resolve("maplist.example.txt");
                if (target is null || File.Exists(target)) return;

                var source = Path.GetFullPath(Path.Combine(_plugin.ModulePath, "..", "maplist.example.txt"));
                if (!File.Exists(source)) return;

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.Copy(source, target);
                Server.PrintToConsole($"[RTV] Wrote an example list to {target} - rename it to maplist.txt");
            }
            catch (Exception ex)
            {
                // Seeding is a convenience. A read-only or missing configs directory must not turn
                // "no map list" into a crash on load.
                _debugLogger.LogWarning(ex, "[RTV.MapLister] Could not write the example map list.");
            }
        }

        public void OnMapStart(string _map)
        {
            if (_plugin is not null)
                LoadMaps();
        }

        public void OnConfigParsed(Config config)
        {
            _debugLogger = config.General.DebugLogging ? _logger : NullLogger<MapLister>.Instance;
            PluginPaths.Capture(config);
        }


        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
            LoadMaps();
        }

        // Returns the exact map name, or null if none found
        public string? GetExactMapName(string name)
        {
            return Maps
                .Select(m => m.Name)
                .FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // Returns all map names that contain the given argument
        public List<string> GetMatchingMapNames(string partial)
        {
            return [.. Maps
                .Select(m => m.Name)
                .Where(n => n.Contains(partial, StringComparison.OrdinalIgnoreCase))];
        }

        // Disable maps no longer available on the workshop from showing (maplist.txt is left untouched)
        public void PruneMaps(IEnumerable<Map> toRemove)
        {
            var removeList = toRemove.ToList();
            if (removeList.Count == 0)
                return;

            int before = Maps.Length;
            Maps = [.. Maps.Where(m => !removeList.Any(r =>
                string.Equals(r.Name, m.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Id, m.Id, StringComparison.Ordinal)))];

            int removed = before - Maps.Length;
            if (removed == 0)
                return;

            _debugLogger.LogInformation("[RTV.MapLister] Pruned {Removed} unavailable map(s) from the in-memory list; {Remaining} remain.", removed, Maps.Length);
            EventMapsLoaded?.Invoke(this, Maps);
        }
    }
}
