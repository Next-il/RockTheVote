using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using PanoramaManager;

namespace cs2_rockthevote
{
    /// <summary>
    /// The live map vote as a Panorama card, for both the end-of-map vote and RTV - they share one
    /// entry point in <see cref="EndMapVoteManager.StartVote"/>, so they share this too.
    ///
    /// <para><b>Deliberately not interactive.</b> The contract takes no mouse and hides nothing, and
    /// every element in the layout is hittest false, so players keep playing while the vote runs.
    /// That is the whole reason this exists: the WasdMenu it replaces captures movement keys.
    /// Voting happens through <c>css_1</c>..<c>css_N</c>, which CounterStrikeSharp also reaches from
    /// chat as <c>!1</c> and <c>/1</c>.</para>
    ///
    /// <para>The bar width is a class rather than a value, in 5% steps, because a CustomHud layout
    /// only lets the server toggle classes - it cannot write a width.</para>
    /// </summary>
    public sealed class VotePanel : IDisposable
    {
        private const string Layout = "panorama/layout/custom_game/vote_hud.vxml_c";

        /// <summary>Matches the row pool in vote_hud.xml.</summary>
        public const int Slots = 8;

        private const int BarSteps = 20;   // w0..w20, i.e. 5% per step

        private readonly PanelHandle _panel;

        /// <summary>What each row currently shows, so a vote can be resolved back to a map and the
        /// previous bar class removed before the next is applied.</summary>
        private readonly List<string> _options = [];
        private readonly Dictionary<int, string> _barClass = new();

        private bool _open;

        /// <summary>Which kind of vote is showing. MapVoted branches on it for its messaging,
        /// so passing the wrong one makes an end-of-map vote report itself as an RTV.</summary>
        public bool IsRtv { get; private set; }

        public VotePanel()
        {
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                RootPanelId  = "PanoramaRoot",
                RevealClass  = "show",

                // The two that matter: it must not take the mouse, and it must not touch the HUD.
                CaptureInput = false,
                HideHud      = HideHudFlags.None,

                RowCount     = 1,   // the row pool here is driven directly
            });
        }

        public void Dispose() => _panel.Dispose();

        /// <summary>Which map a 1-based key stands for, or null if that key is not on the board.</summary>
        public string? MapForKey(int key) =>
            key >= 1 && key <= _options.Count ? _options[key - 1] : null;

        public bool IsOpen => _open;

        /// <summary>Shows the panel to everyone with the options in vote order.</summary>
        public void Show(IEnumerable<string> options, bool isRtv, IReadOnlyDictionary<string, int> votes)
        {
            _options.Clear();
            _options.AddRange(options.Take(Slots));
            _barClass.Clear();
            _open = true;
            IsRtv = isRtv;

            _panel.Title    = isRtv ? "Rock the Vote" : "Next Map";
            _panel.Subtitle = "LIVE";

            foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
                _panel.Open(player);

            Update(votes);
        }

        /// <summary>Redraws the counts. Cheap enough to call on every vote.</summary>
        public void Update(IReadOnlyDictionary<string, int> votes)
        {
            if (!_open) return;

            var total   = Math.Max(1, votes.Values.Sum());
            var leading = _options.Count == 0
                ? null
                : _options.OrderByDescending(m => votes.GetValueOrDefault(m)).First();

            // A board where nobody has voted yet has no leader worth highlighting.
            if (votes.Values.Sum() == 0) leading = null;

            foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
            {
                if (!_panel.IsOpenFor(player)) continue;

                for (var i = 0; i < Slots; i++)
                {
                    var show = i < _options.Count;
                    _panel.SetClassFor(player, $"opt{i}", "hidden", !show);

                    if (!show) continue;

                    var map   = _options[i];
                    var count = votes.GetValueOrDefault(map);
                    var pct   = count * 100 / total;

                    _panel.SetVariableFor(player, $"opt{i}_key",  $"{i + 1}.");
                    _panel.SetVariableFor(player, $"opt{i}_name", map);
                    _panel.SetVariableFor(player, $"opt{i}_pct",  $"{pct}%");

                    _panel.SetClassFor(player, $"opt{i}", "leading", map == leading);

                    // Only one width class may be on the bar at a time; the previous one is taken
                    // off before the next goes on, or the stylesheet paints whichever it lists last.
                    var step = $"w{Math.Clamp(pct * BarSteps / 100, 0, BarSteps)}";
                    if (_barClass.TryGetValue(i, out var previous) && previous != step)
                        _panel.SetClassFor(player, $"opt{i}_bar", previous, false);

                    _panel.SetClassFor(player, $"opt{i}_bar", step, true);
                    _barClass[i] = step;
                }

                _panel.SetVariableFor(player, "vote_hint", "Type !1 in chat to vote");
            }
        }

        /// <summary>
        /// Writes the remaining seconds into the footer. Called from the vote's own one-second
        /// tick rather than a timer of its own, so the number on screen is the same one the vote
        /// is actually counting down.
        /// </summary>
        public void SetTimeLeft(int seconds)
        {
            if (!_open) return;

            var text = seconds > 0 ? $"{seconds}s" : string.Empty;

            foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }))
            {
                if (_panel.IsOpenFor(player))
                    _panel.SetVariableFor(player, "vote_time", text);
            }
        }

        /// <summary>Hides the panel for everyone. Safe to call when it was never shown.</summary>
        public void Hide()
        {
            _open = false;
            _options.Clear();
            _barClass.Clear();

            foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true }))
                _panel.Close(player);
        }

        /// <summary>Opens the panel for someone who connected mid-vote, so they are not the only
        /// player who cannot see what is being voted on.</summary>
        public void ShowTo(CCSPlayerController player, IReadOnlyDictionary<string, int> votes)
        {
            if (!_open || player is not { IsValid: true, IsBot: false }) return;

            _panel.Open(player);
            Update(votes);
        }
    }
}
