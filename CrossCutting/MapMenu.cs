using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using cs2_rockthevote.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using static CounterStrikeSharp.API.Core.Listeners;

namespace cs2_rockthevote
{
    /// <summary>What a cell says about its map. Separate from whether it can be clicked (<see cref="MapMenuRow.Enabled"/>).</summary>
    public enum MapRowTone { None, Marked, Cooldown, Current }

    /// <summary>One map (or vote option) in a menu.</summary>
    /// <param name="Key">What a pick gets back: the map's map-list name, or the vote option.</param>
    /// <param name="Name">Drawn bold: the base map name.</param>
    /// <param name="Detail">Drawn muted after it, e.g. "Tier 1, Linear" from "surf_nyx (Tier 1, Linear)".</param>
    /// <param name="Tag">The right-hand tag: "Nominated", "Back in 2 maps", "3 · 42%".</param>
    /// <param name="Reason">The status line when a disabled cell is clicked anyway.</param>
    /// <param name="Percent">0..100 draws the vote-share bar behind the cell; -1 draws none.</param>
    public sealed record MapMenuRow(string Key, string Name, string Detail, string Tag, MapRowTone Tone, bool Enabled,
        bool Mine = false, string? Reason = null, int Percent = -1);

    /// <summary>What a pick did: close the menu, or keep it open with a status line.</summary>
    public readonly record struct MapMenuResult(bool Close, string? Status = null)
    {
        public static MapMenuResult Done => new(true);
        public static MapMenuResult Stay(string status) => new(false, status);
    }

    /// <summary>
    /// One menu: what it lists and what a click does. Rows are rebuilt on every draw, so a menu always shows the
    /// state of now (someone else's nomination, a vote that started) without the feature pushing anything.
    /// </summary>
    public sealed class MapMenuSpec
    {
        /// <summary>"nominate", "maps", "votemap", "mapchooser" or "vote": what <see cref="MapMenu.Refresh"/> and
        /// <see cref="MapMenu.Close"/> address.</summary>
        public required string Kind { get; init; }
        public required Func<CCSPlayerController, string> Title { get; init; }
        public required Func<CCSPlayerController, IReadOnlyList<MapMenuRow>> Rows { get; init; }

        /// <summary>A click on an enabled row, checked again against fresh rows first.</summary>
        public required Func<CCSPlayerController, MapMenuRow, MapMenuResult> Pick { get; init; }

        public Func<CCSPlayerController, string>? Hint { get; init; }

        /// <summary>The header's right side: "NOW de_mirage", "TIME LEFT 35s".</summary>
        public Func<CCSPlayerController, (string Key, string Value)>? Now { get; init; }

        public bool Search { get; init; } = true;

        /// <summary>The ALL / AVAILABLE chips.</summary>
        public bool Filters { get; init; } = true;

        /// <summary>The third chip's label for a count, e.g. "Nominated 3". Null: no third chip.</summary>
        public Func<int, string>? MarkedFilter { get; init; }

        /// <summary>Two clicks: the first arms the row and puts this in the status line, the second picks.</summary>
        public Func<MapMenuRow, string>? Confirm { get; init; }

        /// <summary>One wide column of 8 instead of two (the vote menus).</summary>
        public bool OneColumn { get; init; }

        /// <summary>The chat fallback gets an exit option.</summary>
        public bool ChatExit { get; init; } = true;
    }

    /// <summary>
    /// The map menus (hud/panorama/layout/custom_game/rtv_menu.xml, shipped in the workshop addon): !nominate, !maps,
    /// !votemap, the admin !mapmenu and !revote - and the map vote itself when the vote card is off. A mouse-driven
    /// card with a chat search, filter chips and paging, so a timer server's few hundred maps stay usable.
    ///
    /// <para><b>The features say what, this says how.</b> Each one hands over a <see cref="MapMenuSpec"/>; this class
    /// draws it, pages it, searches it and routes the click back. <see cref="MapRows"/> builds the shared part of a map
    /// row (the current map, the cooldown, the "Tier 1" detail) so every menu marks them the same way.</para>
    ///
    /// <para><b>Only what changed is written.</b> Each viewer keeps the strings and classes last sent, so a redraw on
    /// every vote or every second costs a handful of writes. The cache starts empty whenever the panel starts from its
    /// authored state again: a fresh open, and after the library rebuilt it (PanelAction.Restored).</para>
    ///
    /// <para><b>Without the HUD</b> (PanoramaManager failed to start, or MenuType "ChatMenu") the same spec becomes
    /// CounterStrikeSharp's own chat menu, !1-!9. CS2MenuManager is gone.</para>
    /// </summary>
    public sealed class MapMenu : IPluginDependency<Plugin, Config>
    {
        private const string Layout = "panorama/layout/custom_game/rtv_menu.vxml_c";
        private const string Root = "RtvMenuRoot";

        /// <summary>The cell pool in the layout: two columns of 8, column-major (mm_i0..7 left, mm_i8..15 right).</summary>
        public const int RowsPerColumn = 8;
        public const int Cells = RowsPerColumn * 2;

        private const int BarSteps = 20;
        private const int QueryLength = 48;

        private static readonly string[] ToneClasses = ["t-mark", "t-cool", "t-cur"];
        private static readonly string[] BarClasses = Enumerable.Range(1, BarSteps).Select(i => "b" + i).ToArray();

        private sealed class Viewer
        {
            public required MapMenuSpec Spec;
            public string Query = "";
            public int Filter;          // 0 all, 1 available, 2 marked
            public int Page;
            public int Pages = 1;
            public string? Armed;
            public string Status = "";
            public bool Typing;
            public readonly List<string> Shown = [];
            public Dictionary<string, string> Vars = [];
            public Dictionary<(string Panel, string Class), bool> Classes = [];
        }

        private readonly ILogger<MapMenu> _logger;
        private readonly StringLocalizer _localizer;
        private readonly MapLister _mapLister;
        private readonly MapCooldown _mapCooldown;
        private readonly Dictionary<int, Viewer> _viewers = [];
        private GeneralConfig _generalConfig = new();
        private PanelHandle? _panel;

        public MapMenu(StringLocalizer localizer, MapLister mapLister, MapCooldown mapCooldown, ILogger<MapMenu> logger)
        {
            _localizer = localizer;
            _mapLister = mapLister;
            _mapCooldown = mapCooldown;
            _logger = logger;
        }

        /// <summary>The HUD menu is up. False: every menu is the chat menu.</summary>
        public bool Available => _panel != null;

        public void OnConfigParsed(Config config)
        {
            _generalConfig = config.General;
        }

        public void OnLoad(Plugin plugin)
        {
            plugin.RegisterListener<OnClientDisconnect>(slot => _viewers.Remove(slot));

            if (_panel != null)
                return;

            try
            {
                Panorama.Init(plugin);
                _panel = Panorama.Spawn(Layout, new LayoutContract
                {
                    // Unique server-wide: dialog variables are keyed by panel id
                    RootPanelId   = Root,
                    RevealClass   = "show",
                    CloseButtonId = "mm_close",
                    RowCount      = 0,
                    CaptureInput  = true,
                    HideHud       = HideHudFlags.None,
                    SharedText    = false,
                });
                _panel.OnEvent += OnPanelEvent;
            }
            catch (Exception ex)
            {
                // A HUD that fails to start must not take the menus down - the chat menu still works.
                _logger.LogError(ex, "[RTV.Menu] The map menu HUD could not start; the menus fall back to the chat menu.");
                _panel = null;
            }
        }

        public void Unload(Plugin plugin)
        {
            _viewers.Clear();
            _panel?.Dispose();
            _panel = null;
        }

        /// <summary>"ChatMenu" in a MenuType: the chat menu instead of the HUD.</summary>
        public static bool WantsChat(string? menuType) => ConfigValue.Is(menuType, "ChatMenu");

        // ------------------------------------------------------------------ opening and closing

        /// <summary>
        /// Opens <paramref name="spec"/> for a player, on the HUD unless <paramref name="chat"/> or the HUD is down.
        /// <paramref name="query"/> starts it searched (!nominate dust with several matches). With
        /// <paramref name="toggle"/>, the same menu already open closes instead: there is no Escape key.
        /// </summary>
        public void Open(CCSPlayerController player, MapMenuSpec spec, string query = "", bool chat = false, bool toggle = false)
        {
            if (player is not { IsValid: true, IsBot: false, IsHLTV: false })
                return;

            query = CleanQuery(query);

            if (_panel == null || chat)
            {
                OpenChat(player, spec, query);
                return;
            }

            bool wasOpen = _panel.IsOpenFor(player) && _viewers.ContainsKey(player.Slot);
            if (wasOpen && toggle && query.Length == 0 && _viewers[player.Slot].Spec.Kind == spec.Kind)
            {
                _panel.Close(player);
                return;
            }

            var viewer = new Viewer { Spec = spec, Query = query };

            if (wasOpen)
            {
                // Switching menus on an open card: what is on screen is what the old viewer wrote
                var old = _viewers[player.Slot];
                viewer.Vars = old.Vars;
                viewer.Classes = old.Classes;
            }
            else
            {
                _panel.Open(player);
                if (!_panel.IsOpenFor(player))
                {
                    OpenChat(player, spec, query);
                    return;
                }
            }

            _viewers[player.Slot] = viewer;

            // After the swap, so the Abandoned answer finds a viewer that is not its own and draws nothing
            _panel.CancelPrompt(player);
            Draw(player, viewer);
        }

        public bool IsOpenFor(CCSPlayerController player, string? kind = null) =>
            _panel != null && _viewers.TryGetValue(player.Slot, out var viewer) && _panel.IsOpenFor(player)
            && (kind == null || viewer.Spec.Kind == kind);

        /// <summary>Redraws every open menu of <paramref name="kind"/> from fresh rows.</summary>
        public void Refresh(string kind)
        {
            foreach (var (player, viewer) in OpenViewers(kind))
                Draw(player, viewer);
        }

        /// <summary>Redraws every open menu.</summary>
        public void RefreshAll()
        {
            foreach (var (player, viewer) in OpenViewers(null))
                Draw(player, viewer);
        }

        public void Close(string kind)
        {
            foreach (var (player, _) in OpenViewers(kind))
                _panel?.Close(player);
        }

        private List<(CCSPlayerController Player, Viewer Viewer)> OpenViewers(string? kind)
        {
            var open = new List<(CCSPlayerController, Viewer)>();
            if (_panel == null)
                return open;

            foreach (var (slot, viewer) in _viewers.ToList())
            {
                if (kind != null && viewer.Spec.Kind != kind)
                    continue;

                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is { IsValid: true } && _panel.IsOpenFor(player))
                    open.Add((player, viewer));
            }

            return open;
        }

        // ------------------------------------------------------------------ the shared map rows

        /// <summary>
        /// The map list as rows, in map-list order. The current map and the maps on cooldown are marked, and not
        /// clickable unless <paramref name="unavailableClickable"/> (the admin's change map). <paramref name="decorate"/>
        /// adds the feature's own state: nominations, votes, the player's own pick.
        /// </summary>
        public List<MapMenuRow> MapRows(Func<Map, MapMenuRow, MapMenuRow>? decorate = null, bool unavailableClickable = false)
        {
            var maps = _mapLister.Maps;
            var rows = new List<MapMenuRow>(maps.Length);
            var current = CurrentMapName();

            foreach (var map in maps)
            {
                if (string.IsNullOrWhiteSpace(map.Name))
                    continue;

                var (name, detail) = SplitName(map.Name);
                MapMenuRow row;

                if (name.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    row = new MapMenuRow(map.Name, name, detail, _localizer.Localize("menu.tag.current"),
                        MapRowTone.Current, unavailableClickable, Reason: _localizer.Localize("menu.reason.current"));
                }
                else if (_mapCooldown.IsMapInCooldown(map.Name))
                {
                    int back = MapsUntilBack(name);
                    var when = back <= 1
                        ? _localizer.Localize("menu.tag.back-next")
                        : _localizer.Localize("menu.tag.back-in", back);
                    row = new MapMenuRow(map.Name, name, detail, when, MapRowTone.Cooldown, unavailableClickable,
                        Reason: _localizer.Localize("menu.reason.cooldown", when));
                }
                else
                {
                    row = new MapMenuRow(map.Name, name, detail, "", MapRowTone.None, true);
                }

                rows.Add(decorate != null ? decorate(map, row) : row);
            }

            return rows;
        }

        /// <summary>
        /// How many map changes until a map on cooldown is back: the list is oldest first and drops its oldest entry
        /// once it holds more than MapsInCoolDown, so the oldest map leaves on the next change of a full list.
        /// </summary>
        private int MapsUntilBack(string baseName)
        {
            var list = _mapCooldown.MapsOnCooldown;
            int index = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], baseName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return 1;

            return index + 1 + Math.Max(0, _generalConfig.MapsInCoolDown - list.Count);
        }

        /// <summary>"surf_nyx (Tier 1, Linear)" -> ("surf_nyx", "Tier 1, Linear"). A plain name has no detail.</summary>
        public static (string Name, string Detail) SplitName(string mapName)
        {
            var name = MapNameHelper.GetBaseName(mapName.Trim());
            var detail = mapName.Trim()[name.Length..].Trim();
            if (detail.Length >= 2 && detail[0] == '(' && detail[^1] == ')')
                detail = detail[1..^1].Trim();
            return (name, detail);
        }

        /// <summary>The map being played, as a bare name: a workshop path's last part, if the engine reports one.</summary>
        public static string CurrentMapName()
        {
            var map = Server.MapName?.Trim() ?? "";
            var slash = map.LastIndexOf('/');
            return slash >= 0 ? map[(slash + 1)..] : map;
        }

        /// <summary>The header's "NOW" line for the map menus.</summary>
        public (string Key, string Value) NowPlaying() => (_localizer.Localize("menu.now"), CurrentMapName());

        // ------------------------------------------------------------------ drawing

        private void Draw(CCSPlayerController player, Viewer v)
        {
            if (_panel == null || !_panel.IsOpenFor(player))
                return;

            var spec = v.Spec;
            var all = Rows(player, spec);
            var shown = all.Where(r => Passes(v.Filter, r) && Matches(r, v.Query)).ToList();

            int size = spec.OneColumn ? RowsPerColumn : Cells;
            v.Pages = Math.Max(1, (shown.Count + size - 1) / size);
            v.Page = Math.Clamp(v.Page, 0, v.Pages - 1);
            var page = shown.Skip(v.Page * size).Take(size).ToList();

            v.Shown.Clear();
            v.Shown.AddRange(page.Select(r => r.Key));
            if (v.Armed != null && !v.Shown.Contains(v.Armed))
                v.Armed = null;

            // Header
            Var(player, v, "mm_title", spec.Title(player));
            Var(player, v, "mm_tag", shown.Count == all.Count
                ? _localizer.Localize("menu.count", all.Count)
                : _localizer.Localize("menu.count-of", shown.Count, all.Count));
            var now = spec.Now?.Invoke(player) ?? ("", "");
            Var(player, v, "mm_now_k", now.Key);
            Var(player, v, "mm_now", now.Value);

            // Search and filters
            Var(player, v, "mm_search_k", _localizer.Localize("menu.search"));
            Var(player, v, "mm_q", v.Typing
                ? _localizer.Localize("menu.search-typing")
                : v.Query.Length > 0 ? v.Query : _localizer.Localize("menu.search-placeholder"));
            Cls(player, v, Root, "notools", !spec.Search && !spec.Filters);
            Cls(player, v, Root, "typing", v.Typing);
            Cls(player, v, Root, "has-query", v.Query.Length > 0);
            Cls(player, v, Root, "nofilter", !spec.Filters);
            Cls(player, v, Root, "nofind", !spec.Search);

            if (spec.Filters)
            {
                Var(player, v, "mm_f0", _localizer.Localize("menu.filter.all"));
                Var(player, v, "mm_f1", _localizer.Localize("menu.filter.available", all.Count(IsAvailable)));
                Var(player, v, "mm_f2", spec.MarkedFilter?.Invoke(all.Count(IsMarked)) ?? "");
                Cls(player, v, "mm_f2", "hidden", spec.MarkedFilter == null);
                for (int i = 0; i < 3; i++)
                    Cls(player, v, $"mm_f{i}", "on", v.Filter == i);
            }

            // Cells
            for (int i = 0; i < Cells; i++)
            {
                var id = $"mm_i{i}";
                if (i >= page.Count)
                {
                    Cls(player, v, id, "hidden", true);
                    continue;
                }

                var row = page[i];
                bool armed = v.Armed == row.Key;

                Var(player, v, id + "_n", row.Name);
                Var(player, v, id + "_d", row.Detail);
                Var(player, v, id + "_t", armed ? _localizer.Localize("menu.tag.confirm") : row.Tag);

                Cls(player, v, id, "off", !row.Enabled);
                Cls(player, v, id, "mine", row.Mine);
                Cls(player, v, id, "armed", armed);
                OneOf(player, v, id, row.Tone switch
                {
                    MapRowTone.Marked => "t-mark",
                    MapRowTone.Cooldown => "t-cool",
                    MapRowTone.Current => "t-cur",
                    _ => null,
                }, ToneClasses);

                Cls(player, v, id, "has-bar", row.Percent >= 0);
                int step = row.Percent <= 0 ? 0 : Math.Clamp((int)Math.Round(row.Percent * BarSteps / 100.0), 1, BarSteps);
                OneOf(player, v, id + "_bar", step > 0 ? BarClasses[step - 1] : null, BarClasses);

                Cls(player, v, id, "hidden", false);
            }

            // Body and footer
            Cls(player, v, Root, "cols1", spec.OneColumn);
            Cls(player, v, Root, "paged", v.Pages > 1);
            Cls(player, v, Root, "empty", shown.Count == 0);
            Var(player, v, "mm_empty", v.Query.Length > 0
                ? _localizer.Localize("menu.empty-search", v.Query)
                : _localizer.Localize(all.Count == 0 ? "menu.empty" : "menu.empty-filter"));
            Var(player, v, "mm_hint", spec.Hint?.Invoke(player) ?? "");
            Var(player, v, "mm_status", v.Status);
            Var(player, v, "mm_page", _localizer.Localize("menu.page", v.Page + 1, v.Pages));
        }

        private IReadOnlyList<MapMenuRow> Rows(CCSPlayerController player, MapMenuSpec spec)
        {
            try
            {
                return spec.Rows(player);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.Menu] Building the {Kind} rows failed.", spec.Kind);
                return [];
            }
        }

        private static bool IsAvailable(MapMenuRow row) => row.Tone is not (MapRowTone.Cooldown or MapRowTone.Current);
        private static bool IsMarked(MapMenuRow row) => row.Tone == MapRowTone.Marked || row.Mine;

        private static bool Passes(int filter, MapMenuRow row) => filter switch
        {
            1 => IsAvailable(row),
            2 => IsMarked(row),
            _ => true,
        };

        /// <summary>Every word of the query somewhere in the name or the detail: "nyx linear" finds surf_nyx (Tier 1, Linear).</summary>
        private static bool Matches(MapMenuRow row, string query)
        {
            if (query.Length == 0)
                return true;

            foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!row.Key.Contains(word, StringComparison.OrdinalIgnoreCase)
                    && !row.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                    && !row.Detail.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static string CleanQuery(string? query)
        {
            var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var clean = string.Join(' ', words);
            return clean.Length > QueryLength ? clean[..QueryLength] : clean;
        }

        // ------------------------------------------------------------------ input

        private void OnPanelEvent(PanelEvent e)
        {
            if (e.Player is not { IsValid: true } player)
                return;

            switch (e.Action)
            {
                case PanelAction.Close:
                    _viewers.Remove(player.Slot);
                    return;

                case PanelAction.Restored:
                    if (!_viewers.TryGetValue(player.Slot, out var restored))
                    {
                        _panel?.Close(player);
                        return;
                    }

                    // The rebuilt panel has none of our text or classes any more
                    restored.Vars = [];
                    restored.Classes = [];
                    Draw(player, restored);
                    return;

                case PanelAction.Button:
                    if (_viewers.TryGetValue(player.Slot, out var viewer))
                        Click(player, viewer, e.ElementId);
                    return;
            }
        }

        /// <summary>
        /// mm_iN picks, the rest pages, searches and filters. A pick is checked against fresh rows: the click comes
        /// from a client, and the map may have gone on cooldown, been nominated or left the list since the draw.
        /// </summary>
        private void Click(CCSPlayerController player, Viewer v, string id)
        {
            switch (id)
            {
                case "mm_prev":
                case "mm_next":
                    v.Page = (v.Page + (id == "mm_next" ? 1 : -1) + v.Pages) % v.Pages;
                    v.Armed = null;
                    v.Status = "";
                    Draw(player, v);
                    return;

                case "mm_search":
                    if (v.Spec.Search)
                        ToggleSearch(player, v);
                    return;

                case "mm_clear":
                    v.Query = "";
                    v.Page = 0;
                    v.Armed = null;
                    v.Status = "";
                    if (v.Typing)
                    {
                        v.Typing = false;
                        _panel?.CancelPrompt(player);
                    }
                    Draw(player, v);
                    return;

                case "mm_f0":
                case "mm_f1":
                case "mm_f2":
                    if (!v.Spec.Filters || (id == "mm_f2" && v.Spec.MarkedFilter == null))
                        return;
                    v.Filter = id[^1] - '0';
                    v.Page = 0;
                    v.Armed = null;
                    v.Status = "";
                    Draw(player, v);
                    return;
            }

            if (!id.StartsWith("mm_i", StringComparison.Ordinal)
                || !int.TryParse(id.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var cell)
                || cell >= v.Shown.Count)
                return;

            var key = v.Shown[cell];
            var row = Rows(player, v.Spec).FirstOrDefault(r => r.Key == key);
            if (row == null)
            {
                v.Status = "";
                Draw(player, v);
                return;
            }

            if (!row.Enabled)
            {
                v.Armed = null;
                v.Status = row.Reason ?? "";
                Draw(player, v);
                return;
            }

            if (v.Spec.Confirm != null && v.Armed != key)
            {
                v.Armed = key;
                v.Status = v.Spec.Confirm(row);
                Draw(player, v);
                return;
            }

            MapMenuResult result;
            try
            {
                result = v.Spec.Pick(player, row);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.Menu] The {Kind} pick failed. map={Map}", v.Spec.Kind, row.Key);
                result = MapMenuResult.Done;
            }

            if (result.Close)
            {
                _panel?.Close(player);
                return;
            }

            // The pick may have opened another menu on this card
            if (!_viewers.TryGetValue(player.Slot, out var live) || live != v)
                return;

            v.Armed = null;
            v.Status = result.Status ?? "";
            Draw(player, v);
        }

        /// <summary>
        /// A layout cannot take a keystroke, so the search field borrows chat: one line, swallowed rather than sent,
        /// becomes the query. A second click on the field stops waiting.
        /// </summary>
        private void ToggleSearch(CCSPlayerController player, Viewer v)
        {
            if (_panel == null)
                return;

            if (v.Typing)
            {
                v.Typing = false;
                _panel.CancelPrompt(player);
                Draw(player, v);
                return;
            }

            int slot = player.Slot;
            bool armed = _panel.TryPromptText(player, new TextPrompt
            {
                // Not drawn: the library echoes the answer here, and mm_q is written by Draw
                Variable = "mm_typed",
                Hint = _localizer.LocalizeWithPrefix("menu.search-chat"),
                MaxLength = QueryLength,
                TimeoutSeconds = 60f,
                OnResult = result =>
                {
                    if (!_viewers.TryGetValue(slot, out var live) || live != v)
                        return;

                    live.Typing = false;
                    if (result.Submitted)
                    {
                        live.Query = CleanQuery(result.Text);
                        live.Page = 0;
                        live.Armed = null;
                        live.Status = "";
                    }

                    var again = Utilities.GetPlayerFromSlot(slot);
                    if (again is { IsValid: true })
                        Draw(again, live);
                },
            });

            v.Typing = armed;
            v.Status = armed ? "" : _localizer.Localize("menu.search-busy");
            Draw(player, v);
        }

        // ------------------------------------------------------------------ the chat fallback

        /// <summary>CounterStrikeSharp's own chat menu over the same spec: !1-!9, pages included.</summary>
        private void OpenChat(CCSPlayerController player, MapMenuSpec spec, string query)
        {
            var rows = Rows(player, spec).Where(r => Matches(r, query)).ToList();
            if (rows.Count == 0)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.invalid-map"));
                return;
            }

            var menu = new ChatMenu(query.Length > 0 && spec.Search
                ? _localizer.Localize("nominate.multiple-maps")
                : spec.Title(player))
            {
                ExitButton = spec.ChatExit,
                PostSelectAction = PostSelectAction.Close,
            };

            foreach (var row in rows)
            {
                var key = row.Key;
                var label = row.Tag.Length > 0 ? $"{row.Key} - {row.Tag}" : row.Key;

                menu.AddMenuOption(label, (picker, _) =>
                {
                    var fresh = Rows(picker, spec).FirstOrDefault(r => r.Key == key);
                    if (fresh == null)
                        return;

                    if (!fresh.Enabled)
                    {
                        Say(picker, fresh.Reason);
                        return;
                    }

                    MapMenuResult result;
                    try
                    {
                        result = spec.Pick(picker, fresh);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RTV.Menu] The {Kind} pick failed. map={Map}", spec.Kind, key);
                        return;
                    }

                    if (!result.Close)
                        Say(picker, result.Status);
                }, !row.Enabled);
            }

            MenuManager.OpenChatMenu(player, menu);
        }

        private void Say(CCSPlayerController player, string? text)
        {
            if (!string.IsNullOrEmpty(text) && player.IsValid)
                player.PrintToChat($"{_localizer.Localize("rtv.prefix")} {text}");
        }

        // ------------------------------------------------------------------ writes, only when changed

        private void Var(CCSPlayerController player, Viewer v, string name, string value)
        {
            if (v.Vars.TryGetValue(name, out var old) && old == value)
                return;

            _panel!.SetVariableFor(player, name, value);
            v.Vars[name] = value;
        }

        /// <summary>A class as authored is off: every class this layout's server side toggles starts without it.</summary>
        private void Cls(CCSPlayerController player, Viewer v, string panel, string cls, bool on)
        {
            var key = (panel, cls);
            if ((v.Classes.TryGetValue(key, out var old) && old) == on)
                return;

            if (_panel!.TrySetClassFor(player, panel, cls, on))
                v.Classes[key] = on;
        }

        /// <summary>One of <paramref name="options"/> on (or none), the rest off. Off first, so two are never on at once.</summary>
        private void OneOf(CCSPlayerController player, Viewer v, string panel, string? chosen, string[] options)
        {
            foreach (var option in options)
                if (option != chosen)
                    Cls(player, v, panel, option, false);

            if (chosen != null)
                Cls(player, v, panel, chosen, true);
        }
    }
}
