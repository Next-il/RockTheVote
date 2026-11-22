using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace cs2_rockthevote.Core
{
    public class MapCooldown : IPluginDependency<Plugin, Config>
    {
        List<string> mapsOnCoolDown = new();
        private GeneralConfig _generalConfig = new();
        public event EventHandler<Map[]>? EventCooldownRefreshed;

        // Per-server backup so the cooldown survives a restart: csgo/rockthevote/mapcooldown-<port>.txt
        // (see ServerLocalData). Null until EnsureRestored has run, and for good when memory-only.
        private string? _cooldownFilePath;
        private bool _restored;

        private readonly ILogger<MapCooldown> _logger;

        public MapCooldown(MapLister mapLister, ILogger<MapCooldown> logger)
        {
            _logger = logger;

            // Each time the maps load (i.e. on map start), refresh our list
            mapLister.EventMapsLoaded += (sender, maps) =>
            {
                // Skip until the backup is restored, else we'd overwrite it with an empty list
                if (EnsureRestored())
                    RegisterCurrentMap();

                EventCooldownRefreshed?.Invoke(this, maps);
            };
        }

        public void OnConfigParsed(Config config)
        {
            _generalConfig = config.General;

            // Only for finding the old shared mapcooldown.txt beside the config, read once to seed
            // the per-server file. Capture is idempotent.
            PluginPaths.Capture(config);
        }

        public void OnLoad(Plugin plugin)
        {
            // At boot no map has started yet and this does nothing; the first map start restores.
            if (EnsureRestored())
                RegisterCurrentMap();
        }

        /// <summary>
        /// Loads the saved cooldown once. False until the server has started a map: before that the
        /// per-server path is not known yet, and no map is current anyway.
        ///
        /// <para>The cooldown used to live in configs/plugins/RockTheVote/mapcooldown.txt, which is a
        /// mount shared by every server of a mode on the node, so they all raced on one file. Now each
        /// server keeps its own. The first time, the old shared file is read to seed it, so cooldowns
        /// carry over; it is never written again.</para>
        /// </summary>
        private bool EnsureRestored()
        {
            if (_restored)
                return true;

            var path = ServerLocalData.Resolve("mapcooldown", ".txt", out var problem, out bool notYet);
            if (path is null && notYet)
                return false;

            _restored = true;

            if (path is not null && File.Exists(path))
            {
                _cooldownFilePath = path;
                LoadCooldownFromFile(path);
                return true;
            }

            var legacy = PluginPaths.Resolve("mapcooldown.txt");
            bool seeded = legacy is not null && File.Exists(legacy) && LoadCooldownFromFile(legacy);

            if (path is null)
            {
                _logger.LogWarning("[RTV.Cooldown] No per-server cooldown file, the cooldown is kept in memory only: {Problem}.", problem);
                return true;
            }

            // The caller's RegisterCurrentMap writes the file right after this.
            _cooldownFilePath = path;
            if (seeded)
                _logger.LogInformation("[RTV.Cooldown] Seeding {Path} with {Count} map(s) from the old shared {Legacy}.", path, mapsOnCoolDown.Count, legacy);
            return true;
        }

        // Adds the current map, drops the oldest past the limit, and saves to disk
        private void RegisterCurrentMap()
        {
            var current = Server.MapName?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(current))
                return;

            int maxEntries = _generalConfig.MapsInCoolDown;

            // If cooldown is disabled, keep only the current map
            if (maxEntries <= 0)
            {
                mapsOnCoolDown.Clear();
                mapsOnCoolDown.Add(current);
            }
            else
            {
                // Skip if it's already the newest entry, so a reload doesn't duplicate it
                if (mapsOnCoolDown.Count == 0 || mapsOnCoolDown[^1] != current)
                    mapsOnCoolDown.Add(current);

                // Cycle the oldest maps back into rotation until within the limit
                while (mapsOnCoolDown.Count > maxEntries)
                    mapsOnCoolDown.RemoveAt(0);
            }

            SaveCooldownToFile();
        }

        // Loads the saved cooldown maps from disk, trimmed to the configured limit. False if unreadable.
        private bool LoadCooldownFromFile(string path)
        {
            try
            {
                var restored = File.ReadAllLines(path)
                    .Select(x => x.Trim().ToLowerInvariant())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                int maxEntries = _generalConfig.MapsInCoolDown;
                if (maxEntries > 0 && restored.Count > maxEntries)
                    restored = restored.Skip(restored.Count - maxEntries).ToList();

                mapsOnCoolDown = restored;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.Cooldown] Failed to load map cooldown file from {CooldownPath}. message={Message}", path, ex.Message);
                Server.PrintToConsole($"[RTV.Cooldown] Failed to load map cooldown file from {path}. message={ex.Message}");
                return false;
            }
        }

        // Writes the current cooldown window to this server's file, one base map name per line
        private void SaveCooldownToFile()
        {
            if (string.IsNullOrEmpty(_cooldownFilePath))
                return;

            try
            {
                ServerLocalData.WriteAtomic(_cooldownFilePath,
                    string.Concat(mapsOnCoolDown.Select(m => m + Environment.NewLine)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.Cooldown] Failed to save map cooldown file to {CooldownPath}. message={Message}", _cooldownFilePath, ex.Message);
                Server.PrintToConsole($"[RTV.Cooldown] Failed to save map cooldown file to {_cooldownFilePath}. message={ex.Message}");
            }
        }

        // Ordered oldest -> newest, the last entry is the most recently played map
        public IReadOnlyList<string> MapsOnCooldown => mapsOnCoolDown;

        public bool IsMapInCooldown(string map)
        {
            if (string.IsNullOrEmpty(map))
                return false;

            // Grab the base map name, e.g. "surf_beginner (T1, Staged)" -> "surf_beginner"
            var baseName = MapNameHelper.GetBaseName(map);

            // Compare lowercase
            var lowerName = baseName.Trim().ToLowerInvariant();

            // Always exclude the current map
            var current = Server.MapName?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(current) && lowerName == current)
                return true;

            return mapsOnCoolDown.Contains(lowerName);
        }
    }
}
