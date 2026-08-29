using CounterStrikeSharp.API.Core.Capabilities;

namespace RockTheVote.Api;

/// <summary>
/// One entry of the server's map list.
/// </summary>
/// <param name="Name">Map name as it appears in maplist.txt, e.g. <c>de_dust2</c> or <c>kz_grotto</c>.</param>
/// <param name="WorkshopId">
/// The workshop file id, or null for a map shipped with the game. This is what decides how a map is
/// loaded: null means <c>changelevel</c>, an id means <c>host_workshop_map</c>.
/// </param>
public readonly record struct MapEntry(string Name, string? WorkshopId)
{
    /// <summary>True when this map has to be loaded through the workshop rather than changelevel.</summary>
    public bool IsWorkshop => !string.IsNullOrWhiteSpace(WorkshopId);
}

/// <summary>
/// Read the server's map list from another plugin.
///
/// <para>This assembly must live in <c>addons/counterstrikesharp/shared/</c> so every plugin
/// resolves the same types. A private copy next to a plugin gives it a different
/// <see cref="IMapListApi"/> and the capability cast then fails, looking exactly like RockTheVote
/// is not installed.</para>
/// </summary>
public interface IMapListApi
{
    /// <summary>
    /// Every map currently in the list, in maplist.txt order. A fresh array - mutating it changes
    /// nothing.
    ///
    /// <para>This is the live list, not the file: RockTheVote removes workshop maps it finds are no
    /// longer available, so this can be shorter than maplist.txt.</para>
    /// </summary>
    IReadOnlyList<MapEntry> Maps { get; }

    /// <summary>Whether the list has been loaded yet. False before the first map load.</summary>
    bool Loaded { get; }

    /// <summary>
    /// Raised whenever the list changes - a reload, a map change, or a prune. Consumers that cache
    /// the list should refresh here rather than polling.
    /// </summary>
    event Action<IReadOnlyList<MapEntry>>? MapsChanged;
}

/// <summary>Capability handle for <see cref="IMapListApi"/>.</summary>
public static class MapListApi
{
    public const string CapabilityName = "rockthevote:maplist";

    public static PluginCapability<IMapListApi> Capability { get; } = new(CapabilityName);
}
