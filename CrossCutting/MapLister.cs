using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace cs2_rockthevote
{
    public class MapLister : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<MapLister> _logger;
        private readonly PluginState _pluginState;
        public Map[] Maps { get; private set; } = Array.Empty<Map>();
        public bool MapsLoaded { get; private set; } = false;
        public event EventHandler<Map[]>? EventMapsLoaded;
        private Plugin? _plugin;
        private Config? _config;
        private ILogger _debugLogger = NullLogger.Instance;

        // --- MapListUrl state. Main thread only, except what the fetch task captures. ---

        /// <summary>The newest list the URL returned (or the cache file held), and which URL.</summary>
        private string[]? _lastGoodLines;
        private string? _lastGoodUrl;

        /// <summary>The lines <see cref="Maps"/> was built from; null when it came from maplist.txt.</summary>
        private string[]? _appliedLines;
        private string _source = "maplist.txt";

        /// <summary>What the cache file is known to hold, so an unchanged list is not rewritten every map.</summary>
        private string[]? _cachedLines;
        private string? _cachedUrl;
        private bool _cacheProblemLogged;

        private int _fetchSeq;
        private int _landedSeq;
        private int _fetchesInFlight;
        private string? _inFlightUrl;
        private readonly CancellationTokenSource _unloadCancel = new();

        /// <summary>Polls for the end of the open vote while a fetched list waits to be applied.</summary>
        private Timer? _heldListTimer;

        public MapLister(ILogger<MapLister> logger, PluginState pluginState)
        {
            _logger = logger;
            _pluginState = pluginState;
        }

        /// <summary>General.MapListUrl, trimmed. Read live so css_reloadrtv can change it.</summary>
        public string MapListUrl => _config?.General.MapListUrl?.Trim() ?? "";

        public bool UsesUrl => MapListUrl.Length > 0;

        public void Clear()
        {
            MapsLoaded = false;
            Maps = Array.Empty<Map>();
        }

        /// <summary>
        /// One maplist.txt line to a map: null for a blank or // comment line. The file and the URL
        /// both go through here, so a line means the same thing wherever it comes from.
        /// </summary>
        internal static Map? ParseLine(string rawLine)
        {
            string mapLine = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(mapLine) || mapLine.StartsWith("//"))
                return null;

            string[] args = mapLine.Split(":");
            string mapName = args[0];
            string? mapValue = args.Length == 2 ? args[1] : null;
            return new Map(mapName, mapValue);
        }

        internal static Map[] ParseLines(IEnumerable<string> rawLines) =>
            [.. rawLines.Select(ParseLine).OfType<Map>()];

        /// <summary>
        /// Rebuilds the list right now. Without MapListUrl: maplist.txt, as always. With it: the best
        /// list already in hand - last good, then the cache file, then maplist.txt. A fetch from the
        /// URL is separate (<see cref="FetchFromUrl"/>) and only ever replaces this with a good list.
        /// </summary>
        public void LoadMaps()
        {
            if (UsesUrl)
                ApplyBestKnown();
            else
                LoadFromFile(quietWhenMissing: false);
        }

        private void LoadFromFile(bool quietWhenMissing)
        {
            Clear();
            _appliedLines = null;
            _source = "maplist.txt";

            if (_plugin is null)
            {
                _logger.LogWarning("[RTV.MapLister] LoadMaps called before plugin was assigned.");
                return;
            }

            // Beside the plugin's own config file, so each server keeps its own list while sharing
            // one plugin directory.
            string? mapsFile = PluginPaths.Resolve("maplist.txt");

            if (mapsFile is null || !File.Exists(mapsFile))
            {
                // With MapListUrl set, maplist.txt is only the last resort and may well be gone;
                // the fetch result line already says where the maps came from.
                if (!quietWhenMissing)
                {
                    _logger.LogError("[RTV.MapLister] No maplist.txt at {Path}.", mapsFile ?? "(config not parsed yet)");

                    if (mapsFile is not null)
                    {
                        SeedExample();
                        Server.PrintToConsole($"[RTV] maplist.txt not found. Put it here: {mapsFile}");
                    }
                }

                _source = "maplist.txt (missing)";
                EventMapsLoaded?.Invoke(this, Maps);
                return;
            }

            try
            {
                Maps = ParseLines(File.ReadAllText(mapsFile)
                    .Replace("\r\n", "\n")
                    .Split("\n"));

                MapsLoaded = true;
                _debugLogger.LogInformation("[RTV.MapLister] Loaded {MapCount} maps from {MapListPath}.", Maps.Length, mapsFile);
                Server.PrintToConsole($"[RTV] Loaded {Maps.Length} maps from {mapsFile}");
            }
            catch (Exception ex)
            {
                Clear();
                _logger.LogError(ex, "[RTV.MapLister] Failed to load map list from {MapListPath}. message={Message}", mapsFile, ex.Message);
                Server.PrintToConsole($"[RTV.MapLister] Failed to load map list from {mapsFile}. message={ex.Message}");
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
            if (_plugin is null)
                return;

            // A list held back for the last map's vote is in _lastGoodLines and lands just below.
            StopHeldListTimer();
            LoadMaps();

            // At boot the load-time fetch is usually still out; its answer is just as fresh.
            if (UsesUrl)
                FetchFromUrl(joinInFlight: true);
        }

        public void OnConfigParsed(Config config)
        {
            _config = config;
            _debugLogger = DebugLog.For(_logger, config);
            PluginPaths.Capture(config);
        }


        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
            LoadMaps();

            if (UsesUrl)
                FetchFromUrl();
        }

        public void Unload(Plugin plugin)
        {
            _unloadCancel.Cancel();
            StopHeldListTimer();
        }

        // ---------------------------------------------------------------------------------------
        // MapListUrl
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Fetches MapListUrl off the main thread and applies a good answer on the next frame. A bad
        /// or missing answer never touches the current list. <paramref name="report"/> gets one line
        /// for whoever asked (css_reloadmaps), on the main thread.
        /// </summary>
        public void FetchFromUrl(Action<string>? report = null, bool joinInFlight = false)
        {
            string url = MapListUrl;
            if (url.Length == 0)
                return;

            if (joinInFlight && _fetchesInFlight > 0 && _inFlightUrl == url)
                return;

            int seq = ++_fetchSeq;

            if (!MapListFetcher.IsHttpUrl(url))
            {
                OnFetched(seq, url, MapListResult.Fail("MapListUrl is not an http(s) URL"), report);
                return;
            }

            _fetchesInFlight++;
            _inFlightUrl = url;
            var cancel = _unloadCancel.Token;

            _ = Task.Run(async () =>
            {
                var result = await MapListFetcher.FetchAsync(url, cancel).ConfigureAwait(false);
                if (cancel.IsCancellationRequested)
                    return;

                Server.NextFrame(() =>
                {
                    _fetchesInFlight--;
                    OnFetched(seq, url, result, report);
                });
            });
        }

        private void OnFetched(int seq, string url, MapListResult result, Action<string>? report)
        {
            if (_pluginState.Unloaded)
                return;

            // A newer fetch already landed, or css_reloadrtv pointed MapListUrl somewhere else.
            if (seq < _landedSeq || url != MapListUrl)
            {
                _debugLogger.LogInformation("[RTV.MapList] Dropped a stale fetch result for {Url}.", url);
                return;
            }

            if (result.Lines is not { } lines)
            {
                _logger.LogWarning("[RTV.MapList] Fetching {Url} failed: {Reason}.", url, result.Error);
                _logger.LogInformation("[RTV.MapList] Using {Count} maps from {Source}.", Maps.Length, _source);
                report?.Invoke($"Fetch failed ({result.Error}). Kept {Maps.Length} maps from {_source}.");
                return;
            }

            _landedSeq = seq;
            _lastGoodLines = lines;
            _lastGoodUrl = url;
            SaveCache(url, lines);

            int count = ParseLines(lines).Length;

            if (_appliedLines is not null && _appliedLines.SequenceEqual(lines))
            {
                StopHeldListTimer();
                _source = "the website";
                _logger.LogInformation("[RTV.MapList] Using {Count} maps from {Url} (unchanged).", count, url);
                report?.Invoke($"{count} maps from the website, no change.");
                return;
            }

            // The open vote keeps its options, and a scheduled change must still find its map.
            if (VoteOrChangePending)
            {
                StartHeldListTimer();
                _logger.LogInformation("[RTV.MapList] Fetched {Count} maps from {Url}; they apply once the current vote is over.", count, url);
                report?.Invoke($"{count} maps from the website. They apply once the current vote is over.");
                return;
            }

            ApplyLines(lines, "the website");
            _logger.LogInformation("[RTV.MapList] Using {Count} maps from {Url}.", count, url);
            report?.Invoke($"{count} maps from the website.");
        }

        private bool VoteOrChangePending => _pluginState.EofVoteHappening || _pluginState.MapChangeScheduled;

        /// <summary>Last good, then the cache file, then maplist.txt. Never waits on the network.</summary>
        private void ApplyBestKnown()
        {
            string url = MapListUrl;

            if (_lastGoodLines is not null && _lastGoodUrl == url)
            {
                ApplyLines(_lastGoodLines, "the last good list");
                return;
            }

            // Silent here: before the first map there is no path yet. SaveCache reports a lasting problem.
            var cachePath = MapListCache.Resolve(out _, out _);
            if (cachePath is not null)
            {
                try
                {
                    if (MapListCache.Read(cachePath, url) is { } cached)
                    {
                        _lastGoodLines = cached.Lines;
                        _lastGoodUrl = url;
                        _cachedLines = cached.Lines;
                        _cachedUrl = url;
                        ApplyLines(cached.Lines, $"the cache file {cachePath} (saved {cached.SavedAtUtc:yyyy-MM-dd HH:mm} UTC)");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger.LogWarning(ex, "[RTV.MapList] Could not read the cache file {Path}.", cachePath);
                }
            }

            LoadFromFile(quietWhenMissing: true);
        }

        private void ApplyLines(string[] lines, string source)
        {
            Maps = ParseLines(lines);
            MapsLoaded = true;
            _appliedLines = lines;
            _source = source;
            _debugLogger.LogInformation("[RTV.MapList] Applied {Count} maps from {Source}.", Maps.Length, source);
            EventMapsLoaded?.Invoke(this, Maps);
        }

        private void StartHeldListTimer()
        {
            if (_heldListTimer is not null || _plugin is null)
                return;

            _heldListTimer = _plugin.AddTimer(1.0F, () =>
            {
                if (VoteOrChangePending)
                    return;

                StopHeldListTimer();
                if (_lastGoodLines is { } held && _lastGoodUrl == MapListUrl)
                    ApplyLines(held, "the website");
            }, TimerFlags.REPEAT);
        }

        private void StopHeldListTimer()
        {
            _heldListTimer?.Kill();
            _heldListTimer = null;
        }

        /// <summary>
        /// Runs after a good fetch. A fetch that lands before the first map skips the save quietly
        /// (the map-start fetch saves); any other missing path is worth one WARN per plugin load.
        /// </summary>
        private void SaveCache(string url, string[] lines)
        {
            if (_cachedUrl == url && _cachedLines is not null && _cachedLines.SequenceEqual(lines))
                return;

            var path = MapListCache.Resolve(out var problem, out bool notYet);
            if (path is null)
            {
                if (!notYet && !_cacheProblemLogged)
                {
                    _cacheProblemLogged = true;
                    _logger.LogWarning("[RTV.MapList] No cache file, the last good list is kept in memory only: {Problem}.", problem);
                }
                return;
            }

            try
            {
                MapListCache.Write(path, url, lines);
                _cachedLines = lines;
                _cachedUrl = url;
            }
            catch (Exception ex)
            {
                if (_cacheProblemLogged)
                    return;
                _cacheProblemLogged = true;
                _logger.LogWarning("[RTV.MapList] Could not write the cache file {Path}, the last good list is kept in memory only: {Message}.", path, ex.Message);
            }
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
