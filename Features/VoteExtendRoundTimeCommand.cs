using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using cs2_rockthevote.Core;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using IVotesApi = VotesApi.IVotesApi;
using YesNoVote = VotesApi.YesNoVote;
using VoteEndReason = VotesApi.VoteEndReason;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace cs2_rockthevote
{
    public partial class Plugin
    {
        [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
        public void OnVoteExtendRoundTimeCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player == null || command == null)
                return;

            bool hasPerm = PermissionUtility.HasAny(player, Config.VoteExtend.Permissions);

            if (!hasPerm)
            {
                command.ReplyToCommand(_localizer.LocalizeWithPrefix("general.incorrect.permission"));
                return;
            }

            _voteExtendRoundTime.CommandHandler(player, command);
        }
    }

    public class VoteExtendRoundTimeCommand(TimeLimitManager timeLimitManager, ExtendRoundTimeManager extendRoundTimeManager, GameRules gameRules, IStringLocalizer stringLocalizer, PluginState pluginState, ILogger<VoteExtendRoundTimeCommand> logger) : IPluginDependency<Plugin, Config>
    {
        private readonly ILogger<VoteExtendRoundTimeCommand> _logger = logger;
        private TimeLimitManager _timeLimitManager = timeLimitManager;
        private ExtendRoundTimeManager _extendRoundTimeManager = extendRoundTimeManager;
        private readonly GameRules _gameRules = gameRules;
        private StringLocalizer _localizer = new(stringLocalizer, "extendtime.prefix");
        private PluginState _pluginState = pluginState;
        private VoteExtendConfig _voteExtendConfig = new();
        private GeneralConfig _generalConfig = new();
        private string _initiatingPlayerName = "";
        private DateTime _cooldownEndTime;
        private Plugin? _plugin;
        private Timer? _cooldownTimer;
        private bool _isCooldownActive = false;

        public void CommandHandler(CCSPlayerController player, CommandInfo commandInfo)
        {
            _initiatingPlayerName = player.PlayerName;

            if (!_voteExtendConfig.Enabled)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("extendtime.disbled"));
                return;
            }
            if (_generalConfig.MaxMapExtensions > 0 && _pluginState.MapExtensionCount >= _generalConfig.MaxMapExtensions)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("extendtime.max-extensions-reached", _generalConfig.MaxMapExtensions));
                return;
            }
            if (_isCooldownActive)
            {
                double secondsLeft = Math.Max(0, (_cooldownEndTime - DateTime.UtcNow).TotalSeconds);
                int secondsInt = (int)Math.Ceiling(secondsLeft);

                player.PrintToChat(_localizer.LocalizeWithPrefix("rtv.cooldown", secondsInt));
                return;
            }
            if (_pluginState.DisableCommands)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.disabled"));
                return;
            }
            if (_gameRules.WarmupRunning)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.validation.warmup"));
                return;
            }
            if (_pluginState.RtvVoteHappening || _pluginState.ExtendTimeVoteHappening || VotesBridge.IsVoteActive)
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("general.vote-in-progress"));
                return;
            }
            if (!_timeLimitManager.UnlimitedTime)
            {
                // The yes/no vote runs through Next-il/Votes; without it, the chat vote as before.
                bool usePanorama = _voteExtendConfig.EnablePanorama && VotesBridge.Api != null;
                if (_voteExtendConfig.EnablePanorama && !usePanorama)
                    _logger.LogError("[RTV.VoteExtendTime] VoteExtend.EnablePanorama is on but Next-il/Votes is not loaded (plugins/Votes + shared/VotesApi); using the chat vote instead.");

                if (!usePanorama)
                {
                    _extendRoundTimeManager.StartExtendVote(_voteExtendConfig);
                }
                else if (usePanorama)
                {
                    _pluginState.ExtendTimeVoteHappening = true;
                    int minutes = _generalConfig.RoundTimeExtension;
                    int pool = Utilities.GetPlayers().Count(p => p is { IsValid: true, IsBot: false, IsHLTV: false });

                    bool voteStarted = VotesBridge.Api!.StartYesNo(new YesNoVote
                    {
                        Owner = "RockTheVote",
                        Caller = player,
                        Eyebrow = Card("extendtime.card.eyebrow", "Vote · Extend the map"),
                        Prefix = Card("extendtime.card.prefix", "Extend the map by"),
                        Subject = Card("extendtime.card.subject", "{0} minutes", minutes),
                        DurationSeconds = _voteExtendConfig.VoteDuration,
                        PassPercent = _voteExtendConfig.VotePercentage,
                        // The rule as before: yes reaches VotePercentage of everyone; no votes do not count against it.
                        RequiredYes = Math.Max(1, RequiredYes(pool)),
                        Decide = tally => tally.Voters > 0 && tally.Yes >= Math.Max(1, RequiredYes(tally.Voters)),
                        PassedText = Card("extendtime.card.passed", "map extended by {0} min", minutes),
                        OnEnd = OnExtendVoteEnded,
                    });

                    if (!voteStarted)
                    {
                        _pluginState.ExtendTimeVoteHappening = false;
                        _logger.LogWarning("[RTV.VoteExtendTime] Extend vote failed to start (another vote is running); state reset.");
                        player.PrintToChat(_localizer.LocalizeWithPrefix("general.vote-in-progress"));
                        return;
                    }

                    Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("extendtime.vote-started", _initiatingPlayerName)}");

                    if (_voteExtendConfig.EnableCountdown && ConfigValue.Is(_voteExtendConfig.CountdownType, "chat"))
                    {
                        _extendRoundTimeManager.ChatCountdown(_voteExtendConfig.VoteDuration);
                    }
                }
                else
                {
                    player.PrintToChat(_localizer.LocalizeWithPrefix("extendtime.notapplicable"));
                }
            }
            else
            {
                player.PrintToChat(_localizer.LocalizeWithPrefix("extendtime.notapplicable"));
            }
        }

        private int RequiredYes(int pool) => (int)Math.Ceiling(pool * (_voteExtendConfig.VotePercentage / 100.0));

        /// <summary>A card text from the lang file, or the English default when the server's lang file is older.</summary>
        private string Card(string key, string fallback, params object[] args)
        {
            string value = _localizer.Localize(key, args);
            return value == key ? string.Format(fallback, args) : value;
        }

        /// <summary>The extend yes/no vote ended (Votes).</summary>
        private void OnExtendVoteEnded(VotesApi.VoteResult result)
        {
            _pluginState.ExtendTimeVoteHappening = false;
            _extendRoundTimeManager.KillTimer();

            // Votes cancels a running vote on map change; nothing to report or cool down then.
            if (result.Reason == VoteEndReason.Cancelled && result.CancelReason == "map change")
                return;

            ActivateCooldown();

            if (result.Passed)
            {
                Server.PrintToChatAll($"{_localizer.LocalizeWithPrefix("extendtime.vote-ended.passed2", _generalConfig.RoundTimeExtension)}");
                _extendRoundTimeManager.ExtendRoundTime(_generalConfig.RoundTimeExtension);
                _pluginState.MapExtensionCount++;
                return;
            }

            Server.PrintToChatAll(result.Reason == VoteEndReason.TimeUp
                ? $"{_localizer.LocalizeWithPrefix("extendtime.vote-ended-no-votes")}"
                : $"{_localizer.LocalizeWithPrefix("extendtime.vote-ended.failed2")}");
        }

        private void ActivateCooldown()
        {
            _isCooldownActive = true;

            _cooldownTimer = _plugin?.AddTimer(_voteExtendConfig.CooldownDuration, () =>
            {
                _isCooldownActive = false;
            }, TimerFlags.STOP_ON_MAPCHANGE
            );

            _cooldownEndTime = DateTime.UtcNow.AddSeconds(_voteExtendConfig.CooldownDuration);
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

            // On the Votes path a leaver is dropped by Votes itself.
            if (!_voteExtendConfig.EnablePanorama || VotesBridge.Api == null)
                _extendRoundTimeManager?.RemoveVote(player.Slot);
        }

        public void OnLoad(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void OnConfigParsed(Config config)
        {
            _voteExtendConfig = config.VoteExtend;
            _generalConfig = config.General;
        }
        
        public void OnMapStart(string map)
        {
            KillTimer();
        }

        public void Unload(Plugin plugin)
        {
            KillTimer();
        }
    }
}
