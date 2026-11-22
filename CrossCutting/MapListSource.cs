using System.Net;
using System.Text.Json;

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
    /// The last good fetched list on disk, so a restart while the website is down still gets it:
    /// <c>csgo/rockthevote/maplist-cache-&lt;port&gt;.json</c>. See <see cref="ServerLocalData"/>
    /// for why it lives there and how a shared mount is caught.
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

        /// <inheritdoc cref="ServerLocalData.Resolve"/>
        public static string? Resolve(out string? problem, out bool notYet) =>
            ServerLocalData.Resolve("maplist-cache", ".json", out problem, out notYet);

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

        public static void Write(string path, string url, string[] lines) =>
            ServerLocalData.WriteAtomic(path, JsonSerializer.Serialize(new CacheFile
            {
                Url = url,
                SavedAtUtc = DateTime.UtcNow,
                Lines = lines,
            }, JsonOptions));
    }
}
