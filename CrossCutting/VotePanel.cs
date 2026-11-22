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
    /// <para><b>Three modes on one card.</b> The vote itself; the result for a few seconds after it
    /// (the winner row only, with when the map changes); and a next-map notice when the change
    /// happens. Each viewer's own state - their pick gets a check, and once they have voted the
    /// card folds to the leader and their pick - lives in per-viewer classes.</para>
    ///
    /// <para><b>Everything is redrawn from state.</b> PanoramaManager rebuilds a player's panel by
    /// itself after a respawn, a round restart or a map change and drops every per-viewer class on
    /// the way (PanelAction.Restored), so the card keeps what it shows here and writes it back.</para>
    ///
    /// <para>The bar width is a class rather than a value, in 5% steps, because a CustomHud layout
    /// only lets the server toggle classes - it cannot write a width.</para>
    /// </summary>
    public sealed class VotePanel : IDisposable
    {
        private const string Layout = "panorama/layout/custom_game/vote_hud.vxml_c";
        private const string Root = "RtvVoteRoot";

        /// <summary>Matches the row pool in vote_hud.xml.</summary>
        public const int Slots = 8;

        private const int BarSteps = 20;   // w0..w20, i.e. 5% per step
        private const string DefaultBar = "w0";   // what the layout starts every bar with

        /// <summary>The live card, for the code that announces a map change without owning the vote.
        /// Null when the HUD is off or failed to start.</summary>
        public static VotePanel? Current { get; private set; }

        private enum Mode { Closed, Vote, Result, Notice }

        private readonly PanelHandle _panel;

        /// <summary>What each row currently shows, so a vote can be resolved back to a map.</summary>
        private readonly List<string> _options = [];

        /// <summary>The bar class each viewer's row wears now, keyed by (slot, row), so the old one
        /// comes off before the new one goes on - a panel that wears two paints whichever the
        /// stylesheet lists last.</summary>
        private readonly Dictionary<(int Slot, int Row), string> _barClass = new();

        private Mode _mode = Mode.Closed;
        private IReadOnlyDictionary<string, int> _votes = new Dictionary<string, int>();
        private IReadOnlyDictionary<int, string> _picks = new Dictionary<int, string>();
        private int _timeLeft;
        private string _winner = string.Empty;
        private int _winnerPercent;
        private string _sub = string.Empty;

        /// <summary>Bumped by every show, so a delayed hide only closes the card it was armed for.</summary>
        private int _token;

        /// <summary>Which kind of vote is showing. MapVoted branches on it for its messaging,
        /// so passing the wrong one makes an end-of-map vote report itself as an RTV.</summary>
        public bool IsRtv { get; private set; }

        /// <summary>Once a viewer has voted, fold their card to the leader and their own pick.</summary>
        public bool ShrinkAfterVote { get; set; } = true;

        /// <summary>A vote is on the board (not the result or a notice).</summary>
        public bool IsOpen => _mode == Mode.Vote;

        public VotePanel()
        {
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                RootPanelId  = Root,
                RevealClass  = "show",

                // The two that matter: it must not take the mouse, and it must not touch the HUD.
                CaptureInput = false,
                HideHud      = HideHudFlags.None,

                RowCount     = 0,   // no rowN pool; the rows here are addressed directly

                // Everyone watching a vote sees the same board, so one shared set of strings is
                // correct for the title. Everything that differs per viewer is a class or a
                // per-player variable.
                SharedText   = true,
            });
            _panel.OnEvent += OnPanelEvent;
            Current = this;
        }

        public void Dispose()
        {
            if (Current == this) Current = null;
            _panel.Dispose();
        }

        /// <summary>Which map a 1-based key stands for, or null if that key is not on the board.</summary>
        public string? MapForKey(int key) =>
            _mode == Mode.Vote && key >= 1 && key <= _options.Count ? _options[key - 1] : null;

        private static IEnumerable<CCSPlayerController> Humans() =>
            Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false });

        private void OnPanelEvent(PanelEvent e)
        {
            if (e.Action != PanelAction.Restored || !e.Player.IsValid || _mode == Mode.Closed)
                return;

            // The rebuilt panel starts from the layout again: every bar back on w0.
            Forget(e.Player.Slot);
            Draw(e.Player);
        }

        /// <summary>Drops what a slot's panel was wearing. Call it when the player leaves: whoever
        /// takes the slot next gets a fresh panel.</summary>
        public void Forget(int slot)
        {
            foreach (var key in _barClass.Keys.Where(k => k.Slot == slot).ToList())
                _barClass.Remove(key);
        }

        // ------------------------------------------------------------------ the vote

        /// <summary>Shows the vote to everyone with the options in vote order.</summary>
        public void Show(IEnumerable<string> options, bool isRtv, IReadOnlyDictionary<string, int> votes, IReadOnlyDictionary<int, string> picks)
        {
            _token++;
            _mode = Mode.Vote;
            _options.Clear();
            _options.AddRange(options.Take(Slots));
            _votes = votes;
            _picks = picks;
            _timeLeft = 0;
            IsRtv = isRtv;

            _panel.Title    = isRtv ? "Rock the Vote" : "Next Map";
            _panel.Subtitle = "LIVE";

            foreach (var player in Humans())
            {
                _panel.Open(player);
                Draw(player);
            }
        }

        /// <summary>Redraws the counts. Cheap enough to call on every vote.</summary>
        public void Update(IReadOnlyDictionary<string, int> votes, IReadOnlyDictionary<int, string> picks)
        {
            if (_mode != Mode.Vote) return;

            _votes = votes;
            _picks = picks;

            foreach (var player in Humans())
                if (_panel.IsOpenFor(player))
                    Draw(player);
        }

        /// <summary>
        /// Writes the remaining seconds into the top line. Called from the vote's own one-second
        /// tick rather than a timer of its own, so the number on screen is the same one the vote
        /// is actually counting down.
        /// </summary>
        public void SetTimeLeft(int seconds)
        {
            if (_mode != Mode.Vote) return;

            _timeLeft = seconds;
            var text = TimeText();

            foreach (var player in Humans())
                if (_panel.IsOpenFor(player))
                    _panel.SetVariableFor(player, "vote_time", text);
        }

        /// <summary>Opens the vote for someone who connected mid-vote, so they are not the only
        /// player who cannot see what is being voted on.</summary>
        public void ShowTo(CCSPlayerController player)
        {
            if (_mode != Mode.Vote || player is not { IsValid: true, IsBot: false }) return;

            _panel.Open(player);
            Draw(player);
        }

        // ------------------------------------------------------------------ after the vote

        /// <summary>
        /// Turns the board into the result: the winner row only, green, with <paramref name="sub"/>
        /// (when the map changes) as its last line. Returns the token for <see cref="HideIf"/>.
        /// </summary>
        public int ShowResult(string winner, int percent, string sub)
        {
            _token++;
            _mode = Mode.Result;
            _winner = winner;
            _winnerPercent = Math.Clamp(percent, 0, 100);
            _sub = sub;
            _panel.Title = "Next map";

            foreach (var player in Humans())
                if (_panel.IsOpenFor(player))
                    Draw(player);

            return _token;
        }

        /// <summary>
        /// The next-map notice to everyone: one row with the map, <paramref name="sub"/> under it.
        /// Returns the token for <see cref="HideIf"/>.
        /// </summary>
        public int ShowNotice(string map, string sub)
        {
            _token++;
            _mode = Mode.Notice;
            _options.Clear();
            _options.Add(map);
            _sub = sub;
            _panel.Title = "Next map";

            foreach (var player in Humans())
            {
                _panel.Open(player);
                Draw(player);
            }

            return _token;
        }

        /// <summary>Hides the card if nothing newer was shown since <paramref name="token"/>.</summary>
        public void HideIf(int token)
        {
            if (token == _token) Hide();
        }

        /// <summary>Hides the panel for everyone. Safe to call when it was never shown.</summary>
        public void Hide()
        {
            _mode = Mode.Closed;
            _options.Clear();

            foreach (var player in Utilities.GetPlayers().Where(p => p is { IsValid: true }))
                _panel.Close(player);
        }

        // ------------------------------------------------------------------ drawing

        private string TimeText() => _timeLeft > 0 ? $"{_timeLeft}s" : string.Empty;

        private void Draw(CCSPlayerController player)
        {
            switch (_mode)
            {
                case Mode.Vote:   DrawVote(player); break;
                case Mode.Result: DrawResult(player); break;
                case Mode.Notice: DrawNotice(player); break;
            }
        }

        private void DrawVote(CCSPlayerController player)
        {
            int sum   = _options.Sum(m => _votes.GetValueOrDefault(m));
            int total = Math.Max(1, sum);

            // A board where nobody has voted yet has no leader worth highlighting.
            string? leading = sum == 0 ? null : _options.OrderByDescending(m => _votes.GetValueOrDefault(m)).First();
            string? mine    = _picks.TryGetValue(player.Slot, out var pick) ? pick : null;
            bool fold       = ShrinkAfterVote && mine != null;

            SetRoot(player, voted: mine != null, done: false, notice: false);

            for (var i = 0; i < Slots; i++)
            {
                var show = i < _options.Count;
                _panel.SetClassFor(player, $"opt{i}", "hidden", !show);
                if (!show) continue;

                var map = _options[i];
                var pct = _votes.GetValueOrDefault(map) * 100 / total;

                _panel.SetVariableFor(player, $"opt{i}_key",  $"{i + 1}.");
                _panel.SetVariableFor(player, $"opt{i}_name", map);
                _panel.SetVariableFor(player, $"opt{i}_pct",  $"{pct}%");

                _panel.SetClassFor(player, $"opt{i}", "leading", map == leading);
                _panel.SetClassFor(player, $"opt{i}", "mine", map == mine);
                _panel.SetClassFor(player, $"opt{i}", "fold", fold && map != leading && map != mine);
                SetBar(player, i, pct);
            }

            _panel.SetVariableFor(player, "vote_hint", _options.Count > 1 ? $"!1-!{_options.Count} in chat" : "!1 in chat");
            _panel.SetVariableFor(player, "vote_time", TimeText());
        }

        private void DrawResult(CCSPlayerController player)
        {
            string? mine = _picks.TryGetValue(player.Slot, out var pick) ? pick : null;

            SetRoot(player, voted: false, done: true, notice: false);

            for (var i = 0; i < Slots; i++)
            {
                var show = i < _options.Count;
                _panel.SetClassFor(player, $"opt{i}", "hidden", !show);
                if (!show) continue;

                var map    = _options[i];
                var winner = map == _winner;

                _panel.SetVariableFor(player, $"opt{i}_key",  $"{i + 1}.");
                _panel.SetVariableFor(player, $"opt{i}_name", map);
                _panel.SetVariableFor(player, $"opt{i}_pct",  winner ? $"{_winnerPercent}%" : string.Empty);

                _panel.SetClassFor(player, $"opt{i}", "leading", winner);
                _panel.SetClassFor(player, $"opt{i}", "mine", map == mine);
                _panel.SetClassFor(player, $"opt{i}", "fold", !winner);
                if (winner) SetBar(player, i, _winnerPercent);
            }

            _panel.SetVariableFor(player, "vote_hint", _sub);
            _panel.SetVariableFor(player, "vote_time", string.Empty);
        }

        private void DrawNotice(CCSPlayerController player)
        {
            SetRoot(player, voted: false, done: false, notice: true);

            for (var i = 0; i < Slots; i++)
            {
                var show = i < _options.Count;
                _panel.SetClassFor(player, $"opt{i}", "hidden", !show);
                if (!show) continue;

                _panel.SetVariableFor(player, $"opt{i}_name", _options[i]);
                _panel.SetClassFor(player, $"opt{i}", "leading", false);
                _panel.SetClassFor(player, $"opt{i}", "mine", false);
                _panel.SetClassFor(player, $"opt{i}", "fold", false);
            }

            _panel.SetVariableFor(player, "vote_hint", _sub);
            _panel.SetVariableFor(player, "vote_time", string.Empty);
        }

        private void SetRoot(CCSPlayerController player, bool voted, bool done, bool notice)
        {
            _panel.SetClassFor(player, Root, "voted", voted);
            _panel.SetClassFor(player, Root, "st-done", done);
            _panel.SetClassFor(player, Root, "notice", notice);
        }

        private void SetBar(CCSPlayerController player, int row, int pct)
        {
            var step = $"w{Math.Clamp(pct * BarSteps / 100, 0, BarSteps)}";
            var key  = (player.Slot, row);
            var previous = _barClass.TryGetValue(key, out var p) ? p : DefaultBar;
            if (previous == step) return;

            _panel.SetClassFor(player, $"opt{row}_bar", previous, false);
            _panel.SetClassFor(player, $"opt{row}_bar", step, true);
            _barClass[key] = step;
        }
    }
}
