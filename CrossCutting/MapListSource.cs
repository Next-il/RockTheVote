using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;

namespace cs2_rockthevote
{
    /// <summary>A fetched or cached list: the raw maplist.txt lines, or why there is none.</summary>
    internal sealed record MapListResult(string[]? Lines, string? Error)
    {
        public static MapListResult Fail(string reason) => new(null, reason);
    }

    /// <summary>
    /// Fetches General.MapListUrl. Runs off the main thread and touches no game state - the caller
    /// applies the result on the next frame.
    /// </summary>
    internal static class MapListFetcher
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        // A map list is a few KB. Anything near this is not a map list.
        private const int MaxBodyBytes = 1024 * 1024;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            // Pooled connections are recycled so a server that runs for days still follows DNS changes.
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = Timeout,
            };
            var client = new HttpClient(handler)
            {
                Timeout = Timeout,
                MaxResponseContentBufferSize = MaxBodyBytes,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RockTheVote/2.3");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        public static bool IsHttpUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        public static async Task<MapListResult> FetchAsync(string url, CancellationToken cancel)
        {
            try
            {
                using var response = await Http.GetAsync(url, cancel).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return MapListResult.Fail("404, no list at that URL");
                if (!response.IsSuccessStatusCode)
                    return MapListResult.Fail($"HTTP {(int)response.StatusCode}");

                var body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                return Parse(body);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return MapListResult.Fail("cancelled, plugin unloading");
            }
            catch (OperationCanceledException)
            {
                return MapListResult.Fail($"no answer within {Timeout.TotalSeconds:0}s");
            }
            catch (HttpRequestException ex)
            {
                return MapListResult.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                return MapListResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Accepts only a JSON array of strings that passes <see cref="Validate"/>.</summary>
        public static MapListResult Parse(string body)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return MapListResult.Fail("the response is not JSON");
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                    return MapListResult.Fail($"the response is a JSON {root.ValueKind}, not an array");

                var lines = new List<string>();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return MapListResult.Fail($"item {lines.Count} is a JSON {item.ValueKind}, not a string");
                    lines.Add(item.GetString()!);
                }

                return Validate(lines);
            }
        }

        /// <summary>
        /// Every line must be one maplist.txt line that MapLister's own parser reads as a map, a
        /// blank or a // comment, and at least one map must come out. One bad line rejects the
        /// whole list: a half-applied list is harder to notice than the WARN this produces.
        /// </summary>
        public static MapListResult Validate(IReadOnlyList<string> lines)
        {
            int maps = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.IndexOfAny(['\r', '\n']) >= 0)
                    return MapListResult.Fail($"item {i} has a line break");

                var map = MapLister.ParseLine(line);
                if (map is null)
                    continue;
                if (map.Name.Length == 0)
                    return MapListResult.Fail($"item {i} (\"{Shorten(line)}\") has no map name");
                maps++;
            }

            if (maps == 0)
                return MapListResult.Fail(lines.Count == 0 ? "the list is empty" : "the list has no maps, only blanks or comments");

            return new MapListResult([.. lines], null);
        }

        private static string Shorten(string line) => line.Length <= 40 ? line : line[..40] + "...";
    }

    /// <summary>
    /// The last good fetched list on disk, so a restart while the website is down still gets it.
    ///
    /// <para><b>Where.</b> <c>csgo/rockthevote/maplist-cache-&lt;port&gt;.json</c> under the server's
    /// own game directory. The config folder (configs/plugins/RockTheVote) and the plugin folder are
    /// Mounts bind mounts shared by every server of a mode on a node, and a file several servers
    /// rewrite there races at restart waves. csgo/rockthevote is no mount target, and the port in
    /// the name is a second guard: ports are unique per node, and a mount is per node.</para>
    ///
    /// <para><b>Checked at runtime.</b> On Linux the folder must sit on the same mount as the game
    /// directory itself (per /proc/self/mountinfo). If someone ever mounts it, or the port is not
    /// known, the cache is skipped and the last good list lives in memory only.</para>
    /// </summary>
    internal static class MapListCache
    {
        private sealed class CacheFile
        {
            public string Url { get; set; } = "";
            public DateTime SavedAtUtc { get; set; }
            public string[] Lines { get; set; } = [];
        }

        public sealed record Entry(string[] Lines, DateTime SavedAtUtc);

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // The mount layout does not change while the server runs; checked once per folder.
        private static string? _checkedDirectory;
        private static string? _checkedDirectoryProblem;

        /// <summary>
        /// The cache file for this server, or null with the reason it has none. Main thread only:
        /// it reads the hostport convar. Resolved per use, because hostport may not be set yet when
        /// the plugin loads at boot.
        /// </summary>
        public static string? Resolve(out string? problem)
        {
            problem = null;
            try
            {
                int port = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 0;
                if (port <= 0)
                {
                    problem = "the server port is not known yet";
                    return null;
                }

                var gameDir = Path.GetFullPath(Server.GameDirectory);
                var dir = Path.Combine(gameDir, "csgo", "rockthevote");

                if (_checkedDirectory != dir)
                {
                    _checkedDirectory = dir;
                    _checkedDirectoryProblem = SharedMountProblem(dir, gameDir);
                }

                if (_checkedDirectoryProblem is not null)
                {
                    problem = _checkedDirectoryProblem;
                    return null;
                }

                return Path.Combine(dir, $"maplist-cache-{port}.json");
            }
            catch (Exception ex)
            {
                problem = $"could not work out the cache path: {ex.Message}";
                return null;
            }
        }

        /// <summary>The cached lines for <paramref name="url"/>; null when there is no usable cache.</summary>
        public static Entry? Read(string path, string url)
        {
            if (!File.Exists(path))
                return null;

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);

            // A list cached for another URL is another mode's list.
            if (file is null || !string.Equals(file.Url, url, StringComparison.Ordinal))
                return null;

            return MapListFetcher.Validate(file.Lines).Lines is { } lines
                ? new Entry(lines, file.SavedAtUtc)
                : null;
        }

        /// <summary>Writes a temp file and renames it over the old one, so a reader never sees half a file.</summary>
        public static void Write(string path, string url, string[] lines)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new CacheFile
            {
                Url = url,
                SavedAtUtc = DateTime.UtcNow,
                Lines = lines,
            }, JsonOptions);

            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>
        /// Null when <paramref name="dir"/> is on the same mount as the game directory, i.e. the
        /// server's own volume. Non-Linux hosts have no mountinfo; the port in the name still guards.
        /// </summary>
        private static string? SharedMountProblem(string dir, string gameDir)
        {
            const string mountInfo = "/proc/self/mountinfo";
            if (!File.Exists(mountInfo))
                return null;

            var mountPoints = new List<string>();
            foreach (var line in File.ReadLines(mountInfo))
            {
                // "36 35 98:0 /root /mount/point rw,... - ext4 /dev/x rw": the 5th field is the mount point.
                var fields = line.Split(' ');
                if (fields.Length > 4)
                    mountPoints.Add(UnescapeMountPath(fields[4]));
            }

            var dirMount = CoveringMount(dir, mountPoints);
            var gameMount = CoveringMount(gameDir, mountPoints);
            return dirMount == gameMount
                ? null
                : $"{dir} is inside the bind mount {dirMount}, which may be shared with other servers";
        }

        private static string? CoveringMount(string path, List<string> mountPoints) =>
            mountPoints.Where(m => IsUnder(path, m)).MaxBy(m => m.Length);

        private static bool IsUnder(string path, string mount) =>
            mount == "/"
            || path == mount
            || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);

        // mountinfo writes space, tab, newline and backslash as \040, \011, \012, \134.
        private static string UnescapeMountPath(string field) =>
            Regex.Replace(field, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
    }
}
