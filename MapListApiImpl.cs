using RockTheVote.Api;

namespace cs2_rockthevote;

/// <summary>
/// The cross-plugin face of <see cref="MapLister"/>.
///
/// <para>The lister itself is unchanged - it still owns maplist.txt and the pruning of workshop
/// maps that have gone missing. This only exposes that state across the plugin load-context
/// boundary, translating the internal <see cref="Map"/> into the shared
/// <see cref="MapEntry"/> that both sides can agree on.</para>
/// </summary>
internal sealed class MapListApiImpl : IMapListApi
{
    private readonly MapLister _lister;

    public MapListApiImpl(MapLister lister)
    {
        _lister = lister;

        // MapLister raises this on load, on every map change, and after a prune - so consumers get
        // the pruned list rather than a stale copy of the file.
        _lister.EventMapsLoaded += (_, maps) => MapsChanged?.Invoke(Convert(maps));
    }

    public bool Loaded => _lister.MapsLoaded;

    public IReadOnlyList<MapEntry> Maps => Convert(_lister.Maps);

    public event Action<IReadOnlyList<MapEntry>>? MapsChanged;

    /// <summary>
    /// A blank id and a missing id mean the same thing - a stock map loaded with changelevel - so
    /// both collapse to null rather than leaving the consumer to guess.
    /// </summary>
    private static IReadOnlyList<MapEntry> Convert(IEnumerable<Map> maps) =>
        [.. maps.Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => new MapEntry(
                    m.Name.Trim(),
                    string.IsNullOrWhiteSpace(m.Id) ? null : m.Id!.Trim()))];
}
