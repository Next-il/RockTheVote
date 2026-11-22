using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using IVotesApi = VotesApi.IVotesApi;
using YesNoVote = VotesApi.YesNoVote;
using VoteEndReason = VotesApi.VoteEndReason;

namespace cs2_rockthevote
{
    public partial class Plugin
    {
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnRTV(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null)
                return;
            
            _rtvManager.CommandHandler(player);
        }

        /// <summary>
        /// Casts a vote for option N on the HUD vote card.
        ///
        /// <para>Registered as css_1..css_N rather than read out of chat: CounterStrikeSharp already
        /// routes <c>!1</c> and <c>/1</c> to the matching css_ command, so this gets both prefixes
        /// with no say hook, and nothing swallows ordinary chat.</para>
        /// </summary>
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
        public void OnVoteKeyCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player is not { IsValid: true }) return;

            var hud = _endMapVoteManager?.Hud;
            if (hud is null || !hud.IsOpen) return;

            // "css_3" -> 3. The command name is the only place the number lives, so there is nothing
            // to parse out of the player's message.
            var name = command.GetArg(0);
            if (!int.TryParse(name.AsSpan(name.LastIndexOf('_') + 1), out var key)) return;

            var map = hud.MapForKey(key);
            if (map is null) return;

            _endMapVoteManager.MapVoted(player, map, hud.IsRtv, allowRevote: true);
        }

        /// <summary>Starts the map vote now, ignoring the vote threshold. Admin only.</summary>
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        [RequiresPermissions("@css/changemap")]
        public void OnForceRtvCommand(CCSPlayerController? player, CommandInfo command)
        {
            _rtvManager.ForceVote(player, command);
        }
    }

    public class RockTheVoteCommand : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<RockTheVoteCommand> _logger;
        private ILogger _debugLogger = NullLogger.Instance;
        private readonly StringLocalizer _localizer;
        private readonly GameRules _gameRules;
        private readonly EndMapVoteManager _endmapVoteManager;
        private readonly PluginState _pluginState;
        private readonly AFKManager _afk;
        private RtvConfig _config = new();
        private GeneralConfig _generalConfig = new();
        private AsyncVoteManager? _voteManager;
        private bool _isCooldownActive = false;
        private string _initiatingPlayerName = "";
        private DateTime _cooldownEndTime;
        private DateTime _rtvEndTime;
        private Plugin? _plugin;
        private Timer? _cooldownTimer;
        private Timer? _rtvTimer;
        private Timer? _reminderTimer;

        public int TimeLeft => (int)Math.Max(0, (_rtvEndTime - DateTime.UtcNow).TotalSeconds);


        public RockTheVoteCommand(GameRules gameRules, EndMapVoteManager endmapVoteManager, StringLocalizer localizer, PluginState pluginState, AFKManager afkManager, ILogger<RockTheVoteCommand> logger)
        {
            _localizer = localizer;
            _gameRules = gameRules;
            _endmapVoteManager = endmapVoteManager;
            _pluginState = pluginState;
            _afk = afkManager;
            _logger = logger;
        }

        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void Unload(Plugin plugin)
        {
            StopRtvTimer();
            StopReminderTimer();
            KillTimer();
        }

        public void OnConfigParsed(Config config)
        {
            _config = config.Rtv;
            _generalConfig = config.General;
            _debugLogger = DebugLog.For(_logger, config);

            if (_config.EnablePanorama && _config.AlwaysActive)
            {
                _logger.LogWarning("[RTV.RtvCommand] Rtv.EnablePanorama and Rtv.AlwaysActive are both enabled in your config but they are incompatible; forcing AlwaysActive=false so the panorama vote is used.");
                _config.AlwaysActive = false;
            }

            _voteManager = new AsyncVoteManager(_config.VotePercentage);

            if (!uint.TryParse(_config.SoundPath, out _) && !SoundEventHelper.IsFullVolume(_config.SoundVolume))
            {
                _logger.LogWarning("[RTV.RtvCommand] To modify the sound volume (any value aside from 1) you need to use the soundevent_hash rather than the sound path. E.g. 1974266470 for felix_broken_fang_pick_1_map_tk01");
            }
        }

        public void OnMapStart(string map)
        {
            _voteManager?.OnMapStart(map);
            StopRtvTimer();
            KillTimer();
            StopReminderTimer();
        }

        private bool TreatVotedPlayersAsActive() =>
            _config.AlwaysActive && !_generalConfig.IncludeAFK;

        private IEnumerable<CCSPlayerController> EligiblePlayers(bool countVotedIfAfk = false)
        {
            var players = ServerManager.ValidPlayers().Where(p => p.ReallyValid());

            // Exclude spectators if configured
            if (!_generalConfig.IncludeSpectator)
                players = players.Where(p => p.Team != CsTeam.Spectator);

            // Exclude AFK if configured, but allow already-voted players to stay eligible when requested
            if (!_generalConfig.IncludeAFK)
            {
                HashSet<int>? voters = null;
                if (countVotedIfAfk && _voteManager != null)
                    voters = new HashSet<int>(_voteManager.Voters);

                players = players.Where(p =>
                {
                    if (voters != null && voters.Contains(p.Slot))
                        return true;

                    return !_afk.IsAfk(p);
                });
            }

            return players;
        }

        private int EligibleCount(bool countVotedIfAfk = false)
        {
            return EligiblePlayers(countVotedIfAfk).Count();
        }

        private static string Tag(CCSPlayerController p)
            => $"{p.PlayerName} [slot {p.Slot}]";

        /// <summary>
        /// The yes/no rtv vote runs through Next-il/Votes. With Votes missing the plugin falls back to the chat vote
        /// (the AlwaysActive-style counter), so !rtv never stops working.
        /// </summary>
        private bool UsePanorama
        {
            get
            {
                if (!_config.EnablePanorama || _config.AlwaysActive) return false;
                if (VotesBridge.Api != null) return true;

                if (!_warnedNoVotes)
                {
                    _warnedNoVotes = true;
                    _logger.LogError("[RTV.RtvCommand] Rtv.EnablePanorama is on but Next-il/Votes is not loaded (plugins/Votes + shared/VotesApi); using the chat vote instead.");
                }
                return false;
            }
        }
        private bool _warnedNoVotes;

        /// <summary>A card text from the lang file, or the English default when the server's lang file is older.</summary>
        private string Card(string key, string fallback)
        {
            string value = _localizer.Localize(key);
            return value == key ? fallback : value;
        }

        private int RequiredYesVotes(int eligiblePlayers)
        {
            return (int)Math.Ceiling(eligiblePlayers * (_config.VotePercentage / 100.0));
        }

        private void StartRtvTimer()
        {
            _pluginState.RtvVoteHappening = true;
            _rtvEndTime = DateTime.UtcNow.AddSeconds(_config.RtvVoteDuration);

            if (_config.EnableCountdown && ConfigValue.Is(_config.CountdownType, "chat"))
            {
                _plugin!.AddTimer(0.1f, () => ChatCountdown(_config.RtvVoteDuration),
                    TimerFlags.STOP_ON_MAPCHANGE);
            }

            _rtvTimer = _plugin!.AddTimer(_config.RtvVoteDuration, () =>
            {
                _pluginState.RtvVoteHappening = false;

                if (!_voteManager!.VotesAlreadyReached)
                {
                    Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.time-up"));
                    ActivateCooldown();
                }

            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        private void StopRtvTimer()
        {
            _rtvTimer?.Kill();
            _rtvTimer = null;
            _pluginState.RtvVoteHappening = false;
        }

        public void CommandHandler(CCSPlayerController? player)
        {
            try
            {
                bool alwaysActive = _config.AlwaysActive;
                bool usePanorama = UsePanorama;

                if (player == null)
                    return;

                _initiatingPlayerName = player.PlayerName;
                bool otherVoteInProgress = _pluginState.MapChangeScheduled || _pluginState.EofVoteHappening || _pluginState.ExtendTimeVoteHappening;

                double elapsed = Server.CurrentTime - _gameRules.GameStartTime;
                if (elapsed < _config.MapStartDelay)
                {
                    int secondsLeft = (int)Math.Ceiling(_config.MapStartDelay - elapsed);
                    player.PrintToChat(_localizer.LocalizeWithPrefix("rtv.cooldown", secondsLeft));
                    return;
                }
                if (_isCooldownActive)
                {
                    double secondsLeft = Math.Max(0, (_cooldownEndTime - DateTime.UtcNow).TotalSeconds);
                    int secondsInt = (int)Math.Ceiling(secondsLeft);

                    player.PrintToChat(_localizer.LocalizeWithPrefix("rtv.cooldown", secondsInt));
                    return;
                }
                if (!_config.Enabled || (alwaysActive
                        ? (_pluginState.MapChangeScheduled || _pluginState.EofVoteHappening)
                        : otherVoteInProgress))
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("rtv.disabled"));
                    return;
                }
                if (_gameRules.WarmupRunning && !_config.EnabledInWarmup)
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.warmup"));
                    return;
                }
                if (_config.MinRounds > 0 && _config.MinRounds > _gameRules.TotalRoundsPlayed)
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.minimum-rounds", _config.MinRounds));
                    return;
                }
                if (ServerManager.ValidPlayerCount() < _config.MinPlayers)
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.minimum-players", _config.MinPlayers));
                    return;
                }
                
                _debugLogger.LogInformation("[RTV.RtvCommand] RTV starting (caller: {Caller}), IncludeAFK={IncludeAFK}", Tag(player), _generalConfig.IncludeAFK);
                if (_generalConfig.DebugLogging)
                    Server.PrintToConsole($"[RTV.RtvCommand] RTV starting (caller: {Tag(player)}), IncludeAFK={_generalConfig.IncludeAFK}");

                if (usePanorama)
                {
                    if (VotesBridge.IsVoteActive)
                    {
                        player.PrintToChat(_localizer.LocalizeWithPrefix("general.vote-in-progress"));
                        return;
                    }

                    // Who may vote: everyone (Votes' default, every connected human), or the eligible pool when AFK
                    // players or spectators are left out.
                    List<CCSPlayerController>? voters = null;
                    if (!_generalConfig.IncludeAFK || !_generalConfig.IncludeSpectator)
                    {
                        _afk.CheckAllPlayers();

                        voters = EligiblePlayers().ToList(); // this should already exclude AFK
                        if (_generalConfig.DebugLogging)
                        {
                            string roster = string.Join(", ", voters.Select(Tag));
                            _debugLogger.LogInformation("[RTV.RtvCommand] RTV vote started. Eligible (non-AFK) players = {Count}: {Players}",
                                voters.Count, roster);
                            Server.PrintToConsole($"[RTV.RtvCommand] RTV vote started. Eligible (non-AFK) players = {voters.Count}: {roster}");
                        }

                        // Bail for edge case lol
                        if (voters.Count == 0)
                        {
                            player.PrintToChat(_localizer.LocalizeWithPrefix("general.eligible"));
                            return;
                        }
                    }

                    int pool = voters?.Count ?? Utilities.GetPlayers().Count(p => p is { IsValid: true, IsBot: false, IsHLTV: false });

                    bool started = VotesBridge.Api!.StartYesNo(new YesNoVote
                    {
                        Owner = "RockTheVote",
                        Caller = player,
                        Eyebrow = Card("rtv.card.eyebrow", "Vote · Rock the vote"),
                        Prefix = Card("rtv.card.prefix", ""),
                        Subject = Card("rtv.card.subject", "Change the map"),
                        DurationSeconds = _config.RtvVoteDuration,
                        PassPercent = _config.VotePercentage,
                        Voters = voters,
                        // The rule as before: yes reaches VotePercentage of the pool; no votes do not count against it.
                        RequiredYes = Math.Max(1, RequiredYesVotes(pool)),
                        Decide = tally => tally.Voters > 0 && tally.Yes >= Math.Max(1, RequiredYesVotes(tally.Voters)),
                        PassedText = Card("rtv.card.passed", "map vote starts now"),
                        OnEnd = OnRtvVoteEnded,
                    });

                    if (!started)
                    {
                        player.PrintToChat(_localizer.LocalizeWithPrefix("general.vote-in-progress"));
                        return;
                    }

                    _rtvEndTime = DateTime.UtcNow.AddSeconds(_config.RtvVoteDuration);
                    _pluginState.RtvVoteHappening = true;
                    Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("rtv.rocked-the-vote", _initiatingPlayerName)}");
                }

                if (!usePanorama)
                {
                    bool countVotedAsActive = TreatVotedPlayersAsActive();

                    if (!_generalConfig.IncludeAFK)
                    {
                        _afk.CheckAllPlayers();
                        _afk.MarkPlayerActive(player);
                    }
                    
                    // Block spectators from voting when excluded
                    if (!_generalConfig.IncludeSpectator && player.Team == CsTeam.Spectator)
                    {
                        // "Spectators are excluded from this vote."
                        player.PrintToChat($"{_localizer.LocalizeWithPrefix("general.spectator")}");
                        return;
                    }

                    // Calculate AFK-aware required votes using active + already-voted players
                    int eligible = Math.Max(EligibleCount(countVotedAsActive), 0);
                    int requiredYesVotes = RequiredYesVotes(eligible);

                    // Add the vote first with the current eligible pool
                    VoteResult result = _voteManager!.AddVote(player.Slot, eligible);

                    switch (result.Result)
                    {
                        case VoteResultEnum.Added:
                            if (!alwaysActive && _rtvTimer == null)
                                StartRtvTimer();
                            else if (alwaysActive)
                                StartReminderTimer();
                            Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("rtv.rocked-the-vote", player.PlayerName)} {_localizer.Localize("general.votes-needed", result.VoteCount, requiredYesVotes)}");
                            Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("rtv.instructions")}");
                            // Early pass if threshold met
                            if (result.VoteCount >= requiredYesVotes)
                            {
                                if (!alwaysActive)
                                    StopRtvTimer();
                                StopReminderTimer();
                                _endmapVoteManager.StartVote(isRtv: true);
                                Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.votes-reached"));
                            }
                            break;
                        

                        case VoteResultEnum.AlreadyAddedBefore:
                            player.PrintToChat($"{_localizer.LocalizeWithPrefix("rtv.already-rocked-the-vote")} {_localizer.Localize("general.votes-needed", result.VoteCount, requiredYesVotes)}");
                            // Early pass if threshold met
                            if (result.VoteCount >= requiredYesVotes)
                            {
                                if (!alwaysActive)
                                    StopRtvTimer();
                                StopReminderTimer();
                                _endmapVoteManager.StartVote(isRtv: true);
                                Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.votes-reached"));
                            }
                            break;

                        case VoteResultEnum.VotesAlreadyReached:
                            if (!alwaysActive)
                                StopRtvTimer();
                            StopReminderTimer();
                            player.PrintToChat(_localizer.LocalizeWithPrefix("rtv.disabled"));
                            break;

                        case VoteResultEnum.VotesReached:
                            if (!alwaysActive)
                                StopRtvTimer();
                            StopReminderTimer();
                            _endmapVoteManager.StartVote(isRtv: true);
                            Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("rtv.rocked-the-vote", player.PlayerName)} {_localizer.Localize("general.votes-needed", result.VoteCount, requiredYesVotes)}");
                            Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.votes-reached"));
                            break;
                    }
                }

                if (_config.SoundEnabled)
                {
                    SoundEventHelper.PlaySound(player, _config.SoundPath, _config.SoundVolume);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RTV.RtvCommand] Something went wrong with the rtv command: {Message}", ex.Message);
            }
        }

        /// <summary>The rtv yes/no vote ended (Votes). A pass starts the map vote; anything else is a fail with a cooldown.</summary>
        private void OnRtvVoteEnded(VotesApi.VoteResult result)
        {
            _pluginState.RtvVoteHappening = false;

            if (result.Passed)
            {
                Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("rtv.votes-reached")}");
                _endmapVoteManager.StartVote(isRtv: true);
                return;
            }

            // Votes cancels a running vote on map change; that is not a failed rtv.
            if (result.Reason == VoteEndReason.Cancelled && result.CancelReason == "map change")
                return;

            Server.PrintToChatAll(result.Reason == VoteEndReason.TimeUp
                ? $"{_localizer.LocalizeWithPrefix("rtv.time-up")}"
                : $"{_localizer.LocalizeWithPrefix("rtv.failed")}");
            ActivateCooldown();
        }

        public void ChatCountdown(int secondsLeft)
        {
            if (!_pluginState.RtvVoteHappening)
                return;

            string text = _localizer.LocalizeWithPrefix("general.chat-countdown", secondsLeft);
            foreach (var player in ServerManager.ValidPlayers())
                player.PrintToChat(text);

            int nextSecondsLeft = secondsLeft - _config.ChatCountdownInterval;
            if (nextSecondsLeft <= 0)
                return;

            _plugin?.AddTimer(
                _config.ChatCountdownInterval, () =>
                {
                    try
                    {
                        ChatCountdown(nextSecondsLeft);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RTV.RtvCommand] ChatCountdown timer callback failed: {Message}", ex.Message);
                    }
                }, TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        private void StartReminderTimer()
        {
            if (_reminderTimer != null || !_config.AlwaysActive || !_config.AlwaysActiveReminder)
                return;

            if (_pluginState.MapChangeScheduled || _pluginState.EofVoteHappening)
                return;

            if (_config.ReminderInterval <= 0)
                return;

            _reminderTimer = _plugin?.AddTimer(
                _config.ReminderInterval,() =>
                {
                    try
                    {
                        SendReminder();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RTV.RtvCommand] RTV reminder failed: {Message}", ex.Message);
                    }
                },
                TimerFlags.STOP_ON_MAPCHANGE | TimerFlags.REPEAT
            );
        }

        // Re-check the RTV pass threshold outside !rtv (e.g. after disconnects, shrink pool), non-panorama only
        private void TryPassByThreshold()
        {
            if (_voteManager == null || !_config.Enabled)
                return;

            bool usePanorama = UsePanorama;
            if (usePanorama)
                return;

            if (_voteManager.VotesAlreadyReached || _voteManager.VoteCount == 0)
                return;

            if (_pluginState.MapChangeScheduled || _pluginState.EofVoteHappening)
                return;

            bool countVotedAsActive = TreatVotedPlayersAsActive();
            int eligible = Math.Max(EligibleCount(countVotedAsActive), 0);
            int requiredYesVotes = RequiredYesVotes(eligible);

            if (_voteManager.VoteCount < requiredYesVotes)
                return;

            if (!_config.AlwaysActive)
                StopRtvTimer();
            StopReminderTimer();
            _endmapVoteManager.StartVote(isRtv: true);
            Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.votes-reached"));
        }

        /// <summary>
        /// Starts the map vote immediately, skipping the vote threshold entirely. This is the
        /// admin override for "everyone wants a new map but we are two votes short".
        ///
        /// <para>Refusals are reported rather than silent. StartVote already returns quietly when a
        /// vote is running, which from a command looks identical to the command not existing.</para>
        /// </summary>
        public void ForceVote(CCSPlayerController? player, CommandInfo command)
        {
            var who = player?.PlayerName ?? "Console";

            if (_pluginState.MapChangeScheduled)
            {
                command.ReplyToCommand(_localizer.LocalizeWithPrefix("rtv.force-map-scheduled"));
                return;
            }

            if (_pluginState.EofVoteHappening)
            {
                command.ReplyToCommand(_localizer.LocalizeWithPrefix("rtv.force-already-voting"));
                return;
            }

            // Same teardown the threshold path does, so a forced vote does not leave the countdown
            // and reminder running underneath it.
            if (!_config.AlwaysActive)
                StopRtvTimer();
            StopReminderTimer();

            _endmapVoteManager.StartVote(isRtv: true);

            Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.forced", who));
            _logger.LogInformation("[RTV] {Who} forced a map vote.", who);
        }

        private void SendReminder()
        {
            if (_voteManager == null || !_config.AlwaysActive || !_config.AlwaysActiveReminder)
            {
                StopReminderTimer();
                return;
            }

            if (_pluginState.MapChangeScheduled || _pluginState.EofVoteHappening)
            {
                StopReminderTimer();
                return;
            }

            if (_voteManager.VotesAlreadyReached)
            {
                StopReminderTimer();
                return;
            }

            bool countVotedAsActive = TreatVotedPlayersAsActive();
            int eligible = Math.Max(EligibleCount(countVotedAsActive), 0);
            int requiredYesVotes = RequiredYesVotes(eligible);
            int remaining = Math.Max(requiredYesVotes - _voteManager.VoteCount, 0);

            if (remaining <= 0)
            {
                TryPassByThreshold();
                StopReminderTimer();
                return;
            }

            Server.PrintToChatAll(_localizer.LocalizeWithPrefix("rtv.in-progress", remaining));
        }

        private void StopReminderTimer()
        {
            _reminderTimer?.Kill();
            _reminderTimer = null;
        }

        private void ActivateCooldown()
        {
            _isCooldownActive = true;

            _cooldownTimer = _plugin?.AddTimer(_config.CooldownDuration, () =>
            {
                _isCooldownActive = false;
            }, TimerFlags.STOP_ON_MAPCHANGE);

            _cooldownEndTime = DateTime.UtcNow.AddSeconds(_config.CooldownDuration);
        }

        public void KillTimer()
        {
            _isCooldownActive = false;
            _cooldownEndTime = DateTime.MinValue;
            _cooldownTimer?.Kill();
            _cooldownTimer = null;
        }

        public void PlayerDisconnected(CCSPlayerController? player)
        {
            if (player == null)
                return;

            bool usePanorama = UsePanorama;

            if (!usePanorama)
            {
                _voteManager?.RemoveVote(player.Slot);

                Server.NextFrame(() =>
                {
                    if (_pluginState.Unloaded)
                        return;

                    try
                    {
                        TryPassByThreshold();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[RTV.RtvCommand] Post-disconnect threshold re-check failed: {Message}", ex.Message);
                    }
                });
            }
            // On the Votes path a leaver is dropped by Votes itself.
        }
    }
}
